using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal interface INetworkRpcRuntime
{
    string Id { get; }
    bool IsRegistered { get; }
    void OnFrame(FrameEvent frame);
    void Unregister();
}

internal static class NetworkRpcRuntime
{
    private const string InternalOwner = "ofs.framework.rpc";

    internal static NetworkRpc<TRequest, TResponse> Create<TRequest, TResponse>(
        string ownerId,
        IModLogger logger,
        ModNetworkRpcDefinition<TRequest, TResponse> definition,
        Func<bool> isClientActive,
        Action<INetworkRpcRuntime> removed)
    {
        ValidateDefinition(definition);
        NetworkRpc<TRequest, TResponse>? rpc = null;
        var channel = NetworkMessageRuntime.Register(
            InternalOwner,
            logger,
            new ModNetworkChannelDefinition(
                CreateChannelId(ownerId, definition.Id),
                message => rpc!.Receive(message),
                Direction: ModNetworkDirection.Bidirectional,
                MaxPayloadBytes: NetworkRpcCodec.HeaderBytes +
                    Math.Max(definition.MaxRequestBytes, definition.MaxResponseBytes),
                RequireAuthentication: true,
                DisableOnException: false));
        try
        {
            rpc = new NetworkRpc<TRequest, TResponse>(
                ownerId,
                logger,
                definition,
                channel,
                isClientActive,
                () => NetworkMessageRuntime.IsClientHandlerReady,
                () => ModRuntime.IsMainThread,
                removed);
            logger.Info(
                $"Registered network RPC '{rpc.QualifiedId}': " +
                $"request={definition.MaxRequestBytes}, response={definition.MaxResponseBytes}, " +
                $"rate={FormatRateLimit(rpc.RateLimit)}, auth={definition.Authorize is not null}.");
            return rpc;
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
                "Network RPC id must contain 1-80 lowercase ASCII letters, digits, '.', '_' or '-'.",
                nameof(id));
    }

    private static void ValidateDefinition<TRequest, TResponse>(
        ModNetworkRpcDefinition<TRequest, TResponse> definition)
    {
        ValidateId(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.Handler);
        ValidateSerializer(definition.RequestSerializer);
        ValidateSerializer(definition.ResponseSerializer);
        ValidateLimit(definition.MaxRequestBytes, nameof(definition.MaxRequestBytes));
        ValidateLimit(definition.MaxResponseBytes, nameof(definition.MaxResponseBytes));
        _ = ResolveTimeout(definition.DefaultTimeout);
        _ = ResolveRateLimit(definition.RateLimit);
    }

    private static void ValidateSerializer<T>(ModNetworkSerializer<T>? serializer)
    {
        if (serializer is null) return;
        ArgumentNullException.ThrowIfNull(serializer.Serialize);
        ArgumentNullException.ThrowIfNull(serializer.Deserialize);
    }

    private static void ValidateLimit(int value, string parameterName)
    {
        if (value is < 1 or > NetworkRpcCodec.MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"RPC body limit must be 1-{NetworkRpcCodec.MaximumBodyBytes} bytes.");
    }

    internal static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        var resolved = timeout ?? TimeSpan.FromSeconds(10);
        if (resolved < TimeSpan.FromMilliseconds(100) || resolved > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(
                nameof(timeout), "RPC timeout must be between 100 ms and 5 minutes.");
        return resolved;
    }

