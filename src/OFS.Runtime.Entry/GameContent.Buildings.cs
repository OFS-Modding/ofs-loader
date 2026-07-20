using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class BuildingRegistry(
        string ownerId,
        IUnsafeIl2CppApi unsafeApi,
        Func<bool> isNetworkActive) : IBuildingRegistry
    {
        private static readonly Dictionary<string, BuildingRegistration> OwnedBuildings =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<nint, string> MutationOwners = new();
        private readonly Dictionary<nint, BuildingDefinition> _loadSnapshots = new();
        private readonly List<nint> _loadMutationOrder = [];
        private readonly HashSet<nint> _loadOwnershipAdded = [];
        private readonly List<UnityObject> _loadClones = [];
        private readonly List<BuildingRegistration> _loadRegistrations = [];
        private bool _loadCommitted;

        private readonly nint _buildingClass = RequireClass(unsafeApi, "T_BuildingItemSO");
        private readonly nint _managerClass = RequireClass(unsafeApi, "ScriptableListManager");
        private readonly nint _unityObjectClass = RequireUnityClass(
            unsafeApi,
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Object");

        public int Count
        {
            get
            {
                EnsureMainThread();
                return GetCount(GetList());
            }
        }

        public unsafe UnityObject Clone(UnityObject source, string newBuildingId)
        {
            EnsureMainThread();
            ValidateBuilding(source);
            ValidateBuildingId(newBuildingId);
            if (!FindById(newBuildingId).IsNull || OwnedBuildings.ContainsKey(newBuildingId))
            {
                throw new InvalidOperationException($"Building id '{newBuildingId}' is already registered.");
            }

            nint* arguments = stackalloc nint[1];
            arguments[0] = source.Pointer;
            var clone = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _unityObjectClass, "Instantiate", 1),
                0,
                (nint)arguments);
            if (clone == 0)
            {
                throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
            }
            unsafeApi.WriteObjectReference(
                clone,
                RequireField(unsafeApi, _buildingClass, "BuildingID"),
                unsafeApi.NewString(newBuildingId));
            var result = new UnityObject(clone);
            if (!_loadCommitted) _loadClones.Add(result);
            return result;
        }

        public UnityObject FindById(string buildingId)
        {
            EnsureMainThread();
            ValidateBuildingId(buildingId);
            var list = GetList();
            var count = GetCount(list);
            for (var index = 0; index < count; ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0 && string.Equals(ReadBuildingId(asset), buildingId, StringComparison.Ordinal))
                {
                    return new UnityObject(asset);
                }
            }
            return default;
        }

        public BuildingDefinition Describe(UnityObject buildingScriptableObject)
        {
            EnsureMainThread();
            ValidateBuilding(buildingScriptableObject);
            var pointer = buildingScriptableObject.Pointer;
            return new BuildingDefinition(
                buildingScriptableObject,
                ReadBuildingId(pointer),
                ReadString(pointer, "Name"),
                ReadString(pointer, "Description"),
                ReadInt(pointer, "Price"),
                (BuildingCategory)ReadInt(pointer, "Category"),
                ReadInt(pointer, "packageQuantity"),
                new UnityObject(ReadReference(pointer, "Icon")),
                new UnityObject(ReadReference(pointer, "Prefab")),
                ReadFloat(pointer, "rotationStep"),
                ReadInt(pointer, "Level"),
                ReadBool(pointer, "canBeSoldInMarket"),
                ReadBool(pointer, "canBeSoldBackToMarket"),
                ReadBool(pointer, "canBeResaledWithHammer"),
                ReadBool(pointer, "canBeRelocatedWithHammer"),
                ReadBool(pointer, "excludeFromBoxSpawn"),
                ReadBool(pointer, "checkTerrainSupport"),
                ReadBool(pointer, "isBlockedDuringTutorial"),
                ReadBool(pointer, "isTutorialFree"),
                ReadInt(pointer, "additionalPlacementLayers"),
                ReadBool(pointer, "placeOnlyOnAdditionalLayers"),
                ReadBool(pointer, "canPlaceOnWall"),
                ReadBool(pointer, "ignoreGridSnap"),
                (UpgradeKind)ReadInt(pointer, "requiredUpgrade"),
                ReadInt(pointer, "requiredUpgradeLevel"),
                ReadBool(pointer, "fullVersionOnly"),
                ReadBool(pointer, "updateAIPath"));
        }

        public IReadOnlyList<BuildingDefinition> GetAll()
        {
            EnsureMainThread();
            var list = GetList();
            var result = new List<BuildingDefinition>(GetCount(list));
            for (var index = 0; index < GetCount(list); ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0)
                {
                    result.Add(Describe(new UnityObject(asset)));
                }
            }
            return result;
        }

        public int GetNetworkIndex(UnityObject buildingScriptableObject)
        {
            EnsureMainThread();
            ValidateBuilding(buildingScriptableObject);
            var list = GetList();
            var count = GetCount(list);
            for (var index = 0; index < count; ++index)
            {
                if (GetAt(list, index) == buildingScriptableObject.Pointer)
                {
                    return index;
                }
            }
            return -1;
        }

        public void Update(UnityObject buildingScriptableObject, BuildingPatch patch)
        {
            EnsureMainThread();
            ValidateBuilding(buildingScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            var before = Describe(buildingScriptableObject);
            var claim = ClaimMutation(buildingScriptableObject.Pointer, before);
            try
            {
                ApplyUpdate(buildingScriptableObject, patch);
            }
            catch
            {
                RestoreDefinition(before);
                CancelClaim(buildingScriptableObject.Pointer, claim);
                throw;
            }
        }

        private void ApplyUpdate(UnityObject buildingScriptableObject, BuildingPatch patch)
        {
            EnsureMainThread();
            ValidateBuilding(buildingScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            var pointer = buildingScriptableObject.Pointer;

            if (patch.Name is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(patch.Name);
                WriteString(pointer, "Name", patch.Name);
            }
            if (patch.Description is not null) WriteString(pointer, "Description", patch.Description);
            if (patch.Price is not null)
            {
                RequireNonNegative(patch.Price.Value, nameof(patch.Price));
                WriteInt(pointer, "Price", patch.Price.Value);
            }
            if (patch.Category is not null)
            {
                if (!Enum.IsDefined(patch.Category.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Unknown building category.");
                }
                WriteInt(pointer, "Category", (int)patch.Category.Value);
            }
            if (patch.PackageQuantity is not null)
            {
                if (patch.PackageQuantity < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch.PackageQuantity));
                }
                WriteInt(pointer, "packageQuantity", patch.PackageQuantity.Value);
            }
            if (patch.Icon is not null) WriteReference(pointer, "Icon", patch.Icon.Value.Pointer);
            if (patch.Prefab is not null) WriteReference(pointer, "Prefab", patch.Prefab.Value.Pointer);
            if (patch.RotationStep is not null)
            {
                if (!float.IsFinite(patch.RotationStep.Value) || patch.RotationStep <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch.RotationStep));
                }
                WriteFloat(pointer, "rotationStep", patch.RotationStep.Value);
            }
            if (patch.Level is not null)
            {
                RequireNonNegative(patch.Level.Value, nameof(patch.Level));
                WriteInt(pointer, "Level", patch.Level.Value);
            }
            WriteOptionalBool(pointer, "canBeSoldInMarket", patch.SoldInMarket);
            WriteOptionalBool(pointer, "canBeSoldBackToMarket", patch.SoldBackToMarket);
            WriteOptionalBool(pointer, "canBeResaledWithHammer", patch.ResalableWithHammer);
            WriteOptionalBool(pointer, "canBeRelocatedWithHammer", patch.RelocatableWithHammer);
            WriteOptionalBool(pointer, "excludeFromBoxSpawn", patch.ExcludedFromBoxSpawn);
            WriteOptionalBool(pointer, "checkTerrainSupport", patch.CheckTerrainSupport);
            WriteOptionalBool(pointer, "isBlockedDuringTutorial", patch.BlockedDuringTutorial);
            WriteOptionalBool(pointer, "isTutorialFree", patch.TutorialFree);
            if (patch.AdditionalPlacementLayers is not null)
            {
                WriteInt(pointer, "additionalPlacementLayers", patch.AdditionalPlacementLayers.Value);
            }
            WriteOptionalBool(pointer, "placeOnlyOnAdditionalLayers", patch.PlaceOnlyOnAdditionalLayers);
            WriteOptionalBool(pointer, "canPlaceOnWall", patch.PlaceOnWall);
            WriteOptionalBool(pointer, "ignoreGridSnap", patch.IgnoreGridSnap);
            if (patch.RequiredUpgrade is not null)
            {
                if (!Enum.IsDefined(patch.RequiredUpgrade.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Unknown upgrade kind.");
                }
                WriteInt(pointer, "requiredUpgrade", (int)patch.RequiredUpgrade.Value);
            }
            if (patch.RequiredUpgradeLevel is not null)
            {
                RequireNonNegative(patch.RequiredUpgradeLevel.Value, nameof(patch.RequiredUpgradeLevel));
                WriteInt(pointer, "requiredUpgradeLevel", patch.RequiredUpgradeLevel.Value);
            }
            WriteOptionalBool(pointer, "fullVersionOnly", patch.FullVersionOnly);
            WriteOptionalBool(pointer, "updateAIPath", patch.UpdateAiPath);
        }

        private void RestoreDefinition(BuildingDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteString(pointer, "Name", definition.Name);
            WriteString(pointer, "Description", definition.Description);
            WriteInt(pointer, "Price", definition.Price);
            WriteInt(pointer, "Category", (int)definition.Category);
            WriteInt(pointer, "packageQuantity", definition.PackageQuantity);
            WriteReference(pointer, "Icon", definition.Icon.Pointer);
            WriteReference(pointer, "Prefab", definition.Prefab.Pointer);
            WriteFloat(pointer, "rotationStep", definition.RotationStep);
            WriteInt(pointer, "Level", definition.Level);
            WriteBool(pointer, "canBeSoldInMarket", definition.SoldInMarket);
            WriteBool(pointer, "canBeSoldBackToMarket", definition.SoldBackToMarket);
            WriteBool(pointer, "canBeResaledWithHammer", definition.ResalableWithHammer);
            WriteBool(pointer, "canBeRelocatedWithHammer", definition.RelocatableWithHammer);
            WriteBool(pointer, "excludeFromBoxSpawn", definition.ExcludedFromBoxSpawn);
            WriteBool(pointer, "checkTerrainSupport", definition.CheckTerrainSupport);
            WriteBool(pointer, "isBlockedDuringTutorial", definition.BlockedDuringTutorial);
            WriteBool(pointer, "isTutorialFree", definition.TutorialFree);
            WriteInt(pointer, "additionalPlacementLayers", definition.AdditionalPlacementLayers);
            WriteBool(pointer, "placeOnlyOnAdditionalLayers", definition.PlaceOnlyOnAdditionalLayers);
            WriteBool(pointer, "canPlaceOnWall", definition.PlaceOnWall);
            WriteBool(pointer, "ignoreGridSnap", definition.IgnoreGridSnap);
            WriteInt(pointer, "requiredUpgrade", (int)definition.RequiredUpgrade);
            WriteInt(pointer, "requiredUpgradeLevel", definition.RequiredUpgradeLevel);
            WriteBool(pointer, "fullVersionOnly", definition.FullVersionOnly);
            WriteBool(pointer, "updateAIPath", definition.UpdateAiPath);
        }

        private MutationClaim ClaimMutation(nint pointer, BuildingDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Building '{before.BuildingId}' is already mutated by mod '{owner}'.");
                }
            }
            else
            {
                MutationOwners.Add(pointer, ownerId);
                ownershipAdded = true;
                if (!_loadCommitted) _loadOwnershipAdded.Add(pointer);
            }
            var snapshotAdded = !_loadCommitted && _loadSnapshots.TryAdd(pointer, before);
            if (snapshotAdded)
            {
                _loadMutationOrder.Add(pointer);
            }
            return new MutationClaim(ownershipAdded, snapshotAdded);
        }

        private void CancelClaim(nint pointer, MutationClaim claim)
        {
            if (claim.SnapshotAdded)
            {
                _loadSnapshots.Remove(pointer);
                _loadMutationOrder.Remove(pointer);
            }
            if (claim.OwnershipAdded)
            {
                MutationOwners.Remove(pointer);
                _loadOwnershipAdded.Remove(pointer);
            }
        }

        public IBuildingRegistration Register(UnityObject buildingScriptableObject)
        {
            EnsureMainThread();
            ValidateBuilding(buildingScriptableObject);
            if (isNetworkActive() && !IsBuildingRegistrationWindowOpen)
            {
                throw new InvalidOperationException(
                    "An active Mirror session only accepts building registration inside the ContentReady callback window.");
            }

            var buildingId = ReadBuildingId(buildingScriptableObject.Pointer);
            ValidateBuildingId(buildingId);
            var list = GetList();
            if (OwnedBuildings.TryGetValue(buildingId, out var tracked))
            {
                if (tracked.OwnerId != ownerId || tracked.Asset.Pointer != buildingScriptableObject.Pointer)
                {
                    throw new InvalidOperationException(
                        $"Building id '{buildingId}' is already owned by mod '{tracked.OwnerId}'.");
                }
                var currentIndex = FindPointerIndex(list, tracked.Asset.Pointer);
                if (currentIndex >= 0)
                {
                    tracked.UpdateNetworkIndex(currentIndex);
                    return tracked;
                }

                return Append(list, tracked);
            }
            if (!FindById(buildingId).IsNull)
            {
                throw new InvalidOperationException(
                    $"Building id '{buildingId}' is already registered by the base game or an untracked source.");
            }

            var networkIndex = GetCount(list);
            Add(list, buildingScriptableObject.Pointer);
            if (GetCount(list) != networkIndex + 1 || GetAt(list, networkIndex) != buildingScriptableObject.Pointer)
            {
                throw new InvalidOperationException(
                    $"ScriptableListManager did not append building '{buildingId}' deterministically.");
            }

            var registration = new BuildingRegistration(
                ownerId,
                buildingId,
                buildingScriptableObject,
                networkIndex);
            OwnedBuildings.Add(buildingId, registration);
            if (!_loadCommitted) _loadRegistrations.Add(registration);
            RuntimeLog.Write(
                $"Building registered: owner={ownerId}, id={buildingId}, networkIndex={networkIndex}.");
            return registration;
        }

        internal void CommitTransaction()
        {
            _loadCommitted = true;
            _loadSnapshots.Clear();
            _loadMutationOrder.Clear();
            _loadOwnershipAdded.Clear();
            _loadClones.Clear();
            _loadRegistrations.Clear();
        }

        internal void BeginTransaction()
        {
            if (!_loadCommitted)
            {
                throw new InvalidOperationException("A building content transaction is already active.");
            }
            _loadCommitted = false;
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
            for (var index = _loadRegistrations.Count - 1; index >= 0; --index)
            {
                Try(() => RollbackRegistration(_loadRegistrations[index]));
            }
            for (var index = _loadMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _loadMutationOrder[index];
                if (!_loadSnapshots.TryGetValue(pointer, out var snapshot)) continue;
                try { RestoreDefinition(snapshot); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                finally
                {
                    if (_loadOwnershipAdded.Contains(pointer)) MutationOwners.Remove(pointer);
                    _loadSnapshots.Remove(pointer);
                }
            }
            for (var index = _loadClones.Count - 1; index >= 0; --index)
            {
                var clone = _loadClones[index];
                Try(() => DestroyClone(clone));
            }
            _loadRegistrations.Clear();
            _loadMutationOrder.Clear();
            _loadSnapshots.Clear();
            _loadOwnershipAdded.Clear();
            _loadClones.Clear();
            _loadCommitted = true;
            if (failures is not null)
            {
                throw new AggregateException("Building content rollback was incomplete.", failures);
            }

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);

        private void RollbackRegistration(BuildingRegistration registration)
        {
            if (!OwnedBuildings.TryGetValue(registration.BuildingId, out var owned) ||
                !ReferenceEquals(owned, registration) ||
                !string.Equals(registration.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var list = GetList();
            var index = FindPointerIndex(list, registration.Asset.Pointer);
            if (index >= 0)
            {
                var last = GetCount(list) - 1;
                if (index != last)
                {
                    throw new InvalidOperationException(
                        $"Cannot roll back building '{registration.BuildingId}' at non-tail index {index}.");
                }
                RemoveAt(list, index);
            }
            OwnedBuildings.Remove(registration.BuildingId);
            registration.UpdateNetworkIndex(-1);
        }

        private unsafe void DestroyClone(UnityObject clone)
        {
            if (clone.IsNull) return;
            nint* arguments = stackalloc nint[1];
            arguments[0] = clone.Pointer;
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _unityObjectClass, "Destroy", 1),
                0,
                (nint)arguments);
        }

        private BuildingRegistration Append(nint list, BuildingRegistration registration)
        {
            var networkIndex = GetCount(list);
            Add(list, registration.Asset.Pointer);
            if (GetCount(list) != networkIndex + 1 || GetAt(list, networkIndex) != registration.Asset.Pointer)
            {
                throw new InvalidOperationException(
                    $"ScriptableListManager did not re-append building '{registration.BuildingId}' deterministically.");
            }
            registration.UpdateNetworkIndex(networkIndex);
            RuntimeLog.Write(
                $"Building re-registered for the current content list: owner={ownerId}, " +
                $"id={registration.BuildingId}, networkIndex={networkIndex}.");
            return registration;
        }

        private int FindPointerIndex(nint list, nint pointer)
        {
            var count = GetCount(list);
            for (var index = 0; index < count; ++index)
            {
                if (GetAt(list, index) == pointer)
                {
                    return index;
                }
            }
            return -1;
        }

        private nint GetManager()
        {
            var method = RequireMethod(unsafeApi, _managerClass, "get_Instance", 0);
            var manager = unsafeApi.RuntimeInvoke(method, 0, 0);
            return manager != 0
                ? manager
                : throw new InvalidOperationException("ScriptableListManager.Instance is not ready in this scene.");
        }

        private nint GetList()
        {
            var list = unsafeApi.ReadObjectReference(
                GetManager(),
                RequireField(unsafeApi, _managerClass, "allBuildingItemSOs"));
            return list != 0
                ? list
                : throw new InvalidOperationException("ScriptableListManager building list is null.");
        }

        private int GetCount(nint list)
        {
            var listClass = unsafeApi.GetObjectClass(list);
            var boxed = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "get_Count", 0),
                list,
                0);
            var unboxed = unsafeApi.Unbox(boxed);
            return unboxed != 0
                ? Marshal.ReadInt32(unboxed)
                : throw new InvalidOperationException("List<T>.Count returned null.");
        }

        private unsafe nint GetAt(nint list, int index)
        {
            var listClass = unsafeApi.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void Add(nint list, nint asset)
        {
            var listClass = unsafeApi.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = asset;
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "Add", 1),
                list,
                (nint)arguments);
        }

        private unsafe void RemoveAt(nint list, int index)
        {
            var listClass = unsafeApi.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "RemoveAt", 1),
                list,
                (nint)arguments);
        }

        private string ReadBuildingId(nint asset)
        {
            var value = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _buildingClass, "GetBuildingID", 0),
                asset,
                0);
            return value == 0 ? string.Empty : unsafeApi.ReadString(value);
        }

        private void ValidateBuilding(UnityObject asset)
        {
            if (asset.IsNull)
            {
                throw new ArgumentException("Building ScriptableObject is null.");
            }
            if (!unsafeApi.IsAssignableFrom(_buildingClass, unsafeApi.GetObjectClass(asset.Pointer)))
            {
                throw new ArgumentException("Asset is not a T_BuildingItemSO.");
            }
        }

        private static void ValidateBuildingId(string buildingId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(buildingId);
            if (buildingId.Length > 100)
            {
                throw new ArgumentException("Building id must contain at most 100 characters.");
            }
        }

        private string ReadString(nint asset, string field)
        {
            var value = ReadReference(asset, field);
            return value == 0 ? string.Empty : unsafeApi.ReadString(value);
        }

        private void WriteString(nint asset, string field, string value) =>
            WriteReference(asset, field, unsafeApi.NewString(value));

        private nint ReadReference(nint asset, string field) =>
            unsafeApi.ReadObjectReference(asset, RequireField(unsafeApi, _buildingClass, field));

        private void WriteReference(nint asset, string field, nint value) =>
            unsafeApi.WriteObjectReference(asset, RequireField(unsafeApi, _buildingClass, field), value);

        private int ReadInt(nint asset, string field) =>
            unsafeApi.ReadInt32(asset, RequireField(unsafeApi, _buildingClass, field));

        private void WriteInt(nint asset, string field, int value) =>
            unsafeApi.WriteInt32(asset, RequireField(unsafeApi, _buildingClass, field), value);

        private float ReadFloat(nint asset, string field) =>
            unsafeApi.ReadSingle(asset, RequireField(unsafeApi, _buildingClass, field));

        private void WriteFloat(nint asset, string field, float value) =>
            unsafeApi.WriteSingle(asset, RequireField(unsafeApi, _buildingClass, field), value);

        private void WriteBool(nint asset, string field, bool value) =>
            unsafeApi.WriteBoolean(asset, RequireField(unsafeApi, _buildingClass, field), value);

        private bool ReadBool(nint asset, string field) =>
            unsafeApi.ReadBoolean(asset, RequireField(unsafeApi, _buildingClass, field));

        private void WriteOptionalBool(nint asset, string field, bool? value)
        {
            if (value is not null)
            {
                unsafeApi.WriteBoolean(
                    asset,
                    RequireField(unsafeApi, _buildingClass, field),
                    value.Value);
            }
        }

        private static void RequireNonNegative(int value, string parameter)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }

        private sealed class BuildingRegistration(
            string ownerId,
            string buildingId,
            UnityObject asset,
            int networkIndex) : IBuildingRegistration
        {
            public string OwnerId { get; } = ownerId;
            public string BuildingId { get; } = buildingId;
            public UnityObject Asset { get; } = asset;
            public int NetworkIndex { get; private set; } = networkIndex;

            public void UpdateNetworkIndex(int value) => NetworkIndex = value;
        }
    }

    private static nint RequireUnityClass(
        IUnsafeIl2CppApi unsafeApi,
        string assembly,
        string namespaze,
        string name)
    {
        var klass = unsafeApi.FindClass(assembly, namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Unity class '{namespaze}.{name}' was not found in {assembly}.");
    }
}
