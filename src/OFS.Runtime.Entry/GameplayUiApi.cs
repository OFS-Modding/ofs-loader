using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class GameplayUiRuntime
{
    private static GameplayPanel? _activePanel;

    internal static int FactorySceneHandle { get; private set; }

    internal static bool IsSceneActive(int handle) =>
        handle != 0 && handle == FactorySceneHandle;

    internal static bool IsAvailable(IUnsafeIl2CppApi api) =>
        FactorySceneHandle != 0 &&
        UnityUiRuntime.FindLoadedGameObjectPointer("NotificationComputer", api) != 0 &&
        UnityUiRuntime.FindLoadedGameObjectPointer("Confirmation Panel", api) != 0 &&
        UnityUiRuntime.FindLoadedGameObjectPointer("PanelUI", api) != 0;

    internal static bool IsVanillaPanelOpen(IUnsafeIl2CppApi api)
    {
        var panel = UnityUiRuntime.FindLoadedGameObjectPointer("Confirmation Panel", api);
        return UnityUiRuntime.IsActiveInHierarchyForSdk(panel);
    }

    internal static void NotifySceneLoaded(SceneEvent scene)
    {
        if (string.Equals(scene.Name, "Factory", StringComparison.Ordinal))
        {
            FactorySceneHandle = scene.Handle;
            RuntimeLog.Write($"Gameplay UI templates armed for Factory scene {scene.Handle}.");
        }
    }

    internal static void NotifySceneUnloaded(SceneEvent scene)
    {
        if (scene.Handle != FactorySceneHandle) return;
        _activePanel?.Close(GameplayPanelCloseReason.SceneUnloaded);
        _activePanel = null;
        UnityUiRuntime.SetGameplayPanelOpen(false);
        FactorySceneHandle = 0;
        RuntimeLog.Write($"Gameplay UI handles invalidated for Factory scene {scene.Handle}.");
    }

    internal static void Open(GameplayPanel panel)
    {
        if (UnityUiRuntime.IsDialogueOpen)
        {
            throw new InvalidOperationException(
                "A gameplay panel cannot open while a framework dialogue is active.");
        }
        if (ReferenceEquals(_activePanel, panel))
        {
            if (!panel.Visible) panel.OpenCore();
            return;
        }

        _activePanel?.Close(GameplayPanelCloseReason.Replaced);
        _activePanel = panel;
        try
        {
            panel.OpenCore();
        }
        catch
        {
            if (ReferenceEquals(_activePanel, panel)) _activePanel = null;
            UnityUiRuntime.SetGameplayPanelOpen(false);
            throw;
        }
    }

    internal static void Closed(GameplayPanel panel)
    {
        if (ReferenceEquals(_activePanel, panel))
        {
            _activePanel = null;
            UnityUiRuntime.SetGameplayPanelOpen(false);
        }
    }

    internal static void CancelActive() =>
        _activePanel?.Close(GameplayPanelCloseReason.UserCancelled);
}

internal sealed class GameplayUiApi : IGameplayUiApi
{
    private readonly string _ownerId;
    private readonly IUnsafeIl2CppApi _api;
    private readonly IModLogger _logger;
    private readonly Dictionary<string, GameplayHud> _huds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameplayPanel> _panels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComputerAppRegistration> _computerApps =
        new(StringComparer.OrdinalIgnoreCase);
    private int _labelRefreshCountdown;

    internal GameplayUiApi(
        string ownerId,
        IUnsafeIl2CppApi api,
        IModEvents events,
        IModLogger logger)
    {
        _ownerId = ownerId;
        _api = api;
        _logger = logger;
        events.SceneUnloaded += OnSceneUnloaded;
        events.FrameUpdate += OnFrameUpdate;
    }

    public bool IsAvailable =>
        ModRuntime.IsMainThread && GameplayUiRuntime.IsAvailable(_api);

