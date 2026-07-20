using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class SaveLifecycleRuntime
{
    private static IUnsafeIl2CppApi? _unsafeApi;
    private static nint _managerClass;
    private static nint _afterSaveOriginal;
    private static nint _loadCompleteOriginal;
    private static nint _isSinglePlayerModeGetter;
    private static nint _isLoadPendingGetter;
    private static NativeDetour? _afterSaveHook;
    private static NativeDetour? _loadCompleteHook;
    private static bool _factorySceneActive;
    private static bool _loadDeliveredForFactoryScene;
    private static bool _loadDeliveredBeforeFactoryScene;
    private static bool _fallbackPending;
    private static int _fallbackStartFrame = -1;
    private static string? _lastFallbackError;

    private const int FallbackDelayFrames = 30;
    private const int FallbackTimeoutFrames = 1800;

    internal static bool IsSinglePlayerMode()
    {
        if (_unsafeApi is null || _isSinglePlayerModeGetter == 0)
        {
            return false;
        }

        return ReadStaticBoolean(_isSinglePlayerModeGetter);
    }

    public static unsafe void Configure(Il2CppProbeResult probe)
    {
        _unsafeApi = new UnsafeIl2CppApi(
            probe.GameAssemblyModule,
            probe.Domain,
            probe.Images);
        _managerClass = _unsafeApi.FindClass(
            "Assembly-CSharp.dll",
            string.Empty,
            "SaveLoadGameManager");
        if (_managerClass == 0)
        {
            throw new TypeLoadException("SaveLoadGameManager was not found.");
        }

        var afterSaveTarget = RequireMethodPointer("OnAfterSave", 1);
        var loadCompleteTarget = RequireMethodPointer("NotifyLoadComplete", 0);
        _isSinglePlayerModeGetter = RequireMethod("get_IsSinglePlayerMode", 0);
        _isLoadPendingGetter = RequireMethod("get_IsLoadPendingOrInProgress", 0);
        _afterSaveHook = HookRuntime.Install(
            "ofs.framework",
            new NativeDetourDefinition(
                "framework.after-save",
                afterSaveTarget,
                (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnAfterSave));
        _afterSaveOriginal = _afterSaveHook.Original;
        try
        {
            _loadCompleteHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.load-complete",
                    loadCompleteTarget,
                    (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnLoadComplete));
            _loadCompleteOriginal = _loadCompleteHook.Original;
        }
        catch
        {
            _afterSaveHook.Remove();
            _afterSaveHook = null;
            _afterSaveOriginal = 0;
            throw;
        }

        RuntimeLog.Write(
            $"Save lifecycle hooks installed: afterSave=0x{afterSaveTarget:X}, " +
            $"loadComplete=0x{loadCompleteTarget:X}.");
    }

    public static void NotifySceneLoaded(SceneEvent scene)
    {
        if (string.Equals(scene.Name, "Main Menu", StringComparison.Ordinal))
        {
            ResetLoadFallback();
            return;
        }
        if (!string.Equals(scene.Name, "Factory", StringComparison.Ordinal))
        {
            return;
        }

        _factorySceneActive = true;
        _loadDeliveredForFactoryScene = _loadDeliveredBeforeFactoryScene;
        _loadDeliveredBeforeFactoryScene = false;
        _fallbackPending = !_loadDeliveredForFactoryScene;
        _fallbackStartFrame = -1;
        _lastFallbackError = null;
        RuntimeLog.Write(_fallbackPending
            ? "Factory scene armed the single-player save-load fallback."
            : "Factory scene matched an earlier SaveLoadGameManager.NotifyLoadComplete callback.");
    }

    public static void NotifySceneUnloaded(SceneEvent scene)
    {
        if (string.Equals(scene.Name, "Factory", StringComparison.Ordinal))
        {
            ResetLoadFallback();
        }
    }

    public static void Poll(FrameEvent frame)
    {
        if (!_fallbackPending || _loadDeliveredForFactoryScene)
        {
            return;
        }

        if (_fallbackStartFrame < 0)
        {
            _fallbackStartFrame = frame.FrameCount;
            return;
        }

        var elapsed = unchecked(frame.FrameCount - _fallbackStartFrame);
        if (elapsed < FallbackDelayFrames)
        {
            return;
        }

        try
        {
            if (!ReadStaticBoolean(_isSinglePlayerModeGetter) ||
                ReadStaticBoolean(_isLoadPendingGetter))
            {
                if (elapsed >= FallbackTimeoutFrames)
                {
                    RuntimeLog.Write(
                        "Single-player save-load fallback timed out while vanilla still " +
                        "reported multiplayer or pending load operations.");
                    _fallbackPending = false;
                }
                return;
            }

            DispatchLoadCompleted("Factory readiness fallback");
        }
        catch (Exception exception)
        {
            _lastFallbackError = exception.Message;
            if (elapsed >= FallbackTimeoutFrames)
            {
                RuntimeLog.Write(
                    "Single-player save-load fallback timed out: " + _lastFallbackError);
                _fallbackPending = false;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAfterSave(nint instance, int slot, nint methodInfo)
    {
        try
        {
            var original = (delegate* unmanaged[Cdecl]<nint, int, nint, void>)_afterSaveOriginal;
            original(instance, slot, methodInfo);
            ModRuntime.NotifySaveCompleted(slot);
        }
        catch (Exception exception)
        {
            SafeLog($"After-save bridge failed: {exception}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLoadComplete(nint methodInfo)
    {
        try
        {
            var original = (delegate* unmanaged[Cdecl]<nint, void>)_loadCompleteOriginal;
            original(methodInfo);
            DispatchLoadCompleted("SaveLoadGameManager.NotifyLoadComplete");
        }
        catch (Exception exception)
        {
            SafeLog($"Load-complete bridge failed: {exception}");
        }
    }

    private static void DispatchLoadCompleted(string source)
    {
        if (_factorySceneActive && _loadDeliveredForFactoryScene)
        {
            RuntimeLog.Write($"Ignored duplicate mod load lifecycle from {source}.");
            return;
        }
        if (!_factorySceneActive && _loadDeliveredBeforeFactoryScene)
        {
            RuntimeLog.Write($"Ignored duplicate pre-Factory mod load lifecycle from {source}.");
            return;
        }

        var slot = GetCurrentSlot();
        ModRuntime.NotifyLoadCompleted(slot);
        _fallbackPending = false;
        if (_factorySceneActive)
        {
            _loadDeliveredForFactoryScene = true;
        }
        else
        {
            _loadDeliveredBeforeFactoryScene = true;
        }
        RuntimeLog.Write($"Mod load lifecycle delivered for slot {slot} via {source}.");
    }

    private static int GetCurrentSlot()
    {
        var getInstance = RequireMethod("get_Instance", 0);
        var instance = _unsafeApi!.RuntimeInvoke(getInstance, 0, 0);
        if (instance == 0)
        {
            throw new InvalidOperationException("SaveLoadGameManager.Instance is unavailable.");
        }
        var boxed = _unsafeApi.RuntimeInvoke(
            RequireMethod("GetCurrentSaveSlot", 0),
            instance,
            0);
        var value = _unsafeApi.Unbox(boxed);
        return value != 0
            ? Marshal.ReadInt32(value)
            : throw new InvalidDataException("Current save slot could not be unboxed.");
    }

    private static bool ReadStaticBoolean(nint method)
    {
        var boxed = _unsafeApi!.RuntimeInvoke(method, 0, 0);
        var value = _unsafeApi.Unbox(boxed);
        return value != 0
            ? Marshal.ReadByte(value) != 0
            : throw new InvalidDataException("Static save lifecycle flag could not be unboxed.");
    }

    private static void ResetLoadFallback()
    {
        _factorySceneActive = false;
        _loadDeliveredForFactoryScene = false;
        _loadDeliveredBeforeFactoryScene = false;
        _fallbackPending = false;
        _fallbackStartFrame = -1;
        _lastFallbackError = null;
    }

    private static nint RequireMethod(string name, int argumentCount)
    {
        var method = _unsafeApi!.FindMethod(_managerClass, name, argumentCount);
        return method != 0
            ? method
            : throw new MissingMethodException($"SaveLoadGameManager.{name}/{argumentCount} was not found.");
    }

    private static nint RequireMethodPointer(string name, int argumentCount)
    {
        var pointer = _unsafeApi!.GetMethodPointer(RequireMethod(name, argumentCount));
        return pointer != 0
            ? pointer
            : throw new MissingMethodException(
                $"SaveLoadGameManager.{name}/{argumentCount} has no native pointer.");
    }

    private static void SafeLog(string message)
    {
        try
        {
            RuntimeLog.Write(message);
        }
        catch
        {
            // Managed exceptions must not cross the native detour boundary.
        }
    }
}
