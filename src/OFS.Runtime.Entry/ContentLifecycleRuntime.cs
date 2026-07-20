using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class ContentLifecycleRuntime
{
    private static nint _awakeOriginal;
    private static NativeDetour? _awakeHook;

    public static unsafe void Configure(Il2CppProbeResult probe)
    {
        var api = new UnsafeIl2CppApi(
            probe.GameAssemblyModule,
            probe.Domain,
            probe.Images);
        var awake = api.FindMethod(probe.ItemSoManagerClass, "Awake", 0);
        var target = api.GetMethodPointer(awake);
        if (awake == 0 || target == 0)
        {
            throw new MissingMethodException("ItemSOManager.Awake/0 was not found.");
        }

        _awakeHook = HookRuntime.Install(
            "ofs.framework",
            new NativeDetourDefinition(
                "framework.content-ready",
                target,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnItemManagerAwake));
        _awakeOriginal = _awakeHook.Original;
        RuntimeLog.Write($"Content lifecycle hook installed at 0x{target:X}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnItemManagerAwake(nint instance, nint methodInfo)
    {
        try
        {
            var original = (delegate* unmanaged[Cdecl]<nint, nint, void>)_awakeOriginal;
            original(instance, methodInfo);
            ModRuntime.NotifyContentReady();
        }
        catch (Exception exception)
        {
            try
            {
                RuntimeLog.Write($"Content-ready bridge failed: {exception}");
            }
            catch
            {
                // Managed exceptions must not cross the native detour boundary.
            }
        }
    }
}