    public IGameplayHud CreateHud(GameplayHudDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        GameplayUiValidation.ValidateId(definition.Id, "HUD");
        GameplayUiValidation.ValidateText(definition.Title, "HUD title", 200, allowEmpty: false);
        GameplayUiValidation.ValidateText(definition.Text, "HUD text", 2000, allowEmpty: true);
        GameplayUiValidation.ValidateAnchor(definition.Anchor);
        GameplayUiValidation.ValidateOffset(definition.OffsetX, nameof(definition.OffsetX));
        GameplayUiValidation.ValidateOffset(definition.OffsetY, nameof(definition.OffsetY));
        EnsureAvailable();
        if (_huds.ContainsKey(definition.Id))
            throw new InvalidOperationException($"HUD id '{definition.Id}' is already registered.");

        var handle = GameplayHud.Create(
            _ownerId,
            definition,
            _api,
            GameplayUiRuntime.FactorySceneHandle,
            () => _huds.Remove(definition.Id));
        _huds.Add(definition.Id, handle);
        _logger.Info(
            $"Created gameplay HUD '{_ownerId}:{definition.Id}' at {definition.Anchor}, " +
            $"visible={definition.Visible}.");
        return handle;
    }

    public IGameplayPanel CreatePanel(GameplayPanelDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        GameplayUiValidation.ValidateId(definition.Id, "Panel");
        GameplayUiValidation.ValidateText(definition.Title, "Panel title", 200, allowEmpty: false);
        GameplayUiValidation.ValidateText(definition.Body, "Panel body", 4000, allowEmpty: true);
        EnsureAvailable();
        if (_panels.ContainsKey(definition.Id))
            throw new InvalidOperationException($"Gameplay panel id '{definition.Id}' is already registered.");

        var handle = GameplayPanel.Create(
            _ownerId,
            definition,
            _api,
            _logger,
            GameplayUiRuntime.FactorySceneHandle,
            () => _panels.Remove(definition.Id));
        _panels.Add(definition.Id, handle);
        _logger.Info($"Created gameplay panel '{_ownerId}:{definition.Id}'.");
        return handle;
    }

    public IComputerAppRegistration RegisterComputerApp(ComputerAppDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        GameplayUiValidation.ValidateId(definition.Id, "Computer app");
        GameplayUiValidation.ValidateText(
            definition.Label, "Computer app label", 100, allowEmpty: false);
        ArgumentNullException.ThrowIfNull(definition.OnPressed);
        if (_computerApps.ContainsKey(definition.Id))
            throw new InvalidOperationException(
                $"Computer app id '{definition.Id}' is already registered.");
        var handle = new ComputerAppRegistration(
            _ownerId,
            definition,
            _api,
            GameplayUiRuntime.FactorySceneHandle,
            () => _computerApps.Remove(definition.Id));
        _computerApps.Add(definition.Id, handle);
        handle.TryMaterialize();
        _logger.Info($"Registered factory computer app '{_ownerId}:{definition.Id}'.");
        return handle;
    }

    internal void RemoveAll()
    {
        foreach (var panel in _panels.Values.ToArray()) panel.Remove();
        foreach (var hud in _huds.Values.ToArray()) hud.Remove();
        foreach (var app in _computerApps.Values.ToArray()) app.Remove();
        _panels.Clear();
        _huds.Clear();
        _computerApps.Clear();
    }

    private void OnSceneUnloaded(SceneEvent scene)
    {
        foreach (var panel in _panels.Values
                     .Where(value => value.SceneHandle == scene.Handle)
                     .ToArray())
            panel.Remove();
        foreach (var hud in _huds.Values
                     .Where(value => value.SceneHandle == scene.Handle)
                     .ToArray())
            hud.Remove();
        foreach (var app in _computerApps.Values
                     .Where(value => value.SceneHandle == scene.Handle)
                     .ToArray())
            app.Remove();
    }

    private void OnFrameUpdate(FrameEvent _)
    {
        if (--_labelRefreshCountdown > 0) return;
        _labelRefreshCountdown = 15;
        foreach (var hud in _huds.Values) hud.RefreshLabels();
        foreach (var panel in _panels.Values) panel.RefreshLabels();
        foreach (var app in _computerApps.Values)
        {
            app.TryMaterialize();
            app.Refresh();
        }
    }

    private void EnsureAvailable()
    {
        if (!GameplayUiRuntime.IsAvailable(_api))
            throw new InvalidOperationException(
                "Gameplay UI is only available while the Factory scene and its vanilla templates are loaded.");
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Gameplay UI must be used on Unity's main thread.");
    }
}

internal sealed class ComputerAppRegistration : IComputerAppRegistration
{
    private readonly IUnsafeIl2CppApi _api;
    private readonly Action _onPressed;
    private readonly Action _onRemove;
    private nint _root;
    private nint _button;
    private string _label;
    private bool _visible;
    private bool _removed;

