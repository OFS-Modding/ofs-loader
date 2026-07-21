using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent(
    string ownerId,
    IUnsafeIl2CppApi unsafeApi,
    IUnityApi unity,
    Func<bool> isNetworkActive,
    Func<bool> isServerActive,
    IModEvents events,
    IModLogger logger) : IGameContent
{
    private static bool _buildingRegistrationWindowOpen;
    private readonly ItemRegistry _items = new(ownerId, unsafeApi);
    private readonly BuildingRegistry _buildings = new(ownerId, unsafeApi, isNetworkActive);
    private readonly RecipeRegistry _recipes = new(ownerId, unsafeApi);
    private readonly ContractRegistry _contracts = new(ownerId, unsafeApi);
    private readonly CompanyRegistry _companies = new(ownerId, unsafeApi);
    private readonly FoodRegistry _foods = new(ownerId, unsafeApi);
    private readonly BuildingCategoryRegistry _buildingCategories = new(ownerId, unsafeApi);
    private readonly UpgradeRegistry _upgrades = new(ownerId, unsafeApi);
    private readonly OffsiteContractRegistry _offsiteContracts =
        new(ownerId, unsafeApi, events, logger);
    private readonly PropertyRegistry _properties = new(ownerId, unsafeApi, events, logger);
    private readonly ItemSpawnProfileRegistry _itemSpawnProfiles = new(ownerId, unsafeApi);
    private readonly MiningAreaSpawnerRegistry _miningAreaSpawners =
        new(ownerId, unsafeApi, unity, isServerActive);
    private readonly MiningNodeRegistry _miningNodes =
        new(unsafeApi, unity, isServerActive);

    public IItemRegistry Items => _items;
    public IBuildingRegistry Buildings => _buildings;
    public IRecipeRegistry Recipes => _recipes;
    public IContractRegistry Contracts => _contracts;
    public ICompanyRegistry Companies => _companies;
    public IFoodRegistry Foods => _foods;
    public IBuildingCategoryRegistry BuildingCategories => _buildingCategories;
    public IUpgradeRegistry Upgrades => _upgrades;
    public IOffsiteContractRegistry OffsiteContracts => _offsiteContracts;
    public IPropertyRegistry Properties => _properties;
    public IItemSpawnProfileRegistry ItemSpawnProfiles => _itemSpawnProfiles;
    public IMiningAreaSpawnerRegistry MiningAreaSpawners => _miningAreaSpawners;
    public IMiningNodeRegistry MiningNodes => _miningNodes;

    internal void BeginTransaction()
    {
        _items.BeginTransaction();
        _buildings.BeginTransaction();
        _recipes.BeginTransaction();
        _contracts.BeginTransaction();
        _companies.BeginTransaction();
        _foods.BeginTransaction();
        _buildingCategories.BeginTransaction();
        _upgrades.BeginTransaction();
        _offsiteContracts.BeginTransaction();
        _properties.BeginTransaction();
        _itemSpawnProfiles.BeginTransaction();
        _miningAreaSpawners.BeginTransaction();
    }

    internal void CommitTransaction()
    {
        _properties.CommitTransaction();
        _itemSpawnProfiles.CommitTransaction();
        _miningAreaSpawners.CommitTransaction();
        _offsiteContracts.CommitTransaction();
        _upgrades.CommitTransaction();
        _buildingCategories.CommitTransaction();
        _foods.CommitTransaction();
        _companies.CommitTransaction();
        _contracts.CommitTransaction();
        _recipes.CommitTransaction();
        _buildings.CommitTransaction();
        _items.CommitTransaction();
    }

    internal void RollbackTransaction()
    {
        List<Exception>? failures = null;
        Rollback(_properties.RollbackTransaction);
        Rollback(_miningAreaSpawners.RollbackTransaction);
        Rollback(_itemSpawnProfiles.RollbackTransaction);
        Rollback(_offsiteContracts.RollbackTransaction);
        Rollback(_upgrades.RollbackTransaction);
        Rollback(_buildingCategories.RollbackTransaction);
        Rollback(_foods.RollbackTransaction);
        Rollback(_companies.RollbackTransaction);
        Rollback(_contracts.RollbackTransaction);
        Rollback(_recipes.RollbackTransaction);
        Rollback(_buildings.RollbackTransaction);
        Rollback(_items.RollbackTransaction);
        if (failures is not null)
        {
            throw new AggregateException(
                $"Content rollback for mod '{ownerId}' was incomplete.",
                failures);
        }

        void Rollback(Action action)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }

    internal static void BeginBuildingRegistration() =>
        _buildingRegistrationWindowOpen = true;

    internal static void EndBuildingRegistration() =>
        _buildingRegistrationWindowOpen = false;

    internal static bool IsBuildingRegistrationWindowOpen =>
        _buildingRegistrationWindowOpen;

    private sealed class ItemRegistry(
        string ownerId,
        IUnsafeIl2CppApi unsafeApi) : IItemRegistry
    {
        private static readonly Dictionary<string, OwnedItem> OwnedItems =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<nint, string> MutationOwners = new();
        private readonly Dictionary<nint, ItemDefinition> _loadSnapshots = new();
        private readonly List<nint> _loadMutationOrder = [];
        private readonly HashSet<nint> _loadOwnershipAdded = [];
        private readonly List<UnityObject> _loadClones = [];
        private readonly List<ItemRegistration> _loadRegistrations = [];
        private bool _loadCommitted;

        private readonly nint _itemClass = RequireClass(unsafeApi, "T_ItemSO");
        private readonly nint _managerClass = RequireClass(unsafeApi, "ItemSOManager");
        private readonly nint _unityObjectClass = RequireUnityClass(
            unsafeApi,
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Object");
        private readonly nint _gameObjectClass = RequireUnityClass(
            unsafeApi,
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "GameObject");
        private readonly nint _spriteClass = RequireUnityClass(
            unsafeApi,
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Sprite");
        private readonly nint _filterListField =
            RequireField(unsafeApi, RequireClass(unsafeApi, "T_ItemSO"), "FilterTypes");
        private readonly nint _filterListClass = unsafeApi.GetFieldTypeClass(
            RequireField(unsafeApi, RequireClass(unsafeApi, "T_ItemSO"), "FilterTypes"));

        public IReadOnlyList<ItemDefinition> GetAll()
        {
            EnsureMainThread();
            var list = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _managerClass, "GetAllItemSOs", 0),
                GetManager(),
                0);
            if (list == 0) return [];
            var listClass = unsafeApi.GetObjectClass(list);
            var countValue = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "get_Count", 0),
                list,
                0);
            var countPointer = unsafeApi.Unbox(countValue);
            if (countPointer == 0)
                throw new InvalidDataException("Item catalog Count returned null.");
            var count = Marshal.ReadInt32(countPointer);
            var result = new List<ItemDefinition>(count);
            for (var index = 0; index < count; ++index)
            {
                var item = GetItemAt(list, listClass, index);
                if (item != 0) result.Add(Describe(new UnityObject(item)));
            }
            return result;
        }

        private unsafe nint GetItemAt(nint list, nint listClass, int index)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, listClass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        public unsafe UnityObject Clone(UnityObject source, string newItemId)
        {
            EnsureMainThread();
            ValidateItem(source);
            ValidateItemId(newItemId);
            if (!FindById(newItemId).IsNull || OwnedItems.ContainsKey(newItemId))
            {
                throw new InvalidOperationException($"Item id '{newItemId}' is already registered.");
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
                RequireField(unsafeApi, _itemClass, "ItemID"),
                unsafeApi.NewString(newItemId));
            var result = new UnityObject(clone);
            if (!_loadCommitted) _loadClones.Add(result);
            return result;
        }

        public UnityObject FindById(string itemId)
        {
            EnsureMainThread();
            ValidateItemId(itemId);
            return new UnityObject(FindByIdPointer(itemId));
        }

        public ItemDefinition Describe(UnityObject itemScriptableObject)
        {
            EnsureMainThread();
            ValidateItem(itemScriptableObject);
            return new ItemDefinition(
                itemScriptableObject,
                ReadItemId(itemScriptableObject),
                ReadStringField(itemScriptableObject, "Name"),
                ReadStringField(itemScriptableObject, "Description"),
                unsafeApi.ReadInt32(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Price")),
                unsafeApi.ReadSingle(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Scale")),
                (ItemKind)unsafeApi.ReadInt32(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Type")),
                ReadFilters(itemScriptableObject.Pointer),
                ReadObjectField(itemScriptableObject.Pointer, "Icon"),
                ReadObjectField(itemScriptableObject.Pointer, "MiningVFX"),
                ReadObjectField(itemScriptableObject.Pointer, "PickupVFX"),
                ReadObjectField(itemScriptableObject.Pointer, "SpawnPrefab"),
                ReadObjectField(itemScriptableObject.Pointer, "VisualPrefab"),
                ReadBoolField(itemScriptableObject.Pointer, "isNode"),
                ReadIntField(itemScriptableObject.Pointer, "nodeHealth"),
                ReadIntField(itemScriptableObject.Pointer, "collectAmountMin"),
                ReadIntField(itemScriptableObject.Pointer, "collectAmountMax"),
                ReadObjectField(itemScriptableObject.Pointer, "NodeVisualPrefab"),
                (MiningVfxKind)ReadIntField(itemScriptableObject.Pointer, "nodeHitVFX"),
                (MiningSfxKind)ReadIntField(itemScriptableObject.Pointer, "nodeHitSFX"),
                ReadBoolField(itemScriptableObject.Pointer, "isMysteryItem"),
                (MysteryItemKind)ReadIntField(itemScriptableObject.Pointer, "mysteryType"),
                (UpgradeKind)ReadIntField(itemScriptableObject.Pointer, "requiredUpgrade"),
                ReadIntField(itemScriptableObject.Pointer, "requiredUpgradeLevel"),
                ReadBoolField(itemScriptableObject.Pointer, "fullVersionOnly"));
        }

        public void Update(UnityObject itemScriptableObject, ItemPatch patch)
        {
            EnsureMainThread();
            ValidateItem(itemScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            var before = Describe(itemScriptableObject);
            var claim = ClaimMutation(itemScriptableObject.Pointer, before);
            try
            {
                ApplyUpdate(itemScriptableObject, patch);
            }
            catch
            {
                RestoreDefinition(before);
                CancelClaim(itemScriptableObject.Pointer, claim);
                throw;
            }
        }

        private void ApplyUpdate(UnityObject itemScriptableObject, ItemPatch patch)
        {
            EnsureMainThread();
            ValidateItem(itemScriptableObject);
            ArgumentNullException.ThrowIfNull(patch);
            if (patch.Name is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(patch.Name);
                WriteStringField(itemScriptableObject, "Name", patch.Name);
            }
            if (patch.Description is not null)
            {
                WriteStringField(itemScriptableObject, "Description", patch.Description);
            }
            if (patch.Price is not null)
            {
                if (patch.Price < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Item price cannot be negative.");
                }
                unsafeApi.WriteInt32(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Price"),
                    patch.Price.Value);
            }
            if (patch.Scale is not null)
            {
                if (!float.IsFinite(patch.Scale.Value) || patch.Scale <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Item scale must be positive and finite.");
                }
                unsafeApi.WriteSingle(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Scale"),
                    patch.Scale.Value);
            }
            if (patch.Kind is not null)
            {
                if (!Enum.IsDefined(patch.Kind.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Unknown item kind.");
                }
                unsafeApi.WriteInt32(
                    itemScriptableObject.Pointer,
                    RequireField(unsafeApi, _itemClass, "Type"),
                    (int)patch.Kind.Value);
            }
            if (patch.Filters is not null)
            {
                ReplaceFilters(itemScriptableObject.Pointer, patch.Filters);
            }
            WriteOptionalAsset(itemScriptableObject.Pointer, "Icon", patch.Icon, _spriteClass, "Sprite");
            WriteOptionalAsset(itemScriptableObject.Pointer, "MiningVFX", patch.MiningVfx, _gameObjectClass, "GameObject");
            WriteOptionalAsset(itemScriptableObject.Pointer, "PickupVFX", patch.PickupVfx, _gameObjectClass, "GameObject");
            WriteOptionalAsset(itemScriptableObject.Pointer, "SpawnPrefab", patch.SpawnPrefab, _gameObjectClass, "GameObject");
            WriteOptionalAsset(itemScriptableObject.Pointer, "VisualPrefab", patch.VisualPrefab, _gameObjectClass, "GameObject");
            WriteOptionalBool(itemScriptableObject.Pointer, "isNode", patch.IsNode);
            if (patch.NodeHealth is not null)
            {
                if (patch.NodeHealth is < 0 or > 20)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Node health must be between 0 and 20.");
                }
                WriteIntField(itemScriptableObject.Pointer, "nodeHealth", patch.NodeHealth.Value);
            }
            if (patch.CollectAmountMin is not null || patch.CollectAmountMax is not null)
            {
                var collectMin = patch.CollectAmountMin ??
                    ReadIntField(itemScriptableObject.Pointer, "collectAmountMin");
                var collectMax = patch.CollectAmountMax ??
                    ReadIntField(itemScriptableObject.Pointer, "collectAmountMax");
                if (collectMin < 1 || collectMax < 1 || collectMin > collectMax)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(patch),
                        "Collection amounts must be positive and minimum cannot exceed maximum.");
                }
                if (patch.CollectAmountMin is not null)
                {
                    WriteIntField(itemScriptableObject.Pointer, "collectAmountMin", collectMin);
                }
                if (patch.CollectAmountMax is not null)
                {
                    WriteIntField(itemScriptableObject.Pointer, "collectAmountMax", collectMax);
                }
            }
            WriteOptionalAsset(
                itemScriptableObject.Pointer,
                "NodeVisualPrefab",
                patch.NodeVisualPrefab,
                _gameObjectClass,
                "GameObject");
            WriteOptionalEnum(itemScriptableObject.Pointer, "nodeHitVFX", patch.NodeHitVfx);
            WriteOptionalEnum(itemScriptableObject.Pointer, "nodeHitSFX", patch.NodeHitSfx);
            WriteOptionalBool(itemScriptableObject.Pointer, "isMysteryItem", patch.IsMysteryItem);
            WriteOptionalEnum(itemScriptableObject.Pointer, "mysteryType", patch.MysteryKind);
            WriteOptionalEnum(itemScriptableObject.Pointer, "requiredUpgrade", patch.RequiredUpgrade);
            if (patch.RequiredUpgradeLevel is not null)
            {
                if (patch.RequiredUpgradeLevel < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Required upgrade level cannot be negative.");
                }
                WriteIntField(
                    itemScriptableObject.Pointer,
                    "requiredUpgradeLevel",
                    patch.RequiredUpgradeLevel.Value);
            }
            WriteOptionalBool(itemScriptableObject.Pointer, "fullVersionOnly", patch.FullVersionOnly);
        }

        private void RestoreDefinition(ItemDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteStringField(definition.Asset, "Name", definition.Name);
            WriteStringField(definition.Asset, "Description", definition.Description);
            unsafeApi.WriteInt32(pointer, RequireField(unsafeApi, _itemClass, "Price"), definition.Price);
            unsafeApi.WriteSingle(pointer, RequireField(unsafeApi, _itemClass, "Scale"), definition.Scale);
            unsafeApi.WriteInt32(pointer, RequireField(unsafeApi, _itemClass, "Type"), (int)definition.Kind);
            ReplaceFilters(pointer, definition.Filters);
            WriteReference("Icon", definition.Icon);
            WriteReference("MiningVFX", definition.MiningVfx);
            WriteReference("PickupVFX", definition.PickupVfx);
            WriteReference("SpawnPrefab", definition.SpawnPrefab);
            WriteReference("VisualPrefab", definition.VisualPrefab);
            unsafeApi.WriteBoolean(pointer, RequireField(unsafeApi, _itemClass, "isNode"), definition.IsNode);
            WriteIntField(pointer, "nodeHealth", definition.NodeHealth);
            WriteIntField(pointer, "collectAmountMin", definition.CollectAmountMin);
            WriteIntField(pointer, "collectAmountMax", definition.CollectAmountMax);
            WriteReference("NodeVisualPrefab", definition.NodeVisualPrefab);
            WriteIntField(pointer, "nodeHitVFX", (int)definition.NodeHitVfx);
            WriteIntField(pointer, "nodeHitSFX", (int)definition.NodeHitSfx);
            unsafeApi.WriteBoolean(
                pointer,
                RequireField(unsafeApi, _itemClass, "isMysteryItem"),
                definition.IsMysteryItem);
            WriteIntField(pointer, "mysteryType", (int)definition.MysteryKind);
            WriteIntField(pointer, "requiredUpgrade", (int)definition.RequiredUpgrade);
            WriteIntField(pointer, "requiredUpgradeLevel", definition.RequiredUpgradeLevel);
            unsafeApi.WriteBoolean(
                pointer,
                RequireField(unsafeApi, _itemClass, "fullVersionOnly"),
                definition.FullVersionOnly);

            void WriteReference(string field, UnityObject value) =>
                unsafeApi.WriteObjectReference(
                    pointer,
                    RequireField(unsafeApi, _itemClass, field),
                    value.Pointer);
        }

        private MutationClaim ClaimMutation(nint pointer, ItemDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Item '{before.ItemId}' is already mutated by mod '{owner}'.");
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

        private UnityObject ReadObjectField(nint item, string fieldName) =>
            new(unsafeApi.ReadObjectReference(
                item,
                RequireField(unsafeApi, _itemClass, fieldName)));

        private int ReadIntField(nint item, string fieldName) =>
            unsafeApi.ReadInt32(item, RequireField(unsafeApi, _itemClass, fieldName));

        private bool ReadBoolField(nint item, string fieldName) =>
            unsafeApi.ReadBoolean(item, RequireField(unsafeApi, _itemClass, fieldName));

        private void WriteIntField(nint item, string fieldName, int value) =>
            unsafeApi.WriteInt32(item, RequireField(unsafeApi, _itemClass, fieldName), value);

        private void WriteOptionalBool(nint item, string fieldName, bool? value)
        {
            if (value is not null)
            {
                unsafeApi.WriteBoolean(
                    item,
                    RequireField(unsafeApi, _itemClass, fieldName),
                    value.Value);
            }
        }

        private void WriteOptionalAsset(
            nint item,
            string fieldName,
            UnityObject? value,
            nint expectedClass,
            string expectedType)
        {
            if (value is null)
            {
                return;
            }
            if (!value.Value.IsNull &&
                !unsafeApi.IsAssignableFrom(expectedClass, unsafeApi.GetObjectClass(value.Value.Pointer)))
            {
                throw new ArgumentException($"Item field '{fieldName}' must reference a Unity {expectedType}.");
            }
            unsafeApi.WriteObjectReference(
                item,
                RequireField(unsafeApi, _itemClass, fieldName),
                value.Value.Pointer);
        }

        private void WriteOptionalEnum<TEnum>(nint item, string fieldName, TEnum? value)
            where TEnum : struct, Enum
        {
            if (value is null)
            {
                return;
            }
            if (!Enum.IsDefined(value.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Unknown {typeof(TEnum).Name} value.");
            }
            WriteIntField(item, fieldName, Convert.ToInt32(value.Value));
        }

        private IReadOnlyList<ItemFilter> ReadFilters(nint item)
        {
            var list = unsafeApi.ReadObjectReference(item, _filterListField);
            if (list == 0)
            {
                return [];
            }

            var count = ReadListCount(list);
            var result = new List<ItemFilter>(count);
            for (var index = 0; index < count; ++index)
            {
                var boxed = GetListValue(list, index);
                var value = unsafeApi.Unbox(boxed);
                if (value == 0)
                {
                    throw new InvalidDataException("Filter List<T> returned a null value.");
                }
                result.Add((ItemFilter)Marshal.ReadInt32(value));
            }
            return result;
        }

        private void ReplaceFilters(nint item, IReadOnlyList<ItemFilter> filters)
        {
            if (filters.Count > Enum.GetValues<ItemFilter>().Length)
            {
                throw new ArgumentException("An item has more filters than the game defines.", nameof(filters));
            }
            if (filters.Distinct().Count() != filters.Count)
            {
                throw new ArgumentException("Item filters must be unique.", nameof(filters));
            }
            foreach (var filter in filters)
            {
                if (!Enum.IsDefined(filter))
                {
                    throw new ArgumentOutOfRangeException(nameof(filters), $"Unknown item filter '{filter}'.");
                }
            }

            var list = unsafeApi.NewObject(_filterListClass);
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _filterListClass, ".ctor", 0),
                list,
                0);
            // Attach before invoking Add so the IL2CPP object graph roots the list.
            unsafeApi.WriteObjectReference(item, _filterListField, list);
            foreach (var filter in filters)
            {
                AddListValue(list, (int)filter);
            }
        }

        private int ReadListCount(nint list)
        {
            var boxed = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _filterListClass, "get_Count", 0),
                list,
                0);
            var value = unsafeApi.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("Filter List<T>.Count returned null.");
        }

        private unsafe nint GetListValue(nint list, int index)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _filterListClass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void AddListValue(nint list, int value)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&value);
            _ = unsafeApi.RuntimeInvoke(
                RequireMethod(unsafeApi, _filterListClass, "Add", 1),
                list,
                (nint)arguments);
        }

        public IItemRegistration Register(UnityObject itemScriptableObject)
        {
            EnsureMainThread();
            ValidateItem(itemScriptableObject);
            var itemId = ReadItemId(itemScriptableObject);
            ValidateItemId(itemId);

            var existing = FindByIdPointer(itemId);
            if (OwnedItems.TryGetValue(itemId, out var tracked))
            {
                if (tracked.OwnerId != ownerId || tracked.Asset.Pointer != itemScriptableObject.Pointer)
                {
                    throw new InvalidOperationException(
                        $"Item id '{itemId}' is already owned by mod '{tracked.OwnerId}'.");
                }
                return tracked.Registration;
            }

            if (existing != 0)
            {
                throw new InvalidOperationException(
                    $"Item id '{itemId}' is already registered by the base game or an untracked source.");
            }

            InvokeWithObject("AddItemSOToCache", itemScriptableObject.Pointer);
            var registered = FindByIdPointer(itemId);
            if (registered != itemScriptableObject.Pointer)
            {
                throw new InvalidOperationException(
                    $"ItemSOManager did not retain item '{itemId}' after registration.");
            }

            var registration = new ItemRegistration(
                this,
                itemId,
                itemScriptableObject);
            OwnedItems.Add(itemId, new OwnedItem(ownerId, itemScriptableObject, registration));
            if (!_loadCommitted) _loadRegistrations.Add(registration);
            return registration;
        }

        private unsafe nint FindByIdPointer(string itemId)
        {
            var method = RequireMethod(unsafeApi, _managerClass, "GetItemSOById", 1);
            nint* arguments = stackalloc nint[1];
            arguments[0] = unsafeApi.NewString(itemId);
            return unsafeApi.RuntimeInvoke(method, GetManager(), (nint)arguments);
        }

        private string ReadItemId(UnityObject item)
        {
            var getId = RequireMethod(unsafeApi, _itemClass, "GetItemID", 0);
            return unsafeApi.ReadString(unsafeApi.RuntimeInvoke(getId, item.Pointer, 0));
        }

        private string ReadStringField(UnityObject item, string fieldName)
        {
            var value = unsafeApi.ReadObjectReference(
                item.Pointer,
                RequireField(unsafeApi, _itemClass, fieldName));
            return value == 0 ? string.Empty : unsafeApi.ReadString(value);
        }

        private void WriteStringField(UnityObject item, string fieldName, string value) =>
            unsafeApi.WriteObjectReference(
                item.Pointer,
                RequireField(unsafeApi, _itemClass, fieldName),
                unsafeApi.NewString(value));

        private void ValidateItem(UnityObject item)
        {
            if (item.IsNull)
            {
                throw new ArgumentException("Item ScriptableObject is null.");
            }
            var candidateClass = unsafeApi.GetObjectClass(item.Pointer);
            if (!unsafeApi.IsAssignableFrom(_itemClass, candidateClass))
            {
                throw new ArgumentException("Asset is not a T_ItemSO.");
            }
        }

        private unsafe void InvokeWithObject(string methodName, nint value)
        {
            var method = RequireMethod(unsafeApi, _managerClass, methodName, 1);
            nint* arguments = stackalloc nint[1];
            arguments[0] = value;
            _ = unsafeApi.RuntimeInvoke(method, GetManager(), (nint)arguments);
        }

        private nint GetManager()
        {
            var getInstance = RequireMethod(unsafeApi, _managerClass, "get_Instance", 0);
            var manager = unsafeApi.RuntimeInvoke(getInstance, 0, 0);
            return manager != 0
                ? manager
                : throw new InvalidOperationException("ItemSOManager.Instance is not ready in this scene.");
        }

        private void Unregister(ItemRegistration registration)
        {
            EnsureMainThread();
            if (!registration.IsRegistered)
            {
                return;
            }
            if (!OwnedItems.TryGetValue(registration.ItemId, out var owned) ||
                !ReferenceEquals(owned.Registration, registration) ||
                owned.OwnerId != ownerId)
            {
                throw new InvalidOperationException(
                    $"Mod '{ownerId}' no longer owns item '{registration.ItemId}'.");
            }

            InvokeWithObject("RemoveItemSOFromCache", registration.Asset.Pointer);
            OwnedItems.Remove(registration.ItemId);
            registration.MarkUnregistered();
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
                throw new InvalidOperationException("An item content transaction is already active.");
            }
            _loadCommitted = false;
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
            for (var index = _loadRegistrations.Count - 1; index >= 0; --index)
            {
                Try(() => Unregister(_loadRegistrations[index]));
            }
            for (var index = _loadMutationOrder.Count - 1; index >= 0; --index)
            {
                var pointer = _loadMutationOrder[index];
                if (_loadSnapshots.TryGetValue(pointer, out var snapshot))
                {
                    try { RestoreDefinition(snapshot); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                    finally
                    {
                        if (_loadOwnershipAdded.Contains(pointer)) MutationOwners.Remove(pointer);
                        _loadSnapshots.Remove(pointer);
                    }
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
                throw new AggregateException("Item content rollback was incomplete.", failures);
            }

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
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

        private static void ValidateItemId(string itemId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            if (itemId.Length > 100)
            {
                throw new ArgumentException("Item id must contain at most 100 characters.");
            }
        }

        private sealed record OwnedItem(
            string OwnerId,
            UnityObject Asset,
            ItemRegistration Registration);

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);

        private sealed class ItemRegistration(
            ItemRegistry registry,
            string itemId,
            UnityObject asset) : IItemRegistration
        {
            public string ItemId { get; } = itemId;
            public UnityObject Asset { get; } = asset;
            public bool IsRegistered { get; private set; } = true;

            public void Unregister() => registry.Unregister(this);
            public void Dispose() => Unregister();
            public void MarkUnregistered() => IsRegistered = false;
        }
    }

    private static nint RequireClass(IUnsafeIl2CppApi unsafeApi, string name)
    {
        var klass = unsafeApi.FindClass("Assembly-CSharp.dll", string.Empty, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Game class '{name}' was not found.");
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
            : throw new MissingMethodException($"Game method '{name}/{argumentCount}' was not found.");
    }

    private static nint RequireField(IUnsafeIl2CppApi unsafeApi, nint klass, string name)
    {
        var field = unsafeApi.FindField(klass, name);
        return field != 0
            ? field
            : throw new MissingFieldException($"Game field '{name}' was not found.");
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
        {
            throw new InvalidOperationException(
                "Content API calls must run on Unity's main thread. Use a lifecycle event or MainThread.Post().");
        }
    }
}
