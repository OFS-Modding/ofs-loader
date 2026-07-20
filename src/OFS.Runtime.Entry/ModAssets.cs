using System.Security.Cryptography;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets : IModAssets
{
    private const string SupportedUnityVersion = "6000.3.13f1";
    private const string SupportedBuildTarget = "StandaloneWindows64";
    private readonly List<ModAssetBundle> _bundles = [];
    private readonly List<ModAssetBundleSet> _sets = [];
    private readonly string ownerId;
    private readonly string modDirectory;
    private readonly IUnsafeIl2CppApi unsafeApi;
    private readonly IModLogger logger;

    internal ModAssets(
        string ownerId,
        string modDirectory,
        IUnsafeIl2CppApi unsafeApi,
        IModLogger logger)
    {
        this.ownerId = ownerId;
        this.modDirectory = modDirectory;
        this.unsafeApi = unsafeApi;
        this.logger = logger;
    }

    public IReadOnlyList<IModAssetBundle> LoadedBundles =>
        _bundles.Where(bundle => bundle.IsLoaded).Cast<IModAssetBundle>().ToArray();

    public IModAssetBundle LoadBundle(string relativePath)
    {
        EnsureMainThread();
        var source = ResolveContainedPath(relativePath);
        return LoadAbsoluteBundle(source, Path.GetFileName(source));
    }

    public IModAssetBundleSet LoadBundleSet(string relativeIndexPath)
    {
        EnsureMainThread();
        var indexPath = ResolveContainedPath(relativeIndexPath);
        var index = BundleIndexReader.Read(indexPath);
        if (!string.Equals(index.UnityVersion, SupportedUnityVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Bundle index requires Unity '{index.UnityVersion}', expected '{SupportedUnityVersion}'.");
        if (!string.Equals(index.BuildTarget, SupportedBuildTarget, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Bundle index target '{index.BuildTarget}' is not '{SupportedBuildTarget}'.");

        var indexDirectory = Path.GetDirectoryName(indexPath)
            ?? throw new InvalidDataException("Bundle index has no parent directory.");
        var loaded = new List<ModAssetBundle>();
        try
        {
            foreach (var record in BundleIndexReader.ResolveLoadOrder(index))
            {
                var path = ResolveContainedAbsolute(indexDirectory, record.Name);
                BundleIndexReader.VerifyFile(path, record);
                loaded.Add(LoadAbsoluteBundle(path, record.Name));
            }
            var set = new ModAssetBundleSet(
                this, indexPath, index.UnityVersion, index.BuildTarget, loaded);
            _sets.Add(set);
            logger.Info(
                $"Loaded AssetBundle set '{relativeIndexPath}' with {loaded.Count} bundle(s): " +
                string.Join(", ", loaded.Select(bundle => bundle.Name)) + ".");
            return set;
        }
        catch
        {
            foreach (var bundle in loaded.AsEnumerable().Reverse()) bundle.Unload();
            throw;
        }
    }

    private void Remove(ModAssetBundle bundle) => _bundles.Remove(bundle);
    private void Remove(ModAssetBundleSet set) => _sets.Remove(set);

    internal void RemoveAll()
    {
        RemoveAllAudio();
        RemoveAllMeshes();
        RemoveAllMaterials();
        RemoveAllImages();
        foreach (var set in _sets.ToArray().Reverse()) set.Unload();
        foreach (var bundle in _bundles.ToArray().Reverse()) bundle.Unload();
        _sets.Clear();
        _bundles.Clear();
    }

    private ModAssetBundle LoadAbsoluteBundle(string source, string name)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("AssetBundle does not exist.", source);
        var bundle = ModAssetBundle.Load(this, name, source, unsafeApi);
        _bundles.Add(bundle);
        logger.Info(
            $"Loaded AssetBundle '{bundle.Name}' ({bundle.AssetNames.Count} asset(s), " +
            $"{bundle.ScenePaths.Count} scene(s)) from '{source}'.");
        return bundle;
    }

    private string ResolveContainedPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Asset paths must be relative to the mod directory.");
        return ResolveContainedAbsolute(modDirectory, relativePath);
    }

    private string ResolveContainedAbsolute(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(modDirectory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Asset path escapes the mod directory.");
        return resolved;
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException(
                "AssetBundle calls must run on Unity's main thread. Use context.MainThread.Post().");
    }

    internal sealed partial class ModAssetBundle : IModAssetBundle
    {
        private readonly ModAssets _owner;
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _assetBundleClass;
        private readonly nint _unityObjectType;
        private readonly nint _gameObjectClass;
        private readonly NativeAssetBundleBridge? _nativeBridge;
        private IReadOnlyList<string>? _assetNames;
        private IReadOnlyList<string>? _scenePaths;
        private nint _bundle;

        private ModAssetBundle(
            ModAssets owner,
            string name,
            string sourcePath,
            IUnsafeIl2CppApi api,
            nint assetBundleClass,
            nint unityObjectType,
            nint gameObjectClass,
            nint bundle,
            NativeAssetBundleBridge? nativeBridge = null)
        {
            _owner = owner;
            Name = name;
            SourcePath = sourcePath;
            _api = api;
            _assetBundleClass = assetBundleClass;
            _unityObjectType = unityObjectType;
            _gameObjectClass = gameObjectClass;
            _bundle = bundle;
            _nativeBridge = nativeBridge;
        }

        public string SourcePath { get; }
        public string Name { get; }
        public bool IsLoaded => _bundle != 0;
        public IReadOnlyList<string> AssetNames => _assetNames ??= ReadAssetNames();
        public IReadOnlyList<string> ScenePaths => _scenePaths ??= ReadScenePaths();

        internal static unsafe ModAssetBundle Load(
            ModAssets owner,
            string name,
            string source,
            IUnsafeIl2CppApi api)
        {
            var klass = api.FindClass(
                "UnityEngine.AssetBundleModule.dll", "UnityEngine", "AssetBundle");
            var unityObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            var gameObjectClass = api.FindClass(
                "UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            if (unityObjectClass == 0 || gameObjectClass == 0)
                throw new NotSupportedException("Unity Object/GameObject APIs are unavailable.");
            if (klass == 0)
            {
                var nativeBridge = new NativeAssetBundleBridge(api, unityObjectClass);
                var nativeBundle = nativeBridge.LoadFromFile(source);
                if (nativeBundle == 0)
                    throw new InvalidDataException(
                        $"Unity rejected AssetBundle '{source}'. Check Unity version and target platform.");
                return new ModAssetBundle(
                    owner,
                    name,
                    source,
                    api,
                    0,
                    api.GetTypeObject(unityObjectClass),
                    gameObjectClass,
                    nativeBundle,
                    nativeBridge);
            }
            nint* arguments = stackalloc nint[1];
            arguments[0] = api.NewString(source);
            var bundle = api.RuntimeInvoke(RequireMethod(api, klass, "LoadFromFile", 1), 0, (nint)arguments);
            if (bundle == 0)
                throw new InvalidDataException(
                    $"Unity rejected AssetBundle '{source}'. Check Unity version and target platform.");
            return new ModAssetBundle(
                owner, name, source, api, klass, api.GetTypeObject(unityObjectClass), gameObjectClass, bundle);
        }

        public bool Contains(string assetName)
        {
            EnsureLoaded();
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            return AssetNames.Contains(assetName, StringComparer.OrdinalIgnoreCase);
        }

        public bool ContainsScene(string scenePath)
        {
            EnsureLoaded();
            ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
            return ScenePaths.Contains(scenePath, StringComparer.OrdinalIgnoreCase);
        }

        public unsafe UnityObject LoadAsset(string assetName)
        {
            EnsureMainThread();
            EnsureLoaded();
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            if (_nativeBridge is not null)
                return new UnityObject(_nativeBridge.LoadAsset(_bundle, assetName, _unityObjectType));
            nint* arguments = stackalloc nint[2];
            arguments[0] = _api.NewString(assetName);
            arguments[1] = _unityObjectType;
            return new UnityObject(_api.RuntimeInvoke(
                RequireMethod(_api, _assetBundleClass, "LoadAsset", 2), _bundle, (nint)arguments));
        }

        public UnityObject LoadPrefab(string assetName)
        {
            var asset = LoadAsset(assetName);
            if (asset.IsNull)
                throw new KeyNotFoundException($"AssetBundle '{Name}' has no asset '{assetName}'.");
            var actualClass = _api.GetObjectClass(asset.Pointer);
            if (!_api.IsAssignableFrom(_gameObjectClass, actualClass))
                throw new InvalidDataException(
                    $"Asset '{assetName}' in bundle '{Name}' is not a UnityEngine.GameObject prefab.");
            return asset;
        }

        public unsafe IReadOnlyList<UnityObject> LoadAllAssets()
        {
            EnsureMainThread();
            EnsureLoaded();
            if (_nativeBridge is not null)
                return AssetNames
                    .Select(LoadAsset)
                    .Where(asset => !asset.IsNull)
                    .DistinctBy(asset => asset.Pointer)
                    .ToArray();
            nint* arguments = stackalloc nint[1];
            arguments[0] = _unityObjectType;
            var array = _api.RuntimeInvoke(
                RequireMethod(_api, _assetBundleClass, "LoadAllAssets", 1), _bundle, (nint)arguments);
            return ReadReferenceArray(array).Select(pointer => new UnityObject(pointer)).ToArray();
        }

        public unsafe void Unload(bool unloadLoadedObjects = false)
        {
            EnsureMainThread();
            if (_bundle == 0) return;
            _owner.UnloadScenes(this);
            if (_owner.HasLiveScenes(this))
            {
                _unloadRequested = true;
                _unloadLoadedObjects |= unloadLoadedObjects;
                return;
            }
            UnloadNative(unloadLoadedObjects);
        }

        private unsafe void UnloadNative(bool unloadLoadedObjects)
        {
            if (_bundle == 0) return;
            if (_nativeBridge is not null)
            {
                _nativeBridge.Unload(_bundle, unloadLoadedObjects);
                _bundle = 0;
                _owner.Remove(this);
                return;
            }
            byte nativeValue = unloadLoadedObjects ? (byte)1 : (byte)0;
            nint* arguments = stackalloc nint[1];
            arguments[0] = (nint)(&nativeValue);
            _ = _api.RuntimeInvoke(
                RequireMethod(_api, _assetBundleClass, "Unload", 1), _bundle, (nint)arguments);
            _bundle = 0;
            _owner.Remove(this);
        }

        internal void CompleteDeferredUnload()
        {
            if (_unloadRequested && !_owner.HasLiveScenes(this))
                UnloadNative(_unloadLoadedObjects);
        }

        public void Dispose() => Unload();

        private IReadOnlyList<string> ReadAssetNames()
        {
            EnsureMainThread();
            EnsureLoaded();
            var array = _nativeBridge is not null
                ? _nativeBridge.GetAllAssetNames(_bundle)
                : _api.RuntimeInvoke(
                    RequireMethod(_api, _assetBundleClass, "GetAllAssetNames", 0), _bundle, 0);
            return ReadReferenceArray(array)
                .Select(pointer => pointer == 0 ? string.Empty : _api.ReadString(pointer))
                .Where(value => value.Length != 0)
                .ToArray();
        }

        private IReadOnlyList<string> ReadScenePaths()
        {
            EnsureMainThread();
            EnsureLoaded();
            var array = _nativeBridge is not null
                ? _nativeBridge.GetAllScenePaths(_bundle)
                : _api.RuntimeInvoke(
                    RequireMethod(_api, _assetBundleClass, "GetAllScenePaths", 0), _bundle, 0);
            return ReadReferenceArray(array)
                .Select(pointer => pointer == 0 ? string.Empty : _api.ReadString(pointer))
                .Where(value => value.Length != 0)
                .ToArray();
        }

        private IReadOnlyList<nint> ReadReferenceArray(nint array)
        {
            if (array == 0) return [];
            var length = _api.GetArrayLength(array);
            if (length > 100_000) throw new InvalidDataException("Unity returned an implausibly large asset array.");
            var result = new nint[(int)length];
            for (nuint index = 0; index < length; index++)
                result[(int)index] = _api.ReadArrayElementReference(array, index);
            return result;
        }

        private void EnsureLoaded()
        {
            if (_bundle == 0) throw new ObjectDisposedException(nameof(IModAssetBundle));
            if (_unloadRequested)
                throw new InvalidOperationException($"AssetBundle '{Name}' is waiting for its scenes to unload.");
        }
    }

    private sealed class ModAssetBundleSet(
        ModAssets owner,
        string indexPath,
        string unityVersion,
        string buildTarget,
        IReadOnlyList<ModAssetBundle> bundles) : IModAssetBundleSet
    {
        private bool _loaded = true;
        public string IndexPath { get; } = indexPath;
        public string UnityVersion { get; } = unityVersion;
        public string BuildTarget { get; } = buildTarget;
        public IReadOnlyList<IModAssetBundle> Bundles { get; } = bundles.Cast<IModAssetBundle>().ToArray();
        public bool IsLoaded => _loaded && Bundles.Any(bundle => bundle.IsLoaded);

        public IModAssetBundle GetBundle(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!_loaded) throw new ObjectDisposedException(nameof(IModAssetBundleSet));
            return Bundles.FirstOrDefault(bundle =>
                    string.Equals(bundle.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Bundle set has no bundle '{name}'.");
        }

        public void Unload(bool unloadLoadedObjects = false)
        {
            if (!_loaded) return;
            foreach (var bundle in Bundles.Reverse()) bundle.Unload(unloadLoadedObjects);
            _loaded = false;
            owner.Remove(this);
        }

        public void Dispose() => Unload();
    }

    internal static nint RequireMethod(
        IUnsafeIl2CppApi api,
        nint klass,
        string name,
        int argumentCount)
    {
        var method = api.FindMethod(klass, name, argumentCount);
        return method != 0
            ? method
            : throw new MissingMethodException($"AssetBundle.{name}/{argumentCount} was not found.");
    }
}

internal static class BundleIndexReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static BundleIndex Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bundle index does not exist.", path);
        var index = JsonSerializer.Deserialize<BundleIndex>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Bundle index deserialized to null.");
        if (index.SchemaVersion != 1) throw new InvalidDataException("Unsupported bundle index schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(index.UnityVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(index.BuildTarget);
        if (index.Bundles is null || index.Bundles.Count == 0)
            throw new InvalidDataException("Bundle index contains no bundles.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in index.Bundles)
        {
            ValidateName(bundle.Name);
            if (!names.Add(bundle.Name)) throw new InvalidDataException($"Duplicate bundle '{bundle.Name}'.");
            if (bundle.Bytes < 1) throw new InvalidDataException($"Bundle '{bundle.Name}' has invalid size.");
            if (bundle.Sha256.Length != 64 || bundle.Sha256.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException($"Bundle '{bundle.Name}' has invalid SHA-256.");
            bundle.Dependencies ??= [];
        }
        foreach (var bundle in index.Bundles)
            foreach (var dependency in bundle.Dependencies)
            {
                ValidateName(dependency);
                if (!names.Contains(dependency))
                    throw new InvalidDataException(
                        $"Bundle '{bundle.Name}' depends on missing bundle '{dependency}'.");
            }
        return index;
    }

    internal static IReadOnlyList<BundleIndexRecord> ResolveLoadOrder(BundleIndex index)
    {
        var records = index.Bundles.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<BundleIndexRecord>();
        foreach (var name in records.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            Visit(name);
        return order;

        void Visit(string name)
        {
            if (state.TryGetValue(name, out var current))
            {
                if (current == 1) throw new InvalidDataException($"AssetBundle dependency cycle includes '{name}'.");
                return;
            }
            state[name] = 1;
            var record = records[name];
            foreach (var dependency in record.Dependencies.OrderBy(
                value => value, StringComparer.OrdinalIgnoreCase)) Visit(dependency);
            state[name] = 2;
            order.Add(record);
        }
    }

    internal static void VerifyFile(string path, BundleIndexRecord record)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Bundle '{record.Name}' is missing.", path);
        var info = new FileInfo(path);
        if (info.Length != record.Bytes)
            throw new InvalidDataException(
                $"Bundle '{record.Name}' size mismatch: expected {record.Bytes}, got {info.Length}.");
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Bundle '{record.Name}' SHA-256 mismatch.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException($"Invalid AssetBundle name '{name}'.");
    }
}

internal sealed class BundleIndex
{
    public int SchemaVersion { get; set; }
    public string UnityVersion { get; set; } = string.Empty;
    public string BuildTarget { get; set; } = string.Empty;
    public List<BundleIndexRecord> Bundles { get; set; } = [];
}

internal sealed class BundleIndexRecord
{
    public string Name { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string UnityHash { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = [];
}
