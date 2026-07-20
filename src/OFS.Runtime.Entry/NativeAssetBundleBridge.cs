using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

/// <summary>
/// Calls UnityPlayer's generated AssetBundle bindings when IL2CPP stripping has
/// removed the managed UnityEngine.AssetBundle class from the player metadata.
/// </summary>
internal sealed class NativeAssetBundleBridge
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ManagedSpanWrapper(nint begin, int length)
    {
        internal readonly nint Begin = begin;
        internal readonly int Length = length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ResolveIcallDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LoadFromFileDelegate(
        ref ManagedSpanWrapper path,
        uint crc,
        ulong offset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetStringArrayDelegate(nint unitySelf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LoadAssetDelegate(
        nint unitySelf,
        ref ManagedSpanWrapper name,
        nint type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UnloadDelegate(
        nint unitySelf,
        [MarshalAs(UnmanagedType.I1)] bool unloadAllLoadedObjects);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetGcHandleTargetDelegate(nint handle);

    private readonly LoadFromFileDelegate _loadFromFile;
    private readonly GetStringArrayDelegate _getAllAssetNames;
    private readonly GetStringArrayDelegate _getAllScenePaths;
    private readonly LoadAssetDelegate _loadAsset;
    private readonly UnloadDelegate _unload;
    private readonly GetGcHandleTargetDelegate _getGcHandleTarget;
    private readonly int _cachedPointerOffset;

    internal NativeAssetBundleBridge(IUnsafeIl2CppApi api, nint unityObjectClass)
    {
        var export = NativeLibrary.GetExport(api.GameAssemblyModule, "il2cpp_resolve_icall");
        var resolve = Marshal.GetDelegateForFunctionPointer<ResolveIcallDelegate>(export);
        _loadFromFile = Resolve<LoadFromFileDelegate>(
            resolve, "UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
        _getAllAssetNames = Resolve<GetStringArrayDelegate>(
            resolve, "UnityEngine.AssetBundle::GetAllAssetNames_Injected");
        _getAllScenePaths = Resolve<GetStringArrayDelegate>(
            resolve, "UnityEngine.AssetBundle::GetAllScenePaths_Injected");
        _loadAsset = Resolve<LoadAssetDelegate>(
            resolve, "UnityEngine.AssetBundle::LoadAsset_Internal_Injected");
        _unload = Resolve<UnloadDelegate>(
            resolve, "UnityEngine.AssetBundle::Unload_Injected");
        _getGcHandleTarget = Marshal.GetDelegateForFunctionPointer<GetGcHandleTargetDelegate>(
            NativeLibrary.GetExport(api.GameAssemblyModule, "il2cpp_gchandle_get_target"));
        var cachedPointer = api.FindField(unityObjectClass, "m_CachedPtr");
        if (cachedPointer == 0)
            throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
        _cachedPointerOffset = api.GetFieldOffset(cachedPointer);
    }

    internal unsafe nint LoadFromFile(string path)
    {
        fixed (char* characters = path)
        {
            var span = new ManagedSpanWrapper((nint)characters, path.Length);
            var handle = _loadFromFile(ref span, 0, 0);
            if (handle == 0) return 0;
            _ = UnmarshalUnityObject(handle, "AssetBundle.LoadFromFile", out var nativeBundle);
            return nativeBundle;
        }
    }

    internal nint GetAllAssetNames(nint bundle) => _getAllAssetNames(bundle);
    internal nint GetAllScenePaths(nint bundle) => _getAllScenePaths(bundle);

    internal unsafe nint LoadAsset(nint bundle, string name, nint type)
    {
        nint handle;
        fixed (char* characters = name)
        {
            var span = new ManagedSpanWrapper((nint)characters, name.Length);
            handle = _loadAsset(bundle, ref span, type);
        }
        return handle == 0
            ? 0
            : UnmarshalUnityObject(handle, $"AssetBundle.LoadAsset('{name}')", out _);
    }

    internal void Unload(nint bundle, bool unloadAllLoadedObjects) =>
        _unload(bundle, unloadAllLoadedObjects);

    private nint UnmarshalUnityObject(
        nint gcHandle,
        string operation,
        out nint nativeObject)
    {
        var managedObject = _getGcHandleTarget(gcHandle);
        if (managedObject == 0)
            throw new InvalidDataException(
                $"{operation} returned a GCHandle without an IL2CPP target.");
        nativeObject = Marshal.ReadIntPtr(managedObject, _cachedPointerOffset);
        if (nativeObject == 0)
            throw new InvalidDataException(
                $"{operation} materialized a destroyed Unity object.");
        return managedObject;
    }

    private static T Resolve<T>(ResolveIcallDelegate resolve, string name)
        where T : Delegate
    {
        var pointer = resolve(name);
        return pointer != 0
            ? Marshal.GetDelegateForFunctionPointer<T>(pointer)
            : throw new MissingMethodException($"Native Unity icall '{name}' was not resolved.");
    }
}
