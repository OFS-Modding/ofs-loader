using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OFS.Runtime.Entry;

public static class BootstrapEntry
{
    private const int Success = 0;
    private const int InitializationFailure = 1;

    [UnmanagedCallersOnly(EntryPoint = "OFS_Initialize", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int Initialize(nint arguments, int argumentSize)
    {
        try
        {
            RuntimeLog.Write("OFS managed runtime entered through hostfxr.");
            var nativeApi = NativeBootstrapApi.Read(arguments, argumentSize);
            HookRuntime.Configure(nativeApi);
            var result = Il2CppProbe.Run();
            RuntimeLog.Write(
                $"IL2CPP probe complete: domain=0x{result.Domain:X}, " +
                $"assemblies={result.AssemblyCount}, Assembly-CSharp={result.HasAssemblyCSharp}, " +
                $"MainMenuManager=0x{result.MainMenuManagerClass:X}, " +
                $"ItemSOManager=0x{result.ItemSoManagerClass:X}.");
            if (!result.IsUsable)
            {
                return InitializationFailure;
            }
            var callback = (delegate* unmanaged[Cdecl]<nint, void>)&OnMainMenuStart;
            var installHook = (delegate* unmanaged[Cdecl]<nint, nint, int>)
                nativeApi.InstallMainMenuStartHook;
            var installed = installHook(
                result.MainMenuManagerStartNative,
                (nint)callback);
            RuntimeLog.Write(installed != 0
                ? $"MainMenuManager.Start hook requested at 0x{result.MainMenuManagerStartNative:X}."
                : "Native bootstrap rejected the MainMenuManager.Start hook.");
            var buttonCallback = (delegate* unmanaged[Cdecl]<nint, int>)&OnButtonPress;
            var installButtonHook = (delegate* unmanaged[Cdecl]<nint, nint, int>)
                nativeApi.InstallButtonPressHook;
            var buttonHookInstalled = installButtonHook(
                UnityUiRuntime.ButtonPressNative,
                (nint)buttonCallback);
            RuntimeLog.Write(buttonHookInstalled != 0
                ? $"Button.Press hook requested at 0x{UnityUiRuntime.ButtonPressNative:X}."
                : "Native bootstrap rejected the Button.Press hook.");
            var uiUpdateCallback = (delegate* unmanaged[Cdecl]<void>)&OnUiUpdate;
            var installUiUpdateHook = (delegate* unmanaged[Cdecl]<nint, nint, int>)
                nativeApi.InstallUiUpdateHook;
            var uiUpdateHookInstalled = installUiUpdateHook(
                UnityUiRuntime.EventSystemUpdateNative,
                (nint)uiUpdateCallback);
            RuntimeLog.Write(uiUpdateHookInstalled != 0
                ? $"EventSystem.Update hook requested at 0x{UnityUiRuntime.EventSystemUpdateNative:X}."
                : "Native bootstrap rejected the EventSystem.Update hook.");
            if (installed != 0 && buttonHookInstalled != 0 && uiUpdateHookInstalled != 0)
            {
                LifecycleRuntime.Configure(result);
                ContentLifecycleRuntime.Configure(result);
                SaveLifecycleRuntime.Configure(result);
                ModRuntime.Initialize(result);
            }
            return installed != 0 && buttonHookInstalled != 0 && uiUpdateHookInstalled != 0
                ? Success
                : InitializationFailure;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Managed runtime initialization failed: {exception}");
            return InitializationFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMainMenuStart(nint instance)
    {
        try
        {
            RuntimeLog.Write(
                $"MainMenuManager.Start hook fired on Unity main thread; instance=0x{instance:X}.");
            UnityUiRuntime.BuildModsMenu(instance);
            ModRuntime.NotifyMainMenuReady();
        }
        catch (Exception exception)
        {
            try
            {
                RuntimeLog.Write($"Mods UI construction failed: {exception}");
            }
            catch
            {
                // Never allow a managed exception to cross the native detour boundary.
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnButtonPress(nint instance)
    {
        try
        {
            return UnityUiRuntime.HandleButtonPress(instance) ? 1 : 0;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Mods button handler failed: {exception}");
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUiUpdate()
    {
        try
        {
            ModRuntime.PumpMainThread();
            ModRuntime.NotifyFrameUpdate(LifecycleRuntime.ReadFrame());
            UnityUiRuntime.PollCloseInput();
        }
        catch
        {
            // The player loop must never observe a managed exception.
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeBootstrapApi
{
    public readonly nuint Size;
    public readonly nint InstallMainMenuStartHook;
    public readonly nint InstallButtonPressHook;
    public readonly nint InstallUiUpdateHook;
    public readonly nint InstallDetour;
    public readonly nint RemoveDetour;

    public static NativeBootstrapApi Read(nint arguments, int argumentSize)
    {
        var expectedSize = Marshal.SizeOf<NativeBootstrapApi>();
        if (arguments == 0 || argumentSize < expectedSize)
        {
            throw new InvalidOperationException("The native bootstrap API is missing or truncated.");
        }

        var api = Marshal.PtrToStructure<NativeBootstrapApi>(arguments);
        if (api.Size < (nuint)expectedSize ||
            api.InstallMainMenuStartHook == 0 ||
            api.InstallButtonPressHook == 0 ||
            api.InstallUiUpdateHook == 0 ||
            api.InstallDetour == 0 ||
            api.RemoveDetour == 0)
        {
            throw new InvalidOperationException("The native bootstrap API is incompatible.");
        }

        return api;
    }
}

internal static class RuntimeLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        var logDirectory = Path.Combine(gameDirectory, "OFS", "logs");
        Directory.CreateDirectory(logDirectory);

        var line = $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            using var stream = new FileStream(
                Path.Combine(logDirectory, "runtime.log"),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }
    }
}

internal sealed record Il2CppProbeResult(
    nint Domain,
    nuint AssemblyCount,
    bool HasAssemblyCSharp,
    nint MainMenuManagerClass,
    nint MainMenuManagerInstanceGetter,
    nint MainMenuManagerStartMethodInfo,
    nint MainMenuManagerStartNative,
    nint ItemSoManagerClass,
    nint ItemSoManagerInstanceGetter,
    nint ItemSoManagerGetAllItems,
    nint GameAssemblyModule,
    IReadOnlyDictionary<string, nint> Images)
{
    public bool IsUsable =>
        HasAssemblyCSharp &&
        MainMenuManagerClass != 0 &&
        MainMenuManagerInstanceGetter != 0 &&
        MainMenuManagerStartMethodInfo != 0 &&
        MainMenuManagerStartNative != 0 &&
        ItemSoManagerClass != 0 &&
        ItemSoManagerInstanceGetter != 0 &&
        ItemSoManagerGetAllItems != 0;
}

internal static class Il2CppProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint DomainGetDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ThreadAttachDelegate(nint domain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint DomainGetAssembliesDelegate(nint domain, out nuint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AssemblyGetImageDelegate(nint assembly);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ImageGetNameDelegate(nint image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ClassFromNameDelegate(
        nint image,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ClassGetMethodFromNameDelegate(
        nint klass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int argumentCount);

    public static Il2CppProbeResult Run()
    {
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The game process path has no directory.");
        var gameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll");
        RuntimeLog.Write("IL2CPP probe: opening the loaded GameAssembly module.");
        var gameAssembly = NativeLibrary.Load(gameAssemblyPath);

        RuntimeLog.Write("IL2CPP probe: resolving required exports.");
        var domainGet = LoadExport<DomainGetDelegate>(gameAssembly, "il2cpp_domain_get");
        var threadAttach = LoadExport<ThreadAttachDelegate>(gameAssembly, "il2cpp_thread_attach");
        var domainGetAssemblies = LoadExport<DomainGetAssembliesDelegate>(
            gameAssembly,
            "il2cpp_domain_get_assemblies");
        var assemblyGetImage = LoadExport<AssemblyGetImageDelegate>(
            gameAssembly,
            "il2cpp_assembly_get_image");
        var imageGetName = LoadExport<ImageGetNameDelegate>(gameAssembly, "il2cpp_image_get_name");
        var classFromName = LoadExport<ClassFromNameDelegate>(gameAssembly, "il2cpp_class_from_name");
        var classGetMethodFromName = LoadExport<ClassGetMethodFromNameDelegate>(
            gameAssembly,
            "il2cpp_class_get_method_from_name");

        RuntimeLog.Write("IL2CPP probe: waiting for the domain.");
        var domain = WaitForDomain(domainGet);
        RuntimeLog.Write($"IL2CPP probe: domain ready at 0x{domain:X}; attaching thread.");
        _ = threadAttach(domain);

        RuntimeLog.Write("IL2CPP probe: enumerating assemblies.");
        var assemblies = domainGetAssemblies(domain, out var assemblyCount);
        if (assemblies == 0 || assemblyCount == 0)
        {
            throw new InvalidOperationException("IL2CPP returned no loaded assemblies.");
        }

        var hasAssemblyCSharp = false;
        nint assemblyCSharpImage = 0;
        var images = new Dictionary<string, nint>(StringComparer.Ordinal);
        for (nuint index = 0; index < assemblyCount; ++index)
        {
            var assembly = Marshal.ReadIntPtr(assemblies, checked((int)(index * (nuint)IntPtr.Size)));
            var image = assemblyGetImage(assembly);
            var namePointer = imageGetName(image);
            var name = Marshal.PtrToStringUTF8(namePointer);
            if (name is not null)
            {
                images[name] = image;
            }
            if (string.Equals(name, "Assembly-CSharp.dll", StringComparison.Ordinal))
            {
                hasAssemblyCSharp = true;
                assemblyCSharpImage = image;
            }
        }

        var mainMenuManager = assemblyCSharpImage != 0
            ? classFromName(assemblyCSharpImage, string.Empty, "MainMenuManager")
            : 0;
        var itemSoManager = assemblyCSharpImage != 0
            ? classFromName(assemblyCSharpImage, string.Empty, "ItemSOManager")
            : 0;

        var mainMenuStartMethod = mainMenuManager != 0
            ? classGetMethodFromName(mainMenuManager, "Start", 0)
            : 0;
        var mainMenuStartNative = mainMenuStartMethod != 0
            ? Marshal.ReadIntPtr(mainMenuStartMethod)
            : 0;

        UnityUiRuntime.Configure(images, mainMenuManager);

        return new Il2CppProbeResult(
            domain,
            assemblyCount,
            hasAssemblyCSharp,
            mainMenuManager,
            mainMenuManager != 0 ? classGetMethodFromName(mainMenuManager, "get_Instance", 0) : 0,
            mainMenuStartMethod,
            mainMenuStartNative,
            itemSoManager,
            itemSoManager != 0 ? classGetMethodFromName(itemSoManager, "get_Instance", 0) : 0,
            itemSoManager != 0 ? classGetMethodFromName(itemSoManager, "GetAllItemSOs", 0) : 0,
            gameAssembly,
            images);
    }

    private static nint WaitForDomain(DomainGetDelegate domainGet)
    {
        for (var attempt = 0; attempt < 120; ++attempt)
        {
            var domain = domainGet();
            if (domain != 0)
            {
                return domain;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("IL2CPP domain was not ready after 30 seconds.");
    }

    private static T LoadExport<T>(nint module, string name) where T : Delegate
    {
        var pointer = NativeLibrary.GetExport(module, name);
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }
}