    internal ComputerAppRegistration(
        string ownerId,
        ComputerAppDefinition definition,
        IUnsafeIl2CppApi api,
        int sceneHandle,
        Action onRemove)
    {
        OwnerId = ownerId;
        Id = definition.Id;
        _label = definition.Label;
        _visible = definition.Visible;
        _onPressed = definition.OnPressed;
        _api = api;
        SceneHandle = sceneHandle;
        _onRemove = onRemove;
    }

    internal int SceneHandle { get; }
    public string Id { get; }
    public string OwnerId { get; }
    public bool IsMaterialized => _root != 0;
    public bool IsAlive => !_removed && GameplayUiRuntime.IsSceneActive(SceneHandle);

    public string Label
    {
        get => _label;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "Computer app label", 100, allowEmpty: false);
            _label = value;
            Refresh();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            EnsureAlive();
            _visible = value;
            Refresh();
        }
    }

    internal void TryMaterialize()
    {
        if (!IsAlive || _root != 0) return;
        var controllerClass = _api.FindClass(
            "Assembly-CSharp.dll", string.Empty, "ComputerAppButtonController");
        if (controllerClass == 0) return;
        var templateController = UnityUiRuntime.FindActiveLoadedComponentPointer(
            controllerClass, _api);
        if (templateController == 0) return;
        var templateRoot = UnityUiRuntime.GetGameObjectForSdk(templateController);
        if (templateRoot == 0) return;

        var clone = UnityUiRuntime.CloneSiblingPointer(templateRoot);
        try
        {
            var cloneController = UnityUiRuntime.TryGetComponentPointer(clone, controllerClass);
            if (cloneController == 0)
                throw new InvalidOperationException(
                    "The cloned computer app button has no controller component.");
            var buttonField = _api.FindField(controllerClass, "button");
            var button = buttonField == 0
                ? 0
                : _api.ReadObjectReference(cloneController, buttonField);
            if (button == 0) button = UnityUiRuntime.GetButtonPointer(clone);
            _root = clone;
            _button = button;
            UnityUiRuntime.SetObjectNameForSdk(
                clone, $"OFS Computer App ({OwnerId}:{Id})");
            UnityUiRuntime.RegisterFrameworkButton(_button, _onPressed);
            Refresh();
        }
        catch
        {
            UnityUiRuntime.DestroyForSdk(clone);
            throw;
        }
    }

    internal void Refresh()
    {
        if (!IsAlive || _root == 0) return;
        UnityUiRuntime.SetLabelForSdk(_root, _label);
        UnityUiRuntime.SetActiveForSdk(_root, _visible);
    }

    public void Remove()
    {
        if (_removed) return;
        var alive = IsAlive;
        _removed = true;
        _onRemove();
        if (_button != 0) UnityUiRuntime.UnregisterFrameworkButton(_button);
        if (alive && _root != 0) UnityUiRuntime.DestroyForSdk(_root);
        _button = 0;
        _root = 0;
    }

    public void Dispose() => Remove();

    private void EnsureAlive()
    {
        if (!IsAlive)
            throw new ObjectDisposedException(Id, "The factory computer app is no longer alive.");
    }
}

