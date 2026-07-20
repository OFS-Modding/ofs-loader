using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class InputRuntime
{
    private static readonly object Gate = new();
    private static readonly List<InputActionHandle> Actions = [];
    private static IUnsafeIl2CppApi? _api;
    private static nint _keyboardClass;
    private static nint _mouseClass;
    private static nint _buttonClass;
    private static nint _eventSystemClass;
    private static long _nextSequence;
    private static bool _available;

    public static bool IsAvailable => _available;

    public static void Initialize(IUnsafeIl2CppApi api)
    {
        lock (Gate)
        {
            Actions.Clear();
            _nextSequence = 0;
            _api = api;
            try
            {
                _keyboardClass = RequireClass(
                    api,
                    "Unity.InputSystem.dll",
                    "UnityEngine.InputSystem",
                    "Keyboard");
                _mouseClass = RequireClass(
                    api,
                    "Unity.InputSystem.dll",
                    "UnityEngine.InputSystem",
                    "Mouse");
                _buttonClass = RequireClass(
                    api,
                    "Unity.InputSystem.dll",
                    "UnityEngine.InputSystem.Controls",
                    "ButtonControl");
                _eventSystemClass = RequireClass(
                    api,
                    "UnityEngine.UI.dll",
                    "UnityEngine.EventSystems",
                    "EventSystem");
                _ = RequireMethod(api, _keyboardClass, "get_current", 0);
                _ = RequireMethod(api, _keyboardClass, "get_Item", 1);
                _ = RequireMethod(api, _mouseClass, "get_current", 0);
                foreach (var getter in MouseGetters.Values)
                {
                    _ = RequireMethod(api, _mouseClass, getter, 0);
                }
                _ = RequireMethod(api, _buttonClass, "get_isPressed", 0);
                _ = RequireMethod(api, _buttonClass, "get_wasPressedThisFrame", 0);
                _ = RequireMethod(api, _buttonClass, "get_wasReleasedThisFrame", 0);
                _ = RequireMethod(api, _eventSystemClass, "get_current", 0);
                _ = RequireMethod(api, _eventSystemClass, "get_currentSelectedGameObject", 0);
                _available = true;
                RuntimeLog.Write("Unity Input System bindings resolved; mod input API is available.");
            }
            catch (Exception exception)
            {
                _available = false;
                RuntimeLog.Write($"Mod input API unavailable: {exception.Message}");
            }
        }
    }

    public static void Poll(FrameEvent frame)
    {
        if (!_available || _api is null)
        {
            return;
        }

        try
        {
            var keyboard = _api.RuntimeInvoke(
                RequireMethod(_api, _keyboardClass, "get_current", 0),
                0,
                0);
            var mouse = _api.RuntimeInvoke(
                RequireMethod(_api, _mouseClass, "get_current", 0),
                0,
                0);
            var cache = new Dictionary<InputBinding, InputButtonState>();
            InputButtonState Read(InputBinding binding)
            {
                if (!cache.TryGetValue(binding, out var state))
                {
                    state = ReadBinding(binding, keyboard, mouse);
                    cache.Add(binding, state);
                }
                return state;
            }

            PollCore(
                frame,
                Read,
                UnityUiRuntime.IsFrameworkUiCapturingInput,
                HasSelectedUi());
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Mod input poll failed for frame {frame.FrameCount}: {exception}");
        }
    }

    internal static void InitializeForTests()
    {
        lock (Gate)
        {
            Actions.Clear();
            _nextSequence = 0;
            _api = null;
            _available = true;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Actions.Clear();
            _nextSequence = 0;
            _available = false;
            _api = null;
        }
    }

    internal static void PollForTests(
        FrameEvent frame,
        IReadOnlyDictionary<InputBinding, InputButtonState> states,
        bool frameworkUiOpen = false,
        bool selectedUi = false) =>
        PollCore(
            frame,
            binding => states.TryGetValue(binding, out var state) ? state : default,
            frameworkUiOpen,
            selectedUi);

    internal static IModInputAction Register(
        string ownerId,
        ModRuntime.ModLogger logger,
        ModInputActionDefinition definition)
    {
        lock (Gate)
        {
            if (!_available)
            {
                throw new NotSupportedException("Unity Input System is unavailable in this game build.");
            }
            ValidateDefinition(definition);
            var qualifiedId = $"{ownerId}:{definition.Id}";
            if (Actions.Any(action => string.Equals(
                    action.QualifiedId,
                    qualifiedId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Input action '{qualifiedId}' is already registered.");
            }

            var action = new InputActionHandle(
                ownerId,
                logger,
                definition,
                ++_nextSequence);
            Actions.Add(action);
            SortActions();
            logger.Info(
                $"Registered input action '{definition.Id}': " +
                $"binding={definition.Binding.Device}/{definition.Binding.Code}, " +
                $"trigger={definition.Trigger}, modifiers={definition.Modifiers}.");
            return action;
        }
    }

    internal static void RemoveAll(string ownerId)
    {
        lock (Gate)
        {
            foreach (var action in Actions
                         .Where(action => string.Equals(
                             action.OwnerId,
                             ownerId,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                action.Unregister();
            }
        }
    }

    internal static void ValidateBinding(InputBinding binding)
    {
        if (!Enum.IsDefined(binding.Device))
        {
            throw new ArgumentOutOfRangeException(nameof(binding), "Unknown input device kind.");
        }
        if (binding.Device == InputDeviceKind.Keyboard)
        {
            if (!Enum.IsDefined(typeof(ModKey), binding.Code) || binding.Code == (int)ModKey.None)
            {
                throw new ArgumentOutOfRangeException(nameof(binding), "Unknown or empty keyboard key.");
            }
        }
        else if (!Enum.IsDefined(typeof(ModMouseButton), binding.Code))
        {
            throw new ArgumentOutOfRangeException(nameof(binding), "Unknown mouse button.");
        }
    }

    private static void PollCore(
        FrameEvent frame,
        Func<InputBinding, InputButtonState> read,
        bool frameworkUiOpen,
        bool selectedUi)
    {
        InputActionHandle[] snapshot;
        lock (Gate)
        {
            snapshot = Actions.Where(action => action.Enabled && action.IsRegistered).ToArray();
        }
        foreach (var action in snapshot)
        {
            if (IsCaptured(action.CapturePolicy, frameworkUiOpen, selectedUi) ||
                !ModifiersHeld(action.Modifiers, read))
            {
                continue;
            }
            var state = read(action.Binding);
            var observed = InputTrigger.None;
            if (state.Pressed) observed |= InputTrigger.Pressed;
            if (state.Held) observed |= InputTrigger.Held;
            if (state.Released) observed |= InputTrigger.Released;
            observed &= action.Trigger;
            if (observed == InputTrigger.None)
            {
                continue;
            }

            try
            {
                action.Callback(new ModInputEvent(
                    frame,
                    action.Binding,
                    observed,
                    action.Modifiers));
            }
            catch (Exception exception)
            {
                action.Logger.Error(exception, $"Input action '{action.Id}' failed.");
                if (action.DisableOnException)
                {
                    action.Enabled = false;
                    action.Logger.Warning($"Input action '{action.Id}' disabled after callback failure.");
                }
            }
        }
    }

    private static bool IsCaptured(
        InputCapturePolicy policy,
        bool frameworkUiOpen,
        bool selectedUi) => policy switch
        {
            InputCapturePolicy.Always => false,
            InputCapturePolicy.NoFrameworkUi => frameworkUiOpen,
            InputCapturePolicy.NoSelectedUi => frameworkUiOpen || selectedUi,
            _ => true,
        };

    private static bool ModifiersHeld(
        InputModifiers modifiers,
        Func<InputBinding, InputButtonState> read)
    {
        if ((modifiers & InputModifiers.Shift) != 0 &&
            !EitherHeld(ModKey.LeftShift, ModKey.RightShift, read)) return false;
        if ((modifiers & InputModifiers.Control) != 0 &&
            !EitherHeld(ModKey.LeftCtrl, ModKey.RightCtrl, read)) return false;
        if ((modifiers & InputModifiers.Alt) != 0 &&
            !EitherHeld(ModKey.LeftAlt, ModKey.RightAlt, read)) return false;
        if ((modifiers & InputModifiers.Meta) != 0 &&
            !EitherHeld(ModKey.LeftMeta, ModKey.RightMeta, read)) return false;
        return true;
    }

    private static bool EitherHeld(
        ModKey left,
        ModKey right,
        Func<InputBinding, InputButtonState> read) =>
        read(InputBinding.ForKey(left)).Held || read(InputBinding.ForKey(right)).Held;

    private static unsafe InputButtonState ReadBinding(
        InputBinding binding,
        nint keyboard,
        nint mouse)
    {
        ValidateBinding(binding);
        var api = _api!;
        nint button;
        if (binding.Device == InputDeviceKind.Keyboard)
        {
            if (keyboard == 0) return default;
            var code = binding.Code;
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&code);
            button = api.RuntimeInvoke(
                RequireMethod(api, _keyboardClass, "get_Item", 1),
                keyboard,
                (nint)arguments);
        }
        else
        {
            if (mouse == 0) return default;
            var getter = MouseGetters[(ModMouseButton)binding.Code];
            button = api.RuntimeInvoke(
                RequireMethod(api, _mouseClass, getter, 0),
                mouse,
                0);
        }

        return button == 0
            ? default
            : new InputButtonState(
                ReadBooleanProperty(api, button, "get_wasPressedThisFrame"),
                ReadBooleanProperty(api, button, "get_isPressed"),
                ReadBooleanProperty(api, button, "get_wasReleasedThisFrame"));
    }

    private static bool HasSelectedUi()
    {
        var api = _api!;
        var eventSystem = api.RuntimeInvoke(
            RequireMethod(api, _eventSystemClass, "get_current", 0),
            0,
            0);
        return eventSystem != 0 && api.RuntimeInvoke(
            RequireMethod(api, _eventSystemClass, "get_currentSelectedGameObject", 0),
            eventSystem,
            0) != 0;
    }

    private static bool ReadBooleanProperty(
        IUnsafeIl2CppApi api,
        nint instance,
        string getter)
    {
        var boxed = api.RuntimeInvoke(RequireMethod(api, _buttonClass, getter, 0), instance, 0);
        var value = api.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private static void ValidateDefinition(ModInputActionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Id) ||
            definition.Id.Length > 100 ||
            definition.Id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Input action id must be 1-100 ASCII letters, digits, dots, dashes or underscores.");
        }
        ArgumentNullException.ThrowIfNull(definition.Triggered);
        ValidateBinding(definition.Binding);
        if (definition.Trigger == InputTrigger.None ||
            (definition.Trigger & ~(InputTrigger.Pressed | InputTrigger.Held | InputTrigger.Released)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Input trigger is empty or invalid.");
        }
        if ((definition.Modifiers & ~(
                InputModifiers.Shift |
                InputModifiers.Control |
                InputModifiers.Alt |
                InputModifiers.Meta)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Input modifiers contain unknown flags.");
        }
        if (!Enum.IsDefined(definition.CapturePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Unknown input capture policy.");
        }
    }

    private static void SortActions() => Actions.Sort(static (left, right) =>
    {
        var order = left.Order.CompareTo(right.Order);
        return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
    });

    private static nint RequireClass(
        IUnsafeIl2CppApi api,
        string assembly,
        string namespaze,
        string name)
    {
        var klass = api.FindClass(assembly, namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Input class '{namespaze}.{name}' was not found.");
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi api,
        nint klass,
        string name,
        int argumentCount)
    {
        var method = api.FindMethod(klass, name, argumentCount);
        return method != 0
            ? method
            : throw new MissingMethodException($"Input method '{name}/{argumentCount}' was not found.");
    }

    private static readonly IReadOnlyDictionary<ModMouseButton, string> MouseGetters =
        new Dictionary<ModMouseButton, string>
        {
            [ModMouseButton.Left] = "get_leftButton",
            [ModMouseButton.Middle] = "get_middleButton",
            [ModMouseButton.Right] = "get_rightButton",
            [ModMouseButton.Back] = "get_backButton",
            [ModMouseButton.Forward] = "get_forwardButton",
        };

    internal readonly record struct InputButtonState(bool Pressed, bool Held, bool Released);

    private sealed class InputActionHandle : IModInputAction
    {
        private InputBinding _binding;
        private InputTrigger _trigger;
        private InputModifiers _modifiers;
        private InputCapturePolicy _capturePolicy;

        public InputActionHandle(
            string ownerId,
            ModRuntime.ModLogger logger,
            ModInputActionDefinition definition,
            long sequence)
        {
            OwnerId = ownerId;
            Logger = logger;
            Id = definition.Id;
            QualifiedId = $"{ownerId}:{definition.Id}";
            _binding = definition.Binding;
            _trigger = definition.Trigger;
            _modifiers = definition.Modifiers;
            _capturePolicy = definition.CapturePolicy;
            Callback = definition.Triggered;
            Order = definition.Order;
            DisableOnException = definition.DisableOnException;
            Sequence = sequence;
        }

        public string OwnerId { get; }
        public ModRuntime.ModLogger Logger { get; }
        public string Id { get; }
        public string QualifiedId { get; }
        public Action<ModInputEvent> Callback { get; }
        public int Order { get; }
        public bool DisableOnException { get; }
        public long Sequence { get; }
        public bool Enabled { get; set; } = true;
        public bool IsRegistered { get; private set; } = true;

        public InputBinding Binding
        {
            get => _binding;
            set
            {
                ValidateBinding(value);
                _binding = value;
            }
        }

        public InputTrigger Trigger
        {
            get => _trigger;
            set
            {
                if (value == InputTrigger.None ||
                    (value & ~(InputTrigger.Pressed | InputTrigger.Held | InputTrigger.Released)) != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _trigger = value;
            }
        }

        public InputModifiers Modifiers
        {
            get => _modifiers;
            set
            {
                if ((value & ~(
                        InputModifiers.Shift |
                        InputModifiers.Control |
                        InputModifiers.Alt |
                        InputModifiers.Meta)) != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _modifiers = value;
            }
        }

        public InputCapturePolicy CapturePolicy
        {
            get => _capturePolicy;
            set
            {
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
                _capturePolicy = value;
            }
        }

        public void Unregister()
        {
            lock (Gate)
            {
                if (!IsRegistered) return;
                Actions.Remove(this);
                IsRegistered = false;
                Enabled = false;
            }
        }

        public void Dispose() => Unregister();
    }
}

internal sealed class ModInput(
    string ownerId,
    ModRuntime.ModLogger logger) : IModInput
{
    public bool IsAvailable => InputRuntime.IsAvailable;
    public IModInputAction Register(ModInputActionDefinition definition) =>
        InputRuntime.Register(ownerId, logger, definition);
    public void RemoveAll() => InputRuntime.RemoveAll(ownerId);
}
