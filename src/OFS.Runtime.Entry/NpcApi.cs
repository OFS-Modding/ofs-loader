using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class NpcApi(
    string ownerId,
    IWorldApi world,
    IUnityApi unity,
    IUnsafeIl2CppApi unsafeApi,
    IModEvents events,
    IModLogger logger) : INpcApi
{
    private readonly Dictionary<string, NpcDefinitionRegistration> _definitions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Npc> _npcs = new();
    private readonly NpcBehaviorRuntime _behaviors = new(ownerId, events, logger);
    private readonly nint _npcAnimatorClass = unsafeApi.FindClass(
        "Assembly-CSharp.dll",
        string.Empty,
        "NPCAnimator");
    private readonly nint _followerEntityClass = unsafeApi.FindClass(
        "AstarPathfindingProject.dll",
        "Pathfinding",
        "FollowerEntity");
    private readonly VanillaEmployeeBinding? _vanillaEmployee =
        VanillaEmployeeBinding.TryCreate(unsafeApi, logger);

    public INpc SpawnLocal(NpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var spawned = world.Spawn(new PrefabSpawnDefinition(
            definition.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Parent,
            definition.Name,
            definition.Persistent,
            definition.Active));
        try
        {
            return AttachOwned(
                spawned.GameObject,
                () => spawned.IsSpawned,
                spawned.Despawn,
                definition.RequireNpcAnimator,
                definition.RequireNavigation);
        }
        catch
        {
            spawned.Despawn();
            throw;
        }
    }

    public INpcDefinitionRegistration RegisterDefinition(NpcDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = definition with
        {
            Variants = (definition.Variants ?? []).ToArray(),
            Behaviors = (definition.Behaviors ?? []).ToArray(),
        };
        ValidateDefinition(normalized);
        if (_definitions.ContainsKey(normalized.Id))
        {
            throw new InvalidOperationException(
                $"Mod '{ownerId}' already registered NPC definition '{normalized.Id}'.");
        }

        var registration = new NpcDefinitionRegistration(this, ownerId, normalized);
        _definitions.Add(normalized.Id, registration);
        logger.Info(
            $"Registered NPC definition '{registration.QualifiedId}' with " +
            $"{registration.VariantIds.Count} visual variant(s) and " +
            $"{normalized.Behaviors?.Count ?? 0} behavior(s).");
        return registration;
    }

    public INpc SpawnLocal(NpcDefinitionSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DefinitionId);
        if (!_definitions.TryGetValue(definition.DefinitionId, out var registration) ||
            !registration.IsRegistered)
        {
            throw new KeyNotFoundException(
                $"Mod '{ownerId}' has no active NPC definition '{definition.DefinitionId}'.");
        }

        var resolved = registration.Resolve(definition.VariantId);
        var npc = SpawnLocal(new NpcSpawnDefinition(
            resolved.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Name ?? resolved.DisplayName ?? registration.Definition.DisplayName,
            definition.Parent,
            definition.Persistent ?? registration.Definition.Persistent,
            definition.Active ?? registration.Definition.Active,
            registration.Definition.RequireNpcAnimator ||
                registration.Definition.InitialIdleAnimation.HasValue,
            registration.Definition.RequireNavigation ||
                registration.Definition.DefaultMoveSpeed.HasValue));
        try
        {
            if (registration.Definition.InitialIdleAnimation is { } idle)
            {
                npc.SetIdleAnimation(idle);
            }
            if (registration.Definition.DefaultMoveSpeed is { } speed)
            {
                npc.Navigation!.MaxSpeed = speed;
            }
            foreach (var behavior in registration.Definition.Behaviors ?? [])
            {
                _ = AttachBehavior(npc, behavior);
            }
            logger.Info(
                $"Spawned NPC definition '{registration.QualifiedId}' " +
                $"variant='{definition.VariantId ?? "<default>"}' as 0x{npc.GameObject.Pointer:X}.");
            return npc;
        }
        catch
        {
            npc.Despawn();
            throw;
        }
    }

    public INpcBehavior AttachBehavior(INpc npc, NpcBehaviorDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(npc.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Mod '{ownerId}' cannot attach behavior to NPC owned by '{npc.OwnerId}'.");
        }
        if (!npc.IsSpawned)
        {
            throw new ObjectDisposedException(nameof(npc), "Cannot attach behavior to a despawned NPC.");
        }
        return _behaviors.Attach(npc, definition);
    }

    public bool TryGetVanillaEmployee(
        UnityObject gameObject,
        out IVanillaEmployeeController controller)
    {
        EnsureMainThread();
        controller = null!;
        if (gameObject.IsNull)
        {
            throw new ArgumentException("Employee GameObject is null.", nameof(gameObject));
        }
        if (_vanillaEmployee is null)
        {
            return false;
        }
        var component = UnityUiRuntime.TryGetComponentPointer(
            gameObject.Pointer,
            _vanillaEmployee.Class);
        if (component == 0)
        {
            return false;
        }
        controller = _vanillaEmployee.Create(new UnityObject(component), gameObject);
        return true;
    }

    public IReadOnlyList<IVanillaEmployeeController> FindVanillaEmployees(
        bool activeOnly = true)
    {
        EnsureMainThread();
        if (_vanillaEmployee is null)
        {
            return [];
        }
        return unity.FindComponents(
                "Assembly-CSharp.dll",
                string.Empty,
                "T_Employee",
                activeOnly)
            .Select(component => _vanillaEmployee.Create(
                component,
                _vanillaEmployee.GetGameObject(component)))
            .ToArray();
    }

    public VanillaHiredEmployeeSnapshot HireVanillaEmployeeServer(
        VanillaEmployeeHireDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateId(definition.Id, "Vanilla employee");
        if (_vanillaEmployee is null)
        {
            throw new NotSupportedException(
                "EmployeeManager integration is unavailable for this game build.");
        }
        return _vanillaEmployee.HireServer(ownerId, definition);
    }

    public bool TryGetHiredVanillaEmployee(
        string id,
        out VanillaHiredEmployeeSnapshot employee)
    {
        EnsureMainThread();
        ValidateId(id, "Vanilla employee");
        employee = default!;
        return _vanillaEmployee is not null &&
            _vanillaEmployee.TryGetHired($"{ownerId}:{id}", out employee);
    }

    public void FireVanillaEmployeeServer(string id)
    {
        EnsureMainThread();
        ValidateId(id, "Vanilla employee");
        if (_vanillaEmployee is null)
        {
            throw new NotSupportedException(
                "EmployeeManager integration is unavailable for this game build.");
        }
        _vanillaEmployee.FireServer($"{ownerId}:{id}");
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
        {
            throw new InvalidOperationException(
                "NPC behavior attachment must run on Unity's main thread.");
        }
    }

    internal INpc AttachOwned(
        UnityObject gameObject,
        Func<bool> isSpawned,
        Action despawn,
        bool requireNpcAnimator,
        bool requireNavigation)
    {
        ArgumentNullException.ThrowIfNull(isSpawned);
        ArgumentNullException.ThrowIfNull(despawn);
        if (gameObject.IsNull)
        {
            throw new ArgumentException("NPC GameObject is null.", nameof(gameObject));
        }

        var animator = _npcAnimatorClass == 0
            ? UnityObject.Null
            : new UnityObject(UnityUiRuntime.TryGetComponentPointer(
                gameObject.Pointer,
                _npcAnimatorClass));
        if (requireNpcAnimator && animator.IsNull)
        {
            throw new InvalidOperationException(
                "Spawned NPC prefab does not contain the game's NPCAnimator component.");
        }
        var navigator = _followerEntityClass == 0
            ? UnityObject.Null
            : new UnityObject(UnityUiRuntime.TryGetComponentPointer(
                gameObject.Pointer,
                _followerEntityClass));
        if (requireNavigation && navigator.IsNull)
        {
            throw new InvalidOperationException(
                "Spawned NPC prefab does not contain Pathfinding.FollowerEntity.");
        }
        var npc = new Npc(
            this,
            ownerId,
            gameObject,
            animator,
            navigator,
            isSpawned,
            despawn,
            unity,
            unsafeApi,
            _npcAnimatorClass,
            _followerEntityClass);
        _npcs.Add(npc);
        return npc;
    }

    internal void RemoveAll()
    {
        foreach (var npc in _npcs.ToArray().Reverse())
        {
            try
            {
                npc.Despawn();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "NPC cleanup failed during owner rollback.");
            }
        }
        foreach (var registration in _definitions.Values.ToArray())
        {
            registration.MarkUnregistered();
        }
        _definitions.Clear();
        _behaviors.RemoveAll();
    }

    private void OnNpcDespawning(Npc npc)
    {
        _behaviors.DetachAll(npc);
        _npcs.Remove(npc);
    }

    private void Unregister(NpcDefinitionRegistration registration)
    {
        if (!registration.IsRegistered)
        {
            return;
        }
        if (!_definitions.TryGetValue(registration.Id, out var current) ||
            !ReferenceEquals(current, registration))
        {
            throw new InvalidOperationException("NPC definition registration ownership changed.");
        }
        _definitions.Remove(registration.Id);
        registration.MarkUnregistered();
    }

    internal static void ValidateDefinition(NpcDefinition definition)
    {
        ValidateId(definition.Id, "NPC definition");
        if (definition.Prefab.IsNull)
        {
            throw new ArgumentException("NPC definition prefab is null.", nameof(definition));
        }
        if (definition.InitialIdleAnimation is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Initial idle animation index must be non-negative.");
        }
        if (definition.DefaultMoveSpeed is { } speed &&
            (!float.IsFinite(speed) || speed < 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Default NPC movement speed must be finite and non-negative.");
        }

        var variantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in definition.Variants ?? [])
        {
            ArgumentNullException.ThrowIfNull(variant);
            ValidateId(variant.Id, "NPC visual variant");
            if (!variantIds.Add(variant.Id))
            {
                throw new ArgumentException(
                    $"NPC visual variant id '{variant.Id}' is duplicated.",
                    nameof(definition));
            }
            if (variant.Prefab.IsNull)
            {
                throw new ArgumentException(
                    $"NPC visual variant '{variant.Id}' has a null prefab.",
                    nameof(definition));
            }
        }

        var behaviorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var behavior in definition.Behaviors ?? [])
        {
            ArgumentNullException.ThrowIfNull(behavior);
            ValidateBehavior(behavior);
            if (!behaviorIds.Add(behavior.Id))
            {
                throw new ArgumentException(
                    $"NPC behavior id '{behavior.Id}' is duplicated in definition '{definition.Id}'.",
                    nameof(definition));
            }
        }
    }

    internal static void ValidateBehavior(NpcBehaviorDefinition definition)
    {
        ValidateId(definition.Id, "NPC behavior");
        if (definition.Started is null && definition.Update is null && definition.Stopped is null)
        {
            throw new ArgumentException(
                $"NPC behavior '{definition.Id}' must define at least one callback.",
                nameof(definition));
        }
    }

    private static void ValidateId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 ||
            id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                $"{kind} id must contain 1-100 ASCII letters, digits, '.', '_' or '-'.");
        }
    }

    private sealed class NpcDefinitionRegistration(
        NpcApi api,
        string registrationOwnerId,
        NpcDefinition definition) : INpcDefinitionRegistration
    {
        private readonly Dictionary<string, NpcVisualVariantDefinition> _variants =
            (definition.Variants ?? []).ToDictionary(
                variant => variant.Id,
                StringComparer.OrdinalIgnoreCase);

        public NpcDefinition Definition { get; } = definition;
        public string Id { get; } = definition.Id;
        public string QualifiedId { get; } = $"{registrationOwnerId}:{definition.Id}";
        public IReadOnlyList<string> VariantIds { get; } =
            (definition.Variants ?? []).Select(variant => variant.Id).ToArray();
        public bool IsRegistered { get; private set; } = true;

        public (UnityObject Prefab, string? DisplayName) Resolve(string? variantId)
        {
            if (string.IsNullOrWhiteSpace(variantId))
            {
                return (Definition.Prefab, Definition.DisplayName);
            }
            if (!_variants.TryGetValue(variantId, out var variant))
            {
                throw new KeyNotFoundException(
                    $"NPC definition '{QualifiedId}' has no visual variant '{variantId}'.");
            }
            return (variant.Prefab, variant.DisplayName);
        }

        public void Unregister() => api.Unregister(this);
        public void Dispose() => Unregister();
        public void MarkUnregistered() => IsRegistered = false;
    }

    private sealed class VanillaEmployeeBinding
    {
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _componentGetGameObject;
        private readonly nint _transformClass;
        private readonly nint _gameObjectClass;
        private readonly nint _gameObjectGetTransform;
        private readonly nint _networkServerGetActive;
        private readonly nint _networkBehaviourGetIsServer;
        private readonly nint _setSyncVarDirtyBit;
        private readonly nint _employeeId;
        private readonly nint _firstName;
        private readonly nint _lastName;
        private readonly nint _type;
        private readonly nint _workState;
        private readonly nint _isCarrying;
        private readonly nint _isWorking;
        private readonly nint _moveSpeed;
        private readonly nint _requestToggleWork;
        private readonly nint _requestUnstuck;
        private readonly nint _serverInitialize;
        private readonly nint _refreshInteractableName;
        private readonly nint _refreshNameText;
        private readonly nint _homePoint;
        private readonly nint _dropOffPoint;
        private readonly nint _hiredEmployeeDataClass;
        private readonly nint _hiredEmployeeId;
        private readonly nint _hiredFirstName;
        private readonly nint _hiredLastName;
        private readonly nint _hiredAvatarIndex;
        private readonly nint _hiredType;
        private readonly nint _hiredProfile;
        private readonly nint _hiredAgility;
        private readonly nint _hiredIntelligence;
        private readonly nint _hiredTechnique;
        private readonly nint _hiredStamina;
        private readonly nint _hiredDailyWage;
        private readonly nint _hiredDay;
        private readonly nint _hiredState;
        private readonly nint _hiredActiveOffsiteContractId;
        private readonly nint _hiredIsFired;
        private readonly nint _employeeManagerClass;
        private readonly nint _employeeManagerGetInstance;
        private readonly nint _employeeManagerHiredEmployees;
        private readonly nint _employeeManagerGetSorterCapacity;
        private readonly nint _employeeManagerGetHiredSorterCount;
        private readonly nint _employeeManagerGetMinerCapacity;
        private readonly nint _employeeManagerGetHiredMinerCount;
        private readonly nint _employeeManagerSpawnSorter;
        private readonly nint _employeeManagerFindFreeMinerSlot;
        private readonly nint _employeeManagerDespawnEmployee;
        private readonly nint _employeeManagerFire;
        private readonly nint _employeeManagerRemoveFired;
        private readonly nint _employeeManagerRpcHired;
        private readonly nint _employeeManagerOnEmployeeHired;
        private readonly nint _minerSlotGetVehicle;
        private readonly nint _minerVehicleServerOccupy;
        private readonly nint _minerVehicleServerVacate;

        private VanillaEmployeeBinding(IUnsafeIl2CppApi api)
        {
            _api = api;
            Class = RequireClass(api, "Assembly-CSharp.dll", string.Empty, "T_Employee");
            _transformClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
            _gameObjectClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            _componentGetGameObject = RequireMethod(
                api,
                RequireClass(
                    api,
                    "UnityEngine.CoreModule.dll",
                    "UnityEngine",
                    "Component"),
                "get_gameObject",
                0);
            _gameObjectGetTransform = RequireMethod(
                api,
                _gameObjectClass,
                "get_transform",
                0);
            _networkServerGetActive = RequireMethod(
                api,
                RequireClass(api, "Mirror.dll", "Mirror", "NetworkServer"),
                "get_active",
                0);
            var networkBehaviourClass = RequireClass(
                api, "Mirror.dll", "Mirror", "NetworkBehaviour");
            _networkBehaviourGetIsServer = RequireMethod(
                api, networkBehaviourClass, "get_isServer", 0);
            _setSyncVarDirtyBit = RequireMethod(
                api, networkBehaviourClass, "SetSyncVarDirtyBit", 1);
            _employeeId = RequireField(api, Class, "employeeId");
            _firstName = RequireField(api, Class, "firstName");
            _lastName = RequireField(api, Class, "lastName");
            _type = RequireField(api, Class, "type");
            _workState = RequireField(api, Class, "_workState");
            _isCarrying = RequireField(api, Class, "_isCarrying");
            _isWorking = RequireField(api, Class, "_isWorking");
            _moveSpeed = RequireField(api, Class, "moveSpeed");
            _requestToggleWork = RequireMethod(api, Class, "RequestToggleWork", 0);
            _requestUnstuck = RequireMethod(api, Class, "RequestUnstuck", 0);
            _serverInitialize = RequireMethod(
                api,
                Class,
                "ServerInitialize",
                ["HiredEmployeeData", "UnityEngine.Transform", "UnityEngine.Transform"]);
            _refreshInteractableName = RequireMethod(
                api, Class, "RefreshInteractableName", 0);
            _refreshNameText = RequireMethod(api, Class, "RefreshNameText", 0);
            _homePoint = RequireField(api, Class, "homePoint");
            _dropOffPoint = RequireField(api, Class, "dropOffPoint");
            _hiredEmployeeDataClass = RequireClass(
                api, "Assembly-CSharp.dll", string.Empty, "HiredEmployeeData");
            _hiredEmployeeId = RequireField(
                api, _hiredEmployeeDataClass, "employeeId", "HiredEmployeeData");
            _hiredFirstName = RequireField(
                api, _hiredEmployeeDataClass, "firstName", "HiredEmployeeData");
            _hiredLastName = RequireField(
                api, _hiredEmployeeDataClass, "lastName", "HiredEmployeeData");
            _hiredAvatarIndex = RequireField(
                api, _hiredEmployeeDataClass, "avatarIndex", "HiredEmployeeData");
            _hiredType = RequireField(api, _hiredEmployeeDataClass, "type", "HiredEmployeeData");
            _hiredProfile = RequireField(
                api, _hiredEmployeeDataClass, "profile", "HiredEmployeeData");
            _hiredAgility = RequireField(
                api, _hiredEmployeeDataClass, "agility", "HiredEmployeeData");
            _hiredIntelligence = RequireField(
                api, _hiredEmployeeDataClass, "intelligence", "HiredEmployeeData");
            _hiredTechnique = RequireField(
                api, _hiredEmployeeDataClass, "technique", "HiredEmployeeData");
            _hiredStamina = RequireField(
                api, _hiredEmployeeDataClass, "stamina", "HiredEmployeeData");
            _hiredDailyWage = RequireField(
                api, _hiredEmployeeDataClass, "dailyWage", "HiredEmployeeData");
            _hiredDay = RequireField(
                api, _hiredEmployeeDataClass, "hiredDay", "HiredEmployeeData");
            _hiredState = RequireField(
                api, _hiredEmployeeDataClass, "state", "HiredEmployeeData");
            _hiredActiveOffsiteContractId = RequireField(
                api,
                _hiredEmployeeDataClass,
                "activeOffsiteContractId",
                "HiredEmployeeData");
            _hiredIsFired = RequireField(
                api, _hiredEmployeeDataClass, "isFired", "HiredEmployeeData");
            _employeeManagerClass = RequireClass(
                api, "Assembly-CSharp.dll", string.Empty, "EmployeeManager");
            _employeeManagerGetInstance = RequireMethod(
                api, _employeeManagerClass, "get_Instance", 0);
            _employeeManagerHiredEmployees = RequireField(
                api, _employeeManagerClass, "_hiredEmployees", "EmployeeManager");
            _employeeManagerGetSorterCapacity = RequireMethod(
                api, _employeeManagerClass, "get_SorterCapacity", 0);
            _employeeManagerGetHiredSorterCount = RequireMethod(
                api, _employeeManagerClass, "get_HiredSorterCount", 0);
            _employeeManagerGetMinerCapacity = RequireMethod(
                api, _employeeManagerClass, "get_MinerCapacity", 0);
            _employeeManagerGetHiredMinerCount = RequireMethod(
                api, _employeeManagerClass, "get_HiredMinerCount", 0);
            _employeeManagerSpawnSorter = RequireMethod(
                api, _employeeManagerClass, "ServerSpawnSorterEmployee", ["HiredEmployeeData"]);
            _employeeManagerFindFreeMinerSlot = RequireMethod(
                api, _employeeManagerClass, "FindFreeMinerSlot", 0);
            _employeeManagerDespawnEmployee = RequireMethod(
                api, _employeeManagerClass, "ServerDespawnEmployee", ["System.String"]);
            _employeeManagerFire = RequireMethod(
                api, _employeeManagerClass, "ServerFire", ["System.String"]);
            _employeeManagerRemoveFired = RequireMethod(
                api, _employeeManagerClass, "ServerRemoveFiredEmployees", 0);
            _employeeManagerRpcHired = RequireMethod(
                api, _employeeManagerClass, "RpcOnEmployeeHired", ["HiredEmployeeData"]);
            _employeeManagerOnEmployeeHired = RequireField(
                api, _employeeManagerClass, "onEmployeeHired", "EmployeeManager");
            var minerSlotClass = RequireClass(
                api, "Assembly-CSharp.dll", string.Empty, "MinerSlot");
            _minerSlotGetVehicle = RequireMethod(api, minerSlotClass, "get_Vehicle", 0);
            var minerVehicleClass = RequireClass(
                api, "Assembly-CSharp.dll", string.Empty, "T_MinerVehicle");
            _minerVehicleServerOccupy = RequireMethod(
                api, minerVehicleClass, "ServerOccupy", ["HiredEmployeeData"]);
            _minerVehicleServerVacate = RequireMethod(
                api, minerVehicleClass, "ServerVacate", 0);
        }

        internal nint Class { get; }

        internal static VanillaEmployeeBinding? TryCreate(
            IUnsafeIl2CppApi api,
            IModLogger logger)
        {
            try
            {
                return new VanillaEmployeeBinding(api);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    $"Vanilla T_Employee controller is unavailable for this build: {exception.Message}");
                return null;
            }
        }

        internal IVanillaEmployeeController Create(
            UnityObject component,
            UnityObject gameObject) =>
            new VanillaEmployeeController(this, component, gameObject);

        internal UnityObject GetGameObject(UnityObject component)
        {
            var gameObject = _api.RuntimeInvoke(_componentGetGameObject, component.Pointer, 0);
            return gameObject != 0
                ? new UnityObject(gameObject)
                : throw new InvalidOperationException("T_Employee has no GameObject.");
        }

        internal VanillaHiredEmployeeSnapshot HireServer(
            string ownerId,
            VanillaEmployeeHireDefinition definition)
        {
            ValidateHireDefinition(definition);
            var qualifiedId = $"{ownerId}:{definition.Id}";
            ValidateText(qualifiedId, nameof(definition.Id), 100);
            var manager = RequireEmployeeManagerServer();
            if (TryGetHired(manager, qualifiedId, out _, out _))
            {
                throw new InvalidOperationException(
                    $"EmployeeManager already contains employee '{qualifiedId}'.");
            }
            if (!definition.BypassCapacity)
            {
                var capacity = ReadManagerInt32(
                    manager,
                    definition.Type == VanillaEmployeeType.Sorter
                        ? _employeeManagerGetSorterCapacity
                        : _employeeManagerGetMinerCapacity);
                var hired = ReadManagerInt32(
                    manager,
                    definition.Type == VanillaEmployeeType.Sorter
                        ? _employeeManagerGetHiredSorterCount
                        : _employeeManagerGetHiredMinerCount);
                if (hired >= capacity)
                {
                    throw new InvalidOperationException(
                        $"Vanilla {definition.Type} capacity is full ({hired}/{capacity}).");
                }
            }

            var boxedData = CreateHiredData(
                qualifiedId,
                definition.FirstName,
                definition.LastName,
                definition.Type,
                definition.Profile,
                definition.AvatarIndex,
                definition.Agility,
                definition.Intelligence,
                definition.Technique,
                definition.Stamina,
                definition.DailyWage,
                definition.HiredDay);
            var valueData = _api.Unbox(boxedData);
            if (valueData == 0)
            {
                throw new InvalidOperationException("HiredEmployeeData could not be unboxed.");
            }
            var hiredEmployees = RequireHiredEmployees(manager);
            var add = RequireMethod(_api, _api.GetObjectClass(hiredEmployees), "Add", 1);
            nint occupiedVehicle = 0;
            var added = false;
            try
            {
                InvokeValueType(add, hiredEmployees, valueData);
                added = true;
                if (definition.Type == VanillaEmployeeType.Sorter)
                {
                    InvokeValueType(_employeeManagerSpawnSorter, manager, valueData);
                }
                else
                {
                    var slot = _api.RuntimeInvoke(_employeeManagerFindFreeMinerSlot, manager, 0);
                    if (slot == 0)
                    {
                        throw new InvalidOperationException("EmployeeManager has no free miner slot.");
                    }
                    occupiedVehicle = _api.RuntimeInvoke(_minerSlotGetVehicle, slot, 0);
                    if (occupiedVehicle == 0 ||
                        !InvokeValueTypeBoolean(
                            _minerVehicleServerOccupy,
                            occupiedVehicle,
                            valueData))
                    {
                        throw new InvalidOperationException("The selected miner slot rejected the employee.");
                    }
                }
                var hiredEvent = _api.ReadObjectReference(
                    manager,
                    _employeeManagerOnEmployeeHired);
                if (hiredEvent != 0)
                {
                    var invokeEvent = RequireMethod(
                        _api,
                        _api.GetObjectClass(hiredEvent),
                        "Invoke",
                        1);
                    InvokeValueType(invokeEvent, hiredEvent, valueData);
                }
                InvokeValueType(_employeeManagerRpcHired, manager, valueData);
            }
            catch
            {
                if (occupiedVehicle != 0)
                {
                    try { _ = _api.RuntimeInvoke(_minerVehicleServerVacate, occupiedVehicle, 0); }
                    catch { }
                }
                if (definition.Type == VanillaEmployeeType.Sorter)
                {
                    try { InvokeString(_employeeManagerDespawnEmployee, manager, qualifiedId); }
                    catch { }
                }
                if (added)
                {
                    TryRemoveHired(manager, qualifiedId);
                }
                throw;
            }

            if (!TryGetHired(manager, qualifiedId, out var result, out _))
            {
                throw new InvalidOperationException(
                    "EmployeeManager did not retain the newly hired employee.");
            }
            return result;
        }

        internal bool TryGetHired(
            string qualifiedId,
            out VanillaHiredEmployeeSnapshot employee)
        {
            employee = default!;
            var manager = _api.RuntimeInvoke(_employeeManagerGetInstance, 0, 0);
            return manager != 0 && TryGetHired(manager, qualifiedId, out employee, out _);
        }

        internal void FireServer(string qualifiedId)
        {
            var manager = RequireEmployeeManagerServer();
            if (!TryGetHired(manager, qualifiedId, out _, out _))
            {
                throw new KeyNotFoundException(
                    $"EmployeeManager has no employee '{qualifiedId}'.");
            }
            InvokeString(_employeeManagerFire, manager, qualifiedId);
            _ = _api.RuntimeInvoke(_employeeManagerRemoveFired, manager, 0);
            if (TryGetHired(manager, qualifiedId, out var remaining, out _) &&
                !remaining.IsFired)
            {
                throw new InvalidOperationException(
                    $"EmployeeManager refused to fire '{qualifiedId}'.");
            }
        }

        private nint RequireEmployeeManagerServer()
        {
            if (!IsServerActive)
            {
                throw new InvalidOperationException(
                    "EmployeeManager mutations require an active Mirror server.");
            }
            var manager = _api.RuntimeInvoke(_employeeManagerGetInstance, 0, 0);
            if (manager == 0)
            {
                throw new InvalidOperationException("EmployeeManager.Instance is not ready.");
            }
            if (!ReadBooleanMethod(manager, _networkBehaviourGetIsServer))
            {
                throw new InvalidOperationException("EmployeeManager is not a server component.");
            }
            return manager;
        }

        private nint RequireHiredEmployees(nint manager)
        {
            var list = _api.ReadObjectReference(manager, _employeeManagerHiredEmployees);
            return list != 0
                ? list
                : throw new InvalidOperationException("EmployeeManager._hiredEmployees is null.");
        }

        private bool TryGetHired(
            nint manager,
            string qualifiedId,
            out VanillaHiredEmployeeSnapshot employee,
            out int index)
        {
            var list = RequireHiredEmployees(manager);
            var listClass = _api.GetObjectClass(list);
            var getCount = RequireMethod(_api, listClass, "get_Count", 0);
            var getItem = RequireMethod(_api, listClass, "get_Item", 1);
            var count = ReadManagerInt32(list, getCount);
            unsafe
            {
                nint* arguments = stackalloc nint[1];
                for (index = 0; index < count; index++)
                {
                    var itemIndex = index;
                    arguments[0] = (nint)(&itemIndex);
                    var boxed = _api.RuntimeInvoke(getItem, list, (nint)arguments);
                    if (boxed != 0)
                    {
                        var candidate = ReadHiredSnapshot(boxed);
                        if (string.Equals(
                                candidate.QualifiedId,
                                qualifiedId,
                                StringComparison.Ordinal))
                        {
                            employee = candidate;
                            return true;
                        }
                    }
                }
            }
            employee = default!;
            index = -1;
            return false;
        }

        private void TryRemoveHired(nint manager, string qualifiedId)
        {
            if (!TryGetHired(manager, qualifiedId, out _, out var index)) return;
            var list = RequireHiredEmployees(manager);
            var removeAt = RequireMethod(_api, _api.GetObjectClass(list), "RemoveAt", 1);
            unsafe
            {
                nint* arguments = stackalloc nint[1];
                arguments[0] = (nint)(&index);
                _ = _api.RuntimeInvoke(removeAt, list, (nint)arguments);
            }
        }

        private int ReadManagerInt32(nint instance, nint method)
        {
            var boxed = _api.RuntimeInvoke(method, instance, 0);
            var value = _api.Unbox(boxed);
            return value == 0
                ? throw new InvalidDataException("Vanilla manager returned an empty integer.")
                : Marshal.ReadInt32(value);
        }

        private unsafe void InvokeValueType(nint method, nint instance, nint valueData)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = valueData;
            _ = _api.RuntimeInvoke(method, instance, (nint)arguments);
        }

        private unsafe bool InvokeValueTypeBoolean(nint method, nint instance, nint valueData)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = valueData;
            var boxed = _api.RuntimeInvoke(method, instance, (nint)arguments);
            var value = _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private unsafe void InvokeString(nint method, nint instance, string value)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = _api.NewString(value);
            _ = _api.RuntimeInvoke(method, instance, (nint)arguments);
        }

        private bool IsServerActive
        {
            get
            {
                var boxed = _api.RuntimeInvoke(_networkServerGetActive, 0, 0);
                var value = _api.Unbox(boxed);
                return value != 0 && Marshal.ReadByte(value) != 0;
            }
        }

        private VanillaEmployeeSnapshot ReadSnapshot(nint component) => new(
            ReadString(component, _employeeId),
            ReadString(component, _firstName),
            ReadString(component, _lastName),
            (VanillaEmployeeType)_api.ReadInt32(component, _type),
            (VanillaEmployeeWorkState)_api.ReadInt32(component, _workState),
            _api.ReadBoolean(component, _isCarrying),
            _api.ReadBoolean(component, _isWorking));

        private string ReadString(nint component, nint field)
        {
            var value = _api.ReadObjectReference(component, field);
            return value == 0 ? string.Empty : _api.ReadString(value);
        }

        private bool ReadBooleanMethod(nint component, nint method)
        {
            var boxed = _api.RuntimeInvoke(method, component, 0);
            var value = _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private unsafe void InitializeServer(
            nint component,
            VanillaEmployeeInitialization initialization)
        {
            ValidateInitialization(initialization);
            var serverActive = IsServerActive;
            var componentIsServer = ReadBooleanMethod(component, _networkBehaviourGetIsServer);
            var serverInitializePointer = _api.GetMethodPointer(_serverInitialize);
            if (!serverActive || !componentIsServer)
            {
                throw new InvalidOperationException(
                    "Vanilla employees may only be initialized on an active server component.");
            }
            if (ReadSnapshot(component).IsInitialized)
            {
                throw new InvalidOperationException(
                    "This vanilla employee is already initialized and cannot be rebound.");
            }

            var home = ResolveTransform(initialization.HomePoint, nameof(initialization.HomePoint));
            var dropOff = ResolveTransform(
                initialization.DropOffPoint,
                nameof(initialization.DropOffPoint));
            var boxedData = CreateHiredData(
                initialization.EmployeeId,
                initialization.FirstName,
                initialization.LastName,
                initialization.Type,
                initialization.Profile,
                initialization.AvatarIndex,
                initialization.Agility,
                initialization.Intelligence,
                initialization.Technique,
                initialization.Stamina,
                initialization.DailyWage,
                initialization.HiredDay);
            var valueData = _api.Unbox(boxedData);
            if (valueData == 0)
            {
                throw new InvalidOperationException("HiredEmployeeData could not be unboxed.");
            }

            var readBackId = GetString(boxedData, _hiredEmployeeId);
            var readBackFirstName = GetString(boxedData, _hiredFirstName);
            var readBackType = GetInt32(boxedData, _hiredType);
            if (!string.Equals(readBackId, initialization.EmployeeId, StringComparison.Ordinal) ||
                !string.Equals(readBackFirstName, initialization.FirstName, StringComparison.Ordinal) ||
                readBackType != (int)initialization.Type)
            {
                throw new InvalidDataException(
                    "HiredEmployeeData failed its native field round-trip before initialization.");
            }
            if (serverInitializePointer == 0)
            {
                throw new MissingMethodException(
                    "T_Employee.ServerInitialize has no native method pointer.");
            }
            // MSVC x64 passes this non-trivial value type by address. The last
            // argument is IL2CPP's hidden MethodInfo pointer. runtime_invoke's
            // value-type marshaling is avoided here because this structure
            // contains managed references and its exported ABI is known.
            var serverInitialize =
                (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)
                serverInitializePointer;
            serverInitialize(component, valueData, home, dropOff, _serverInitialize);

            var snapshot = ReadSnapshot(component);
            if (!snapshot.IsInitialized)
            {
                RuntimeLog.Write(
                    "T_Employee.ServerInitialize produced no identity; applying the reversed " +
                    "SyncVar initialization path.");
                ApplyReversedInitialization(component, initialization, home, dropOff);
                snapshot = ReadSnapshot(component);
            }
            if (!snapshot.IsInitialized || !string.Equals(
                    snapshot.EmployeeId,
                    initialization.EmployeeId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "T_Employee.ServerInitialize returned without applying HiredEmployeeData.");
            }
        }

        private unsafe void ApplyReversedInitialization(
            nint component,
            VanillaEmployeeInitialization initialization,
            nint home,
            nint dropOff)
        {
            var employeeId = _api.NewString(initialization.EmployeeId);
            var firstName = _api.NewString(initialization.FirstName);
            var lastName = _api.NewString(initialization.LastName);
            _api.WriteObjectReference(component, _employeeId, employeeId);
            _api.WriteObjectReference(component, _firstName, firstName);
            _api.WriteObjectReference(component, _lastName, lastName);
            _api.WriteInt32(component, _type, (int)initialization.Type);
            _api.WriteObjectReference(component, _homePoint, home);
            _api.WriteObjectReference(component, _dropOffPoint, dropOff);

            ulong dirtyBits = 0x0F;
            nint* dirtyArguments = stackalloc nint[1];
            dirtyArguments[0] = (nint)(&dirtyBits);
            _ = _api.RuntimeInvoke(_setSyncVarDirtyBit, component, (nint)dirtyArguments);
            _ = _api.RuntimeInvoke(_refreshInteractableName, component, 0);
            _ = _api.RuntimeInvoke(_refreshNameText, component, 0);
            RuntimeLog.Write(
                "Reversed T_Employee initialization applied: syncVars=0x0F, " +
                $"home=0x{home:X}, dropOff=0x{dropOff:X}.");
        }

        private nint ResolveTransform(UnityObject value, string parameter)
        {
            if (value.IsNull)
            {
                throw new ArgumentException("Employee point is null.", parameter);
            }
            var klass = _api.GetObjectClass(value.Pointer);
            if (_api.IsAssignableFrom(_transformClass, klass))
            {
                return value.Pointer;
            }
            if (_api.IsAssignableFrom(_gameObjectClass, klass))
            {
                var transform = _api.RuntimeInvoke(_gameObjectGetTransform, value.Pointer, 0);
                return transform != 0
                    ? transform
                    : throw new InvalidOperationException("Employee point GameObject has no Transform.");
            }
            throw new ArgumentException(
                "Employee point must be a UnityEngine.GameObject or UnityEngine.Transform.",
                parameter);
        }

        private unsafe void SetString(nint boxed, nint field, string value)
        {
            var native = _api.NewString(value);
            // Reference fields take the Il2CppObject* directly; value fields
            // below take an address to caller-owned native storage.
            _api.SetFieldValue(boxed, field, native);
        }

        private unsafe void SetInt32(nint boxed, nint field, int value) =>
            _api.SetFieldValue(boxed, field, (nint)(&value));

        private unsafe void SetBoolean(nint boxed, nint field, bool value)
        {
            byte native = value ? (byte)1 : (byte)0;
            _api.SetFieldValue(boxed, field, (nint)(&native));
        }

        private unsafe string GetString(nint boxed, nint field)
        {
            nint native = 0;
            _api.GetFieldValue(boxed, field, (nint)(&native));
            return native == 0 ? string.Empty : _api.ReadString(native);
        }

        private unsafe int GetInt32(nint boxed, nint field)
        {
            var native = 0;
            _api.GetFieldValue(boxed, field, (nint)(&native));
            return native;
        }

        private nint CreateHiredData(
            string employeeId,
            string firstName,
            string lastName,
            VanillaEmployeeType type,
            VanillaEmployeeProfile profile,
            int avatarIndex,
            int agility,
            int intelligence,
            int technique,
            int stamina,
            int dailyWage,
            int hiredDay)
        {
            var boxed = _api.NewObject(_hiredEmployeeDataClass);
            SetString(boxed, _hiredEmployeeId, employeeId);
            SetString(boxed, _hiredFirstName, firstName);
            SetString(boxed, _hiredLastName, lastName);
            SetInt32(boxed, _hiredAvatarIndex, avatarIndex);
            SetInt32(boxed, _hiredType, (int)type);
            SetInt32(boxed, _hiredProfile, (int)profile);
            SetInt32(boxed, _hiredAgility, agility);
            SetInt32(boxed, _hiredIntelligence, intelligence);
            SetInt32(boxed, _hiredTechnique, technique);
            SetInt32(boxed, _hiredStamina, stamina);
            SetInt32(boxed, _hiredDailyWage, dailyWage);
            SetInt32(boxed, _hiredDay, hiredDay);
            SetInt32(boxed, _hiredState, 0); // HiredEmployeeState.Idle
            SetString(boxed, _hiredActiveOffsiteContractId, string.Empty);
            SetBoolean(boxed, _hiredIsFired, false);
            return boxed;
        }

        private VanillaHiredEmployeeSnapshot ReadHiredSnapshot(nint boxed) => new(
            GetString(boxed, _hiredEmployeeId),
            GetString(boxed, _hiredFirstName),
            GetString(boxed, _hiredLastName),
            (VanillaEmployeeType)GetInt32(boxed, _hiredType),
            (VanillaEmployeeProfile)GetInt32(boxed, _hiredProfile),
            GetInt32(boxed, _hiredAvatarIndex),
            GetInt32(boxed, _hiredAgility),
            GetInt32(boxed, _hiredIntelligence),
            GetInt32(boxed, _hiredTechnique),
            GetInt32(boxed, _hiredStamina),
            GetInt32(boxed, _hiredDailyWage),
            GetInt32(boxed, _hiredDay),
            GetBoolean(boxed, _hiredIsFired),
            GetString(boxed, _hiredActiveOffsiteContractId));

        private unsafe bool GetBoolean(nint boxed, nint field)
        {
            byte native = 0;
            _api.GetFieldValue(boxed, field, (nint)(&native));
            return native != 0;
        }

        private static void ValidateHireDefinition(VanillaEmployeeHireDefinition value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateText(value.FirstName, nameof(value.FirstName), 100);
            ValidateText(value.LastName, nameof(value.LastName), 100);
            if (!Enum.IsDefined(value.Type))
                throw new ArgumentOutOfRangeException(nameof(value.Type));
            if (!Enum.IsDefined(value.Profile))
                throw new ArgumentOutOfRangeException(nameof(value.Profile));
            ValidateStat(value.Agility, nameof(value.Agility));
            ValidateStat(value.Intelligence, nameof(value.Intelligence));
            ValidateStat(value.Technique, nameof(value.Technique));
            ValidateStat(value.Stamina, nameof(value.Stamina));
            if (value.AvatarIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(value.AvatarIndex));
            if (value.DailyWage < 0)
                throw new ArgumentOutOfRangeException(nameof(value.DailyWage));
            if (value.HiredDay < 0)
                throw new ArgumentOutOfRangeException(nameof(value.HiredDay));
        }

        private static void ValidateInitialization(VanillaEmployeeInitialization value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateText(value.EmployeeId, nameof(value.EmployeeId), 100);
            ValidateText(value.FirstName, nameof(value.FirstName), 100);
            ValidateText(value.LastName, nameof(value.LastName), 100);
            if (!Enum.IsDefined(value.Type))
                throw new ArgumentOutOfRangeException(nameof(value.Type));
            if (!Enum.IsDefined(value.Profile))
                throw new ArgumentOutOfRangeException(nameof(value.Profile));
            if (value.AvatarIndex < 0) throw new ArgumentOutOfRangeException(nameof(value.AvatarIndex));
            ValidateStat(value.Agility, nameof(value.Agility));
            ValidateStat(value.Intelligence, nameof(value.Intelligence));
            ValidateStat(value.Technique, nameof(value.Technique));
            ValidateStat(value.Stamina, nameof(value.Stamina));
            if (value.DailyWage < 0) throw new ArgumentOutOfRangeException(nameof(value.DailyWage));
            if (value.HiredDay < 0) throw new ArgumentOutOfRangeException(nameof(value.HiredDay));
            if (value.HomePoint.IsNull) throw new ArgumentException("Home point is null.", nameof(value.HomePoint));
            if (value.DropOffPoint.IsNull)
                throw new ArgumentException("Drop-off point is null.", nameof(value.DropOffPoint));
        }

        private static void ValidateText(string value, string parameter, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
                throw new ArgumentException(
                    $"A non-empty value of at most {maximumLength} characters is required.",
                    parameter);
        }

        private static void ValidateStat(int value, string parameter)
        {
            if (value is < 0 or > 5)
                throw new ArgumentOutOfRangeException(parameter, "Employee stats must be between 0 and 5.");
        }

        private sealed class VanillaEmployeeController(
            VanillaEmployeeBinding binding,
            UnityObject component,
            UnityObject gameObject) : IVanillaEmployeeController
        {
            public UnityObject Component { get; } = component;
            public UnityObject GameObject { get; } = gameObject;
            public bool IsServerActive
            {
                get
                {
                    EnsureMainThread();
                    return binding.IsServerActive;
                }
            }
            public VanillaEmployeeSnapshot Snapshot
            {
                get
                {
                    EnsureMainThread();
                    return binding.ReadSnapshot(Component.Pointer);
                }
            }
            public float MoveSpeed
            {
                get
                {
                    EnsureMainThread();
                    return binding._api.ReadSingle(Component.Pointer, binding._moveSpeed);
                }
                set
                {
                    EnsureMainThread();
                    if (!float.IsFinite(value) || value < 0f)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(value),
                            "Vanilla employee move speed must be finite and non-negative.");
                    }
                    if (!binding.IsServerActive)
                    {
                        throw new InvalidOperationException(
                            "Vanilla employee move speed may only be changed by the active Mirror server.");
                    }
                    binding._api.WriteSingle(Component.Pointer, binding._moveSpeed, value);
                }
            }

            public void InitializeServer(VanillaEmployeeInitialization initialization)
            {
                EnsureMainThread();
                binding.InitializeServer(Component.Pointer, initialization);
            }

            public void RequestToggleWork() => Invoke(binding._requestToggleWork);
            public void RequestUnstuck() => Invoke(binding._requestUnstuck);

            private void Invoke(nint method)
            {
                EnsureMainThread();
                _ = binding._api.RuntimeInvoke(method, Component.Pointer, 0);
            }
        }

        private static nint RequireClass(
            IUnsafeIl2CppApi api,
            string assembly,
            string namespaze,
            string name)
        {
            var klass = api.FindClass(assembly, namespaze, name);
            return klass != 0
                ? klass
                : throw new TypeLoadException($"{assembly}:{namespaze}.{name}");
        }

        private static nint RequireField(
            IUnsafeIl2CppApi api,
            nint klass,
            string name,
            string typeName = "T_Employee")
        {
            var field = api.FindField(klass, name);
            return field != 0
                ? field
                : throw new MissingFieldException($"{typeName}.{name}");
        }

        private static nint RequireMethod(
            IUnsafeIl2CppApi api,
            nint klass,
            string name,
            int argumentCount)
        {
            var method = api.FindMethod(klass, name, argumentCount);
            return method != 0
                ? method
                : throw new MissingMethodException($"T_Employee.{name}/{argumentCount}");
        }

        private static nint RequireMethod(
            IUnsafeIl2CppApi api,
            nint klass,
            string name,
            IReadOnlyList<string> parameterTypes)
        {
            var method = api.FindMethodBySignature(klass, name, parameterTypes);
            return method != 0
                ? method
                : throw new MissingMethodException(
                    $"T_Employee.{name}({string.Join(", ", parameterTypes)})");
        }
    }

    private sealed class Npc(
        NpcApi api,
        string npcOwnerId,
        UnityObject gameObject,
        UnityObject animator,
        UnityObject navigator,
        Func<bool> isSpawned,
        Action despawn,
        IUnityApi unity,
        IUnsafeIl2CppApi unsafeApi,
        nint animatorClass,
        nint navigatorClass) : INpc
    {
        public string OwnerId { get; } = npcOwnerId;
        public UnityObject GameObject { get; } = gameObject;
        public UnityObject Animator { get; } = animator;
        public INpcNavigation? Navigation { get; } = navigator.IsNull
            ? null
            : new NpcNavigation(navigator, unsafeApi, navigatorClass, isSpawned);
        public bool IsSpawned => isSpawned();

        public UnityTransform Transform
        {
            get => unity.GetTransform(GameObject);
            set => unity.SetTransform(GameObject, value);
        }

        public void SetIdleAnimation(int index) => InvokeWithInt32("SetIdleIndex", index);
        public void PlayAction(int index) => InvokeWithInt32("PlayAction", index);
        public void StopAction() => Invoke("StopAction", 0);
        public void Despawn()
        {
            if (!IsSpawned)
            {
                return;
            }
            api.OnNpcDespawning(this);
            despawn();
        }
        public void Dispose() => Despawn();

        private unsafe void InvokeWithInt32(string methodName, int value)
        {
            EnsureAnimator();
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&value);
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(methodName, 1),
                Animator.Pointer,
                (nint)arguments);
        }

        private void Invoke(string methodName, int argumentCount)
        {
            EnsureAnimator();
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(methodName, argumentCount),
                Animator.Pointer,
                0);
        }

        private nint RequireMethod(string name, int argumentCount)
        {
            var method = unsafeApi.FindMethod(animatorClass, name, argumentCount);
            return method != 0
                ? method
                : throw new MissingMethodException($"NPCAnimator.{name}/{argumentCount} was not found.");
        }

        private void EnsureAnimator()
        {
            if (!ModRuntime.IsMainThread)
            {
                throw new InvalidOperationException("NPC API calls must run on Unity's main thread.");
            }
            if (Animator.IsNull)
            {
                throw new InvalidOperationException(
                    "This NPC has no NPCAnimator component. Use Transform or a custom mechanic instead.");
            }
        }
    }

    private sealed class NpcNavigation(
        UnityObject component,
        IUnsafeIl2CppApi unsafeApi,
        nint navigatorClass,
        Func<bool> isSpawned) : INpcNavigation
    {
        private readonly nint _getMaxSpeed = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_maxSpeed",
            0);
        private readonly nint _setMaxSpeed = RequireMethod(
            unsafeApi,
            navigatorClass,
            "set_maxSpeed",
            1);
        private readonly nint _setDestination = RequireMethod(
            unsafeApi,
            navigatorClass,
            "set_destination",
            1);
        private readonly nint _getHasPath = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_hasPath",
            0);
        private readonly nint _getPathPending = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_pathPending",
            0);
        private readonly nint _getReachedDestination = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_reachedDestination",
            0);
        private readonly nint _getReachedEndOfPath = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_reachedEndOfPath",
            0);
        private readonly nint _getIsStopped = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_isStopped",
            0);
        private readonly nint _setIsStopped = RequireMethod(
            unsafeApi,
            navigatorClass,
            "set_isStopped",
            1);
        private readonly nint _getRemainingDistance = RequireMethod(
            unsafeApi,
            navigatorClass,
            "get_remainingDistance",
            0);
        private readonly nint _searchPath = RequireMethod(
            unsafeApi,
            navigatorClass,
            "SearchPath",
            0);
        private readonly nint _teleport = RequireMethod(
            unsafeApi,
            navigatorClass,
            "Teleport",
            2);

        public UnityObject Component { get; } = component;

        public float MaxSpeed
        {
            get
            {
                EnsureUsable();
                return ReadSingle(_getMaxSpeed);
            }
            set
            {
                EnsureUsable();
                if (!float.IsFinite(value) || value < 0f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        "NPC max speed must be finite and non-negative.");
                }
                WriteSingle(_setMaxSpeed, value);
            }
        }

        public NpcNavigationState State
        {
            get
            {
                EnsureUsable();
                return new NpcNavigationState(
                    ReadBoolean(_getHasPath),
                    ReadBoolean(_getPathPending),
                    ReadBoolean(_getReachedDestination),
                    ReadBoolean(_getReachedEndOfPath),
                    ReadBoolean(_getIsStopped),
                    ReadSingle(_getRemainingDistance));
            }
        }

        public unsafe void MoveTo(UnityVector3 destination, float? maxSpeed = null)
        {
            EnsureUsable();
            ValidateFinite(destination, nameof(destination));
            if (maxSpeed.HasValue)
            {
                MaxSpeed = maxSpeed.Value;
            }

            var native = NativeVector3.From(destination);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&native);
            _ = unsafeApi.RuntimeInvoke(_setDestination, Component.Pointer, (nint)arguments);
            WriteBoolean(_setIsStopped, false);
            _ = unsafeApi.RuntimeInvoke(_searchPath, Component.Pointer, 0);
        }

        public void Stop()
        {
            EnsureUsable();
            WriteBoolean(_setIsStopped, true);
        }

        public void Resume()
        {
            EnsureUsable();
            WriteBoolean(_setIsStopped, false);
            _ = unsafeApi.RuntimeInvoke(_searchPath, Component.Pointer, 0);
        }

        public unsafe void Teleport(UnityVector3 position, bool clearPath = true)
        {
            EnsureUsable();
            ValidateFinite(position, nameof(position));
            var native = NativeVector3.From(position);
            var clear = clearPath ? (byte)1 : (byte)0;
            nint* arguments = stackalloc nint[2];
            arguments[0] = (nint)(&native);
            arguments[1] = (nint)(&clear);
            _ = unsafeApi.RuntimeInvoke(_teleport, Component.Pointer, (nint)arguments);
        }

        private float ReadSingle(nint method)
        {
            var value = unsafeApi.Unbox(unsafeApi.RuntimeInvoke(method, Component.Pointer, 0));
            return value != 0
                ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value))
                : throw new InvalidDataException("FollowerEntity returned an empty float value.");
        }

        private bool ReadBoolean(nint method)
        {
            var value = unsafeApi.Unbox(unsafeApi.RuntimeInvoke(method, Component.Pointer, 0));
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private unsafe void WriteSingle(nint method, float value)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&value);
            _ = unsafeApi.RuntimeInvoke(method, Component.Pointer, (nint)arguments);
        }

        private unsafe void WriteBoolean(nint method, bool value)
        {
            var native = value ? (byte)1 : (byte)0;
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&native);
            _ = unsafeApi.RuntimeInvoke(method, Component.Pointer, (nint)arguments);
        }

        private void EnsureUsable()
        {
            if (!ModRuntime.IsMainThread)
            {
                throw new InvalidOperationException(
                    "NPC navigation calls must run on Unity's main thread.");
            }
            if (!isSpawned())
            {
                throw new ObjectDisposedException(nameof(INpc), "NPC has already been despawned.");
            }
        }

        private static void ValidateFinite(UnityVector3 value, string parameter)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw new ArgumentOutOfRangeException(parameter, "NPC position must be finite.");
            }
        }

        private static nint RequireMethod(
            IUnsafeIl2CppApi unsafeApi,
            nint klass,
            string name,
            int arguments)
        {
            var method = unsafeApi.FindMethod(klass, name, arguments);
            return method != 0
                ? method
                : throw new MissingMethodException($"FollowerEntity.{name}/{arguments} was not found.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeVector3
        {
            public float X;
            public float Y;
            public float Z;

            public static NativeVector3 From(UnityVector3 value) => new()
            {
                X = value.X,
                Y = value.Y,
                Z = value.Z,
            };
        }
    }
}