internal static class GameplayUiValidation
{
    internal static void ValidateId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException(
                $"{kind} id must use 1-100 ASCII letters, digits, dots, dashes or underscores.");
    }

    internal static void ValidateText(string value, string kind, int maximum, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum ||
            value.Any(character => character is '\r' or '\n' && kind.Contains("title", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{kind} must contain at most {maximum} valid characters.");
    }

    internal static void ValidateAnchor(GameplayUiAnchor anchor)
    {
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
    }

    internal static void ValidateOffset(float value, string parameterName)
    {
        if (!float.IsFinite(value) || MathF.Abs(value) > 2000f)
            throw new ArgumentOutOfRangeException(parameterName, "HUD offsets must be finite and within +/-2000.");
    }
}

internal sealed class GameplayHud : IGameplayHud
{
    private readonly nint _root;
    private readonly nint _titleObject;
    private readonly nint _textObject;
    private readonly Action _onRemove;
    private string _title;
    private string _text;
    private GameplayUiAnchor _anchor;
    private float _offsetX;
    private float _offsetY;
    private bool _visible;
    private bool _removed;

    private GameplayHud(
        string ownerId,
        string id,
        nint root,
        nint titleObject,
        nint textObject,
        string title,
        string text,
        GameplayUiAnchor anchor,
        float offsetX,
        float offsetY,
        bool visible,
        int sceneHandle,
        Action onRemove)
    {
        OwnerId = ownerId;
        Id = id;
        _root = root;
        _titleObject = titleObject;
        _textObject = textObject;
        _title = title;
        _text = text;
        _anchor = anchor;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _visible = visible;
        SceneHandle = sceneHandle;
        _onRemove = onRemove;
    }

    internal int SceneHandle { get; }
    public string Id { get; }
    public string OwnerId { get; }
    public bool IsAlive => !_removed && GameplayUiRuntime.IsSceneActive(SceneHandle);

    public string Title
    {
        get => _title;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "HUD title", 200, allowEmpty: false);
            _title = value;
            RefreshLabels();
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "HUD text", 2000, allowEmpty: true);
            _text = value;
            RefreshLabels();
        }
    }

    public GameplayUiAnchor Anchor
    {
        get => _anchor;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateAnchor(value);
            _anchor = value;
            Reposition();
        }
    }

    public float OffsetX
    {
        get => _offsetX;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateOffset(value, nameof(value));
            _offsetX = value;
            Reposition();
        }
    }

    public float OffsetY
    {
        get => _offsetY;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateOffset(value, nameof(value));
            _offsetY = value;
            Reposition();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            EnsureAlive();
            UnityUiRuntime.SetActiveForSdk(_root, value);
            _visible = value;
        }
    }

    internal static GameplayHud Create(
        string ownerId,
        GameplayHudDefinition definition,
        IUnsafeIl2CppApi api,
        int sceneHandle,
        Action onRemove)
    {
        var template = UnityUiRuntime.FindLoadedGameObjectPointer("NotificationComputer", api);
        if (template == 0) throw new InvalidOperationException("NotificationComputer template is unavailable.");
        var root = UnityUiRuntime.CloneSiblingPointer(template);
        try
        {
            UnityUiRuntime.SetObjectNameForSdk(root, $"OFS HUD ({ownerId}:{definition.Id})");
            var title = UnityUiRuntime.FindDescendantPointer(root, "Header_Text");
            var text = UnityUiRuntime.FindDescendantPointer(root, "Notification_Text");
            var handle = new GameplayHud(
                ownerId, definition.Id, root, title, text,
                definition.Title, definition.Text, definition.Anchor,
                definition.OffsetX, definition.OffsetY, definition.Visible,
                sceneHandle, onRemove);
            handle.RefreshLabels();
            handle.Reposition();
            UnityUiRuntime.SetActiveForSdk(root, definition.Visible);
            return handle;
        }
        catch
        {
            UnityUiRuntime.DestroyForSdk(root);
            throw;
        }
    }

    public void Show() => Visible = true;
    public void Hide() => Visible = false;

    public void Remove()
    {
        if (_removed) return;
        var alive = IsAlive;
        _removed = true;
        _onRemove();
        if (alive) UnityUiRuntime.DestroyForSdk(_root);
    }

    public void Dispose() => Remove();

    internal void RefreshLabels()
    {
        if (!IsAlive) return;
        UnityUiRuntime.SetTextForSdk(_titleObject, _title);
        UnityUiRuntime.SetTextForSdk(_textObject, _text);
    }

    private void Reposition()
    {
        var (x, y) = _anchor switch
        {
            GameplayUiAnchor.TopLeft => (-700f, 430f),
            GameplayUiAnchor.TopRight => (700f, 430f),
            GameplayUiAnchor.BottomLeft => (-700f, -430f),
            GameplayUiAnchor.BottomRight => (700f, -430f),
            _ => throw new ArgumentOutOfRangeException(nameof(_anchor)),
        };
        UnityUiRuntime.SetRectForSdk(_root, x + _offsetX, y + _offsetY, 535f, 158f);
    }

    private void EnsureAlive()
    {
        if (!IsAlive)
            throw new ObjectDisposedException(Id, "The Factory scene or gameplay HUD is no longer alive.");
    }
}

