using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class WorldApi(
    string ownerId,
    IUnityApi unity,
    IModEvents events,
    IModLogger logger) : IWorldApi
{
    private readonly List<SpawnedObject> _spawned = new();

    public void Attach() => events.SceneUnloaded += OnSceneUnloaded;

    public ISpawnedObject Spawn(PrefabSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Prefab.IsNull)
        {
            throw new ArgumentException("Spawn prefab is null.");
        }
        ValidateFinite(definition.Position, nameof(definition.Position));
        ValidateFinite(definition.Rotation, nameof(definition.Rotation));

        var gameObject = unity.Instantiate(
            definition.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Parent);
        try
        {
            if (!string.IsNullOrWhiteSpace(definition.Name))
            {
                unity.SetName(gameObject, definition.Name);
            }
            if (definition.Persistent)
            {
                unity.DontDestroyOnLoad(gameObject);
            }
            unity.SetActive(gameObject, definition.Active);

            var handle = new SpawnedObject(this, ownerId, gameObject, definition.Persistent, unity);
            _spawned.Add(handle);
            return handle;
        }
        catch
        {
            unity.Destroy(gameObject);
            throw;
        }
    }

    internal void Remove(SpawnedObject instance) => _spawned.Remove(instance);

    internal void RemoveAll()
    {
        foreach (var instance in _spawned.ToArray().Reverse())
        {
            try { instance.Despawn(); }
            catch (Exception exception) { logger.Error(exception, "World object rollback cleanup failed."); }
        }
        _spawned.Clear();
    }

    private void OnSceneUnloaded(SceneEvent _)
    {
        foreach (var instance in _spawned.Where(value => !value.Persistent).ToArray())
            instance.Abandon();
        _spawned.RemoveAll(value => !value.IsSpawned);
    }

    private static void ValidateFinite(UnityVector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameter, "Vector components must be finite.");
        }
    }

    private static void ValidateFinite(UnityQuaternion value, string parameter)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(parameter, "Quaternion components must be finite.");
        }
        var magnitudeSquared =
            value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W;
        if (magnitudeSquared < 0.000001f)
        {
            throw new ArgumentOutOfRangeException(parameter, "Quaternion must have non-zero magnitude.");
        }
    }

    internal sealed class SpawnedObject(
        WorldApi owner,
        string ownerId,
        UnityObject gameObject,
        bool persistent,
        IUnityApi unity) : ISpawnedObject
    {
        public string OwnerId { get; } = ownerId;
        public UnityObject GameObject { get; } = gameObject;
        internal bool Persistent { get; } = persistent;
        public bool IsSpawned { get; private set; } = true;

        public void Despawn()
        {
            if (!IsSpawned)
            {
                return;
            }
            unity.Destroy(GameObject);
            IsSpawned = false;
            owner.Remove(this);
        }

        internal void Abandon() => IsSpawned = false;

        public void Dispose() => Despawn();
    }
}
