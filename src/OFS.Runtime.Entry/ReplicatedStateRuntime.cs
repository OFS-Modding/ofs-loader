using System.Security.Cryptography;
using System.Text;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal interface IReplicatedStateRuntime
{
    string Id { get; }
    bool IsRegistered { get; }
    void OnFrame(FrameEvent frame, bool serverActive, bool clientActive);
    void Unregister();
}

internal static class ReplicatedStateRuntime
{
    private const string InternalOwner = "ofs.framework.state";

    internal static ReplicatedState<T> Create<T>(
        string ownerId,
        IModLogger logger,
        ModReplicatedStateDefinition<T> definition,
        Func<bool> isServerActive,
        Func<bool> isClientActive,
        Action<IReplicatedStateRuntime> removed)
    {
        ValidateId(definition.Id);
        if (definition.MaxValueBytes is < 1 or > ReplicatedStateCodec.MaximumValueBytes)
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                $"Replicated state value limit must be 1-{ReplicatedStateCodec.MaximumValueBytes} bytes.");
        if (definition.Serializer is not null)
        {
            ArgumentNullException.ThrowIfNull(definition.Serializer.Serialize);
            ArgumentNullException.ThrowIfNull(definition.Serializer.Deserialize);
        }
        ReplicatedState<T>? state = null;
        var channelId = CreateChannelId(ownerId, definition.Id);
        var channel = NetworkMessageRuntime.Register(
            InternalOwner,
            logger,
            new ModNetworkChannelDefinition(
                channelId,
                message => state!.Receive(message),
                Direction: ModNetworkDirection.Bidirectional,
                MaxPayloadBytes: ReplicatedStateCodec.SnapshotHeaderBytes +
                    definition.MaxValueBytes,
                RequireAuthentication: true,
                DisableOnException: false));
        try
        {
            state = new ReplicatedState<T>(
                ownerId,
                logger,
                definition,
                channel,
                isServerActive,
                isClientActive,
                () => NetworkMessageRuntime.IsClientHandlerReady,
                removed);
            logger.Info(
                $"Registered replicated state '{state.QualifiedId}': " +
                $"maxValue={definition.MaxValueBytes}, serializer=" +
                $"{(definition.Serializer is null ? "json" : "custom")}.");
            return state;
        }
        catch
        {
            channel.Unregister();
            throw;
        }
    }

    internal static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 80 || id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException(
                "Replicated state id must contain 1-80 lowercase ASCII letters, digits, '.', '_' or '-'.",
                nameof(id));
    }

    private static string CreateChannelId(string ownerId, string stateId)
    {
        var key = Encoding.UTF8.GetBytes($"{ownerId}\0{stateId}");
        return "s." + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    }
}

internal sealed class ReplicatedState<T> : IModReplicatedState<T>, IReplicatedStateRuntime
{
    private const int SyncRetryFrames = 30;
    private readonly IModLogger _logger;
    private readonly ModReplicatedStateDefinition<T> _definition;
    private readonly ModNetworkSerializer<T> _serializer;
    private readonly IModNetworkChannel _channel;
    private readonly Func<bool> _isServerActive;
    private readonly Func<bool> _isClientActive;
    private readonly Func<bool> _isClientHandlerReady;
    private readonly Action<IReplicatedStateRuntime> _removed;
    private bool _enabled = true;
    private bool _lastServerActive;
    private bool _lastClientActive;
    private bool _syncRequestPending;
    private bool _awaitingSnapshot;
    private int _nextSyncAttemptFrame;

    internal ReplicatedState(
        string ownerId,
        IModLogger logger,
        ModReplicatedStateDefinition<T> definition,
        IModNetworkChannel channel,
        Func<bool> isServerActive,
        Func<bool> isClientActive,
        Func<bool> isClientHandlerReady,
        Action<IReplicatedStateRuntime> removed)
    {
        OwnerId = ownerId;
        Id = definition.Id;
        QualifiedId = $"{ownerId}:{definition.Id}";
        Value = definition.InitialValue;
        _logger = logger;
        _definition = definition;
        _serializer = definition.Serializer ?? ModNetworkSerializers.Json<T>();
        _channel = channel;
        _isServerActive = isServerActive;
        _isClientActive = isClientActive;
        _isClientHandlerReady = isClientHandlerReady;
        _removed = removed;
    }

    public string OwnerId { get; }
    public string Id { get; }
    public string QualifiedId { get; }
    public T Value { get; private set; }
    public ulong Revision { get; private set; }
    public bool IsSynchronized { get; private set; }
    public bool IsRegistered { get; private set; } = true;
    public bool Enabled
    {
        get => _enabled && _channel.Enabled;
        set
        {
            EnsureRegistered();
            _enabled = value;
            _channel.Enabled = value;
        }
    }

    public void SetServer(
        T value,
        ModNetworkTransport transport = ModNetworkTransport.Reliable)
    {
        EnsureUsable();
        EnsureMainThread();
        if (!_isServerActive())
            throw new InvalidOperationException(
                "Replicated state may only be changed by an active Mirror server.");
        if (Revision == ulong.MaxValue)
            throw new InvalidOperationException("Replicated state revision overflowed.");
        var serialized = Serialize(value);
        var previous = Value;
        Value = value;
        Revision = Revision == 0 ? 1 : Revision + 1;
        IsSynchronized = true;
        _channel.SendToAllClients(
            ReplicatedStateCodec.EncodeSnapshot(Revision, serialized),
            transport);
        InvokeUpdated(new ModReplicatedStateUpdate<T>(
            previous,
            value,
            Revision,
            ModReplicatedStateUpdateOrigin.ServerSet));
    }

