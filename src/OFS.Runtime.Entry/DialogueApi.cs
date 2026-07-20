using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class DialogueRuntime
{
    private static DialogueSession? _active;

    internal static void Open(DialogueSession session)
    {
        _active?.Close(DialogueCloseReason.Replaced);
        _active = session;
        try { session.OpenUi(); }
        catch
        {
            if (ReferenceEquals(_active, session)) _active = null;
            throw;
        }
    }

    internal static void Closed(DialogueSession session)
    {
        if (ReferenceEquals(_active, session)) _active = null;
    }

    internal static void CancelActive() => _active?.Close(DialogueCloseReason.Cancelled);
}

internal sealed class DialogueApi : IDialogueApi
{
    private readonly string _ownerId;
    private readonly IUnsafeIl2CppApi _api;
    private readonly ILocalizationApi _localization;
    private readonly IModLogger _logger;
    private readonly List<DialogueSession> _sessions = [];

    public DialogueApi(
        string ownerId,
        IUnsafeIl2CppApi api,
        ILocalizationApi localization,
        IModEvents events,
        IModLogger logger)
    {
        _ownerId = ownerId;
        _api = api;
        _localization = localization;
        _logger = logger;
        events.SceneUnloaded += _ => CloseAll(DialogueCloseReason.SceneUnloaded);
    }

    public bool IsUiAvailable =>
        ModRuntime.IsMainThread &&
        UnityUiRuntime.FindLoadedGameObjectPointer("Confirmation Panel", _api) != 0;

    public IDialogueSession Open(DialogueDefinition definition)
    {
        EnsureMainThread();
        var graph = DialogueGraph.Validate(definition);
        var session = new DialogueSession(
            this, _ownerId, graph, _api, _localization, _logger);
        DialogueRuntime.Open(session);
        _sessions.Add(session);
        _logger.Info($"Opened dialogue '{_ownerId}:{definition.Id}' at node '{definition.StartNodeId}'.");
        return session;
    }

    internal void Closed(DialogueSession session)
    {
        DialogueRuntime.Closed(session);
        _sessions.Remove(session);
    }

    internal void CloseAll(DialogueCloseReason reason)
    {
        foreach (var session in _sessions.ToArray()) session.Close(reason);
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Dialogues must be opened on Unity's main thread.");
    }
}

internal sealed class DialogueGraph
{
    private DialogueGraph(DialogueDefinition definition, Dictionary<string, DialogueNodeDefinition> nodes)
    {
        Definition = definition;
        Nodes = nodes;
    }

    internal DialogueDefinition Definition { get; }
    internal IReadOnlyDictionary<string, DialogueNodeDefinition> Nodes { get; }

    internal static DialogueGraph Validate(DialogueDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateId(definition.Id, "Dialogue");
        ValidateId(definition.StartNodeId, "Start node");
        ArgumentNullException.ThrowIfNull(definition.Nodes);
        if (definition.Nodes.Count is < 1 or > 256)
            throw new ArgumentException("A dialogue needs 1-256 nodes.", nameof(definition));

        var nodes = new Dictionary<string, DialogueNodeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            ValidateId(node.Id, "Node");
            ValidateText(node.Speaker, "speaker");
            ValidateText(node.Body, "body");
            ArgumentNullException.ThrowIfNull(node.Choices);
            if (node.Choices.Count is < 1 or > 4)
                throw new ArgumentException($"Dialogue node '{node.Id}' needs 1-4 choices.");
            if (!nodes.TryAdd(node.Id, node))
                throw new ArgumentException($"Dialogue node id '{node.Id}' is duplicated.");

            var choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in node.Choices)
            {
                ArgumentNullException.ThrowIfNull(choice);
                ValidateId(choice.Id, "Choice");
                ValidateText(choice.Label, "choice label");
                if (!choices.Add(choice.Id))
                    throw new ArgumentException($"Choice id '{choice.Id}' is duplicated in node '{node.Id}'.");
                if (!choice.Close && string.IsNullOrWhiteSpace(choice.NextNodeId))
                    throw new ArgumentException(
                        $"Choice '{node.Id}:{choice.Id}' must close or select a next node.");
            }
        }
        if (!nodes.ContainsKey(definition.StartNodeId))
            throw new ArgumentException($"Start node '{definition.StartNodeId}' does not exist.");
        foreach (var node in nodes.Values)
            foreach (var choice in node.Choices)
                if (!choice.Close && !nodes.ContainsKey(choice.NextNodeId!))
                    throw new ArgumentException(
                        $"Choice '{node.Id}:{choice.Id}' targets missing node '{choice.NextNodeId}'.");
        return new DialogueGraph(definition, nodes);
    }

    private static void ValidateText(DialogueText text, string field)
    {
        if (string.IsNullOrWhiteSpace(text.Value))
            throw new ArgumentException($"Dialogue {field} cannot be empty.");
    }

    private static void ValidateId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException(
                $"{kind} id must use 1-100 ASCII letters, digits, dots, dashes or underscores.");
    }
}