    internal static ModNetworkRpcRateLimit ResolveRateLimit(ModNetworkRpcRateLimit? rateLimit)
    {
        var resolved = rateLimit ?? ModNetworkRpcRateLimit.Default;
        if (!resolved.Enabled) return resolved;
        if (resolved.Burst is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(
                nameof(rateLimit), "RPC rate-limit burst must be between 1 and 10000.");
        if (!double.IsFinite(resolved.RefillPerSecond) ||
            resolved.RefillPerSecond is < 0.01 or > 10_000)
            throw new ArgumentOutOfRangeException(
                nameof(rateLimit),
                "RPC rate-limit refill must be finite and between 0.01 and 10000 per second.");
        return resolved;
    }

    private static string FormatRateLimit(ModNetworkRpcRateLimit limit) => limit.Enabled
        ? $"{limit.Burst}/{limit.RefillPerSecond:0.##}s"
        : "unlimited";

    private static string CreateChannelId(string ownerId, string rpcId)
    {
        var key = Encoding.UTF8.GetBytes($"{ownerId}\0{rpcId}");
        return "r." + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    }
}

internal sealed class NetworkRpc<TRequest, TResponse> :
    IModNetworkRpc<TRequest, TResponse>, INetworkRpcRuntime
{
    private readonly IModLogger _logger;
    private readonly ModNetworkRpcDefinition<TRequest, TResponse> _definition;
    private readonly ModNetworkSerializer<TRequest> _requestSerializer;
    private readonly ModNetworkSerializer<TResponse> _responseSerializer;
    private readonly IModNetworkChannel _channel;
    private readonly Func<bool> _isClientActive;
    private readonly Func<bool> _isClientHandlerReady;
    private readonly Func<bool> _isMainThread;
    private readonly Action<INetworkRpcRuntime> _removed;
    private readonly Dictionary<uint, PendingInvocation> _pending = [];
    private readonly NetworkRpcRateLimiter _rateLimiter;
    private uint _nextRequestId;
    private bool _enabled = true;

    internal NetworkRpc(
        string ownerId,
        IModLogger logger,
        ModNetworkRpcDefinition<TRequest, TResponse> definition,
        IModNetworkChannel channel,
        Func<bool> isClientActive,
        Func<bool> isClientHandlerReady,
        Func<bool> isMainThread,
        Action<INetworkRpcRuntime> removed)
    {
        OwnerId = ownerId;
        Id = definition.Id;
        QualifiedId = $"{ownerId}:{definition.Id}";
        _logger = logger;
        _definition = definition;
        _requestSerializer = definition.RequestSerializer ?? ModNetworkSerializers.Json<TRequest>();
        _responseSerializer = definition.ResponseSerializer ?? ModNetworkSerializers.Json<TResponse>();
        _channel = channel;
        RateLimit = NetworkRpcRuntime.ResolveRateLimit(definition.RateLimit);
        _rateLimiter = new NetworkRpcRateLimiter(RateLimit, Stopwatch.Frequency);
        _isClientActive = isClientActive;
        _isClientHandlerReady = isClientHandlerReady;
        _isMainThread = isMainThread;
        _removed = removed;
    }

    public string OwnerId { get; }
    public string Id { get; }
    public string QualifiedId { get; }
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
    public int PendingCount => _pending.Count;
    public ModNetworkRpcRateLimit RateLimit { get; }

    public IModNetworkRpcCall InvokeServer(
        TRequest request,
        Action<ModNetworkRpcResult<TResponse>> completed,
        TimeSpan? timeout = null,
        ModNetworkTransport transport = ModNetworkTransport.Reliable)
    {
        EnsureUsable();
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(completed);
        if (!_isClientActive())
            throw new InvalidOperationException("Mirror NetworkClient is not active.");
        if (!_isClientHandlerReady())
            throw new InvalidOperationException("OFS Mirror client handler is not ready.");
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        var body = SerializeRequest(request);
        var requestId = AllocateRequestId();
        var deadline = CreateDeadline(timeout ?? _definition.DefaultTimeout);
        var call = new RpcCall(this, requestId);
        _pending.Add(requestId, new PendingInvocation(completed, deadline));
        try
        {
            _channel.SendToServer(
                NetworkRpcCodec.Encode(NetworkRpcMessageKind.Request, requestId, body),
                transport);
            return call;
        }
        catch
        {
            _pending.Remove(requestId);
            throw;
        }
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        _pending.Clear();
        _rateLimiter.Clear();
        _channel.Unregister();
        IsRegistered = false;
        _enabled = false;
        _removed(this);
    }

    public void Dispose() => Unregister();

    void INetworkRpcRuntime.OnFrame(FrameEvent _)
    {
        var timestamp = Stopwatch.GetTimestamp();
        ExpirePending(timestamp);
        _rateLimiter.RemoveIdle(timestamp);
    }

    internal void Receive(ModNetworkMessageEvent received)
    {
        if (!IsRegistered || !Enabled) return;
        if (!NetworkRpcCodec.TryDecode(
                received.Payload.Span, out var message, out var decodeError))
        {
            _logger.Warning($"RPC '{QualifiedId}' rejected malformed payload: {decodeError}");
            return;
        }
        if (received.ReceivedByServer)
        {
            HandleServer(received, message);
            return;
        }
        HandleClient(message);
    }

    private void HandleServer(ModNetworkMessageEvent received, NetworkRpcMessage message)
    {
        if (message.Kind != NetworkRpcMessageKind.Request || received.Sender is null)
        {
            _logger.Warning($"RPC '{QualifiedId}' server received a non-request message.");
            return;
        }
        try
        {
            if (message.Body.Length > _definition.MaxRequestBytes)
                throw new InvalidDataException(
                    $"Request is {message.Body.Length} bytes; limit is {_definition.MaxRequestBytes}.");
            if (!_rateLimiter.TryConsume(
                    NetworkMessageRuntime.GetPeerToken(received.Sender),
                    Stopwatch.GetTimestamp()))
            {
                RejectServerRequest(
                    received.Sender,
                    message.RequestId,
                    "RPC rate limit exceeded.");
                return;
            }
            var request = _requestSerializer.Deserialize(message.Body);
            var requestContext = new ModNetworkRpcRequest<TRequest>(
                request,
                received.Sender,
                received.Transport);
            if (_definition.Authorize is not null)
            {
                var authorization = _definition.Authorize(requestContext);
                if (!authorization.IsAuthorized)
                {
                    RejectServerRequest(
                        received.Sender,
                        message.RequestId,
                        string.IsNullOrWhiteSpace(authorization.Error)
                            ? "RPC request was not authorized."
                            : authorization.Error);
                    return;
                }
            }
            var response = _definition.Handler(requestContext);
            var body = SerializeResponse(response);
            _channel.SendToClient(
                received.Sender,
                NetworkRpcCodec.Encode(NetworkRpcMessageKind.Success, message.RequestId, body),
                ModNetworkTransport.Reliable);
        }
        catch (Exception exception)
        {
            _logger.Warning(
                $"RPC '{QualifiedId}' request {message.RequestId} failed: {exception.Message}");
            try
            {
                _channel.SendToClient(
                    received.Sender,
                    NetworkRpcCodec.EncodeError(
                        message.RequestId,
                        exception.Message,
                        _definition.MaxResponseBytes),
                    ModNetworkTransport.Reliable);
            }
            catch (Exception sendException)
            {
                _logger.Error(sendException, $"RPC '{QualifiedId}' could not return its error response.");
            }
        }
    }

    private void RejectServerRequest(INetworkPeer sender, uint requestId, string error)
    {
        _logger.Warning($"RPC '{QualifiedId}' request {requestId} rejected: {error}");
        try
        {
            _channel.SendToClient(
                sender,
                NetworkRpcCodec.EncodeError(requestId, error, _definition.MaxResponseBytes),
                ModNetworkTransport.Reliable);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"RPC '{QualifiedId}' could not return its rejection response.");
        }
    }

