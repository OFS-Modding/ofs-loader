using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class OffsiteContractRegistry : IOffsiteContractRegistry
    {
        private static readonly Dictionary<string, OffsiteRegistration> Owned =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<nint, string> MutationOwners = [];

        private readonly string _ownerId;
        private readonly IUnsafeIl2CppApi _api;
        private readonly IModLogger _logger;
        private readonly nint _contractClass;
        private readonly nint _managerClass;
        private readonly nint _generationConfigClass;
        private readonly nint _propertyClass;
        private readonly nint _itemClass;
        private readonly nint _employeeStatClass;
        private readonly nint _unityObjectClass;
        private readonly nint _scriptableObjectClass;
        private readonly List<OffsiteRegistration> _registrations = [];
        private readonly Dictionary<nint, OffsiteContractDefinition> _transactionSnapshots = [];
        private readonly List<nint> _transactionMutationOrder = [];
        private readonly HashSet<nint> _transactionOwnershipAdded = [];
        private readonly List<UnityObject> _transactionClones = [];
        private readonly List<OffsiteRegistration> _transactionRegistrations = [];
        private bool _transactionCommitted;
        private int _frame;
        private nint _observedManager;

        public OffsiteContractRegistry(
            string ownerId,
            IUnsafeIl2CppApi api,
            IModEvents events,
            IModLogger logger)
        {
            _ownerId = ownerId;
            _api = api;
            _logger = logger;
            _contractClass = RequireClass(api, "OffsiteContractSO");
            _managerClass = RequireClass(api, "OffsiteContractManager");
            _generationConfigClass = RequireClass(api, "OffsiteContractGenerationConfigSO");
            _propertyClass = RequireClass(api, "PropertyConfigSO");
            _itemClass = RequireClass(api, "T_ItemSO");
            _employeeStatClass = RequireClass(api, "EmployeeStatType");
            _unityObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            if (_unityObjectClass == 0)
                throw new TypeLoadException("UnityEngine.Object was not found.");
            _scriptableObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "ScriptableObject");
            if (_scriptableObjectClass == 0)
                throw new TypeLoadException("UnityEngine.ScriptableObject was not found.");
            events.FrameUpdate += OnFrameUpdate;
        }

        public bool IsAvailable
        {
            get
            {
                EnsureMainThread();
                return TryGetPool(out _, out _);
            }
        }

        public int Count
        {
            get
            {
                EnsureMainThread();
                if (!TryGetPool(out _, out var pool)) return Owned.Count;
                var count = GetCount(pool);
                foreach (var registration in Owned.Values)
                    if (FindPointerIndex(pool, registration.Asset.Pointer) < 0) ++count;
                return count;
            }
        }

        public unsafe UnityObject Clone(UnityObject source, string newContractId)
        {
            EnsureMainThread();
            ValidateContract(source);
            ValidateId(newContractId);
            if (!FindById(newContractId).IsNull || Owned.ContainsKey(newContractId))
                throw new InvalidOperationException(
                    $"Offsite contract id '{newContractId}' is already registered or pending.");

            nint* arguments = stackalloc nint[1];
            arguments[0] = source.Pointer;
            var clone = _api.RuntimeInvoke(
                RequireMethod(_api, _unityObjectClass, "Instantiate", 1),
                0,
                (nint)arguments);
            if (clone == 0)
                throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
            WriteString(clone, _contractClass, "contractId", newContractId);
            var result = new UnityObject(clone);
            if (!_transactionCommitted) _transactionClones.Add(result);
            return result;
        }

        public UnityObject Create(string contractId)
        {
            EnsureMainThread();
            ValidateId(contractId);
            if (!FindById(contractId).IsNull || Owned.ContainsKey(contractId))
                throw new InvalidOperationException(
                    $"Offsite contract id '{contractId}' is already registered or pending.");
            var create = _api.FindMethodBySignature(
                _scriptableObjectClass,
                "CreateInstance",
                ["System.Type"]);
            if (create == 0)
                throw new MissingMethodException(
                    "UnityEngine.ScriptableObject.CreateInstance(System.Type)");
            var asset = _api.Invoke(
                create,
                0,
                Il2CppArgument.FromReference(_api.GetTypeObject(_contractClass)));
            if (asset == 0)
                throw new InvalidOperationException("ScriptableObject.CreateInstance returned null.");
            WriteString(asset, _contractClass, "contractId", contractId);
            var result = new UnityObject(asset);
            if (!_transactionCommitted) _transactionClones.Add(result);
            return result;
        }

        public UnityObject FindById(string contractId)
        {
            EnsureMainThread();
            ValidateId(contractId);
            if (TryGetPool(out _, out var pool))
            {
                var count = GetCount(pool);
                for (var index = 0; index < count; ++index)
                {
                    var asset = GetAt(pool, index);
                    if (asset != 0 && string.Equals(
                            ReadId(asset),
                            contractId,
                            StringComparison.Ordinal))
                        return new UnityObject(asset);
                }
            }
            return Owned.TryGetValue(contractId, out var pending)
                ? pending.Asset
                : default;
        }

        public OffsiteContractDefinition Describe(UnityObject contractScriptableObject)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            return DescribeCore(contractScriptableObject);
        }

        public IReadOnlyList<OffsiteContractDefinition> GetAll()
        {
            EnsureMainThread();
            var result = new List<OffsiteContractDefinition>();
            var seen = new HashSet<nint>();
            if (TryGetPool(out _, out var pool))
            {
                var count = GetCount(pool);
                result.Capacity = count + Owned.Count;
                for (var index = 0; index < count; ++index)
                {
                    var asset = GetAt(pool, index);
                    if (asset != 0 && seen.Add(asset))
                        result.Add(DescribeCore(new UnityObject(asset)));
                }
            }
            foreach (var registration in Owned.Values)
                if (seen.Add(registration.Asset.Pointer))
                    result.Add(DescribeCore(registration.Asset));
            return result;
        }

        public void Update(UnityObject contractScriptableObject, OffsiteContractPatch patch)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            var before = DescribeCore(contractScriptableObject);
            var claim = ClaimMutation(contractScriptableObject.Pointer, before);
            try
            {
                ApplyPatch(contractScriptableObject.Pointer, patch, before);
                RefreshCacheIfReady();
            }
            catch
            {
                Restore(before);
                RefreshCacheIfReady();
                CancelClaim(contractScriptableObject.Pointer, claim);
                throw;
            }
        }

        public IOffsiteContractRegistration Register(UnityObject contractScriptableObject)
        {
            EnsureMainThread();
            ValidateContract(contractScriptableObject);
            if (!IsBuildingRegistrationWindowOpen)
                throw new InvalidOperationException(
                    "Offsite contracts must be registered during ContentReady so deferred " +
                    "Factory materialization remains transactional.");

            var contractId = ReadId(contractScriptableObject.Pointer);
            ValidateId(contractId);
            ValidateDefinition(DescribeCore(contractScriptableObject));
            if (Owned.TryGetValue(contractId, out var existing))
            {
                if (!string.Equals(existing.OwnerId, _ownerId, StringComparison.OrdinalIgnoreCase) ||
                    existing.Asset.Pointer != contractScriptableObject.Pointer)
                    throw new InvalidOperationException(
                        $"Offsite contract '{contractId}' is already owned by mod '{existing.OwnerId}'.");
                if (!_registrations.Contains(existing)) _registrations.Add(existing);
                return existing;
            }

            if (TryFindPoolEntry(contractId, out _))
                throw new InvalidOperationException(
                    $"Offsite contract '{contractId}' already exists in the vanilla generation pool.");

            var registration = new OffsiteRegistration(
                _ownerId,
                contractId,
                contractScriptableObject);
            Owned.Add(contractId, registration);
            _registrations.Add(registration);
            if (!_transactionCommitted) _transactionRegistrations.Add(registration);
            RuntimeLog.Write(
                $"Offsite contract queued: owner={_ownerId}, id={contractId}; " +
                "waiting for OffsiteContractManager.");
            return registration;
        }

        internal void BeginTransaction()
        {
            if (!_transactionCommitted)
                throw new InvalidOperationException(
                    "An offsite contract content transaction is already active.");
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
            for (var index = _transactionMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _transactionMutationOrder[index];
                if (!_transactionSnapshots.TryGetValue(pointer, out var snapshot)) continue;
                try { Restore(snapshot); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                finally
                {
                    if (_transactionOwnershipAdded.Contains(pointer)) MutationOwners.Remove(pointer);
                    _transactionSnapshots.Remove(pointer);
                }
            }
            if (_transactionMutationOrder.Count != 0) Try(RefreshCacheIfReady);
            for (var index = _transactionClones.Count - 1; index >= 0; --index)
                Try(() => DestroyClone(_transactionClones[index]));
            ClearTransaction();
            _transactionCommitted = true;
            if (failures is not null)
                throw new AggregateException("Offsite contract rollback was incomplete.", failures);

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }

        private void OnFrameUpdate(FrameEvent _)
        {
            if (_registrations.Count == 0 || ++_frame % 30 != 0) return;
            try
            {
                MaterializePending();
            }
            catch (Exception exception)
            {
                var message = exception.Message;
                var changed = false;
                foreach (var registration in _registrations.Where(value => !value.IsMaterialized))
                {
                    if (!string.Equals(
                            registration.MaterializationError,
                            message,
                            StringComparison.Ordinal))
                    {
                        registration.SetError(message);
                        changed = true;
                    }
                }
                if (changed)
                    _logger.Error(exception, "Deferred offsite contract materialization failed.");
            }
        }

        private void MaterializePending()
        {
            if (_transactionCommitted is false || !TryGetPool(out var manager, out var pool)) return;
            if (_observedManager != manager)
            {
                _observedManager = manager;
                foreach (var registration in _registrations) registration.SetError(null);
            }

            var appended = new List<OffsiteRegistration>();
            try
            {
                foreach (var registration in _registrations)
                {
                    ValidateDefinition(DescribeCore(registration.Asset));
                    var pointerIndex = FindPointerIndex(pool, registration.Asset.Pointer);
                    if (pointerIndex >= 0)
                    {
                        registration.MarkMaterialized(pointerIndex);
                        continue;
                    }

                    var duplicate = FindIdIndex(pool, registration.ContractId);
                    if (duplicate >= 0)
                        throw new InvalidOperationException(
                            $"Offsite contract id '{registration.ContractId}' collides with " +
                            $"pool index {duplicate} during deferred materialization.");

                    var index = GetCount(pool);
                    Add(pool, registration.Asset.Pointer);
                    if (GetCount(pool) != index + 1 ||
                        GetAt(pool, index) != registration.Asset.Pointer)
                        throw new InvalidOperationException(
                            $"Offsite pool did not append '{registration.ContractId}' deterministically.");
                    registration.MarkMaterialized(index);
                    appended.Add(registration);
                }

                if (appended.Count != 0) RefreshCache(manager);
                foreach (var registration in _registrations.Where(value => value.IsMaterialized))
                    EnsureCacheEntry(manager, registration);
                foreach (var registration in appended)
                {
                    registration.SetError(null);
                    RuntimeLog.Write(
                        $"Offsite contract materialized: owner={registration.OwnerId}, " +
                        $"id={registration.ContractId}, index={registration.Index}.");
                }
            }
            catch
            {
                for (var index = appended.Count - 1; index >= 0; --index)
                {
                    var registration = appended[index];
                    var poolIndex = FindPointerIndex(pool, registration.Asset.Pointer);
                    if (poolIndex < 0) continue;
                    if (poolIndex != GetCount(pool) - 1)
                        throw new InvalidOperationException(
                            $"Deferred rollback cannot remove non-tail offsite contract " +
                            $"'{registration.ContractId}'.");
                    RemoveAt(pool, poolIndex);
                    registration.MarkPending();
                }
                throw;
            }
        }

        private OffsiteContractDefinition DescribeCore(UnityObject asset)
        {
            var pointer = asset.Pointer;
            var duration = ReadVector2Int(pointer, "durationHoursRange");
            var amount = ReadVector2Int(pointer, "amountPerHour");
            return new OffsiteContractDefinition(
                asset,
                ReadId(pointer),
                ReadReference(pointer, _contractClass, "propertyConfig"),
                ReadInt(pointer, _contractClass, "requiredLevel"),
                duration.X,
                duration.Y,
                ReadReferenceList(pointer, "itemPool"),
                amount.X,
                amount.Y,
                ReadInt(pointer, _contractClass, "rewardItemCount"),
                ReadProfiles(pointer),
                ReadInt(pointer, _contractClass, "requiredMinerCount"));
        }

        private void ApplyPatch(
            nint pointer,
            OffsiteContractPatch patch,
            OffsiteContractDefinition before)
        {
            var property = patch.Property ?? before.Property;
            var requiredLevel = patch.RequiredLevel ?? before.RequiredLevel;
            var durationMin = patch.DurationHoursMin ?? before.DurationHoursMin;
            var durationMax = patch.DurationHoursMax ?? before.DurationHoursMax;
            var itemPool = patch.ItemPool ?? before.ItemPool;
            var amountMin = patch.AmountPerHourMin ?? before.AmountPerHourMin;
            var amountMax = patch.AmountPerHourMax ?? before.AmountPerHourMax;
            var rewardCount = patch.RewardItemCount ?? before.RewardItemCount;
            var profiles = patch.MatchingProfiles ?? before.MatchingProfiles;
            var minerCount = patch.RequiredMinerCount ?? before.RequiredMinerCount;

            ValidateProperty(property);
            if (requiredLevel < 1) throw new ArgumentOutOfRangeException(nameof(patch.RequiredLevel));
            ValidatePositiveRange(durationMin, durationMax, "duration hours");
            ValidateItemPool(itemPool);
            ValidatePositiveRange(amountMin, amountMax, "amount per hour");
            if (rewardCount < 1 || rewardCount > itemPool.Count)
                throw new ArgumentOutOfRangeException(nameof(patch.RewardItemCount));
            ValidateProfiles(profiles);
            if (minerCount < 1) throw new ArgumentOutOfRangeException(nameof(patch.RequiredMinerCount));

            if (patch.Property is not null)
                WriteReference(pointer, _contractClass, "propertyConfig", property);
            if (patch.RequiredLevel is not null)
                WriteInt(pointer, _contractClass, "requiredLevel", requiredLevel);
            if (patch.DurationHoursMin is not null || patch.DurationHoursMax is not null)
                WriteVector2Int(pointer, "durationHoursRange", durationMin, durationMax);
            if (patch.ItemPool is not null) ReplaceItemPool(pointer, itemPool);
            if (patch.AmountPerHourMin is not null || patch.AmountPerHourMax is not null)
                WriteVector2Int(pointer, "amountPerHour", amountMin, amountMax);
            if (patch.RewardItemCount is not null)
                WriteInt(pointer, _contractClass, "rewardItemCount", rewardCount);
            if (patch.MatchingProfiles is not null) ReplaceProfiles(pointer, profiles);
            if (patch.RequiredMinerCount is not null)
                WriteInt(pointer, _contractClass, "requiredMinerCount", minerCount);
        }

        private void Restore(OffsiteContractDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteReference(pointer, _contractClass, "propertyConfig", definition.Property);
            WriteInt(pointer, _contractClass, "requiredLevel", definition.RequiredLevel);
            WriteVector2Int(
                pointer,
                "durationHoursRange",
                definition.DurationHoursMin,
                definition.DurationHoursMax);
            ReplaceItemPool(pointer, definition.ItemPool, validate: false);
            WriteVector2Int(
                pointer,
                "amountPerHour",
                definition.AmountPerHourMin,
                definition.AmountPerHourMax);
            WriteInt(pointer, _contractClass, "rewardItemCount", definition.RewardItemCount);
            ReplaceProfiles(pointer, definition.MatchingProfiles, validate: false);
            WriteInt(pointer, _contractClass, "requiredMinerCount", definition.RequiredMinerCount);
        }

        private MutationClaim ClaimMutation(nint pointer, OffsiteContractDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, _ownerId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Offsite contract '{before.ContractId}' is already mutated by mod '{owner}'.");
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

        private void RollbackRegistration(OffsiteRegistration registration)
        {
            if (TryGetPool(out var manager, out var pool))
            {
                var index = FindPointerIndex(pool, registration.Asset.Pointer);
                if (index >= 0)
                {
                    if (index != GetCount(pool) - 1)
                        throw new InvalidOperationException(
                            $"Cannot roll back offsite contract '{registration.ContractId}' " +
                            $"at non-tail index {index}.");
                    RemoveAt(pool, index);
                    RefreshCache(manager);
                }
            }
            if (Owned.TryGetValue(registration.ContractId, out var current) &&
                ReferenceEquals(current, registration))
                Owned.Remove(registration.ContractId);
            _registrations.Remove(registration);
            registration.MarkPending();
        }

        private bool TryGetPool(out nint manager, out nint pool)
        {
            manager = _api.RuntimeInvoke(
                RequireMethod(_api, _managerClass, "get_Instance", 0),
                0,
                0);
            if (manager == 0)
            {
                pool = 0;
                return false;
            }
            var config = _api.ReadObjectReference(
                manager,
                RequireField(_api, _managerClass, "generationConfig"));
            if (config == 0)
            {
                pool = 0;
                return false;
            }
            pool = _api.ReadObjectReference(
                config,
                RequireField(_api, _generationConfigClass, "allContracts"));
            return pool != 0;
        }

        private bool TryFindPoolEntry(string contractId, out nint asset)
        {
            asset = 0;
            if (!TryGetPool(out _, out var pool)) return false;
            var index = FindIdIndex(pool, contractId);
            if (index < 0) return false;
            asset = GetAt(pool, index);
            return asset != 0;
        }

        private int FindIdIndex(nint pool, string contractId)
        {
            var count = GetCount(pool);
            for (var index = 0; index < count; ++index)
            {
                var asset = GetAt(pool, index);
                if (asset != 0 && string.Equals(
                        ReadId(asset),
                        contractId,
                        StringComparison.Ordinal)) return index;
            }
            return -1;
        }

        private int FindPointerIndex(nint pool, nint pointer)
        {
            var count = GetCount(pool);
            for (var index = 0; index < count; ++index)
                if (GetAt(pool, index) == pointer) return index;
            return -1;
        }

        private void RefreshCacheIfReady()
        {
            if (TryGetPool(out var manager, out _)) RefreshCache(manager);
        }

        private void RefreshCache(nint manager) => _ = _api.RuntimeInvoke(
            RequireMethod(_api, _managerClass, "BuildConfigCache", 0),
            manager,
            0);

        private void EnsureCacheEntry(nint manager, OffsiteRegistration registration)
        {
            var key = _api.NewString(registration.ContractId);
            var lookup = _api.Invoke(
                RequireMethod(_api, _managerClass, "GetContractConfig", 1),
                manager,
                Il2CppArgument.FromReference(key));
            if (lookup == registration.Asset.Pointer) return;
            if (lookup != 0)
                throw new InvalidOperationException(
                    $"Offsite cache id '{registration.ContractId}' belongs to another asset.");

            var cache = _api.ReadObjectReference(
                manager,
                RequireField(_api, _managerClass, "_contractConfigCache"));
            if (cache == 0)
                throw new InvalidOperationException(
                    "OffsiteContractManager._contractConfigCache is null after BuildConfigCache.");
            var cacheClass = _api.GetObjectClass(cache);
            _ = _api.Invoke(
                RequireMethod(_api, cacheClass, "set_Item", 2),
                cache,
                Il2CppArgument.FromReference(key),
                Il2CppArgument.FromReference(registration.Asset.Pointer));
            lookup = _api.Invoke(
                RequireMethod(_api, _managerClass, "GetContractConfig", 1),
                manager,
                Il2CppArgument.FromReference(key));
            if (lookup != registration.Asset.Pointer)
                throw new InvalidOperationException(
                    $"Offsite cache rejected '{registration.ContractId}' after direct dictionary repair.");
            if (!registration.CacheRepairLogged)
            {
                registration.CacheRepairLogged = true;
                RuntimeLog.Write(
                    $"Offsite cache repaired after vanilla rebuild skipped custom asset: " +
                    $"owner={registration.OwnerId}, id={registration.ContractId}.");
            }
        }

        private string ReadId(nint asset) =>
            ReadString(asset, _contractClass, "contractId");

        private IReadOnlyList<UnityObject> ReadReferenceList(nint contract, string field)
        {
            var list = _api.ReadObjectReference(contract, RequireField(_api, _contractClass, field));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<UnityObject>(count);
            for (var index = 0; index < count; ++index)
                result.Add(new UnityObject(GetAt(list, index)));
            return result;
        }

        private void ReplaceItemPool(
            nint contract,
            IReadOnlyList<UnityObject> values,
            bool validate = true)
        {
            if (validate) ValidateItemPool(values);
            var field = RequireField(_api, _contractClass, "itemPool");
            var listClass = _api.GetFieldTypeClass(field);
            var list = NewConstructedObject(listClass);
            _api.WriteObjectReference(contract, field, list);
            foreach (var value in values) Add(list, value.Pointer);
        }

        private IReadOnlyList<EmployeeStatKind> ReadProfiles(nint contract)
        {
            var array = _api.ReadObjectReference(
                contract,
                RequireField(_api, _contractClass, "matchingProfile"));
            if (array == 0) return [];
            var arrayClass = _api.GetObjectClass(array);
            var getValue = RequireMethod(_api, arrayClass, "GetValue", 1);
            var length = checked((int)_api.GetArrayLength(array));
            var result = new List<EmployeeStatKind>(length);
            for (var index = 0; index < length; ++index)
            {
                var boxed = _api.Invoke(
                    getValue,
                    array,
                    Il2CppArgument.FromInt32(index));
                var value = _api.Unbox(boxed);
                if (value == 0) throw new InvalidDataException("EmployeeStatType[] returned null.");
                result.Add((EmployeeStatKind)Marshal.ReadInt32(value));
            }
            return result;
        }

        private unsafe void ReplaceProfiles(
            nint contract,
            IReadOnlyList<EmployeeStatKind> values,
            bool validate = true)
        {
            if (validate) ValidateProfiles(values);
            var array = _api.NewArray(_employeeStatClass, checked((nuint)values.Count));
            var arrayClass = _api.GetObjectClass(array);
            var setValue = RequireMethod(_api, arrayClass, "SetValue", 2);
            for (var index = 0; index < values.Count; ++index)
            {
                var raw = (int)values[index];
                var boxed = _api.BoxValue(_employeeStatClass, (nint)(&raw));
                _ = _api.Invoke(
                    setValue,
                    array,
                    Il2CppArgument.FromReference(boxed),
                    Il2CppArgument.FromInt32(index));
            }
            _api.WriteObjectReference(
                contract,
                RequireField(_api, _contractClass, "matchingProfile"),
                array);
        }

        private unsafe (int X, int Y) ReadVector2Int(nint contract, string field)
        {
            int* values = stackalloc int[2];
            _api.GetFieldValue(
                contract,
                RequireField(_api, _contractClass, field),
                (nint)values);
            return (values[0], values[1]);
        }

        private unsafe void WriteVector2Int(nint contract, string field, int x, int y)
        {
            int* values = stackalloc int[2];
            values[0] = x;
            values[1] = y;
            _api.SetFieldValue(
                contract,
                RequireField(_api, _contractClass, field),
                (nint)values);
        }

        private nint NewConstructedObject(nint klass)
        {
            var result = _api.NewObject(klass);
            _ = _api.RuntimeInvoke(RequireMethod(_api, klass, ".ctor", 0), result, 0);
            return result;
        }

        private int GetCount(nint list)
        {
            var klass = _api.GetObjectClass(list);
            var boxed = _api.RuntimeInvoke(RequireMethod(_api, klass, "get_Count", 0), list, 0);
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("Offsite List<T>.Count returned null.");
        }

        private unsafe nint GetAt(nint list, int index)
        {
            var klass = _api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return _api.RuntimeInvoke(
                RequireMethod(_api, klass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void Add(nint list, nint value)
        {
            var klass = _api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = value;
            _ = _api.RuntimeInvoke(RequireMethod(_api, klass, "Add", 1), list, (nint)arguments);
        }

        private unsafe void RemoveAt(nint list, int index)
        {
            var klass = _api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, klass, "RemoveAt", 1),
                list,
                (nint)arguments);
        }

        private string ReadString(nint instance, nint klass, string field)
        {
            var value = _api.ReadObjectReference(instance, RequireField(_api, klass, field));
            return value == 0 ? string.Empty : _api.ReadString(value);
        }

        private void WriteString(nint instance, nint klass, string field, string value) =>
            _api.WriteObjectReference(
                instance,
                RequireField(_api, klass, field),
                _api.NewString(value));

        private UnityObject ReadReference(nint instance, nint klass, string field) =>
            new(_api.ReadObjectReference(instance, RequireField(_api, klass, field)));

        private void WriteReference(
            nint instance,
            nint klass,
            string field,
            UnityObject value) =>
            _api.WriteObjectReference(instance, RequireField(_api, klass, field), value.Pointer);

        private int ReadInt(nint instance, nint klass, string field) =>
            _api.ReadInt32(instance, RequireField(_api, klass, field));

        private void WriteInt(nint instance, nint klass, string field, int value) =>
            _api.WriteInt32(instance, RequireField(_api, klass, field), value);

        private void ValidateContract(UnityObject asset)
        {
            if (asset.IsNull ||
                !_api.IsAssignableFrom(_contractClass, _api.GetObjectClass(asset.Pointer)))
                throw new ArgumentException("Asset is not an OffsiteContractSO.");
        }

        private void ValidateProperty(UnityObject property)
        {
            if (property.IsNull ||
                !_api.IsAssignableFrom(_propertyClass, _api.GetObjectClass(property.Pointer)))
                throw new ArgumentException("Offsite property is not a PropertyConfigSO.");
        }

        private void ValidateItemPool(IReadOnlyList<UnityObject> values)
        {
            if (values.Count is < 1 or > 1000)
                throw new ArgumentException("Offsite item pool must contain between 1 and 1000 items.");
            if (values.Select(value => value.Pointer).Distinct().Count() != values.Count)
                throw new ArgumentException("Offsite item pool must not contain duplicates.");
            foreach (var value in values)
                if (value.IsNull ||
                    !_api.IsAssignableFrom(_itemClass, _api.GetObjectClass(value.Pointer)))
                    throw new ArgumentException("Offsite item pool contains a non-T_ItemSO asset.");
        }

        private void ValidateDefinition(OffsiteContractDefinition definition)
        {
            ValidateId(definition.ContractId);
            ValidateProperty(definition.Property);
            if (definition.RequiredLevel < 1)
                throw new ArgumentOutOfRangeException(nameof(definition.RequiredLevel));
            ValidatePositiveRange(
                definition.DurationHoursMin,
                definition.DurationHoursMax,
                "duration hours");
            ValidateItemPool(definition.ItemPool);
            ValidatePositiveRange(
                definition.AmountPerHourMin,
                definition.AmountPerHourMax,
                "amount per hour");
            if (definition.RewardItemCount < 1 ||
                definition.RewardItemCount > definition.ItemPool.Count)
                throw new ArgumentOutOfRangeException(nameof(definition.RewardItemCount));
            ValidateProfiles(definition.MatchingProfiles);
            if (definition.RequiredMinerCount < 1)
                throw new ArgumentOutOfRangeException(nameof(definition.RequiredMinerCount));
        }

        private static void ValidateProfiles(IReadOnlyList<EmployeeStatKind> values)
        {
            if (values.Count > Enum.GetValues<EmployeeStatKind>().Length ||
                values.Distinct().Count() != values.Count)
                throw new ArgumentException("Matching employee profiles must be unique.");
            foreach (var value in values)
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(values));
        }

        private static void ValidatePositiveRange(int minimum, int maximum, string label)
        {
            if (minimum < 1 || maximum < minimum)
                throw new ArgumentException(
                    $"Offsite {label} range must be positive and ordered.");
        }

        private static void ValidateId(string contractId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
            if (contractId.Length > 100)
                throw new ArgumentException("Offsite contract id must contain at most 100 characters.");
        }

        private unsafe void DestroyClone(UnityObject clone)
        {
            if (clone.IsNull) return;
            nint* arguments = stackalloc nint[1];
            arguments[0] = clone.Pointer;
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
            _transactionClones.Clear();
            _transactionRegistrations.Clear();
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);

        private sealed class OffsiteRegistration(
            string ownerId,
            string contractId,
            UnityObject asset) : IOffsiteContractRegistration
        {
            public string OwnerId { get; } = ownerId;
            public string ContractId { get; } = contractId;
            public UnityObject Asset { get; } = asset;
            public int Index { get; private set; } = -1;
            public bool IsMaterialized => Index >= 0;
            public string? MaterializationError { get; private set; }
            public bool CacheRepairLogged { get; set; }

            public void MarkMaterialized(int index)
            {
                Index = index;
                MaterializationError = null;
            }

            public void MarkPending() => Index = -1;
            public void SetError(string? value) => MaterializationError = value;
        }
    }
}
