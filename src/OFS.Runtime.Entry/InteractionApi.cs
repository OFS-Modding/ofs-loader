using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class InteractionRuntime
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FireDelegate(nint instance, nint methodInfo);

    private static readonly Dictionary<nint, InteractionRegistration> Routes = new();
    private static FireDelegate? _primaryReplacement;
    private static FireDelegate? _secondaryReplacement;
    private static FireDelegate? _primaryOriginal;
    private static FireDelegate? _secondaryOriginal;
    private static INativeDetour? _primaryHook;
    private static INativeDetour? _secondaryHook;
    private static nint _interactableClass;

    internal static nint InteractableClass => _interactableClass;
    internal static bool IsAvailable => _primaryHook?.IsInstalled == true && _secondaryHook?.IsInstalled == true;

    internal static void Initialize(IUnsafeIl2CppApi api)
    {
        _interactableClass = api.FindClass("Assembly-CSharp.dll", string.Empty, "Interactable");
        if (_interactableClass == 0)
        {
            RuntimeLog.Write("Interaction API unavailable: Interactable class was not found.");
            return;
        }

        var primaryMethod = api.FindMethod(_interactableClass, "FirePrimary", 0);
        var secondaryMethod = api.FindMethod(_interactableClass, "FireSecondary", 0);
        var primaryTarget = api.GetMethodPointer(primaryMethod);
        var secondaryTarget = api.GetMethodPointer(secondaryMethod);
        if (primaryTarget == 0 || secondaryTarget == 0)
        {
            RuntimeLog.Write("Interaction API unavailable: FirePrimary/FireSecondary has no native target.");
            return;
        }

        _primaryReplacement = OnPrimary;
        _secondaryReplacement = OnSecondary;
        _primaryHook = HookRuntime.Install(
            "ofs.framework.interactions",
            new NativeDetourDefinition(
                "interactable-primary-router",
                primaryTarget,
                Marshal.GetFunctionPointerForDelegate(_primaryReplacement)));
        try
        {
            _secondaryHook = HookRuntime.Install(
                "ofs.framework.interactions",
                new NativeDetourDefinition(
                    "interactable-secondary-router",
                    secondaryTarget,
                    Marshal.GetFunctionPointerForDelegate(_secondaryReplacement)));
            _primaryOriginal = Marshal.GetDelegateForFunctionPointer<FireDelegate>(_primaryHook.Original);
            _secondaryOriginal = Marshal.GetDelegateForFunctionPointer<FireDelegate>(_secondaryHook.Original);
            RuntimeLog.Write(
                $"Interaction router installed: primary=0x{primaryTarget:X}, secondary=0x{secondaryTarget:X}.");
        }
        catch
        {
            _primaryHook.Remove();
            _primaryHook = null;
            throw;
        }
    }

    internal static void Add(InteractionRegistration registration)
    {
        if (Routes.TryGetValue(registration.Interactable.Pointer, out var existing))
        {
            throw new InvalidOperationException(
                $"Interactable 0x{registration.Interactable.Pointer:X} is already owned by " +
                $"'{existing.OwnerId}:{existing.Id}'.");
        }
        Routes.Add(registration.Interactable.Pointer, registration);
    }

    internal static void Remove(InteractionRegistration registration) =>
        Routes.Remove(registration.Interactable.Pointer);

    internal static void DispatchForTests(
        InteractionRegistration registration,
        InteractionButton button,
        Action original) => Dispatch(registration, button, original);

    private static void OnPrimary(nint instance, nint methodInfo) =>
        RouteNative(instance, methodInfo, InteractionButton.Primary, _primaryOriginal);

    private static void OnSecondary(nint instance, nint methodInfo) =>
        RouteNative(instance, methodInfo, InteractionButton.Secondary, _secondaryOriginal);

    private static void RouteNative(
        nint instance,
        nint methodInfo,
        InteractionButton button,
        FireDelegate? original)
    {
        try
        {
            if (Routes.TryGetValue(instance, out var registration) && registration.Enabled)
            {
                Dispatch(registration, button, () => original?.Invoke(instance, methodInfo));
            }
            else
            {
                original?.Invoke(instance, methodInfo);
            }
        }
        catch (Exception exception)
        {
            try { RuntimeLog.Write($"Interaction native route failed safely: {exception}"); }
            catch { }
        }
    }

    private static void Dispatch(
        InteractionRegistration registration,
        InteractionButton button,
        Action original)
    {
        var callback = button == InteractionButton.Primary
            ? registration.Definition.Primary
            : registration.Definition.Secondary;
        var handling = button == InteractionButton.Primary
            ? registration.Definition.PrimaryHandling
            : registration.Definition.SecondaryHandling;
        if (callback is null)
        {
            original();
            return;
        }

        void InvokeModCallback()
        {
            using var lease = ModSafetyStore.EnterRuntimeCallback(
                registration.OwnerId,
                $"interaction:{registration.Id}:{button}");
            callback(registration.CreateEvent(button));
        }

        var originalCalled = false;
        try
        {
            if (handling == InteractionHandling.BeforeOriginal)
            {
                InvokeModCallback();
                originalCalled = true;
                original();
            }
            else if (handling == InteractionHandling.AfterOriginal)
            {
                originalCalled = true;
                original();
                InvokeModCallback();
            }
            else
            {
                InvokeModCallback();
            }
        }
        catch (Exception exception)
        {
            registration.Enabled = false;
            registration.Logger.Error(exception,
                $"Interaction '{registration.Id}' {button} callback failed and was disabled.");
            if (!originalCalled) original();
        }
    }
}

