using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class NetworkMessageRuntime
{
    private const int HandlerRefreshIntervalFrames = 30;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NetworkChannel> Channels =
        new(StringComparer.OrdinalIgnoreCase);
    private static IUnsafeIl2CppApi? _api;
    private static bool _available;
    private static string? _unavailableReason;
    private static nint _networkServerClass;
    private static nint _networkClientClass;
    private static nint _networkMessagesClass;
    private static nint _networkReaderClass;
    private static nint _networkWriterClass;
    private static nint _networkWriterPoolClass;
    private static nint _networkConnectionClass;
    private static nint _networkConnectionToClientClass;
    private static nint _serverConnectionsField;
    private static nint _serverHandlersField;
    private static nint _clientHandlersField;
    private static nint _connectionAuthenticatedField;
    private static nint _clientConnectionIdField;
    private static nint _readerRemaining;
    private static nint _readerReadBytes;
    private static nint _writerPoolGet;
    private static nint _writerPoolReturn;
    private static nint _writerWriteBytes;
    private static nint _writerToArraySegment;
    private static nint _connectionSend;
    private static nint _clientGetConnection;
    private static nint _clientGetActive;
    private static nint _serverGetActive;
    private static nint _clientAddress;
    private static int _nextHandlerRefreshFrame;
    private static nint _serverHandlerDelegate;
    private static nint _clientHandlerDelegate;
    private static bool _serverHandlerReady;
    private static bool _clientHandlerReady;

    internal static bool IsAvailable => _available;
    internal static string? UnavailableReason => _unavailableReason;
    internal static bool IsServerHandlerReady => _serverHandlerReady;
    internal static bool IsClientHandlerReady => _clientHandlerReady;

    internal static long GetPeerToken(INetworkPeer peer) => peer is NetworkPeer runtimePeer
        ? runtimePeer.Connection.ToInt64()
        : RuntimeHelpers.GetHashCode(peer);

    internal static void Initialize(IUnsafeIl2CppApi api)
    {
        if (_api is not null) return;
        _api = api;
        try
        {
            Resolve(api);
            EnsureMessageIdAvailable(api);
            _available = true;
            EnsureHandlersRegistered();
            RuntimeLog.Write(
                $"OFS Mirror message envelope installed: id=0x{NetworkEnvelopeCodec.MessageId:X4}, " +
                $"maxPayload={NetworkEnvelopeCodec.MaxPayloadBytes}.");
        }
        catch (Exception exception)
        {
            _available = false;
            _unavailableReason = exception.Message;
            RuntimeLog.Write($"OFS Mirror message envelope unavailable: {exception}");
        }
    }

    internal static void Poll(FrameEvent frame)
    {
        if (!_available || frame.FrameCount < _nextHandlerRefreshFrame) return;
        _nextHandlerRefreshFrame = unchecked(frame.FrameCount + HandlerRefreshIntervalFrames);
        try
        {
            EnsureHandlersRegistered();
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"OFS Mirror handler registration refresh failed: {exception.Message}");
        }
    }

    internal static IModNetworkChannel Register(
        string ownerId,
        IModLogger logger,
        ModNetworkChannelDefinition definition)
    {
        if (!_available)
            throw new NotSupportedException(
                $"Mirror mod messaging is unavailable: {_unavailableReason ?? "initialization failed"}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateChannelId(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.Received);
        if (!Enum.IsDefined(definition.Direction))
            throw new ArgumentOutOfRangeException(nameof(definition), "Network direction is invalid.");
        if (definition.MaxPayloadBytes is < 1 or > NetworkEnvelopeCodec.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                $"Channel payload limit must be 1-{NetworkEnvelopeCodec.MaxPayloadBytes} bytes.");

        var channel = new NetworkChannel(ownerId, logger, definition);
        lock (Gate)
        {
            if (!Channels.TryAdd(channel.QualifiedId, channel))
                throw new InvalidOperationException(
                    $"Network channel '{channel.QualifiedId}' is already registered.");
        }
        logger.Info(
            $"Registered Mirror channel '{channel.QualifiedId}': direction={channel.Direction}, " +
            $"maxPayload={channel.MaxPayloadBytes}, auth={definition.RequireAuthentication}.");
        return channel;
    }

    private static void Resolve(IUnsafeIl2CppApi api)
    {
        _networkServerClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkServer");
        _networkClientClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkClient");
        _networkMessagesClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkMessages");
        _networkReaderClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkReader");
        _networkWriterClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkWriter");
        _networkWriterPoolClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkWriterPool");
        _networkConnectionClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkConnection");
        _networkConnectionToClientClass = RequireClass(
            api, "Mirror.dll", "Mirror", "NetworkConnectionToClient");
        _serverConnectionsField = RequireField(api, _networkServerClass, "connections");
        _serverHandlersField = RequireField(api, _networkServerClass, "handlers");
        _clientHandlersField = RequireField(api, _networkClientClass, "handlers");
        _connectionAuthenticatedField = RequireField(
            api, _networkConnectionClass, "isAuthenticated");
        _clientConnectionIdField = RequireField(
            api, _networkConnectionToClientClass, "connectionId");
        _readerRemaining = RequireMethod(api, _networkReaderClass, "get_Remaining", 0);
        _readerReadBytes = RequireMethod(api, _networkReaderClass, "ReadBytes", 2);
        _writerPoolGet = RequireMethod(api, _networkWriterPoolClass, "Get", 0);
        _writerPoolReturn = RequireMethod(api, _networkWriterPoolClass, "Return", 1);
        _writerWriteBytes = RequireMethod(api, _networkWriterClass, "WriteBytes", 3);
        _writerToArraySegment = RequireMethod(api, _networkWriterClass, "ToArraySegment", 0);
        _connectionSend = RequireMethod(
            api,
            _networkConnectionClass,
            "Send",
            ["System.ArraySegment`1", "System.Int32"]);
        _clientGetConnection = RequireMethod(api, _networkClientClass, "get_connection", 0);
        _clientGetActive = RequireMethod(api, _networkClientClass, "get_active", 0);
        _serverGetActive = RequireMethod(api, _networkServerClass, "get_active", 0);
        _clientAddress = RequireMethod(
            api, _networkConnectionToClientClass, "get_address", 0);
    }

    private static unsafe void EnsureHandlersRegistered()
    {
        var api = RequireApi();
        api.EnsureClassInitialized(_networkServerClass);
        api.EnsureClassInitialized(_networkClientClass);
        _serverHandlerReady = EnsureHandler(
            api,
            _serverHandlersField,
            ref _serverHandlerDelegate,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&OnServerMessage,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, nint, void>)&OnServerMessageInstance,
            "server");
        _clientHandlerReady = EnsureHandler(
            api,
            _clientHandlersField,
            ref _clientHandlerDelegate,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&OnClientMessage,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, nint, void>)&OnClientMessageInstance,
            "client");
    }

    private static unsafe bool EnsureHandler(
        IUnsafeIl2CppApi api,
        nint handlersField,
        ref nint handlerDelegate,
        nint staticCallback,
        nint instanceCallback,
        string side)
    {
        var handlers = api.ReadStaticObjectReference(handlersField);
        if (handlers == 0) return false;
        var handlersClass = api.GetObjectClass(handlers);
        var containsKey = RequireMethod(api, handlersClass, "ContainsKey", 1);
        var id = NetworkEnvelopeCodec.MessageId;
        nint* containsArguments = stackalloc nint[1];
        containsArguments[0] = (nint)(&id);
        if (ReadBoxedBoolean(api.RuntimeInvoke(
            containsKey, handlers, (nint)containsArguments))) return true;

        if (handlerDelegate == 0)
        {
            var source = GetFirstDictionaryValue(api, handlers);
            if (source == 0) return false;
            handlerDelegate = api.ShallowCloneObject(source);
            var target = Marshal.ReadIntPtr(source, 4 * nint.Size);
            var callback = target == 0 ? staticCallback : instanceCallback;
            // Il2CppDelegate starts after Il2CppObject with method_ptr and invoke_impl.
            Marshal.WriteIntPtr(handlerDelegate, 2 * nint.Size, callback);
            Marshal.WriteIntPtr(handlerDelegate, 3 * nint.Size, callback);
            RuntimeLog.Write(
                $"OFS Mirror {side} delegate cloned: source=0x{source:X}, " +
                $"target=0x{target:X}, callback=0x{callback:X}.");
        }

        var setItem = RequireMethod(api, handlersClass, "set_Item", 2);
        nint* setArguments = stackalloc nint[2];
        setArguments[0] = (nint)(&id);
        setArguments[1] = handlerDelegate;
        _ = api.RuntimeInvoke(setItem, handlers, (nint)setArguments);
        RuntimeLog.Write(
            $"OFS Mirror {side} handler registered for id=0x{id:X4}.");
        return true;
    }

    private static nint GetFirstDictionaryValue(IUnsafeIl2CppApi api, nint dictionary)
    {
        var values = api.RuntimeInvoke(
            RequireMethod(api, api.GetObjectClass(dictionary), "get_Values", 0), dictionary, 0);
        if (values == 0) return 0;
        var boxedEnumerator = api.RuntimeInvoke(
            RequireMethod(api, api.GetObjectClass(values), "GetEnumerator", 0), values, 0);
        if (boxedEnumerator == 0) return 0;
        var enumeratorClass = api.GetObjectClass(boxedEnumerator);
        var enumerator = api.Unbox(boxedEnumerator);
        var moveNext = RequireMethod(api, enumeratorClass, "MoveNext", 0);
        var getCurrent = RequireMethod(api, enumeratorClass, "get_Current", 0);
        var dispose = api.FindMethod(enumeratorClass, "Dispose", 0);
        try
        {
            return ReadBoxedBoolean(api.RuntimeInvoke(moveNext, enumerator, 0))
                ? api.RuntimeInvoke(getCurrent, enumerator, 0)
                : 0;
        }
        finally
        {
            if (dispose != 0) _ = api.RuntimeInvoke(dispose, enumerator, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerMessage(
        nint connection,
        nint reader,
        int transportChannel,
        nint methodInfo) =>
        HandleRegisteredMessage(reader, connection, transportChannel, receivedByServer: true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerMessageInstance(
        nint target,
        nint connection,
        nint reader,
        int transportChannel,
        nint methodInfo) =>
        HandleRegisteredMessage(reader, connection, transportChannel, receivedByServer: true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientMessage(
        nint connection,
        nint reader,
        int transportChannel,
        nint methodInfo) =>
        HandleRegisteredMessage(reader, 0, transportChannel, receivedByServer: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientMessageInstance(
        nint target,
        nint connection,
        nint reader,
        int transportChannel,
        nint methodInfo) =>
        HandleRegisteredMessage(reader, 0, transportChannel, receivedByServer: false);

    private static void HandleRegisteredMessage(
        nint reader,
        nint connection,
        int transportChannel,
        bool receivedByServer)
    {
        try
        {
            var api = RequireApi();
            var remaining = ReadBoxedInt32(api.RuntimeInvoke(_readerRemaining, reader, 0));
            var body = ReadBytes(reader, remaining);
            var frame = new byte[body.Length + sizeof(ushort)];
            frame[0] = (byte)(NetworkEnvelopeCodec.MessageId & 0xFF);
            frame[1] = (byte)(NetworkEnvelopeCodec.MessageId >> 8);
            body.CopyTo(frame, sizeof(ushort));
            if (!NetworkEnvelopeCodec.TryDecode(
                frame, out var qualifiedChannelId, out var payload, out var error))
            {
                RuntimeLog.Write($"Rejected malformed OFS Mirror envelope: {error}");
                return;
            }
            Dispatch(
                qualifiedChannelId,
                payload,
                connection,
                transportChannel,
                receivedByServer);
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"OFS Mirror registered handler failed: {exception}");
        }
    }

    private static unsafe void EnsureMessageIdAvailable(IUnsafeIl2CppApi api)
    {
        api.EnsureClassInitialized(_networkMessagesClass);
        var lookupField = RequireField(api, _networkMessagesClass, "Lookup");
        var lookup = api.ReadStaticObjectReference(lookupField);
        if (lookup == 0) return;
        var containsKey = RequireMethod(api, api.GetObjectClass(lookup), "ContainsKey", 1);
        var id = NetworkEnvelopeCodec.MessageId;
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&id);
        if (ReadBoxedBoolean(api.RuntimeInvoke(containsKey, lookup, (nint)arguments)))
            throw new InvalidOperationException(
                $"Mirror message id 0x{id:X4} collides with an AOT NetworkMessage.");
    }

    private static void Dispatch(
        string qualifiedChannelId,
        byte[] payload,
        nint connection,
        int transportChannel,
        bool receivedByServer)
    {
        NetworkChannel? channel;
        lock (Gate) Channels.TryGetValue(qualifiedChannelId, out channel);
        if (channel is null || !channel.IsRegistered || !channel.Enabled)
        {
            RuntimeLog.Write($"Ignored unregistered OFS network channel '{qualifiedChannelId}'.");
            return;
        }
        if (payload.Length > channel.MaxPayloadBytes)
        {
            channel.Logger.Warning(
                $"Dropped {payload.Length}-byte message exceeding channel limit " +
                $"{channel.MaxPayloadBytes}.");
            return;
        }
        var permitted = receivedByServer
            ? channel.Direction is ModNetworkDirection.ClientToServer or ModNetworkDirection.Bidirectional
            : channel.Direction is ModNetworkDirection.ServerToClient or ModNetworkDirection.Bidirectional;
        if (!permitted)
        {
            channel.Logger.Warning(
                $"Dropped message arriving against channel direction {channel.Direction}.");
            return;
        }

        NetworkPeer? peer = null;
        if (receivedByServer)
        {
            peer = CreatePeer(connection);
            if (channel.Definition.RequireAuthentication && !peer.IsAuthenticated)
            {
                channel.Logger.Warning(
                    $"Dropped unauthenticated message from connection {peer.ConnectionId}.");
                return;
            }
        }
        try
        {
            using var attribution = ModSafetyStore.EnterHotRuntimeCallback(
                channel.OwnerId,
                $"network:{channel.Id}:received");
            channel.Definition.Received(new ModNetworkMessageEvent(
                channel.Id,
                channel.QualifiedId,
                payload,
                ToTransport(transportChannel),
                receivedByServer,
                peer));
        }
        catch (Exception exception)
        {
            channel.Logger.Error(exception, $"Network channel '{channel.QualifiedId}' handler failed.");
            if (channel.Definition.DisableOnException) channel.Enabled = false;
        }
    }

    private static NetworkPeer CreatePeer(nint connection)
    {
        var api = RequireApi();
        var connectionId = api.ReadInt32(connection, _clientConnectionIdField);
        var addressValue = api.RuntimeInvoke(_clientAddress, connection, 0);
        var address = addressValue == 0 ? string.Empty : api.ReadString(addressValue);
        return new NetworkPeer(
            connection,
            connectionId,
            address,
            api.ReadBoolean(connection, _connectionAuthenticatedField));
    }

    private static unsafe byte[] ReadBytes(nint reader, int count)
    {
        var api = RequireApi();
        var array = api.NewByteArray(new byte[count]);
        nint* arguments = stackalloc nint[2];
        arguments[0] = array;
        arguments[1] = (nint)(&count);
        _ = api.RuntimeInvoke(_readerReadBytes, reader, (nint)arguments);
        return api.ReadByteArray(array);
    }

    private static unsafe void SendFrame(nint connection, byte[] frame, ModNetworkTransport transport)
    {
        if (connection == 0) throw new InvalidOperationException("Mirror connection is null.");
        var api = RequireApi();
        var writer = api.RuntimeInvoke(_writerPoolGet, 0, 0);
        if (writer == 0) throw new InvalidOperationException("NetworkWriterPool.Get returned null.");
        try
        {
            var array = api.NewByteArray(frame);
            var offset = 0;
            var count = frame.Length;
            nint* writeArguments = stackalloc nint[3];
            writeArguments[0] = array;
            writeArguments[1] = (nint)(&offset);
            writeArguments[2] = (nint)(&count);
            _ = api.RuntimeInvoke(_writerWriteBytes, writer, (nint)writeArguments);
            var boxedSegment = api.RuntimeInvoke(_writerToArraySegment, writer, 0);
            var segment = api.Unbox(boxedSegment);
            if (segment == 0) throw new InvalidOperationException("Writer returned an empty segment value.");
            var channelId = (int)transport;
            nint* sendArguments = stackalloc nint[2];
            sendArguments[0] = segment;
            sendArguments[1] = (nint)(&channelId);
            var concreteSend = api.FindMethodBySignature(
                api.GetObjectClass(connection),
                "Send",
                ["System.ArraySegment`1", "System.Int32"]);
            if (concreteSend == 0)
                concreteSend = api.ResolveVirtualMethod(connection, _connectionSend);
            _ = api.RuntimeInvoke(concreteSend, connection, (nint)sendArguments);
        }
        finally
        {
            ReturnWriter(api, writer);
        }
    }

    private static unsafe void ReturnWriter(IUnsafeIl2CppApi api, nint writer)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = writer;
        _ = api.RuntimeInvoke(_writerPoolReturn, 0, (nint)arguments);
    }

    private static void ValidateSend(NetworkChannel channel, ReadOnlyMemory<byte> payload)
    {
        if (!channel.IsRegistered) throw new ObjectDisposedException(channel.QualifiedId);
        if (!channel.Enabled) throw new InvalidOperationException("Network channel is disabled.");
        if (payload.Length > channel.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"Payload exceeds channel limit {channel.MaxPayloadBytes}.");
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Network messages must be sent from Unity's main thread.");
    }

    private static void SendToServer(
        NetworkChannel channel,
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport)
    {
        ValidateSend(channel, payload);
        if (channel.Direction == ModNetworkDirection.ServerToClient)
            throw new InvalidOperationException("This channel does not allow client-to-server messages.");
        ValidateTransport(transport);
        if (!ReadStaticBoolean(_clientGetActive))
            throw new InvalidOperationException("Mirror NetworkClient is not active.");
        var connection = RequireApi().RuntimeInvoke(_clientGetConnection, 0, 0);
        SendFrame(connection, NetworkEnvelopeCodec.Encode(channel.QualifiedId, payload.Span), transport);
    }

    private static void SendToClient(
        NetworkChannel channel,
        INetworkPeer peer,
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport)
    {
        ValidateSend(channel, payload);
        if (channel.Direction == ModNetworkDirection.ClientToServer)
            throw new InvalidOperationException("This channel does not allow server-to-client messages.");
        ValidateTransport(transport);
        if (!ReadStaticBoolean(_serverGetActive))
            throw new InvalidOperationException("Mirror NetworkServer is not active.");
        if (peer is not NetworkPeer runtimePeer || !TryResolvePeer(runtimePeer, out var connection))
            throw new ArgumentException("Peer does not belong to the active Mirror server.", nameof(peer));
        SendFrame(connection, NetworkEnvelopeCodec.Encode(channel.QualifiedId, payload.Span), transport);
    }

    private static void SendToAllClients(
        NetworkChannel channel,
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport,
        bool authenticatedOnly)
    {
        ValidateSend(channel, payload);
        if (channel.Direction == ModNetworkDirection.ClientToServer)
            throw new InvalidOperationException("This channel does not allow server-to-client messages.");
        ValidateTransport(transport);
        if (!ReadStaticBoolean(_serverGetActive))
            throw new InvalidOperationException("Mirror NetworkServer is not active.");
        var frame = NetworkEnvelopeCodec.Encode(channel.QualifiedId, payload.Span);
        foreach (var connection in EnumerateServerConnections())
        {
            if (!authenticatedOnly || RequireApi().ReadBoolean(
                    connection, _connectionAuthenticatedField))
                SendFrame(connection, frame, transport);
        }
    }

    private static unsafe bool TryResolvePeer(NetworkPeer peer, out nint connection)
    {
        connection = 0;
        var api = RequireApi();
        var dictionary = api.ReadStaticObjectReference(_serverConnectionsField);
        if (dictionary == 0) return false;
        var tryGetValue = RequireMethod(api, api.GetObjectClass(dictionary), "TryGetValue", 2);
        var id = peer.ConnectionId;
        nint found = 0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = (nint)(&id);
        arguments[1] = (nint)(&found);
        if (!ReadBoxedBoolean(api.RuntimeInvoke(tryGetValue, dictionary, (nint)arguments)) ||
            found == 0 || found != peer.Connection)
            return false;
        connection = found;
        return true;
    }

    private static IEnumerable<nint> EnumerateServerConnections()
    {
        var api = RequireApi();
        var dictionary = api.ReadStaticObjectReference(_serverConnectionsField);
        if (dictionary == 0) yield break;
        var values = api.RuntimeInvoke(
            RequireMethod(api, api.GetObjectClass(dictionary), "get_Values", 0), dictionary, 0);
        if (values == 0) yield break;
        var boxedEnumerator = api.RuntimeInvoke(
            RequireMethod(api, api.GetObjectClass(values), "GetEnumerator", 0), values, 0);
        if (boxedEnumerator == 0) yield break;
        var enumeratorClass = api.GetObjectClass(boxedEnumerator);
        var enumerator = api.Unbox(boxedEnumerator);
        var moveNext = RequireMethod(api, enumeratorClass, "MoveNext", 0);
        var getCurrent = RequireMethod(api, enumeratorClass, "get_Current", 0);
        var dispose = api.FindMethod(enumeratorClass, "Dispose", 0);
        try
        {
            while (ReadBoxedBoolean(api.RuntimeInvoke(moveNext, enumerator, 0)))
            {
                var current = api.RuntimeInvoke(getCurrent, enumerator, 0);
                if (current != 0) yield return current;
            }
        }
        finally
        {
            if (dispose != 0) _ = api.RuntimeInvoke(dispose, enumerator, 0);
        }
    }

    private static bool ReadStaticBoolean(nint getter) =>
        ReadBoxedBoolean(RequireApi().RuntimeInvoke(getter, 0, 0));

    private static bool ReadBoxedBoolean(nint boxed)
    {
        var value = RequireApi().Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private static int ReadBoxedInt32(nint boxed)
    {
        var value = RequireApi().Unbox(boxed);
        return value == 0 ? 0 : Marshal.ReadInt32(value);
    }

    private static ModNetworkTransport ToTransport(int channelId) => channelId switch
    {
        0 => ModNetworkTransport.Reliable,
        1 => ModNetworkTransport.Unreliable,
        _ => ModNetworkTransport.Reliable,
    };

    private static void ValidateTransport(ModNetworkTransport transport)
    {
        if (!Enum.IsDefined(transport))
            throw new ArgumentOutOfRangeException(nameof(transport));
    }

    private static void ValidateChannelId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 80 || id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException(
                "Network channel id must contain 1-80 lowercase ASCII letters, digits, '.', '_' or '-'.",
                nameof(id));
    }

    private static void Unregister(NetworkChannel channel)
    {
        lock (Gate)
        {
            if (!channel.IsRegistered) return;
            if (!Channels.Remove(channel.QualifiedId, out var owned) || !ReferenceEquals(owned, channel))
                throw new InvalidOperationException("Network channel ownership changed.");
            channel.MarkUnregistered();
        }
    }

    private static IUnsafeIl2CppApi RequireApi() => _api
        ?? throw new InvalidOperationException("Mirror message runtime is not initialized.");

    private static nint RequireClass(
        IUnsafeIl2CppApi api, string assembly, string namespaze, string name)
    {
        var klass = api.FindClass(assembly, namespaze, name);
        return klass != 0 ? klass : throw new TypeLoadException(
            $"IL2CPP class '{namespaze}.{name}' was not found in '{assembly}'.");
    }

    private static nint RequireField(IUnsafeIl2CppApi api, nint klass, string name)
    {
        var field = api.FindField(klass, name);
        return field != 0 ? field : throw new MissingFieldException(name);
    }

    private static nint RequireMethod(IUnsafeIl2CppApi api, nint klass, string name, int arguments)
    {
        var method = api.FindMethod(klass, name, arguments);
        return method != 0 ? method : throw new MissingMethodException($"{name}/{arguments}");
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi api,
        nint klass,
        string name,
        IReadOnlyList<string> parameterTypeNames)
    {
        var method = api.FindMethodBySignature(klass, name, parameterTypeNames);
        return method != 0 ? method : throw new MissingMethodException(
            $"{name}({string.Join(", ", parameterTypeNames)})");
    }

    private sealed class NetworkPeer(
        nint connection,
        int connectionId,
        string address,
        bool isAuthenticated) : INetworkPeer
    {
        internal nint Connection { get; } = connection;
        public int ConnectionId { get; } = connectionId;
        public string Address { get; } = address;
        public bool IsAuthenticated { get; } = isAuthenticated;
    }

    private sealed class NetworkChannel(
        string ownerId,
        IModLogger logger,
        ModNetworkChannelDefinition definition) : IModNetworkChannel
    {
        public string OwnerId { get; } = ownerId;
        public string Id { get; } = definition.Id;
        public string QualifiedId { get; } = $"{ownerId}:{definition.Id}";
        public ModNetworkDirection Direction { get; } = definition.Direction;
        public int MaxPayloadBytes { get; } = definition.MaxPayloadBytes;
        public bool IsRegistered { get; private set; } = true;
        public bool Enabled { get; set; } = true;
        internal IModLogger Logger { get; } = logger;
        internal ModNetworkChannelDefinition Definition { get; } = definition;

        public void SendToServer(
            ReadOnlyMemory<byte> payload,
            ModNetworkTransport transport = ModNetworkTransport.Reliable) =>
            NetworkMessageRuntime.SendToServer(this, payload, transport);

        public void SendToClient(
            INetworkPeer peer,
            ReadOnlyMemory<byte> payload,
            ModNetworkTransport transport = ModNetworkTransport.Reliable) =>
            NetworkMessageRuntime.SendToClient(this, peer, payload, transport);

        public void SendToAllClients(
            ReadOnlyMemory<byte> payload,
            ModNetworkTransport transport = ModNetworkTransport.Reliable,
            bool authenticatedOnly = true) =>
            NetworkMessageRuntime.SendToAllClients(this, payload, transport, authenticatedOnly);

        public void Unregister() => NetworkMessageRuntime.Unregister(this);
        public void Dispose() => Unregister();
        internal void MarkUnregistered()
        {
            IsRegistered = false;
            Enabled = false;
        }
    }
}
