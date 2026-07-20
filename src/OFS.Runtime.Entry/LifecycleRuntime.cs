using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class LifecycleRuntime
{
    private static IUnsafeIl2CppApi? _unsafeApi;
    private static nint _getSceneName;
    private static nint _sceneLoadedOriginal;
    private static nint _sceneUnloadedOriginal;
    private static nint _getDeltaTime;
    private static nint _getUnscaledDeltaTime;
    private static nint _getFrameCount;
    private static NativeDetour? _sceneLoadedHook;
    private static NativeDetour? _sceneUnloadedHook;

    public static unsafe void Configure(Il2CppProbeResult probe)
    {
        _unsafeApi = new UnsafeIl2CppApi(
            probe.GameAssemblyModule,
            probe.Domain,
            probe.Images);
        var sceneManagerClass = RequireClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine.SceneManagement",
            "SceneManager");
        var sceneClass = RequireClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine.SceneManagement",
            "Scene");
        var timeClass = RequireClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Time");

        var loadedMethod = RequireMethod(sceneManagerClass, "Internal_SceneLoaded", 2);
        var unloadedMethod = RequireMethod(sceneManagerClass, "Internal_SceneUnloaded", 1);
        _getSceneName = FindMethod(sceneClass, "GetNameInternal", 1);
        _getDeltaTime = RequireMethod(timeClass, "get_deltaTime", 0);
        _getUnscaledDeltaTime = RequireMethod(timeClass, "get_unscaledDeltaTime", 0);
        _getFrameCount = RequireMethod(timeClass, "get_frameCount", 0);

        _sceneLoadedHook = HookRuntime.Install(
            "ofs.framework",
            new NativeDetourDefinition(
                "framework.scene-loaded",
                RequireMethodPointer(loadedMethod),
                (nint)(delegate* unmanaged[Cdecl]<int, int, nint, void>)&OnSceneLoaded));
        _sceneLoadedOriginal = _sceneLoadedHook.Original;

        try
        {
            _sceneUnloadedHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.scene-unloaded",
                    RequireMethodPointer(unloadedMethod),
                    (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&OnSceneUnloaded));
            _sceneUnloadedOriginal = _sceneUnloadedHook.Original;
        }
        catch
        {
            _sceneLoadedHook.Remove();
            _sceneLoadedHook = null;
            _sceneLoadedOriginal = 0;
            throw;
        }

        RuntimeLog.Write(
            $"Scene lifecycle hooks installed: loaded=0x{_sceneLoadedHook.Target:X}, " +
            $"unloaded=0x{_sceneUnloadedHook.Target:X}.");
    }

    public static FrameEvent ReadFrame()
    {
        var api = _unsafeApi
            ?? throw new InvalidOperationException("Lifecycle runtime is not configured.");
        return new FrameEvent(
            ReadBoxedInt32(api, _getFrameCount),
            ReadBoxedSingle(api, _getDeltaTime),
            ReadBoxedSingle(api, _getUnscaledDeltaTime));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSceneLoaded(int sceneHandle, int loadMode, nint methodInfo)
    {
        try
        {
            var original = (delegate* unmanaged[Cdecl]<int, int, nint, void>)_sceneLoadedOriginal;
            original(sceneHandle, loadMode, methodInfo);
            ModRuntime.NotifySceneLoaded(new SceneEvent(
                sceneHandle,
                TryGetSceneName(sceneHandle),
                Enum.IsDefined(typeof(SceneLoadMode), loadMode)
                    ? (SceneLoadMode)loadMode
                    : null,
                loadMode));
        }
        catch (Exception exception)
        {
            SafeLog($"Scene-loaded bridge failed: {exception}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSceneUnloaded(int sceneHandle, nint methodInfo)
    {
        var name = TryGetSceneName(sceneHandle);
        try
        {
            var original = (delegate* unmanaged[Cdecl]<int, nint, void>)_sceneUnloadedOriginal;
            original(sceneHandle, methodInfo);
            ModRuntime.NotifySceneUnloaded(new SceneEvent(sceneHandle, name, null, -1));
        }
        catch (Exception exception)
        {
            SafeLog($"Scene-unloaded bridge failed: {exception}");
        }
    }

    private static unsafe string? TryGetSceneName(int sceneHandle)
    {
        if (_unsafeApi is null || _getSceneName == 0)
        {
            return null;
        }
        try
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&sceneHandle);
            var value = _unsafeApi.RuntimeInvoke(_getSceneName, 0, (nint)arguments);
            return value == 0 ? null : _unsafeApi.ReadString(value);
        }
        catch
        {
            return null;
        }
    }

    private static int ReadBoxedInt32(IUnsafeIl2CppApi api, nint method)
    {
        var boxed = api.RuntimeInvoke(method, 0, 0);
        var value = api.Unbox(boxed);
        return value != 0
            ? Marshal.ReadInt32(value)
            : throw new InvalidDataException("Unity Time value could not be unboxed.");
    }

    private static float ReadBoxedSingle(IUnsafeIl2CppApi api, nint method) =>
        BitConverter.Int32BitsToSingle(ReadBoxedInt32(api, method));

    private static nint RequireClass(string assembly, string namespaze, string name)
    {
        var klass = _unsafeApi!.FindClass(assembly, namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Unity class '{namespaze}.{name}' was not found.");
    }

    private static nint FindMethod(nint klass, string name, int arguments) =>
        _unsafeApi!.FindMethod(klass, name, arguments);

    private static nint RequireMethod(nint klass, string name, int arguments)
    {
        var method = FindMethod(klass, name, arguments);
        return method != 0
            ? method
            : throw new MissingMethodException($"Unity method '{name}/{arguments}' was not found.");
    }

    private static nint RequireMethodPointer(nint method)
    {
        var pointer = _unsafeApi!.GetMethodPointer(method);
        return pointer != 0
            ? pointer
            : throw new MissingMethodException("Unity method has no native pointer.");
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
