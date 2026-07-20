using System.Globalization;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class UpgradeRegistry : IUpgradeRegistry
    {
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _managerClass;
        private readonly UpgradeGroupCatalog _groups;
        private readonly UpgradeTabCatalog _tabs;

        public UpgradeRegistry(string ownerId, IUnsafeIl2CppApi api)
        {
            _api = api;
            _managerClass = RequireClass(api, "UpgradeManager");
            _groups = new UpgradeGroupCatalog(ownerId, api, RefreshCachesIfReady);
            _tabs = new UpgradeTabCatalog(ownerId, api, RefreshCachesIfReady);
        }

        public int GroupCount => _groups.Count;
        public int TabCount => _tabs.Count;
        public UnityObject CloneGroup(UnityObject source, int newTypeId) =>
            _groups.Clone(source, newTypeId);
        public UnityObject CloneTab(UnityObject source, int newCategoryId) =>
            _tabs.Clone(source, newCategoryId);
        public UnityObject FindGroupByType(int typeId) => _groups.Find(typeId);
        public UnityObject FindTabByCategory(int categoryId) => _tabs.Find(categoryId);
        public UpgradeGroupDefinition DescribeGroup(UnityObject groupScriptableObject) =>
            _groups.Describe(groupScriptableObject);
        public UpgradeTabDefinition DescribeTab(UnityObject tabScriptableObject) =>
            _tabs.Describe(tabScriptableObject);
        public IReadOnlyList<UpgradeGroupDefinition> GetGroups() => _groups.GetAll();
        public IReadOnlyList<UpgradeTabDefinition> GetTabs() => _tabs.GetAll();
        public void UpdateGroup(UnityObject groupScriptableObject, UpgradeGroupPatch patch) =>
            _groups.Update(groupScriptableObject, patch);
        public void UpdateTab(UnityObject tabScriptableObject, UpgradeTabPatch patch) =>
            _tabs.Update(tabScriptableObject, patch);
        public IUpgradeGroupRegistration RegisterGroup(UnityObject groupScriptableObject) =>
            _groups.Register(groupScriptableObject);
        public IUpgradeTabRegistration RegisterTab(UnityObject tabScriptableObject) =>
            _tabs.Register(tabScriptableObject);

        public bool IsManagerReady
        {
            get
            {
                EnsureMainThread();
                return GetManager() != 0;
            }
        }

        public int GetLevel(int typeId)
        {
            EnsureMainThread();
            ValidateTypeId(typeId, nameof(typeId));
            var manager = RequireManager();
            var boxed = InvokeIntArgument(manager, "GetUpgradeLevel", typeId);
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("UpgradeManager.GetUpgradeLevel returned null.");
        }

        public bool CanUpgrade(int typeId)
        {
            EnsureMainThread();
            ValidateTypeId(typeId, nameof(typeId));
            var manager = RequireManager();
            var boxed = InvokeIntArgument(manager, "CanUpgrade", typeId);
            var value = _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadByte(value) != 0
                : throw new InvalidDataException("UpgradeManager.CanUpgrade returned null.");
        }

        public void RequestUpgrade(int typeId)
        {
            EnsureMainThread();
            ValidateTypeId(typeId, nameof(typeId));
            _ = InvokeIntArgument(RequireManager(), "RequestUpgrade", typeId);
        }

        internal void BeginTransaction()
        {
            _groups.BeginTransaction();
            try { _tabs.BeginTransaction(); }
            catch
            {
                _groups.RollbackTransaction();
                throw;
            }
        }

        internal void CommitTransaction()
        {
            _tabs.CommitTransaction();
            _groups.CommitTransaction();
        }

        internal void RollbackTransaction()
        {
            List<Exception>? failures = null;
            Try(_tabs.RollbackTransaction);
            Try(_groups.RollbackTransaction);
            if (failures is not null)
                throw new AggregateException("Upgrade content rollback was incomplete.", failures);

            void Try(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }

        private nint GetManager() => _api.RuntimeInvoke(
            RequireMethod(_api, _managerClass, "get_Instance", 0),
            0,
            0);

        private nint RequireManager()
        {
            var manager = GetManager();
            return manager != 0
                ? manager
                : throw new InvalidOperationException("UpgradeManager.Instance is unavailable.");
        }

        private void RefreshCachesIfReady()
        {
            var manager = GetManager();
            if (manager == 0) return;
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, _managerClass, "InitCache", 0),
                manager,
                0);
        }

        private unsafe nint InvokeIntArgument(nint manager, string methodName, int value)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&value);
            return _api.RuntimeInvoke(
                RequireMethod(_api, _managerClass, methodName, 1),
                manager,
                (nint)arguments);
        }

        private sealed class UpgradeGroupCatalog
            : CentralCatalogRegistry<UpgradeGroupDefinition>
        {
            private readonly Action _refresh;
            private readonly nint _spriteClass;
            private readonly nint _levelClass;
            private readonly nint _changeClass;

            public UpgradeGroupCatalog(
                string ownerId,
                IUnsafeIl2CppApi api,
                Action refresh)
                : base(ownerId, api, "UpgradeGroupSO", "get_AllUpgradeGroups")
            {
                _refresh = refresh;
                _spriteClass = RequireUnityType(api, "Sprite");
                _levelClass = RequireClass(api, "UpgradeLevelData");
                _changeClass = RequireClass(api, "UpgradeChangeEntry");
            }

            protected override string Kind => "UpgradeGroupSO";
            public int Count => CountCore;

            public UnityObject Clone(UnityObject source, int newTypeId)
            {
                ValidateTypeId(newTypeId, nameof(newTypeId));
                return CloneCore(source, FormatIdentity(newTypeId));
            }

            public UnityObject Find(int typeId)
            {
                ValidateTypeId(typeId, nameof(typeId));
                return FindCore(FormatIdentity(typeId));
            }

            public UpgradeGroupDefinition Describe(UnityObject asset) => DescribeChecked(asset);
            public IReadOnlyList<UpgradeGroupDefinition> GetAll() => GetAllCore();

            public void Update(UnityObject asset, UpgradeGroupPatch patch)
            {
                ArgumentNullException.ThrowIfNull(patch);
                UpdateCore(asset, () => ApplyPatch(asset.Pointer, patch));
            }

            public IUpgradeGroupRegistration Register(UnityObject asset)
            {
                var record = RegisterCore(asset);
                if (record.Handle is IUpgradeGroupRegistration existing) return existing;
                var handle = new UpgradeGroupRegistration(record);
                record.Handle = handle;
                return handle;
            }

            protected override string ReadIdentity(nint asset) =>
                FormatIdentity(ReadInt(asset, AssetClass, "upgradeType"));

            protected override void WriteIdentity(nint asset, string identity) =>
                WriteInt(asset, AssetClass, "upgradeType", ParseIdentity(identity));

            protected override void ValidateIdentity(string identity) =>
                ValidateTypeId(ParseIdentity(identity), nameof(identity));

            protected override UpgradeGroupDefinition DescribeCore(UnityObject asset)
            {
                var pointer = asset.Pointer;
                return new UpgradeGroupDefinition(
                    asset,
                    ReadInt(pointer, AssetClass, "upgradeType"),
                    ReadString(pointer, AssetClass, "upgradeNameKey"),
                    ReadReference(pointer, AssetClass, "icon"),
                    ReadInt(pointer, AssetClass, "category"),
                    ReadString(pointer, AssetClass, "levelPrefixKey"),
                    ReadLevels(pointer),
                    ReadInt(pointer, AssetClass, "linkedItemType"));
            }

            protected override void RestoreCore(UpgradeGroupDefinition definition)
            {
                var pointer = definition.Asset.Pointer;
                WriteString(pointer, AssetClass, "upgradeNameKey", definition.NameKey);
                WriteReference(pointer, AssetClass, "icon", definition.Icon);
                WriteInt(pointer, AssetClass, "category", definition.CategoryId);
                WriteString(pointer, AssetClass, "levelPrefixKey", definition.LevelPrefixKey);
                ReplaceLevels(pointer, definition.Levels, validate: false);
                WriteInt(pointer, AssetClass, "linkedItemType", definition.LinkedItemTypeId);
            }

            protected override void RefreshCaches() => _refresh();

            private void ApplyPatch(nint pointer, UpgradeGroupPatch patch)
            {
                if (patch.NameKey is not null)
                {
                    ValidateDisplayText(patch.NameKey, nameof(patch.NameKey));
                    WriteString(pointer, AssetClass, "upgradeNameKey", patch.NameKey);
                }
                if (patch.Icon is not null)
                {
                    ValidateOptionalAsset(patch.Icon.Value, _spriteClass, "Upgrade icon", Api);
                    WriteReference(pointer, AssetClass, "icon", patch.Icon.Value);
                }
                if (patch.CategoryId is not null)
                {
                    ValidateTypeId(patch.CategoryId.Value, nameof(patch.CategoryId));
                    WriteInt(pointer, AssetClass, "category", patch.CategoryId.Value);
                }
                if (patch.LevelPrefixKey is not null)
                {
                    ValidateDisplayText(patch.LevelPrefixKey, nameof(patch.LevelPrefixKey));
                    WriteString(pointer, AssetClass, "levelPrefixKey", patch.LevelPrefixKey);
                }
                if (patch.Levels is not null) ReplaceLevels(pointer, patch.Levels, validate: true);
                if (patch.LinkedItemTypeId is not null)
                {
                    ValidateTypeId(patch.LinkedItemTypeId.Value, nameof(patch.LinkedItemTypeId));
                    WriteInt(pointer, AssetClass, "linkedItemType", patch.LinkedItemTypeId.Value);
                }
            }

            private IReadOnlyList<UpgradeLevelDefinition> ReadLevels(nint group)
            {
                var list = Api.ReadObjectReference(group, AssetField("levels"));
                if (list == 0) return [];
                var count = GetCount(list);
                var result = new List<UpgradeLevelDefinition>(count);
                for (var index = 0; index < count; ++index)
                {
                    var level = GetAt(list, index);
                    if (level == 0) continue;
                    result.Add(new UpgradeLevelDefinition(
                        ReadString(level, _levelClass, "titleKey"),
                        ReadString(level, _levelClass, "descriptionKey"),
                        ReadChanges(level),
                        ReadInt(level, _levelClass, "requiredFactoryLevel"),
                        ReadInt(level, _levelClass, "cost"),
                        ReadBool(level, _levelClass, "availableInDemo"),
                        ReadReference(level, _levelClass, "levelIcon")));
                }
                return result;
            }

            private IReadOnlyList<UpgradeChangeDefinition> ReadChanges(nint level)
            {
                var list = Api.ReadObjectReference(level, Field(_levelClass, "changes"));
                if (list == 0) return [];
                var count = GetCount(list);
                var result = new List<UpgradeChangeDefinition>(count);
                for (var index = 0; index < count; ++index)
                {
                    var change = GetAt(list, index);
                    if (change == 0) continue;
                    result.Add(new UpgradeChangeDefinition(
                        ReadString(change, _changeClass, "textKey"),
                        ReadString(change, _changeClass, "oldValue"),
                        ReadString(change, _changeClass, "newValue")));
                }
                return result;
            }

            private void ReplaceLevels(
                nint group,
                IReadOnlyList<UpgradeLevelDefinition> levels,
                bool validate)
            {
                if (validate && levels.Count is < 1 or > 100)
                    throw new ArgumentException("An upgrade group must define between 1 and 100 levels.");
                var targetField = AssetField("levels");
                var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
                Api.WriteObjectReference(group, targetField, list);
                foreach (var definition in levels)
                {
                    ArgumentNullException.ThrowIfNull(definition);
                    if (validate) ValidateLevel(definition);
                    var level = NewConstructedObject(_levelClass);
                    WriteString(level, _levelClass, "titleKey", definition.TitleKey);
                    WriteString(level, _levelClass, "descriptionKey", definition.DescriptionKey);
                    WriteInt(
                        level,
                        _levelClass,
                        "requiredFactoryLevel",
                        definition.RequiredFactoryLevel);
                    WriteInt(level, _levelClass, "cost", definition.Cost);
                    WriteBool(level, _levelClass, "availableInDemo", definition.AvailableInDemo);
                    WriteReference(level, _levelClass, "levelIcon", definition.Icon);
                    ReplaceChanges(level, definition.Changes);
                    Add(list, level);
                }
            }

            private void ReplaceChanges(
                nint level,
                IReadOnlyList<UpgradeChangeDefinition> changes)
            {
                var targetField = Field(_levelClass, "changes");
                var list = NewConstructedObject(Api.GetFieldTypeClass(targetField));
                Api.WriteObjectReference(level, targetField, list);
                foreach (var definition in changes)
                {
                    var change = NewConstructedObject(_changeClass);
                    WriteString(change, _changeClass, "textKey", definition.TextKey);
                    WriteString(change, _changeClass, "oldValue", definition.OldValue);
                    WriteString(change, _changeClass, "newValue", definition.NewValue);
                    Add(list, change);
                }
            }

            private void ValidateLevel(UpgradeLevelDefinition level)
            {
                ValidateDisplayText(level.TitleKey, nameof(level.TitleKey));
                ValidateDisplayText(level.DescriptionKey, nameof(level.DescriptionKey));
                RequireNonNegative(level.RequiredFactoryLevel, nameof(level.RequiredFactoryLevel));
                RequireNonNegative(level.Cost, nameof(level.Cost));
                ValidateOptionalAsset(level.Icon, _spriteClass, "Upgrade level icon", Api);
                if (level.Changes.Count > 100)
                    throw new ArgumentException("An upgrade level may contain at most 100 changes.");
                foreach (var change in level.Changes)
                {
                    ArgumentNullException.ThrowIfNull(change);
                    ValidateDisplayText(change.TextKey, nameof(change.TextKey));
                    ValidateDisplayText(change.OldValue, nameof(change.OldValue));
                    ValidateDisplayText(change.NewValue, nameof(change.NewValue));
                }
            }

            private sealed class UpgradeGroupRegistration(RegistrationRecord record)
                : IUpgradeGroupRegistration
            {
                public int TypeId => ParseIdentity(record.Identity);
                public UnityObject Asset => record.Asset;
                public int Index => record.Index;
            }
        }

        private sealed class UpgradeTabCatalog
            : CentralCatalogRegistry<UpgradeTabDefinition>
        {
            private readonly Action _refresh;
            private readonly nint _groupClass;

            public UpgradeTabCatalog(
                string ownerId,
                IUnsafeIl2CppApi api,
                Action refresh)
                : base(ownerId, api, "UpgradeTabSO", "get_AllUpgradeTabs")
            {
                _refresh = refresh;
                _groupClass = RequireClass(api, "UpgradeGroupSO");
            }

            protected override string Kind => "UpgradeTabSO";
            public int Count => CountCore;

            public UnityObject Clone(UnityObject source, int newCategoryId)
            {
                ValidateTypeId(newCategoryId, nameof(newCategoryId));
                return CloneCore(source, FormatIdentity(newCategoryId));
            }

            public UnityObject Find(int categoryId)
            {
                ValidateTypeId(categoryId, nameof(categoryId));
                return FindCore(FormatIdentity(categoryId));
            }

            public UpgradeTabDefinition Describe(UnityObject asset) => DescribeChecked(asset);
            public IReadOnlyList<UpgradeTabDefinition> GetAll() => GetAllCore();

            public void Update(UnityObject asset, UpgradeTabPatch patch)
            {
                ArgumentNullException.ThrowIfNull(patch);
                UpdateCore(asset, () => ApplyPatch(asset.Pointer, patch));
            }

            public IUpgradeTabRegistration Register(UnityObject asset)
            {
                var record = RegisterCore(asset);
                if (record.Handle is IUpgradeTabRegistration existing) return existing;
                var handle = new UpgradeTabRegistration(record);
                record.Handle = handle;
                return handle;
            }

            protected override string ReadIdentity(nint asset) =>
                FormatIdentity(ReadInt(asset, AssetClass, "category"));

            protected override void WriteIdentity(nint asset, string identity) =>
                WriteInt(asset, AssetClass, "category", ParseIdentity(identity));

            protected override void ValidateIdentity(string identity) =>
                ValidateTypeId(ParseIdentity(identity), nameof(identity));

            protected override UpgradeTabDefinition DescribeCore(UnityObject asset)
            {
                var pointer = asset.Pointer;
                return new UpgradeTabDefinition(
                    asset,
                    ReadInt(pointer, AssetClass, "category"),
                    ReadString(pointer, AssetClass, "tabName"),
                    ReadReferenceList(pointer, AssetClass, "groups"));
            }

            protected override void RestoreCore(UpgradeTabDefinition definition)
            {
                var pointer = definition.Asset.Pointer;
                WriteString(pointer, AssetClass, "tabName", definition.Name);
                ReplaceReferenceList(
                    pointer,
                    AssetClass,
                    "groups",
                    definition.Groups,
                    _groupClass);
            }

            protected override void RefreshCaches() => _refresh();

            private void ApplyPatch(nint pointer, UpgradeTabPatch patch)
            {
                if (patch.Name is not null)
                {
                    ValidateDisplayText(patch.Name, nameof(patch.Name));
                    WriteString(pointer, AssetClass, "tabName", patch.Name);
                }
                if (patch.Groups is not null)
                {
                    if (patch.Groups.Count > 1000 ||
                        patch.Groups.Select(value => value.Pointer).Distinct().Count() !=
                        patch.Groups.Count)
                        throw new ArgumentException("Upgrade tab groups must be unique.");
                    ReplaceReferenceList(
                        pointer,
                        AssetClass,
                        "groups",
                        patch.Groups,
                        _groupClass);
                }
            }

            private sealed class UpgradeTabRegistration(RegistrationRecord record)
                : IUpgradeTabRegistration
            {
                public int CategoryId => ParseIdentity(record.Identity);
                public UnityObject Asset => record.Asset;
                public int Index => record.Index;
            }
        }

        private static string FormatIdentity(int value) =>
            value.ToString(CultureInfo.InvariantCulture);

        private static int ParseIdentity(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new ArgumentException("Upgrade identity is not a non-negative integer.");

        private static void ValidateTypeId(int value, string parameter)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