internal sealed class NpcBehaviorRuntime
{
    private readonly string _ownerId;
    private readonly IModEvents _events;
    private readonly IModLogger _logger;
    private readonly List<BehaviorController> _controllers = new();
    private long _sequence;
    private bool _attached = true;

    public NpcBehaviorRuntime(string ownerId, IModEvents events, IModLogger logger)
    {
        _ownerId = ownerId;
        _events = events;
        _logger = logger;
        _events.FrameUpdate += OnFrameUpdate;
    }

    public INpcBehavior Attach(INpc npc, NpcBehaviorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(definition);
        NpcApi.ValidateBehavior(definition);
        if (!_attached)
        {
            throw new ObjectDisposedException(nameof(NpcBehaviorRuntime));
        }
        if (_controllers.Any(controller =>
                controller.IsAttached &&
                ReferenceEquals(controller.Npc, npc) &&
                string.Equals(controller.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"NPC 0x{npc.GameObject.Pointer:X} already has behavior '{definition.Id}' " +
                $"owned by mod '{_ownerId}'.");
        }

        var controller = new BehaviorController(
            this,
            npc,
            definition,
            _logger,
            _sequence++);
        _controllers.Add(controller);
        _controllers.Sort(static (left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        });
        controller.InvokeStarted();
        _logger.Info(
            $"Attached NPC behavior '{definition.Id}' to 0x{npc.GameObject.Pointer:X} " +
            $"at order {definition.Order}.");
        return controller;
    }

    public void DetachAll(INpc npc)
    {
        foreach (var controller in _controllers
                     .Where(value => ReferenceEquals(value.Npc, npc))
                     .ToArray())
        {
            controller.Detach();
        }
    }

    public void RemoveAll()
    {
        foreach (var controller in _controllers.ToArray().Reverse())
        {
            controller.Detach();
        }
        if (_attached)
        {
            _events.FrameUpdate -= OnFrameUpdate;
            _attached = false;
        }
    }

    private void OnFrameUpdate(FrameEvent frame)
    {
        foreach (var controller in _controllers.ToArray())
        {
            if (!controller.Npc.IsSpawned)
            {
                controller.Detach();
                continue;
            }
            controller.InvokeUpdate(frame);
        }
    }

    private void Remove(BehaviorController controller)
    {
        if (!controller.IsAttached)
        {
            return;
        }
        _controllers.Remove(controller);
        controller.MarkDetached();
        controller.InvokeStopped();
    }

    private sealed class BehaviorController(
        NpcBehaviorRuntime runtime,
        INpc npc,
        NpcBehaviorDefinition definition,
        IModLogger behaviorLogger,
        long sequence) : INpcBehavior
    {
        public string Id { get; } = definition.Id;
        public INpc Npc { get; } = npc;
        public int Order { get; } = definition.Order;
        public bool Enabled { get; set; } = true;
        public bool IsAttached { get; private set; } = true;
        public long Sequence { get; } = sequence;

        public void Detach() => runtime.Remove(this);
        public void Dispose() => Detach();
        public void MarkDetached() => IsAttached = false;

        public void InvokeStarted()
        {
            if (definition.Started is not null)
            {
                Invoke(() => definition.Started(Npc), "Started");
            }
        }

        public void InvokeUpdate(FrameEvent frame)
        {
            if (Enabled && IsAttached && definition.Update is not null)
            {
                Invoke(() => definition.Update(Npc, frame), "Update");
            }
        }

        public void InvokeStopped()
        {
            if (definition.Stopped is null)
            {
                return;
            }
            try
            {
                definition.Stopped(Npc);
            }
            catch (Exception exception)
            {
                behaviorLogger.Error(exception, $"NPC behavior '{Id}' failed during Stopped.");
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
                behaviorLogger.Error(exception, $"NPC behavior '{Id}' failed during {phase}.");
                if (definition.DisableOnException)
                {
                    Enabled = false;
                    behaviorLogger.Warning(
                        $"NPC behavior '{Id}' was disabled after an exception.");
                }
            }
        }
    }
}