    private void HandleClient(NetworkRpcMessage message)
    {
        if (!_pending.Remove(message.RequestId, out var pending))
        {
            _logger.Trace(
                $"RPC '{QualifiedId}' ignored late/unknown response {message.RequestId}.");
            return;
        }
        ModNetworkRpcResult<TResponse> result;
        try
        {
            result = message.Kind switch
            {
                NetworkRpcMessageKind.Success => DecodeSuccess(message.Body),
                NetworkRpcMessageKind.Error => DecodeRemoteError(message.Body),
                _ => new ModNetworkRpcResult<TResponse>(
                    ModNetworkRpcStatus.ProtocolError,
                    default,
                    "Client received an RPC request instead of a response."),
            };
        }
        catch (Exception exception)
        {
            result = new ModNetworkRpcResult<TResponse>(
                ModNetworkRpcStatus.ProtocolError,
                default,
                exception.Message);
        }
        Complete(pending, result);
    }

    private ModNetworkRpcResult<TResponse> DecodeSuccess(byte[] body)
    {
        if (body.Length > _definition.MaxResponseBytes)
            throw new InvalidDataException(
                $"Response is {body.Length} bytes; limit is {_definition.MaxResponseBytes}.");
        return new ModNetworkRpcResult<TResponse>(
            ModNetworkRpcStatus.Succeeded,
            _responseSerializer.Deserialize(body),
            null);
    }

