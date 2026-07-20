using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModHooks(
    string ownerId,
    ModRuntime.ModLogger logger,
    IUnsafeIl2CppApi unsafeApi) : IModHooks
{
    private readonly List<INativeDetour> _handles = new();

    public INativeDetour Install(NativeDetourDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateIdAvailable(definition.Id);

        var handle = HookRuntime.Install(ownerId, definition);
        _handles.Add(handle);
        LogInstalled(definition.Id, definition.Target, handle.Original);
        return handle;
    }

    public IIl2CppMethodDetour<TDelegate> InstallIl2Cpp<TDelegate>(
        Il2CppMethodDetourDefinition definition,
        TDelegate replacement)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateIdAvailable(definition.Id);
        ValidateMethodTarget(definition);

        var klass = unsafeApi.FindClass(
            definition.AssemblyName,
            definition.Namespace,
            definition.ClassName);
        if (klass == 0)
        {
            throw new InvalidOperationException(
                $"IL2CPP class '{definition.Namespace}.{definition.ClassName}' was not found " +
                $"in '{definition.AssemblyName}'.");
        }

        var methodInfo = unsafeApi.FindMethod(
            klass,
            definition.MethodName,
            definition.ArgumentCount);
        if (methodInfo == 0)
        {
            throw new MissingMethodException(
                $"IL2CPP method '{definition.Namespace}.{definition.ClassName}." +
                $"{definition.MethodName}/{definition.ArgumentCount}' was not found.");
        }

        var target = unsafeApi.GetMethodPointer(methodInfo);
        if (target == 0)
        {
            throw new MissingMethodException(
                $"IL2CPP method '{definition.Namespace}.{definition.ClassName}." +
                $"{definition.MethodName}/{definition.ArgumentCount}' has no native implementation.");
        }

        var guardedReplacement = HotDetourGuard.Wrap(ownerId, definition.Id, replacement);
        var replacementPointer = Marshal.GetFunctionPointerForDelegate(guardedReplacement);
        var native = HookRuntime.Install(
            ownerId,
            new NativeDetourDefinition(definition.Id, target, replacementPointer));
        try
        {
            var original = Marshal.GetDelegateForFunctionPointer<TDelegate>(native.Original);
            var handle = new Il2CppMethodDetour<TDelegate>(
                native,
                methodInfo,
                guardedReplacement,
                original);
            _handles.Add(handle);
            LogInstalled(definition.Id, target, native.Original);
            logger.Info(
                $"Resolved declarative IL2CPP hook '{definition.Id}' to " +
                $"{definition.AssemblyName}!{definition.Namespace}.{definition.ClassName}." +
                $"{definition.MethodName}/{definition.ArgumentCount} (MethodInfo=0x{methodInfo:X}).");
            return handle;
        }
        catch
        {
            native.Remove();
            throw;
        }
    }

    public void RemoveAll()
    {
        List<Exception>? failures = null;
        for (var index = _handles.Count - 1; index >= 0; --index)
        {
            try
            {
                _handles[index].Remove();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
        {
            throw new AggregateException(
                $"One or more hooks owned by mod '{ownerId}' could not be removed.",
                failures);
        }
    }

    private void ValidateIdAvailable(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
        {
            throw new ArgumentException("Hook id must contain 1-100 characters.");
        }
        if (_handles.Any(handle =>
                handle.IsInstalled &&
                string.Equals(handle.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Mod '{ownerId}' already owns a hook named '{id}'.");
        }
    }

    private static void ValidateMethodTarget(Il2CppMethodDetourDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.AssemblyName);
        ArgumentNullException.ThrowIfNull(definition.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ClassName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.MethodName);
        if (definition.ArgumentCount is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "IL2CPP method argument count must be between 0 and 64.");
        }
    }

    private void LogInstalled(string id, nint target, nint original) =>
        logger.Info(
            $"Installed hook '{id}' target=0x{target:X}, original=0x{original:X}.");
}

internal static class HookRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, NativeDetour> HooksByTarget = new();
    private static nint _installDetour;
    private static nint _removeDetour;
    private static Func<nint, nint, (bool Success, nint Original)>? _testInstaller;
    private static Func<nint, bool>? _testRemover;

    public static void Configure(NativeBootstrapApi api)
    {
        _installDetour = api.InstallDetour;
        _removeDetour = api.RemoveDetour;
        _testInstaller = null;
        _testRemover = null;
    }

    internal static void ConfigureForTests(
        Func<nint, nint, (bool Success, nint Original)> installer,
        Func<nint, bool> remover)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(remover);
        lock (Gate)
        {
            if (HooksByTarget.Count != 0)
            {
                throw new InvalidOperationException("Cannot replace the hook backend while hooks are installed.");
            }
            _installDetour = 0;
            _removeDetour = 0;
            _testInstaller = installer;
            _testRemover = remover;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            if (HooksByTarget.Count != 0)
            {
                throw new InvalidOperationException("Cannot reset the hook backend while hooks are installed.");
            }
            _installDetour = 0;
            _removeDetour = 0;
            _testInstaller = null;
            _testRemover = null;
        }
    }

    public static unsafe NativeDetour Install(
        string ownerId,
        NativeDetourDefinition definition)
    {
        if ((_installDetour == 0 || _removeDetour == 0) &&
            (_testInstaller is null || _testRemover is null))
        {
            throw new InvalidOperationException("The native detour bridge is unavailable.");
        }
        if (definition.Target == 0 || definition.Replacement == 0)
        {
            throw new ArgumentException("Hook target and replacement pointers must be non-zero.");
        }

        lock (Gate)
        {
            if (HooksByTarget.TryGetValue(definition.Target, out var existing))
            {
                throw new InvalidOperationException(
                    $"Target 0x{definition.Target:X} is already hooked by mod '{existing.OwnerId}'.");
            }

            nint original;
            bool installed;
            if (_testInstaller is not null)
            {
                var result = _testInstaller(definition.Target, definition.Replacement);
                installed = result.Success;
                original = result.Original;
            }
            else
            {
                original = 0;
                var install = (delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)_installDetour;
                installed = install(definition.Target, definition.Replacement, &original) != 0;
            }
            if (!installed || original == 0)
            {
                throw new InvalidOperationException(
                    $"Native bootstrap rejected hook '{definition.Id}' at 0x{definition.Target:X}.");
            }

            var handle = new NativeDetour(
                ownerId,
                definition.Id,
                definition.Target,
                definition.Replacement,
                original);
            HooksByTarget.Add(definition.Target, handle);
            return handle;
        }
    }

    public static unsafe void Remove(NativeDetour handle)
    {
        lock (Gate)
        {
            if (!handle.IsInstalled)
            {
                return;
            }
            if (!HooksByTarget.TryGetValue(handle.Target, out var owned) ||
                !ReferenceEquals(owned, handle))
            {
                throw new InvalidOperationException(
                    $"Hook '{handle.Id}' is not the registered owner of 0x{handle.Target:X}.");
            }

            var removed = _testRemover is not null
                ? _testRemover(handle.Target)
                : ((delegate* unmanaged[Cdecl]<nint, int>)_removeDetour)(handle.Target) != 0;
            if (!removed)
            {
                throw new InvalidOperationException(
                    $"Native bootstrap could not remove hook '{handle.Id}'.");
            }
            HooksByTarget.Remove(handle.Target);
            handle.MarkRemoved();
        }
    }
}