internal sealed class GameplayPanel : IGameplayPanel
{
    private const int MaximumButtons = 4;
    private readonly nint _root;
    private readonly nint _titleObject;
    private readonly nint _bodyObject;
    private readonly nint _buttonTemplate;
    private readonly nint _closeButton;
    private readonly Action<GameplayPanelClosedEvent>? _closed;
    private readonly IUnsafeIl2CppApi _api;
    private readonly IModLogger _logger;
    private readonly Action _onRemove;
    private readonly Dictionary<string, GameplayPanelButton> _buttons =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool[] _occupiedSlots = new bool[MaximumButtons];
    private DialogueInputGuard? _inputGuard;
    private nint _playerUi;
    private bool _playerUiWasActive;
    private string _title;
    private string _body;
    private bool _visible;
    private bool _removed;

    private GameplayPanel(
        string ownerId,
        string id,
        nint root,
        nint titleObject,
        nint bodyObject,
        nint buttonTemplate,
        nint closeButton,
        string title,
        string body,
        Action<GameplayPanelClosedEvent>? closed,
        IUnsafeIl2CppApi api,
        IModLogger logger,
        int sceneHandle,
        Action onRemove)
    {
        OwnerId = ownerId;
        Id = id;
        _root = root;
        _titleObject = titleObject;
        _bodyObject = bodyObject;
        _buttonTemplate = buttonTemplate;
        _closeButton = closeButton;
        _title = title;
        _body = body;
        _closed = closed;
        _api = api;
        _logger = logger;
        SceneHandle = sceneHandle;
        _onRemove = onRemove;
    }

    internal int SceneHandle { get; }
    public string Id { get; }
    public string OwnerId { get; }
    public bool Visible => _visible;
    public bool IsAlive => !_removed && GameplayUiRuntime.IsSceneActive(SceneHandle);

