using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModMechanics(
    string ownerId,
    IModEvents events,
    IModLogger logger) : IModMechanics
{
    private readonly List<Mechanic> _mechanics = new();

    public void Attach()
    {
        events.FrameUpdate += OnFrameUpdate;
        events.SceneLoaded += OnSceneLoaded;
        events.SceneUnloaded += OnSceneUnloaded;
    }

    public IMechanic Register(MechanicDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.Update);
        if (definition.Id.Length > 100)
        {
            throw new ArgumentException("Mechanic id must contain at most 100 characters.");
        }
        if (_mechanics.Any(mechanic =>
                mechanic.IsRegistered &&
                string.Equals(mechanic.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Mod '{ownerId}' already registered mechanic '{definition.Id}'.");
        }

        var handle = new Mechanic(this, definition, logger);
        _mechanics.Add(handle);
        _mechanics.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        logger.Info($"Registered mechanic '{definition.Id}' at order {definition.Order}.");
        return handle;
    }

    public void RemoveAll()
    {
        foreach (var mechanic in _mechanics.ToArray())
        {
            mechanic.Unregister();
        }
        events.FrameUpdate -= OnFrameUpdate;
        events.SceneLoaded -= OnSceneLoaded;
        events.SceneUnloaded -= OnSceneUnloaded;
    }

    private void OnFrameUpdate(FrameEvent frame)
    {
        foreach (var mechanic in _mechanics.ToArray())
        {
            mechanic.InvokeUpdate(frame);
        }
    }

    private void OnSceneLoaded(SceneEvent scene)
    {
        foreach (var mechanic in _mechanics.ToArray())
        {
            mechanic.InvokeSceneLoaded(scene);
        }
    }

    private void OnSceneUnloaded(SceneEvent scene)
    {
        foreach (var mechanic in _mechanics.ToArray())
        {
            mechanic.InvokeSceneUnloaded(scene);
        }
    }

    private void Remove(Mechanic mechanic)
    {
        if (!mechanic.IsRegistered)
        {
            return;
        }
        _mechanics.Remove(mechanic);
        mechanic.MarkUnregistered();
    }

    private sealed class Mechanic(
        ModMechanics owner,
        MechanicDefinition definition,
        IModLogger mechanicLogger) : IMechanic
    {
        public string Id { get; } = definition.Id;
        public int Order { get; } = definition.Order;
        public bool Enabled { get; set; } = true;
        public bool IsRegistered { get; private set; } = true;

        public void Unregister() => owner.Remove(this);
        public void Dispose() => Unregister();
        public void MarkUnregistered() => IsRegistered = false;

        public void InvokeUpdate(FrameEvent frame)
        {
            if (Enabled && IsRegistered)
            {
                Invoke(() => definition.Update(frame), "Update");
            }
        }

        public void InvokeSceneLoaded(SceneEvent scene)
        {
            if (Enabled && IsRegistered && definition.SceneLoaded is not null)
            {
                Invoke(() => definition.SceneLoaded(scene), "SceneLoaded");
            }
        }

        public void InvokeSceneUnloaded(SceneEvent scene)
        {
            if (Enabled && IsRegistered && definition.SceneUnloaded is not null)
            {
                Invoke(() => definition.SceneUnloaded(scene), "SceneUnloaded");
            }
        }

        private void Invoke(Action callback, string phase)
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                mechanicLogger.Error(exception, $"Mechanic '{Id}' failed during {phase}.");
                if (definition.DisableOnException)
                {
                    Enabled = false;
                    mechanicLogger.Warning($"Mechanic '{Id}' was disabled after an exception.");
                }
            }
        }
    }
}
