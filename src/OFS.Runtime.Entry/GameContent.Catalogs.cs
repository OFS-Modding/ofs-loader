using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class CompanyRegistry(string ownerId, IUnsafeIl2CppApi api)
        : CentralCatalogRegistry<CompanyDefinition>(
            ownerId,
            api,
            "CompanySO",
            "get_AllCompanies"), ICompanyRegistry
    {
        private readonly nint _spriteClass = RequireUnityType(api, "Sprite");
        protected override string Kind => "CompanySO";
        public int Count => CountCore;

        public UnityObject Clone(UnityObject source, string newCompanyId) =>
            CloneCore(source, newCompanyId);

        public UnityObject FindById(string companyId) => FindCore(companyId);

        public CompanyDefinition Describe(UnityObject companyScriptableObject) =>
            DescribeChecked(companyScriptableObject);

        public IReadOnlyList<CompanyDefinition> GetAll() => GetAllCore();

        public void Update(UnityObject companyScriptableObject, CompanyPatch patch)
        {
            ArgumentNullException.ThrowIfNull(patch);
            UpdateCore(companyScriptableObject, () => ApplyPatch(companyScriptableObject.Pointer, patch));
        }

        public ICompanyRegistration Register(UnityObject companyScriptableObject)
        {
            var record = RegisterCore(companyScriptableObject);
            if (record.Handle is ICompanyRegistration existing) return existing;
            var handle = new CompanyRegistration(record);
            record.Handle = handle;
            return handle;
        }

        protected override string ReadIdentity(nint asset) =>
            ReadString(asset, AssetClass, "companyId");

        protected override void WriteIdentity(nint asset, string identity) =>
            WriteString(asset, AssetClass, "companyId", identity);

        protected override void ValidateIdentity(string identity) =>
            ValidateTextId(identity, "Company", 100);

        protected override CompanyDefinition DescribeCore(UnityObject asset) => new(
            asset,
            ReadIdentity(asset.Pointer),
            ReadString(asset.Pointer, AssetClass, "companyName"),
            ReadString(asset.Pointer, AssetClass, "companyDescKey"),
            ReadReference(asset.Pointer, AssetClass, "companyLogo"),
            ReadReference(asset.Pointer, AssetClass, "companyBackground"),
            ReadColor(asset.Pointer, AssetClass, "logoColor"),
            ReadCategories(asset.Pointer));

        protected override void RestoreCore(CompanyDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteString(pointer, AssetClass, "companyName", definition.Name);
            WriteString(pointer, AssetClass, "companyDescKey", definition.DescriptionKey);
            WriteReference(pointer, AssetClass, "companyLogo", definition.Logo);
            WriteReference(pointer, AssetClass, "companyBackground", definition.Background);
            WriteColor(pointer, AssetClass, "logoColor", definition.LogoColor);
            ReplaceCategories(pointer, definition.InterestedCategories, validate: false);
        }

        private void ApplyPatch(nint pointer, CompanyPatch patch)
        {
            if (patch.Name is not null)
            {
                ValidateDisplayText(patch.Name, nameof(patch.Name));
                WriteString(pointer, AssetClass, "companyName", patch.Name);
            }
            if (patch.DescriptionKey is not null)
            {
                ValidateDisplayText(patch.DescriptionKey, nameof(patch.DescriptionKey));
                WriteString(pointer, AssetClass, "companyDescKey", patch.DescriptionKey);
            }
            if (patch.Logo is not null)
            {
                ValidateOptionalAsset(patch.Logo.Value, _spriteClass, "Company logo", Api);
                WriteReference(pointer, AssetClass, "companyLogo", patch.Logo.Value);
            }
            if (patch.Background is not null)
            {
                ValidateOptionalAsset(
                    patch.Background.Value,
                    _spriteClass,
                    "Company background",
                    Api);
                WriteReference(pointer, AssetClass, "companyBackground", patch.Background.Value);
            }
            if (patch.LogoColor is not null)
            {
                ValidateColor(patch.LogoColor.Value);
                WriteColor(pointer, AssetClass, "logoColor", patch.LogoColor.Value);
            }
            if (patch.InterestedCategories is not null)
                ReplaceCategories(pointer, patch.InterestedCategories, validate: true);
        }

        private IReadOnlyList<ItemFilter> ReadCategories(nint pointer) =>
            ReadEnumList(pointer, AssetClass, "interestedCategories")
                .Select(value => (ItemFilter)value)
                .ToArray();

        private void ReplaceCategories(
            nint pointer,
            IReadOnlyList<ItemFilter> values,
            bool validate)
        {
            if (validate)
            {
                if (values.Count > Enum.GetValues<ItemFilter>().Length ||
                    values.Distinct().Count() != values.Count)
                    throw new ArgumentException("Company categories must be unique valid filters.");
                foreach (var value in values)
                    if (!Enum.IsDefined(value))
                        throw new ArgumentOutOfRangeException(nameof(values));
            }
            ReplaceEnumList(
                pointer,
                AssetClass,
                "interestedCategories",
                values.Select(value => (int)value).ToArray());
        }

        private sealed class CompanyRegistration(RegistrationRecord record)
            : ICompanyRegistration
        {
            public string CompanyId => record.Identity;
            public UnityObject Asset => record.Asset;
            public int Index => record.Index;
        }
    }

    private sealed class FoodRegistry(string ownerId, IUnsafeIl2CppApi api)
        : CentralCatalogRegistry<FoodDefinition>(
            ownerId,
            api,
            "T_FoodSO",
            "get_AllFoodSOs"), IFoodRegistry
    {
        private readonly nint _spriteClass = RequireUnityType(api, "Sprite");
        private readonly nint _audioClipClass = RequireUnityType(api, "AudioClip");
        private readonly nint _buffClass = RequireClass(api, "BuffOption");
        protected override string Kind => "T_FoodSO";
        public int Count => CountCore;

        public UnityObject Clone(UnityObject source, string newFoodId) =>
            CloneCore(source, newFoodId);

        public UnityObject FindById(string foodId) => FindCore(foodId);

        public FoodDefinition Describe(UnityObject foodScriptableObject) =>
            DescribeChecked(foodScriptableObject);

        public IReadOnlyList<FoodDefinition> GetAll() => GetAllCore();

        public void Update(UnityObject foodScriptableObject, FoodPatch patch)
        {
            ArgumentNullException.ThrowIfNull(patch);
            UpdateCore(foodScriptableObject, () => ApplyPatch(foodScriptableObject.Pointer, patch));
        }

        public IFoodRegistration Register(UnityObject foodScriptableObject)
        {
            var record = RegisterCore(foodScriptableObject);
            if (record.Handle is IFoodRegistration existing) return existing;
            var handle = new FoodRegistration(record);
            record.Handle = handle;
            return handle;
        }

        protected override string ReadIdentity(nint asset) =>
            ReadString(asset, AssetClass, "foodID");

        protected override void WriteIdentity(nint asset, string identity) =>
            WriteString(asset, AssetClass, "foodID", identity);

        protected override void ValidateIdentity(string identity) =>
            ValidateTextId(identity, "Food", 100);

        protected override FoodDefinition DescribeCore(UnityObject asset)
        {
            var pointer = asset.Pointer;
            return new FoodDefinition(
                asset,
                ReadIdentity(pointer),
                ReadString(pointer, AssetClass, "Name"),
                ReadString(pointer, AssetClass, "Description"),
                ReadInt(pointer, AssetClass, "Price"),
                ReadReference(pointer, AssetClass, "Icon"),
                ReadReference(pointer, AssetClass, "CategoryIcon"),
                ReadBool(pointer, AssetClass, "isAlcohol"),
                ReadInt(pointer, AssetClass, "alcoholAmount"),
                ReadReference(pointer, AssetClass, "eatClip"),
                ReadFloat(pointer, AssetClass, "eatClipVolume"),
                (FoodConsumptionKind)ReadInt(pointer, AssetClass, "consumingType"),
                ReadFloat(pointer, AssetClass, "consumingTime"),
                ReadBuffs(pointer));
        }

        protected override void RestoreCore(FoodDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteString(pointer, AssetClass, "Name", definition.Name);
            WriteString(pointer, AssetClass, "Description", definition.Description);
            WriteInt(pointer, AssetClass, "Price", definition.Price);
            WriteReference(pointer, AssetClass, "Icon", definition.Icon);
            WriteReference(pointer, AssetClass, "CategoryIcon", definition.CategoryIcon);
            WriteBool(pointer, AssetClass, "isAlcohol", definition.IsAlcohol);
            WriteInt(pointer, AssetClass, "alcoholAmount", definition.AlcoholAmount);
            WriteReference(pointer, AssetClass, "eatClip", definition.EatClip);
            WriteFloat(pointer, AssetClass, "eatClipVolume", definition.EatClipVolume);
            WriteInt(pointer, AssetClass, "consumingType", (int)definition.ConsumptionKind);
            WriteFloat(pointer, AssetClass, "consumingTime", definition.ConsumptionTime);
            ReplaceBuffs(pointer, definition.Buffs, validate: false);
        }

        private void ApplyPatch(nint pointer, FoodPatch patch)
        {
            if (patch.Name is not null)
            {
                ValidateDisplayText(patch.Name, nameof(patch.Name));
                WriteString(pointer, AssetClass, "Name", patch.Name);
            }
            if (patch.Description is not null)
            {
                ValidateDisplayText(patch.Description, nameof(patch.Description), 4000);
                WriteString(pointer, AssetClass, "Description", patch.Description);
            }
            if (patch.Price is not null)
            {
                RequireNonNegative(patch.Price.Value, nameof(patch.Price));
                WriteInt(pointer, AssetClass, "Price", patch.Price.Value);
            }
            if (patch.Icon is not null)
            {
                ValidateOptionalAsset(patch.Icon.Value, _spriteClass, "Food icon", Api);
                WriteReference(pointer, AssetClass, "Icon", patch.Icon.Value);
            }
            if (patch.CategoryIcon is not null)
            {
                ValidateOptionalAsset(
                    patch.CategoryIcon.Value,
                    _spriteClass,
                    "Food category icon",
                    Api);
                WriteReference(pointer, AssetClass, "CategoryIcon", patch.CategoryIcon.Value);
            }
            if (patch.IsAlcohol is not null)
                WriteBool(pointer, AssetClass, "isAlcohol", patch.IsAlcohol.Value);
            if (patch.AlcoholAmount is not null)
            {
                RequireNonNegative(patch.AlcoholAmount.Value, nameof(patch.AlcoholAmount));
                WriteInt(pointer, AssetClass, "alcoholAmount", patch.AlcoholAmount.Value);
            }
            if (patch.EatClip is not null)
            {
                ValidateOptionalAsset(
                    patch.EatClip.Value,
                    _audioClipClass,
                    "Food audio clip",
                    Api);
                WriteReference(pointer, AssetClass, "eatClip", patch.EatClip.Value);
            }
            if (patch.EatClipVolume is not null)
            {
                RequireFiniteRange(patch.EatClipVolume.Value, 0f, 1f, nameof(patch.EatClipVolume));
                WriteFloat(pointer, AssetClass, "eatClipVolume", patch.EatClipVolume.Value);
            }
            if (patch.ConsumptionKind is not null)
            {
                if (!Enum.IsDefined(patch.ConsumptionKind.Value))
                    throw new ArgumentOutOfRangeException(nameof(patch.ConsumptionKind));
                WriteInt(pointer, AssetClass, "consumingType", (int)patch.ConsumptionKind.Value);
            }
            if (patch.ConsumptionTime is not null)
            {
                RequireFiniteRange(
                    patch.ConsumptionTime.Value,
                    0.01f,
                    3600f,
                    nameof(patch.ConsumptionTime));
                WriteFloat(pointer, AssetClass, "consumingTime", patch.ConsumptionTime.Value);
            }
            if (patch.Buffs is not null) ReplaceBuffs(pointer, patch.Buffs, validate: true);
        }

        private IReadOnlyList<FoodBuffDefinition> ReadBuffs(nint food)
        {
            var list = Api.ReadObjectReference(food, AssetField("buffOptions"));
            if (list == 0) return [];
            var count = GetCount(list);
            var result = new List<FoodBuffDefinition>(count);
            for (var index = 0; index < count; ++index)
            {
                var buff = GetAt(list, index);
                if (buff == 0) continue;
                result.Add(new FoodBuffDefinition(
                    (FoodBuffKind)ReadInt(buff, _buffClass, "type"),
                    ReadFloat(buff, _buffClass, "value"),
                    ReadFloat(buff, _buffClass, "durationSeconds")));
            }
            return result;
        }

        private void ReplaceBuffs(
            nint food,
            IReadOnlyList<FoodBuffDefinition> buffs,
            bool validate)
        {
            if (validate)
            {
                if (buffs.Count is < 1 or > 100)
                    throw new ArgumentException("Food must define between 1 and 100 buff choices.");
                foreach (var buff in buffs)
                {
                    ArgumentNullException.ThrowIfNull(buff);
                    if (!Enum.IsDefined(buff.Kind))
                        throw new ArgumentOutOfRangeException(nameof(buffs));
                    RequireFinite(buff.Value, nameof(buffs));
                    RequireFiniteRange(buff.DurationSeconds, 0f, 86400f, nameof(buffs));
                }
            }
            var targetField = AssetField("buffOptions");
            var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
            Api.WriteObjectReference(food, targetField, list);
            foreach (var definition in buffs)
            {
                var buff = NewConstructedObject(_buffClass);
                WriteInt(buff, _buffClass, "type", (int)definition.Kind);
                WriteFloat(buff, _buffClass, "value", definition.Value);
                WriteFloat(buff, _buffClass, "durationSeconds", definition.DurationSeconds);
                Add(list, buff);
            }
        }

        private sealed class FoodRegistration(RegistrationRecord record) : IFoodRegistration
        {
            public string FoodId => record.Identity;
            public UnityObject Asset => record.Asset;
            public int Index => record.Index;
        }
    }

    private sealed class BuildingCategoryRegistry(string ownerId, IUnsafeIl2CppApi api)
        : CentralCatalogRegistry<BuildingCategoryDefinition>(
            ownerId,
            api,
            "T_BuildingCategorySO",
            "get_AllBuildingCategories"), IBuildingCategoryRegistry
    {
        private readonly nint _spriteClass = RequireUnityType(api, "Sprite");
        private readonly nint _buildingClass = RequireClass(api, "T_BuildingItemSO");
        protected override string Kind => "T_BuildingCategorySO";
        public int Count => CountCore;

        public UnityObject Clone(UnityObject source, string newCategoryId) =>
            CloneCore(source, newCategoryId);

        public UnityObject FindById(string categoryId) => FindCore(categoryId);

        public BuildingCategoryDefinition Describe(UnityObject categoryScriptableObject) =>
            DescribeChecked(categoryScriptableObject);

        public IReadOnlyList<BuildingCategoryDefinition> GetAll() => GetAllCore();

        public void Update(UnityObject categoryScriptableObject, BuildingCategoryPatch patch)
        {
            ArgumentNullException.ThrowIfNull(patch);
            UpdateCore(categoryScriptableObject, () => ApplyPatch(categoryScriptableObject.Pointer, patch));
        }

        public IBuildingCategoryRegistration Register(UnityObject categoryScriptableObject)
        {
            var record = RegisterCore(categoryScriptableObject);
            if (record.Handle is IBuildingCategoryRegistration existing) return existing;
            var handle = new BuildingCategoryRegistration(record);
            record.Handle = handle;
            return handle;
        }

        protected override string ReadIdentity(nint asset) =>
            ReadString(asset, AssetClass, "CategoryId");

        protected override void WriteIdentity(nint asset, string identity) =>
            WriteString(asset, AssetClass, "CategoryId", identity);

        protected override void ValidateIdentity(string identity) =>
            ValidateTextId(identity, "Building category", 100);

        protected override BuildingCategoryDefinition DescribeCore(UnityObject asset)
        {
            var pointer = asset.Pointer;
            return new BuildingCategoryDefinition(
                asset,
                ReadIdentity(pointer),
                ReadString(pointer, AssetClass, "CategoryName"),
                ReadString(pointer, AssetClass, "CategoryDescription"),
                ReadReference(pointer, AssetClass, "CategoryIcon"),
                ReadReferenceList(pointer, AssetClass, "Buildings"),
                ReadBool(pointer, AssetClass, "AllowScrollCycle"),
                ReadInt(pointer, AssetClass, "DefaultSelectedIndex"));
        }

        protected override void RestoreCore(BuildingCategoryDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteString(pointer, AssetClass, "CategoryName", definition.Name);
            WriteString(pointer, AssetClass, "CategoryDescription", definition.Description);
            WriteReference(pointer, AssetClass, "CategoryIcon", definition.Icon);
            ReplaceReferenceList(
                pointer,
                AssetClass,
                "Buildings",
                definition.Buildings,
                _buildingClass);
            WriteBool(pointer, AssetClass, "AllowScrollCycle", definition.AllowScrollCycle);
            WriteInt(pointer, AssetClass, "DefaultSelectedIndex", definition.DefaultSelectedIndex);
        }

        private void ApplyPatch(nint pointer, BuildingCategoryPatch patch)
        {
            var before = DescribeCore(new UnityObject(pointer));
            var buildings = patch.Buildings ?? before.Buildings;
            var selectedIndex = patch.DefaultSelectedIndex ?? before.DefaultSelectedIndex;
            ValidateSelectedIndex(buildings.Count, selectedIndex);
            if (patch.Name is not null)
            {
                ValidateDisplayText(patch.Name, nameof(patch.Name));
                WriteString(pointer, AssetClass, "CategoryName", patch.Name);
            }
            if (patch.Description is not null)
            {
                ValidateDisplayText(patch.Description, nameof(patch.Description), 4000);
                WriteString(pointer, AssetClass, "CategoryDescription", patch.Description);
            }
            if (patch.Icon is not null)
            {
                ValidateOptionalAsset(
                    patch.Icon.Value,
                    _spriteClass,
                    "Building category icon",
                    Api);
                WriteReference(pointer, AssetClass, "CategoryIcon", patch.Icon.Value);
            }
            if (patch.Buildings is not null)
                ReplaceReferenceList(pointer, AssetClass, "Buildings", buildings, _buildingClass);
            if (patch.AllowScrollCycle is not null)
                WriteBool(pointer, AssetClass, "AllowScrollCycle", patch.AllowScrollCycle.Value);
            if (patch.DefaultSelectedIndex is not null)
                WriteInt(pointer, AssetClass, "DefaultSelectedIndex", selectedIndex);
        }

        private static void ValidateSelectedIndex(int count, int index)
        {
            if (count == 0)
            {
                if (index is not 0 and not -1)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return;
            }
            if (index < 0 || index >= count)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        private sealed class BuildingCategoryRegistration(RegistrationRecord record)
            : IBuildingCategoryRegistration
        {
            public string CategoryId => record.Identity;
            public UnityObject Asset => record.Asset;
            public int Index => record.Index;
        }
    }

    private static nint RequireUnityType(IUnsafeIl2CppApi api, string name)
    {
        var result = api.FindClass("UnityEngine.CoreModule.dll", "UnityEngine", name);
        if (result == 0)
            result = api.FindClass("UnityEngine.AudioModule.dll", "UnityEngine", name);
        return result != 0
            ? result
            : throw new TypeLoadException($"UnityEngine.{name} was not found.");
    }

    private static void ValidateTextId(string value, string label, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maxLength)
            throw new ArgumentException($"{label} id must contain at most {maxLength} characters.");
    }

    private static void ValidateDisplayText(string value, string parameter, int maxLength = 500)
    {
        if (value.Length > maxLength)
            throw new ArgumentException($"Text must contain at most {maxLength} characters.", parameter);
    }

    private static void ValidateOptionalAsset(
        UnityObject value,
        nint requiredClass,
        string label,
        IUnsafeIl2CppApi? api = null)
    {
        // A null Unity reference intentionally clears an optional visual/audio field.
        if (value.IsNull) return;
        var runtime = api ?? throw new InvalidOperationException(
            $"{label} validation requires an IL2CPP runtime.");
        if (!runtime.IsAssignableFrom(requiredClass, runtime.GetObjectClass(value.Pointer)))
            throw new ArgumentException($"{label} has the wrong Unity type.");
    }

    private static void ValidateColor(UnityColor color)
    {
        RequireFinite(color.R, nameof(color));
        RequireFinite(color.G, nameof(color));
        RequireFinite(color.B, nameof(color));
        RequireFinite(color.A, nameof(color));
    }

    private static void RequireNonNegative(int value, string parameter)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameter);
    }

    private static void RequireFinite(float value, string parameter)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameter);
    }

    private static void RequireFiniteRange(float value, float minimum, float maximum, string parameter)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameter);
    }
}
