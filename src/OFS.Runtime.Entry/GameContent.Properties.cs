using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class PropertyRegistry
        : CentralCatalogRegistry<PropertyDefinition>, IPropertyRegistry
    {
        private readonly IModLogger _logger;
        private readonly nint _spriteClass;
        private readonly nint _spawnProfileClass;
        private readonly nint _contractClass;
        private readonly nint _propertyManagerClass;
        private readonly nint _contractManagerClass;
        private readonly List<PropertyRegistration> _registrations = [];
        private int _transactionRegistrationStart = -1;
        private int _frame;
        private string? _lastCacheError;

        public PropertyRegistry(
            string ownerId,
            IUnsafeIl2CppApi api,
            IModEvents events,
            IModLogger logger)
            : base(ownerId, api, "PropertyConfigSO", "get_AllPropertyConfigs")
        {
            _logger = logger;
            _spriteClass = RequireUnityType(api, "Sprite");
            _spawnProfileClass = RequireClass(api, "T_ItemSpawnProfile");
            _contractClass = RequireClass(api, "ContractSO");
            _propertyManagerClass = RequireClass(api, "ComputerPropertyManager");
            _contractManagerClass = RequireClass(api, "ComputerContractManager");
            events.FrameUpdate += OnFrameUpdate;
        }

        protected override string Kind => "PropertyConfigSO";
        public int Count => CountCore;

        public UnityObject Clone(UnityObject source, string newConfigId) =>
            CloneCore(source, newConfigId);

        public UnityObject FindById(string configId) => FindCore(configId);

        public PropertyDefinition Describe(UnityObject propertyConfigScriptableObject) =>
            DescribeChecked(propertyConfigScriptableObject);

        public IReadOnlyList<PropertyDefinition> GetAll() => GetAllCore();

        public void Update(UnityObject propertyConfigScriptableObject, PropertyPatch patch)
        {
            ArgumentNullException.ThrowIfNull(patch);
            UpdateCore(propertyConfigScriptableObject, () =>
            {
                ApplyPatch(propertyConfigScriptableObject.Pointer, patch);
                ValidateDefinition(DescribeCore(propertyConfigScriptableObject));
            });
        }

        public IPropertyRegistration Register(UnityObject propertyConfigScriptableObject)
        {
            ValidateDefinition(DescribeChecked(propertyConfigScriptableObject));
            var record = RegisterCore(propertyConfigScriptableObject);
            if (record.Handle is PropertyRegistration existing)
            {
                if (!_registrations.Contains(existing)) _registrations.Add(existing);
                EnsureCaches(existing);
                return existing;
            }
            var handle = new PropertyRegistration(record);
            record.Handle = handle;
            _registrations.Add(handle);
            EnsureCaches(handle);
            return handle;
        }

        internal new void BeginTransaction()
        {
            base.BeginTransaction();
            _transactionRegistrationStart = _registrations.Count;
        }

        internal new void CommitTransaction()
        {
            base.CommitTransaction();
            _transactionRegistrationStart = -1;
        }

        internal new void RollbackTransaction()
        {
            try { base.RollbackTransaction(); }
            finally
            {
                if (_transactionRegistrationStart >= 0 &&
                    _transactionRegistrationStart < _registrations.Count)
                    _registrations.RemoveRange(
                        _transactionRegistrationStart,
                        _registrations.Count - _transactionRegistrationStart);
                _transactionRegistrationStart = -1;
            }
        }

        protected override string ReadIdentity(nint asset) =>
            ReadString(asset, AssetClass, "configId");

        protected override void WriteIdentity(nint asset, string identity) =>
            WriteString(asset, AssetClass, "configId", identity);

        protected override void ValidateIdentity(string identity) =>
            ValidateTextId(identity, "Property", 100);

        protected override PropertyDefinition DescribeCore(UnityObject asset)
        {
            var pointer = asset.Pointer;
            return new PropertyDefinition(
                asset,
                ReadIdentity(pointer),
                ReadString(pointer, AssetClass, "displayName"),
                ReadStringList(pointer, AssetClass, "propertyNames"),
                (PropertyKind)ReadInt(pointer, AssetClass, "propertyType"),
                ReadInt(pointer, AssetClass, "propertyLevel"),
                ReadInt(pointer, AssetClass, "minPrice"),
                ReadInt(pointer, AssetClass, "maxPrice"),
                ReadInt(pointer, AssetClass, "priceRoundingStep"),
                ReadStringList(pointer, AssetClass, "propertyAddresses"),
                ReadIntList(pointer, AssetClass, "propertySizes"),
                ReadReferenceList(pointer, AssetClass, "propertyVisuals"),
                ReadReferenceList(pointer, AssetClass, "loadingBackgrounds"),
                ReadString(pointer, AssetClass, "linkedSceneName"),
                ReadReferenceList(pointer, AssetClass, "itemSpawnProfiles"),
                ReadReferenceList(pointer, AssetClass, "contracts"));
        }

        protected override void RestoreCore(PropertyDefinition definition)
        {
            var pointer = definition.Asset.Pointer;
            WriteString(pointer, AssetClass, "displayName", definition.DisplayNameKey);
            ReplaceStringList(pointer, AssetClass, "propertyNames", definition.PropertyNameKeys);
            WriteInt(pointer, AssetClass, "propertyType", (int)definition.Kind);
            WriteInt(pointer, AssetClass, "propertyLevel", definition.Level);
            WriteInt(pointer, AssetClass, "minPrice", definition.MinPrice);
            WriteInt(pointer, AssetClass, "maxPrice", definition.MaxPrice);
            WriteInt(pointer, AssetClass, "priceRoundingStep", definition.PriceRoundingStep);
            ReplaceStringList(pointer, AssetClass, "propertyAddresses", definition.AddressKeys);
            ReplaceIntList(pointer, AssetClass, "propertySizes", definition.Sizes);
            ReplaceReferenceList(pointer, AssetClass, "propertyVisuals", definition.Visuals, _spriteClass);
            ReplaceReferenceList(
                pointer,
                AssetClass,
                "loadingBackgrounds",
                definition.LoadingBackgrounds,
                _spriteClass);
            WriteString(pointer, AssetClass, "linkedSceneName", definition.LinkedSceneName);
            ReplaceReferenceList(
                pointer,
                AssetClass,
                "itemSpawnProfiles",
                definition.ItemSpawnProfiles,
                _spawnProfileClass);
            ReplaceReferenceList(pointer, AssetClass, "contracts", definition.Contracts, _contractClass);
        }

        protected override void RefreshCaches()
        {
            RefreshCacheIfReady(_propertyManagerClass, "BuildConfigCache");
            RefreshCacheIfReady(_contractManagerClass, "BuildConfigCaches");
        }

        private void ApplyPatch(nint pointer, PropertyPatch patch)
        {
            if (patch.DisplayNameKey is not null)
                WriteString(pointer, AssetClass, "displayName", patch.DisplayNameKey);
            if (patch.PropertyNameKeys is not null)
                ReplaceStringList(pointer, AssetClass, "propertyNames", patch.PropertyNameKeys);
            if (patch.Kind is not null)
                WriteInt(pointer, AssetClass, "propertyType", (int)patch.Kind.Value);
            if (patch.Level is not null)
                WriteInt(pointer, AssetClass, "propertyLevel", patch.Level.Value);
            if (patch.MinPrice is not null)
                WriteInt(pointer, AssetClass, "minPrice", patch.MinPrice.Value);
            if (patch.MaxPrice is not null)
                WriteInt(pointer, AssetClass, "maxPrice", patch.MaxPrice.Value);
            if (patch.PriceRoundingStep is not null)
                WriteInt(pointer, AssetClass, "priceRoundingStep", patch.PriceRoundingStep.Value);
            if (patch.AddressKeys is not null)
                ReplaceStringList(pointer, AssetClass, "propertyAddresses", patch.AddressKeys);
            if (patch.Sizes is not null)
                ReplaceIntList(pointer, AssetClass, "propertySizes", patch.Sizes);
            if (patch.Visuals is not null)
                ReplaceReferenceList(pointer, AssetClass, "propertyVisuals", patch.Visuals, _spriteClass);
            if (patch.LoadingBackgrounds is not null)
                ReplaceReferenceList(
                    pointer,
                    AssetClass,
                    "loadingBackgrounds",
                    patch.LoadingBackgrounds,
                    _spriteClass);
            if (patch.LinkedSceneName is not null)
                WriteString(pointer, AssetClass, "linkedSceneName", patch.LinkedSceneName);
            if (patch.ItemSpawnProfiles is not null)
                ReplaceReferenceList(
                    pointer,
                    AssetClass,
                    "itemSpawnProfiles",
                    patch.ItemSpawnProfiles,
                    _spawnProfileClass);
            if (patch.Contracts is not null)
                ReplaceReferenceList(pointer, AssetClass, "contracts", patch.Contracts, _contractClass);
        }

        private void ValidateDefinition(PropertyDefinition definition)
        {
            ValidateIdentity(definition.ConfigId);
            ValidateRequiredText(definition.DisplayNameKey, nameof(definition.DisplayNameKey));
            ValidateTextList(definition.PropertyNameKeys, nameof(definition.PropertyNameKeys));
            if (!Enum.IsDefined(definition.Kind))
                throw new ArgumentOutOfRangeException(nameof(definition.Kind));
            RequireNonNegative(definition.Level, nameof(definition.Level));
            RequireNonNegative(definition.MinPrice, nameof(definition.MinPrice));
            RequireNonNegative(definition.MaxPrice, nameof(definition.MaxPrice));
            if (definition.MinPrice > definition.MaxPrice)
                throw new ArgumentException("Property MinPrice cannot exceed MaxPrice.");
            if (definition.PriceRoundingStep <= 0)
                throw new ArgumentOutOfRangeException(nameof(definition.PriceRoundingStep));
            ValidateTextList(definition.AddressKeys, nameof(definition.AddressKeys));
            if (definition.Sizes.Count is < 1 or > 1000 || definition.Sizes.Any(value => value <= 0))
                throw new ArgumentException("Property sizes must contain 1..1000 positive values.");
            ValidateAssetList(definition.Visuals, _spriteClass, "visual", requireNonEmpty: true);
            ValidateAssetList(
                definition.LoadingBackgrounds,
                _spriteClass,
                "loading background",
                requireNonEmpty: false);
            ValidateRequiredText(definition.LinkedSceneName, nameof(definition.LinkedSceneName));
            ValidateAssetList(
                definition.ItemSpawnProfiles,
                _spawnProfileClass,
                "item spawn profile",
                requireNonEmpty: true);
            ValidateAssetList(definition.Contracts, _contractClass, "contract", requireNonEmpty: false);
        }

        private static void ValidateRequiredText(string value, string parameter)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
            if (value.Length > 500)
                throw new ArgumentException("Text must contain at most 500 characters.", parameter);
        }

        private static void ValidateTextList(IReadOnlyList<string> values, string parameter)
        {
            if (values.Count is < 1 or > 1000)
                throw new ArgumentException("Property text lists must contain 1..1000 entries.", parameter);
            foreach (var value in values) ValidateRequiredText(value, parameter);
        }

        private void ValidateAssetList(
            IReadOnlyList<UnityObject> values,
            nint requiredClass,
            string label,
            bool requireNonEmpty)
        {
            if (values.Count > 1000 || (requireNonEmpty && values.Count == 0))
                throw new ArgumentException($"Property {label} list has an invalid count.");
            foreach (var value in values)
            {
                if (value.IsNull || !Api.IsAssignableFrom(requiredClass, Api.GetObjectClass(value.Pointer)))
                    throw new ArgumentException($"Property {label} list contains an invalid asset.");
            }
        }

        private void RefreshCacheIfReady(nint managerClass, string methodName)
        {
            var manager = Api.RuntimeInvoke(
                RequireMethod(Api, managerClass, "get_Instance", 0),
                0,
                0);
            if (manager == 0) return;
            _ = Api.RuntimeInvoke(RequireMethod(Api, managerClass, methodName, 0), manager, 0);
        }

        private void OnFrameUpdate(FrameEvent _)
        {
            if (_registrations.Count == 0 || _transactionRegistrationStart >= 0 ||
                ++_frame % 30 != 0) return;
            try
            {
                foreach (var registration in _registrations.Where(value => value.Index >= 0))
                    EnsureCaches(registration);
                _lastCacheError = null;
            }
            catch (Exception exception)
            {
                if (!string.Equals(_lastCacheError, exception.Message, StringComparison.Ordinal))
                {
                    _lastCacheError = exception.Message;
                    _logger.Error(exception, "PropertyConfigSO cache verification failed.");
                }
            }
        }

        private void EnsureCaches(PropertyRegistration registration)
        {
            EnsureCacheEntry(
                _propertyManagerClass,
                "GetConfig",
                "_configCache",
                registration,
                propertyManager: true);
            EnsureCacheEntry(
                _contractManagerClass,
                "GetPropertyConfig",
                "_propertyConfigCache",
                registration,
                propertyManager: false);
        }

        private void EnsureCacheEntry(
            nint managerClass,
            string getterName,
            string cacheFieldName,
            PropertyRegistration registration,
            bool propertyManager)
        {
            var manager = Api.RuntimeInvoke(
                RequireMethod(Api, managerClass, "get_Instance", 0),
                0,
                0);
            if (manager == 0) return;
            var key = Api.NewString(registration.ConfigId);
            var lookup = Api.Invoke(
                RequireMethod(Api, managerClass, getterName, 1),
                manager,
                Il2CppArgument.FromReference(key));
            if (lookup == registration.Asset.Pointer) return;
            if (lookup != 0)
                throw new InvalidOperationException(
                    $"Property cache id '{registration.ConfigId}' belongs to another asset.");

            var cache = Api.ReadObjectReference(
                manager,
                RequireField(Api, managerClass, cacheFieldName));
            if (cache == 0)
                throw new InvalidOperationException(
                    $"{getterName} cache is null after the vanilla rebuild.");
            _ = Api.Invoke(
                RequireMethod(Api, Api.GetObjectClass(cache), "set_Item", 2),
                cache,
                Il2CppArgument.FromReference(key),
                Il2CppArgument.FromReference(registration.Asset.Pointer));
            lookup = Api.Invoke(
                RequireMethod(Api, managerClass, getterName, 1),
                manager,
                Il2CppArgument.FromReference(key));
            if (lookup != registration.Asset.Pointer)
                throw new InvalidOperationException(
                    $"Property cache rejected '{registration.ConfigId}' after direct repair.");
            if (propertyManager ? !registration.PropertyCacheRepairLogged :
                !registration.ContractCacheRepairLogged)
            {
                if (propertyManager) registration.PropertyCacheRepairLogged = true;
                else registration.ContractCacheRepairLogged = true;
                RuntimeLog.Write(
                    $"Property cache repaired after vanilla rebuild skipped custom asset: " +
                    $"owner={OwnerId}, id={registration.ConfigId}, consumer={getterName}.");
            }
        }

        private sealed class PropertyRegistration(RegistrationRecord record)
            : IPropertyRegistration
        {
            public string ConfigId => record.Identity;
            public UnityObject Asset => record.Asset;
            public int Index => record.Index;
            public bool PropertyCacheRepairLogged { get; set; }
            public bool ContractCacheRepairLogged { get; set; }
        }
    }
}