internal sealed class DialogueSession : IDialogueSession
{
    private readonly DialogueApi _owner;
    private readonly DialogueGraph _graph;
    private readonly IUnsafeIl2CppApi _api;
    private readonly ILocalizationApi _localization;
    private readonly IModLogger _logger;
    private DialogueUi? _ui;
    private bool _closedCallbackRaised;

    internal DialogueSession(
        DialogueApi owner,
        string ownerId,
        DialogueGraph graph,
        IUnsafeIl2CppApi api,
        ILocalizationApi localization,
        IModLogger logger)
    {
        _owner = owner;
        OwnerId = ownerId;
        _graph = graph;
        _api = api;
        _localization = localization;
        _logger = logger;
        CurrentNodeId = graph.Definition.StartNodeId;
    }

    public string Id => _graph.Definition.Id;
    public string OwnerId { get; }
    public string CurrentNodeId { get; private set; }
    public bool IsOpen { get; private set; }

    internal void OpenUi()
    {
        _ui = DialogueUi.Create(_api, choice => Choose(choice), () => Close(DialogueCloseReason.Cancelled));
        IsOpen = true;
        UnityUiRuntime.SetDialogueOpen(true);
        try { ShowNode(CurrentNodeId); }
        catch { Close(DialogueCloseReason.Error); throw; }
    }

    public void ShowNode(string nodeId)
    {
        EnsureOpen();
        if (!_graph.Nodes.TryGetValue(nodeId, out var node))
            throw new KeyNotFoundException($"Dialogue '{Id}' has no node '{nodeId}'.");
        CurrentNodeId = node.Id;
        _ui!.Render(
            Resolve(node.Speaker),
            Resolve(node.Body),
            node.Choices.Select(choice => (choice.Id, Resolve(choice.Label))).ToArray());
        try
        {
            if (node.Entered is not null)
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    OwnerId,
                    $"dialogue:{Id}:{node.Id}:entered");
                node.Entered(this);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Dialogue '{Id}' node '{node.Id}' Entered callback failed.");
            Close(DialogueCloseReason.Error);
        }
    }

    public void Choose(string choiceId)
    {
        EnsureOpen();
        var node = _graph.Nodes[CurrentNodeId];
        var choice = node.Choices.FirstOrDefault(value =>
            string.Equals(value.Id, choiceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Dialogue node '{CurrentNodeId}' has no choice '{choiceId}'.");
        try
        {
            if (choice.Selected is not null)
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    OwnerId,
                    $"dialogue:{Id}:{CurrentNodeId}:{choice.Id}:selected");
                choice.Selected(this);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Dialogue '{Id}' choice '{choice.Id}' callback failed.");
            Close(DialogueCloseReason.Error);
            return;
        }
        if (!IsOpen) return;
        if (choice.Close) Close(DialogueCloseReason.Completed);
        else ShowNode(choice.NextNodeId!);
    }

    public void Close(DialogueCloseReason reason = DialogueCloseReason.ModRequested)
    {
        if (!IsOpen) return;
        IsOpen = false;
        try { _ui?.Dispose(); }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Dialogue '{Id}' UI cleanup was incomplete.");
        }
        _ui = null;
        UnityUiRuntime.SetDialogueOpen(false);
        _owner.Closed(this);
        if (!_closedCallbackRaised)
        {
            _closedCallbackRaised = true;
            try
            {
                if (_graph.Definition.Closed is not null)
                {
                    using var callback = ModSafetyStore.EnterRuntimeCallback(
                        OwnerId,
                        $"dialogue:{Id}:closed");
                    _graph.Definition.Closed(new DialogueClosedEvent(this, reason));
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Dialogue '{Id}' Closed callback failed.");
            }
        }
        _logger.Info($"Closed dialogue '{OwnerId}:{Id}' with reason {reason}.");
    }

    public void Dispose() => Close();

    private string Resolve(DialogueText text) =>
        text.IsLocalizationTerm ? _localization.Translate(text.Value) : text.Value;

    private void EnsureOpen()
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(DialogueSession), "Dialogue is closed.");
    }
}

