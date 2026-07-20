using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModMessageBus : IModMessageBus
{
    private static readonly List<Subscription> AllSubscriptions = [];
    private static readonly Dictionary<RetainedKey, ModMessage> Retained = [];
    private static long _nextSequence;
    private static int _dispatchDepth;

    private readonly string _ownerId;
    private readonly ModRuntime.ModLogger _logger;
    private readonly List<Subscription> _subscriptions = [];
    private bool _removed;

    internal ModMessageBus(string ownerId, ModRuntime.ModLogger logger)
    {
        _ownerId = ownerId;
        _logger = logger;
    }

    public IReadOnlyList<IModMessageSubscription> Subscriptions
    {
        get
        {
            EnsureRuntimeAccess();
            return _subscriptions
                .Where(subscription => subscription.IsSubscribed)
                .Cast<IModMessageSubscription>()
                .ToArray();
        }
    }

    public IModMessageSubscription Subscribe(
        string topic,
        Action<ModMessage> handler,
        ModMessageSubscriptionOptions? options = null)
    {
        EnsureUsable();
        topic = ValidateTopic(topic);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new ModMessageSubscriptionOptions();
        var sender = ValidateOptionalModId(options.SenderModId, nameof(options.SenderModId));
        if (_subscriptions.Count(subscription => subscription.IsSubscribed) >=
            ModMessageBusLimits.MaximumSubscriptionsPerMod)
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' reached the local message subscription limit.");

        var subscription = new Subscription(this, topic, sender, handler);
        _subscriptions.Add(subscription);
        AllSubscriptions.Add(subscription);
        if (options.ReplayRetained)
        {
            var replay = Retained.Values
                .Where(message => Matches(subscription, message))
                .OrderBy(message => message.Sequence)
                .ToArray();
            foreach (var message in replay)
            {
                if (!subscription.IsSubscribed) break;
                if (ModRuntime.IsMainThread)
                    Invoke(subscription, message);
                else
                    ModRuntime.EnqueueMainThread(() =>
                    {
                        if (subscription.IsSubscribed) Invoke(subscription, message);
                    });
            }
        }
        return subscription;
    }

    public void Publish(
        string topic,
        ReadOnlyMemory<byte> payload,
        ModMessagePublishOptions? options = null)
    {
        EnsureUsable();
        topic = ValidateTopic(topic);
        options ??= new ModMessagePublishOptions();
        var target = ValidateOptionalModId(options.TargetModId, nameof(options.TargetModId));
        if (payload.Length > ModMessageBusLimits.MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Local message payloads cannot exceed " +
                $"{ModMessageBusLimits.MaximumPayloadBytes} bytes.");
        if (_dispatchDepth >= ModMessageBusLimits.MaximumDispatchDepth)
            throw new InvalidOperationException(
                $"Local message dispatch exceeded depth " +
                $"{ModMessageBusLimits.MaximumDispatchDepth}.");

        var message = new ModMessage(
            checked(++_nextSequence),
            _ownerId,
            topic,
            payload.ToArray(),
            target,
            options.Retain);
        if (options.Retain) Retain(message);
        var recipients = MatchingSubscriptions(message);
        if (ModRuntime.IsMainThread)
            Dispatch(message, recipients);
        else
            ModRuntime.EnqueueMainThread(() => Dispatch(message, recipients));
    }

    public bool RemoveRetained(string topic, string? targetModId = null)
    {
        EnsureUsable();
        topic = ValidateTopic(topic);
        var target = ValidateOptionalModId(targetModId, nameof(targetModId));
        return Retained.Remove(RetainedKey.Create(_ownerId, topic, target));
    }

    internal void RemoveAll()
    {
        EnsureRuntimeAccess();
        if (_removed) return;
        foreach (var subscription in _subscriptions.ToArray().Reverse())
            subscription.Unsubscribe();
        foreach (var key in Retained.Keys
                     .Where(key => string.Equals(
                         key.SenderModId, _ownerId, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            Retained.Remove(key);
        _removed = true;
    }

    internal static void Initialize()
    {
        AllSubscriptions.Clear();
        Retained.Clear();
        _nextSequence = 0;
        _dispatchDepth = 0;
    }

    internal static void ResetForTests() => Initialize();

    internal static void ValidateTopicForTests(string topic) => _ = ValidateTopic(topic);

    private void Retain(ModMessage message)
    {
        var key = RetainedKey.Create(message.SenderModId, message.Topic, message.TargetModId);
        var current = Retained
            .Where(pair => string.Equals(
                pair.Key.SenderModId, _ownerId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var replacingBytes = Retained.TryGetValue(key, out var replacing)
            ? replacing.Payload.Length
            : 0;
        if (replacing is null && current.Length >= ModMessageBusLimits.MaximumRetainedMessagesPerMod)
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' reached the retained-message limit.");
        var retainedBytes = current.Sum(pair => pair.Value.Payload.Length);
        if (checked(retainedBytes - replacingBytes + message.Payload.Length) >
            ModMessageBusLimits.MaximumRetainedBytesPerMod)
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' reached the retained-message byte limit.");
        Retained[key] = message;
    }

    private static Subscription[] MatchingSubscriptions(ModMessage message) =>
        AllSubscriptions
            .Where(subscription => Matches(subscription, message))
            .ToArray();

    private static void Dispatch(ModMessage message, IReadOnlyList<Subscription> recipients)
    {
        foreach (var subscription in recipients)
        {
            if (subscription.IsSubscribed) Invoke(subscription, message);
        }
    }

    private static void Invoke(Subscription subscription, ModMessage message)
    {
        if (_dispatchDepth >= ModMessageBusLimits.MaximumDispatchDepth)
        {
            subscription.Logger.Error(
                $"Local message handler skipped because dispatch exceeded depth " +
                $"{ModMessageBusLimits.MaximumDispatchDepth}.");
            return;
        }
        ++_dispatchDepth;
        try
        {
            try
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    subscription.OwnerId,
                    $"message:local:{message.Topic}");
                subscription.Handler(message with { Payload = message.Payload.ToArray() });
            }
            catch (Exception exception)
            {
                subscription.Logger.Error(
                    exception,
                    $"Local message handler failed for topic '{message.Topic}' " +
                    $"from '{message.SenderModId}'.");
            }
        }
        finally
        {
            --_dispatchDepth;
        }
    }

    private static bool Matches(Subscription subscription, ModMessage message) =>
        subscription.IsSubscribed &&
        string.Equals(subscription.Topic, message.Topic, StringComparison.Ordinal) &&
        (subscription.SenderModId is null || string.Equals(
            subscription.SenderModId,
            message.SenderModId,
            StringComparison.OrdinalIgnoreCase)) &&
        (message.TargetModId is null || string.Equals(
            message.TargetModId,
            subscription.OwnerId,
            StringComparison.OrdinalIgnoreCase));

    private void Remove(Subscription subscription)
    {
        _subscriptions.Remove(subscription);
        AllSubscriptions.Remove(subscription);
    }

    private void EnsureUsable()
    {
        EnsureRuntimeAccess();
        if (_removed) throw new ObjectDisposedException(nameof(IModMessageBus));
    }

    private static string ValidateTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (topic.Length > ModMessageBusLimits.MaximumTopicLength ||
            !string.Equals(topic, topic.Trim(), StringComparison.Ordinal) ||
            topic.Any(character =>
                character > 0x7f ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '-' or '_' or '/' or ':')))
            throw new ArgumentException(
                "Topics must contain only ASCII letters, digits, '.', '-', '_', '/' or ':' " +
                $"and at most {ModMessageBusLimits.MaximumTopicLength} characters.",
                nameof(topic));
        return topic;
    }

    private static string? ValidateOptionalModId(string? value, string parameterName)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 80 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException(
                "Mod ids must contain 3-80 ASCII letters, digits, dots, dashes or underscores.",
                parameterName);
        return value;
    }

    private static void EnsureRuntimeAccess()
    {
        if (!ModRuntime.CanUseLocalMessages)
            throw new InvalidOperationException(
                "Local mod messages may be registered during IOFSMod.Load and otherwise " +
                "must be used on Unity's main thread. Use context.MainThread.Post().");
    }

    private sealed class Subscription(
        ModMessageBus owner,
        string topic,
        string? senderModId,
        Action<ModMessage> handler) : IModMessageSubscription
    {
        public string OwnerId => owner._ownerId;
        public string Topic { get; } = topic;
        public string? SenderModId { get; } = senderModId;
        public bool IsSubscribed { get; private set; } = true;
        internal Action<ModMessage> Handler { get; } = handler;
        internal ModRuntime.ModLogger Logger => owner._logger;

        public void Unsubscribe()
        {
            EnsureRuntimeAccess();
            if (!IsSubscribed) return;
            IsSubscribed = false;
            owner.Remove(this);
        }

        public void Dispose() => Unsubscribe();
    }

    private readonly record struct RetainedKey(
        string SenderModId,
        string Topic,
        string? TargetModId)
    {
        internal static RetainedKey Create(string sender, string topic, string? target) =>
            new(sender.ToLowerInvariant(), topic, target?.ToLowerInvariant());
    }
}
