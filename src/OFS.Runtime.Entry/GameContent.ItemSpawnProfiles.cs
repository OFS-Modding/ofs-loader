using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class ItemSpawnProfileRegistry : IItemSpawnProfileRegistry
    {
        private static readonly Dictionary<nint, string> MutationOwners = [];

        private readonly string _ownerId;
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _profileClass;
        private readonly nint _layerClass;
        private readonly nint _weightedClass;
        private readonly nint _itemClass;
        private readonly nint _propertyClass;
        private readonly nint _listManagerClass;
        private readonly nint _unityObjectClass;
        private readonly nint _scriptableObjectClass;
        private readonly Dictionary<nint, ItemSpawnProfileDefinition> _transactionSnapshots = [];
        private readonly List<nint> _transactionMutationOrder = [];
        private readonly HashSet<nint> _transactionOwnershipAdded = [];
        private readonly List<UnityObject> _transactionCreated = [];
        private bool _transactionCommitted;

        public ItemSpawnProfileRegistry(string ownerId, IUnsafeIl2CppApi api)
        {
            _ownerId = ownerId;
            _api = api;
            _profileClass = RequireClass(api, "T_ItemSpawnProfile");
            _layerClass = api.FindNestedClass(_profileClass, "LayerData");
            _weightedClass = api.FindNestedClass(_profileClass, "WeightedSO");
            if (_layerClass == 0 || _weightedClass == 0)
                throw new TypeLoadException("T_ItemSpawnProfile nested types were not found.");
            _itemClass = RequireClass(api, "T_ItemSO");
            _propertyClass = RequireClass(api, "PropertyConfigSO");
            _listManagerClass = RequireClass(api, "ScriptableListManager");
            _unityObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            _scriptableObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "ScriptableObject");
            if (_unityObjectClass == 0 || _scriptableObjectClass == 0)
                throw new TypeLoadException("UnityEngine.Object/ScriptableObject was not found.");
        }

        public IReadOnlyList<ItemSpawnProfileDefinition> GetReferenced()
        {
            EnsureMainThread();
            var manager = _api.RuntimeInvoke(
                RequireMethod(_api, _listManagerClass, "get_Instance", 0),
                0,
                0);
            if (manager == 0)
                throw new InvalidOperationException("ScriptableListManager.Instance is unavailable.");
            var properties = _api.RuntimeInvoke(
                RequireMethod(_api, _listManagerClass, "get_AllPropertyConfigs", 0),
                manager,
                0);
            if (properties == 0)
                throw new InvalidOperationException("PropertyConfigSO catalog is unavailable.");

            var result = new List<ItemSpawnProfileDefinition>();
            var seen = new HashSet<nint>();
            for (var propertyIndex = 0; propertyIndex < GetCount(properties); ++propertyIndex)
            {
                var property = GetAt(properties, propertyIndex);
                if (property == 0) continue;
                var profiles = _api.ReadObjectReference(
                    property,
                    RequireField(_api, _propertyClass, "itemSpawnProfiles"));
                if (profiles == 0) continue;
                for (var index = 0; index < GetCount(profiles); ++index)
                {
                    var profile = GetAt(profiles, index);
                    if (profile != 0 && seen.Add(profile))
                        result.Add(DescribeCore(new UnityObject(profile)));
                }
            }
            return result;
        }

        public UnityObject Create(string assetName, ItemSpawnProfileBlueprint blueprint)
        {
            EnsureMainThread();
            ValidateAssetName(assetName);
            ArgumentNullException.ThrowIfNull(blueprint);
            ValidateBlueprint(blueprint);
            var create = _api.FindMethodBySignature(
                _scriptableObjectClass,
                "CreateInstance",
                ["System.Type"]);
            if (create == 0)
                throw new MissingMethodException(
                    "UnityEngine.ScriptableObject.CreateInstance(System.Type)");
            var pointer = _api.Invoke(
                create,
                0,
                Il2CppArgument.FromReference(_api.GetTypeObject(_profileClass)));
            if (pointer == 0)
                throw new InvalidOperationException("ScriptableObject.CreateInstance returned null.");
            var asset = new UnityObject(pointer);
            try
            {
                SetAssetName(pointer, assetName);
                ApplyBlueprint(pointer, blueprint);
                ValidateDefinition(DescribeCore(asset));
                ClaimCreated(pointer);
                if (!_transactionCommitted) _transactionCreated.Add(asset);
                return asset;
            }
            catch
            {
                Destroy(asset);
                throw;
            }
        }

        public unsafe UnityObject Clone(UnityObject source, string assetName)
        {
            EnsureMainThread();
            ValidateProfile(source);
            ValidateAssetName(assetName);
            nint* arguments = stackalloc nint[1];
            arguments[0] = source.Pointer;
            var pointer = _api.RuntimeInvoke(
                RequireMethod(_api, _unityObjectClass, "Instantiate", 1),
                0,
                (nint)arguments);
            if (pointer == 0)
                throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
            var asset = new UnityObject(pointer);
            try
            {
                SetAssetName(pointer, assetName);
                ValidateDefinition(DescribeCore(asset));
                ClaimCreated(pointer);
                if (!_transactionCommitted) _transactionCreated.Add(asset);
                return asset;
            }
            catch
            {
                Destroy(asset);
                throw;
            }
        }

        public ItemSpawnProfileDefinition Describe(UnityObject itemSpawnProfile)
        {
            EnsureMainThread();
            ValidateProfile(itemSpawnProfile);
            return DescribeCore(itemSpawnProfile);
        }

        public void Update(UnityObject itemSpawnProfile, ItemSpawnProfilePatch patch)
        {
            EnsureMainThread();
            ValidateProfile(itemSpawnProfile);
            ArgumentNullException.ThrowIfNull(patch);
            var before = DescribeCore(itemSpawnProfile);
            var claim = ClaimMutation(itemSpawnProfile.Pointer, before);
            try
            {
                ApplyPatch(itemSpawnProfile.Pointer, patch);
                ValidateDefinition(DescribeCore(itemSpawnProfile));
            }
            catch
            {
                Restore(before);
                CancelClaim(itemSpawnProfile.Pointer, claim);
                throw;
            }
        }

        internal void BeginTransaction()
        {
            if (!_transactionCommitted)
                throw new InvalidOperationException("An item spawn profile transaction is already active.");
            _transactionCommitted = false;
        }

        internal void CommitTransaction()
        {
            _transactionCommitted = true;
            ClearTransaction();
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
            for (var index = _transactionMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _transactionMutationOrder[index];
                if (!_transactionSnapshots.TryGetValue(pointer, out var snapshot)) continue;
                try { Restore(snapshot); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                finally
                {
                    if (_transactionOwnershipAdded.Contains(pointer)) MutationOwners.Remove(pointer);
                }
            }
            for (var index = _transactionCreated.Count - 1; index >= 0; --index)
            {
                var asset = _transactionCreated[index];
                try
                {
                    MutationOwners.Remove(asset.Pointer);
                    Destroy(asset);
                }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            ClearTransaction();
            _transactionCommitted = true;
            if (failures is not null)
                throw new AggregateException("Item spawn profile rollback was incomplete.", failures);
        }

        private ItemSpawnProfileDefinition DescribeCore(UnityObject asset)
        {
            var pointer = asset.Pointer;
            return new ItemSpawnProfileDefinition(
                asset,
                GetAssetName(pointer),
                ReadFloat(pointer, _profileClass, "groupSpawnRadius"),
                ReadFloat(pointer, _profileClass, "minGroupDistance"),
                ReadLayer(pointer, "surface", PropertyLayerKind.Surface),
                ReadLayer(pointer, "mid", PropertyLayerKind.Middle),
                ReadLayer(pointer, "deep", PropertyLayerKind.Deep),
                ReadEntries(pointer, _profileClass, "mysteryItems"));
        }

        private ItemSpawnLayerDefinition ReadLayer(
            nint profile,
            string field,
            PropertyLayerKind expected)
        {
            var layer = _api.ReadObjectReference(profile, RequireField(_api, _profileClass, field));
            if (layer == 0) return new ItemSpawnLayerDefinition(expected, []);
            return new ItemSpawnLayerDefinition(
                (PropertyLayerKind)ReadInt(layer, _layerClass, "layerType"),
                ReadEntries(layer, _layerClass, "items"));
        }

        private IReadOnlyList<ItemSpawnEntryDefinition> ReadEntries(
            nint owner,
            nint ownerClass,
            string field)
        {
            var list = _api.ReadObjectReference(owner, RequireField(_api, ownerClass, field));
            if (list == 0) return [];
            var result = new List<ItemSpawnEntryDefinition>(GetCount(list));
            for (var index = 0; index < GetCount(list); ++index)
            {
                var entry = GetAt(list, index);
                if (entry == 0) continue;
                result.Add(new ItemSpawnEntryDefinition(
                    new UnityObject(_api.ReadObjectReference(
                        entry,
                        RequireField(_api, _weightedClass, "so"))),
                    ReadInt(entry, _weightedClass, "minCount"),
                    ReadInt(entry, _weightedClass, "maxCount"),
                    ReadInt(entry, _weightedClass, "spawnGroupMin"),
                    ReadInt(entry, _weightedClass, "spawnGroupMax")));
            }
            return result;
        }

        private void ApplyBlueprint(nint profile, ItemSpawnProfileBlueprint value)
        {
            WriteFloat(profile, _profileClass, "groupSpawnRadius", value.GroupSpawnRadius);
            WriteFloat(profile, _profileClass, "minGroupDistance", value.MinGroupDistance);
            ReplaceLayer(profile, "surface", PropertyLayerKind.Surface, value.SurfaceItems);
            ReplaceLayer(profile, "mid", PropertyLayerKind.Middle, value.MiddleItems);
            ReplaceLayer(profile, "deep", PropertyLayerKind.Deep, value.DeepItems);
            ReplaceEntries(profile, _profileClass, "mysteryItems", value.MysteryItems);
        }

        private void ApplyPatch(nint profile, ItemSpawnProfilePatch patch)
        {
            if (patch.GroupSpawnRadius is not null)
                WriteFloat(profile, _profileClass, "groupSpawnRadius", patch.GroupSpawnRadius.Value);
            if (patch.MinGroupDistance is not null)
                WriteFloat(profile, _profileClass, "minGroupDistance", patch.MinGroupDistance.Value);
            if (patch.SurfaceItems is not null)
                ReplaceLayer(profile, "surface", PropertyLayerKind.Surface, patch.SurfaceItems);
            if (patch.MiddleItems is not null)
                ReplaceLayer(profile, "mid", PropertyLayerKind.Middle, patch.MiddleItems);
            if (patch.DeepItems is not null)
                ReplaceLayer(profile, "deep", PropertyLayerKind.Deep, patch.DeepItems);
            if (patch.MysteryItems is not null)
                ReplaceEntries(profile, _profileClass, "mysteryItems", patch.MysteryItems);
        }

        private void Restore(ItemSpawnProfileDefinition value) => ApplyBlueprint(
            value.Asset.Pointer,
            new ItemSpawnProfileBlueprint(
                value.GroupSpawnRadius,
                value.MinGroupDistance,
                value.Surface.Items,
                value.Middle.Items,
                value.Deep.Items,
                value.MysteryItems));

        private void ReplaceLayer(
            nint profile,
            string field,
            PropertyLayerKind kind,
            IReadOnlyList<ItemSpawnEntryDefinition> entries)
        {
            var layer = NewConstructedObject(_layerClass);
            WriteInt(layer, _layerClass, "layerType", (int)kind);
            ReplaceEntries(layer, _layerClass, "items", entries);
            _api.WriteObjectReference(profile, RequireField(_api, _profileClass, field), layer);
        }

        private void ReplaceEntries(
            nint owner,
            nint ownerClass,
            string field,
            IReadOnlyList<ItemSpawnEntryDefinition> entries)
        {
            var targetField = RequireField(_api, ownerClass, field);
            var list = NewConstructedObject(_api.GetFieldTypeClass(targetField));
            _api.WriteObjectReference(owner, targetField, list);
            foreach (var value in entries)
            {
                var entry = NewConstructedObject(_weightedClass);
                _api.WriteObjectReference(
                    entry,
                    RequireField(_api, _weightedClass, "so"),
                    value.Item.Pointer);
                WriteInt(entry, _weightedClass, "minCount", value.MinCount);
                WriteInt(entry, _weightedClass, "maxCount", value.MaxCount);
                WriteInt(entry, _weightedClass, "spawnGroupMin", value.SpawnGroupMin);
                WriteInt(entry, _weightedClass, "spawnGroupMax", value.SpawnGroupMax);
                Add(list, entry);
            }
        }

        private void ValidateBlueprint(ItemSpawnProfileBlueprint value)
        {
            ValidateGeometry(value.GroupSpawnRadius, value.MinGroupDistance);
            ValidateEntries(value.SurfaceItems, nameof(value.SurfaceItems));
            ValidateEntries(value.MiddleItems, nameof(value.MiddleItems));
            ValidateEntries(value.DeepItems, nameof(value.DeepItems));
            ValidateEntries(value.MysteryItems, nameof(value.MysteryItems));
            if (value.SurfaceItems.Count + value.MiddleItems.Count + value.DeepItems.Count == 0)
                throw new ArgumentException("At least one mining-layer spawn entry is required.");
        }

        private void ValidateDefinition(ItemSpawnProfileDefinition value)
        {
            ValidateAssetName(value.AssetName);
            ValidateGeometry(value.GroupSpawnRadius, value.MinGroupDistance);
            if (value.Surface.Kind != PropertyLayerKind.Surface ||
                value.Middle.Kind != PropertyLayerKind.Middle ||
                value.Deep.Kind != PropertyLayerKind.Deep)
                throw new InvalidDataException("Item spawn profile layer kinds are inconsistent.");
            ValidateEntries(value.Surface.Items, nameof(value.Surface));
            ValidateEntries(value.Middle.Items, nameof(value.Middle));
            ValidateEntries(value.Deep.Items, nameof(value.Deep));
            ValidateEntries(value.MysteryItems, nameof(value.MysteryItems));
            if (value.Surface.Items.Count + value.Middle.Items.Count + value.Deep.Items.Count == 0)
                throw new ArgumentException("At least one mining-layer spawn entry is required.");
        }

        private static void ValidateGeometry(float radius, float distance)
        {
            if (!float.IsFinite(radius) || radius < 0.1f || radius > 1000f)
                throw new ArgumentOutOfRangeException(nameof(radius));
            if (!float.IsFinite(distance) || distance < 0f || distance > 10000f)
                throw new ArgumentOutOfRangeException(nameof(distance));
        }

        private void ValidateEntries(
            IReadOnlyList<ItemSpawnEntryDefinition> entries,
            string parameter)
        {
            if (entries.Count > 1000)
                throw new ArgumentException("A spawn entry list may contain at most 1000 items.", parameter);
            var seen = new HashSet<nint>();
            foreach (var value in entries)
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value.Item.IsNull ||
                    !_api.IsAssignableFrom(_itemClass, _api.GetObjectClass(value.Item.Pointer)))
                    throw new ArgumentException("Spawn entry item is not a T_ItemSO.", parameter);
                if (!seen.Add(value.Item.Pointer))
                    throw new ArgumentException("Spawn entry items must be unique per list.", parameter);
                if (value.MinCount < 0 || value.MinCount > value.MaxCount ||
                    value.MaxCount > 1_000_000)
                    throw new ArgumentException("Spawn entry count range is invalid.", parameter);
                if (value.SpawnGroupMin < 1 || value.SpawnGroupMin > value.SpawnGroupMax ||
                    value.SpawnGroupMax > 100_000)
                    throw new ArgumentException("Spawn entry group range is invalid.", parameter);
            }
        }

        private static void ValidateAssetName(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Length > 200)
                throw new ArgumentException("Spawn profile asset name is longer than 200 characters.");
        }

        private void ValidateProfile(UnityObject value)
        {
            if (value.IsNull ||
                !_api.IsAssignableFrom(_profileClass, _api.GetObjectClass(value.Pointer)))
                throw new ArgumentException("Asset is not a T_ItemSpawnProfile.");
        }

        private void ClaimCreated(nint pointer)
        {
            if (MutationOwners.TryGetValue(pointer, out var owner) &&
                !string.Equals(owner, _ownerId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Spawn profile is owned by mod '{owner}'.");
            MutationOwners[pointer] = _ownerId;
            if (!_transactionCommitted) _transactionOwnershipAdded.Add(pointer);
        }

        private MutationClaim ClaimMutation(nint pointer, ItemSpawnProfileDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, _ownerId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Spawn profile is already mutated by mod '{owner}'.");
            }
            else
            {
                MutationOwners.Add(pointer, _ownerId);
                ownershipAdded = true;
                if (!_transactionCommitted) _transactionOwnershipAdded.Add(pointer);
            }
            var snapshotAdded = !_transactionCommitted &&
                _transactionSnapshots.TryAdd(pointer, before);
            if (snapshotAdded) _transactionMutationOrder.Add(pointer);
            return new MutationClaim(ownershipAdded, snapshotAdded);
        }

        private void CancelClaim(nint pointer, MutationClaim claim)
        {
            if (claim.SnapshotAdded)
            {
                _transactionSnapshots.Remove(pointer);
                _transactionMutationOrder.Remove(pointer);
            }
            if (claim.OwnershipAdded)
            {
                MutationOwners.Remove(pointer);
                _transactionOwnershipAdded.Remove(pointer);
            }
        }

        private string GetAssetName(nint pointer)
        {
            var value = _api.RuntimeInvoke(
                RequireMethod(_api, _unityObjectClass, "get_name", 0),
                pointer,
                0);
            return value == 0 ? string.Empty : _api.ReadString(value);
        }

        private void SetAssetName(nint pointer, string value) => _ = _api.Invoke(
            RequireMethod(_api, _unityObjectClass, "set_name", 1),
            pointer,
            Il2CppArgument.FromReference(_api.NewString(value)));

        private int ReadInt(nint instance, nint klass, string field) =>
            _api.ReadInt32(instance, RequireField(_api, klass, field));

        private void WriteInt(nint instance, nint klass, string field, int value) =>
            _api.WriteInt32(instance, RequireField(_api, klass, field), value);

        private float ReadFloat(nint instance, nint klass, string field) =>
            _api.ReadSingle(instance, RequireField(_api, klass, field));

        private void WriteFloat(nint instance, nint klass, string field, float value) =>
            _api.WriteSingle(instance, RequireField(_api, klass, field), value);

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

        private unsafe void Destroy(UnityObject asset)
        {
            if (asset.IsNull) return;
            nint* arguments = stackalloc nint[1];
            arguments[0] = asset.Pointer;
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, _unityObjectClass, "Destroy", 1),
                0,
                (nint)arguments);
        }

        private void ClearTransaction()
        {
            _transactionSnapshots.Clear();
            _transactionMutationOrder.Clear();
            _transactionOwnershipAdded.Clear();
            _transactionCreated.Clear();
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);
    }
}
