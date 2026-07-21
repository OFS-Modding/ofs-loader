using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class MiningAreaSpawnerRegistry : IMiningAreaSpawnerRegistry
    {
        private static readonly Dictionary<nint, string> Owners = [];

        private readonly string _ownerId;
        private readonly IUnsafeIl2CppApi _api;
        private readonly IUnityApi _unity;
        private readonly Func<bool> _isServerActive;
        private readonly nint _gameObjectClass;
        private readonly nint _unityObjectClass;
        private readonly nint _behaviourClass;
        private readonly nint _spawnerClass;
        private readonly nint _ruleClass;
        private readonly nint _profileClass;
        private readonly nint _itemComponentClass;
        private readonly nint _propertyManagerInstanceField;
        private readonly nint _networkIdentityClass;
        private readonly nint _networkServerClass;
        private readonly nint _networkSpawnMethod;
        private readonly nint _networkDestroyMethod;
        private readonly nint _networkUnspawnMethod;
        private readonly nint _startCoroutineMethod;
        private readonly nint _networkServerOnlyField;
        private readonly List<MiningAreaSpawnerHandle> _transactionHandles = [];
        private bool _transactionCommitted;

        public MiningAreaSpawnerRegistry(
            string ownerId,
            IUnsafeIl2CppApi api,
            IUnityApi unity,
            Func<bool> isServerActive)
        {
            _ownerId = ownerId;
            _api = api;
            _unity = unity;
            _isServerActive = isServerActive;
            _gameObjectClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "GameObject");
            _unityObjectClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            _behaviourClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Behaviour");
            _spawnerClass = RequireClass(api, "T_ItemAreaSpawner");
            _ruleClass = RequireClass(api, "T_ItemSpawnRule");
            _profileClass = RequireClass(api, "T_ItemSpawnProfile");
            _itemComponentClass = RequireClass(api, "T_Item");
            _networkIdentityClass = api.FindClass("Mirror.dll", "Mirror", "NetworkIdentity");
            _networkServerClass = api.FindClass("Mirror.dll", "Mirror", "NetworkServer");
            if (_networkIdentityClass == 0 || _networkServerClass == 0)
                throw new TypeLoadException("Mirror NetworkIdentity/NetworkServer is unavailable.");
            _networkServerOnlyField = RequireField(api, _networkIdentityClass, "serverOnly");
            _networkSpawnMethod = api.FindMethodBySignature(
                _networkServerClass,
                "Spawn",
                ["UnityEngine.GameObject", "Mirror.NetworkConnectionToClient"]);
            _networkDestroyMethod = api.FindMethodBySignature(
                _networkServerClass,
                "Destroy",
                ["UnityEngine.GameObject"]);
            _networkUnspawnMethod = api.FindMethodBySignature(
                _networkServerClass,
                "UnSpawn",
                ["UnityEngine.GameObject"]);
            if (_networkSpawnMethod == 0 || _networkDestroyMethod == 0 ||
                _networkUnspawnMethod == 0)
                throw new MissingMethodException("Mirror NetworkServer spawn lifecycle is unavailable.");
            var monoBehaviourClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "MonoBehaviour");
            _startCoroutineMethod = api.FindMethodBySignature(
                monoBehaviourClass,
                "StartCoroutine",
                ["System.Collections.IEnumerator"]);
            if (_startCoroutineMethod == 0)
                throw new MissingMethodException(
                    "UnityEngine.MonoBehaviour.StartCoroutine(IEnumerator)");
            _propertyManagerInstanceField = RequireField(
                api,
                RequireClass(api, "ComputerPropertyManager"),
                "<Instance>k__BackingField");
        }

        public IMiningAreaSpawner Create(
            MiningAreaSpawnerBlueprint blueprint,
            UnityObject parent = default)
        {
            EnsureMainThread();
            ValidateBlueprint(blueprint);
            EnsureNoActiveSpawner();
            var root = _unity.CreateGameObject(blueprint.Name, parent);
            _unity.SetActive(root, false);
            try
            {
                _unity.SetTransform(root, blueprint.Transform);
                var handle = AttachCore(root, blueprint, ownsRoot: true, restoreActive: false);
                _unity.SetActive(root, blueprint.Active);
                return handle;
            }
            catch
            {
                _unity.Destroy(root);
                throw;
            }
        }

        public IMiningAreaSpawner Attach(
            UnityObject gameObject,
            MiningAreaSpawnerBlueprint blueprint)
        {
            EnsureMainThread();
            ValidateGameObject(gameObject);
            ValidateBlueprint(blueprint);
            EnsureNoActiveSpawner();
            var wasActive = UnityUiRuntime.IsActiveSelfForSdk(gameObject.Pointer);
            if (wasActive) _unity.SetActive(gameObject, false);
            try
            {
                return AttachCore(gameObject, blueprint, ownsRoot: false, restoreActive: wasActive);
            }
            catch
            {
                if (wasActive) _unity.SetActive(gameObject, true);
                throw;
            }
        }

        public IReadOnlyList<MiningAreaSpawnerDefinition> GetLoaded(bool activeOnly = true)
        {
            EnsureMainThread();
            return _unity.FindComponents(
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_ItemAreaSpawner",
                    activeOnly)
                .Select(DescribeComponent)
                .ToArray();
        }

        internal void BeginTransaction()
        {
            if (!_transactionCommitted)
                throw new InvalidOperationException("A mining spawner transaction is already active.");
            _transactionCommitted = false;
        }

        internal void CommitTransaction()
        {
            _transactionCommitted = true;
            _transactionHandles.Clear();
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
            for (var index = _transactionHandles.Count - 1; index >= 0; --index)
            {
                try { _transactionHandles[index].Remove(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            _transactionHandles.Clear();
            _transactionCommitted = true;
            if (failures is not null)
                throw new AggregateException("Mining area spawner rollback was incomplete.", failures);
        }

        private MiningAreaSpawnerHandle AttachCore(
            UnityObject root,
            MiningAreaSpawnerBlueprint blueprint,
            bool ownsRoot,
            bool restoreActive)
        {
            if (!_unity.TryGetComponent(
                    root,
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_ItemAreaSpawner").IsNull)
                throw new InvalidOperationException("GameObject already has a T_ItemAreaSpawner.");

            var identity = _unity.TryGetComponent(root, "Mirror.dll", "Mirror", "NetworkIdentity");
            var ownsIdentity = identity.IsNull;
            if (ownsIdentity)
            {
                identity = _unity.AddComponent(root, "Mirror.dll", "Mirror", "NetworkIdentity");
                _api.WriteBoolean(identity.Pointer, _networkServerOnlyField, true);
            }

            var builtRules = new List<OwnedRule>(blueprint.Rules.Count);
            UnityObject component = default;
            try
            {
                for (var index = 0; index < blueprint.Rules.Count; ++index)
                    builtRules.Add(CreateRule(root, blueprint.Rules[index], index));

                component = _unity.AddComponent(
                    root,
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_ItemAreaSpawner");
                WriteReference(component.Pointer, "pickupPrefab", blueprint.PickupPrefab.Pointer);
                WriteBool(component.Pointer, "randomYawRotation", blueprint.RandomYawRotation);
                WriteReference(component.Pointer, "profile", blueprint.FallbackProfile.Pointer);
                WriteFloat(component.Pointer, "referenceEdge", blueprint.ReferenceEdge);
                WriteInt(
                    component.Pointer,
                    "baseCapacityAtReference",
                    blueprint.BaseCapacityAtReference);
                WriteInt(component.Pointer, "minCapacityPerRule", blueprint.MinCapacityPerRule);
                WriteBool(component.Pointer, "capacityByArea", blueprint.CapacityByArea);
                WriteBool(
                    component.Pointer,
                    "equalDistributionAcrossRules",
                    blueprint.EqualDistributionAcrossRules);
                ReplaceRuleList(component.Pointer, "surface", builtRules, MiningLayerKind.Surface);
                ReplaceRuleList(component.Pointer, "mid", builtRules, MiningLayerKind.Middle);
                ReplaceRuleList(component.Pointer, "deep", builtRules, MiningLayerKind.Deep);

                Owners.Add(component.Pointer, _ownerId);
                var handle = new MiningAreaSpawnerHandle(
                    this,
                    root,
                    component,
                    identity,
                    ownsRoot,
                    ownsIdentity,
                    builtRules,
                    blueprint.ProfileSelection);
                if (!_transactionCommitted) _transactionHandles.Add(handle);
                if (restoreActive) _unity.SetActive(root, true);
                RuntimeLog.Write(
                    $"Mining area spawner attached: owner={_ownerId}, " +
                    $"object={blueprint.Name}, rules={builtRules.Count}.");
                return handle;
            }
            catch
            {
                if (!component.IsNull) _unity.Destroy(component);
                for (var index = builtRules.Count - 1; index >= 0; --index)
                    DestroyRule(builtRules[index]);
                if (ownsIdentity && !identity.IsNull) _unity.Destroy(identity);
                throw;
            }
        }

        private OwnedRule CreateRule(
            UnityObject root,
            MiningSpawnRuleBlueprint blueprint,
            int id)
        {
            var ownsHost = blueprint.HostGameObject.IsNull;
            var host = ownsHost
                ? _unity.CreateGameObject(blueprint.Name, root)
                : blueprint.HostGameObject;
            ValidateGameObject(host);
            if (!_unity.TryGetComponent(
                    host,
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_ItemSpawnRule").IsNull)
                throw new InvalidOperationException(
                    $"Rule host '{blueprint.Name}' already has a T_ItemSpawnRule.");
            var wasActive = UnityUiRuntime.IsActiveSelfForSdk(host.Pointer);
            if (wasActive) _unity.SetActive(host, false);
            UnityObject component = default;
            try
            {
                if (ownsHost) _unity.SetTransform(host, blueprint.Transform);
                component = _unity.AddComponent(
                    host,
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_ItemSpawnRule");
                WriteRuleInt(component.Pointer, "spawnRuleID", id);
                WriteRuleFloat(component.Pointer, "size", blueprint.Size);
                WriteRuleFloat(component.Pointer, "height", blueprint.Height);
                WriteRuleFloat(component.Pointer, "yOffset", blueprint.YOffset);
                WriteRuleBool(component.Pointer, "drawGizmos", false);
                WriteRuleFloat(component.Pointer, "fillAlpha", 0f);
                if (wasActive) _unity.SetActive(host, true);
                return new OwnedRule(
                    host,
                    component,
                    blueprint.Layer,
                    ownsHost,
                    id);
            }
            catch
            {
                if (ownsHost) _unity.Destroy(host);
                else
                {
                    if (!component.IsNull) _unity.Destroy(component);
                    if (wasActive) _unity.SetActive(host, true);
                }
                throw;
            }
        }

        private MiningAreaSpawnerDefinition DescribeComponent(UnityObject component)
        {
            ValidateSpawner(component);
            var rules = new List<MiningSpawnRuleDefinition>();
            ReadRuleList(component.Pointer, "surface", MiningLayerKind.Surface, rules);
            ReadRuleList(component.Pointer, "mid", MiningLayerKind.Middle, rules);
            ReadRuleList(component.Pointer, "deep", MiningLayerKind.Deep, rules);
            return new MiningAreaSpawnerDefinition(
                GetGameObject(component),
                component,
                new UnityObject(ReadReference(component.Pointer, "pickupPrefab")),
                new UnityObject(ReadReference(component.Pointer, "profile")),
                ReadBool(component.Pointer, "randomYawRotation"),
                ReadFloat(component.Pointer, "referenceEdge"),
                ReadInt(component.Pointer, "baseCapacityAtReference"),
                ReadInt(component.Pointer, "minCapacityPerRule"),
                ReadBool(component.Pointer, "capacityByArea"),
                ReadBool(component.Pointer, "equalDistributionAcrossRules"),
                rules,
                ReadBool(component.Pointer, "_isRestoringFromSave"),
                ReadBool(component.Pointer, "_initialCountsCalculated"));
        }

        private void ReadRuleList(
            nint spawner,
            string field,
            MiningLayerKind layer,
            List<MiningSpawnRuleDefinition> result)
        {
            var list = ReadReference(spawner, field);
            if (list == 0) return;
            for (var index = 0; index < GetCount(list); ++index)
            {
                var component = GetAt(list, index);
                if (component == 0) continue;
                result.Add(new MiningSpawnRuleDefinition(
                    GetGameObject(new UnityObject(component)),
                    new UnityObject(component),
                    ReadRuleInt(component, "spawnRuleID"),
                    layer,
                    ReadRuleFloat(component, "size"),
                    ReadRuleFloat(component, "height"),
                    ReadRuleFloat(component, "yOffset")));
            }
        }

        private void ReplaceRuleList(
            nint spawner,
            string field,
            IReadOnlyList<OwnedRule> rules,
            MiningLayerKind layer)
        {
            var target = RequireField(_api, _spawnerClass, field);
            var list = NewConstructedObject(_api.GetFieldTypeClass(target));
            _api.WriteObjectReference(spawner, target, list);
            foreach (var rule in rules.Where(value => value.Layer == layer))
                Add(list, rule.Component.Pointer);
        }

        private void ValidateBlueprint(MiningAreaSpawnerBlueprint blueprint)
        {
            ArgumentNullException.ThrowIfNull(blueprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(blueprint.Name);
            if (blueprint.Name.Length > 200)
                throw new ArgumentException("Spawner name is longer than 200 characters.");
            ValidateReference(blueprint.PickupPrefab, _itemComponentClass, "pickup prefab");
            ValidateReference(blueprint.FallbackProfile, _profileClass, "fallback profile");
            if (!float.IsFinite(blueprint.ReferenceEdge) || blueprint.ReferenceEdge < 0.1f ||
                blueprint.ReferenceEdge > 10000f)
                throw new ArgumentOutOfRangeException(nameof(blueprint.ReferenceEdge));
            if (blueprint.BaseCapacityAtReference < 0 ||
                blueprint.BaseCapacityAtReference > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(blueprint.BaseCapacityAtReference));
            if (blueprint.MinCapacityPerRule < 0 || blueprint.MinCapacityPerRule > 1_000_000)
                throw new ArgumentOutOfRangeException(nameof(blueprint.MinCapacityPerRule));
            if (blueprint.Rules.Count is < 1 or > 1000)
                throw new ArgumentException("Spawner must define 1..1000 rules.");
            if (!Enum.IsDefined(blueprint.ProfileSelection))
                throw new ArgumentOutOfRangeException(nameof(blueprint.ProfileSelection));
            if (blueprint.Rules.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() !=
                blueprint.Rules.Count)
                throw new ArgumentException("Spawner rule names must be unique.");
            foreach (var rule in blueprint.Rules)
            {
                ArgumentNullException.ThrowIfNull(rule);
                ArgumentException.ThrowIfNullOrWhiteSpace(rule.Name);
                if (!Enum.IsDefined(rule.Layer))
                    throw new ArgumentOutOfRangeException(nameof(rule.Layer));
                if (!float.IsFinite(rule.Size) || rule.Size < 0.01f || rule.Size > 10000f)
                    throw new ArgumentOutOfRangeException(nameof(rule.Size));
                if (!float.IsFinite(rule.Height) || rule.Height < 0.01f || rule.Height > 10000f)
                    throw new ArgumentOutOfRangeException(nameof(rule.Height));
                if (!float.IsFinite(rule.YOffset) || Math.Abs(rule.YOffset) > 100000f)
                    throw new ArgumentOutOfRangeException(nameof(rule.YOffset));
                if (!rule.HostGameObject.IsNull) ValidateGameObject(rule.HostGameObject);
            }
        }

        private void EnsureNoActiveSpawner()
        {
            var loaded = _unity.FindComponents(
                "Assembly-CSharp.dll",
                string.Empty,
                "T_ItemAreaSpawner",
                activeOnly: true);
            if (loaded.Count != 0)
                throw new InvalidOperationException(
                    "An active T_ItemAreaSpawner already exists in this scene. " +
                    "The vanilla component is a singleton and cannot be replaced safely.");
        }

        private void ValidateGameObject(UnityObject value)
        {
            if (value.IsNull ||
                !_api.IsAssignableFrom(_gameObjectClass, _api.GetObjectClass(value.Pointer)))
                throw new ArgumentException("Object is not a UnityEngine.GameObject.");
        }

        private void ValidateReference(UnityObject value, nint klass, string label)
        {
            if (value.IsNull || !_api.IsAssignableFrom(klass, _api.GetObjectClass(value.Pointer)))
                throw new ArgumentException($"Spawner {label} has the wrong Unity type.");
        }

        private void ValidateSpawner(UnityObject value)
        {
            if (value.IsNull ||
                !_api.IsAssignableFrom(_spawnerClass, _api.GetObjectClass(value.Pointer)))
                throw new ArgumentException("Object is not a T_ItemAreaSpawner.");
        }

        private UnityObject GetGameObject(UnityObject component)
        {
            var componentClass = _api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Component");
            return new UnityObject(_api.RuntimeInvoke(
                RequireMethod(_api, componentClass, "get_gameObject", 0),
                component.Pointer,
                0));
        }

        private int ComputeCapacity(UnityObject spawner, UnityObject rule)
        {
            unsafe
            {
                nint* arguments = stackalloc nint[1];
                arguments[0] = rule.Pointer;
                var boxed = _api.RuntimeInvoke(
                    RequireMethod(_api, _spawnerClass, "ComputeCapacityForRule", 1),
                    spawner.Pointer,
                    (nint)arguments);
                var value = _api.Unbox(boxed);
                return value != 0
                    ? Marshal.ReadInt32(value)
                    : throw new InvalidDataException("ComputeCapacityForRule returned null.");
            }
        }

        private int GetRemainingNodeCount(
            UnityObject spawner,
            MiningLayerKind layer,
            string itemId)
        {
            if (!Enum.IsDefined(layer))
                throw new ArgumentOutOfRangeException(nameof(layer));
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            var boxed = _api.Invoke(
                RequireMethod(_api, _spawnerClass, "GetRemainingNodeCount", 2),
                spawner.Pointer,
                Il2CppArgument.FromInt32((int)layer),
                Il2CppArgument.FromReference(_api.NewString(itemId)));
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("GetRemainingNodeCount returned null.");
        }

        private nint ResolveProfile(
            UnityObject spawner,
            MiningProfileSelectionMode selection) => selection ==
                MiningProfileSelectionMode.FallbackOnly
            ? WithoutActivePropertyManager(() => _api.RuntimeInvoke(
                RequireMethod(_api, _spawnerClass, "ResolveActiveProfile", 0),
                spawner.Pointer,
                0))
            : _api.RuntimeInvoke(
                RequireMethod(_api, _spawnerClass, "ResolveActiveProfile", 0),
                spawner.Pointer,
                0);

        private void Spawn(
            UnityObject spawner,
            MiningProfileSelectionMode selection)
        {
            if (!_isServerActive())
                throw new InvalidOperationException(
                    "Mining nodes may only be spawned by the active Mirror server/host.");
            if (ReadBool(spawner.Pointer, "_isRestoringFromSave"))
                throw new InvalidOperationException(
                    "Mining nodes cannot be spawned while the vanilla spawner is restoring a save.");
            if (!ValidateSetup(spawner))
                throw new InvalidOperationException(
                    "The vanilla mining spawner rejected its pickup/profile/rule setup.");
            void Invoke() => _ = _api.RuntimeInvoke(
                    RequireMethod(_api, _spawnerClass, "ServerSpawnFromRules", 0),
                    spawner.Pointer,
                    0);
            if (selection == MiningProfileSelectionMode.FallbackOnly)
                WithoutActivePropertyManager(Invoke);
            else
                Invoke();
        }

        private bool EnsureNetworkSpawned(UnityObject gameObject, UnityObject identity)
        {
            if (ReadNetId(identity) != 0) return false;
            _ = _api.Invoke(
                _networkSpawnMethod,
                0,
                Il2CppArgument.FromReference(gameObject.Pointer),
                Il2CppArgument.FromReference(0));
            var netId = ReadNetId(identity);
            if (netId == 0)
                throw new InvalidOperationException(
                    "Mirror did not assign a netId to the runtime mining spawner.");
            return true;
        }

        private void SpawnNow(
            UnityObject spawner,
            MiningProfileSelectionMode selection)
        {
            if (!_isServerActive())
                throw new InvalidOperationException(
                    "Mining nodes may only be spawned by the active Mirror server/host.");
            if (!ValidateSetup(spawner))
                throw new InvalidOperationException(
                    "The vanilla mining spawner rejected its pickup/profile/rule setup.");
            var selectedProfile = ResolveProfile(spawner, selection);
            if (selectedProfile == 0)
                throw new InvalidOperationException("The mining spawner resolved a null profile.");
            WriteReference(spawner.Pointer, "_activeProfile", selectedProfile);
            var enumerator = _api.RuntimeInvoke(
                RequireMethod(_api, _spawnerClass, "Co_ServerSpawnAll", 0),
                spawner.Pointer,
                0);
            if (enumerator == 0)
                throw new InvalidOperationException("Co_ServerSpawnAll returned null.");
            _ = _api.Invoke(
                _startCoroutineMethod,
                spawner.Pointer,
                Il2CppArgument.FromReference(enumerator));
        }

        private uint ReadNetId(UnityObject identity)
        {
            var boxed = _api.RuntimeInvoke(
                RequireMethod(_api, _networkIdentityClass, "get_netId", 0),
                identity.Pointer,
                0);
            var value = _api.Unbox(boxed);
            return value == 0 ? 0 : unchecked((uint)Marshal.ReadInt32(value));
        }

        private void ReleaseNetworkSpawn(UnityObject gameObject, bool destroy)
        {
            if (!_isServerActive()) return;
            _ = _api.Invoke(
                destroy ? _networkDestroyMethod : _networkUnspawnMethod,
                0,
                Il2CppArgument.FromReference(gameObject.Pointer));
        }

        private bool ValidateSetup(UnityObject spawner)
        {
            var boxed = _api.RuntimeInvoke(
                RequireMethod(_api, _spawnerClass, "ValidateSetup", 0),
                spawner.Pointer,
                0);
            var value = _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private T WithoutActivePropertyManager<T>(Func<T> action)
        {
            var original = _api.ReadStaticObjectReference(_propertyManagerInstanceField);
            _api.WriteStaticObjectReference(_propertyManagerInstanceField, 0);
            try { return action(); }
            finally { _api.WriteStaticObjectReference(_propertyManagerInstanceField, original); }
        }

        private void WithoutActivePropertyManager(Action action)
        {
            var original = _api.ReadStaticObjectReference(_propertyManagerInstanceField);
            _api.WriteStaticObjectReference(_propertyManagerInstanceField, 0);
            try { action(); }
            finally { _api.WriteStaticObjectReference(_propertyManagerInstanceField, original); }
        }

        private void Clear(UnityObject spawner) => _ = _api.RuntimeInvoke(
            RequireMethod(_api, _spawnerClass, "ClearTrackingData", 0),
            spawner.Pointer,
            0);

        private void NotifyRestoreComplete(UnityObject spawner) => _ = _api.RuntimeInvoke(
            RequireMethod(_api, _spawnerClass, "NotifyRestoreComplete", 0),
            spawner.Pointer,
            0);

        private bool IsUnityAlive(UnityObject value)
        {
            if (value.IsNull) return false;
            var method = _api.FindMethodBySignature(
                _unityObjectClass,
                "op_Implicit",
                ["UnityEngine.Object"]);
            if (method == 0) return true;
            var boxed = _api.Invoke(
                method,
                0,
                Il2CppArgument.FromReference(value.Pointer));
            var unboxed = _api.Unbox(boxed);
            return unboxed != 0 && Marshal.ReadByte(unboxed) != 0;
        }

        private void Disable(UnityObject component)
        {
            if (component.IsNull ||
                !_api.IsAssignableFrom(_behaviourClass, _api.GetObjectClass(component.Pointer))) return;
            _ = _api.Invoke(
                RequireMethod(_api, _behaviourClass, "set_enabled", 1),
                component.Pointer,
                Il2CppArgument.FromBoolean(false));
        }

        private nint ReadReference(nint pointer, string field) =>
            _api.ReadObjectReference(pointer, RequireField(_api, _spawnerClass, field));
        private void WriteReference(nint pointer, string field, nint value) =>
            _api.WriteObjectReference(pointer, RequireField(_api, _spawnerClass, field), value);
        private int ReadInt(nint pointer, string field) =>
            _api.ReadInt32(pointer, RequireField(_api, _spawnerClass, field));
        private void WriteInt(nint pointer, string field, int value) =>
            _api.WriteInt32(pointer, RequireField(_api, _spawnerClass, field), value);
        private float ReadFloat(nint pointer, string field) =>
            _api.ReadSingle(pointer, RequireField(_api, _spawnerClass, field));
        private void WriteFloat(nint pointer, string field, float value) =>
            _api.WriteSingle(pointer, RequireField(_api, _spawnerClass, field), value);
        private bool ReadBool(nint pointer, string field) =>
            _api.ReadBoolean(pointer, RequireField(_api, _spawnerClass, field));
        private void WriteBool(nint pointer, string field, bool value) =>
            _api.WriteBoolean(pointer, RequireField(_api, _spawnerClass, field), value);
        private int ReadRuleInt(nint pointer, string field) =>
            _api.ReadInt32(pointer, RequireField(_api, _ruleClass, field));
        private void WriteRuleInt(nint pointer, string field, int value) =>
            _api.WriteInt32(pointer, RequireField(_api, _ruleClass, field), value);
        private float ReadRuleFloat(nint pointer, string field) =>
            _api.ReadSingle(pointer, RequireField(_api, _ruleClass, field));
        private void WriteRuleFloat(nint pointer, string field, float value) =>
            _api.WriteSingle(pointer, RequireField(_api, _ruleClass, field), value);
        private void WriteRuleBool(nint pointer, string field, bool value) =>
            _api.WriteBoolean(pointer, RequireField(_api, _ruleClass, field), value);

        private nint NewConstructedObject(nint klass)
        {
            var result = _api.NewObject(klass);
            if (result == 0) throw new InvalidOperationException("IL2CPP allocation returned null.");
            _ = _api.RuntimeInvoke(RequireMethod(_api, klass, ".ctor", 0), result, 0);
            return result;
        }

        private int GetCount(nint list)
        {
            var boxed = _api.RuntimeInvoke(
                RequireMethod(_api, _api.GetObjectClass(list), "get_Count", 0),
                list,
                0);
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("List<T>.Count returned null.");
        }

        private unsafe nint GetAt(nint list, int index)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return _api.RuntimeInvoke(
                RequireMethod(_api, _api.GetObjectClass(list), "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void Add(nint list, nint value)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = value;
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, _api.GetObjectClass(list), "Add", 1),
                list,
                (nint)arguments);
        }

        private void DestroyRule(OwnedRule rule)
        {
            if (rule.OwnsHost) _unity.Destroy(rule.GameObject);
            else _unity.Destroy(rule.Component);
        }

        private sealed class MiningAreaSpawnerHandle(
            MiningAreaSpawnerRegistry owner,
            UnityObject gameObject,
            UnityObject component,
            UnityObject identity,
            bool ownsRoot,
            bool ownsIdentity,
            IReadOnlyList<OwnedRule> ownedRules,
            MiningProfileSelectionMode profileSelection) : IMiningAreaSpawner
        {
            private bool _removed;
            private bool _networkSpawnedByHandle;

            public UnityObject GameObject => gameObject;
            public UnityObject Component => component;
            public IReadOnlyList<MiningSpawnRuleDefinition> Rules =>
                owner.DescribeComponent(component).Rules;
            public bool IsAlive => !_removed && owner.IsUnityAlive(component);
            public bool IsRestoringFromSave
            {
                get
                {
                    EnsureAlive();
                    return owner.ReadBool(component.Pointer, "_isRestoringFromSave");
                }
            }
            public bool InitialCountsCalculated
            {
                get
                {
                    EnsureAlive();
                    return owner.ReadBool(component.Pointer, "_initialCountsCalculated");
                }
            }

            public MiningAreaSpawnerDefinition Describe()
            {
                EnsureAlive();
                return owner.DescribeComponent(component);
            }

            public UnityObject ResolveActiveProfile()
            {
                EnsureAlive();
                return new UnityObject(owner.ResolveProfile(component, profileSelection));
            }

            public int ComputeCapacity(MiningSpawnRuleDefinition rule)
            {
                EnsureAlive();
                if (!Rules.Any(value => value.Component.Pointer == rule.Component.Pointer))
                    throw new ArgumentException("Rule does not belong to this spawner.");
                return owner.ComputeCapacity(component, rule.Component);
            }

            public int GetRemainingNodeCount(MiningLayerKind layer, string itemId)
            {
                EnsureAlive();
                return owner.GetRemainingNodeCount(component, layer, itemId);
            }

            public bool ValidateSetup()
            {
                EnsureAlive();
                return owner.ValidateSetup(component);
            }

            public void NotifyRestoreComplete()
            {
                EnsureAlive();
                owner.NotifyRestoreComplete(component);
            }

            public void SpawnOnServer()
            {
                EnsureAlive();
                var newlySpawned = owner.EnsureNetworkSpawned(gameObject, identity);
                _networkSpawnedByHandle |= newlySpawned;
                if (newlySpawned) owner.NotifyRestoreComplete(component);
                owner.Spawn(component, profileSelection);
            }

            public void SpawnNowOnServer()
            {
                EnsureAlive();
                var newlySpawned = owner.EnsureNetworkSpawned(gameObject, identity);
                _networkSpawnedByHandle |= newlySpawned;
                if (newlySpawned) owner.NotifyRestoreComplete(component);
                owner.SpawnNow(component, profileSelection);
            }

            public void ClearTrackingData()
            {
                EnsureAlive();
                owner.Clear(component);
            }

            public void Remove()
            {
                if (_removed) return;
                _removed = true;
                Owners.Remove(component.Pointer);
                if (ownsRoot)
                {
                    owner._unity.SetActive(gameObject, false);
                    if (_networkSpawnedByHandle)
                        owner.ReleaseNetworkSpawn(gameObject, destroy: true);
                    else
                        owner._unity.Destroy(gameObject);
                    return;
                }
                if (_networkSpawnedByHandle)
                    owner.ReleaseNetworkSpawn(gameObject, destroy: false);
                owner.Disable(component);
                owner._unity.Destroy(component);
                for (var index = ownedRules.Count - 1; index >= 0; --index)
                {
                    owner.Disable(ownedRules[index].Component);
                    owner.DestroyRule(ownedRules[index]);
                }
                if (ownsIdentity) owner._unity.Destroy(identity);
            }

            public void Dispose() => Remove();

            private void EnsureAlive()
            {
                if (!IsAlive) throw new ObjectDisposedException(nameof(IMiningAreaSpawner));
            }
        }

        private sealed record OwnedRule(
            UnityObject GameObject,
            UnityObject Component,
            MiningLayerKind Layer,
            bool OwnsHost,
            int Id);
    }
}
