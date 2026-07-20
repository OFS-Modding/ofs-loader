using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private abstract class CentralCatalogRegistry<TDefinition>
        where TDefinition : class
    {
        private static readonly Dictionary<Type, Dictionary<string, RegistrationRecord>> Owned = [];
        private static readonly Dictionary<(Type Registry, nint Asset), string> MutationOwners = [];

        private readonly Dictionary<nint, TDefinition> _transactionSnapshots = [];
        private readonly List<nint> _transactionMutationOrder = [];
        private readonly HashSet<nint> _transactionOwnershipAdded = [];
        private readonly List<UnityObject> _transactionClones = [];
        private readonly List<RegistrationAppend> _transactionRegistrations = [];
        private bool _transactionCommitted;

        protected CentralCatalogRegistry(
            string ownerId,
            IUnsafeIl2CppApi api,
            string assetClassName,
            string listGetterName)
        {
            OwnerId = ownerId;
            Api = api;
            AssetClass = RequireClass(api, assetClassName);
            ListGetterName = listGetterName;
            ManagerClass = RequireClass(api, "ScriptableListManager");
            UnityObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            if (UnityObjectClass == 0)
                throw new TypeLoadException("UnityEngine.Object was not found.");
        }

        protected string OwnerId { get; }
        protected IUnsafeIl2CppApi Api { get; }
        protected nint AssetClass { get; }
        protected nint ManagerClass { get; }
        protected nint UnityObjectClass { get; }
        protected string ListGetterName { get; }
        protected abstract string Kind { get; }

        protected int CountCore
        {
            get
            {
                EnsureMainThread();
                return GetCount(GetList());
            }
        }

        protected UnityObject CloneCore(UnityObject source, string newIdentity)
        {
            EnsureMainThread();
            ValidateAsset(source);
            ValidateIdentity(newIdentity);
            if (!FindCore(newIdentity).IsNull || GetOwned().ContainsKey(newIdentity))
                throw new InvalidOperationException(
                    $"{Kind} id '{newIdentity}' is already registered.");

            unsafe
            {
                nint* arguments = stackalloc nint[1];
                arguments[0] = source.Pointer;
                var clone = Api.RuntimeInvoke(
                    RequireMethod(Api, UnityObjectClass, "Instantiate", 1),
                    0,
                    (nint)arguments);
                if (clone == 0)
                    throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
                WriteIdentity(clone, newIdentity);
                var result = new UnityObject(clone);
                if (!_transactionCommitted) _transactionClones.Add(result);
                return result;
            }
        }

        protected UnityObject FindCore(string identity)
        {
            EnsureMainThread();
            ValidateIdentity(identity);
            var list = GetList();
            var count = GetCount(list);
            for (var index = 0; index < count; ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0 && string.Equals(
                        ReadIdentity(asset),
                        identity,
                        StringComparison.Ordinal))
                    return new UnityObject(asset);
            }
            return default;
        }

        protected IReadOnlyList<TDefinition> GetAllCore()
        {
            EnsureMainThread();
            var list = GetList();
            var count = GetCount(list);
            var result = new List<TDefinition>(count);
            for (var index = 0; index < count; ++index)
            {
                var asset = GetAt(list, index);
                if (asset != 0) result.Add(DescribeCore(new UnityObject(asset)));
            }
            return result;
        }

        protected TDefinition DescribeChecked(UnityObject asset)
        {
            EnsureMainThread();
            ValidateAsset(asset);
            return DescribeCore(asset);
        }

        protected void UpdateCore(UnityObject asset, Action apply)
        {
            EnsureMainThread();
            ValidateAsset(asset);
            ArgumentNullException.ThrowIfNull(apply);
            var before = DescribeCore(asset);
            var claim = ClaimMutation(asset.Pointer, before);
            try
            {
                apply();
                RefreshCaches();
            }
            catch
            {
                RestoreCore(before);
                RefreshCaches();
                CancelClaim(asset.Pointer, claim);
                throw;
            }
        }

        protected RegistrationRecord RegisterCore(UnityObject asset)
        {
            EnsureMainThread();
            ValidateAsset(asset);
            if (!IsBuildingRegistrationWindowOpen)
                throw new InvalidOperationException(
                    $"{Kind} assets may only be registered inside ContentReady.");

            var identity = ReadIdentity(asset.Pointer);
            ValidateIdentity(identity);
            var owned = GetOwned();
            var list = GetList();
            if (owned.TryGetValue(identity, out var existing))
            {
                if (!string.Equals(existing.OwnerId, OwnerId, StringComparison.OrdinalIgnoreCase) ||
                    existing.Asset.Pointer != asset.Pointer)
                    throw new InvalidOperationException(
                        $"{Kind} id '{identity}' is already owned by mod '{existing.OwnerId}'.");
                var existingIndex = FindPointerIndex(list, asset.Pointer);
                if (existingIndex >= 0)
                {
                    existing.Index = existingIndex;
                    return existing;
                }
                Append(list, existing);
                if (!_transactionCommitted)
                    _transactionRegistrations.Add(new RegistrationAppend(existing, false));
                RefreshCaches();
                return existing;
            }
            if (!FindCore(identity).IsNull)
                throw new InvalidOperationException(
                    $"{Kind} id '{identity}' is already registered by the base game or an untracked source.");

            var index = GetCount(list);
            Add(list, asset.Pointer);
            if (GetCount(list) != index + 1 || GetAt(list, index) != asset.Pointer)
                throw new InvalidOperationException(
                    $"{Kind} registry did not append '{identity}' deterministically.");

            var registration = new RegistrationRecord(OwnerId, identity, asset, index);
            owned.Add(identity, registration);
            if (!_transactionCommitted)
                _transactionRegistrations.Add(new RegistrationAppend(registration, true));
            try
            {
                RefreshCaches();
            }
            catch
            {
                RollbackRegistration(new RegistrationAppend(registration, true));
                _transactionRegistrations.RemoveAll(value =>
                    ReferenceEquals(value.Registration, registration));
                throw;
            }
            RuntimeLog.Write(
                $"{Kind} registered: owner={OwnerId}, id={identity}, index={index}.");
            return registration;
        }

        protected abstract string ReadIdentity(nint asset);
        protected abstract void WriteIdentity(nint asset, string identity);
        protected abstract void ValidateIdentity(string identity);
        protected abstract TDefinition DescribeCore(UnityObject asset);
        protected abstract void RestoreCore(TDefinition definition);
        protected virtual void RefreshCaches() { }

        protected void ValidateAsset(UnityObject asset)
        {
            if (asset.IsNull || !Api.IsAssignableFrom(AssetClass, Api.GetObjectClass(asset.Pointer)))
                throw new ArgumentException($"Asset is not a {Kind} ScriptableObject.");
        }

        protected nint Field(nint klass, string name) => RequireField(Api, klass, name);
        protected nint AssetField(string name) => RequireField(Api, AssetClass, name);

        protected string ReadString(nint instance, nint klass, string field)
        {
            var value = Api.ReadObjectReference(instance, Field(klass, field));
            return value == 0 ? string.Empty : Api.ReadString(value);
        }

        protected void WriteString(nint instance, nint klass, string field, string value) =>
            Api.WriteObjectReference(instance, Field(klass, field), Api.NewString(value));

        protected UnityObject ReadReference(nint instance, nint klass, string field) =>
            new(Api.ReadObjectReference(instance, Field(klass, field)));

        protected void WriteReference(nint instance, nint klass, string field, UnityObject value) =>
            Api.WriteObjectReference(instance, Field(klass, field), value.Pointer);

        protected int ReadInt(nint instance, nint klass, string field) =>
            Api.ReadInt32(instance, Field(klass, field));

        protected void WriteInt(nint instance, nint klass, string field, int value) =>
            Api.WriteInt32(instance, Field(klass, field), value);

        protected float ReadFloat(nint instance, nint klass, string field) =>
            Api.ReadSingle(instance, Field(klass, field));

        protected void WriteFloat(nint instance, nint klass, string field, float value) =>
            Api.WriteSingle(instance, Field(klass, field), value);

        protected bool ReadBool(nint instance, nint klass, string field) =>
            Api.ReadBoolean(instance, Field(klass, field));

        protected void WriteBool(nint instance, nint klass, string field, bool value) =>
            Api.WriteBoolean(instance, Field(klass, field), value);

        protected unsafe UnityColor ReadColor(nint instance, nint klass, string field)
        {
            float* values = stackalloc float[4];
            Api.GetFieldValue(instance, Field(klass, field), (nint)values);
            return new UnityColor(values[0], values[1], values[2], values[3]);
        }

        protected unsafe void WriteColor(
            nint instance,
            nint klass,
            string field,
            UnityColor value)
        {
            float* values = stackalloc float[4];
            values[0] = value.R;
            values[1] = value.G;
            values[2] = value.B;
            values[3] = value.A;
            Api.SetFieldValue(instance, Field(klass, field), (nint)values);
        }

        protected IReadOnlyList<UnityObject> ReadReferenceList(
            nint instance,
            nint klass,
            string field)
        {
            var list = Api.ReadObjectReference(instance, Field(klass, field));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<UnityObject>(count);
            for (var index = 0; index < count; ++index)
                result.Add(new UnityObject(GetAt(list, index)));
            return result;
        }

        protected void ReplaceReferenceList(
            nint instance,
            nint klass,
            string field,
            IReadOnlyList<UnityObject> values,
            nint requiredElementClass)
        {
            if (values.Count > 10000)
                throw new ArgumentException("A content reference list may contain at most 10000 entries.");
            foreach (var value in values)
            {
                if (value.IsNull ||
                    !Api.IsAssignableFrom(requiredElementClass, Api.GetObjectClass(value.Pointer)))
                    throw new ArgumentException("A content reference list contains an invalid asset.");
            }
            var targetField = Field(klass, field);
            var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
            Api.WriteObjectReference(instance, targetField, list);
            foreach (var value in values) Add(list, value.Pointer);
        }

        protected IReadOnlyList<string> ReadStringList(
            nint instance,
            nint klass,
            string field)
        {
            var list = Api.ReadObjectReference(instance, Field(klass, field));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<string>(count);
            for (var index = 0; index < count; ++index)
            {
                var value = GetAt(list, index);
                result.Add(value == 0 ? string.Empty : Api.ReadString(value));
            }
            return result;
        }

        protected void ReplaceStringList(
            nint instance,
            nint klass,
            string field,
            IReadOnlyList<string> values)
        {
            if (values.Count > 10000)
                throw new ArgumentException("A content string list may contain at most 10000 entries.");
            var targetField = Field(klass, field);
            var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
            Api.WriteObjectReference(instance, targetField, list);
            foreach (var value in values) Add(list, Api.NewString(value));
        }

        protected IReadOnlyList<int> ReadIntList(
            nint instance,
            nint klass,
            string field)
        {
            var list = Api.ReadObjectReference(instance, Field(klass, field));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<int>(count);
            for (var index = 0; index < count; ++index)
            {
                var boxed = GetAt(list, index);
                var value = Api.Unbox(boxed);
                if (value == 0) throw new InvalidDataException("Int32 List<T> returned null.");
                result.Add(Marshal.ReadInt32(value));
            }
            return result;
        }

        protected void ReplaceIntList(
            nint instance,
            nint klass,
            string field,
            IReadOnlyList<int> values)
        {
            if (values.Count > 10000)
                throw new ArgumentException("A content integer list may contain at most 10000 entries.");
            var targetField = Field(klass, field);
            var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
            Api.WriteObjectReference(instance, targetField, list);
            foreach (var value in values) AddValue(list, value);
        }

        protected IReadOnlyList<int> ReadEnumList(nint instance, nint klass, string field)
        {
            var list = Api.ReadObjectReference(instance, Field(klass, field));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<int>(count);
            for (var index = 0; index < count; ++index)
            {
                var boxed = GetAt(list, index);
                var value = Api.Unbox(boxed);
                if (value == 0) throw new InvalidDataException("Enum List<T> returned null.");
                result.Add(Marshal.ReadInt32(value));
            }
            return result;
        }

        protected void ReplaceEnumList(
            nint instance,
            nint klass,
            string field,
            IReadOnlyList<int> values)
        {
            var targetField = Field(klass, field);
            var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
            Api.WriteObjectReference(instance, targetField, list);
            foreach (var value in values) AddValue(list, value);
        }

        protected nint NewConstructedObject(nint klass)
        {
            var result = Api.NewObject(klass);
            if (result == 0) throw new InvalidOperationException("IL2CPP object allocation returned null.");
            _ = Api.RuntimeInvoke(RequireMethod(Api, klass, ".ctor", 0), result, 0);
            return result;
        }

        protected int GetCount(nint list)
        {
            var klass = Api.GetObjectClass(list);
            var boxed = Api.RuntimeInvoke(RequireMethod(Api, klass, "get_Count", 0), list, 0);
            var value = Api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("List<T>.Count returned null.");
        }

        protected unsafe nint GetAt(nint list, int index)
        {
            var klass = Api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return Api.RuntimeInvoke(
                RequireMethod(Api, klass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        protected unsafe void Add(nint list, nint value)
        {
            var klass = Api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = value;
            _ = Api.RuntimeInvoke(RequireMethod(Api, klass, "Add", 1), list, (nint)arguments);
        }

        protected unsafe void AddValue(nint list, int value)
        {
            var klass = Api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&value);
            _ = Api.RuntimeInvoke(RequireMethod(Api, klass, "Add", 1), list, (nint)arguments);
        }

        internal void BeginTransaction()
        {
            if (!_transactionCommitted)
                throw new InvalidOperationException($"A {Kind} content transaction is already active.");
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
            if (_transactionRegistrations.Count != 0) Try(RefreshCaches);
            for (var index = _transactionMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _transactionMutationOrder[index];
                if (!_transactionSnapshots.TryGetValue(pointer, out var snapshot)) continue;
                try { RestoreCore(snapshot); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                finally
                {
                    if (_transactionOwnershipAdded.Contains(pointer))
                        MutationOwners.Remove((GetType(), pointer));
                    _transactionSnapshots.Remove(pointer);
                }
            }
            if (_transactionMutationOrder.Count != 0) Try(RefreshCaches);
            for (var index = _transactionClones.Count - 1; index >= 0; --index)
                Try(() => DestroyClone(_transactionClones[index]));
            ClearTransaction();
            _transactionCommitted = true;
            if (failures is not null)
                throw new AggregateException($"{Kind} content rollback was incomplete.", failures);

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }

        private Dictionary<string, RegistrationRecord> GetOwned()
        {
            if (!Owned.TryGetValue(GetType(), out var result))
            {
                result = new Dictionary<string, RegistrationRecord>(StringComparer.Ordinal);
                Owned.Add(GetType(), result);
            }
            return result;
        }

        private MutationClaim ClaimMutation(nint pointer, TDefinition before)
        {
            var key = (GetType(), pointer);
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(key, out var owner))
            {
                if (!string.Equals(owner, OwnerId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"{Kind} asset is already mutated by mod '{owner}'.");
            }
            else
            {
                MutationOwners.Add(key, OwnerId);
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
                MutationOwners.Remove((GetType(), pointer));
                _transactionOwnershipAdded.Remove(pointer);
            }
        }

        private nint GetList()
        {
            var manager = Api.RuntimeInvoke(
                RequireMethod(Api, ManagerClass, "get_Instance", 0),
                0,
                0);
            if (manager == 0)
                throw new InvalidOperationException("ScriptableListManager.Instance is unavailable.");
            var list = Api.RuntimeInvoke(
                RequireMethod(Api, ManagerClass, ListGetterName, 0),
                manager,
                0);
            return list != 0
                ? list
                : throw new InvalidOperationException($"{Kind} catalog list is null.");
        }

        private int FindPointerIndex(nint list, nint pointer)
        {
            var count = GetCount(list);
            for (var index = 0; index < count; ++index)
                if (GetAt(list, index) == pointer) return index;
            return -1;
        }

        private RegistrationRecord Append(nint list, RegistrationRecord registration)
        {
            var index = GetCount(list);
            Add(list, registration.Asset.Pointer);
            if (GetCount(list) != index + 1 || GetAt(list, index) != registration.Asset.Pointer)
                throw new InvalidOperationException(
                    $"{Kind} registry did not re-append '{registration.Identity}' deterministically.");
            registration.Index = index;
            return registration;
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
                        $"Cannot roll back {Kind} '{registration.Identity}' at non-tail index {index}.");
                RemoveAt(list, index);
            }
            var owned = GetOwned();
            if (append.OwnershipAdded &&
                owned.TryGetValue(registration.Identity, out var current) &&
                ReferenceEquals(current, registration))
                owned.Remove(registration.Identity);
            registration.Index = -1;
        }

        private unsafe void RemoveAt(nint list, int index)
        {
            var klass = Api.GetObjectClass(list);
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            _ = Api.RuntimeInvoke(
                RequireMethod(Api, klass, "RemoveAt", 1),
                list,
                (nint)arguments);
        }

        private unsafe void DestroyClone(UnityObject clone)
        {
            if (clone.IsNull) return;
            nint* arguments = stackalloc nint[1];
            arguments[0] = clone.Pointer;
            _ = Api.RuntimeInvoke(
                RequireMethod(Api, UnityObjectClass, "Destroy", 1),
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

        protected sealed class RegistrationRecord(
            string ownerId,
            string identity,
            UnityObject asset,
            int index)
        {
            public string OwnerId { get; } = ownerId;
            public string Identity { get; } = identity;
            public UnityObject Asset { get; } = asset;
            public int Index { get; set; } = index;
            public object? Handle { get; set; }
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);
        private readonly record struct RegistrationAppend(
            RegistrationRecord Registration,
            bool OwnershipAdded);
    }
}
