using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class PlayerApi(IUnsafeIl2CppApi api, IUnityApi unity) : IPlayerApi
{
    private readonly nint _playerClass = RequireClass(api, "GamePlayer");
    private readonly nint _isLocalPlayer = RequireMethod(
        api,
        RequireClass(api, "Mirror.dll", "Mirror", "NetworkBehaviour"),
        "get_isLocalPlayer",
        0);
    private readonly nint _isInDigsiteField = RequireField(
        api,
        RequireClass(api, "GamePlayer"),
        "isInDigsite");
    private readonly nint _setIsInDigsite = RequireMethod(
        api,
        RequireClass(api, "GamePlayer"),
        "SetIsInDigsite",
        1);
    private readonly nint _networkTeleport = api.FindMethodBySignature(
        RequireClass(api, "GamePlayer"),
        "NetworkTeleport",
        ["UnityEngine.Vector3", "UnityEngine.Quaternion"]);

    public IPlayer? Local => GetLoaded().FirstOrDefault(player => player.IsLocal);

    public IReadOnlyList<IPlayer> GetLoaded(bool activeOnly = true)
    {
        EnsureMainThread();
        if (_networkTeleport == 0)
            throw new MissingMethodException(
                "GamePlayer.NetworkTeleport(UnityEngine.Vector3,UnityEngine.Quaternion)");
        return unity.FindComponents(
                "Assembly-CSharp.dll",
                string.Empty,
                "GamePlayer",
                activeOnly)
            .Select(component => (IPlayer)new Player(this, unity.GetGameObject(component), component))
            .ToArray();
    }

    private bool IsLocal(UnityObject component) => ReadBool(api.RuntimeInvoke(
        _isLocalPlayer,
        component.Pointer,
        0));

    private bool IsInDigsite(UnityObject component) =>
        api.ReadBoolean(component.Pointer, _isInDigsiteField);

    private void SetIsInDigsite(UnityObject component, bool value)
    {
        EnsureMainThread();
        _ = api.Invoke(
            _setIsInDigsite,
            component.Pointer,
            Il2CppArgument.FromBoolean(value));
    }

    private void Teleport(
        UnityObject component,
        UnityVector3 position,
        UnityQuaternion rotation)
    {
        EnsureMainThread();
        _ = api.Invoke(
            _networkTeleport,
            component.Pointer,
            Il2CppArgument.FromValue(new NativeVector3(position.X, position.Y, position.Z)),
            Il2CppArgument.FromValue(new NativeQuaternion(
                rotation.X,
                rotation.Y,
                rotation.Z,
                rotation.W)));
    }

    private UnityTransform GetTransform(UnityObject gameObject) =>
        unity.GetTransform(gameObject);

    private bool ReadBool(nint boxed)
    {
        var value = api.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Player API calls must run on Unity's main thread.");
    }

    private static nint RequireClass(IUnsafeIl2CppApi unsafeApi, string name) =>
        RequireClass(unsafeApi, "Assembly-CSharp.dll", string.Empty, name);

    private static nint RequireClass(
        IUnsafeIl2CppApi unsafeApi,
        string assembly,
        string namespaze,
        string name)
    {
        var klass = unsafeApi.FindClass(assembly, namespaze, name);
        return klass != 0 ? klass : throw new TypeLoadException($"{namespaze}.{name}");
    }

    private static nint RequireMethod(
        IUnsafeIl2CppApi unsafeApi,
        nint klass,
        string name,
        int count)
    {
        var method = unsafeApi.FindMethod(klass, name, count);
        return method != 0 ? method : throw new MissingMethodException(name);
    }

    private static nint RequireField(IUnsafeIl2CppApi unsafeApi, nint klass, string name)
    {
        var field = unsafeApi.FindField(klass, name);
        return field != 0 ? field : throw new MissingFieldException(name);
    }

    private readonly record struct NativeVector3(float X, float Y, float Z);
    private readonly record struct NativeQuaternion(float X, float Y, float Z, float W);

    private sealed class Player(
        PlayerApi owner,
        UnityObject gameObject,
        UnityObject component) : IPlayer
    {
        public UnityObject GameObject => gameObject;
        public UnityObject Component => component;
        public bool IsLocal => owner.IsLocal(component);
        public bool IsInDigsite
        {
            get => owner.IsInDigsite(component);
            set => owner.SetIsInDigsite(component, value);
        }
        public UnityTransform Transform => owner.GetTransform(gameObject);
        public void Teleport(UnityVector3 position, UnityQuaternion rotation) =>
            owner.Teleport(component, position, rotation);
    }
}