    public string Title
    {
        get => _title;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "Panel title", 200, allowEmpty: false);
            _title = value;
            RefreshLabels();
        }
    }

    public string Body
    {
        get => _body;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "Panel body", 4000, allowEmpty: true);
            _body = value;
            RefreshLabels();
        }
    }

    internal static GameplayPanel Create(
        string ownerId,
        GameplayPanelDefinition definition,
        IUnsafeIl2CppApi api,
        IModLogger logger,
        int sceneHandle,
        Action onRemove)
    {
        var template = UnityUiRuntime.FindLoadedGameObjectPointer("Confirmation Panel", api);
        var parent = UnityUiRuntime.FindLoadedGameObjectPointer("PanelUI", api);
        if (template == 0 || parent == 0)
            throw new InvalidOperationException("Factory gameplay panel templates are unavailable.");

        var root = UnityUiRuntime.CloneSiblingPointer(template);
        try
        {
            UnityUiRuntime.SetActiveForSdk(root, false);
            UnityUiRuntime.SetParentForSdk(root, parent, worldPositionStays: false);
            UnityUiRuntime.SetObjectNameForSdk(root, $"OFS Gameplay Panel ({ownerId}:{definition.Id})");
            var fireContent = UnityUiRuntime.FindDescendantPointer(root, "Content (Fire)");
            UnityUiRuntime.SetActiveForSdk(fireContent, false);
            var hireContent = UnityUiRuntime.FindDescendantPointer(root, "Content (Hire)");
            UnityUiRuntime.SetActiveForSdk(hireContent, true);
            var title = UnityUiRuntime.FindDescendantPointer(root, "Name_Text");
            var body = UnityUiRuntime.FindDescendantPointer(hireContent, "Desc_Text");
            var buttonTemplate = UnityUiRuntime.FindDescendantPointer(hireContent, "Hire");
            UnityUiRuntime.SetActiveForSdk(buttonTemplate, false);
            var closeObject = UnityUiRuntime.FindDescendantPointer(root, "ButtonClose");
            var closeButton = UnityUiRuntime.GetButtonPointer(closeObject);
            GameplayPanel? handle = null;
            handle = new GameplayPanel(
                ownerId, definition.Id, root, title, body, buttonTemplate, closeButton,
                definition.Title, definition.Body, definition.Closed, api, logger,
                sceneHandle, onRemove);
            UnityUiRuntime.RegisterFrameworkButton(
                closeButton,
                () => handle.Close(GameplayPanelCloseReason.UserCancelled));
            handle.RefreshLabels();
            return handle;
        }
        catch
        {
            UnityUiRuntime.DestroyForSdk(root);
            throw;
        }
    }

    public IGameplayPanelButton AddButton(GameplayPanelButtonDefinition definition)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(definition);
        GameplayUiValidation.ValidateId(definition.Id, "Panel button");
        GameplayUiValidation.ValidateText(definition.Label, "Panel button label", 200, allowEmpty: false);
        ArgumentNullException.ThrowIfNull(definition.OnPressed);
        if (_buttons.ContainsKey(definition.Id))
            throw new InvalidOperationException($"Panel button id '{definition.Id}' is already registered.");
        var slot = Array.FindIndex(_occupiedSlots, value => !value);
        if (slot < 0) throw new InvalidOperationException("A gameplay panel supports at most four buttons.");
        _occupiedSlots[slot] = true;

        nint gameObject = 0;
        try
        {
            gameObject = slot == 0
                ? _buttonTemplate
                : UnityUiRuntime.CloneSiblingPointer(_buttonTemplate);
            UnityUiRuntime.SetObjectNameForSdk(
                gameObject, $"OFS Gameplay Panel Button ({OwnerId}:{Id}:{definition.Id})");
            UnityUiRuntime.SetRectForSdk(gameObject, 0f, -110f - (slot * 72f), 620f, 58f);
            var labelObject = UnityUiRuntime.FindDescendantPointer(gameObject, "Purchase_Text");
            var button = UnityUiRuntime.GetButtonPointer(gameObject);
            GameplayPanelButton? handle = null;
            handle = new GameplayPanelButton(
                this,
                definition.Id,
                gameObject,
                labelObject,
                button,
                definition.Label,
                definition.OnPressed,
                definition.ClosePanel,
                slot,
                slot != 0,
                _logger,
                () => ReleaseButton(definition.Id, slot));
            UnityUiRuntime.RegisterFrameworkButton(button, handle.Invoke);
            UnityUiRuntime.SetActiveForSdk(gameObject, true);
            _buttons.Add(definition.Id, handle);
            return handle;
        }
        catch
        {
            _occupiedSlots[slot] = false;
            if (slot != 0 && gameObject != 0) UnityUiRuntime.DestroyForSdk(gameObject);
            throw;
        }
    }

    public bool RemoveButton(string id)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_buttons.TryGetValue(id, out var button)) return false;
        button.Remove();
        return true;
    }

    public void ClearButtons()
    {
        EnsureAlive();
        foreach (var button in _buttons.Values.ToArray()) button.Remove();
        _buttons.Clear();
        Array.Clear(_occupiedSlots);
    }

    public void Show()
    {
        EnsureAlive();
        GameplayUiRuntime.Open(this);
    }

    internal void OpenCore()
    {
        EnsureAlive();
        if (_visible) return;
        if (GameplayUiRuntime.IsVanillaPanelOpen(_api))
            throw new InvalidOperationException(
                "A gameplay panel cannot open while a vanilla confirmation panel is active.");
        UnityUiRuntime.PrepareGameplayModalInput();
        var guard = DialogueInputGuard.Acquire(_api);
        var playerUi = UnityUiRuntime.FindLoadedGameObjectPointer("PlayerUI", _api);
        var playerUiWasActive = UnityUiRuntime.IsActiveSelfForSdk(playerUi);
        try
        {
            _inputGuard = guard;
            _playerUi = playerUi;
            _playerUiWasActive = playerUiWasActive;
            if (playerUiWasActive) UnityUiRuntime.SetActiveForSdk(playerUi, false);
            RefreshLabels();
            UnityUiRuntime.SetActiveForSdk(_root, true);
            UnityUiRuntime.SetAsLastSiblingForSdk(_root);
            _visible = true;
            UnityUiRuntime.SetGameplayPanelOpen(true);
            _logger.Info($"Opened gameplay panel '{OwnerId}:{Id}'.");
        }
        catch
        {
            _inputGuard = null;
            _playerUi = 0;
            _playerUiWasActive = false;
            if (playerUiWasActive) UnityUiRuntime.SetActiveForSdk(playerUi, true);
            guard.Dispose();
            throw;
        }
    }

    public void Close(GameplayPanelCloseReason reason = GameplayPanelCloseReason.ModRequested)
    {
        if (!_visible) return;
        _visible = false;
        if (IsAlive) UnityUiRuntime.SetActiveForSdk(_root, false);
        try
        {
            _inputGuard?.Dispose();
            if (IsAlive && _playerUi != 0 && _playerUiWasActive)
                UnityUiRuntime.SetActiveForSdk(_playerUi, true);
        }
        finally
        {
            _inputGuard = null;
            _playerUi = 0;
            _playerUiWasActive = false;
        }
        GameplayUiRuntime.Closed(this);
        if (_closed is not null)
        {
            try
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    OwnerId,
                    $"gameplay-panel:{Id}:closed");
                _closed(new GameplayPanelClosedEvent(this, reason));
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Gameplay panel '{Id}' Closed callback failed.");
            }
        }
        _logger.Info($"Closed gameplay panel '{OwnerId}:{Id}' with reason {reason}.");
    }

    public void Remove()
    {
        if (_removed) return;
        if (_visible) Close(GameplayPanelCloseReason.Removed);
        var alive = IsAlive;
        foreach (var button in _buttons.Values.ToArray()) button.Remove();
        _buttons.Clear();
        UnityUiRuntime.UnregisterFrameworkButton(_closeButton);
        _removed = true;
        _onRemove();
        if (alive) UnityUiRuntime.DestroyForSdk(_root);
    }

    public void Dispose() => Remove();

    internal void RefreshLabels()
    {
        if (!IsAlive) return;
        if (_visible)
        {
            _inputGuard?.Maintain();
            UnityUiRuntime.SetAsLastSiblingForSdk(_root);
        }
        UnityUiRuntime.SetTextForSdk(_titleObject, _title);
        UnityUiRuntime.SetTextForSdk(_bodyObject, _body);
        foreach (var button in _buttons.Values) button.RefreshLabel();
    }

    private void ReleaseButton(string id, int slot)
    {
        _buttons.Remove(id);
        _occupiedSlots[slot] = false;
    }

    private void EnsureAlive()
    {
        if (!IsAlive)
            throw new ObjectDisposedException(Id, "The Factory scene or gameplay panel is no longer alive.");
    }
}

