using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static partial class UnityUiRuntime
{
    private static nint _mainMenuManagerClass;
    private static nint _gameObjectClass;
    private static nint _objectClass;
    private static nint _componentClass;
    private static nint _transformClass;
    private static nint _rectTransformClass;
    private static nint _buttonClass;
    private static nint _imageClass;
    private static nint _textClass;
    private static nint _texture2DClass;
    private static nint _spriteClass;
    private static nint _imageConversionClass;
    private static nint _byteClass;
    private static nint _eventSystemClass;
    private static nint _resourcesClass;
    private static nint _managerInstance;
    private static nint _menuButtons;
    private static nint _modsPanel;
    private static nint _modsButtonObject;
    private static nint _modsButton;
    private static nint _closeButton;
    private static nint _inputInfoUi;
    private static nint _headerLabel;
    private static nint _buttonsContainer;
    private static nint _mainButtonTemplate;
    private static nint _panelTemplate;
    private static bool _modsOpen;
    private static ModsView _currentView;
    private static bool _diagnosticsOpen;
    private static int _diagnosticIndex;
    private static bool _browseSearchFocused;
    private static int _sceneGeneration;
    private static ModMenuPanel? _activeExternalPanel;
    private static ModMenuInput? _focusedExternalInput;
    private static bool _dialogueOpen;
    private static bool _gameplayPanelOpen;
    private static int _mainMenuLabelRefreshCountdown;
    private static readonly Dictionary<nint, FakeMod> FakeMods = new();
    private static readonly Dictionary<nint, ModsView> ViewTabs = new();
    private static readonly Dictionary<ModsView, nint> ViewTabObjects = new();
    private static readonly Dictionary<nint, Action> RowActions = new();
    private static readonly Dictionary<nint, nint> SecondaryRowLabels = new();
    private static readonly List<nint> InstalledView = new();
    private static readonly List<nint> InstalledDetailView = new();
    private static readonly List<nint> InstalledCardRows = new();
    private static readonly List<nint> DiagnosticsView = new();
    private static readonly List<nint> BrowseView = new();
    private static readonly List<nint> BrowseDetailView = new();
    private static readonly List<nint> BrowseCardRows = new();
    private static readonly List<nint> SettingsView = new();
    private static readonly HashSet<string> BrowseInstalledIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> BrowseInstallStates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<nint, ThumbnailTarget> BrowseThumbnailTargets = new();
    private static readonly Dictionary<string, nint> CatalogThumbnailSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CatalogThumbnailRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CatalogThumbnailFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient CatalogThumbnailHttp = CreateCatalogThumbnailHttpClient();
    private static CatalogThumbnailStore? _thumbnailStore;
    private static CachedCatalogView? _cachedCatalog;
    private static ModCatalogBrowser? _catalogBrowser;
    private static int _officialCatalogRefreshBusy;
    private static nint _browseSearchRow;
    private static nint _browseSummaryRow;
    private static nint _browsePreviousButton;
    private static nint _browseNextButton;
    private static nint _browseDetailTitle;
    private static nint _browseDetailInfo;
    private static nint _browseDetailInstall;
    private static nint _browseDetailBack;
    private static nint _frameworkDiagnosticsRow;
    private static nint _installedPreviousButton;
    private static nint _installedNextButton;
    private static int _installedPageIndex;
    private static string? _selectedInstalledModId;
    private static bool _confirmInstalledUninstall;
    private static nint _installedDetailTitle;
    private static nint _installedDetailInfo;
    private static nint _installedDetailToggle;
    private static nint _installedDetailUninstall;
    private static nint _installedDetailBack;
    private static nint _diagnosticSummary;
    private static nint _diagnosticTitle;
    private static nint _diagnosticInfo;
    private static nint _diagnosticPrevious;
    private static nint _diagnosticBack;
    private static nint _diagnosticNext;
    private static nint _joinFixActionRow;
    private static nint _joinFixDetailsRow;
    private static nint _joinFixCatalogRow;
    private static readonly Dictionary<nint, string> StaticViewLabels = new();
    private static readonly Dictionary<nint, Action> ExternalButtonActions = new();
    private static readonly Dictionary<string, ModMenuButton> ExternalButtonsById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ModMenuPanel> ExternalPanelsById =
        new(StringComparer.OrdinalIgnoreCase);
    private const float ContentCenterX = -420f;
    private const float ContentWidth = 940f;

    public static nint ButtonPressNative { get; private set; }
    public static nint EventSystemUpdateNative { get; private set; }
    internal static bool IsFrameworkUiCapturingInput =>
        _modsOpen || _activeExternalPanel?.Visible == true || _dialogueOpen ||
        _gameplayPanelOpen;
    internal static bool IsDialogueOpen => _dialogueOpen;

    public static void Configure(
        IReadOnlyDictionary<string, nint> images,
        nint mainMenuManagerClass)
    {
        _mainMenuManagerClass = mainMenuManagerClass;
        _gameObjectClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
        _objectClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "Object");
        _componentClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "Component");
        _transformClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
        _rectTransformClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");
        _buttonClass = ResolveClass(images, "UnityEngine.UI.dll", "UnityEngine.UI", "Button");
        _imageClass = ResolveClass(images, "UnityEngine.UI.dll", "UnityEngine.UI", "Image");
        _textClass = ResolveClass(images, "Unity.TextMeshPro.dll", "TMPro", "TextMeshProUGUI");
        _texture2DClass = ResolveClass(
            images, "UnityEngine.CoreModule.dll", "UnityEngine", "Texture2D");
        _spriteClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "Sprite");
        _imageConversionClass = ResolveClass(
            images, "UnityEngine.ImageConversionModule.dll", "UnityEngine", "ImageConversion");
        _byteClass = ResolveClass(images, "mscorlib.dll", "System", "Byte");
        _eventSystemClass = ResolveClass(images, "UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem");
        _resourcesClass = ResolveClass(images, "UnityEngine.CoreModule.dll", "UnityEngine", "Resources");

        RequireMethod(_gameObjectClass, "get_transform", 0);
        RequireMethod(_objectClass, "get_name", 0);
        RequireMethod(_componentClass, "get_gameObject", 0);
        RequireMethod(_transformClass, "get_childCount", 0);
        RequireMethod(_transformClass, "GetChild", 1);
        var pressMethod = RequireMethod(_buttonClass, "Press", 0);
        ButtonPressNative = Marshal.ReadIntPtr(pressMethod);
        if (ButtonPressNative == 0)
        {
            throw new InvalidOperationException("Button.Press has no native IL2CPP method pointer.");
        }
        var eventSystemUpdate = RequireMethod(_eventSystemClass, "Update", 0);
        EventSystemUpdateNative = Marshal.ReadIntPtr(eventSystemUpdate);
        if (EventSystemUpdateNative == 0)
        {
            throw new InvalidOperationException("EventSystem.Update has no native IL2CPP method pointer.");
        }
    }

    public static void BuildModsMenu(nint manager)
    {
        if (_managerInstance == manager && _modsPanel != 0)
        {
            RuntimeLog.Write("Mods UI already exists for this MainMenuManager instance.");
            return;
        }

        ResetMainMenuSceneState();
        _managerInstance = manager;
        _menuButtons = ReadReferenceField(manager, _mainMenuManagerClass, "menuButtons");
        _inputInfoUi = ReadReferenceField(manager, _mainMenuManagerClass, "inputInfoUI");
        var settingsPanel = ReadReferenceField(manager, _mainMenuManagerClass, "settingsPanel");
        _panelTemplate = settingsPanel;
        _buttonsContainer = FindDescendant(_menuButtons, "Buttons");
        _mainButtonTemplate = FindDirectChild(_buttonsContainer, "ButtonTemplate (Settings)");

        var buttonsTransform = GetTransform(_buttonsContainer);
        var modsButtonObject = Instantiate(_mainButtonTemplate, buttonsTransform);
        SetObjectName(modsButtonObject, "OFS Button (Mods)");
        SetLabel(modsButtonObject, "MODS");
        _modsButtonObject = modsButtonObject;
        _modsButton = GetComponent(modsButtonObject, _buttonClass);
        foreach (var trailingName in new[]
                 {
                     "ButtonTemplate (Credits)",
                     "ButtonTemplate (Exit)",
                     "Version_Text"
                 })
        {
            var trailingObject = FindDirectChild(_buttonsContainer, trailingName);
            _ = InvokeReference(
                RequireMethod(_transformClass, "SetAsLastSibling", 0),
                GetTransform(trailingObject));
        }

        var settingsTransformRoot = GetTransform(settingsPanel);
        var panelParent = InvokeReference(
            RequireMethod(_transformClass, "get_parent", 0),
            settingsTransformRoot);
        _modsPanel = Instantiate(settingsPanel, panelParent);
        SetObjectName(_modsPanel, "OFS Mods Panel");
        SetActive(_modsPanel, false);

        HideDirectChildrenExcept(_modsPanel, "Header Bar");
        var header = FindDirectChild(_modsPanel, "Header Bar");
        var tabs = FindDirectChildOrNull(header, "Tabs");
        if (tabs == 0)
        {
            throw new InvalidOperationException("The Settings header does not expose its native tabs.");
        }
        ConfigureNativeViewTabs(tabs);
        _headerLabel = FindDescendant(header, "Header_Text");
        SetLabel(_headerLabel, "Mods");
        var closeObject = FindDescendant(header, "Close");
        _closeButton = GetComponent(closeObject, _buttonClass);

        var installedMods = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
        BuildInstalledView();
        BuildDiagnosticsView();

        BuildBrowseView(installedMods);

        _joinFixActionRow = CreateActionRow(
            _mainButtonTemplate,
            SettingsView,
            "JOIN FIX    [NO MISMATCH]",
            340f,
            ApplyLastJoinFix);
        _joinFixDetailsRow = CreateActionRow(
            _mainButtonTemplate,
            SettingsView,
            "NO MULTIPLAYER REMEDIATION IS PENDING",
            265f,
            LogLastJoinFix);
        _joinFixCatalogRow = CreateActionRow(
            _mainButtonTemplate,
            SettingsView,
            "CATALOG STATUS",
            190f,
            LogLastJoinFix);
        RefreshJoinFixView();

        ShowView(ModsView.Installed);

        SetActive(modsButtonObject, true);
        RuntimeLog.Write(
            $"Mods UI created: entry=0x{_modsButton:X}, panel=0x{_modsPanel:X}, " +
            $"loadedMods={ModRuntime.LoadedMods.Count}.");
    }

    private static void ResetMainMenuSceneState()
    {
        _sceneGeneration++;
        _menuButtons = 0;
        _modsPanel = 0;
        _modsButtonObject = 0;
        _modsButton = 0;
        _closeButton = 0;
        _inputInfoUi = 0;
        _headerLabel = 0;
        _buttonsContainer = 0;
        _mainButtonTemplate = 0;
        _panelTemplate = 0;
        _modsOpen = false;
        _currentView = ModsView.Installed;
        _browseSearchFocused = false;
        _activeExternalPanel = null;
        _focusedExternalInput = null;
        _mainMenuLabelRefreshCountdown = 0;

        FakeMods.Clear();
        ViewTabs.Clear();
        ViewTabObjects.Clear();
        RowActions.Clear();
        SecondaryRowLabels.Clear();
        InstalledView.Clear();
        InstalledDetailView.Clear();
        InstalledCardRows.Clear();
        DiagnosticsView.Clear();
        BrowseView.Clear();
        BrowseDetailView.Clear();
        BrowseCardRows.Clear();
        SettingsView.Clear();
        StaticViewLabels.Clear();
        BrowseInstalledIds.Clear();
        BrowseInstallStates.Clear();
        BrowseThumbnailTargets.Clear();
        CatalogThumbnailSprites.Clear();
        CatalogThumbnailRequests.Clear();
        CatalogThumbnailFailures.Clear();
        _thumbnailStore = null;
        _cachedCatalog = null;
        _catalogBrowser = null;
        _browseSearchRow = 0;
        _browseSummaryRow = 0;
        _browsePreviousButton = 0;
        _browseNextButton = 0;
        _browseDetailTitle = 0;
        _browseDetailInfo = 0;
        _browseDetailInstall = 0;
        _browseDetailBack = 0;
        _frameworkDiagnosticsRow = 0;
        _installedPreviousButton = 0;
        _installedNextButton = 0;
        _installedPageIndex = 0;
        _selectedInstalledModId = null;
        _confirmInstalledUninstall = false;
        _installedDetailTitle = 0;
        _installedDetailInfo = 0;
        _installedDetailToggle = 0;
        _installedDetailUninstall = 0;
        _installedDetailBack = 0;
        _diagnosticSummary = 0;
        _diagnosticTitle = 0;
        _diagnosticInfo = 0;
        _diagnosticPrevious = 0;
        _diagnosticBack = 0;
        _diagnosticNext = 0;
        _diagnosticsOpen = false;
        _diagnosticIndex = 0;
        _joinFixActionRow = 0;
        _joinFixDetailsRow = 0;
        _joinFixCatalogRow = 0;
        ExternalButtonActions.Clear();
        ExternalButtonsById.Clear();
        ExternalPanelsById.Clear();
    }

    public static void NotifyMainMenuUnloaded()
    {
        if (_managerInstance != 0)
        {
            ResetMainMenuSceneState();
            _managerInstance = 0;
            RuntimeLog.Write("Released Mods UI references for the unloaded Main Menu scene.");
        }
    }

    public static bool HandleButtonPress(nint button)
    {
        if (button == _modsButton)
        {
            _activeExternalPanel?.Close();
            DrainModalCloseInput();
            SetActive(_menuButtons, false);
            SetActive(_modsPanel, true);
            SetActive(_inputInfoUi, false);
            _modsOpen = true;
            DrainViewTabKeys();
            ShowView(ModsView.Installed);
            RuntimeLog.Write("Mods menu opened.");
            return true;
        }

        if (button == _closeButton)
        {
            CloseModsMenu();
            return true;
        }

        if (ViewTabs.TryGetValue(button, out var view))
        {
            ShowView(view);
            RuntimeLog.Write($"Mods view changed to {view}.");
            return true;
        }

        if (FakeMods.TryGetValue(button, out var mod))
        {
            mod.Enabled = !mod.Enabled;
            SetLabel(mod.GameObject, FormatModLabel(mod));
            RuntimeLog.Write($"Fake mod '{mod.Name}' toggled to {(mod.Enabled ? "enabled" : "disabled")}.");
            return true;
        }

        if (RowActions.TryGetValue(button, out var action))
        {
            action();
            return true;
        }

        if (ExternalButtonActions.TryGetValue(button, out var externalAction))
        {
            try
            {
                externalAction();
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"External mod button failed: {exception}");
            }
            return true;
        }

        return false;
    }

    public static IMenuButton AddExternalMainMenuButton(
        string ownerId,
        MainMenuButtonDefinition definition)
    {
        if (_buttonsContainer == 0 || _mainButtonTemplate == 0)
        {
            throw new InvalidOperationException("The main menu UI is not ready.");
        }
        ValidateExternalId(definition.Id, "button");
        var key = OwnerKey(ownerId, definition.Id);
        if (ExternalButtonsById.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var gameObject = Instantiate(_mainButtonTemplate, GetTransform(_buttonsContainer));
        SetObjectName(gameObject, $"OFS Mod Button ({definition.Id})");
        SetLabel(gameObject, definition.Label);
        var button = GetComponent(gameObject, _buttonClass);
        ExternalButtonActions[button] = GuardRuntimeCallback(
            ownerId,
            $"main-menu-button:{definition.Id}",
            definition.OnPressed);
        MoveMainMenuTrailingObjectsToEnd();
        SetActive(gameObject, true);
        var handle = new ModMenuButton(
            definition.Id,
            gameObject,
            button,
            definition.Label,
            _sceneGeneration,
            () =>
            {
                ExternalButtonActions.Remove(button);
                ExternalButtonsById.Remove(key);
            });
        ExternalButtonsById.Add(key, handle);
        RuntimeLog.Write($"External main-menu button registered: '{ownerId}:{definition.Id}'.");
        return handle;
    }

    public static IMenuPanel AddExternalMainMenuPanel(
        string ownerId,
        MainMenuPanelDefinition definition)
    {
        if (_panelTemplate == 0 || _mainButtonTemplate == 0)
        {
            throw new InvalidOperationException("The main menu UI is not ready.");
        }
        ValidateExternalId(definition.Id, "panel");
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        var key = OwnerKey(ownerId, definition.Id);
        if (ExternalPanelsById.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var templateTransform = GetTransform(_panelTemplate);
        var parent = InvokeReference(
            RequireMethod(_transformClass, "get_parent", 0),
            templateTransform);
        var panelObject = Instantiate(_panelTemplate, parent);
        SetObjectName(panelObject, $"OFS Mod Panel ({ownerId}:{definition.Id})");
        HideDirectChildrenExcept(panelObject, "Header Bar");
        var header = FindDirectChild(panelObject, "Header Bar");
        var tabs = FindDirectChildOrNull(header, "Tabs");
        if (tabs != 0) SetActive(tabs, false);
        var titleObject = FindDescendant(header, "Header_Text");
        SetLabel(titleObject, definition.Title);
        var closeObject = FindDescendant(header, "Close");
        var closeButton = GetComponent(closeObject, _buttonClass);

        ModMenuPanel? handle = null;
        handle = new ModMenuPanel(
            ownerId,
            definition.Id,
            panelObject,
            titleObject,
            closeButton,
            definition.Title,
            _sceneGeneration,
            () =>
            {
                ExternalButtonActions.Remove(closeButton);
                ExternalPanelsById.Remove(key);
                if (ReferenceEquals(_activeExternalPanel, handle)) _activeExternalPanel = null;
            });
        ExternalButtonActions[closeButton] = handle.Close;
        ExternalPanelsById.Add(key, handle);
        SetActive(panelObject, false);
        RuntimeLog.Write($"External main-menu panel registered: '{ownerId}:{definition.Id}'.");
        return handle;
    }

    private static string OwnerKey(string ownerId, string localId) => $"{ownerId}\0{localId}";

    private static Action GuardRuntimeCallback(
        string ownerId,
        string phase,
        Action callback) => () =>
        {
            using var lease = ModSafetyStore.EnterRuntimeCallback(ownerId, phase);
            callback();
        };

    private static Action<T>? GuardRuntimeCallback<T>(
        string ownerId,
        string phase,
        Action<T>? callback) => callback is null
            ? null
            : value =>
            {
                using var lease = ModSafetyStore.EnterRuntimeCallback(ownerId, phase);
                callback(value);
            };

    private static Action<T1, T2>? GuardRuntimeCallback<T1, T2>(
        string ownerId,
        string phase,
        Action<T1, T2>? callback) => callback is null
            ? null
            : (first, second) =>
            {
                using var lease = ModSafetyStore.EnterRuntimeCallback(ownerId, phase);
                callback(first, second);
            };

    private static void ValidateExternalId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 ||
            id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                $"A {kind} id of at most 100 ASCII letters, digits, dots, dashes or underscores is required.");
        }
    }

    internal static nint FindActiveGameObjectPointer(string name) =>
        InvokeReferenceWithObject(
            RequireMethod(_gameObjectClass, "Find", 1),
            0,
            Native.string_new(name));

    internal static nint FindLoadedGameObjectPointer(string name, IUnsafeIl2CppApi api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var all = InvokeReferenceWithObject(
            RequireMethod(_resourcesClass, "FindObjectsOfTypeAll", 1),
            0,
            Native.type_get_object(Native.class_get_type(_gameObjectClass)));
        if (all == 0) return 0;
        var length = api.GetArrayLength(all);
        for (nuint index = 0; index < length; index++)
        {
            var candidate = api.ReadArrayElementReference(all, index);
            if (candidate != 0 && string.Equals(GetObjectName(candidate), name, StringComparison.Ordinal))
                return candidate;
        }
        return 0;
    }

    internal static nint FindLoadedComponentPointer(nint componentClass, IUnsafeIl2CppApi api)
    {
        if (componentClass == 0) return 0;
        var all = InvokeReferenceWithObject(
            RequireMethod(_resourcesClass, "FindObjectsOfTypeAll", 1),
            0,
            Native.type_get_object(Native.class_get_type(componentClass)));
        if (all == 0) return 0;
        var length = api.GetArrayLength(all);
        for (nuint index = 0; index < length; index++)
        {
            var candidate = api.ReadArrayElementReference(all, index);
            if (candidate != 0) return candidate;
        }
        return 0;
    }

    internal static IReadOnlyList<nint> FindLoadedComponentPointers(
        nint componentClass,
        IUnsafeIl2CppApi api,
        bool activeOnly)
    {
        if (componentClass == 0) return [];
        var all = InvokeReferenceWithObject(
            RequireMethod(_resourcesClass, "FindObjectsOfTypeAll", 1),
            0,
            Native.type_get_object(Native.class_get_type(componentClass)));
        if (all == 0) return [];
        var result = new List<nint>();
        var seen = new HashSet<nint>();
        var length = api.GetArrayLength(all);
        for (nuint index = 0; index < length; index++)
        {
            var component = api.ReadArrayElementReference(all, index);
            if (component == 0 || !seen.Add(component)) continue;
            if (activeOnly)
            {
                var gameObject = InvokeReference(
                    RequireMethod(_componentClass, "get_gameObject", 0), component);
                if (gameObject == 0 || !InvokeBoolean(
                    RequireMethod(_gameObjectClass, "get_activeInHierarchy", 0), gameObject))
                    continue;
            }
            result.Add(component);
        }
        return result;
    }

    internal static nint FindActiveLoadedComponentPointer(nint componentClass, IUnsafeIl2CppApi api)
    {
        if (componentClass == 0) return 0;
        var all = InvokeReferenceWithObject(
            RequireMethod(_resourcesClass, "FindObjectsOfTypeAll", 1),
            0,
            Native.type_get_object(Native.class_get_type(componentClass)));
        if (all == 0) return 0;
        var length = api.GetArrayLength(all);
        for (nuint index = 0; index < length; index++)
        {
            var component = api.ReadArrayElementReference(all, index);
            if (component == 0) continue;
            var gameObject = InvokeReference(
                RequireMethod(_componentClass, "get_gameObject", 0), component);
            if (gameObject != 0 && InvokeBoolean(
                RequireMethod(_gameObjectClass, "get_activeInHierarchy", 0), gameObject))
                return component;
        }
        return 0;
    }

    internal static nint FindChildPointer(nint parent, string name, bool recursive) =>
        recursive ? FindDescendantOrNull(parent, name) : FindDirectChildOrNull(parent, name);

    internal static nint CloneGameObjectPointer(nint original, nint parent) =>
        Instantiate(original, parent == 0 ? 0 : GetTransform(parent));

    internal static nint CloneSiblingPointer(nint original)
    {
        var transform = GetTransform(original);
        var parent = InvokeReference(RequireMethod(_transformClass, "get_parent", 0), transform);
        return Instantiate(original, parent);
    }

    internal static nint FindDescendantPointer(nint parent, string name) =>
        FindDescendant(parent, name);

    internal static void SetRectForSdk(
        nint gameObject,
        float x,
        float y,
        float width,
        float height) =>
        SetRect(gameObject, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(width, height));

    internal static void RegisterFrameworkButton(nint button, Action action) =>
        ExternalButtonActions[button] = action;

    internal static void UnregisterFrameworkButton(nint button) =>
        ExternalButtonActions.Remove(button);

    internal static void SetDialogueOpen(bool open) => _dialogueOpen = open;

    internal static void SetGameplayPanelOpen(bool open) => _gameplayPanelOpen = open;

    internal static void PrepareGameplayModalInput() => DrainModalCloseInput();

    internal static unsafe nint InstantiateGameObjectPointer(
        nint prefab,
        UnityVector3 position,
        UnityQuaternion rotation,
        nint parent)
    {
        var nativePosition = new Vector3(position.X, position.Y, position.Z);
        var nativeRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        nint* arguments = stackalloc nint[4];
        arguments[0] = prefab;
        arguments[1] = (nint)(&nativePosition);
        arguments[2] = (nint)(&nativeRotation);
        arguments[3] = parent == 0 ? 0 : GetTransform(parent);
        var clone = Native.runtime_invoke(
            RequireMethod(_objectClass, "Instantiate", 4),
            0,
            (nint)arguments,
            out var exception);
        ThrowIfException(exception);
        return clone;
    }

    internal static nint GetComponentPointer(nint gameObject, nint componentClass) =>
        GetComponent(gameObject, componentClass);

    internal static nint TryGetComponentPointer(nint gameObject, nint componentClass) =>
        TryGetComponent(gameObject, componentClass);

    internal static nint GetButtonPointer(nint gameObject) => GetComponent(gameObject, _buttonClass);

    internal static nint AddComponentPointer(nint gameObject, nint componentClass)
    {
        var type = Native.class_get_type(componentClass);
        var typeObject = Native.type_get_object(type);
        return InvokeReferenceWithObject(
            RequireMethod(_gameObjectClass, "AddComponent", 1),
            gameObject,
            typeObject);
    }

    internal static string GetObjectNameForSdk(nint instance) => GetObjectName(instance);

    internal static void SetObjectNameForSdk(nint instance, string name) =>
        SetObjectName(instance, name);

    internal static void SetActiveForSdk(nint gameObject, bool active) =>
        SetActive(gameObject, active);

    internal static bool IsActiveInHierarchyForSdk(nint gameObject) =>
        gameObject != 0 && InvokeBoolean(
            RequireMethod(_gameObjectClass, "get_activeInHierarchy", 0),
            gameObject);

    internal static bool IsActiveSelfForSdk(nint gameObject) =>
        gameObject != 0 && InvokeBoolean(
            RequireMethod(_gameObjectClass, "get_activeSelf", 0),
            gameObject);

    internal static void SetTextForSdk(nint gameObject, string text)
    {
        var component = GetComponent(gameObject, _textClass);
        InvokeVoidWithObject(
            RequireMethod(_textClass, "set_text", 1),
            component,
            Native.string_new(text));
    }

    internal static UnityTransform GetTransformForSdk(nint gameObject)
    {
        var transform = GetTransform(gameObject);
        var position = ReadVector3(RequireMethod(_transformClass, "get_position", 0), transform);
        var rotation = ReadQuaternion(RequireMethod(_transformClass, "get_rotation", 0), transform);
        var scale = ReadVector3(RequireMethod(_transformClass, "get_localScale", 0), transform);
        return new UnityTransform(
            new UnityVector3(position.X, position.Y, position.Z),
            new UnityQuaternion(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new UnityVector3(scale.X, scale.Y, scale.Z));
    }

    internal static void SetTransformForSdk(nint gameObject, UnityTransform value)
    {
        var transform = GetTransform(gameObject);
        InvokeVoidWithVector3(
            RequireMethod(_transformClass, "set_position", 1),
            transform,
            new Vector3(value.Position.X, value.Position.Y, value.Position.Z));
        InvokeVoidWithQuaternion(
            RequireMethod(_transformClass, "set_rotation", 1),
            transform,
            new Quaternion(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W));
        InvokeVoidWithVector3(
            RequireMethod(_transformClass, "set_localScale", 1),
            transform,
            new Vector3(value.Scale.X, value.Scale.Y, value.Scale.Z));
    }

    internal static void SetParentForSdk(nint gameObject, nint parent, bool worldPositionStays) =>
        InvokeVoidWithObjectBool(
            RequireMethod(_transformClass, "SetParent", 2),
            GetTransform(gameObject),
            parent == 0 ? 0 : GetTransform(parent),
            worldPositionStays);

    internal static void SetAsLastSiblingForSdk(nint gameObject) =>
        _ = InvokeReference(
            RequireMethod(_transformClass, "SetAsLastSibling", 0),
            GetTransform(gameObject));

    internal static void DontDestroyOnLoadForSdk(nint instance) =>
        InvokeVoidWithObject(
            RequireMethod(_objectClass, "DontDestroyOnLoad", 1),
            0,
            instance);

    internal static void DestroyForSdk(nint instance) =>
        InvokeVoidWithObject(
            RequireMethod(_objectClass, "Destroy", 1),
            0,
            instance);

    private static void MoveMainMenuTrailingObjectsToEnd()
    {
        foreach (var trailingName in new[]
                 {
                     "ButtonTemplate (Credits)",
                     "ButtonTemplate (Exit)",
                     "Version_Text"
                 })
        {
            var trailingObject = FindDirectChild(_buttonsContainer, trailingName);
            _ = InvokeReference(
                RequireMethod(_transformClass, "SetAsLastSibling", 0),
                GetTransform(trailingObject));
        }
    }

    public static void PollCloseInput()
    {
        RefreshMainMenuLabelsWhenDue();

        if ((!_modsOpen && _activeExternalPanel is null && !_dialogueOpen &&
             !_gameplayPanelOpen) ||
            !Native.IsCurrentProcessForeground())
        {
            return;
        }

        PollBrowseSearchInput();
        PollExternalPanelInput();
        PollViewTabInput();
        RefreshViewTabVisuals();

        const int Escape = 0x1B;
        const int RightMouseButton = 0x02;
        if ((Native.GetAsyncKeyState(Escape) & 1) != 0 ||
            (Native.GetAsyncKeyState(RightMouseButton) & 1) != 0)
        {
            if (_dialogueOpen) DialogueRuntime.CancelActive();
            else if (_gameplayPanelOpen) GameplayUiRuntime.CancelActive();
            else if (_modsOpen && _diagnosticsOpen) CloseRuntimeDiagnostics();
            else if (_modsOpen) CloseModsMenu();
            else _activeExternalPanel?.Close();
        }
    }

    private static void PollViewTabInput()
    {
        if (!_modsOpen || _browseSearchFocused || _dialogueOpen || _gameplayPanelOpen)
        {
            return;
        }
        const int Q = 0x51;
        const int E = 0x45;
        if ((Native.GetAsyncKeyState(Q) & 1) != 0)
        {
            CycleView(-1);
        }
        else if ((Native.GetAsyncKeyState(E) & 1) != 0)
        {
            CycleView(1);
        }
    }

    private static void CycleView(int offset)
    {
        var views = new[] { ModsView.Installed, ModsView.Browse, ModsView.Settings };
        var current = Array.IndexOf(views, _currentView);
        var next = (current + offset + views.Length) % views.Length;
        ShowView(views[next]);
        RuntimeLog.Write($"Mods view changed to {views[next]} by keyboard tab navigation.");
    }

    private static void DrainViewTabKeys()
    {
        _ = Native.GetAsyncKeyState(0x51); // Q
        _ = Native.GetAsyncKeyState(0x45); // E
    }

    private static void RefreshViewTabVisuals()
    {
        foreach (var tab in ViewTabObjects)
        {
            SetButtonVisualColor(
                tab.Value,
                tab.Key == _currentView ? AccentButtonColor : NeutralButtonColor);
        }
    }

    private static void RefreshMainMenuLabelsWhenDue()
    {
        if (_managerInstance == 0 || --_mainMenuLabelRefreshCountdown > 0)
        {
            return;
        }

        _mainMenuLabelRefreshCountdown = 15;
        if (_modsButtonObject != 0)
        {
            SetLabel(_modsButtonObject, "MODS");
        }
        foreach (var button in ExternalButtonsById.Values)
        {
            button.RefreshLabel();
        }
        foreach (var panel in ExternalPanelsById.Values)
        {
            panel.RefreshLabels();
        }
    }

    private static void CloseModsMenu()
    {
        SetActive(_modsPanel, false);
        SetActive(_menuButtons, true);
        SetActive(_inputInfoUi, true);
        _modsOpen = false;
        _browseSearchFocused = false;
        RuntimeLog.Write("Mods menu closed.");
    }

    private static void PollBrowseSearchInput()
    {
        if (!_modsOpen || _currentView != ModsView.Browse ||
            !_browseSearchFocused || _catalogBrowser is null)
        {
            return;
        }

        const int Backspace = 0x08;
        const int Enter = 0x0D;
        const int Delete = 0x2E;
        if ((Native.GetAsyncKeyState(Enter) & 1) != 0)
        {
            _browseSearchFocused = false;
            LogBrowseResultState("search-commit");
            RefreshBrowseView();
            return;
        }

        var query = _catalogBrowser.Query;
        if ((Native.GetAsyncKeyState(Backspace) & 1) != 0 && query.Length > 0)
        {
            _catalogBrowser.SetQuery(query[..^1]);
            LogBrowseResultState("search");
            RefreshBrowseView();
            return;
        }
        if ((Native.GetAsyncKeyState(Delete) & 1) != 0 && query.Length > 0)
        {
            _catalogBrowser.SetQuery(string.Empty);
            LogBrowseResultState("search-clear");
            RefreshBrowseView();
            return;
        }

        var character = ReadBrowseSearchCharacter();
        if (character is not null && query.Length < 40)
        {
            _catalogBrowser.SetQuery(query + character.Value);
            LogBrowseResultState("search");
            RefreshBrowseView();
        }
    }

    private static void LogBrowseResultState(string action)
    {
        if (_catalogBrowser is null) return;
        RuntimeLog.Write(
            $"Catalog browser {action}: query='{_catalogBrowser.Query.Trim()}', " +
            $"matches={_catalogBrowser.MatchCount}, page={_catalogBrowser.PageIndex + 1}/" +
            $"{_catalogBrowser.PageCount}.");
    }

    private static char? ReadBrowseSearchCharacter()
    {
        for (var key = 0x41; key <= 0x5A; ++key)
        {
            if ((Native.GetAsyncKeyState(key) & 1) != 0)
            {
                return char.ToLowerInvariant((char)key);
            }
        }
        for (var key = 0x30; key <= 0x39; ++key)
        {
            if ((Native.GetAsyncKeyState(key) & 1) != 0)
            {
                return (char)key;
            }
        }

        const int Space = 0x20;
        const int OemMinus = 0xBD;
        const int OemPeriod = 0xBE;
        if ((Native.GetAsyncKeyState(Space) & 1) != 0) return ' ';
        if ((Native.GetAsyncKeyState(OemMinus) & 1) != 0) return '-';
        if ((Native.GetAsyncKeyState(OemPeriod) & 1) != 0) return '.';
        return null;
    }

    private static void DrainBrowseSearchKeys()
    {
        foreach (var key in Enumerable.Range(0x30, 10)
                     .Concat(Enumerable.Range(0x41, 26))
                     .Concat([0x08, 0x0D, 0x20, 0x2E, 0xBD, 0xBE]))
        {
            _ = Native.GetAsyncKeyState(key);
        }
    }

    private static void PollExternalPanelInput()
    {
        var input = _focusedExternalInput;
        if (input is null)
        {
            return;
        }
        if (!input.IsAlive || !input.Visible || !input.Panel.Visible ||
            !ReferenceEquals(_activeExternalPanel, input.Panel))
        {
            SetExternalInputFocus(null);
            return;
        }

        const int Backspace = 0x08;
        const int Enter = 0x0D;
        const int Delete = 0x2E;
        if ((Native.GetAsyncKeyState(Enter) & 1) != 0)
        {
            input.Submit();
            input.Blur();
            return;
        }
        if ((Native.GetAsyncKeyState(Backspace) & 1) != 0 && input.Value.Length > 0)
        {
            input.SetValueFromKeyboard(input.Value[..^1]);
            return;
        }
        if ((Native.GetAsyncKeyState(Delete) & 1) != 0 && input.Value.Length > 0)
        {
            input.SetValueFromKeyboard(string.Empty);
            return;
        }

        var character = ReadExternalInputCharacter();
        if (character is not null && input.Value.Length < input.MaxLength)
        {
            input.SetValueFromKeyboard(input.Value + character.Value);
        }
    }

    private static char? ReadExternalInputCharacter()
    {
        const int Shift = 0x10;
        var shifted = (Native.GetAsyncKeyState(Shift) & 0x8000) != 0;
        for (var key = 0x41; key <= 0x5A; ++key)
        {
            if ((Native.GetAsyncKeyState(key) & 1) != 0)
            {
                var value = (char)key;
                return shifted ? value : char.ToLowerInvariant(value);
            }
        }

        const string plainDigits = "0123456789";
        const string shiftedDigits = ")!@#$%^&*(";
        for (var key = 0x30; key <= 0x39; ++key)
        {
            if ((Native.GetAsyncKeyState(key) & 1) != 0)
            {
                return shifted ? shiftedDigits[key - 0x30] : plainDigits[key - 0x30];
            }
        }

        var punctuation = new (int Key, char Plain, char Shifted)[]
        {
            (0x20, ' ', ' '), (0xBA, ';', ':'), (0xBB, '=', '+'),
            (0xBC, ',', '<'), (0xBD, '-', '_'), (0xBE, '.', '>'),
            (0xBF, '/', '?'), (0xC0, '`', '~'), (0xDB, '[', '{'),
            (0xDC, '\\', '|'), (0xDD, ']', '}'), (0xDE, '\'', '"'),
        };
        foreach (var candidate in punctuation)
        {
            if ((Native.GetAsyncKeyState(candidate.Key) & 1) != 0)
            {
                return shifted ? candidate.Shifted : candidate.Plain;
            }
        }
        return null;
    }

    private static void SetExternalInputFocus(ModMenuInput? input)
    {
        if (ReferenceEquals(_focusedExternalInput, input))
        {
            return;
        }
        _focusedExternalInput?.SetFocused(false);
        _focusedExternalInput = input;
        input?.SetFocused(true);
        if (input is not null)
        {
            DrainExternalInputKeys();
        }
    }

    private static void DrainExternalInputKeys()
    {
        foreach (var key in Enumerable.Range(0x30, 10)
                     .Concat(Enumerable.Range(0x41, 26))
                     .Concat([0x08, 0x0D, 0x20, 0x2E, 0xBA, 0xBB, 0xBC,
                         0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE]))
        {
            _ = Native.GetAsyncKeyState(key);
        }
    }

    private static void DrainModalCloseInput()
    {
        _ = Native.GetAsyncKeyState(0x1B); // Escape
        _ = Native.GetAsyncKeyState(0x02); // right mouse button
    }

    private static void InvokeExternalControlCallback(Action callback, string controlId)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"External menu control '{controlId}' callback failed: {exception}");
        }
    }

    private static void ShowExternalPanel(ModMenuPanel panel)
    {
        panel.EnsureAlive();
        if (_activeExternalPanel is not null && !ReferenceEquals(_activeExternalPanel, panel))
        {
            _activeExternalPanel.Close();
        }
        if (_modsOpen)
        {
            SetActive(_modsPanel, false);
            _modsOpen = false;
        }
        DrainModalCloseInput();
        SetActive(_menuButtons, false);
        SetActive(_inputInfoUi, false);
        SetActive(panel.GameObject, true);
        panel.RefreshTitle();
        panel.SetVisible(true);
        _activeExternalPanel = panel;
        RuntimeLog.Write($"External panel opened: '{panel.OwnerId}:{panel.Id}'.");
    }

    private static void CloseExternalPanel(ModMenuPanel panel)
    {
        if (!panel.IsAlive) return;
        if (ReferenceEquals(_focusedExternalInput?.Panel, panel))
        {
            SetExternalInputFocus(null);
        }
        SetActive(panel.GameObject, false);
        panel.SetVisible(false);
        if (ReferenceEquals(_activeExternalPanel, panel))
        {
            _activeExternalPanel = null;
            SetActive(_menuButtons, true);
            SetActive(_inputInfoUi, true);
        }
        RuntimeLog.Write($"External panel closed: '{panel.OwnerId}:{panel.Id}'.");
    }

    private static bool IsSceneHandleAlive(int generation) =>
        generation == _sceneGeneration && _managerInstance != 0;

    private static void ConfigureNativeViewTabs(nint tabs)
    {
        SetActive(tabs, true);
        var buttonCandidates = FindDescendantsWithComponent(tabs, _buttonClass)
            .Where(gameObject =>
                FindDescendantOrNull(gameObject, "ButtonName_Text") != 0 ||
                FindFirstDescendantWithComponent(gameObject, _textClass) != 0)
            .Distinct()
            .ToArray();
        var nativeLabels = new[] { "GRAPHICS", "AUDIO", "CONTROLS", "GAMEPLAY" };
        var nativeTabs = nativeLabels
            .Select(label => buttonCandidates.FirstOrDefault(gameObject => string.Equals(
                GetLabel(gameObject).Trim(), label, StringComparison.OrdinalIgnoreCase)))
            .Where(gameObject => gameObject != 0)
            .ToArray();
        var tabButtons = nativeTabs.Length >= 3
            ? nativeTabs
            : buttonCandidates;
        if (tabButtons.Length < 3)
        {
            throw new InvalidOperationException(
                $"The Settings header exposes only {tabButtons.Length} usable tab buttons.");
        }

        var views = new[] { ModsView.Installed, ModsView.Browse, ModsView.Settings };
        for (var index = 0; index < views.Length; ++index)
        {
            var gameObject = tabButtons[index];
            var view = views[index];
            SetObjectName(gameObject, $"OFS Native Tab ({view})");
            SetLabel(gameObject, view.ToString().ToUpperInvariant());
            ViewTabs[GetComponent(gameObject, _buttonClass)] = view;
            ViewTabObjects[view] = gameObject;
            SetActive(gameObject, true);
        }
        for (var index = views.Length; index < tabButtons.Length; ++index)
        {
            SetActive(tabButtons[index], false);
        }
    }

    private static void BuildInstalledView()
    {
        _frameworkDiagnosticsRow = CreateActionButton(
            _mainButtonTemplate,
            InstalledView,
            "LOADER",
            new Vector2(ContentCenterX, 350f),
            new Vector2(ContentWidth, 54f),
            OpenRuntimeDiagnostics);
        RefreshFrameworkDiagnosticsRow();

        for (var index = 0; index < 4; ++index)
        {
            var cardIndex = index;
            var row = CreateActionButton(
                _mainButtonTemplate,
                InstalledView,
                "INSTALLED MOD",
                new Vector2(ContentCenterX, 280f - index * 70f),
                new Vector2(ContentWidth, 58f),
                () => ActivateInstalledCard(cardIndex));
            InstalledCardRows.Add(row);
        }

        _installedPreviousButton = CreateActionButton(
            _mainButtonTemplate,
            InstalledView,
            "PREVIOUS",
            new Vector2(ContentCenterX - 235f, -25f),
            new Vector2(440f, 54f),
            () => MoveInstalledPage(-1));
        _installedNextButton = CreateActionButton(
            _mainButtonTemplate,
            InstalledView,
            "NEXT",
            new Vector2(ContentCenterX + 235f, -25f),
            new Vector2(440f, 54f),
            () => MoveInstalledPage(1));

        _installedDetailTitle = CreateActionButton(
            _mainButtonTemplate,
            InstalledDetailView,
            "MOD DETAILS",
            new Vector2(ContentCenterX, 330f),
            new Vector2(ContentWidth, 66f));
        _installedDetailInfo = CreateActionButton(
            _mainButtonTemplate,
            InstalledDetailView,
            "DETAILS",
            new Vector2(ContentCenterX, 205f),
            new Vector2(ContentWidth, 150f));
        _installedDetailToggle = CreateActionButton(
            _mainButtonTemplate,
            InstalledDetailView,
            "DISABLE",
            new Vector2(ContentCenterX - 235f, 80f),
            new Vector2(440f, 58f),
            ToggleSelectedInstalledMod);
        _installedDetailUninstall = CreateActionButton(
            _mainButtonTemplate,
            InstalledDetailView,
            "UNINSTALL",
            new Vector2(ContentCenterX + 235f, 80f),
            new Vector2(440f, 58f),
            UninstallSelectedMod);
        _installedDetailBack = CreateActionButton(
            _mainButtonTemplate,
            InstalledDetailView,
            "BACK TO INSTALLED MODS",
            new Vector2(ContentCenterX, 5f),
            new Vector2(ContentWidth, 58f),
            CloseInstalledDetail);
        RefreshInstalledModRows();
    }

    private static void ActivateInstalledCard(int cardIndex)
    {
        var installed = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
        var absoluteIndex = _installedPageIndex * InstalledCardRows.Count + cardIndex;
        if (absoluteIndex < 0 || absoluteIndex >= installed.Count) return;
        _selectedInstalledModId = installed[absoluteIndex].Manifest.Id;
        _confirmInstalledUninstall = false;
        RefreshInstalledModRows();
        RuntimeLog.Write($"Installed mod detail opened: {_selectedInstalledModId}.");
    }

    private static void ToggleSelectedInstalledMod()
    {
        if (_selectedInstalledModId is not { } modId) return;
        try
        {
            if (ModSafetyStore.IsQuarantined(modId))
            {
                var cleared = ModSafetyStore.Clear(modId);
                RuntimeLog.Write(
                    $"Mod quarantine clear requested: id={modId}, cleared={cleared}; " +
                    "restart required to retry loading.");
            }
            else
            {
                var change = ModProfileStore.Toggle(modId);
                RuntimeLog.Write(
                    $"Pending profile changed: id={modId}, desired={change.DesiredEnabled}, " +
                    $"affected={string.Join(',', change.AffectedIds)}, " +
                    $"restart={change.RestartRequired}.");
            }
            _confirmInstalledUninstall = false;
            RefreshInstalledModRows();
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Mod profile toggle failed for '{modId}': {exception}");
        }
    }

    private static void UninstallSelectedMod()
    {
        if (_selectedInstalledModId is not { } modId) return;
        var installed = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
        var mod = installed.FirstOrDefault(value =>
            string.Equals(value.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null || mod.UninstallOnRestart) return;
        if (!_confirmInstalledUninstall)
        {
            _confirmInstalledUninstall = true;
            RefreshInstalledModRows();
            return;
        }
        try
        {
            PendingModInstaller.StageUninstall(modId);
            _confirmInstalledUninstall = false;
            RuntimeLog.Write($"Mod uninstall staged for restart: id={modId}.");
            RefreshInstalledModRows();
        }
        catch (Exception exception)
        {
            _confirmInstalledUninstall = false;
            RuntimeLog.Write($"Mod uninstall staging failed for '{modId}': {exception}");
            RefreshInstalledModRows();
        }
    }

    private static void CloseInstalledDetail()
    {
        _selectedInstalledModId = null;
        _confirmInstalledUninstall = false;
        RefreshInstalledModRows();
    }

    private static void MoveInstalledPage(int offset)
    {
        var installed = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
        var pageSize = Math.Max(1, InstalledCardRows.Count);
        var pageCount = Math.Max(1, (installed.Count + pageSize - 1) / pageSize);
        _installedPageIndex = (_installedPageIndex + offset + pageCount) % pageCount;
        RefreshInstalledModRows();
        RuntimeLog.Write(
            $"Installed mods page: index={_installedPageIndex + 1}/{pageCount}, " +
            $"entries={installed.Count}.");
    }

    private static void BuildBrowseView(IReadOnlyList<InstalledModState> installedMods)
    {
        _cachedCatalog = ModCatalogCache.Load();
        _catalogBrowser = new ModCatalogBrowser(_cachedCatalog.Entries);
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The game process directory is unavailable.");
        _thumbnailStore = new CatalogThumbnailStore(
            Path.Combine(gameDirectory, "OFS", "cache", "thumbnails"),
            CatalogThumbnailHttp);
        BrowseInstalledIds.UnionWith(installedMods.Select(mod => mod.Manifest.Id));

        _browseSearchRow = CreateActionButton(
            _mainButtonTemplate,
            BrowseView,
            "SEARCH: ALL",
            new Vector2(ContentCenterX - 160f, 350f),
            new Vector2(620f, 54f),
            () =>
            {
                _browseSearchFocused = !_browseSearchFocused;
                if (_browseSearchFocused)
                {
                    DrainBrowseSearchKeys();
                }
                RuntimeLog.Write(
                    $"Catalog search focus={_browseSearchFocused}, query='{_catalogBrowser.Query}'.");
                RefreshBrowseView();
            });

        _browseSummaryRow = CreateActionButton(
            _mainButtonTemplate,
            BrowseView,
            "0 MODS",
            new Vector2(ContentCenterX + 320f, 350f),
            new Vector2(280f, 54f));
        SetButtonColor(_browseSummaryRow, new Color(0f, 0f, 0f, 0f));

        for (var index = 0; index < 4; ++index)
        {
            var cardIndex = index;
            var row = CreateActionButton(
                _mainButtonTemplate,
                BrowseView,
                "CATALOG ENTRY",
                new Vector2(ContentCenterX, 280f - index * 70f),
                new Vector2(ContentWidth, 58f),
                () => OpenBrowseCard(cardIndex));
            BrowseCardRows.Add(row);
            RegisterThumbnailTarget(row, new Vector2(ContentCenterX - 425f, 280f - index * 70f));
        }

        _browsePreviousButton = CreateActionButton(
            _mainButtonTemplate,
            BrowseView,
            "PREVIOUS",
            new Vector2(ContentCenterX - 235f, -25f),
            new Vector2(440f, 54f),
            () =>
            {
                _ = _catalogBrowser?.PreviousPage();
                LogBrowseResultState("previous");
                RefreshBrowseView();
            });
        _browseNextButton = CreateActionButton(
            _mainButtonTemplate,
            BrowseView,
            "NEXT",
            new Vector2(ContentCenterX + 235f, -25f),
            new Vector2(440f, 54f),
            () =>
            {
                _ = _catalogBrowser?.NextPage();
                LogBrowseResultState("next");
                RefreshBrowseView();
            });

        _browseDetailTitle = CreateActionButton(
            _mainButtonTemplate,
            BrowseDetailView,
            "MOD DETAILS",
            new Vector2(ContentCenterX, 330f),
            new Vector2(ContentWidth, 66f),
            () => { });
        RegisterThumbnailTarget(_browseDetailTitle, new Vector2(ContentCenterX - 425f, 330f));
        _browseDetailInfo = CreateActionButton(
            _mainButtonTemplate,
            BrowseDetailView,
            "DETAILS",
            new Vector2(ContentCenterX, 195f),
            new Vector2(ContentWidth, 190f),
            () => { });
        SetLabelFontSize(_browseDetailInfo, 17f);
        _browseDetailInstall = CreateActionButton(
            _mainButtonTemplate,
            BrowseDetailView,
            "INSTALL",
            new Vector2(ContentCenterX - 235f, 30f),
            new Vector2(440f, 58f),
            InstallSelectedCatalogEntry);
        _browseDetailBack = CreateActionButton(
            _mainButtonTemplate,
            BrowseDetailView,
            "BACK TO RESULTS",
            new Vector2(ContentCenterX + 235f, 30f),
            new Vector2(440f, 58f),
            () =>
            {
                _catalogBrowser?.BackToResults();
                _browseSearchFocused = false;
                RefreshBrowseView();
            });

        RefreshBrowseView();
        BeginOfficialCatalogRefresh();
    }

    private static void BeginOfficialCatalogRefresh()
    {
        if (Interlocked.CompareExchange(ref _officialCatalogRefreshBusy, 1, 0) != 0)
        {
            return;
        }
        var generation = _sceneGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var refreshed = await ModCatalogCache.RefreshOfficialAsync();
                ModRuntime.EnqueueMainThread(() =>
                {
                    if (!IsSceneHandleAlive(generation)) return;
                    _cachedCatalog = refreshed;
                    _catalogBrowser = new ModCatalogBrowser(refreshed.Entries);
                    RefreshBrowseView();
                    RefreshJoinFixView();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _officialCatalogRefreshBusy, 0);
            }
        });
    }

    private static void BuildDiagnosticsView()
    {
        _diagnosticSummary = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "STARTUP DIAGNOSTICS",
            new Vector2(ContentCenterX, 350f),
            new Vector2(ContentWidth, 58f));
        _diagnosticTitle = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "MANIFEST RESULT",
            new Vector2(ContentCenterX, 275f),
            new Vector2(ContentWidth, 64f));
        _diagnosticInfo = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "DETAILS",
            new Vector2(ContentCenterX, 155f),
            new Vector2(ContentWidth, 145f));
        _diagnosticPrevious = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "PREVIOUS",
            new Vector2(ContentCenterX - 315f, 35f),
            new Vector2(190f, 62f),
            () => MoveRuntimeDiagnostic(-1));
        _diagnosticBack = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "BACK",
            new Vector2(ContentCenterX, 35f),
            new Vector2(260f, 62f),
            CloseRuntimeDiagnostics);
        _diagnosticNext = CreateActionButton(
            _mainButtonTemplate,
            DiagnosticsView,
            "NEXT",
            new Vector2(ContentCenterX + 315f, 35f),
            new Vector2(190f, 62f),
            () => MoveRuntimeDiagnostic(1));
    }

    private static void OpenRuntimeDiagnostics()
    {
        _diagnosticsOpen = true;
        _diagnosticIndex = 0;
        SetViewObjects(InstalledView, false);
        RefreshRuntimeDiagnosticsView();
        var report = ModDiagnosticsRuntime.CurrentReport;
        var page = report?.Mods.FirstOrDefault();
        RuntimeLog.Write(
            $"Runtime diagnostics view opened: state={report?.State}, " +
            $"entries={report?.Mods.Count ?? 0}, problems={report?.ProblemCount ?? 0}, " +
            $"pageStatus={page?.Status}, pageId={page?.Id ?? "<invalid>"}.");
    }

    private static void CloseRuntimeDiagnostics()
    {
        _diagnosticsOpen = false;
        SetViewObjects(DiagnosticsView, false);
        SetViewObjects(InstalledView, _modsOpen && _currentView == ModsView.Installed);
        RefreshInstalledModRows();
        SetLabel(_headerLabel, "Mods");
        RuntimeLog.Write("Runtime diagnostics view closed.");
    }

    private static void MoveRuntimeDiagnostic(int offset)
    {
        var count = ModDiagnosticsRuntime.CurrentReport?.Mods.Count ?? 0;
        if (count == 0) return;
        _diagnosticIndex = (_diagnosticIndex + offset + count) % count;
        RefreshRuntimeDiagnosticsView();
        var diagnostic = ModDiagnosticsRuntime.CurrentReport!.Mods[_diagnosticIndex];
        RuntimeLog.Write(
            $"Runtime diagnostics page: index={_diagnosticIndex + 1}/{count}, " +
            $"status={diagnostic.Status}, id={diagnostic.Id ?? "<invalid>"}.");
    }

    private static void RefreshRuntimeDiagnosticsView()
    {
        var visible = _modsOpen && _currentView == ModsView.Installed && _diagnosticsOpen;
        SetViewObjects(DiagnosticsView, visible);
        if (!visible) return;

        var report = ModDiagnosticsRuntime.CurrentReport;
        if (report is null)
        {
            SetLabel(_diagnosticSummary, "STARTUP DIAGNOSTICS    [UNAVAILABLE]");
            SetLabel(_diagnosticTitle, "NO IN-MEMORY REPORT");
            SetLabel(_diagnosticInfo, "THE LOADER COULD NOT CREATE A DIAGNOSTIC REPORT.");
            SetActive(_diagnosticPrevious, false);
            SetActive(_diagnosticNext, false);
            SetLabel(_headerLabel, "Mods");
            return;
        }

        SetLabel(
            _diagnosticSummary,
            $"STARTUP {report.State.ToString().ToUpperInvariant()}    " +
            $"{report.Mods.Count}/{report.DiscoveredManifestCount} RESULTS    " +
            $"{report.ProblemCount} PROBLEMS");
        if (report.Mods.Count == 0)
        {
            SetLabel(_diagnosticTitle, "NO MOD MANIFESTS DISCOVERED");
            SetLabel(_diagnosticInfo, "THE FRAMEWORK STARTED WITHOUT USER MODS.");
            SetActive(_diagnosticPrevious, false);
            SetActive(_diagnosticNext, false);
        }
        else
        {
            _diagnosticIndex = Math.Clamp(_diagnosticIndex, 0, report.Mods.Count - 1);
            var diagnostic = report.Mods[_diagnosticIndex];
            var identity = diagnostic.Name ?? diagnostic.Id ??
                Path.GetFileName(Path.GetDirectoryName(diagnostic.ManifestPath)) ?? "INVALID MANIFEST";
            SetLabel(
                _diagnosticTitle,
                $"{identity.ToUpperInvariant()}" +
                $"{(diagnostic.Version is null ? string.Empty : $"  v{diagnostic.Version}")}    " +
                $"[{diagnostic.Status.ToString().ToUpperInvariant()}]");
            var related = diagnostic.RelatedModIds.Count == 0
                ? "NONE"
                : string.Join(", ", diagnostic.RelatedModIds);
            var manifestDirectory = Path.GetFileName(
                Path.GetDirectoryName(diagnostic.ManifestPath));
            var manifestDisplay = Path.Combine(
                string.IsNullOrWhiteSpace(manifestDirectory) ? "<unknown>" : manifestDirectory,
                Path.GetFileName(diagnostic.ManifestPath));
            SetLabel(
                _diagnosticInfo,
                $"RESULT {_diagnosticIndex + 1}/{report.Mods.Count}    " +
                $"PHASE: {CompactCatalogText(diagnostic.Phase, 50).ToUpperInvariant()}\n" +
                $"{CompactCatalogText(diagnostic.Message, 260)}\n" +
                $"RELATED: {CompactCatalogText(related, 100)}\n" +
                $"MANIFEST: {CompactCatalogText(manifestDisplay, 80)}");
            var multiple = report.Mods.Count > 1;
            SetActive(_diagnosticPrevious, multiple);
            SetActive(_diagnosticNext, multiple);
            SetLabel(_diagnosticPrevious, $"PREV {_diagnosticIndex + 1}/{report.Mods.Count}");
            SetLabel(_diagnosticNext, $"NEXT {_diagnosticIndex + 1}/{report.Mods.Count}");
        }
        SetLabel(_diagnosticBack, "BACK");
        SetLabel(_headerLabel, "Mods");
    }

    private static void RefreshFrameworkDiagnosticsRow()
    {
        var report = ModDiagnosticsRuntime.CurrentReport;
        var state = report?.State.ToString().ToUpperInvariant() ?? "UNAVAILABLE";
        var entries = report?.Mods.Count ?? 0;
        var problems = report?.ProblemCount ?? 0;
        SetSingleRowLabel(
            _frameworkDiagnosticsRow,
            $"LOADER v{typeof(UnityUiRuntime).Assembly.GetName().Version?.ToString(3)}    " +
            $"{state}    {entries} {(entries == 1 ? "MOD" : "MODS")}    {problems} PROBLEMS");
    }

    private static void ApplyLastJoinFix()
    {
        var plan = NetworkCompatibilityRuntime.LastRemediationPlan;
        if (plan is null)
        {
            RuntimeLog.Write("No multiplayer remediation plan is pending.");
            return;
        }
        if (!plan.Success)
        {
            RuntimeLog.Write(
                $"Join remediation cannot be applied: {string.Join(" ", plan.Errors)}");
            RefreshJoinFixView();
            return;
        }
        if (!plan.RestartRequired)
        {
            RuntimeLog.Write("Join remediation requires no mod/profile changes.");
            RefreshJoinFixView();
            return;
        }

        var generation = _sceneGeneration;
        RuntimeCatalogInstaller.BeginRemediation(
            plan,
            status =>
            {
                if (!IsSceneHandleAlive(generation) || _joinFixActionRow == 0) return;
                SetLabel(_joinFixActionRow, status);
            });
    }

    private static void LogLastJoinFix()
    {
        var plan = NetworkCompatibilityRuntime.LastRemediationPlan;
        if (plan is null)
        {
            RuntimeLog.Write("No multiplayer remediation plan is pending.");
            return;
        }
        RuntimeLog.Write(
            $"Join remediation details: success={plan.Success}, restart={plan.RestartRequired}, " +
            $"install={string.Join(',', plan.InstallOrder.Select(entry => $"{entry.Id}@{entry.Version}"))}, " +
            $"enable={string.Join(',', plan.EnableIds)}, disable={string.Join(',', plan.DisableIds)}, " +
            $"errors={string.Join(" | ", plan.Errors)}.");
    }

    private static void RefreshJoinFixView()
    {
        if (_joinFixActionRow == 0) return;
        var plan = NetworkCompatibilityRuntime.LastRemediationPlan;
        var catalogStatus = _cachedCatalog?.Status ?? "NOT LOADED";
        SetLabel(_joinFixCatalogRow, $"TRUSTED CATALOG    [{catalogStatus}]");
        if (plan is null)
        {
            SetLabel(_joinFixActionRow, "JOIN FIX    [NO MISMATCH]");
            SetLabel(_joinFixDetailsRow, "NO MULTIPLAYER REMEDIATION IS PENDING");
            return;
        }
        if (!plan.Success)
        {
            SetLabel(_joinFixActionRow, "JOIN FIX    [UNAVAILABLE - SELECT FOR LOG]");
            SetLabel(
                _joinFixDetailsRow,
                CompactCatalogText(plan.Errors.FirstOrDefault() ?? "UNKNOWN ERROR", 110).ToUpperInvariant());
            return;
        }
        SetLabel(
            _joinFixActionRow,
            plan.RestartRequired
                ? $"APPLY JOIN FIX    [{plan.InstallOrder.Count} DOWNLOAD / " +
                  $"{plan.EnableIds.Count} ENABLE / {plan.DisableIds.Count} DISABLE]"
                : "JOIN FIX    [NO MOD CHANGES]");
        var differences = plan.Differences.Count == 0
            ? "PROFILE DIFF IS NOT MOD-RESOLVABLE"
            : string.Join("; ", plan.Differences.Take(3).Select(difference =>
                difference.Kind switch
                {
                    NetworkModDifferenceKind.MissingLocal =>
                        $"GET {difference.Id}@{difference.RemoteVersion}",
                    NetworkModDifferenceKind.UnexpectedLocal =>
                        $"DISABLE {difference.Id}",
                    _ =>
                        $"CHANGE {difference.Id} TO {difference.RemoteVersion}",
                }));
        SetLabel(_joinFixDetailsRow, CompactCatalogText(differences, 110).ToUpperInvariant());
    }

    private static void OpenBrowseCard(int cardIndex)
    {
        if (_catalogBrowser is null)
        {
            return;
        }

        var page = _catalogBrowser.PageEntries;
        if (cardIndex >= page.Count)
        {
            return;
        }

        _ = _catalogBrowser.Select(page[cardIndex].Id);
        _browseSearchFocused = false;
        RuntimeLog.Write(
            $"Catalog detail opened: {page[cardIndex].Id} {page[cardIndex].Version}.");
        RefreshBrowseView();
    }

    private static void InstallSelectedCatalogEntry()
    {
        var entry = _catalogBrowser?.Selected;
        if (entry is null || _cachedCatalog?.Catalog is null)
        {
            return;
        }
        if (BrowseInstalledIds.Contains(entry.Id))
        {
            RuntimeLog.Write($"Catalog entry '{entry.Id}' is already installed.");
            return;
        }

        var generation = _sceneGeneration;
        RuntimeCatalogInstaller.BeginInstall(
            _cachedCatalog.Catalog,
            entry.Id,
            state =>
            {
                BrowseInstallStates[entry.Id] = state;
                if (_sceneGeneration == generation && _managerInstance != 0)
                {
                    RefreshBrowseView();
                }
            });
    }

    private static void RefreshBrowseView()
    {
        if (_catalogBrowser is null || _browseSearchRow == 0)
        {
            return;
        }

        var browseVisible = _modsOpen && _currentView == ModsView.Browse;
        var selected = _catalogBrowser.Selected;
        var showResults = browseVisible && selected is null;
        var showDetail = browseVisible && selected is not null;

        SetActive(_browseSearchRow, showResults);
        SetActive(_browseSummaryRow, showResults);
        var query = _catalogBrowser.Query.Trim();
        var searchText = query.Length == 0 ? "ALL" : query.ToUpperInvariant();
        var focus = _browseSearchFocused ? "_" : string.Empty;
        SetLabel(_browseSearchRow, $"SEARCH MODS: {searchText}{focus}");
        SetLabel(
            _browseSummaryRow,
            $"{_catalogBrowser.MatchCount} " +
            $"{(_catalogBrowser.MatchCount == 1 ? "MOD" : "MODS")}    " +
            $"PAGE {_catalogBrowser.PageIndex + 1}/{_catalogBrowser.PageCount}");

        var page = _catalogBrowser.PageEntries;
        for (var index = 0; index < BrowseCardRows.Count; ++index)
        {
            var row = BrowseCardRows[index];
            if (index < page.Count)
            {
                SetBrowseRowColumns(
                    row,
                    $"{page[index].Name.ToUpperInvariant()}    v{page[index].Version}",
                    $"[{GetBrowseState(page[index])}]");
                if (showResults)
                {
                    ApplyCatalogThumbnail(row, page[index]);
                }
                else
                {
                    ResetCatalogThumbnail(row);
                }
                SetActive(row, showResults);
            }
            else if (index == 0 && page.Count == 0)
            {
                SetSingleRowLabel(row, _cachedCatalog?.Entries.Count == 0
                    ? _cachedCatalog.Status
                    : "NO MODS MATCH THIS SEARCH");
                ResetCatalogThumbnail(row);
                SetActive(row, showResults);
            }
            else
            {
                ResetCatalogThumbnail(row);
                SetActive(row, false);
            }
        }

        var hasMultiplePages = _catalogBrowser.PageCount > 1;
        SetLabel(_browsePreviousButton,
            _catalogBrowser.PageIndex > 0 ? "PREVIOUS PAGE" : "FIRST PAGE");
        SetLabel(_browseNextButton,
            _catalogBrowser.PageIndex + 1 < _catalogBrowser.PageCount ? "NEXT PAGE" : "LAST PAGE");
        SetActive(_browsePreviousButton, showResults && hasMultiplePages);
        SetActive(_browseNextButton, showResults && hasMultiplePages);

        foreach (var gameObject in BrowseDetailView)
        {
            SetActive(gameObject, showDetail);
        }
        if (selected is null)
        {
            return;
        }

        SetBrowseRowColumns(
            _browseDetailTitle,
            $"{selected.Name.ToUpperInvariant()}  v{selected.Version}",
            string.Empty);
        if (showDetail)
        {
            ApplyCatalogThumbnail(_browseDetailTitle, selected);
        }
        else
        {
            ResetCatalogThumbnail(_browseDetailTitle);
        }
        var dependencies = selected.Dependencies.Count == 0
            ? "NONE"
            : string.Join(", ", selected.Dependencies.Select(value => value.Id));
        var capabilities = selected.Capabilities.Count == 0
            ? "NONE"
            : string.Join(", ", selected.Capabilities);
        SetSingleRowLabel(
            _browseDetailInfo,
            $"{CompactCatalogText(selected.Summary, 105)}\n" +
            $"BY {CompactCatalogText(selected.Author, 35).ToUpperInvariant()}    " +
            $"MULTIPLAYER: {selected.Multiplayer.ToUpperInvariant()}    " +
            $"SIZE: {FormatBytes(selected.Package.Bytes)}\n" +
            $"DEPENDENCIES: {CompactCatalogText(dependencies, 90)}\n" +
            $"CAPABILITIES: {CompactCatalogText(capabilities, 110)}");

        var installed = BrowseInstalledIds.Contains(selected.Id);
        var state = BrowseInstallStates.TryGetValue(selected.Id, out var installState)
            ? installState
            : installed ? "INSTALLED" : "INSTALL";
        SetLabel(_browseDetailInstall, state);
        SetButtonColor(
            _browseDetailInstall,
            installed ? NeutralButtonColor : AccentButtonColor);
        SetLabel(_browseDetailBack, "BACK TO RESULTS");
        SetActive(_browseDetailInstall, showDetail);
        SetActive(_browseDetailBack, showDetail);
    }

    private static void RegisterThumbnailTarget(nint row, Vector2 position)
    {
        var sourceIcon = FindFirstDescendantWithComponent(row, _imageClass);
        if (sourceIcon == 0)
        {
            RuntimeLog.Write($"Catalog thumbnail target missing below '{GetObjectName(row)}'.");
            return;
        }
        var iconObject = Instantiate(sourceIcon, GetTransform(_modsPanel));
        SetObjectName(iconObject, $"OFS Thumbnail ({GetObjectName(row)})");
        SetActive(sourceIcon, false);
        SetRect(iconObject, new Vector2(0.5f, 0.5f), position, new Vector2(54f, 54f));
        var image = GetComponent(iconObject, _imageClass);
        var defaultSprite = InvokeReference(RequireMethod(_imageClass, "get_sprite", 0), image);
        var defaultColor = ReadColor(RequireMethod(_imageClass, "get_color", 0), image);
        BrowseThumbnailTargets[row] = new ThumbnailTarget(
            iconObject,
            image,
            defaultSprite,
            defaultColor);
        SetActive(iconObject, false);
    }

    private static void ApplyCatalogThumbnail(nint row, ModCatalogEntry entry)
    {
        if (!BrowseThumbnailTargets.TryGetValue(row, out var target))
        {
            return;
        }
        SetActive(target.GameObject, true);
        var thumbnail = entry.Thumbnail;
        if (thumbnail is null)
        {
            SetImageSprite(target.Image, target.DefaultSprite, target.DefaultColor);
            return;
        }
        var hash = thumbnail.Sha256.ToLowerInvariant();
        if (CatalogThumbnailSprites.TryGetValue(hash, out var sprite))
        {
            SetImageSprite(target.Image, sprite, new Color(1f, 1f, 1f, 1f));
            return;
        }
        SetImageSprite(target.Image, target.DefaultSprite, target.DefaultColor);
        EnsureCatalogThumbnailRequested(entry);
    }

    private static void ResetCatalogThumbnail(nint row)
    {
        if (BrowseThumbnailTargets.TryGetValue(row, out var target))
        {
            SetImageSprite(target.Image, target.DefaultSprite, target.DefaultColor);
            SetActive(target.GameObject, false);
        }
    }

    private static void EnsureCatalogThumbnailRequested(ModCatalogEntry entry)
    {
        var thumbnail = entry.Thumbnail;
        if (thumbnail is null || _thumbnailStore is null)
        {
            return;
        }
        var hash = thumbnail.Sha256.ToLowerInvariant();
        if (CatalogThumbnailSprites.ContainsKey(hash) ||
            CatalogThumbnailFailures.Contains(hash) ||
            !CatalogThumbnailRequests.Add(hash))
        {
            return;
        }
        var generation = _sceneGeneration;
        var store = _thumbnailStore;
        _ = Task.Run(async () =>
        {
            try
            {
                var cached = await store.GetOrFetchAsync(thumbnail).ConfigureAwait(false);
                var bytes = await File.ReadAllBytesAsync(cached.Path).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(bytes),
                        Convert.FromHexString(thumbnail.Sha256)))
                {
                    throw new InvalidDataException("Thumbnail cache changed after verification.");
                }
                _ = CatalogThumbnailStore.Inspect(bytes);
                ModRuntime.EnqueueMainThread(() =>
                {
                    if (!IsSceneHandleAlive(generation))
                    {
                        return;
                    }
                    try
                    {
                        var sprite = CreateCatalogThumbnailSprite(bytes, hash);
                        CatalogThumbnailSprites[hash] = sprite;
                        RuntimeLog.Write(
                            $"Catalog thumbnail ready: id={entry.Id}, {cached.Width}x{cached.Height}, " +
                            $"cache={cached.FromCache}, sha256={hash}.");
                        RefreshBrowseView();
                    }
                    catch (Exception exception)
                    {
                        CatalogThumbnailFailures.Add(hash);
                        RuntimeLog.Write(
                            $"Catalog thumbnail Unity decode failed for '{entry.Id}': {exception.Message}");
                    }
                });
            }
            catch (Exception exception)
            {
                ModRuntime.EnqueueMainThread(() =>
                {
                    if (!IsSceneHandleAlive(generation))
                    {
                        return;
                    }
                    CatalogThumbnailFailures.Add(hash);
                    RuntimeLog.Write(
                        $"Catalog thumbnail unavailable for '{entry.Id}': {exception.Message}");
                });
            }
        });
    }

    private static nint CreateCatalogThumbnailSprite(byte[] bytes, string hash)
    {
        var texture = Native.object_new(_texture2DClass);
        if (texture == 0)
        {
            throw new InvalidOperationException("Texture2D allocation returned null.");
        }
        InvokeVoidWithTwoInt32(
            RequireMethod(_texture2DClass, ".ctor", 2),
            texture,
            2,
            2);
        var array = Native.array_new(_byteClass, (nuint)bytes.Length);
        if (array == 0)
        {
            throw new InvalidOperationException("Thumbnail byte[] allocation returned null.");
        }
        Marshal.Copy(bytes, 0, array + (4 * nint.Size), bytes.Length);
        var loaded = InvokeBooleanWithTwoObjects(
            RequireMethod(_imageConversionClass, "LoadImage", 2),
            0,
            texture,
            array);
        if (!loaded)
        {
            throw new InvalidDataException("Unity ImageConversion.LoadImage rejected the thumbnail.");
        }
        SetObjectName(texture, $"OFS Catalog Texture {hash}");
        var width = InvokeInt32(RequireMethod(_texture2DClass, "get_width", 0), texture);
        var height = InvokeInt32(RequireMethod(_texture2DClass, "get_height", 0), texture);
        var sprite = InvokeSpriteCreate(
            RequireMethod(_spriteClass, "Create", 4),
            texture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            100f);
        if (sprite == 0)
        {
            throw new InvalidOperationException("Sprite.Create returned null.");
        }
        SetObjectName(sprite, $"OFS Catalog Sprite {hash}");
        return sprite;
    }

    private static void SetImageSprite(nint image, nint sprite, Color color)
    {
        InvokeVoidWithObject(RequireMethod(_imageClass, "set_sprite", 1), image, sprite);
        InvokeVoidWithBool(RequireMethod(_imageClass, "set_preserveAspect", 1), image, true);
        InvokeVoidWithColor(RequireMethod(_imageClass, "set_color", 1), image, color);
    }

    private static readonly Color AccentButtonColor = new(0.68f, 0.44f, 0.24f, 1f);
    private static readonly Color NeutralButtonColor = new(0.07f, 0.07f, 0.07f, 0.96f);
    private static readonly Color ContentButtonColor = new(0.02f, 0.02f, 0.02f, 0.78f);
    private static readonly Color WarningButtonColor = new(0.66f, 0.25f, 0.20f, 1f);

    private static void SetButtonColor(nint gameObject, Color color)
    {
        var image = TryGetComponent(gameObject, _imageClass);
        if (image != 0)
        {
            InvokeVoidWithColor(RequireMethod(_imageClass, "set_color", 1), image, color);
        }
    }

    private static void SetButtonVisualColor(nint gameObject, Color color)
    {
        SetButtonColor(gameObject, color);
        foreach (var imageObject in FindDescendantsWithComponent(gameObject, _imageClass))
        {
            var image = TryGetComponent(imageObject, _imageClass);
            if (image != 0)
            {
                InvokeVoidWithColor(RequireMethod(_imageClass, "set_color", 1), image, color);
            }
        }
    }

    private static nint FindFirstDescendantWithComponent(nint parent, nint componentClass)
    {
        foreach (var child in GetDirectChildren(GetTransform(parent)))
        {
            if (TryGetComponent(child, componentClass) != 0)
            {
                return child;
            }
            var nested = FindFirstDescendantWithComponent(child, componentClass);
            if (nested != 0)
            {
                return nested;
            }
        }
        return 0;
    }

    private static IEnumerable<nint> FindDescendantsWithComponent(
        nint parent,
        nint componentClass)
    {
        foreach (var child in GetDirectChildren(GetTransform(parent)))
        {
            if (TryGetComponent(child, componentClass) != 0)
            {
                yield return child;
            }
            foreach (var nested in FindDescendantsWithComponent(child, componentClass))
            {
                yield return nested;
            }
        }
    }

    private static HttpClient CreateCatalogThumbnailHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "OFS-Framework",
            ModManifestValidator.CurrentSdkVersion.ToString(3)));
        return client;
    }

    private static string GetBrowseState(ModCatalogEntry entry)
    {
        return BrowseInstallStates.TryGetValue(entry.Id, out var installState)
            ? installState
            : BrowseInstalledIds.Contains(entry.Id) ? "INSTALLED" : "AVAILABLE";
    }

    private static string CompactCatalogText(string? value, int maximumLength)
    {
        var compact = string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length == 0)
        {
            return "N/A";
        }
        return compact.Length <= maximumLength
            ? compact
            : compact[..Math.Max(0, maximumLength - 3)] + "...";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes / (1024d * 1024d):0.0} MiB";
    }

    private static nint CreateActionRow(
        nint template,
        List<nint> viewObjects,
        string label,
        float y,
        Action? action = null)
        => CreateActionButton(
            template,
            viewObjects,
            label,
            new Vector2(ContentCenterX, y),
            new Vector2(ContentWidth, 62f),
            action);

    private static nint CreateActionButton(
        nint template,
        List<nint> viewObjects,
        string label,
        Vector2 position,
        Vector2 size,
        Action? action = null)
    {
        var gameObject = Instantiate(template, GetTransform(_modsPanel));
        SetObjectName(gameObject, $"OFS Row ({label})");
        SetRect(gameObject, new Vector2(0.5f, 0.5f), position, size);
        SetLabel(gameObject, label);
        SetButtonColor(gameObject, ContentButtonColor);
        SetActionButtonIconVisible(gameObject, false);
        RowActions[GetComponent(gameObject, _buttonClass)] = action ??
            (() => RuntimeLog.Write($"Mods row selected: {label}."));
        viewObjects.Add(gameObject);
        SetActive(gameObject, false);
        return gameObject;
    }

    private static void SetRowColumns(nint row, string label, string value)
    {
        var primary = FindDescendant(row, "ButtonName_Text");
        if (!SecondaryRowLabels.TryGetValue(row, out var secondary))
        {
            secondary = Instantiate(primary, GetTransform(row));
            SetObjectName(secondary, "OFS Value Text");
            SecondaryRowLabels[row] = secondary;
        }
        SetRect(primary, new Vector2(0.5f, 0.5f), new Vector2(-170f, 0f), new Vector2(540f, 54f));
        SetRect(secondary, new Vector2(0.5f, 0.5f), new Vector2(300f, 0f), new Vector2(320f, 54f));
        SetLabel(primary, label);
        SetLabel(secondary, value);
        SetActive(secondary, true);
    }

    private static void SetBrowseRowColumns(nint row, string label, string value)
    {
        var primary = FindDescendant(row, "ButtonName_Text");
        if (!SecondaryRowLabels.TryGetValue(row, out var secondary))
        {
            secondary = Instantiate(primary, GetTransform(row));
            SetObjectName(secondary, "OFS Value Text");
            SecondaryRowLabels[row] = secondary;
        }
        SetRect(primary, new Vector2(0.5f, 0.5f), new Vector2(45f, 0f), new Vector2(520f, 54f));
        SetRect(secondary, new Vector2(0.5f, 0.5f), new Vector2(355f, 0f), new Vector2(230f, 54f));
        SetLabel(primary, label);
        SetLabel(secondary, value);
        SetActive(secondary, value.Length != 0);
    }

    private static void SetSingleRowLabel(nint row, string label)
    {
        var primary = FindDescendant(row, "ButtonName_Text");
        SetRect(
            primary,
            new Vector2(0.5f, 0.5f),
            new Vector2(0f, 0f),
            new Vector2(ContentWidth - 40f, 200f));
        SetLabel(primary, label);
        if (SecondaryRowLabels.TryGetValue(row, out var secondary))
        {
            SetActive(secondary, false);
        }
    }

    private static void SetActionButtonIconVisible(nint row, bool visible)
    {
        var iconObject = FindFirstDescendantWithComponent(row, _imageClass);
        if (iconObject != 0)
        {
            SetActive(iconObject, visible);
        }
    }

    private static void ShowView(ModsView view)
    {
        _diagnosticsOpen = false;
        _currentView = view;
        if (view != ModsView.Browse)
        {
            _browseSearchFocused = false;
        }
        SetViewObjects(InstalledView, view == ModsView.Installed);
        SetViewObjects(InstalledDetailView, false);
        SetViewObjects(DiagnosticsView, false);
        RefreshBrowseView();
        SetViewObjects(SettingsView, view == ModsView.Settings);
        foreach (var staticLabel in StaticViewLabels)
        {
            SetLabel(staticLabel.Key, staticLabel.Value);
        }
        if (view == ModsView.Installed)
        {
            RefreshInstalledModRows();
        }
        if (view == ModsView.Settings)
        {
            RefreshJoinFixView();
        }
        foreach (var tab in ViewTabObjects)
        {
            SetLabel(tab.Value, tab.Key.ToString().ToUpperInvariant());
            SetButtonVisualColor(
                tab.Value,
                tab.Key == view ? AccentButtonColor : NeutralButtonColor);
        }
        SetLabel(_headerLabel, "Mods");
    }

    private static void RefreshInstalledModRows()
    {
        if (_frameworkDiagnosticsRow == 0 || InstalledCardRows.Count == 0) return;
        var installed = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
        var pageSize = InstalledCardRows.Count;
        var pageCount = Math.Max(1, (installed.Count + pageSize - 1) / pageSize);
        _installedPageIndex = Math.Clamp(_installedPageIndex, 0, pageCount - 1);
        var page = installed.Skip(_installedPageIndex * pageSize).Take(pageSize).ToArray();
        var selected = _selectedInstalledModId is null
            ? null
            : installed.FirstOrDefault(mod => string.Equals(
                mod.Manifest.Id,
                _selectedInstalledModId,
                StringComparison.OrdinalIgnoreCase));
        if (_selectedInstalledModId is not null && selected is null)
        {
            _selectedInstalledModId = null;
            _confirmInstalledUninstall = false;
        }
        var visible = _modsOpen && _currentView == ModsView.Installed &&
                      !_diagnosticsOpen && _selectedInstalledModId is null;
        var detailVisible = _modsOpen && _currentView == ModsView.Installed &&
                            !_diagnosticsOpen && selected is not null;
        RefreshFrameworkDiagnosticsRow();
        for (var index = 0; index < InstalledCardRows.Count; ++index)
        {
            var row = InstalledCardRows[index];
            if (index < page.Length)
            {
                var mod = page[index];
                var manifestPath = Path.GetFullPath(Path.Combine(mod.Directory, "manifest.json"));
                var diagnostic = ModDiagnosticsRuntime.CurrentReport?.Mods.FirstOrDefault(value =>
                    string.Equals(value.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase));
                SetRowColumns(
                    row,
                    $"{mod.Manifest.Name.ToUpperInvariant()}    v{mod.Manifest.Version}",
                    $"[{FormatInstalledStatus(mod, diagnostic)}]");
                SetActive(row, visible);
            }
            else if (index == 0 && installed.Count == 0)
            {
                SetSingleRowLabel(row, "NO MODS INSTALLED");
                SetActive(row, visible);
            }
            else SetActive(row, false);
        }
        var multiplePages = pageCount > 1;
        SetLabel(_installedPreviousButton,
            _installedPageIndex > 0 ? "PREVIOUS PAGE" : "FIRST PAGE");
        SetLabel(_installedNextButton,
            _installedPageIndex + 1 < pageCount ? "NEXT PAGE" : "LAST PAGE");
        SetActive(_installedPreviousButton, visible && multiplePages);
        SetActive(_installedNextButton, visible && multiplePages);
        SetViewObjects(InstalledDetailView, detailVisible);
        if (selected is not null)
        {
            RefreshInstalledDetail(selected);
        }
    }

    private static void RefreshInstalledDetail(InstalledModState mod)
    {
        var manifestPath = Path.GetFullPath(Path.Combine(mod.Directory, "manifest.json"));
        var diagnostic = ModDiagnosticsRuntime.CurrentReport?.Mods.FirstOrDefault(value =>
            string.Equals(value.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase));
        var dependencies = mod.Manifest.Dependencies.Count == 0
            ? "NONE"
            : string.Join(", ", mod.Manifest.Dependencies.Select(value => value.Id));
        SetLabel(_installedDetailTitle,
            $"{mod.Manifest.Name.ToUpperInvariant()}    v{mod.Manifest.Version}");
        SetSingleRowLabel(_installedDetailInfo,
            $"STATUS: {FormatInstalledStatus(mod, diagnostic)}    ID: {mod.Manifest.Id}\n" +
            $"AUTHOR: {CompactCatalogText(mod.Manifest.Author, 50).ToUpperInvariant()}\n" +
            $"DEPENDENCIES: {CompactCatalogText(dependencies, 90)}\n" +
            "CHANGES ARE APPLIED AFTER RESTART");
        var toggle = mod.Quarantined
            ? "CLEAR QUARANTINE"
            : mod.DesiredEnabled ? "DISABLE ON RESTART" : "ENABLE ON RESTART";
        SetLabel(_installedDetailToggle, toggle);
        SetLabel(_installedDetailUninstall, mod.UninstallOnRestart
            ? "UNINSTALL ON RESTART"
            : _confirmInstalledUninstall ? "CONFIRM UNINSTALL" : "UNINSTALL");
        SetButtonColor(_installedDetailToggle, AccentButtonColor);
        SetButtonColor(
            _installedDetailUninstall,
            _confirmInstalledUninstall ? WarningButtonColor : NeutralButtonColor);
        SetLabel(_installedDetailBack, "BACK TO INSTALLED MODS");
    }

    private static string FormatInstalledStatus(
        InstalledModState mod,
        RuntimeModDiagnostic? diagnostic)
    {
        if (mod.UninstallOnRestart) return "UNINSTALL ON RESTART";
        if (mod.Quarantined) return "QUARANTINED";
        if (mod.RetryOnRestart) return "RETRY ON RESTART";
        if (mod.ActiveEnabled != mod.DesiredEnabled)
        {
            return mod.DesiredEnabled ? "ENABLE ON RESTART" : "DISABLE ON RESTART";
        }
        if (mod.Loaded) return "ENABLED";
        if (!mod.ActiveEnabled) return "DISABLED";
        return diagnostic?.Status == ModStartupStatus.Failed ? "FAILED" : "BLOCKED";
    }

    private static void SetViewObjects(IEnumerable<nint> objects, bool active)
    {
        foreach (var gameObject in objects)
        {
            SetActive(gameObject, active);
        }
    }

    private static void CreateFakeMod(
        nint template,
        string name,
        string version,
        bool enabled,
        float y)
    {
        var gameObject = Instantiate(template, GetTransform(_modsPanel));
        SetObjectName(gameObject, $"OFS Fake Mod ({name})");
        SetRect(gameObject, new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(760f, 82f));
        var button = GetComponent(gameObject, _buttonClass);
        var mod = new FakeMod(gameObject, name, version, enabled);
        FakeMods[button] = mod;
        InstalledView.Add(gameObject);
        SetLabel(gameObject, FormatModLabel(mod));
        SetActive(gameObject, true);
    }

    private static string FormatModLabel(FakeMod mod) =>
        $"{mod.Name.ToUpperInvariant()}  v{mod.Version}    [{(mod.Enabled ? "ENABLED" : "DISABLED")}]";

    private static nint Instantiate(nint original, nint parent)
    {
        var method = RequireMethod(_objectClass, "Instantiate", 1);
        var clone = InvokeReferenceWithObject(method, 0, original);
        if (parent != 0)
        {
            InvokeVoidWithObjectBool(
                RequireMethod(_transformClass, "SetParent", 2),
                GetTransform(clone),
                parent,
                false);
        }
        return clone;
    }

    private static nint GetTransform(nint gameObject) =>
        InvokeReference(RequireMethod(_gameObjectClass, "get_transform", 0), gameObject);

    private static nint GetComponent(nint gameObject, nint componentClass)
    {
        var component = TryGetComponent(gameObject, componentClass);
        return component != 0
            ? component
            : throw new InvalidOperationException(
                $"GameObject '{GetObjectName(gameObject)}' lacks the requested component.");
    }

    private static nint TryGetComponent(nint gameObject, nint componentClass)
    {
        var type = Native.class_get_type(componentClass);
        var typeObject = Native.type_get_object(type);
        return InvokeReferenceWithObject(
            RequireMethod(_gameObjectClass, "GetComponent", 1),
            gameObject,
            typeObject);
    }

    private static void SetLabel(nint gameObject, string text)
    {
        var labelObject = FindTextLabelObject(gameObject);
        var textComponent = GetComponent(labelObject, _textClass);
        var managedText = Native.string_new(text);
        InvokeVoidWithObject(
            RequireMethod(_textClass, "set_text", 1),
            textComponent,
            managedText);
    }

    private static string GetLabel(nint gameObject)
    {
        var labelObject = FindTextLabelObject(gameObject);
        var textComponent = GetComponent(labelObject, _textClass);
        var value = InvokeReference(RequireMethod(_textClass, "get_text", 0), textComponent);
        if (value == 0) return string.Empty;
        var length = Native.string_length(value);
        return Marshal.PtrToStringUni(Native.string_chars(value), length) ?? string.Empty;
    }

    private static void SetLabelFontSize(nint gameObject, float size)
    {
        var labelObject = FindTextLabelObject(gameObject);
        var textComponent = GetComponent(labelObject, _textClass);
        InvokeVoidWithFloat(
            RequireMethod(_textClass, "set_fontSize", 1),
            textComponent,
            size);
    }

    private static nint FindTextLabelObject(nint gameObject)
    {
        var labelObject = TryGetComponent(gameObject, _textClass) != 0
            ? gameObject
            : FindDescendantOrNull(gameObject, "ButtonName_Text");
        if (labelObject == 0)
        {
            labelObject = FindFirstDescendantWithComponent(gameObject, _textClass);
        }
        return labelObject != 0
            ? labelObject
            : throw new InvalidOperationException(
                $"GameObject '{GetObjectName(gameObject)}' has no text label.");
    }

    private static void SetObjectName(nint instance, string name)
    {
        InvokeVoidWithObject(
            RequireMethod(_objectClass, "set_name", 1),
            instance,
            Native.string_new(name));
    }

    private static void SetActive(nint gameObject, bool active) =>
        InvokeVoidWithBool(
            RequireMethod(_gameObjectClass, "SetActive", 1),
            gameObject,
            active);

    private static void HideDirectChildrenExcept(nint parent, string keptName)
    {
        var transform = GetTransform(parent);
        foreach (var child in GetDirectChildren(transform))
        {
            if (!string.Equals(GetObjectName(child), keptName, StringComparison.Ordinal))
            {
                SetActive(child, false);
            }
        }
    }

    private static nint FindDescendant(nint parent, string name)
    {
        var direct = FindDirectChildOrNull(parent, name);
        if (direct != 0)
        {
            return direct;
        }

        foreach (var child in GetDirectChildren(GetTransform(parent)))
        {
            var nested = FindDescendantOrNull(child, name);
            if (nested != 0)
            {
                return nested;
            }
        }

        throw new InvalidOperationException(
            $"Child '{name}' was not found below '{GetObjectName(parent)}'.");
    }

    private static nint FindDescendantOrNull(nint parent, string name)
    {
        var direct = FindDirectChildOrNull(parent, name);
        if (direct != 0)
        {
            return direct;
        }
        foreach (var child in GetDirectChildren(GetTransform(parent)))
        {
            var nested = FindDescendantOrNull(child, name);
            if (nested != 0)
            {
                return nested;
            }
        }
        return 0;
    }

    private static nint FindDirectChild(nint parent, string name)
    {
        var child = FindDirectChildOrNull(parent, name);
        return child != 0
            ? child
            : throw new InvalidOperationException(
                $"Direct child '{name}' was not found below '{GetObjectName(parent)}'.");
    }

    private static nint FindDirectChildOrNull(nint parent, string name) =>
        GetDirectChildren(GetTransform(parent))
            .FirstOrDefault(child => string.Equals(GetObjectName(child), name, StringComparison.Ordinal));

    private static IEnumerable<nint> GetDirectChildren(nint transform)
    {
        var childCount = InvokeInt32(RequireMethod(_transformClass, "get_childCount", 0), transform);
        for (var index = 0; index < childCount; ++index)
        {
            var childTransform = InvokeReferenceWithInt32(
                RequireMethod(_transformClass, "GetChild", 1),
                transform,
                index);
            yield return InvokeReference(
                RequireMethod(_componentClass, "get_gameObject", 0),
                childTransform);
        }
    }

    private static void SetRect(nint gameObject, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var rect = GetTransform(gameObject);
        InvokeVoidWithVector2(RequireMethod(_rectTransformClass, "set_anchorMin", 1), rect, anchor);
        InvokeVoidWithVector2(RequireMethod(_rectTransformClass, "set_anchorMax", 1), rect, anchor);
        InvokeVoidWithVector2(RequireMethod(_rectTransformClass, "set_pivot", 1), rect, new Vector2(0.5f, 0.5f));
        InvokeVoidWithVector2(RequireMethod(_rectTransformClass, "set_anchoredPosition", 1), rect, position);
        InvokeVoidWithVector2(RequireMethod(_rectTransformClass, "set_sizeDelta", 1), rect, size);
    }

    private static void LogHierarchy(
        nint gameObject,
        string path,
        int depth,
        int maxDepth)
    {
        if (gameObject == 0 || depth >= maxDepth)
        {
            return;
        }

        var transform = InvokeReference(RequireMethod(_gameObjectClass, "get_transform", 0), gameObject);
        var childCount = InvokeInt32(RequireMethod(_transformClass, "get_childCount", 0), transform);
        for (var index = 0; index < childCount; ++index)
        {
            var childTransform = InvokeReferenceWithInt32(
                RequireMethod(_transformClass, "GetChild", 1),
                transform,
                index);
            var childGameObject = InvokeReference(
                RequireMethod(_componentClass, "get_gameObject", 0),
                childTransform);
            var name = GetObjectName(childGameObject);
            var childPath = $"{path}/{name}";
            RuntimeLog.Write($"Unity UI hierarchy: {childPath}");
            LogHierarchy(childGameObject, childPath, depth + 1, maxDepth);
        }
    }

    private static nint ResolveClass(
        IReadOnlyDictionary<string, nint> images,
        string assembly,
        string namespaze,
        string name)
    {
        if (!images.TryGetValue(assembly, out var image))
        {
            var shortName = Path.GetFileNameWithoutExtension(assembly);
            image = images
                .Where(pair => string.Equals(
                    Path.GetFileNameWithoutExtension(pair.Key),
                    shortName,
                    StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }
        if (image == 0)
        {
            var candidates = string.Join(", ", images.Keys.Where(key => key.Contains("UnityEngine", StringComparison.Ordinal)));
            throw new InvalidOperationException(
                $"IL2CPP image '{assembly}' is not loaded. Unity candidates: {candidates}");
        }

        var klass = Native.class_from_name(image, namespaze, name);
        return klass != 0
            ? klass
            : throw new InvalidOperationException($"IL2CPP class '{namespaze}.{name}' was not found.");
    }

    private static nint ReadReferenceField(
        nint instance,
        nint klass,
        string fieldName,
        bool searchParents = false)
    {
        var currentClass = klass;
        while (currentClass != 0)
        {
            var field = Native.class_get_field_from_name(currentClass, fieldName);
            if (field != 0)
            {
                var offset = Native.field_get_offset(field);
                return Marshal.ReadIntPtr(instance, offset);
            }

            if (!searchParents)
            {
                break;
            }
            currentClass = Native.class_get_parent(currentClass);
        }

        throw new InvalidOperationException($"IL2CPP field '{fieldName}' was not found.");
    }

    private static string GetObjectName(nint instance)
    {
        if (instance == 0)
        {
            return "<null>";
        }

        var value = InvokeReference(RequireMethod(_objectClass, "get_name", 0), instance);
        if (value == 0)
        {
            return "<unnamed>";
        }

        var length = Native.string_length(value);
        var characters = Native.string_chars(value);
        return Marshal.PtrToStringUni(characters, length) ?? "<invalid>";
    }

    private static nint RequireMethod(nint klass, string name, int argumentCount)
    {
        var method = Native.class_get_method_from_name(klass, name, argumentCount);
        return method != 0
            ? method
            : throw new InvalidOperationException($"IL2CPP method '{name}/{argumentCount}' was not found.");
    }

    private static nint InvokeReference(nint method, nint instance)
    {
        var result = Native.runtime_invoke(method, instance, 0, out var exception);
        ThrowIfException(exception);
        return result;
    }

    private static unsafe nint InvokeReferenceWithInt32(nint method, nint instance, int value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        var result = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
        return result;
    }

    private static unsafe nint InvokeReferenceWithObject(
        nint method,
        nint instance,
        nint value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = value;
        var result = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
        return result;
    }

    private static unsafe void InvokeVoidWithObjectBool(
        nint method,
        nint instance,
        nint first,
        bool second)
    {
        byte secondValue = second ? (byte)1 : (byte)0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = first;
        arguments[1] = (nint)(&secondValue);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithObject(nint method, nint instance, nint value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = value;
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithInt32(nint method, nint instance, int value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithFloat(nint method, nint instance, float value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithTwoInt32(
        nint method,
        nint instance,
        int first,
        int second)
    {
        nint* arguments = stackalloc nint[2];
        arguments[0] = (nint)(&first);
        arguments[1] = (nint)(&second);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithBool(nint method, nint instance, bool value)
    {
        byte nativeValue = value ? (byte)1 : (byte)0;
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&nativeValue);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithVector2(nint method, nint instance, Vector2 value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithVector3(nint method, nint instance, Vector3 value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithColor(nint method, nint instance, Color value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static unsafe void InvokeVoidWithQuaternion(nint method, nint instance, Quaternion value)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&value);
        _ = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
    }

    private static Vector3 ReadVector3(nint method, nint instance)
    {
        var boxed = InvokeReference(method, instance);
        return Marshal.PtrToStructure<Vector3>(Native.object_unbox(boxed));
    }

    private static Quaternion ReadQuaternion(nint method, nint instance)
    {
        var boxed = InvokeReference(method, instance);
        return Marshal.PtrToStructure<Quaternion>(Native.object_unbox(boxed));
    }

    private static Color ReadColor(nint method, nint instance)
    {
        var boxed = InvokeReference(method, instance);
        return Marshal.PtrToStructure<Color>(Native.object_unbox(boxed));
    }

    private static int InvokeInt32(nint method, nint instance)
    {
        var boxed = InvokeReference(method, instance);
        return Marshal.ReadInt32(Native.object_unbox(boxed));
    }

    private static bool InvokeBoolean(nint method, nint instance)
    {
        var boxed = InvokeReference(method, instance);
        return Marshal.ReadByte(Native.object_unbox(boxed)) != 0;
    }

    private static unsafe bool InvokeBooleanWithTwoObjects(
        nint method,
        nint instance,
        nint first,
        nint second)
    {
        nint* arguments = stackalloc nint[2];
        arguments[0] = first;
        arguments[1] = second;
        var boxed = Native.runtime_invoke(method, instance, (nint)arguments, out var exception);
        ThrowIfException(exception);
        return boxed != 0 && Marshal.ReadByte(Native.object_unbox(boxed)) != 0;
    }

    private static unsafe nint InvokeSpriteCreate(
        nint method,
        nint texture,
        Rect rect,
        Vector2 pivot,
        float pixelsPerUnit)
    {
        nint* arguments = stackalloc nint[4];
        arguments[0] = texture;
        arguments[1] = (nint)(&rect);
        arguments[2] = (nint)(&pivot);
        arguments[3] = (nint)(&pixelsPerUnit);
        var sprite = Native.runtime_invoke(method, 0, (nint)arguments, out var exception);
        ThrowIfException(exception);
        return sprite;
    }

    private static void ThrowIfException(nint exception)
    {
        if (exception != 0)
        {
            throw new InvalidOperationException($"Unity invocation raised IL2CPP exception 0x{exception:X}.");
        }
    }

    private static partial class Native
    {
        private const string GameAssembly = "GameAssembly.dll";

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_from_name(nint image, string namespaze, string name);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_method_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_get_method_from_name(nint klass, string name, int argumentCount);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_field_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_get_field_from_name(nint klass, string name);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_offset")]
        internal static partial int field_get_offset(nint field);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_parent")]
        internal static partial nint class_get_parent(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_type")]
        internal static partial nint class_get_type(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_type_get_object")]
        internal static partial nint type_get_object(nint type);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_runtime_invoke")]
        internal static partial nint runtime_invoke(
            nint method,
            nint instance,
            nint parameters,
            out nint exception);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_new")]
        internal static partial nint object_new(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_array_new")]
        internal static partial nint array_new(nint elementClass, nuint length);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_unbox")]
        internal static partial nint object_unbox(nint instance);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_length")]
        internal static partial int string_length(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_chars")]
        internal static partial nint string_chars(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_new", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint string_new(string value);

        [LibraryImport("user32.dll")]
        internal static partial short GetAsyncKeyState(int virtualKey);

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

        internal static bool IsCurrentProcessForeground()
        {
            var window = GetForegroundWindow();
            if (window == 0)
            {
                return false;
            }
            _ = GetWindowThreadProcessId(window, out var processId);
            return processId == (uint)Environment.ProcessId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vector2(float x, float y)
    {
        public float X = x;
        public float Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(float x, float y, float width, float height)
    {
        public float X = x;
        public float Y = y;
        public float Width = width;
        public float Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Color(float red, float green, float blue, float alpha)
    {
        public float Red = red;
        public float Green = green;
        public float Blue = blue;
        public float Alpha = alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vector3(float x, float y, float z)
    {
        public float X = x;
        public float Y = y;
        public float Z = z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Quaternion(float x, float y, float z, float w)
    {
        public float X = x;
        public float Y = y;
        public float Z = z;
        public float W = w;
    }

    private sealed class FakeMod(
        nint gameObject,
        string name,
        string version,
        bool enabled)
    {
        public nint GameObject { get; } = gameObject;
        public string Name { get; } = name;
        public string Version { get; } = version;
        public bool Enabled { get; set; } = enabled;
    }

    private sealed record ThumbnailTarget(
        nint GameObject,
        nint Image,
        nint DefaultSprite,
        Color DefaultColor);

    private enum ModsView
    {
        Installed,
        Browse,
        Settings
    }

    private sealed class ModMenuButton : IMenuButton
    {
        private readonly nint _gameObject;
        private readonly int _generation;
        private readonly Action _onRemove;
        private string _label;
        private bool _visible = true;
        private bool _removed;

        public ModMenuButton(
            string id,
            nint gameObject,
            nint button,
            string label,
            int generation,
            Action onRemove)
        {
            Id = id;
            _gameObject = gameObject;
            Button = button;
            _label = label;
            _generation = generation;
            _onRemove = onRemove;
        }

        public string Id { get; }
        public nint Button { get; }
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Label
        {
            get => _label;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                SetLabel(_gameObject, value);
                _label = value;
            }
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                EnsureAlive();
                SetActive(_gameObject, value);
                _visible = value;
            }
        }

        public void Remove()
        {
            if (_removed) return;
            var alive = IsAlive;
            _removed = true;
            _onRemove();
            if (alive) DestroyForSdk(_gameObject);
        }

        public void Dispose() => Remove();

        public void RefreshLabel()
        {
            if (IsAlive)
            {
                SetLabel(_gameObject, _label);
            }
        }

        private void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or button is no longer alive.");
            }
        }
    }

    private sealed class ModMenuText : IMenuText
    {
        private readonly nint _gameObject;
        private readonly int _generation;
        private readonly Action _onRemove;
        private string _text;
        private bool _visible = true;
        private bool _removed;

        public ModMenuText(
            string id,
            nint gameObject,
            string text,
            int generation,
            Action onRemove)
        {
            Id = id;
            _gameObject = gameObject;
            _text = text;
            _generation = generation;
            _onRemove = onRemove;
        }

        public string Id { get; }
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Text
        {
            get => _text;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                SetLabel(_gameObject, value);
                _text = value;
            }
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                EnsureAlive();
                SetActive(_gameObject, value);
                _visible = value;
            }
        }

        public void Remove()
        {
            if (_removed) return;
            var alive = IsAlive;
            _removed = true;
            _onRemove();
            if (alive) DestroyForSdk(_gameObject);
        }

        public void Dispose() => Remove();

        public void RefreshLabel()
        {
            if (IsAlive)
            {
                SetLabel(_gameObject, _text);
            }
        }

        private void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or text is no longer alive.");
            }
        }
    }

    private sealed class ModMenuToggle : IMenuToggle
    {
        private readonly nint _gameObject;
        private readonly string _onText;
        private readonly string _offText;
        private readonly Action<bool>? _onChanged;
        private readonly int _generation;
        private readonly Action _onRemove;
        private string _label;
        private bool _value;
        private bool _visible = true;
        private bool _removed;

        public ModMenuToggle(
            string id,
            nint gameObject,
            string label,
            bool value,
            string onText,
            string offText,
            Action<bool>? onChanged,
            int generation,
            Action onRemove)
        {
            Id = id;
            _gameObject = gameObject;
            _label = label;
            _value = value;
            _onText = onText;
            _offText = offText;
            _onChanged = onChanged;
            _generation = generation;
            _onRemove = onRemove;
            RefreshLabel();
        }

        public string Id { get; }
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Label
        {
            get => _label;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                _label = value;
                RefreshLabel();
            }
        }

        public bool Value
        {
            get => _value;
            set => SetValue(value, notify: true);
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                EnsureAlive();
                SetActive(_gameObject, value);
                _visible = value;
            }
        }

        public void Toggle() => Value = !Value;

        public void Remove()
        {
            if (_removed) return;
            var alive = IsAlive;
            _removed = true;
            _onRemove();
            if (alive) DestroyForSdk(_gameObject);
        }

        public void Dispose() => Remove();

        private void SetValue(bool value, bool notify)
        {
            EnsureAlive();
            if (_value == value) return;
            _value = value;
            RefreshLabel();
            if (notify && _onChanged is not null)
            {
                InvokeExternalControlCallback(() => _onChanged(value), Id);
            }
        }

        public void RefreshLabel() =>
            SetLabel(_gameObject, $"{_label}: {(_value ? _onText : _offText)}");

        private void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or toggle is no longer alive.");
            }
        }
    }

    private sealed class ModMenuChoice : IMenuChoice
    {
        private readonly nint _gameObject;
        private readonly IReadOnlyList<string> _options;
        private readonly Action<int, string>? _onChanged;
        private readonly int _generation;
        private readonly Action _onRemove;
        private string _label;
        private int _selectedIndex;
        private bool _visible = true;
        private bool _removed;

        public ModMenuChoice(
            string id,
            nint gameObject,
            string label,
            string[] options,
            int selectedIndex,
            Action<int, string>? onChanged,
            int generation,
            Action onRemove)
        {
            Id = id;
            _gameObject = gameObject;
            _label = label;
            _options = Array.AsReadOnly(options);
            _selectedIndex = selectedIndex;
            _onChanged = onChanged;
            _generation = generation;
            _onRemove = onRemove;
            RefreshLabel();
        }

        public string Id { get; }
        public IReadOnlyList<string> Options => _options;
        public string SelectedValue => _options[_selectedIndex];
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Label
        {
            get => _label;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                _label = value;
                RefreshLabel();
            }
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                EnsureAlive();
                if ((uint)value >= (uint)_options.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                RefreshLabel();
                if (_onChanged is not null)
                {
                    InvokeExternalControlCallback(
                        () => _onChanged(_selectedIndex, SelectedValue),
                        Id);
                }
            }
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                EnsureAlive();
                SetActive(_gameObject, value);
                _visible = value;
            }
        }

        public void SelectNext() => SelectedIndex = (_selectedIndex + 1) % _options.Count;

        public void SelectPrevious() =>
            SelectedIndex = (_selectedIndex + _options.Count - 1) % _options.Count;

        public void Remove()
        {
            if (_removed) return;
            var alive = IsAlive;
            _removed = true;
            _onRemove();
            if (alive) DestroyForSdk(_gameObject);
        }

        public void Dispose() => Remove();

        public void RefreshLabel() =>
            SetLabel(
                _gameObject,
                $"{_label}: {SelectedValue}  [{_selectedIndex + 1}/{_options.Count}]");

        private void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or choice is no longer alive.");
            }
        }
    }

    private sealed class ModMenuInput : IMenuInput
    {
        private readonly nint _gameObject;
        private readonly Action<string>? _onChanged;
        private readonly Action<string>? _onSubmitted;
        private readonly int _generation;
        private readonly Action _onRemove;
        private string _label;
        private string _value;
        private string _placeholder;
        private bool _focused;
        private bool _visible = true;
        private bool _removed;

        public ModMenuInput(
            ModMenuPanel panel,
            string id,
            nint gameObject,
            string label,
            string value,
            string placeholder,
            int maxLength,
            Action<string>? onChanged,
            Action<string>? onSubmitted,
            int generation,
            Action onRemove)
        {
            Panel = panel;
            Id = id;
            _gameObject = gameObject;
            _label = label;
            _value = value;
            _placeholder = placeholder;
            MaxLength = maxLength;
            _onChanged = onChanged;
            _onSubmitted = onSubmitted;
            _generation = generation;
            _onRemove = onRemove;
            RefreshLabel();
        }

        public ModMenuPanel Panel { get; }
        public string Id { get; }
        public int MaxLength { get; }
        public bool IsFocused => _focused;
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Label
        {
            get => _label;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                _label = value;
                RefreshLabel();
            }
        }

        public string Value
        {
            get => _value;
            set => SetValue(value, notify: true);
        }

        public string Placeholder
        {
            get => _placeholder;
            set
            {
                EnsureAlive();
                ArgumentNullException.ThrowIfNull(value);
                ValidateDisplayText(value, nameof(value));
                _placeholder = value;
                RefreshLabel();
            }
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                EnsureAlive();
                SetActive(_gameObject, value);
                _visible = value;
                if (!value && IsFocused) Blur();
            }
        }

        public void Focus()
        {
            EnsureAlive();
            if (!Visible || !Panel.Visible)
            {
                throw new InvalidOperationException("The menu input can only be focused while visible.");
            }
            SetExternalInputFocus(this);
        }

        public void Blur()
        {
            if (ReferenceEquals(_focusedExternalInput, this))
            {
                SetExternalInputFocus(null);
            }
            else if (_focused)
            {
                SetFocused(false);
            }
        }

        public void Submit()
        {
            EnsureAlive();
            if (_onSubmitted is not null)
            {
                InvokeExternalControlCallback(() => _onSubmitted(_value), Id);
            }
        }

        public void Remove()
        {
            if (_removed) return;
            if (IsFocused) Blur();
            var alive = IsAlive;
            _removed = true;
            _onRemove();
            if (alive) DestroyForSdk(_gameObject);
        }

        public void Dispose() => Remove();

        public void SetFocused(bool focused)
        {
            _focused = focused;
            if (IsAlive) RefreshLabel();
        }

        public void SetValueFromKeyboard(string value) => SetValue(value, notify: true);

        private void SetValue(string value, bool notify)
        {
            EnsureAlive();
            ArgumentNullException.ThrowIfNull(value);
            ValidateDisplayText(value, nameof(value));
            if (value.Length > MaxLength)
            {
                throw new ArgumentException($"Menu input cannot exceed {MaxLength} characters.", nameof(value));
            }
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            RefreshLabel();
            if (notify && _onChanged is not null)
            {
                InvokeExternalControlCallback(() => _onChanged(value), Id);
            }
        }

        public void RefreshLabel()
        {
            var display = _value.Length > 0 ? _value : _placeholder;
            SetLabel(_gameObject, $"{_label}: {display}{(_focused ? "_" : string.Empty)}");
        }

        private static void ValidateDisplayText(string value, string parameterName)
        {
            if (value.Any(character => character is '\r' or '\n' || char.IsControl(character)))
            {
                throw new ArgumentException("Menu input cannot contain control characters.", parameterName);
            }
        }

        private void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or input is no longer alive.");
            }
        }
    }

    private sealed class ModMenuPanel : IMenuPanel
    {
        private const int MaximumRows = 6;
        private readonly nint _titleObject;
        private readonly nint _closeButton;
        private readonly int _generation;
        private readonly Action _onRemove;
        private readonly Dictionary<string, IDisposable> _controls =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly bool[] _occupiedRows = new bool[MaximumRows];
        private string _title;
        private bool _visible;
        private bool _removed;

        public ModMenuPanel(
            string ownerId,
            string id,
            nint gameObject,
            nint titleObject,
            nint closeButton,
            string title,
            int generation,
            Action onRemove)
        {
            OwnerId = ownerId;
            Id = id;
            GameObject = gameObject;
            _titleObject = titleObject;
            _closeButton = closeButton;
            _title = title;
            _generation = generation;
            _onRemove = onRemove;
        }

        public string OwnerId { get; }
        public string Id { get; }
        public nint GameObject { get; }
        public bool Visible => _visible;
        public bool IsAlive => !_removed && IsSceneHandleAlive(_generation);

        public string Title
        {
            get => _title;
            set
            {
                EnsureAlive();
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                SetLabel(_titleObject, value);
                _title = value;
            }
        }

        public IMenuButton AddButton(MenuPanelButtonDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Label);
            ArgumentNullException.ThrowIfNull(definition.OnPressed);
            var row = AllocateRow(definition.Id);
            var gameObject = CreateRow(definition.Label, row);
            var button = GetComponent(gameObject, _buttonClass);
            ExternalButtonActions[button] = GuardRuntimeCallback(
                OwnerId,
                $"menu-panel:{Id}:{definition.Id}:pressed",
                definition.OnPressed);
            ModMenuButton? handle = null;
            handle = new ModMenuButton(
                definition.Id,
                gameObject,
                button,
                definition.Label,
                _generation,
                () =>
                {
                    ExternalButtonActions.Remove(button);
                    ReleaseRow(definition.Id, row);
                });
            _controls.Add(definition.Id, handle);
            return handle;
        }

        public IMenuText AddText(MenuPanelTextDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Text);
            var row = AllocateRow(definition.Id);
            var gameObject = CreateRow(definition.Text, row);
            var button = GetComponent(gameObject, _buttonClass);
            ExternalButtonActions[button] = () => { };
            ModMenuText? handle = null;
            handle = new ModMenuText(
                definition.Id,
                gameObject,
                definition.Text,
                _generation,
                () =>
                {
                    ExternalButtonActions.Remove(button);
                    ReleaseRow(definition.Id, row);
                });
            _controls.Add(definition.Id, handle);
            return handle;
        }

        public IMenuToggle AddToggle(MenuPanelToggleDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Label);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.OnText);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.OffText);
            var row = AllocateRow(definition.Id);
            var gameObject = CreateRow(string.Empty, row);
            var button = GetComponent(gameObject, _buttonClass);
            ModMenuToggle? handle = null;
            handle = new ModMenuToggle(
                definition.Id,
                gameObject,
                definition.Label,
                definition.InitialValue,
                definition.OnText,
                definition.OffText,
                GuardRuntimeCallback(
                    OwnerId,
                    $"menu-panel:{Id}:{definition.Id}:changed",
                    definition.OnChanged),
                _generation,
                () =>
                {
                    ExternalButtonActions.Remove(button);
                    ReleaseRow(definition.Id, row);
                });
            ExternalButtonActions[button] = handle.Toggle;
            _controls.Add(definition.Id, handle);
            return handle;
        }

        public IMenuChoice AddChoice(MenuPanelChoiceDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Label);
            ArgumentNullException.ThrowIfNull(definition.Options);
            var options = definition.Options.ToArray();
            if (options.Length is < 1 or > 32 ||
                options.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("A menu choice requires between 1 and 32 non-empty options.");
            }
            if ((uint)definition.InitialIndex >= (uint)options.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    "The initial choice index must reference an option.");
            }
            var row = AllocateRow(definition.Id);
            var gameObject = CreateRow(string.Empty, row);
            var button = GetComponent(gameObject, _buttonClass);
            ModMenuChoice? handle = null;
            handle = new ModMenuChoice(
                definition.Id,
                gameObject,
                definition.Label,
                options,
                definition.InitialIndex,
                GuardRuntimeCallback(
                    OwnerId,
                    $"menu-panel:{Id}:{definition.Id}:changed",
                    definition.OnChanged),
                _generation,
                () =>
                {
                    ExternalButtonActions.Remove(button);
                    ReleaseRow(definition.Id, row);
                });
            ExternalButtonActions[button] = handle.SelectNext;
            _controls.Add(definition.Id, handle);
            return handle;
        }

        public IMenuInput AddInput(MenuPanelInputDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Label);
            ArgumentNullException.ThrowIfNull(definition.InitialValue);
            ArgumentNullException.ThrowIfNull(definition.Placeholder);
            if (definition.MaxLength is < 1 or > 256)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    "Menu input MaxLength must be between 1 and 256.");
            }
            if (definition.InitialValue.Length > definition.MaxLength)
            {
                throw new ArgumentException("The initial menu input value exceeds MaxLength.");
            }
            var row = AllocateRow(definition.Id);
            var gameObject = CreateRow(string.Empty, row);
            var button = GetComponent(gameObject, _buttonClass);
            ModMenuInput? handle = null;
            handle = new ModMenuInput(
                this,
                definition.Id,
                gameObject,
                definition.Label,
                definition.InitialValue,
                definition.Placeholder,
                definition.MaxLength,
                GuardRuntimeCallback(
                    OwnerId,
                    $"menu-panel:{Id}:{definition.Id}:changed",
                    definition.OnChanged),
                GuardRuntimeCallback(
                    OwnerId,
                    $"menu-panel:{Id}:{definition.Id}:submitted",
                    definition.OnSubmitted),
                _generation,
                () =>
                {
                    ExternalButtonActions.Remove(button);
                    ReleaseRow(definition.Id, row);
                });
            ExternalButtonActions[button] = handle.Focus;
            _controls.Add(definition.Id, handle);
            return handle;
        }

        public bool RemoveControl(string id)
        {
            EnsureAlive();
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!_controls.TryGetValue(id, out var control))
            {
                return false;
            }
            control.Dispose();
            return true;
        }

        public void Clear()
        {
            EnsureAlive();
            DisposeControls();
        }

        public void Show() => ShowExternalPanel(this);
        public void Close() => CloseExternalPanel(this);
        public void SetVisible(bool value) => _visible = value;
        public void RefreshTitle() => SetLabel(_titleObject, _title);

        public void RefreshLabels()
        {
            if (!IsAlive) return;
            RefreshTitle();
            foreach (var control in _controls.Values)
            {
                switch (control)
                {
                    case ModMenuButton button:
                        button.RefreshLabel();
                        break;
                    case ModMenuText text:
                        text.RefreshLabel();
                        break;
                    case ModMenuToggle toggle:
                        toggle.RefreshLabel();
                        break;
                    case ModMenuChoice choice:
                        choice.RefreshLabel();
                        break;
                    case ModMenuInput input:
                        input.RefreshLabel();
                        break;
                }
            }
        }

        public void Remove()
        {
            if (_removed) return;
            if (Visible) Close();
            var alive = IsAlive;
            DisposeControls();
            _removed = true;
            ExternalButtonActions.Remove(_closeButton);
            _onRemove();
            if (alive) DestroyForSdk(GameObject);
        }

        public void Dispose() => Remove();

        public void EnsureAlive()
        {
            if (!IsAlive)
            {
                throw new ObjectDisposedException(Id, "The main-menu scene or panel is no longer alive.");
            }
        }

        private int AllocateRow(string id)
        {
            EnsureAlive();
            ValidateExternalId(id, "panel control");
            if (_controls.ContainsKey(id))
            {
                throw new InvalidOperationException($"Panel control id '{id}' is already registered.");
            }
            var row = Array.FindIndex(_occupiedRows, occupied => !occupied);
            if (row < 0)
            {
                throw new InvalidOperationException($"Panel '{Id}' supports at most {MaximumRows} rows.");
            }
            _occupiedRows[row] = true;
            return row;
        }

        private void ReleaseRow(string id, int row)
        {
            _controls.Remove(id);
            _occupiedRows[row] = false;
        }

        private void DisposeControls()
        {
            foreach (var control in _controls.Values.ToArray()) control.Dispose();
            _controls.Clear();
            Array.Clear(_occupiedRows);
        }

        private nint CreateRow(string label, int row)
        {
            var gameObject = Instantiate(_mainButtonTemplate, GetTransform(GameObject));
            SetObjectName(gameObject, $"OFS Mod Panel Row ({OwnerId}:{Id}:{row})");
            SetRect(
                gameObject,
                new Vector2(0.5f, 0.5f),
                new Vector2(0f, 190f - (row * 92f)),
                new Vector2(760f, 76f));
            SetLabel(gameObject, label);
            SetActive(gameObject, true);
            return gameObject;
        }
    }
}