    public void BroadcastCurrent(
        ModNetworkTransport transport = ModNetworkTransport.Reliable)
    {
        EnsureUsable();
        EnsureMainThread();
        if (!_isServerActive())
            throw new InvalidOperationException(
                "Replicated state may only be broadcast by an active Mirror server.");
        EnsureAuthoritativeRevision();
        SendSnapshotToAll(transport);
    }

    public void RequestSync()
    {
        EnsureUsable();
        EnsureMainThread();
        if (!_isClientActive())
            throw new InvalidOperationException("Mirror NetworkClient is not active.");
        if (!_isClientHandlerReady())
            throw new InvalidOperationException("OFS Mirror client handler is not ready.");
        SendSyncRequest();
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        _channel.Unregister();
        IsRegistered = false;
        _enabled = false;
        _syncRequestPending = false;
        _awaitingSnapshot = false;
        _removed(this);
    }

    public void Dispose() => Unregister();

    void IReplicatedStateRuntime.OnFrame(
        FrameEvent frame,
        bool serverActive,
        bool clientActive)
    {
        if (!IsRegistered || !Enabled) return;
        if (serverActive && !_lastServerActive)
        {
            Revision = 1;
            IsSynchronized = true;
            _logger.Info(
                $"Replicated state '{QualifiedId}' established server revision {Revision}.");
        }
        if (clientActive && !_lastClientActive)
        {
            if (!serverActive) IsSynchronized = false;
            _syncRequestPending = true;
            _nextSyncAttemptFrame = frame.FrameCount;
        }
        if (!clientActive)
        {
            _syncRequestPending = false;
            _awaitingSnapshot = false;
        }
        _lastServerActive = serverActive;
        _lastClientActive = clientActive;
        if (!clientActive || !_syncRequestPending ||
            !_isClientHandlerReady() || frame.FrameCount < _nextSyncAttemptFrame) return;
        try
        {
            SendSyncRequest();
        }
        catch (Exception exception)
        {
            _nextSyncAttemptFrame = unchecked(frame.FrameCount + SyncRetryFrames);
            _logger.Warning(
                $"Replicated state '{QualifiedId}' sync request will retry: {exception.Message}");
        }
    }

    internal void Receive(ModNetworkMessageEvent received)
    {
        if (!IsRegistered || !Enabled) return;
        try
        {
            if (!ReplicatedStateCodec.TryDecode(
                    received.Payload.Span, out var message, out var error))
                throw new InvalidDataException(error);
            if (received.ReceivedByServer)
            {
                if (message.Kind != ReplicatedStateMessageKind.SyncRequest || received.Sender is null)
                    throw new InvalidDataException("Server expected an authenticated sync request.");
                if (!_isServerActive()) return;
                EnsureAuthoritativeRevision();
                _channel.SendToClient(
                    received.Sender,
                    ReplicatedStateCodec.EncodeSnapshot(Revision, Serialize(Value)),
                    ModNetworkTransport.Reliable);
                _logger.Trace(
                    $"Replicated state '{QualifiedId}' answered sync for " +
                    $"connection {received.Sender.ConnectionId} at revision {Revision}.");
                return;
            }
            if (message.Kind != ReplicatedStateMessageKind.Snapshot)
                throw new InvalidDataException("Client expected an authoritative state snapshot.");
            if (message.Value.Length > _definition.MaxValueBytes)
                throw new InvalidDataException(
                    $"Snapshot exceeds state limit {_definition.MaxValueBytes} bytes.");
            if (!_awaitingSnapshot && message.Revision <= Revision) return;
            var value = _serializer.Deserialize(message.Value);
            var previous = Value;
            Value = value;
            Revision = message.Revision;
            IsSynchronized = true;
            _awaitingSnapshot = false;
            _logger.Info(
                $"Replicated state '{QualifiedId}' applied remote revision {Revision}.");
            InvokeUpdated(new ModReplicatedStateUpdate<T>(
                previous,
                value,
                Revision,
                ModReplicatedStateUpdateOrigin.RemoteSnapshot));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Replicated state '{QualifiedId}' receive failed.");
            if (_definition.DisableOnException) Enabled = false;
        }
    }

    private void SendSyncRequest()
    {
        _channel.SendToServer(
            ReplicatedStateCodec.EncodeSyncRequest(),
            ModNetworkTransport.Reliable);
        _syncRequestPending = false;
        _awaitingSnapshot = true;
        _logger.Trace($"Replicated state '{QualifiedId}' requested an authoritative snapshot.");
    }

    private void SendSnapshotToAll(ModNetworkTransport transport) =>
        _channel.SendToAllClients(
            ReplicatedStateCodec.EncodeSnapshot(Revision, Serialize(Value)),
            transport);

    private byte[] Serialize(T value)
    {
        var serialized = _serializer.Serialize(value)
            ?? throw new InvalidDataException("Replicated state serializer returned null.");
        if (serialized.Length > _definition.MaxValueBytes)
            throw new InvalidDataException(
                $"Serialized state is {serialized.Length} bytes; limit is {_definition.MaxValueBytes}.");
        return serialized;
    }

    private void EnsureAuthoritativeRevision()
    {
        if (Revision != 0) return;
        Revision = 1;
        IsSynchronized = true;
    }

    private void InvokeUpdated(ModReplicatedStateUpdate<T> update)
    {
        if (_definition.Updated is null) return;
        try
        {
            _definition.Updated(update);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Replicated state '{QualifiedId}' update callback failed.");
            if (_definition.DisableOnException) Enabled = false;
        }
    }

    private void EnsureUsable()
    {
        EnsureRegistered();
        if (!Enabled) throw new InvalidOperationException("Replicated state is disabled.");
    }

    private void EnsureRegistered()
    {
        if (!IsRegistered) throw new ObjectDisposedException(QualifiedId);
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException(
                "Replicated state operations must run on Unity's main thread.");
    }
}
