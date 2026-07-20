using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class RecipeRegistry : IRecipeRegistry
    {
        private static readonly Dictionary<nint, string> MutationOwners = new();
        private readonly Dictionary<nint, RecipeDefinition> _loadSnapshots = new();
        private readonly List<nint> _loadMutationOrder = [];
        private readonly HashSet<nint> _loadOwnershipAdded = [];
        private bool _loadCommitted;
        private readonly string _ownerId;
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _itemClass;
        private readonly nint _buildingClass;
        private readonly nint _ingredientClass;
        private readonly nint _recipeListField;
        private readonly nint _recipeListClass;
        private readonly nint _ingredientItemField;
        private readonly nint _ingredientCountField;

        public RecipeRegistry(string ownerId, IUnsafeIl2CppApi api)
        {
            _ownerId = ownerId;
            _api = api;
            _itemClass = RequireClass(api, "T_ItemSO");
            _buildingClass = RequireClass(api, "T_BuildingItemSO");
            _ingredientClass = api.FindNestedClass(_itemClass, "RecipeIngredient");
            if (_ingredientClass == 0)
            {
                throw new TypeLoadException("T_ItemSO.RecipeIngredient was not found.");
            }
            _recipeListField = RequireField(api, _itemClass, "RecipeList");
            _recipeListClass = api.GetFieldTypeClass(_recipeListField);
            _ingredientItemField = RequireField(api, _ingredientClass, "Item");
            _ingredientCountField = RequireField(api, _ingredientClass, "Count");
        }

        public RecipeDefinition Describe(UnityObject productItem)
        {
            EnsureMainThread();
            ValidateItem(productItem, nameof(productItem));
            var product = productItem.Pointer;
            var producedBy = ReadReference(product, "producedBy");
            var ore = ReadReference(product, "ore");
            var ingredients = ReadIngredients(product);
            return new RecipeDefinition(
                productItem,
                new UnityObject(producedBy),
                _api.ReadSingle(product, RequireField(_api, _itemClass, "productionTime")),
                new UnityObject(ore),
                _api.ReadInt32(product, RequireField(_api, _itemClass, "oreCount")),
                ingredients,
                ReadBooleanProperty(product, "get_UsesOreRecipe"),
                ReadBooleanProperty(product, "get_UsesListRecipe"));
        }

        public void Update(UnityObject productItem, RecipePatch patch)
        {
            EnsureMainThread();
            ValidateItem(productItem, nameof(productItem));
            ArgumentNullException.ThrowIfNull(patch);
            var before = Describe(productItem);
            var claim = ClaimMutation(productItem.Pointer, before);
            try
            {
                ApplyUpdate(productItem, patch);
            }
            catch
            {
                RestoreDefinition(before);
                CancelClaim(productItem.Pointer, claim);
                throw;
            }
        }

        private void ApplyUpdate(UnityObject productItem, RecipePatch patch)
        {
            EnsureMainThread();
            ValidateItem(productItem, nameof(productItem));
            ArgumentNullException.ThrowIfNull(patch);
            var product = productItem.Pointer;

            if (patch.ProducedBy is not null)
            {
                ValidateOptionalBuilding(patch.ProducedBy.Value);
                WriteReference(product, "producedBy", patch.ProducedBy.Value.Pointer);
            }
            if (patch.ProductionTime is not null)
            {
                if (!float.IsFinite(patch.ProductionTime.Value) || patch.ProductionTime.Value < 0f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(patch),
                        "Production time must be finite and non-negative.");
                }
                _api.WriteSingle(
                    product,
                    RequireField(_api, _itemClass, "productionTime"),
                    patch.ProductionTime.Value);
            }
            if (patch.Ore is not null)
            {
                ValidateOptionalItem(patch.Ore.Value, nameof(patch.Ore));
                WriteReference(product, "ore", patch.Ore.Value.Pointer);
            }
            if (patch.OreCount is not null)
            {
                if (patch.OreCount.Value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(patch), "Ore count cannot be negative.");
                }
                _api.WriteInt32(
                    product,
                    RequireField(_api, _itemClass, "oreCount"),
                    patch.OreCount.Value);
            }
            if (patch.Ingredients is not null)
            {
                ReplaceIngredients(product, patch.Ingredients);
            }
        }

        private void RestoreDefinition(RecipeDefinition definition)
        {
            var product = definition.Product.Pointer;
            WriteReference(product, "producedBy", definition.ProducedBy.Pointer);
            _api.WriteSingle(
                product,
                RequireField(_api, _itemClass, "productionTime"),
                definition.ProductionTime);
            WriteReference(product, "ore", definition.Ore.Pointer);
            _api.WriteInt32(
                product,
                RequireField(_api, _itemClass, "oreCount"),
                definition.OreCount);
            ReplaceIngredients(
                product,
                definition.Ingredients
                    .Select(value => new RecipeIngredientPatch(value.Item, value.Count))
                    .ToArray());
        }

        private MutationClaim ClaimMutation(nint pointer, RecipeDefinition before)
        {
            var ownershipAdded = false;
            if (MutationOwners.TryGetValue(pointer, out var owner))
            {
                if (!string.Equals(owner, _ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Recipe for '{ReadItemId(pointer)}' is already mutated by mod '{owner}'.");
                }
            }
            else
            {
                MutationOwners.Add(pointer, _ownerId);
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

        internal void CommitTransaction()
        {
            _loadCommitted = true;
            _loadSnapshots.Clear();
            _loadMutationOrder.Clear();
            _loadOwnershipAdded.Clear();
        }

        internal void BeginTransaction()
        {
            if (!_loadCommitted)
            {
                throw new InvalidOperationException("A recipe content transaction is already active.");
            }
            _loadCommitted = false;
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
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
            _loadSnapshots.Clear();
            _loadMutationOrder.Clear();
            _loadOwnershipAdded.Clear();
            _loadCommitted = true;
            if (failures is not null)
            {
                throw new AggregateException("Recipe content rollback was incomplete.", failures);
            }
        }

        private IReadOnlyList<RecipeIngredientDefinition> ReadIngredients(nint product)
        {
            var list = _api.ReadObjectReference(product, _recipeListField);
            if (list == 0)
            {
                return [];
            }

            var result = new List<RecipeIngredientDefinition>(GetListCount(list));
            for (var index = 0; index < GetListCount(list); ++index)
            {
                var ingredient = GetListItem(list, index);
                if (ingredient == 0)
                {
                    continue;
                }
                var item = _api.ReadObjectReference(ingredient, _ingredientItemField);
                var count = _api.ReadInt32(ingredient, _ingredientCountField);
                result.Add(new RecipeIngredientDefinition(
                    new UnityObject(item),
                    item == 0 ? string.Empty : ReadItemId(item),
                    count));
            }
            return result;
        }

        private void ReplaceIngredients(
            nint product,
            IReadOnlyList<RecipeIngredientPatch> ingredients)
        {
            if (ingredients.Count > 100)
            {
                throw new ArgumentException("A recipe may contain at most 100 ingredients.");
            }

            var list = NewConstructedObject(_recipeListClass);
            // Attach immediately: the product now roots the list in IL2CPP's object graph.
            _api.WriteObjectReference(product, _recipeListField, list);
            foreach (var definition in ingredients)
            {
                ArgumentNullException.ThrowIfNull(definition);
                ValidateItem(definition.Item, nameof(definition.Item));
                if (definition.Count <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(ingredients),
                        "Recipe ingredient counts must be positive.");
                }

                var ingredient = NewConstructedObject(_ingredientClass);
                // List<T>.Add roots the ingredient before any further managed calls.
                AddListItem(list, ingredient);
                _api.WriteObjectReference(
                    ingredient,
                    _ingredientItemField,
                    definition.Item.Pointer);
                _api.WriteInt32(ingredient, _ingredientCountField, definition.Count);
            }
        }

        private nint NewConstructedObject(nint klass)
        {
            var instance = _api.NewObject(klass);
            var constructor = _api.FindMethod(klass, ".ctor", 0);
            if (constructor == 0)
            {
                throw new MissingMethodException("IL2CPP class has no parameterless constructor.");
            }
            _ = _api.RuntimeInvoke(constructor, instance, 0);
            return instance;
        }

        private int GetListCount(nint list)
        {
            var boxed = _api.RuntimeInvoke(
                RequireMethod(_api, _recipeListClass, "get_Count", 0),
                list,
                0);
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("Recipe List<T>.Count returned null.");
        }

        private unsafe nint GetListItem(nint list, int index)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&index);
            return _api.RuntimeInvoke(
                RequireMethod(_api, _recipeListClass, "get_Item", 1),
                list,
                (nint)arguments);
        }

        private unsafe void AddListItem(nint list, nint item)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = item;
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, _recipeListClass, "Add", 1),
                list,
                (nint)arguments);
        }

        private bool ReadBooleanProperty(nint instance, string getter)
        {
            var boxed = _api.RuntimeInvoke(
                RequireMethod(_api, _itemClass, getter, 0),
                instance,
                0);
            var value = _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private string ReadItemId(nint item)
        {
            var value = _api.RuntimeInvoke(
                RequireMethod(_api, _itemClass, "GetItemID", 0),
                item,
                0);
            return value == 0 ? string.Empty : _api.ReadString(value);
        }

        private nint ReadReference(nint product, string field) =>
            _api.ReadObjectReference(product, RequireField(_api, _itemClass, field));

        private void WriteReference(nint product, string field, nint value) =>
            _api.WriteObjectReference(product, RequireField(_api, _itemClass, field), value);

        private void ValidateOptionalItem(UnityObject item, string parameter)
        {
            if (!item.IsNull)
            {
                ValidateItem(item, parameter);
            }
        }

        private void ValidateOptionalBuilding(UnityObject building)
        {
            if (!building.IsNull &&
                !_api.IsAssignableFrom(_buildingClass, _api.GetObjectClass(building.Pointer)))
            {
                throw new ArgumentException("Recipe producer is not a T_BuildingItemSO.");
            }
        }

        private void ValidateItem(UnityObject item, string parameter)
        {
            if (item.IsNull || !_api.IsAssignableFrom(_itemClass, _api.GetObjectClass(item.Pointer)))
            {
                throw new ArgumentException("Recipe item is not a T_ItemSO.", parameter);
            }
        }

        private readonly record struct MutationClaim(bool OwnershipAdded, bool SnapshotAdded);
    }
}