internal sealed class Il2CppMethodDetour<TDelegate>(
    NativeDetour native,
    nint methodInfo,
    TDelegate replacementDelegate,
    TDelegate originalDelegate) : IIl2CppMethodDetour<TDelegate>
    where TDelegate : Delegate
{
    // Both delegates must remain rooted while native code can enter either thunk.
    private readonly TDelegate _replacementDelegate = replacementDelegate;

    public string Id => native.Id;
    public nint Target => native.Target;
    public nint Replacement => native.Replacement;
    public nint Original => native.Original;
    public bool IsInstalled => native.IsInstalled;
    public nint MethodInfo { get; } = methodInfo;
    public TDelegate OriginalDelegate { get; } = originalDelegate;

    public void Remove()
    {
        native.Remove();
        GC.KeepAlive(_replacementDelegate);
    }

    public void Dispose() => Remove();
}

internal sealed class NativeDetour(
    string ownerId,
    string id,
    nint target,
    nint replacement,
    nint original) : INativeDetour
{
    public string OwnerId { get; } = ownerId;
    public string Id { get; } = id;
    public nint Target { get; } = target;
    public nint Replacement { get; } = replacement;
    public nint Original { get; } = original;
    public bool IsInstalled { get; private set; } = true;

    public void Remove() => HookRuntime.Remove(this);
    public void Dispose() => Remove();
    public void MarkRemoved() => IsInstalled = false;
}