internal sealed class DialogueUi : IDisposable
{
    private readonly nint _root;
    private readonly nint _speaker;
    private readonly nint _body;
    private readonly nint _buttonTemplate;
    private readonly nint _closeButton;
    private readonly List<(nint GameObject, nint Button, bool IsClone)> _choices = [];
    private readonly Action<string> _onChoice;
    private readonly DialogueInputGuard _inputGuard;
    private bool _disposed;

    private DialogueUi(
        nint root,
        nint speaker,
        nint body,
        nint buttonTemplate,
        nint closeButton,
        DialogueInputGuard inputGuard,
        Action<string> onChoice,
        Action onClose)
    {
        _root = root;
        _speaker = speaker;
        _body = body;
        _buttonTemplate = buttonTemplate;
        _closeButton = closeButton;
        _inputGuard = inputGuard;
        _onChoice = onChoice;
        UnityUiRuntime.RegisterFrameworkButton(closeButton, onClose);
    }

    internal static DialogueUi Create(
        IUnsafeIl2CppApi api,
        Action<string> onChoice,
        Action onClose)
    {
        var template = UnityUiRuntime.FindLoadedGameObjectPointer("Confirmation Panel", api);
        if (template == 0)
            throw new InvalidOperationException(
                "The current scene has no loaded vanilla Confirmation Panel dialogue template.");
        var root = UnityUiRuntime.CloneSiblingPointer(template);
        UnityUiRuntime.SetObjectNameForSdk(root, "OFS Dialogue Panel");
        UnityUiRuntime.SetActiveForSdk(root, false);
        var fireContent = UnityUiRuntime.FindDescendantPointer(root, "Content (Fire)");
        UnityUiRuntime.SetActiveForSdk(fireContent, false);
        var hireContent = UnityUiRuntime.FindDescendantPointer(root, "Content (Hire)");
        UnityUiRuntime.SetActiveForSdk(hireContent, true);
        var speaker = UnityUiRuntime.FindDescendantPointer(root, "Name_Text");
        var body = UnityUiRuntime.FindDescendantPointer(root, "Desc_Text");
        var buttonTemplate = UnityUiRuntime.FindDescendantPointer(hireContent, "Hire");
        var closeObject = UnityUiRuntime.FindDescendantPointer(root, "ButtonClose");
        var closeButton = UnityUiRuntime.GetButtonPointer(closeObject);
        var inputGuard = DialogueInputGuard.Acquire(api);
        var ui = new DialogueUi(
            root, speaker, body, buttonTemplate, closeButton, inputGuard, onChoice, onClose);
        UnityUiRuntime.SetActiveForSdk(root, true);
        return ui;
    }

    internal void Render(
        string speaker,
        string body,
        IReadOnlyList<(string Id, string Label)> choices)
    {
        ClearChoices();
        UnityUiRuntime.SetTextForSdk(_speaker, speaker);
        UnityUiRuntime.SetTextForSdk(_body, body);
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            var gameObject = index == 0
                ? _buttonTemplate
                : UnityUiRuntime.CloneSiblingPointer(_buttonTemplate);
            UnityUiRuntime.SetObjectNameForSdk(gameObject, $"OFS Dialogue Choice ({choice.Id})");
            UnityUiRuntime.SetRectForSdk(gameObject, 0f, -110f - (index * 72f), 620f, 58f);
            var label = UnityUiRuntime.FindDescendantPointer(gameObject, "Purchase_Text");
            UnityUiRuntime.SetTextForSdk(label, choice.Label);
            var button = UnityUiRuntime.GetButtonPointer(gameObject);
            UnityUiRuntime.RegisterFrameworkButton(button, () => _onChoice(choice.Id));
            UnityUiRuntime.SetActiveForSdk(gameObject, true);
            _choices.Add((gameObject, button, index != 0));
        }
    }

    private void ClearChoices()
    {
        foreach (var choice in _choices)
        {
            UnityUiRuntime.UnregisterFrameworkButton(choice.Button);
            if (choice.IsClone) UnityUiRuntime.DestroyForSdk(choice.GameObject);
            else UnityUiRuntime.SetActiveForSdk(choice.GameObject, false);
        }
        _choices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ClearChoices();
            UnityUiRuntime.UnregisterFrameworkButton(_closeButton);
            UnityUiRuntime.DestroyForSdk(_root);
        }
        finally
        {
            _inputGuard.Dispose();
        }
    }
}

internal sealed class DialogueInputGuard : IDisposable
{
    private readonly IUnsafeIl2CppApi _api;
    private readonly nint _interactionManager;
    private readonly nint _inputActiveField;
    private readonly bool _inputWasActive;
    private readonly nint _cursorClass;
    private readonly bool _cursorWasVisible;
    private readonly int _cursorLockState;
    private bool _disposed;