internal sealed class GameplayPanelButton : IGameplayPanelButton
{
    private readonly GameplayPanel _panel;
    private readonly nint _gameObject;
    private readonly nint _labelObject;
    private readonly nint _button;
    private readonly Action _onPressed;
    private readonly bool _closePanel;
    private readonly bool _isClone;
    private readonly IModLogger _logger;
    private readonly Action _onRemove;
    private string _label;
    private bool _visible = true;
    private bool _removed;

    internal GameplayPanelButton(
        GameplayPanel panel,
        string id,
        nint gameObject,
        nint labelObject,
        nint button,
        string label,
        Action onPressed,
        bool closePanel,
        int slot,
        bool isClone,
        IModLogger logger,
        Action onRemove)
    {
        _panel = panel;
        Id = id;
        _gameObject = gameObject;
        _labelObject = labelObject;
        _button = button;
        _label = label;
        _onPressed = onPressed;
        _closePanel = closePanel;
        Slot = slot;
        _isClone = isClone;
        _logger = logger;
        _onRemove = onRemove;
        RefreshLabel();
    }

    internal int Slot { get; }
    public string Id { get; }
    public bool IsAlive => !_removed && _panel.IsAlive;

    public string Label
    {
        get => _label;
        set
        {
            EnsureAlive();
            GameplayUiValidation.ValidateText(value, "Panel button label", 200, allowEmpty: false);
            _label = value;
            RefreshLabel();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            EnsureAlive();
            UnityUiRuntime.SetActiveForSdk(_gameObject, value);
            _visible = value;
        }
    }

    internal void Invoke()
    {
        if (!IsAlive || !_visible) return;
        try
        {
            using var callback = ModSafetyStore.EnterRuntimeCallback(
                _panel.OwnerId,
                $"gameplay-panel:{_panel.Id}:{Id}:pressed");
            _onPressed();
            if (_closePanel && _panel.Visible)
                _panel.Close(GameplayPanelCloseReason.Button);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Gameplay panel button '{Id}' callback failed.");
        }
    }

    public void Remove()
    {
        if (_removed) return;
        var alive = IsAlive;
        _removed = true;
        UnityUiRuntime.UnregisterFrameworkButton(_button);
        _onRemove();
        if (alive)
        {
            if (_isClone) UnityUiRuntime.DestroyForSdk(_gameObject);
            else UnityUiRuntime.SetActiveForSdk(_gameObject, false);
        }
    }

    public void Dispose() => Remove();

    internal void RefreshLabel()
    {
        if (IsAlive) UnityUiRuntime.SetTextForSdk(_labelObject, _label);
    }

    private void EnsureAlive()
    {
        if (!IsAlive)
            throw new ObjectDisposedException(Id, "The gameplay panel or button is no longer alive.");
    }
}
