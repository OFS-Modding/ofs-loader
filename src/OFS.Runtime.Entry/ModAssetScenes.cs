using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets
{
    private static readonly HashSet<string> SceneReservations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ModScene> AllScenes = [];
    private readonly List<ModScene> _scenes = [];
    private SceneBridge? _sceneBridge;

    public IReadOnlyList<IModScene> LoadedScenes =>
        _scenes.Where(scene => scene.IsLive).Cast<IModScene>().ToArray();

    internal static void PollScenes()
    {
        EnsureMainThread();
        foreach (var scene in AllScenes.ToArray())
        {
            try
            {
                scene.Refresh();
                scene.ClearPollingFailure();
            }
            catch (Exception exception)
            {
                scene.RecordPollingFailure(exception);
            }
        }

        foreach (var bundle in AllScenes
                     .Select(scene => scene.RuntimeBundle)
                     .Distinct()
                     .ToArray())
        {
            bundle.CompleteDeferredUnload();
        }
    }

    private SceneBridge Scenes => _sceneBridge ??= new SceneBridge(unsafeApi);

    private IModScene LoadScene(
        ModAssetBundle bundle,
        string scenePath,
        bool asynchronous,
        ModSceneLoadOptions options)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        if (!bundle.ContainsScene(scenePath))
            throw new KeyNotFoundException(
                $"AssetBundle '{bundle.Name}' has no scene '{scenePath}'. Use ScenePaths for exact paths.");
        if (Scenes.IsLoaded(scenePath))
            throw new InvalidOperationException($"Scene '{scenePath}' is already loaded by Unity.");
        if (!SceneReservations.Add(scenePath))
            throw new InvalidOperationException(
                $"Scene '{scenePath}' is already loading or owned by another mod.");

        ModScene? createdScene = null;
        try
        {
            var operation = asynchronous
                ? Scenes.LoadAdditiveAsync(scenePath)
                : Scenes.LoadAdditive(scenePath);
            var scene = new ModScene(
                this,
                bundle,
                Scenes,
                scenePath,
                operation,
                asynchronous,
                options.AllowSceneActivation,
                options.SetActiveWhenLoaded);
            createdScene = scene;
            _scenes.Add(scene);
            AllScenes.Add(scene);
            if (asynchronous)
            {
                scene.AllowSceneActivation = options.AllowSceneActivation;
            }
            else
            {
                scene.CompleteSynchronousLoad();
            }
            logger.Info(
                $"{(asynchronous ? "Loading" : "Loaded")} additive scene '{scenePath}' " +
                $"from AssetBundle '{bundle.Name}'.");
            return scene;
        }
        catch (Exception exception)
        {
            if (createdScene is null)
            {
                SceneReservations.Remove(scenePath);
            }
            else
            {
                try
                {
                    createdScene.Unload();
                }
                catch (Exception cleanupException)
                {
                    logger.Error(
                        cleanupException,
                        $"Scene '{scenePath}' load cleanup could not be initiated; " +
                        "ownership remains reserved.");
                }
                logger.Error(
                    exception,
                    $"Scene '{scenePath}' failed after Unity began loading it; " +
                    "the SDK retained ownership for cleanup.");
            }
            throw;
        }
    }

    private void Release(ModScene scene)
    {
        _scenes.Remove(scene);
        AllScenes.Remove(scene);
        SceneReservations.Remove(scene.ScenePath);
        scene.RuntimeBundle.CompleteDeferredUnload();
    }

    internal bool HasLiveScenes(ModAssetBundle bundle) =>
        _scenes.Any(scene => ReferenceEquals(scene.RuntimeBundle, bundle) && scene.IsLive);

    internal void UnloadScenes(ModAssetBundle bundle)
    {
        foreach (var scene in _scenes
                     .Where(scene => ReferenceEquals(scene.RuntimeBundle, bundle) && scene.IsLive)
                     .ToArray()
                     .Reverse())
        {
            scene.Unload();
        }
    }

    internal sealed partial class ModAssetBundle
    {
        private bool _unloadRequested;
        private bool _unloadLoadedObjects;

        public IReadOnlyList<IModScene> LoadedScenes =>
            _owner._scenes
                .Where(scene => ReferenceEquals(scene.RuntimeBundle, this) && scene.IsLive)
                .Cast<IModScene>()
                .ToArray();

        public IModScene LoadSceneAdditive(string scenePath, bool setActive = false) =>
            _owner.LoadScene(
                this,
                scenePath,
                asynchronous: false,
                new ModSceneLoadOptions(SetActiveWhenLoaded: setActive));

        public IModScene LoadSceneAdditiveAsync(
            string scenePath,
            ModSceneLoadOptions? options = null) =>
            _owner.LoadScene(
                this,
                scenePath,
                asynchronous: true,
                options ?? new ModSceneLoadOptions());
    }

    private sealed class ModScene : IModScene
    {
        private readonly ModAssets _owner;
        private readonly SceneBridge _bridge;
        private nint _operation;
        private bool _operationIsLoad;
        private bool _allowSceneActivation;
        private bool _setActiveWhenLoaded;
        private bool _unloadAfterLoad;
        private string? _previousActiveScenePath;
        private ModSceneStatus _status;
        private int _pollFailures;

        internal ModScene(
            ModAssets owner,
            ModAssetBundle bundle,
            SceneBridge bridge,
            string scenePath,
            nint operation,
            bool asynchronous,
            bool allowSceneActivation,
            bool setActiveWhenLoaded)
        {
            _owner = owner;
            RuntimeBundle = bundle;
            _bridge = bridge;
            ScenePath = scenePath;
            Name = Path.GetFileNameWithoutExtension(scenePath);
            _operation = operation;
            _operationIsLoad = asynchronous;
            _allowSceneActivation = allowSceneActivation;
            _setActiveWhenLoaded = setActiveWhenLoaded;
            _status = asynchronous ? ModSceneStatus.Loading : ModSceneStatus.Loaded;
        }

        public string OwnerId => _owner.ownerId;
        public string ScenePath { get; }
        public string Name { get; }
        public IModAssetBundle Bundle => RuntimeBundle;
        internal ModAssetBundle RuntimeBundle { get; }
        public ModSceneStatus Status => _status;
        public bool IsLoaded => _status == ModSceneStatus.Loaded;
        public bool IsActive => IsLoaded && _bridge.IsActive(ScenePath);
        public int? Handle => IsLoaded ? _bridge.GetHandle(ScenePath) : null;
        public string? Error { get; private set; }
        internal bool IsLive => _status is
            ModSceneStatus.Loading or ModSceneStatus.Loaded or ModSceneStatus.Unloading;

        public float Progress
        {
            get
            {
                EnsureMainThread();
                if (_operation != 0) return _bridge.GetProgress(_operation);
                return _status == ModSceneStatus.Loaded ? 1f : 0f;
            }
        }

        public bool AllowSceneActivation
        {
            get => _allowSceneActivation;
            set
            {
                EnsureMainThread();
                if (_status != ModSceneStatus.Loading || !_operationIsLoad || _operation == 0)
                    throw new InvalidOperationException(
                        "Scene activation can only be changed during an asynchronous load.");
                _bridge.SetAllowSceneActivation(_operation, value);
                _allowSceneActivation = value;
            }
        }

        internal void CompleteSynchronousLoad()
        {
            if (!_bridge.IsLoaded(ScenePath))
                throw new InvalidOperationException(
                    $"Unity returned from loading '{ScenePath}', but the scene is not loaded.");
            if (_setActiveWhenLoaded) SetActiveCore();
        }

        internal void Refresh()
        {
            if (!IsLive) return;
            if (_status == ModSceneStatus.Loading)
            {
                if (_operation == 0 || !_bridge.IsDone(_operation)) return;
                _operation = 0;
                _operationIsLoad = false;
                if (!_bridge.IsLoaded(ScenePath))
                {
                    Fail($"Unity completed loading '{ScenePath}', but no loaded scene was found.");
                    return;
                }
                _status = ModSceneStatus.Loaded;
                if (_setActiveWhenLoaded && !_unloadAfterLoad) SetActiveCore();
                if (_unloadAfterLoad) BeginUnload();
                return;
            }

            if (_status == ModSceneStatus.Loaded && !_bridge.IsLoaded(ScenePath))
            {
                CompleteUnload();
                return;
            }

            if (_status == ModSceneStatus.Unloading && !_bridge.IsLoaded(ScenePath))
                CompleteUnload();
        }

        public void SetActive()
        {
            EnsureMainThread();
            if (_status == ModSceneStatus.Loading)
            {
                _setActiveWhenLoaded = true;
                return;
            }
            if (_status != ModSceneStatus.Loaded)
                throw new InvalidOperationException($"Scene '{ScenePath}' is not loaded.");
            SetActiveCore();
        }

        private void SetActiveCore()
        {
            if (_bridge.IsActive(ScenePath)) return;
            _previousActiveScenePath ??= _bridge.GetActivePath();
            _bridge.SetActive(ScenePath);
        }

        public void Unload()
        {
            EnsureMainThread();
            if (_status is ModSceneStatus.Unloaded or ModSceneStatus.Failed or ModSceneStatus.Unloading)
                return;
            if (_status == ModSceneStatus.Loading)
            {
                _unloadAfterLoad = true;
                if (!_allowSceneActivation)
                {
                    _bridge.SetAllowSceneActivation(_operation, true);
                    _allowSceneActivation = true;
                }
                return;
            }
            BeginUnload();
        }

        private void BeginUnload()
        {
            if (IsActive && !string.IsNullOrWhiteSpace(_previousActiveScenePath) &&
                _bridge.IsLoaded(_previousActiveScenePath!))
            {
                _bridge.SetActive(_previousActiveScenePath!);
            }
            _operation = _bridge.Unload(ScenePath);
            _operationIsLoad = false;
            _status = ModSceneStatus.Unloading;
            _owner.logger.Info($"Unloading additive scene '{ScenePath}'.");
        }

        private void CompleteUnload()
        {
            _operation = 0;
            _status = ModSceneStatus.Unloaded;
            _owner.Release(this);
        }

        internal void Fail(string error)
        {
            if (!IsLive) return;
            Error = error;
            _operation = 0;
            _status = ModSceneStatus.Failed;
            _owner.Release(this);
        }

        internal void RecordPollingFailure(Exception exception)
        {
            Error = $"Scene lifecycle polling failed: {exception.Message}";
            ++_pollFailures;
            if (_pollFailures is 1 or 10 || _pollFailures % 300 == 0)
                _owner.logger.Error(
                    exception,
                    $"Scene '{ScenePath}' polling failed {_pollFailures} time(s); " +
                    "ownership is retained to avoid unloading a live bundle.");
        }

        internal void ClearPollingFailure() => _pollFailures = 0;

        public void Dispose() => Unload();
    }

    private sealed class SceneBridge
    {
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _sceneManagerClass;
        private readonly nint _sceneClass;
        private readonly nint _sceneHandleClass;
        private readonly nint _asyncOperationClass;
        private readonly nint _getSceneByPath;
        private readonly nint _getActiveScene;
        private readonly nint _setActiveScene;
        private readonly nint _loadScene;
        private readonly nint _loadSceneAsync;
        private readonly nint _unloadSceneAsync;
        private readonly nint _sceneIsValid;
        private readonly nint _sceneIsLoaded;
        private readonly nint _scenePath;
        private readonly nint _sceneHandle;
        private readonly nint _sceneHandleToInt;
        private readonly nint _operationIsDone;
        private readonly nint _operationProgress;
        private readonly nint _operationSetAllowActivation;

        internal SceneBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            _sceneManagerClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine.SceneManagement", "SceneManager");
            _sceneClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine.SceneManagement", "Scene");
            _sceneHandleClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine.SceneManagement", "SceneHandle");
            _asyncOperationClass = RequireClass(
                api, "UnityEngine.CoreModule.dll", "UnityEngine", "AsyncOperation");
            _getSceneByPath = RequireMethod(api, _sceneManagerClass, "GetSceneByPath", 1);
            _getActiveScene = RequireMethod(api, _sceneManagerClass, "GetActiveScene", 0);
            _setActiveScene = RequireSignature(
                api, _sceneManagerClass, "SetActiveScene", "UnityEngine.SceneManagement.Scene");
            _loadScene = RequireSignature(
                api,
                _sceneManagerClass,
                "LoadScene",
                "System.String",
                "UnityEngine.SceneManagement.LoadSceneMode");
            _loadSceneAsync = RequireSignature(
                api,
                _sceneManagerClass,
                "LoadSceneAsync",
                "System.String",
                "UnityEngine.SceneManagement.LoadSceneMode");
            _unloadSceneAsync = RequireSignature(
                api, _sceneManagerClass, "UnloadSceneAsync", "UnityEngine.SceneManagement.Scene");
            _sceneIsValid = RequireMethod(api, _sceneClass, "IsValid", 0);
            _sceneIsLoaded = RequireMethod(api, _sceneClass, "get_isLoaded", 0);
            _scenePath = RequireMethod(api, _sceneClass, "get_path", 0);
            _sceneHandle = RequireMethod(api, _sceneClass, "get_handle", 0);
            _sceneHandleToInt = RequireSignature(
                api, _sceneHandleClass, "op_Implicit", "UnityEngine.SceneManagement.SceneHandle");
            _operationIsDone = RequireMethod(api, _asyncOperationClass, "get_isDone", 0);
            _operationProgress = RequireMethod(api, _asyncOperationClass, "get_progress", 0);
            _operationSetAllowActivation = RequireMethod(
                api, _asyncOperationClass, "set_allowSceneActivation", 1);
        }

        internal unsafe nint LoadAdditive(string scenePath)
        {
            var mode = 1;
            nint* arguments = stackalloc nint[2];
            arguments[0] = _api.NewString(scenePath);
            arguments[1] = (nint)(&mode);
            _ = _api.RuntimeInvoke(_loadScene, 0, (nint)arguments);
            return 0;
        }

        internal unsafe nint LoadAdditiveAsync(string scenePath)
        {
            var mode = 1;
            nint* arguments = stackalloc nint[2];
            arguments[0] = _api.NewString(scenePath);
            arguments[1] = (nint)(&mode);
            var operation = _api.RuntimeInvoke(_loadSceneAsync, 0, (nint)arguments);
            return operation != 0
                ? operation
                : throw new InvalidOperationException($"Unity rejected scene load '{scenePath}'.");
        }

        internal bool IsLoaded(string scenePath)
        {
            var scene = GetScene(scenePath);
            var value = scene == 0 ? 0 : _api.Unbox(scene);
            return value != 0 && ReadBoolean(_sceneIsValid, value) &&
                   ReadBoolean(_sceneIsLoaded, value);
        }

        internal bool IsActive(string scenePath)
        {
            var active = _api.RuntimeInvoke(_getActiveScene, 0, 0);
            var target = GetScene(scenePath);
            if (active == 0 || target == 0) return false;
            return ReadHandle(_api.Unbox(active)) == ReadHandle(_api.Unbox(target));
        }

        internal string? GetActivePath()
        {
            var active = _api.RuntimeInvoke(_getActiveScene, 0, 0);
            var value = active == 0 ? 0 : _api.Unbox(active);
            if (value == 0 || !ReadBoolean(_sceneIsValid, value)) return null;
            var path = _api.RuntimeInvoke(_scenePath, value, 0);
            return path == 0 ? null : _api.ReadString(path);
        }

        internal int? GetHandle(string scenePath)
        {
            var scene = GetScene(scenePath);
            var value = scene == 0 ? 0 : _api.Unbox(scene);
            return value == 0 || !ReadBoolean(_sceneIsValid, value)
                ? null
                : ReadHandle(value);
        }

        internal unsafe void SetActive(string scenePath)
        {
            var scene = GetScene(scenePath);
            var value = scene == 0 ? 0 : _api.Unbox(scene);
            if (value == 0 || !ReadBoolean(_sceneIsValid, value) ||
                !ReadBoolean(_sceneIsLoaded, value))
                throw new InvalidOperationException($"Scene '{scenePath}' is not loaded.");
            nint* arguments = stackalloc nint[1];
            arguments[0] = _api.Unbox(scene);
            var result = _api.RuntimeInvoke(_setActiveScene, 0, (nint)arguments);
            if (!ReadBoxedBoolean(result))
                throw new InvalidOperationException($"Unity refused to activate scene '{scenePath}'.");
        }

        internal unsafe nint Unload(string scenePath)
        {
            var scene = GetScene(scenePath);
            var value = scene == 0 ? 0 : _api.Unbox(scene);
            if (value == 0 || !ReadBoolean(_sceneIsValid, value)) return 0;
            nint* arguments = stackalloc nint[1];
            arguments[0] = _api.Unbox(scene);
            return _api.RuntimeInvoke(_unloadSceneAsync, 0, (nint)arguments);
        }

        internal bool IsDone(nint operation) => ReadBoolean(_operationIsDone, operation);

        internal float GetProgress(nint operation)
        {
            var boxed = _api.RuntimeInvoke(_operationProgress, operation, 0);
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value == 0 ? 0 : BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value));
        }

        internal unsafe void SetAllowSceneActivation(nint operation, bool value)
        {
            byte nativeValue = value ? (byte)1 : (byte)0;
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&nativeValue);
            _ = _api.RuntimeInvoke(
                _operationSetAllowActivation, operation, (nint)arguments);
        }

        private unsafe nint GetScene(string scenePath)
        {
            nint* arguments = stackalloc nint[1];
            arguments[0] = _api.NewString(scenePath);
            return _api.RuntimeInvoke(_getSceneByPath, 0, (nint)arguments);
        }

        private bool ReadBoolean(nint method, nint instance) =>
            ReadBoxedBoolean(_api.RuntimeInvoke(method, instance, 0));

        private bool ReadBoxedBoolean(nint boxed)
        {
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private unsafe int ReadHandle(nint sceneValue)
        {
            var boxedHandle = _api.RuntimeInvoke(_sceneHandle, sceneValue, 0);
            var handle = boxedHandle == 0 ? 0 : _api.Unbox(boxedHandle);
            if (handle == 0) return -1;
            nint* arguments = stackalloc nint[1];
            arguments[0] = handle;
            var boxedInt = _api.RuntimeInvoke(_sceneHandleToInt, 0, (nint)arguments);
            var value = boxedInt == 0 ? 0 : _api.Unbox(boxedInt);
            return value == 0 ? -1 : Marshal.ReadInt32(value);
        }

        private static nint RequireClass(
            IUnsafeIl2CppApi api,
            string assembly,
            string namespaze,
            string name)
        {
            var klass = api.FindClass(assembly, namespaze, name);
            return klass != 0
                ? klass
                : throw new NotSupportedException($"Unity class {namespaze}.{name} is unavailable.");
        }

        private static nint RequireSignature(
            IUnsafeIl2CppApi api,
            nint klass,
            string name,
            params string[] parameters)
        {
            var method = api.FindMethodBySignature(klass, name, parameters);
            return method != 0
                ? method
                : throw new MissingMethodException(
                    $"Unity method {name}({string.Join(", ", parameters)}) was not found.");
        }
    }
}
