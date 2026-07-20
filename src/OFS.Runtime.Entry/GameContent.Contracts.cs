using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class ContractRegistry : IContractRegistry
    {
        private static readonly Dictionary<string, ContractRegistration> OwnedContracts =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<nint, string> MutationOwners = new();
        private readonly Dictionary<nint, ContractDefinition> _transactionSnapshots = new();
        private readonly List<nint> _transactionMutationOrder = [];
        private readonly HashSet<nint> _transactionOwnershipAdded = [];
        private readonly List<UnityObject> _transactionClones = [];
        private readonly List<RegistrationAppend> _transactionRegistrations = [];
        private bool _transactionCommitted;

        private readonly string ownerId;
        private readonly IUnsafeIl2CppApi api;
        private readonly nint _contractClass;
        private readonly nint _companyClass;
        private readonly nint _itemClass;
        private readonly nint _managerClass;
        private readonly nint _materialClass;
        private readonly nint _materialsField;
        private readonly nint _materialsListClass;
        private readonly nint _materialItemField;
        private readonly nint _materialCountField;
        private readonly nint _unityObjectClass;

        public ContractRegistry(string ownerId, IUnsafeIl2CppApi api)
        {
            this.ownerId = ownerId;
            this.api = api;
            _contractClass = RequireClass(api, "ContractSO");
            _companyClass = RequireClass(api, "CompanySO");
            _itemClass = RequireClass(api, "T_ItemSO");
            _managerClass = RequireClass(api, "ComputerContractManager");
            _unityObjectClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            _materialClass = api.FindNestedClass(_contractClass, "ContractMaterial");
            if (_materialClass == 0)
            {
                throw new TypeLoadException("ContractSO.ContractMaterial was not found.");
            }
            _materialsField = RequireField(api, _contractClass, "requiredMaterials");
            _materialsListClass = api.GetFieldTypeClass(_materialsField);
            _materialItemField = RequireField(api, _materialClass, "item");
            _materialCountField = RequireField(api, _materialClass, "count");
        }

        public int Count
        {
            get
            {
                EnsureMainThread();
                return GetCount(GetList());
            }
        }

        public unsafe UnityObject Clone(UnityObject source, string newContractId)
        {
            EnsureMainThread();
            ValidateContract(source);
            ValidateContractId(newContractId);
            if (!FindById(newContractId).IsNull || OwnedContracts.ContainsKey(newContractId))
            {
                throw new InvalidOperationException(
                    $"Contract id '{newContractId}' is already registered.");
            }

            nint* arguments = stackalloc nint[1];
            arguments[0] = source.Pointer;
            var clone = api.RuntimeInvoke(
                RequireMethod(api, _unityObjectClass, "Instantiate", 1),
                0,
                (nint)arguments);
            if (clone == 0)
            {
                throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
            }
            WriteString(clone, "contractId", newContractId);
            var result = new UnityObject(clone);
            if (!_transactionCommitted) _transactionClones.Add(result);
            return result;
        }

        public UnityObject FindById(string contractId)
        {
            EnsureMainThread();
            ValidateContractId(contractId);
            var list = GetList();
            for (var index = 0; index < GetCount(list); ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0 && string.Equals(ReadContractId(asset), contractId, StringComparison.Ordinal))
                {
                    return new UnityObject(asset);
                }
            }
            return default;
        }

        public ContractDefinition Describe(UnityObject contractScriptableObject)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            var pointer = contractScriptableObject.Pointer;
            return new ContractDefinition(
                contractScriptableObject,
                ReadContractId(pointer),
                new UnityObject(ReadReference(pointer, "company")),
                ReadInt(pointer, "priceMin"),
                ReadInt(pointer, "priceMax"),
                ReadInt(pointer, "priceRoundingStep"),
                ReadInt(pointer, "deliveryDayMin"),
                ReadInt(pointer, "deliveryDayMax"),
                ReadMaterials(pointer),
                ReadInt(pointer, "requiredLevel"),
                (ContractTier)ReadInt(pointer, "tier"),
                ReadInt(pointer, "TierDecayLevelGap"));
        }

        public IReadOnlyList<ContractDefinition> GetAll()
        {
            EnsureMainThread();
            var list = GetList();
            var result = new List<ContractDefinition>(GetCount(list));
            for (var index = 0; index < GetCount(list); ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0) result.Add(Describe(new UnityObject(asset)));
            }
            return result;
        }

        public void Update(UnityObject contractScriptableObject, ContractPatch patch)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            var before = Describe(contractScriptableObject);
            var claim = ClaimMutation(contractScriptableObject.Pointer, before);
            try
            {
                ApplyUpdate(contractScriptableObject, patch, before);
            }
            catch
            {
                RestoreDefinition(before);
                CancelClaim(contractScriptableObject.Pointer, claim);
                throw;
            }
        }

        private void ApplyUpdate(
            UnityObject contractScriptableObject,
            ContractPatch patch,
            ContractDefinition before)
        {
            var pointer = contractScriptableObject.Pointer;
            if (patch.Company is not null)
            {
                ValidateCompany(patch.Company.Value);
                WriteReference(pointer, "company", patch.Company.Value.Pointer);
            }

            if (patch.PriceMin is not null || patch.PriceMax is not null)
            {
                var priceMin = patch.PriceMin ?? before.PriceMin;
                var priceMax = patch.PriceMax ?? before.PriceMax;
                RequireNonNegative(priceMin, nameof(patch.PriceMin));
                RequireNonNegative(priceMax, nameof(patch.PriceMax));
                if (priceMin > priceMax)
                {
                    throw new ArgumentException("Contract PriceMin cannot exceed PriceMax.", nameof(patch));
                }
                if (patch.PriceMin is not null) WriteInt(pointer, "priceMin", priceMin);
                if (patch.PriceMax is not null) WriteInt(pointer, "priceMax", priceMax);
            }

            if (patch.PriceRoundingStep is not null)
            {
                if (patch.PriceRoundingStep <= 0)
                    throw new ArgumentOutOfRangeException(nameof(patch.PriceRoundingStep));
                WriteInt(pointer, "priceRoundingStep", patch.PriceRoundingStep.Value);
            }

            if (patch.DeliveryDayMin is not null || patch.DeliveryDayMax is not null)
            {
                var dayMin = patch.DeliveryDayMin ?? before.DeliveryDayMin;
                var dayMax = patch.DeliveryDayMax ?? before.DeliveryDayMax;
                RequireNonNegative(dayMin, nameof(patch.DeliveryDayMin));
                RequireNonNegative(dayMax, nameof(patch.DeliveryDayMax));
                if (dayMin > dayMax)
                {
                    throw new ArgumentException(
                        "Contract DeliveryDayMin cannot exceed DeliveryDayMax.",
                        nameof(patch));
                }
                if (patch.DeliveryDayMin is not null) WriteInt(pointer, "deliveryDayMin", dayMin);
                if (patch.DeliveryDayMax is not null) WriteInt(pointer, "deliveryDayMax", dayMax);
            }

            if (patch.Materials is not null) ReplaceMaterials(pointer, patch.Materials);
            if (patch.RequiredLevel is not null)
            {
                RequireNonNegative(patch.RequiredLevel.Value, nameof(patch.RequiredLevel));
                WriteInt(pointer, "requiredLevel", patch.RequiredLevel.Value);
            }
            if (patch.Tier is not null)
            {
                if (!Enum.IsDefined(patch.Tier.Value))
                    throw new ArgumentOutOfRangeException(nameof(patch.Tier));
                WriteInt(pointer, "tier", (int)patch.Tier.Value);
            }
            if (patch.TierDecayLevelGap is not null)
            {
                RequireNonNegative(patch.TierDecayLevelGap.Value, nameof(patch.TierDecayLevelGap));
                WriteInt(pointer, "TierDecayLevelGap", patch.TierDecayLevelGap.Value);
            }
        }

        public IContractRegistration Register(UnityObject contractScriptableObject)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            if (!IsBuildingRegistrationWindowOpen)
            {
                throw new InvalidOperationException(
                    "Contracts may only be registered inside the ContentReady callback window.");
            }

            var contractId = ReadContractId(contractScriptableObject.Pointer);
            ValidateContractId(contractId);
            var list = GetList();
            if (OwnedContracts.TryGetValue(contractId, out var existing))
            {
                if (!string.Equals(existing.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase) ||
                    existing.Asset.Pointer != contractScriptableObject.Pointer)
                {
                    throw new InvalidOperationException(
                        $"Contract id '{contractId}' is already owned by mod '{existing.OwnerId}'.");
                }
                var current = FindPointerIndex(list, existing.Asset.Pointer);
                if (current >= 0)
                {
                    existing.UpdateIndex(current);
                    return existing;
                }
                Append(list, existing);
                if (!_transactionCommitted)
                    _transactionRegistrations.Add(new RegistrationAppend(existing, false));
                RefreshCacheIfReady();
                return existing;
            }
            if (!FindById(contractId).IsNull)
            {
                throw new InvalidOperationException(
                    $"Contract id '{contractId}' is already registered by the base game or an untracked source.");
            }

            var index = GetCount(list);
            Add(list, contractScriptableObject.Pointer);
            if (GetCount(list) != index + 1 || GetAt(list, index) != contractScriptableObject.Pointer)
            {
                throw new InvalidOperationException(
                    $"Contract registry did not append '{contractId}' deterministically.");
            }
            var registration = new ContractRegistration(ownerId, contractId, contractScriptableObject, index);
            OwnedContracts.Add(contractId, registration);
            if (!_transactionCommitted)
                _transactionRegistrations.Add(new RegistrationAppend(registration, true));
            try
            {
                RefreshCacheIfReady();
            }
            catch
            {
                RollbackRegistration(new RegistrationAppend(registration, true));
                _transactionRegistrations.RemoveAll(value => ReferenceEquals(value.Registration, registration));
                throw;
            }
            RuntimeLog.Write(
                $"Contract registered: owner={ownerId}, id={contractId}, index={index}.");
            return registration;
        }

        private void RestoreDefinition(ContractDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteReference(pointer, "company", definition.Company.Pointer);
            WriteInt(pointer, "priceMin", definition.PriceMin);
            WriteInt(pointer, "priceMax", definition.PriceMax);
            WriteInt(pointer, "priceRoundingStep", definition.PriceRoundingStep);
            WriteInt(pointer, "deliveryDayMin", definition.DeliveryDayMin);
            WriteInt(pointer, "deliveryDayMax", definition.DeliveryDayMax);
            ReplaceMaterials(
                pointer,
                definition.Materials.Select(value =>
                    new ContractMaterialPatch(value.Item, value.Count)).ToArray(),
                validate: false);
            WriteInt(pointer, "requiredLevel", definition.RequiredLevel);
            WriteInt(pointer, "tier", (int)definition.Tier);
            WriteInt(pointer, "TierDecayLevelGap", definition.TierDecayLevelGap);
        }

        private IReadOnlyList<ContractMaterialDefinition> ReadMaterials(nint contract)
        {
            var list = api.ReadObjectReference(contract, _materialsField);
            if (list == 0) return [];
            var result = new List<ContractMaterialDefinition>(GetCount(list));
            for (var index = 0; index < GetCount(list); ++index)
            {
                var material = GetAt(list, index);
                if (material == 0) continue;
                var item = api.ReadObjectReference(material, _materialItemField);
                result.Add(new ContractMaterialDefinition(
                    new UnityObject(item),
                    item == 0 ? string.Empty : ReadItemId(item),
                    api.ReadInt32(material, _materialCountField)));
            }
            return result;
        }

        private void ReplaceMaterials(
            nint contract,
            IReadOnlyList<ContractMaterialPatch> materials,
            bool validate = true)
        {
            if (validate && materials.Count > 100)
                throw new ArgumentException("A contract may contain at most 100 materials.");
            var list = NewConstructedObject(_materialsListClass);
            api.WriteObjectReference(contract, _materialsField, list);
            foreach (var definition in materials)
            {
                if (validate)
                {
                    ArgumentNullException.ThrowIfNull(definition);
                    ValidateItem(definition.Item);
                    if (definition.Count <= 0)
                        throw new ArgumentOutOfRangeException(nameof(materials));
                }
                var material = NewConstructedObject(_materialClass);
                Add(list, material);
                api.WriteObjectReference(material, _materialItemField, definition.Item.Pointer);
                api.WriteInt32(material, _materialCountField, definition.Count);
            }
        }

        private nint NewConstructedObject(nint klass)
        {
            var instance = api.NewObject(klass);
            _ = api.RuntimeInvoke(RequireMethod(api, klass, ".ctor", 0), instance, 0);
            return instance;
        }

        private MutationClaim ClaimMutation(nint pointer, ContractDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Contract '{before.ContractId}' is already mutated by mod '{owner}'.");
                }
            }
            else
            {
                MutationOwners.Add(pointer, ownerId);
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

        internal void BeginTransaction()
        {
            if (!_transactionCommitted)
                throw new InvalidOperationException("A contract content transaction is already active.");
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
            for (var index = _transactionRegistrations.Count - 1; index >= 0; --index)
                Try(() => RollbackRegistration(_transactionRegistrations[index]));
            if (_transactionRegistrations.Count != 0) Try(RefreshCacheIfReady);
            for (var index = _transactionMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _transactionMutationOrder[index];
                if (!_transactionSnapshots.TryGetValue(pointer, out var snapshot)) continue;
                try { RestoreDefinition(snapshot); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                finally
                {
                    if (_transactionOwnershipAdded.Contains(pointer)) MutationOwners.Remove(pointer);
                    _transactionSnapshots.Remove(pointer);
                }
            }
            for (var index = _transactionClones.Count - 1; index >= 0; --index)
                Try(() => DestroyClone(_transactionClones[index]));
            ClearTransaction();
            _transactionCommitted = true;
            if (failures is not null)
                throw new AggregateException("Contract content rollback was incomplete.", failures);

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }

        private void ClearTransaction()
        {
            _transactionSnapshots.Clear();
            _transactionMutationOrder.Clear();
            _transactionOwnershipAdded.Clear();
            _transactionClones.Clear();
            _transactionRegistrations.Clear();
        }

        private void RollbackRegistration(RegistrationAppend append)
        {
            var registration = append.Registration;
            var list = GetList();
            var index = FindPointerIndex(list, registration.Asset.Pointer);
            if (index >= 0)
            {
                var last = GetCount(list) - 1;
                if (index != last)
                    throw new InvalidOperationException(
                        $"Cannot roll back contract '{registration.ContractId}' at non-tail index {index}.");
                RemoveAt(list, index);
            }
            if (append.OwnershipAdded &&
                OwnedContracts.TryGetValue(registration.ContractId, out var owned) &&
                ReferenceEquals(owned, registration))
            {
                OwnedContracts.Remove(registration.ContractId);
            }
            registration.UpdateIndex(-1);
        }

        private ContractRegistration Append(nint list, ContractRegistration registration)
        {
            var index = GetCount(list);
            Add(list, registration.Asset.Pointer);
            if (GetCount(list) != index + 1 || GetAt(list, index) != registration.Asset.Pointer)
                throw new InvalidOperationException(
                    $"Contract registry did not re-append '{registration.ContractId}' deterministically.");
            registration.UpdateIndex(index);
            return registration;
        }

        private void RefreshCacheIfReady()
        {
            var instance = api.RuntimeInvoke(
                RequireMethod(api, _managerClass, "get_Instance", 0),
                0,
                0);
            if (instance == 0) return;
            _ = api.RuntimeInvoke(
                RequireMethod(api, _managerClass, "BuildConfigCaches", 0),
                instance,
                0);
        }

        private nint GetList()
        {
            var list = api.RuntimeInvoke(
                RequireMethod(api, _managerClass, "get_allContractConfigs", 0),
                0,
                0);
            return list != 0
                ? list
                : throw new InvalidOperationException("The global ContractSO list is unavailable.");
        }

        private int FindPointerIndex(nint list, nint pointer)
        {
            for (var index = 0; index < GetCount(list); ++index)
                if (GetAt(list, index) == pointer) return index;
            return -1;
        }

        private int GetCount(nint list)
        {
            var listClass = api.GetObjectClass(list);
            var boxed = api.RuntimeInvoke(RequireMethod(api, listClass, "get_Count", 0), list, 0);
            var value = api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("Contract List<T>.Count returned null.");
        }

        private unsafe nint GetAt(nint list, int index)
        {
            var listClass = api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return api.RuntimeInvoke(
                RequireMethod(api, listClass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void Add(nint list, nint item)
        {
            var listClass = api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = item;
            _ = api.RuntimeInvoke(RequireMethod(api, listClass, "Add", 1), list, (nint)arguments);
        }

        private unsafe void RemoveAt(nint list, int index)
        {
            var listClass = api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            _ = api.RuntimeInvoke(
                RequireMethod(api, listClass, "RemoveAt", 1),
                list,
                (nint)arguments);
        }

        private unsafe void DestroyClone(UnityObject clone)
        {
            if (clone.IsNull) return;
            nint* arguments = stackalloc nint[1];
            arguments[0] = clone.Pointer;
            _ = api.RuntimeInvoke(
                RequireMethod(api, _unityObjectClass, "Destroy", 1),
                0,
                (nint)arguments);
        }

        private string ReadContractId(nint contract)
        {
            var value = api.RuntimeInvoke(
                RequireMethod(api, _contractClass, "get_ContractId", 0),
                contract,
                0);
            return value == 0 ? string.Empty : api.ReadString(value);
        }

        private string ReadItemId(nint item)
        {
            var value = api.RuntimeInvoke(
                RequireMethod(api, _itemClass, "GetItemID", 0),
                item,
                0);
            return value == 0 ? string.Empty : api.ReadString(value);
        }

        private nint ReadReference(nint asset, string field) =>
            api.ReadObjectReference(asset, RequireField(api, _contractClass, field));

        private void WriteReference(nint asset, string field, nint value) =>
            api.WriteObjectReference(asset, RequireField(api, _contractClass, field), value);

        private void WriteString(nint asset, string field, string value) =>
            WriteReference(asset, field, api.NewString(value));

        private int ReadInt(nint asset, string field) =>
            api.ReadInt32(asset, RequireField(api, _contractClass, field));

        private void WriteInt(nint asset, string field, int value) =>
            api.WriteInt32(asset, RequireField(api, _contractClass, field), value);

        private void ValidateContract(UnityObject asset)
        {
            if (asset.IsNull || !api.IsAssignableFrom(_contractClass, api.GetObjectClass(asset.Pointer)))
                throw new ArgumentException("Asset is not a ContractSO.");
        }

        private void ValidateCompany(UnityObject asset)
        {
            if (asset.IsNull || !api.IsAssignableFrom(_companyClass, api.GetObjectClass(asset.Pointer)))
                throw new ArgumentException("Company is not a CompanySO.");
        }

        private void ValidateItem(UnityObject asset)
        {
            if (asset.IsNull || !api.IsAssignableFrom(_itemClass, api.GetObjectClass(asset.Pointer)))
                throw new ArgumentException("Contract material is not a T_ItemSO.");
        }

        private static void ValidateContractId(string contractId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
            if (contractId.Length > 100)
                throw new ArgumentException("Contract id must contain at most 100 characters.");
        }

        private static void RequireNonNegative(int value, string parameter)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(parameter);
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);
        private readonly record struct RegistrationAppend(
            ContractRegistration Registration,
            bool OwnershipAdded);

        private sealed class ContractRegistration(
            string ownerId,
            string contractId,
            UnityObject asset,
            int index) : IContractRegistration
        {
            public string OwnerId { get; } = ownerId;
            public string ContractId { get; } = contractId;
            public UnityObject Asset { get; } = asset;
            public int Index { get; private set; } = index;
            public void UpdateIndex(int value) => Index = value;
        }
    }
}