internal sealed class InteractionApi : IInteractionApi
{
    private readonly string _ownerId;
    private readonly IUnsafeIl2CppApi _api;
    private readonly IModLogger _logger;
    private readonly List<InteractionRegistration> _registrations = [];

    public InteractionApi(string ownerId, IUnsafeIl2CppApi api, IModEvents events, IModLogger logger)
    {
        _ownerId = ownerId;
        _api = api;
        _logger = logger;
        events.SceneUnloaded += _ => RemoveSceneLocal();
    }

    public bool IsAvailable => InteractionRuntime.IsAvailable;

    public IInteractionRegistration Register(InteractionDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateId(definition.Id);
        if (definition.GameObject.IsNull)
            throw new ArgumentException("Interaction GameObject is null.", nameof(definition));
        if (definition.Primary is null && definition.Secondary is null)
            throw new ArgumentException("At least one interaction callback is required.", nameof(definition));
        if (!IsAvailable)
            throw new InvalidOperationException("The native interaction router is unavailable.");
        if (_registrations.Any(value => value.IsRegistered &&
            string.Equals(value.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Interaction id '{definition.Id}' is already registered.");

        var component = UnityUiRuntime.TryGetComponentPointer(
            definition.GameObject.Pointer,
            InteractionRuntime.InteractableClass);
        if (component == 0)
            throw new InvalidOperationException(
                "The target GameObject has no vanilla Interactable component. " +
                "Custom prefabs should derive from a compatible vanilla prefab.");

        var registration = new InteractionRegistration(
            this, _ownerId, definition, new UnityObject(component), _api, _logger);
        try
        {
            registration.ApplyConfiguration();
            InteractionRuntime.Add(registration);
            _registrations.Add(registration);
            _logger.Info($"Registered interaction '{_ownerId}:{definition.Id}' on 0x{component:X}.");
            return registration;
        }
        catch
        {
            registration.RestoreConfiguration();
            throw;
        }
    }

    internal void Remove(InteractionRegistration registration, bool restore)
    {
        if (!registration.IsRegistered) return;
        InteractionRuntime.Remove(registration);
        registration.MarkRemoved();
        if (restore) registration.RestoreConfiguration();
    }

    internal void RemoveAll(bool restore = true)
    {
        foreach (var registration in _registrations.ToArray()) Remove(registration, restore);
        _registrations.Clear();
    }

    private void RemoveSceneLocal()
    {
        foreach (var registration in _registrations
                     .Where(value => value.IsRegistered && !value.Definition.Persistent)
                     .ToArray())
            Remove(registration, restore: false);
        _registrations.RemoveAll(value => !value.IsRegistered);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("Interaction id must use 1-100 ASCII letters, digits, dots, dashes or underscores.");
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Interactions must be registered on Unity's main thread.");
    }
}

internal sealed class InteractionRegistration : IInteractionRegistration
{
    private readonly InteractionApi _owner;
    private readonly IUnsafeIl2CppApi _api;
    private readonly List<FieldSnapshot> _snapshots = [];

    internal InteractionRegistration(
        InteractionApi owner,
        string ownerId,
        InteractionDefinition definition,
        UnityObject interactable,
        IUnsafeIl2CppApi api,
        IModLogger logger)
    {
        _owner = owner;
        OwnerId = ownerId;
        Definition = definition;
        Interactable = interactable;
        _api = api;
        Logger = logger;
    }

    public string Id => Definition.Id;
    public string OwnerId { get; }
    public UnityObject GameObject => Definition.GameObject;
    public UnityObject Interactable { get; }
    public bool Enabled { get; set; } = true;
    public bool IsRegistered { get; private set; } = true;
    internal InteractionDefinition Definition { get; }
    internal IModLogger Logger { get; }

    internal InteractionEvent CreateEvent(InteractionButton button) =>
        new(this, button, GameObject, Interactable);

    internal void ApplyConfiguration()
    {
        if (Definition.DisplayName is not null)
            WriteReference("interactableName", _api.NewString(Definition.DisplayName));
        if (Definition.DisplayName is not null)
            WriteBoolean("interactableNameIsLocalized", Definition.DisplayNameIsLocalizationTerm);
        if (Definition.PrimaryMode is { } primaryMode) WriteInt32("primaryMode", (int)primaryMode);
        if (Definition.PrimaryPrompt is { } primaryPrompt) WriteInt32("currentPrimaryState", (int)primaryPrompt);
        if (Definition.PrimaryHoldSeconds is { } primaryHold) WriteSingle("primaryDefaultHold", primaryHold);
        if (Definition.SecondaryMode is { } secondaryMode) WriteInt32("secondaryMode", (int)secondaryMode);
        if (Definition.SecondaryPrompt is { } secondaryPrompt)
        {
            WriteInt32("currentSecondaryState", (int)secondaryPrompt);
            WriteBoolean("enableSecondary", secondaryPrompt != SecondaryInteractionPrompt.None);
        }
        if (Definition.SecondaryHoldSeconds is { } secondaryHold) WriteSingle("secondaryDefaultHold", secondaryHold);
    }

    internal void RestoreConfiguration()
    {
        for (var index = _snapshots.Count - 1; index >= 0; index--)
        {
            var snapshot = _snapshots[index];
            switch (snapshot.Kind)
            {
                case FieldKind.Reference: _api.WriteObjectReference(Interactable.Pointer, snapshot.Field, snapshot.Reference); break;
                case FieldKind.Int32: _api.WriteInt32(Interactable.Pointer, snapshot.Field, snapshot.Int32); break;
                case FieldKind.Single: _api.WriteSingle(Interactable.Pointer, snapshot.Field, snapshot.Single); break;
                case FieldKind.Boolean: _api.WriteBoolean(Interactable.Pointer, snapshot.Field, snapshot.Boolean); break;
            }
        }
        _snapshots.Clear();
    }

    internal void MarkRemoved() => IsRegistered = false;
    public void Unregister() => _owner.Remove(this, restore: true);
    public void Dispose() => Unregister();

    private nint Field(string name)
    {
        var field = _api.FindField(InteractionRuntime.InteractableClass, name);
        return field != 0 ? field : throw new MissingFieldException("Interactable", name);
    }

    private void WriteReference(string name, nint value)
    {
        var field = Field(name);
        _snapshots.Add(new(field, FieldKind.Reference, _api.ReadObjectReference(Interactable.Pointer, field)));
        _api.WriteObjectReference(Interactable.Pointer, field, value);
    }
    private void WriteInt32(string name, int value)
    {
        var field = Field(name);
        _snapshots.Add(new(field, FieldKind.Int32, Int32: _api.ReadInt32(Interactable.Pointer, field)));
        _api.WriteInt32(Interactable.Pointer, field, value);
    }
    private void WriteSingle(string name, float value)
    {
        var field = Field(name);
        _snapshots.Add(new(field, FieldKind.Single, Single: _api.ReadSingle(Interactable.Pointer, field)));
        _api.WriteSingle(Interactable.Pointer, field, value);
    }
    private void WriteBoolean(string name, bool value)
    {
        var field = Field(name);
        _snapshots.Add(new(field, FieldKind.Boolean, Boolean: _api.ReadBoolean(Interactable.Pointer, field)));
        _api.WriteBoolean(Interactable.Pointer, field, value);
    }

    private enum FieldKind { Reference, Int32, Single, Boolean }
    private readonly record struct FieldSnapshot(
        nint Field,
        FieldKind Kind,
        nint Reference = 0,
        int Int32 = 0,
        float Single = 0,
        bool Boolean = false);
}