    private ModNetworkRpcResult<TResponse> DecodeRemoteError(byte[] body)
    {
        if (body.Length > _definition.MaxResponseBytes)
            throw new InvalidDataException(
                $"Error response is {body.Length} bytes; limit is {_definition.MaxResponseBytes}.");
        return new ModNetworkRpcResult<TResponse>(
            ModNetworkRpcStatus.RemoteError,
            default,
            NetworkRpcCodec.DecodeError(body));
    }

    private void ExpirePending(long timestamp)
    {
        if (_pending.Count == 0) return;
        foreach (var pair in _pending.Where(value => value.Value.Deadline <= timestamp).ToArray())
        {
            if (!_pending.Remove(pair.Key, out var pending)) continue;
            Complete(pending, new ModNetworkRpcResult<TResponse>(
                ModNetworkRpcStatus.TimedOut,
                default,
                "RPC response timed out."));
        }
    }

    private void Cancel(uint requestId)
    {
        EnsureMainThread();
        if (!_pending.Remove(requestId, out var pending)) return;
        Complete(pending, new ModNetworkRpcResult<TResponse>(
            ModNetworkRpcStatus.Cancelled,
            default,
            "RPC call was cancelled."));
    }

    private bool IsPending(uint requestId) => _pending.ContainsKey(requestId);

    private void Complete(PendingInvocation pending, ModNetworkRpcResult<TResponse> result)
    {
        try
        {
            pending.Completed(result);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"RPC '{QualifiedId}' completion callback failed.");
        }
    }

    private byte[] SerializeRequest(TRequest value)
    {
        var body = _requestSerializer.Serialize(value)
            ?? throw new InvalidDataException("RPC request serializer returned null.");
        if (body.Length > _definition.MaxRequestBytes)
            throw new InvalidDataException(
                $"Serialized request is {body.Length} bytes; limit is {_definition.MaxRequestBytes}.");
        return body;
    }

    private byte[] SerializeResponse(TResponse value)
    {
        var body = _responseSerializer.Serialize(value)
            ?? throw new InvalidDataException("RPC response serializer returned null.");
        if (body.Length > _definition.MaxResponseBytes)
            throw new InvalidDataException(
                $"Serialized response is {body.Length} bytes; limit is {_definition.MaxResponseBytes}.");
        return body;
    }

    private uint AllocateRequestId()
    {
        for (var attempts = 0; attempts < int.MaxValue; attempts++)
        {
            _nextRequestId = unchecked(_nextRequestId + 1);
            if (_nextRequestId != 0 && !_pending.ContainsKey(_nextRequestId)) return _nextRequestId;
        }
        throw new InvalidOperationException("RPC request id space is exhausted.");
    }

    private static long CreateDeadline(TimeSpan? timeout)
    {
        var resolved = NetworkRpcRuntime.ResolveTimeout(timeout);
        var ticks = checked((long)Math.Ceiling(
            resolved.TotalSeconds * Stopwatch.Frequency));
        return checked(Stopwatch.GetTimestamp() + ticks);
    }

    private void EnsureUsable()
    {
        EnsureRegistered();
        if (!Enabled) throw new InvalidOperationException("Network RPC is disabled.");
    }

    private void EnsureRegistered()
    {
        if (!IsRegistered) throw new ObjectDisposedException(QualifiedId);
    }

    private void EnsureMainThread()
    {
        if (!_isMainThread())
            throw new InvalidOperationException("Network RPC calls must run on Unity's main thread.");
    }

    private sealed record PendingInvocation(
        Action<ModNetworkRpcResult<TResponse>> Completed,
        long Deadline);

    private sealed class RpcCall(NetworkRpc<TRequest, TResponse> owner, uint requestId) :
        IModNetworkRpcCall
    {
        public uint RequestId { get; } = requestId;
        public bool IsPending => owner.IsPending(RequestId);
        public void Cancel() => owner.Cancel(RequestId);
    }
}
