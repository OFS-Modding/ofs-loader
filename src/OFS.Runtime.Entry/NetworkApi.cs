using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class NetworkApi(
    string ownerId,
    IUnityApi unity,
    IUnsafeIl2CppApi unsafeApi,
    NpcApi npcApi,
    EntityApi entityApi,
    IModEvents events,
    IModLogger logger) : INetworkApi
{
    private static readonly Dictionary<nint, PrefabState> RegisteredPrefabs = new();
    private readonly List<PrefabRegistration> _prefabRegistrations = [];
    private readonly List<NetworkEntityRegistration> _entityRegistrations = [];
    private readonly List<NetworkedObject> _objects = [];
    private readonly List<IModNetworkChannel> _channels = [];
    private readonly List<IReplicatedStateRuntime> _states = [];
    private readonly List<INetworkRpcRuntime> _rpcs = [];
    private readonly nint _networkServerClass = RequireClass(unsafeApi, "NetworkServer");
    private readonly nint _networkClientClass = RequireClass(unsafeApi, "NetworkClient");
    private readonly nint _networkIdentityClass = RequireClass(unsafeApi, "NetworkIdentity");
    private readonly nint _networkServerSpawnedField = RequireField(
        unsafeApi, RequireClass(unsafeApi, "NetworkServer"), "spawned");
    private readonly nint _networkClientSpawnedField = RequireField(
        unsafeApi, RequireClass(unsafeApi, "NetworkClient"), "spawned");
    private readonly nint _componentGetGameObject = RequireMethod(
        unsafeApi,
        RequireClass(unsafeApi, "UnityEngine.CoreModule.dll", "UnityEngine", "Component"),
        "get_gameObject",
        0);
    private readonly nint _networkServerSpawnMethod = RequireMethod(
        unsafeApi,
        RequireClass(unsafeApi, "NetworkServer"),
        "Spawn",
        ["UnityEngine.GameObject", "Mirror.NetworkConnectionToClient"]);

    public void Attach()
    {
        events.SceneUnloaded += OnSceneUnloaded;
        events.FrameUpdate += OnFrameUpdate;
    }

    public NetworkCompatibilityProfile CompatibilityProfile =>
        NetworkCompatibilityRuntime.Profile;

    public NetworkCompatibilityResult LastCompatibilityCheck =>
        NetworkCompatibilityRuntime.LastCheck;

    public NetworkRemediationPlan? LastRemediationPlan =>
        NetworkCompatibilityRuntime.LastRemediationPlan;

    public bool IsServerActive
    {
        get
        {
            EnsureMainThread();
            return ReadStaticBoolean(_networkServerClass, "get_active");
        }
    }

    public bool IsClientActive
    {
        get
        {
            EnsureMainThread();
            return ReadStaticBoolean(_networkClientClass, "get_active");
        }
    }

    public INetworkPrefabRegistration RegisterPrefab(UnityObject prefab)
    {
        EnsureMainThread();
        EnsureGameObject(prefab);
        var identity = unity.TryGetComponent(prefab, "Mirror.dll", "Mirror", "NetworkIdentity");
        if (identity.IsNull)
        {
            throw new ArgumentException("Network prefab must contain Mirror.NetworkIdentity.");
        }

        if (RegisteredPrefabs.TryGetValue(prefab.Pointer, out var existing))
        {
            if (existing.OwnerId != ownerId)
            {
                throw new InvalidOperationException(
                    $"Network prefab 0x{prefab.Pointer:X} is already owned by mod '{existing.OwnerId}'.");
            }
            existing.ReferenceCount++;
        }
        else
        {
            InvokeStaticWithObject(_networkClientClass, "RegisterPrefab", prefab.Pointer);
            RegisteredPrefabs.Add(prefab.Pointer, new PrefabState(ownerId, prefab));
        }
        var registration = new PrefabRegistration(this, ownerId, prefab);
        _prefabRegistrations.Add(registration);
        return registration;
    }

    public INetworkEntityDefinitionRegistration RegisterEntityDefinition(string entityDefinitionId)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(entityDefinitionId);
        if (_entityRegistrations.Any(value => value.IsRegistered &&
            string.Equals(value.DefinitionId, entityDefinitionId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Network entity definition '{entityDefinitionId}' is already registered.");
        var prefabs = entityApi.GetDefinitionPrefabs(entityDefinitionId);
        var leases = new List<INetworkPrefabRegistration>();
        try
        {
            foreach (var prefab in prefabs) leases.Add(RegisterPrefab(prefab));
            var registration = new NetworkEntityRegistration(
                this, ownerId, entityDefinitionId, prefabs, leases);
            _entityRegistrations.Add(registration);
            logger.Info(
                $"Registered network entity definition '{registration.QualifiedId}' with " +
                $"{prefabs.Count} unique prefab(s).");
            return registration;
        }
        catch
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
            throw;
        }
    }

    public IModNetworkChannel RegisterChannel(ModNetworkChannelDefinition definition)
    {
        var channel = NetworkMessageRuntime.Register(ownerId, logger, definition);
        _channels.Add(channel);
        return channel;
    }

    public IModReplicatedState<T> RegisterState<T>(ModReplicatedStateDefinition<T> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ReplicatedStateRuntime.ValidateId(definition.Id);
        if (_states.Any(value => value.IsRegistered && string.Equals(
                value.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Replicated state '{ownerId}:{definition.Id}' is already registered.");
        var state = ReplicatedStateRuntime.Create(
            ownerId,
            logger,
            definition,
            () => ReadStaticBoolean(_networkServerClass, "get_active"),
            () => ReadStaticBoolean(_networkClientClass, "get_active"),
            RemoveState);
        _states.Add(state);
        return state;
    }

    public IModNetworkRpc<TRequest, TResponse> RegisterRpc<TRequest, TResponse>(
        ModNetworkRpcDefinition<TRequest, TResponse> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        NetworkRpcRuntime.ValidateId(definition.Id);
        if (_rpcs.Any(value => value.IsRegistered && string.Equals(
                value.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Network RPC '{ownerId}:{definition.Id}' is already registered.");
        var rpc = NetworkRpcRuntime.Create(
            ownerId,
            logger,
            definition,
            () => ReadStaticBoolean(_networkClientClass, "get_active"),
            RemoveRpc);
        _rpcs.Add(rpc);
        return rpc;
    }

    public INetworkedObject SpawnServer(PrefabSpawnDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsServerActive)
        {
            throw new InvalidOperationException(
                "Mirror NetworkServer is not active. Networked objects may only be spawned by host/server.");
        }
        if (!RegisteredPrefabs.TryGetValue(definition.Prefab.Pointer, out var registered) ||
            !string.Equals(registered.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Network prefab is not registered. Every peer must register the same prefab before server spawn.");
        }

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
            InvokeStaticWithObjectAndNull(_networkServerSpawnMethod, gameObject.Pointer);
            var identity = unity.TryGetComponent(
                gameObject, "Mirror.dll", "Mirror", "NetworkIdentity");
            if (identity.IsNull || ReadNetId(identity) == 0)
                throw new InvalidOperationException(
                    "Mirror spawned the prefab without assigning a valid NetworkIdentity netId.");
            var networked = new NetworkedObject(
                this, gameObject, identity, definition.Persistent);
            _objects.Add(networked);
            return networked;
        }
        catch
        {
            unity.Destroy(gameObject);
            throw;
        }
    }

    public INetworkedNpc SpawnNpcServer(NetworkNpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var networked = SpawnServer(new PrefabSpawnDefinition(
            definition.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Parent,
            definition.Name,
            definition.Persistent,
            definition.Active));
        try
        {
            var npc = npcApi.AttachOwned(
                networked.GameObject,
                () => networked.IsSpawned,
                networked.Despawn,
                definition.RequireNpcAnimator,
                definition.RequireNavigation);
            return new NetworkedNpc(networked, npc);
        }
        catch
        {
            networked.Despawn();
            throw;
        }
    }

    public INetworkedEntity SpawnEntityServer(NetworkEntitySpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DefinitionId);
        var registration = _entityRegistrations.FirstOrDefault(value =>
            value.IsRegistered && string.Equals(
                value.DefinitionId, definition.DefinitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Network entity definition '{definition.DefinitionId}' is not registered on this peer.");
        var localDefinition = new EntitySpawnDefinition(
            definition.DefinitionId,
            definition.Position,
            definition.Rotation,
            definition.VariantId,
            definition.Name,
            definition.Parent,
            definition.Persistent,
            definition.Active);
        var resolved = entityApi.Resolve(localDefinition);
        if (!registration.Prefabs.Any(value => value.Pointer == resolved.Prefab.Pointer))
            throw new InvalidOperationException(
                $"Entity prefab 0x{resolved.Prefab.Pointer:X} is not covered by its network registration.");
        var networked = SpawnServer(new PrefabSpawnDefinition(
            resolved.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Parent,
            resolved.Name,
            resolved.Persistent,
            resolved.Active));
        try
        {
            var entity = entityApi.AttachResolved(
                resolved,
                networked.GameObject,
                () => networked.IsSpawned,
                networked.Despawn);
            var result = new NetworkedEntity(networked, entity);
            logger.Info(
                $"Spawned network entity '{registration.QualifiedId}' " +
                $"variant='{definition.VariantId ?? "<default>"}', netId={result.NetId}.");
            return result;
        }
        catch
        {
            networked.Despawn();
            throw;
        }
    }

    public bool TryResolveObject(
        NetworkObjectReference reference,
        out NetworkObjectResolution resolution)
    {
        EnsureMainThread();
        resolution = default;
        if (!reference.IsValid) return false;

        var serverIdentity = TryResolveSpawned(_networkServerClass, _networkServerSpawnedField, reference.NetId);
        var clientIdentity = TryResolveSpawned(_networkClientClass, _networkClientSpawnedField, reference.NetId);
        if (serverIdentity == 0 && clientIdentity == 0) return false;
        if (serverIdentity != 0 && clientIdentity != 0 && serverIdentity != clientIdentity)
            throw new InvalidOperationException(
                $"Mirror server/client registries disagree for netId {reference.NetId}.");

        var identity = new UnityObject(serverIdentity != 0 ? serverIdentity : clientIdentity);
        var gameObject = new UnityObject(
            unsafeApi.RuntimeInvoke(_componentGetGameObject, identity.Pointer, 0));
        if (gameObject.IsNull)
            throw new InvalidOperationException(
                $"Mirror NetworkIdentity for netId {reference.NetId} has no GameObject.");
        var side = (serverIdentity != 0 ? NetworkObjectSide.Server : NetworkObjectSide.None) |
                   (clientIdentity != 0 ? NetworkObjectSide.Client : NetworkObjectSide.None);
        var owned = _objects.Any(value =>
            value.IsSpawned &&
            value.NetworkIdentity.Pointer == identity.Pointer &&
            value.NetId == reference.NetId);
        resolution = new NetworkObjectResolution(reference, identity, gameObject, side, owned);
        return true;
    }

    public NetworkObjectResolution ResolveObject(NetworkObjectReference reference) =>
        TryResolveObject(reference, out var resolution)
            ? resolution
            : throw new KeyNotFoundException(
                $"Mirror object netId {reference.NetId} is not spawned on this peer.");

    public NetworkObjectResolution ResolveOwnedObject(NetworkObjectReference reference)
    {
        var resolution = ResolveObject(reference);
        if (!resolution.IsOwnedByMod)
            throw new UnauthorizedAccessException(
                $"Mirror object netId {reference.NetId} is not owned by mod '{ownerId}'.");
        return resolution;
    }

    private void Unregister(PrefabRegistration registration)
    {
        EnsureMainThread();
        if (!registration.IsRegistered)
        {
            return;
        }
        if (!RegisteredPrefabs.TryGetValue(registration.Prefab.Pointer, out var existing) ||
            existing.OwnerId != ownerId || existing.ReferenceCount < 1)
        {
            throw new InvalidOperationException("Network prefab registration ownership changed.");
        }
        _prefabRegistrations.Remove(registration);
        existing.ReferenceCount--;
        if (existing.ReferenceCount == 0)
        {
            InvokeStaticWithObject(_networkClientClass, "UnregisterPrefab", registration.Prefab.Pointer);
            RegisteredPrefabs.Remove(registration.Prefab.Pointer);
        }
        registration.MarkUnregistered();
    }

    private void Unregister(NetworkEntityRegistration registration)
    {
        EnsureMainThread();
        if (!registration.IsRegistered) return;
        if (!_entityRegistrations.Remove(registration))
            throw new InvalidOperationException("Network entity definition ownership changed.");
        foreach (var lease in registration.Leases.Reverse()) lease.Dispose();
        registration.MarkUnregistered();
    }

    private void Despawn(NetworkedObject networkedObject)
    {
        EnsureMainThread();
        if (!networkedObject.IsSpawned)
        {
            return;
        }
        if (!IsServerActive)
        {
            networkedObject.MarkDespawned();
            _objects.Remove(networkedObject);
            logger.Warning(
                "NetworkServer stopped before object cleanup; handle was abandoned after server shutdown.");
            return;
        }
        InvokeStaticWithObject(_networkServerClass, "Destroy", networkedObject.GameObject.Pointer);
        networkedObject.MarkDespawned();
        _objects.Remove(networkedObject);
    }

    internal uint ReadNetId(UnityObject identity)
    {
        var boxed = unsafeApi.RuntimeInvoke(
            RequireMethod(unsafeApi, _networkIdentityClass, "get_netId", 0),
            identity.Pointer,
            0);
        var value = unsafeApi.Unbox(boxed);
        return value == 0 ? 0u : unchecked((uint)Marshal.ReadInt32(value));
    }

    private unsafe nint TryResolveSpawned(nint klass, nint spawnedField, uint netId)
    {
        unsafeApi.EnsureClassInitialized(klass);
        var dictionary = unsafeApi.ReadStaticObjectReference(spawnedField);
        if (dictionary == 0) return 0;
        var tryGetValue = RequireMethod(
            unsafeApi, unsafeApi.GetObjectClass(dictionary), "TryGetValue", 2);
        nint found = 0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = (nint)(&netId);
        arguments[1] = (nint)(&found);
        var boxed = unsafeApi.RuntimeInvoke(tryGetValue, dictionary, (nint)arguments);
        var value = unsafeApi.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0 ? found : 0;
    }

    internal void RemoveAll()
    {
        foreach (var rpc in _rpcs.ToArray().Reverse())
        {
            try { rpc.Unregister(); }
            catch (Exception exception)
            {
                logger.Error(exception, "Network RPC rollback cleanup failed.");
            }
        }
        _rpcs.Clear();
        foreach (var state in _states.ToArray().Reverse())
        {
            try { state.Unregister(); }
            catch (Exception exception)
            {
                logger.Error(exception, "Replicated state rollback cleanup failed.");
            }
        }
        _states.Clear();
        foreach (var channel in _channels.ToArray().Reverse())
        {
            try { channel.Unregister(); }
            catch (Exception exception)
            {
                logger.Error(exception, "Network channel rollback cleanup failed.");
            }
        }
        _channels.Clear();
        foreach (var networked in _objects.ToArray().Reverse())
        {
            try { networked.Despawn(); }
            catch (Exception exception)
            {
                networked.MarkDespawned();
                logger.Error(exception, "Network object rollback cleanup failed.");
            }
        }
        _objects.Clear();
        foreach (var registration in _entityRegistrations.ToArray().Reverse())
            registration.Unregister();
        foreach (var registration in _prefabRegistrations.ToArray().Reverse())
            registration.Unregister();
    }

    private void OnSceneUnloaded(SceneEvent _)
    {
        foreach (var networked in _objects.Where(value => !value.Persistent).ToArray())
        {
            networked.MarkDespawned();
            _objects.Remove(networked);
        }
    }

    private void OnFrameUpdate(FrameEvent frame)
    {
        var serverActive = ReadStaticBoolean(_networkServerClass, "get_active");
        var clientActive = ReadStaticBoolean(_networkClientClass, "get_active");
        foreach (var state in _states.ToArray())
            state.OnFrame(frame, serverActive, clientActive);
        foreach (var rpc in _rpcs.ToArray()) rpc.OnFrame(frame);
    }

    private void RemoveState(IReplicatedStateRuntime state) => _states.Remove(state);
    private void RemoveRpc(INetworkRpcRuntime rpc) => _rpcs.Remove(rpc);

    private bool ReadStaticBoolean(nint klass, string getter)
    {
        var boxed = unsafeApi.RuntimeInvoke(RequireMethod(unsafeApi, klass, getter, 0), 0, 0);
        var value = unsafeApi.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private unsafe void InvokeStaticWithObject(nint klass, string methodName, nint value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = value;
        _ = unsafeApi.RuntimeInvoke(
            RequireMethod(unsafeApi, klass, methodName, 1),
            0,
            (nint)arguments);
    }

    private unsafe void InvokeStaticWithObjectAndNull(nint method, nint value)
    {
        nint* arguments = stackalloc nint[2];
        arguments[0] = value;
        arguments[1] = 0;
        _ = unsafeApi.RuntimeInvoke(method, 0, (nint)arguments);
    }

    private static nint RequireClass(IUnsafeIl2CppApi unsafeApi, string name)
    {
        var klass = unsafeApi.FindClass("Mirror.dll", "Mirror", name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Mirror.{name} was not found in Mirror.dll.");
    }

    private static nint RequireClass(
        IUnsafeIl2CppApi unsafeApi,
        string assembly,
        string namespaze,
        string name)
    {
        var klass = unsafeApi.FindClass(assembly, namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException(
                $"{namespaze}.{name} was not found in {assembly}.");
    }

    private static nint RequireField(IUnsafeIl2CppApi unsafeApi, nint klass, string name)
    {
        var field = unsafeApi.FindField(klass, name);
        return field != 0 ? field : throw new MissingFieldException(name);
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi unsafeApi,
        nint klass,
        string name,
        int argumentCount)
    {
        var method = unsafeApi.FindMethod(klass, name, argumentCount);
        return method != 0
            ? method
            : throw new MissingMethodException($"Mirror method '{name}/{argumentCount}' was not found.");
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi unsafeApi,
        nint klass,
        string name,
        IReadOnlyList<string> parameterTypeNames)
    {
        var method = unsafeApi.FindMethodBySignature(klass, name, parameterTypeNames);
        return method != 0
            ? method
            : throw new MissingMethodException(
                $"Mirror method '{name}({string.Join(", ", parameterTypeNames)})' was not found.");
    }

    private static void EnsureGameObject(UnityObject value)
    {
        if (value.IsNull)
        {
            throw new ArgumentException("Network prefab is null.");
        }
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
        {
            throw new InvalidOperationException("Network API calls must run on Unity's main thread.");
        }
    }

    private sealed class PrefabRegistration(
        NetworkApi api,
        string ownerId,
        UnityObject prefab) : INetworkPrefabRegistration
    {
        public string OwnerId { get; } = ownerId;
        public UnityObject Prefab { get; } = prefab;
        public bool IsRegistered { get; private set; } = true;
        public void Unregister() => api.Unregister(this);
        public void Dispose() => Unregister();
        public void MarkUnregistered() => IsRegistered = false;
    }

    private sealed class PrefabState(string ownerId, UnityObject prefab)
    {
        public string OwnerId { get; } = ownerId;
        public UnityObject Prefab { get; } = prefab;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class NetworkEntityRegistration(
        NetworkApi api,
        string ownerId,
        string definitionId,
        IReadOnlyList<UnityObject> prefabs,
        IReadOnlyList<INetworkPrefabRegistration> leases) : INetworkEntityDefinitionRegistration
    {
        public string DefinitionId { get; } = definitionId;
        public string QualifiedId { get; } = $"{ownerId}:{definitionId}";
        public IReadOnlyList<UnityObject> Prefabs { get; } = prefabs;
        public bool IsRegistered { get; private set; } = true;
        internal IReadOnlyList<INetworkPrefabRegistration> Leases { get; } = leases;
        public void Unregister() => api.Unregister(this);
        public void Dispose() => Unregister();
        internal void MarkUnregistered() => IsRegistered = false;
    }

    private sealed class NetworkedObject(
        NetworkApi api,
        UnityObject gameObject,
        UnityObject networkIdentity,
        bool persistent) : INetworkedObject
    {
        public UnityObject GameObject { get; } = gameObject;
        public UnityObject NetworkIdentity { get; } = networkIdentity;
        public uint NetId => IsSpawned ? api.ReadNetId(NetworkIdentity) : 0;
        public NetworkObjectReference Reference => new(NetId);
        internal bool Persistent { get; } = persistent;
        public bool IsSpawned { get; private set; } = true;
        public void Despawn() => api.Despawn(this);
        public void Dispose() => Despawn();
        public void MarkDespawned() => IsSpawned = false;
    }

    private sealed class NetworkedNpc(
        INetworkedObject networked,
        INpc npc) : INetworkedNpc
    {
        public string OwnerId => npc.OwnerId;
        public UnityObject GameObject => networked.GameObject;
        public UnityObject NetworkIdentity => networked.NetworkIdentity;
        public uint NetId => networked.NetId;
        public NetworkObjectReference Reference => networked.Reference;
        public UnityObject Animator => npc.Animator;
        public INpcNavigation? Navigation => npc.Navigation;
        public bool IsSpawned => networked.IsSpawned;

        public UnityTransform Transform
        {
            get => npc.Transform;
            set => npc.Transform = value;
        }

        public void SetIdleAnimation(int index) => npc.SetIdleAnimation(index);
        public void PlayAction(int index) => npc.PlayAction(index);
        public void StopAction() => npc.StopAction();
        public void Despawn() => npc.Despawn();
        public void Dispose() => Despawn();
    }

    private sealed class NetworkedEntity(
        INetworkedObject networked,
        IEntity entity) : INetworkedEntity
    {
        public string OwnerId => entity.OwnerId;
        public string DefinitionId => entity.DefinitionId;
        public string? VariantId => entity.VariantId;
        public UnityObject GameObject => networked.GameObject;
        public bool Persistent => entity.Persistent;
        public bool IsSpawned => networked.IsSpawned && entity.IsSpawned;
        public IInteractionRegistration? Interaction => entity.Interaction;
        public UnityObject NetworkIdentity => networked.NetworkIdentity;
        public uint NetId => networked.NetId;
        public NetworkObjectReference Reference => networked.Reference;
        public UnityTransform Transform
        {
            get => entity.Transform;
            set => entity.Transform = value;
        }
        public void Despawn() => entity.Despawn();
        public void Dispose() => Despawn();
    }
}
