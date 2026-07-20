using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeColor(float R, float G, float B, float A);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeVector4(float X, float Y, float Z, float W);

    private static readonly Dictionary<(nint Renderer, int Slot), ModRendererBinding>
        RendererReservations = [];
    private static readonly List<ModRendererBinding> AllRendererBindings = [];
    private readonly List<ModMaterial> _materials = [];
    private readonly List<ModRendererBinding> _rendererBindings = [];
    private MaterialBridge? _materialBridge;

    public IReadOnlyList<IModMaterial> LoadedMaterials =>
        _materials.Where(material => material.IsLoaded).Cast<IModMaterial>().ToArray();

    public IReadOnlyList<IModRendererBinding> ActiveRendererBindings =>
        _rendererBindings.ToArray().Where(binding => binding.IsBound)
            .Cast<IModRendererBinding>()
            .ToArray();

    public UnityObject FindShader(string shaderName)
    {
        EnsureMainThread();
        return new UnityObject(Materials.FindShader(shaderName));
    }

    public IReadOnlyList<UnityObject> GetRendererSharedMaterials(UnityObject renderer)
    {
        EnsureMainThread();
        return Materials.GetSharedMaterials(renderer.Pointer)
            .Select(pointer => new UnityObject(pointer))
            .ToArray();
    }

    public IModMaterial CreateMaterial(string shaderName, string name)
    {
        EnsureMainThread();
        ValidateMaterialName(name);
        var shader = Materials.FindShader(shaderName);
        if (shader == 0) throw new KeyNotFoundException($"Unity shader '{shaderName}' was not found.");
        return AddMaterial(name.Trim(), Materials.CreateFromShader(shader, name.Trim()));
    }

    public IModMaterial CloneMaterial(UnityObject sourceMaterial, string name)
    {
        EnsureMainThread();
        ValidateMaterialName(name);
        Materials.RequireMaterial(sourceMaterial.Pointer, nameof(sourceMaterial));
        return AddMaterial(name.Trim(), Materials.Clone(sourceMaterial.Pointer, name.Trim()));
    }

    public IModRendererBinding BindRendererMaterial(
        UnityObject renderer,
        int slot,
        IModMaterial material)
    {
        EnsureMainThread();
        if (material is not ModMaterial runtimeMaterial ||
            !ReferenceEquals(runtimeMaterial.RuntimeOwner, this))
            throw new InvalidOperationException("A renderer can only bind a material owned by this mod.");
        if (!runtimeMaterial.IsLoaded) throw new ObjectDisposedException(runtimeMaterial.Name);
        var current = Materials.GetSharedMaterials(renderer.Pointer);
        if (slot < 0 || slot >= current.Count)
            throw new ArgumentOutOfRangeException(
                nameof(slot), $"Renderer has {current.Count} shared-material slot(s).");
        var key = (renderer.Pointer, slot);
        if (RendererReservations.TryGetValue(key, out var existing))
            throw new InvalidOperationException(
                $"Renderer 0x{renderer.Pointer:X} slot {slot} is already bound by " +
                $"mod '{existing.OwnerId}'.");

        var binding = new ModRendererBinding(
            this,
            Materials,
            renderer.Pointer,
            slot,
            runtimeMaterial,
            current[slot]);
        RendererReservations.Add(key, binding);
        try
        {
            var replacement = current.ToArray();
            replacement[slot] = runtimeMaterial.Material.Pointer;
            Materials.SetSharedMaterials(renderer.Pointer, replacement);
            _rendererBindings.Add(binding);
            AllRendererBindings.Add(binding);
            return binding;
        }
        catch
        {
            RendererReservations.Remove(key);
            throw;
        }
    }

    internal static void PollMaterials()
    {
        EnsureMainThread();
        foreach (var binding in AllRendererBindings.ToArray())
        {
            try { binding.Refresh(); }
            catch (Exception exception) { binding.RecordPollingFailure(exception); }
        }
    }

    private MaterialBridge Materials => _materialBridge ??= new MaterialBridge(unsafeApi);

    private IModMaterial AddMaterial(string name, nint pointer)
    {
        var material = new ModMaterial(this, Materials, name, pointer);
        _materials.Add(material);
        logger.Info($"Created material '{name}' for mod '{ownerId}'.");
        return material;
    }

    private static void ValidateMaterialName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 256)
            throw new ArgumentException("Material names cannot exceed 256 characters.", nameof(name));
    }

    private void Remove(ModMaterial material) => _materials.Remove(material);

    private void Remove(ModRendererBinding binding)
    {
        _rendererBindings.Remove(binding);
        AllRendererBindings.Remove(binding);
        RendererReservations.Remove((binding.Renderer.Pointer, binding.Slot));
    }

    private void RemoveAllMaterials()
    {
        foreach (var binding in _rendererBindings.ToArray().Reverse())
        {
            try { binding.Unbind(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Renderer material slot {binding.Slot} cleanup failed.");
            }
        }
        foreach (var material in _materials.ToArray().Reverse())
        {
            try { material.Unload(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Material '{material.Name}' cleanup failed.");
            }
        }
    }

    private sealed class ModMaterial(
        ModAssets owner,
        MaterialBridge bridge,
        string name,
        nint material) : IModMaterial
    {
        private nint _material = material;

        internal ModAssets RuntimeOwner => owner;
        public string OwnerId => owner.ownerId;
        public string Name { get; } = name;
        public UnityObject Material => new(_material);
        public UnityObject Shader => new(RequireLoaded(() => bridge.GetShader(_material)));
        public bool IsLoaded
        {
            get
            {
                EnsureMainThread();
                return _material != 0 && bridge.IsObjectAlive(_material);
            }
        }
        public int RenderQueue => RequireLoaded(() => bridge.GetRenderQueue(_material));
        public IReadOnlyList<IModRendererBinding> ActiveBindings =>
            owner._rendererBindings.ToArray()
                .Where(binding => binding.IsBound && ReferenceEquals(binding.RuntimeMaterial, this))
                .Cast<IModRendererBinding>()
                .ToArray();

        public bool HasProperty(string propertyName) =>
            RequireLoaded(() => bridge.HasProperty(_material, propertyName));

        public UnityColor GetColor(string propertyName)
        {
            var value = RequireLoaded(() => bridge.GetColor(_material, propertyName));
            return new UnityColor(value.R, value.G, value.B, value.A);
        }

        public void SetColor(string propertyName, UnityColor value)
        {
            EnsureFinite(value.R, value.G, value.B, value.A);
            RequireLoaded(() => bridge.SetColor(
                _material, propertyName, new NativeColor(value.R, value.G, value.B, value.A)));
        }

        public float GetFloat(string propertyName) =>
            RequireLoaded(() => bridge.GetFloat(_material, propertyName));

        public void SetFloat(string propertyName, float value)
        {
            EnsureFinite(value);
            RequireLoaded(() => bridge.SetFloat(_material, propertyName, value));
        }

        public UnityVector4 GetVector(string propertyName)
        {
            var value = RequireLoaded(() => bridge.GetVector(_material, propertyName));
            return new UnityVector4(value.X, value.Y, value.Z, value.W);
        }

        public void SetVector(string propertyName, UnityVector4 value)
        {
            EnsureFinite(value.X, value.Y, value.Z, value.W);
            RequireLoaded(() => bridge.SetVector(
                _material, propertyName, new NativeVector4(value.X, value.Y, value.Z, value.W)));
        }

        public UnityObject GetTexture(string propertyName) =>
            new(RequireLoaded(() => bridge.GetTexture(_material, propertyName)));

        public void SetTexture(string propertyName, UnityObject texture) =>
            RequireLoaded(() => bridge.SetTexture(_material, propertyName, texture.Pointer));

        public UnityVector2 GetTextureOffset(string propertyName)
        {
            var value = RequireLoaded(() => bridge.GetTextureOffset(_material, propertyName));
            return new UnityVector2(value.X, value.Y);
        }

        public void SetTextureOffset(string propertyName, UnityVector2 value)
        {
            EnsureFinite(value.X, value.Y);
            RequireLoaded(() => bridge.SetTextureOffset(
                _material, propertyName, new NativeVector2(value.X, value.Y)));
        }

        public UnityVector2 GetTextureScale(string propertyName)
        {
            var value = RequireLoaded(() => bridge.GetTextureScale(_material, propertyName));
            return new UnityVector2(value.X, value.Y);
        }

        public void SetTextureScale(string propertyName, UnityVector2 value)
        {
            EnsureFinite(value.X, value.Y);
            RequireLoaded(() => bridge.SetTextureScale(
                _material, propertyName, new NativeVector2(value.X, value.Y)));
        }

        public bool IsKeywordEnabled(string keyword) =>
            RequireLoaded(() => bridge.IsKeywordEnabled(_material, keyword));

        public void EnableKeyword(string keyword) =>
            RequireLoaded(() => bridge.EnableKeyword(_material, keyword));

        public void DisableKeyword(string keyword) =>
            RequireLoaded(() => bridge.DisableKeyword(_material, keyword));

        public void SetRenderQueue(int renderQueue)
        {
            if (renderQueue is < -1 or > 5000) throw new ArgumentOutOfRangeException(nameof(renderQueue));
            RequireLoaded(() => bridge.SetRenderQueue(_material, renderQueue));
        }

        public void Unload()
        {
            EnsureMainThread();
            if (_material == 0) return;
            Exception? failure = null;
            foreach (var binding in owner._rendererBindings
                         .Where(item => ReferenceEquals(item.RuntimeMaterial, this))
                         .ToArray()
                         .Reverse())
            {
                try { binding.Unbind(); }
                catch (Exception exception) { failure ??= exception; }
            }
            if (failure is not null)
                throw new InvalidOperationException(
                    $"Material '{Name}' bindings could not be restored.", failure);
            bridge.Destroy(_material);
            _material = 0;
            owner.Remove(this);
        }

        public void Dispose() => Unload();

        private T RequireLoaded<T>(Func<T> operation)
        {
            EnsureMainThread();
            if (!IsLoaded) throw new ObjectDisposedException(Name);
            return operation();
        }

        private void RequireLoaded(Action operation)
        {
            EnsureMainThread();
            if (!IsLoaded) throw new ObjectDisposedException(Name);
            operation();
        }

        private static void EnsureFinite(params float[] values)
        {
            if (values.Any(value => !float.IsFinite(value)))
                throw new ArgumentOutOfRangeException(nameof(values), "Material values must be finite.");
        }
    }

    private sealed class ModRendererBinding(
        ModAssets owner,
        MaterialBridge bridge,
        nint renderer,
        int slot,
        ModMaterial material,
        nint originalMaterial) : IModRendererBinding
    {
        private bool _bound = true;
        private int _pollingFailures;

        internal ModMaterial RuntimeMaterial => material;
        public string OwnerId => owner.ownerId;
        public UnityObject Renderer { get; } = new(renderer);
        public int Slot { get; } = slot;
        public IModMaterial Material => material;
        public UnityObject OriginalMaterial { get; } = new(originalMaterial);
        public bool IsBound
        {
            get
            {
                EnsureMainThread();
                Refresh();
                return _bound;
            }
        }

        public void Unbind()
        {
            EnsureMainThread();
            if (!_bound) return;
            if (bridge.IsObjectAlive(renderer))
            {
                var current = bridge.GetSharedMaterials(renderer);
                if (Slot < current.Count && current[Slot] == material.Material.Pointer)
                {
                    var restored = current.ToArray();
                    restored[Slot] = originalMaterial;
                    bridge.SetSharedMaterials(renderer, restored);
                }
            }
            Release();
        }

        public void Dispose() => Unbind();

        internal void Refresh()
        {
            if (!_bound) return;
            if (!bridge.IsObjectAlive(renderer))
            {
                Release();
                return;
            }
            var current = bridge.GetSharedMaterials(renderer);
            if (Slot >= current.Count || current[Slot] != material.Material.Pointer)
                Release();
            _pollingFailures = 0;
        }

        internal void RecordPollingFailure(Exception exception)
        {
            ++_pollingFailures;
            if (_pollingFailures == 1 || _pollingFailures == 10 || _pollingFailures % 300 == 0)
                owner.logger.Error(
                    exception,
                    $"Renderer material binding slot {Slot} polling failed " +
                    $"{_pollingFailures} time(s).");
        }

        private void Release()
        {
            if (!_bound) return;
            _bound = false;
            owner.Remove(this);
        }
    }

    private sealed class MaterialBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint MaterialResolveIcallDelegate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetTextureTransformDelegate(
            nint material,
            int propertyId,
            out NativeVector4 value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetTextureVector2Delegate(
            nint material,
            int propertyId,
            ref NativeVector2 value);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MaterialStringSpan(nint begin, int length)
        {
            internal readonly nint Begin = begin;
            internal readonly int Length = length;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetKeywordDelegate(
            nint material,
            ref MaterialStringSpan keyword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetKeywordDelegate(
            nint material,
            ref MaterialStringSpan keyword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetMaterialIntDelegate(nint material);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetMaterialIntDelegate(nint material, int value);

        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _materialClass;
        private readonly nint _rendererClass;
        private readonly nint _textureClass;
        private readonly nint _findShader;
        private readonly nint _propertyToId;
        private readonly nint _materialFromShader;
        private readonly nint _materialFromMaterial;
        private readonly nint _getShader;
        private readonly nint _hasProperty;
        private readonly nint _getColor;
        private readonly nint _setColor;
        private readonly nint _getFloat;
        private readonly nint _setFloat;
        private readonly nint _getVector;
        private readonly nint _setVector;
        private readonly nint _getTexture;
        private readonly nint _setTexture;
        private readonly GetTextureTransformDelegate _getTextureTransform;
        private readonly SetTextureVector2Delegate _setTextureOffset;
        private readonly SetTextureVector2Delegate _setTextureScale;
        private readonly GetKeywordDelegate _isKeywordEnabled;
        private readonly SetKeywordDelegate _enableKeyword;
        private readonly SetKeywordDelegate _disableKeyword;
        private readonly GetMaterialIntDelegate _getRenderQueue;
        private readonly SetMaterialIntDelegate _setRenderQueue;
        private readonly nint _getSharedMaterials;
        private readonly nint _setSharedMaterials;
        private readonly nint _setName;
        private readonly nint _destroy;
        private readonly nint _objectImplicit;
        private readonly int _unityObjectCachedPointerOffset;

        internal MaterialBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            var core = "UnityEngine.CoreModule.dll";
            var shaderClass = RequireClass(api, core, "UnityEngine", "Shader");
            _materialClass = RequireClass(api, core, "UnityEngine", "Material");
            _rendererClass = RequireClass(api, core, "UnityEngine", "Renderer");
            _textureClass = RequireClass(api, core, "UnityEngine", "Texture");
            var objectClass = RequireClass(api, core, "UnityEngine", "Object");
            _findShader = RequireSignature(api, shaderClass, "Find", "System.String");
            _propertyToId = RequireSignature(api, shaderClass, "PropertyToID", "System.String");
            _materialFromShader = RequireSignature(api, _materialClass, ".ctor", "UnityEngine.Shader");
            _materialFromMaterial = RequireSignature(api, _materialClass, ".ctor", "UnityEngine.Material");
            _getShader = RequireMethod(api, _materialClass, "get_shader", 0);
            _hasProperty = RequireSignature(api, _materialClass, "HasProperty", "System.Int32");
            _getColor = RequireSignature(api, _materialClass, "GetColor", "System.Int32");
            _setColor = RequireSignature(
                api, _materialClass, "SetColor", "System.Int32", "UnityEngine.Color");
            _getFloat = RequireSignature(api, _materialClass, "GetFloat", "System.Int32");
            _setFloat = RequireSignature(
                api, _materialClass, "SetFloat", "System.Int32", "System.Single");
            _getVector = RequireSignature(api, _materialClass, "GetVector", "System.Int32");
            _setVector = RequireSignature(
                api, _materialClass, "SetVector", "System.Int32", "UnityEngine.Vector4");
            _getTexture = RequireSignature(api, _materialClass, "GetTexture", "System.Int32");
            _setTexture = RequireSignature(
                api, _materialClass, "SetTexture", "System.Int32", "UnityEngine.Texture");
            var cachedPointer = api.FindField(objectClass, "m_CachedPtr");
            if (cachedPointer == 0)
                throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
            _unityObjectCachedPointerOffset = api.GetFieldOffset(cachedPointer);
            var resolvePointer = NativeLibrary.GetExport(
                api.GameAssemblyModule, "il2cpp_resolve_icall");
            var resolve = Marshal.GetDelegateForFunctionPointer<MaterialResolveIcallDelegate>(
                resolvePointer);
            _getTextureTransform = Resolve<GetTextureTransformDelegate>(
                resolve, "UnityEngine.Material::GetTextureScaleAndOffsetImpl_Injected");
            _setTextureOffset = Resolve<SetTextureVector2Delegate>(
                resolve, "UnityEngine.Material::SetTextureOffsetImpl_Injected");
            _setTextureScale = Resolve<SetTextureVector2Delegate>(
                resolve, "UnityEngine.Material::SetTextureScaleImpl_Injected");
            _isKeywordEnabled = Resolve<GetKeywordDelegate>(
                resolve, "UnityEngine.Material::IsKeywordEnabled_Injected");
            _enableKeyword = Resolve<SetKeywordDelegate>(
                resolve, "UnityEngine.Material::EnableKeyword_Injected");
            _disableKeyword = Resolve<SetKeywordDelegate>(
                resolve, "UnityEngine.Material::DisableKeyword_Injected");
            _getRenderQueue = Resolve<GetMaterialIntDelegate>(
                resolve, "UnityEngine.Material::get_renderQueue_Injected");
            _setRenderQueue = Resolve<SetMaterialIntDelegate>(
                resolve, "UnityEngine.Material::set_renderQueue_Injected");
            _getSharedMaterials = RequireMethod(api, _rendererClass, "get_sharedMaterials", 0);
            _setSharedMaterials = RequireSignature(
                api, _rendererClass, "set_sharedMaterials", "UnityEngine.Material[]");
            _setName = RequireSignature(api, objectClass, "set_name", "System.String");
            _destroy = RequireSignature(api, objectClass, "Destroy", "UnityEngine.Object");
            _objectImplicit = RequireSignature(api, objectClass, "op_Implicit", "UnityEngine.Object");
        }

        internal nint FindShader(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _api.Invoke(
                _findShader, 0, Il2CppArgument.FromReference(_api.NewString(name.Trim())));
        }

        internal nint CreateFromShader(nint shader, string name) =>
            Construct(_materialFromShader, shader, name);

        internal nint Clone(nint source, string name) =>
            Construct(_materialFromMaterial, source, name);

        internal void RequireMaterial(nint instance, string parameterName)
        {
            if (instance == 0 || !IsObjectAlive(instance) ||
                !_api.IsAssignableFrom(_materialClass, _api.GetObjectClass(instance)))
                throw new ArgumentException("Unity object is not a live Material.", parameterName);
        }

        internal nint GetShader(nint material) => _api.Invoke(_getShader, material);
        internal int GetRenderQueue(nint material) => _getRenderQueue(NativePointer(material));
        internal void SetRenderQueue(nint material, int value) =>
            _setRenderQueue(NativePointer(material), value);

        internal bool HasProperty(nint material, string name) =>
            ReadBoolean(_api.Invoke(
                _hasProperty, material, Il2CppArgument.FromInt32(PropertyId(name))));

        internal NativeColor GetColor(nint material, string name)
        {
            RequireProperty(material, name);
            return ReadValue<NativeColor>(_api.Invoke(
                _getColor, material, Il2CppArgument.FromInt32(PropertyId(name))));
        }

        internal void SetColor(nint material, string name, NativeColor value)
        {
            RequireProperty(material, name);
            _ = _api.Invoke(
                _setColor,
                material,
                Il2CppArgument.FromInt32(PropertyId(name)),
                Il2CppArgument.FromValue(value));
        }

        internal float GetFloat(nint material, string name)
        {
            RequireProperty(material, name);
            return ReadValue<float>(_api.Invoke(
                _getFloat, material, Il2CppArgument.FromInt32(PropertyId(name))));
        }

        internal void SetFloat(nint material, string name, float value)
        {
            RequireProperty(material, name);
            _ = _api.Invoke(
                _setFloat,
                material,
                Il2CppArgument.FromInt32(PropertyId(name)),
                Il2CppArgument.FromSingle(value));
        }

        internal NativeVector4 GetVector(nint material, string name)
        {
            RequireProperty(material, name);
            return ReadValue<NativeVector4>(_api.Invoke(
                _getVector, material, Il2CppArgument.FromInt32(PropertyId(name))));
        }

        internal void SetVector(nint material, string name, NativeVector4 value)
        {
            RequireProperty(material, name);
            _ = _api.Invoke(
                _setVector,
                material,
                Il2CppArgument.FromInt32(PropertyId(name)),
                Il2CppArgument.FromValue(value));
        }

        internal nint GetTexture(nint material, string name)
        {
            RequireProperty(material, name);
            return _api.Invoke(
                _getTexture, material, Il2CppArgument.FromInt32(PropertyId(name)));
        }

        internal void SetTexture(nint material, string name, nint texture)
        {
            RequireProperty(material, name);
            if (texture != 0 && (!IsObjectAlive(texture) ||
                !_api.IsAssignableFrom(_textureClass, _api.GetObjectClass(texture))))
                throw new ArgumentException("Unity object is not a live Texture.", nameof(texture));
            _ = _api.Invoke(
                _setTexture,
                material,
                Il2CppArgument.FromInt32(PropertyId(name)),
                Il2CppArgument.FromReference(texture));
        }

        internal NativeVector2 GetTextureOffset(nint material, string name)
        {
            RequireProperty(material, name);
            _getTextureTransform(NativePointer(material), PropertyId(name), out var value);
            return new NativeVector2(value.Z, value.W);
        }

        internal void SetTextureOffset(nint material, string name, NativeVector2 value)
        {
            RequireProperty(material, name);
            _setTextureOffset(NativePointer(material), PropertyId(name), ref value);
        }

        internal NativeVector2 GetTextureScale(nint material, string name)
        {
            RequireProperty(material, name);
            _getTextureTransform(NativePointer(material), PropertyId(name), out var value);
            return new NativeVector2(value.X, value.Y);
        }

        internal void SetTextureScale(nint material, string name, NativeVector2 value)
        {
            RequireProperty(material, name);
            _setTextureScale(NativePointer(material), PropertyId(name), ref value);
        }

        internal unsafe bool IsKeywordEnabled(nint material, string keyword)
        {
            keyword = ValidateKeyword(keyword);
            fixed (char* characters = keyword)
            {
                var span = new MaterialStringSpan((nint)characters, keyword.Length);
                return _isKeywordEnabled(NativePointer(material), ref span);
            }
        }

        internal unsafe void EnableKeyword(nint material, string keyword)
        {
            keyword = ValidateKeyword(keyword);
            fixed (char* characters = keyword)
            {
                var span = new MaterialStringSpan((nint)characters, keyword.Length);
                _enableKeyword(NativePointer(material), ref span);
            }
        }

        internal unsafe void DisableKeyword(nint material, string keyword)
        {
            keyword = ValidateKeyword(keyword);
            fixed (char* characters = keyword)
            {
                var span = new MaterialStringSpan((nint)characters, keyword.Length);
                _disableKeyword(NativePointer(material), ref span);
            }
        }

        internal IReadOnlyList<nint> GetSharedMaterials(nint renderer)
        {
            RequireRenderer(renderer);
            var array = _api.Invoke(_getSharedMaterials, renderer);
            if (array == 0) return [];
            var length = _api.GetArrayLength(array);
            if (length > 1024) throw new InvalidDataException("Renderer material array is unreasonably large.");
            var result = new nint[(int)length];
            for (nuint index = 0; index < length; ++index)
                result[(int)index] = _api.ReadArrayElementReference(array, index);
            return result;
        }

        internal void SetSharedMaterials(nint renderer, IReadOnlyList<nint> materials)
        {
            RequireRenderer(renderer);
            ArgumentNullException.ThrowIfNull(materials);
            var array = _api.NewArray(_materialClass, (nuint)materials.Count);
            for (var index = 0; index < materials.Count; ++index)
                _api.WriteArrayElementReference(array, (nuint)index, materials[index]);
            _ = _api.Invoke(
                _setSharedMaterials,
                renderer,
                Il2CppArgument.FromReference(array));
        }

        internal bool IsObjectAlive(nint instance)
        {
            if (instance == 0) return false;
            return ReadBoolean(_api.Invoke(
                _objectImplicit, 0, Il2CppArgument.FromReference(instance)));
        }

        internal void Destroy(nint instance) =>
            _ = _api.Invoke(_destroy, 0, Il2CppArgument.FromReference(instance));

        private nint Construct(nint constructor, nint source, string name)
        {
            nint material = 0;
            try
            {
                material = _api.NewObject(_materialClass);
                _ = _api.Invoke(constructor, material, Il2CppArgument.FromReference(source));
                _ = _api.Invoke(
                    _setName,
                    material,
                    Il2CppArgument.FromReference(_api.NewString($"OFS {name}")));
                return material;
            }
            catch
            {
                if (material != 0)
                {
                    try { Destroy(material); }
                    catch { }
                }
                throw;
            }
        }

        private nint NativePointer(nint unityObject)
        {
            var pointer = Marshal.ReadIntPtr(unityObject, _unityObjectCachedPointerOffset);
            return pointer != 0
                ? pointer
                : throw new InvalidOperationException("Unity object has no native pointer.");
        }

        private void RequireRenderer(nint renderer)
        {
            if (renderer == 0 || !IsObjectAlive(renderer) ||
                !_api.IsAssignableFrom(_rendererClass, _api.GetObjectClass(renderer)))
                throw new ArgumentException("Unity object is not a live Renderer.", nameof(renderer));
        }

        private void RequireProperty(nint material, string name)
        {
            if (!HasProperty(material, name))
                throw new KeyNotFoundException($"Material has no shader property '{name}'.");
        }

        private int PropertyId(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Length > 256) throw new ArgumentException("Shader property name is too long.", nameof(name));
            return ReadInt32(_api.Invoke(
                _propertyToId, 0, Il2CppArgument.FromReference(_api.NewString(name))));
        }

        private static string ValidateKeyword(string keyword)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
            if (keyword.Length > 256) throw new ArgumentException("Shader keyword is too long.", nameof(keyword));
            return keyword;
        }

        private bool ReadBoolean(nint boxed)
        {
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        private int ReadInt32(nint boxed)
        {
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0
                ? Marshal.ReadInt32(value)
                : throw new InvalidDataException("Unity Int32 result could not be unboxed.");
        }

        private T ReadValue<T>(nint boxed) where T : unmanaged
        {
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0
                ? Marshal.PtrToStructure<T>(value)
                : throw new InvalidDataException($"Unity {typeof(T).Name} result could not be unboxed.");
        }

        private static nint RequireClass(
            IUnsafeIl2CppApi api,
            string assembly,
            string namespaze,
            string name)
        {
            var klass = api.FindClass(assembly, namespaze, name);
            return klass != 0 ? klass : throw new TypeLoadException($"{namespaze}.{name}");
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
                : throw new MissingMethodException($"{name}({string.Join(", ", parameters)})");
        }

        private static T Resolve<T>(MaterialResolveIcallDelegate resolve, string name)
            where T : Delegate
        {
            var pointer = resolve(name);
            return pointer != 0
                ? Marshal.GetDelegateForFunctionPointer<T>(pointer)
                : throw new MissingMethodException($"Native Unity icall '{name}' was not resolved.");
        }
    }
}