    private DialogueInputGuard(
        IUnsafeIl2CppApi api,
        nint interactionManager,
        nint inputActiveField,
        bool inputWasActive,
        nint cursorClass,
        bool cursorWasVisible,
        int cursorLockState)
    {
        _api = api;
        _interactionManager = interactionManager;
        _inputActiveField = inputActiveField;
        _inputWasActive = inputWasActive;
        _cursorClass = cursorClass;
        _cursorWasVisible = cursorWasVisible;
        _cursorLockState = cursorLockState;
    }

    internal static unsafe DialogueInputGuard Acquire(IUnsafeIl2CppApi api)
    {
        var interactionClass = api.FindClass(
            "Assembly-CSharp.dll", string.Empty, "PlayerInteractionManager");
        var manager = UnityUiRuntime.FindActiveLoadedComponentPointer(interactionClass, api);
        var inputField = interactionClass == 0 ? 0 : api.FindField(interactionClass, "InputActive");
        var inputWasActive = manager != 0 && inputField != 0 && api.ReadBoolean(manager, inputField);
        if (manager != 0 && inputField != 0) api.WriteBoolean(manager, inputField, false);

        var cursorClass = api.FindClass(
            "UnityEngine.CoreModule.dll", "UnityEngine", "Cursor");
        var cursorWasVisible = false;
        var cursorLockState = 0;
        if (cursorClass != 0)
        {
            var visible = api.RuntimeInvoke(RequireMethod(api, cursorClass, "get_visible", 0), 0, 0);
            var lockState = api.RuntimeInvoke(RequireMethod(api, cursorClass, "get_lockState", 0), 0, 0);
            cursorWasVisible = visible != 0 && api.Unbox(visible) != 0 &&
                System.Runtime.InteropServices.Marshal.ReadByte(api.Unbox(visible)) != 0;
            cursorLockState = lockState == 0 || api.Unbox(lockState) == 0
                ? 0
                : System.Runtime.InteropServices.Marshal.ReadInt32(api.Unbox(lockState));
            byte show = 1;
            var unlocked = 0;
            nint* visibleArguments = stackalloc nint[1];
            visibleArguments[0] = (nint)(&show);
            _ = api.RuntimeInvoke(RequireMethod(api, cursorClass, "set_visible", 1), 0, (nint)visibleArguments);
            nint* lockArguments = stackalloc nint[1];
            lockArguments[0] = (nint)(&unlocked);
            _ = api.RuntimeInvoke(RequireMethod(api, cursorClass, "set_lockState", 1), 0, (nint)lockArguments);
        }
        return new DialogueInputGuard(
            api, manager, inputField, inputWasActive, cursorClass, cursorWasVisible, cursorLockState);
    }

    internal unsafe void Maintain()
    {
        if (_disposed) return;
        if (_interactionManager != 0 && _inputActiveField != 0)
            _api.WriteBoolean(_interactionManager, _inputActiveField, false);
        if (_cursorClass == 0) return;
        byte visible = 1;
        var unlocked = 0;
        nint* visibleArguments = stackalloc nint[1];
        visibleArguments[0] = (nint)(&visible);
        _ = _api.RuntimeInvoke(
            RequireMethod(_api, _cursorClass, "set_visible", 1),
            0,
            (nint)visibleArguments);
        nint* lockArguments = stackalloc nint[1];
        lockArguments[0] = (nint)(&unlocked);
        _ = _api.RuntimeInvoke(
            RequireMethod(_api, _cursorClass, "set_lockState", 1),
            0,
            (nint)lockArguments);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_interactionManager != 0 && _inputActiveField != 0)
            _api.WriteBoolean(_interactionManager, _inputActiveField, _inputWasActive);
        if (_cursorClass == 0) return;
        byte visible = _cursorWasVisible ? (byte)1 : (byte)0;
        var lockState = _cursorLockState;
        nint* visibleArguments = stackalloc nint[1];
        visibleArguments[0] = (nint)(&visible);
        _ = _api.RuntimeInvoke(RequireMethod(_api, _cursorClass, "set_visible", 1), 0, (nint)visibleArguments);
        nint* lockArguments = stackalloc nint[1];
        lockArguments[0] = (nint)(&lockState);
        _ = _api.RuntimeInvoke(RequireMethod(_api, _cursorClass, "set_lockState", 1), 0, (nint)lockArguments);
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi api,
        nint klass,
        string name,
        int argumentCount)
    {
        var method = api.FindMethod(klass, name, argumentCount);
        return method != 0 ? method : throw new MissingMethodException("UnityEngine.Cursor", name);
    }
}
