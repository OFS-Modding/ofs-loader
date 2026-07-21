using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class UnityApi(IUnsafeIl2CppApi unsafeApi) : IUnityApi
{
    public UnityObject CreateGameObject(string name, UnityObject parent = default)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var gameObjectClass = RequireClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "GameObject");
        var constructor = unsafeApi.FindMethodBySignature(
            gameObjectClass,
            ".ctor",
            ["System.String"]);
        if (constructor == 0)
            throw new MissingMethodException("UnityEngine.GameObject..ctor(System.String)");
        var pointer = unsafeApi.NewObject(gameObjectClass);
        if (pointer == 0)
            throw new InvalidOperationException("UnityEngine.GameObject allocation returned null.");
        _ = unsafeApi.Invoke(
            constructor,
            pointer,
            Il2CppArgument.FromReference(unsafeApi.NewString(name)));
        var result = new UnityObject(pointer);
        if (!parent.IsNull) SetParent(result, parent, worldPositionStays: false);
        return result;
    }

    public UnityObject FindActiveGameObject(string name)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new UnityObject(UnityUiRuntime.FindActiveGameObjectPointer(name));
    }

    public UnityObject FindChild(UnityObject parent, string name, bool recursive = true)
    {
        EnsureMainThread();
        EnsureNotNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new UnityObject(UnityUiRuntime.FindChildPointer(parent.Pointer, name, recursive));
    }

    public UnityObject CloneGameObject(UnityObject original, UnityObject parent = default)
    {
        EnsureMainThread();
        EnsureNotNull(original);
        return new UnityObject(
            UnityUiRuntime.CloneGameObjectPointer(original.Pointer, parent.Pointer));
    }

    public UnityObject Instantiate(
        UnityObject prefab,
        UnityVector3 position,
        UnityQuaternion rotation,
        UnityObject parent = default)
    {
        EnsureMainThread();
        EnsureNotNull(prefab);
        return new UnityObject(UnityUiRuntime.InstantiateGameObjectPointer(
            prefab.Pointer,
            position,
            rotation,
            parent.Pointer));
    }

    public UnityObject GetComponent(
        UnityObject gameObject,
        string assemblyName,
        string namespaze,
        string className)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        var klass = RequireClass(assemblyName, namespaze, className);
        return new UnityObject(UnityUiRuntime.GetComponentPointer(gameObject.Pointer, klass));
    }

    public UnityObject TryGetComponent(
        UnityObject gameObject,
        string assemblyName,
        string namespaze,
        string className)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        var klass = RequireClass(assemblyName, namespaze, className);
        return new UnityObject(UnityUiRuntime.TryGetComponentPointer(gameObject.Pointer, klass));
    }

    public IReadOnlyList<UnityObject> FindComponents(
        string assemblyName,
        string namespaze,
        string className,
        bool activeOnly = true)
    {
        EnsureMainThread();
        var klass = RequireClass(assemblyName, namespaze, className);
        return UnityUiRuntime.FindLoadedComponentPointers(klass, unsafeApi, activeOnly)
            .Select(pointer => new UnityObject(pointer))
            .ToArray();
    }

    public UnityObject AddComponent(
        UnityObject gameObject,
        string assemblyName,
        string namespaze,
        string className)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        var klass = RequireClass(assemblyName, namespaze, className);
        return new UnityObject(UnityUiRuntime.AddComponentPointer(gameObject.Pointer, klass));
    }

    public UnityObject GetGameObject(UnityObject component)
    {
        EnsureMainThread();
        EnsureNotNull(component);
        var componentClass = RequireClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Component");
        var getter = unsafeApi.FindMethod(componentClass, "get_gameObject", 0);
        if (getter == 0)
            throw new MissingMethodException("UnityEngine.Component.get_gameObject");
        return new UnityObject(unsafeApi.RuntimeInvoke(getter, component.Pointer, 0));
    }

    public string GetName(UnityObject instance)
    {
        EnsureMainThread();
        EnsureNotNull(instance);
        return UnityUiRuntime.GetObjectNameForSdk(instance.Pointer);
    }

    public void SetName(UnityObject instance, string name)
    {
        EnsureMainThread();
        EnsureNotNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UnityUiRuntime.SetObjectNameForSdk(instance.Pointer, name);
    }

    public void SetActive(UnityObject gameObject, bool active)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        UnityUiRuntime.SetActiveForSdk(gameObject.Pointer, active);
    }

    public void SetText(UnityObject gameObjectWithTextMeshPro, string text)
    {
        EnsureMainThread();
        EnsureNotNull(gameObjectWithTextMeshPro);
        ArgumentNullException.ThrowIfNull(text);
        UnityUiRuntime.SetTextForSdk(gameObjectWithTextMeshPro.Pointer, text);
    }

    public UnityTransform GetTransform(UnityObject gameObject)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        return UnityUiRuntime.GetTransformForSdk(gameObject.Pointer);
    }

    public void SetTransform(UnityObject gameObject, UnityTransform transform)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        UnityUiRuntime.SetTransformForSdk(gameObject.Pointer, transform);
    }

    public void SetParent(UnityObject gameObject, UnityObject parent, bool worldPositionStays = true)
    {
        EnsureMainThread();
        EnsureNotNull(gameObject);
        UnityUiRuntime.SetParentForSdk(gameObject.Pointer, parent.Pointer, worldPositionStays);
    }

    public void DontDestroyOnLoad(UnityObject instance)
    {
        EnsureMainThread();
        EnsureNotNull(instance);
        UnityUiRuntime.DontDestroyOnLoadForSdk(instance.Pointer);
    }

    public void Destroy(UnityObject instance)
    {
        EnsureMainThread();
        EnsureNotNull(instance);
        UnityUiRuntime.DestroyForSdk(instance.Pointer);
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
        {
            throw new InvalidOperationException(
                "Unity API calls must run on the Unity main thread. Use context.MainThread.Post().");
        }
    }

    private static void EnsureNotNull(UnityObject instance)
    {
        if (instance.IsNull)
        {
            throw new ArgumentException("Unity object handle is null.");
        }
    }

    private nint RequireClass(string assemblyName, string namespaze, string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        var klass = unsafeApi.FindClass(assemblyName, namespaze, className);
        return klass != 0
            ? klass
            : throw new InvalidOperationException(
                $"IL2CPP class '{namespaze}.{className}' was not found in '{assemblyName}'.");
    }
}
