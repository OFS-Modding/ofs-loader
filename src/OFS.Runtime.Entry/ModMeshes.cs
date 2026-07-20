using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeVector3(float X, float Y, float Z);

    private static readonly Dictionary<nint, ModMeshBinding> MeshFilterReservations = [];
    private static readonly List<ModMeshBinding> AllMeshBindings = [];
    private readonly List<ModMesh> _meshes = [];
    private readonly List<ModMeshBinding> _meshBindings = [];
    private MeshBridge? _meshBridge;

    public IReadOnlyList<IModMesh> LoadedMeshes =>
        _meshes.ToArray().Where(mesh => mesh.IsLoaded).Cast<IModMesh>().ToArray();

    public IReadOnlyList<IModMeshBinding> ActiveMeshBindings =>
        _meshBindings.ToArray().Where(binding => binding.IsBound)
            .Cast<IModMeshBinding>()
            .ToArray();

    public IModMesh CreateMesh(
        string name,
        ModMeshGeometry geometry,
        bool markDynamic = false,
        bool uploadMeshData = false)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 256)
            throw new ArgumentException("Mesh names cannot exceed 256 characters.", nameof(name));
        var validated = ValidatedMeshGeometry.Create(geometry);
        var pointer = Meshes.Create(name);
        try
        {
            Meshes.Apply(pointer, validated, clear: false, markDynamic, uploadMeshData);
            var mesh = new ModMesh(this, Meshes, name, pointer, validated, !uploadMeshData);
            _meshes.Add(mesh);
            logger.Info(
                $"Created mesh '{name}' for mod '{ownerId}' with " +
                $"{validated.Vertices.Length} vertices, {validated.IndexCount} indices and " +
                $"{validated.SubMeshes.Length} submesh(es).");
            return mesh;
        }
        catch
        {
            try { Meshes.Destroy(pointer); }
            catch { }
            throw;
        }
    }

    public UnityObject GetMeshFilterSharedMesh(UnityObject meshFilter)
    {
        EnsureMainThread();
        return new UnityObject(Meshes.GetSharedMesh(meshFilter.Pointer));
    }

    public IModMeshBinding BindMeshFilter(UnityObject meshFilter, IModMesh mesh)
    {
        EnsureMainThread();
        if (mesh is not ModMesh runtimeMesh || !ReferenceEquals(runtimeMesh.RuntimeOwner, this))
            throw new InvalidOperationException("A MeshFilter can only bind a mesh owned by this mod.");
        if (!runtimeMesh.IsLoaded) throw new ObjectDisposedException(runtimeMesh.Name);
        var filter = meshFilter.Pointer;
        var original = Meshes.GetSharedMesh(filter);
        if (MeshFilterReservations.TryGetValue(filter, out var existing))
            throw new InvalidOperationException(
                $"MeshFilter 0x{filter:X} is already bound by mod '{existing.OwnerId}'.");

        var binding = new ModMeshBinding(this, Meshes, filter, runtimeMesh, original);
        MeshFilterReservations.Add(filter, binding);
        try
        {
            Meshes.SetSharedMesh(filter, runtimeMesh.Mesh.Pointer);
            _meshBindings.Add(binding);
            AllMeshBindings.Add(binding);
            return binding;
        }
        catch
        {
            MeshFilterReservations.Remove(filter);
            try
            {
                if (Meshes.GetSharedMesh(filter) == runtimeMesh.Mesh.Pointer)
                    Meshes.SetSharedMesh(filter, original);
            }
            catch { }
            throw;
        }
    }

    internal static void PollMeshes()
    {
        EnsureMainThread();
        foreach (var binding in AllMeshBindings.ToArray())
        {
            try { binding.Refresh(); }
            catch (Exception exception) { binding.RecordPollingFailure(exception); }
        }
    }

    private MeshBridge Meshes => _meshBridge ??= new MeshBridge(unsafeApi);

    private void Remove(ModMesh mesh) => _meshes.Remove(mesh);

    private void Remove(ModMeshBinding binding)
    {
        _meshBindings.Remove(binding);
        AllMeshBindings.Remove(binding);
        MeshFilterReservations.Remove(binding.MeshFilter.Pointer);
    }

    private void RemoveAllMeshes()
    {
        foreach (var binding in _meshBindings.ToArray().Reverse())
        {
            try { binding.Unbind(); }
            catch (Exception exception)
            {
                logger.Error(exception, "MeshFilter cleanup failed.");
            }
        }
        foreach (var mesh in _meshes.ToArray().Reverse())
        {
            try { mesh.Unload(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Mesh '{mesh.Name}' cleanup failed.");
            }
        }
    }

    private sealed class ModMesh(
        ModAssets owner,
        MeshBridge bridge,
        string name,
        nint mesh,
        ValidatedMeshGeometry geometry,
        bool readable) : IModMesh
    {
        private nint _mesh = mesh;
        private bool _readable = readable;
        private int _vertexCount = geometry.Vertices.Length;
        private int _indexCount = geometry.IndexCount;
        private int _subMeshCount = geometry.SubMeshes.Length;

        internal ModAssets RuntimeOwner => owner;
        public string OwnerId => owner.ownerId;
        public string Name { get; } = name;
        public UnityObject Mesh => new(_mesh);
        public bool IsLoaded
        {
            get
            {
                EnsureMainThread();
                return _mesh != 0 && bridge.IsObjectAlive(_mesh);
            }
        }
        public bool IsReadable => _readable && IsLoaded;
        public int VertexCount => RequireLoaded(() => _vertexCount);
        public int IndexCount => RequireLoaded(() => _indexCount);
        public int SubMeshCount => RequireLoaded(() => _subMeshCount);
        public IReadOnlyList<IModMeshBinding> ActiveBindings =>
            owner._meshBindings.ToArray()
                .Where(binding => binding.IsBound && ReferenceEquals(binding.RuntimeMesh, this))
                .Cast<IModMeshBinding>()
                .ToArray();

        public void Update(ModMeshGeometry geometry, bool uploadMeshData = false)
        {
            EnsureMainThread();
            if (!IsLoaded) throw new ObjectDisposedException(Name);
            if (!_readable)
                throw new InvalidOperationException(
                    $"Mesh '{Name}' was uploaded as non-readable and cannot be updated.");
            var validated = ValidatedMeshGeometry.Create(geometry);
            bridge.Apply(_mesh, validated, clear: true, markDynamic: false, uploadMeshData);
            _vertexCount = validated.Vertices.Length;
            _indexCount = validated.IndexCount;
            _subMeshCount = validated.SubMeshes.Length;
            _readable = !uploadMeshData;
        }

        public void Unload()
        {
            EnsureMainThread();
            if (_mesh == 0) return;
            Exception? failure = null;
            foreach (var binding in owner._meshBindings.ToArray()
                         .Where(item => ReferenceEquals(item.RuntimeMesh, this))
                         .Reverse())
            {
                try { binding.Unbind(); }
                catch (Exception exception) { failure ??= exception; }
            }
            if (failure is not null)
                throw new InvalidOperationException(
                    $"Mesh '{Name}' bindings could not be restored.", failure);
            bridge.Destroy(_mesh);
            _mesh = 0;
            _readable = false;
            owner.Remove(this);
        }

        public void Dispose() => Unload();

        private T RequireLoaded<T>(Func<T> operation)
        {
            EnsureMainThread();
            if (!IsLoaded) throw new ObjectDisposedException(Name);
            return operation();
        }
    }

    private sealed class ModMeshBinding(
        ModAssets owner,
        MeshBridge bridge,
        nint meshFilter,
        ModMesh mesh,
        nint originalMesh) : IModMeshBinding
    {
        private bool _bound = true;
        private int _pollingFailures;

        internal ModMesh RuntimeMesh => mesh;
        public string OwnerId => owner.ownerId;
        public UnityObject MeshFilter { get; } = new(meshFilter);
        public IModMesh Mesh => mesh;
        public UnityObject OriginalMesh { get; } = new(originalMesh);
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
            if (bridge.IsObjectAlive(meshFilter) &&
                bridge.GetSharedMesh(meshFilter) == mesh.Mesh.Pointer)
                bridge.SetSharedMesh(meshFilter, originalMesh);
            Release();
        }

        public void Dispose() => Unbind();

        internal void Refresh()
        {
            if (!_bound) return;
            if (!bridge.IsObjectAlive(meshFilter) ||
                bridge.GetSharedMesh(meshFilter) != mesh.Mesh.Pointer)
                Release();
            _pollingFailures = 0;
        }

        internal void RecordPollingFailure(Exception exception)
        {
            ++_pollingFailures;
            if (_pollingFailures == 1 || _pollingFailures == 10 || _pollingFailures % 300 == 0)
                owner.logger.Error(
                    exception,
                    $"MeshFilter binding polling failed {_pollingFailures} time(s).");
        }

        private void Release()
        {
            if (!_bound) return;
            _bound = false;
            owner.Remove(this);
        }
    }

    private sealed record ValidatedSubMesh(int[] Indices, ModMeshTopology Topology);

    private sealed record ValidatedMeshGeometry(
        NativeVector3[] Vertices,
        ValidatedSubMesh[] SubMeshes,
        NativeVector3[]? Normals,
        NativeVector4[]? Tangents,
        NativeVector2[]? Uv0,
        NativeColor[]? Colors,
        bool RecalculateNormals,
        bool RecalculateTangents,
        bool RecalculateBounds,
        int IndexCount)
    {
        internal static ValidatedMeshGeometry Create(ModMeshGeometry geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(geometry.Vertices);
            ArgumentNullException.ThrowIfNull(geometry.SubMeshes);
            var vertices = geometry.Vertices.ToArray();
            if (vertices.Length is < 1 or > ModMeshLimits.MaximumVertices)
                throw new ArgumentOutOfRangeException(
                    nameof(geometry),
                    $"Mesh vertex count must be between 1 and {ModMeshLimits.MaximumVertices}.");
            if (geometry.SubMeshes.Count is < 1 or > ModMeshLimits.MaximumSubMeshes)
                throw new ArgumentOutOfRangeException(
                    nameof(geometry),
                    $"Mesh submesh count must be between 1 and {ModMeshLimits.MaximumSubMeshes}.");
            ValidateFinite(vertices, "vertices");

            var totalIndices = 0;
            var subMeshes = new ValidatedSubMesh[geometry.SubMeshes.Count];
            for (var subMeshIndex = 0; subMeshIndex < geometry.SubMeshes.Count; ++subMeshIndex)
            {
                var source = geometry.SubMeshes[subMeshIndex]
                    ?? throw new ArgumentException($"Submesh {subMeshIndex} is null.", nameof(geometry));
                if (!Enum.IsDefined(source.Topology))
                    throw new ArgumentOutOfRangeException(
                        nameof(geometry), $"Submesh {subMeshIndex} has an unknown topology.");
                ArgumentNullException.ThrowIfNull(source.Indices);
                var indices = source.Indices.ToArray();
                ValidatePrimitiveCount(indices.Length, source.Topology, subMeshIndex);
                totalIndices = checked(totalIndices + indices.Length);
                if (totalIndices > ModMeshLimits.MaximumIndices)
                    throw new ArgumentOutOfRangeException(
                        nameof(geometry),
                        $"Mesh index count exceeds {ModMeshLimits.MaximumIndices}.");
                for (var index = 0; index < indices.Length; ++index)
                {
                    if ((uint)indices[index] >= (uint)vertices.Length)
                        throw new ArgumentOutOfRangeException(
                            nameof(geometry),
                            $"Submesh {subMeshIndex} index {indices[index]} is outside the vertex array.");
                }
                subMeshes[subMeshIndex] = new ValidatedSubMesh(indices, source.Topology);
            }

            var normals = ConvertOptional(
                geometry.Normals, vertices.Length, value => new NativeVector3(value.X, value.Y, value.Z),
                "normals", value => AreFinite(value.X, value.Y, value.Z));
            var tangents = ConvertOptional(
                geometry.Tangents, vertices.Length,
                value => new NativeVector4(value.X, value.Y, value.Z, value.W),
                "tangents", value => AreFinite(value.X, value.Y, value.Z, value.W));
            var uv = ConvertOptional(
                geometry.Uv0, vertices.Length, value => new NativeVector2(value.X, value.Y),
                "UV0", value => AreFinite(value.X, value.Y));
            var colors = ConvertOptional(
                geometry.Colors, vertices.Length,
                value => new NativeColor(value.R, value.G, value.B, value.A),
                "colors", value => AreFinite(value.R, value.G, value.B, value.A));
            if (geometry.RecalculateTangents && uv is null)
                throw new ArgumentException(
                    "RecalculateTangents requires a complete UV0 channel.", nameof(geometry));
            if (geometry.RecalculateTangents && normals is null && !geometry.RecalculateNormals)
                throw new ArgumentException(
                    "RecalculateTangents requires normals or RecalculateNormals.", nameof(geometry));

            return new ValidatedMeshGeometry(
                vertices.Select(value => new NativeVector3(value.X, value.Y, value.Z)).ToArray(),
                subMeshes,
                normals,
                tangents,
                uv,
                colors,
                geometry.RecalculateNormals,
                geometry.RecalculateTangents,
                geometry.RecalculateBounds,
                totalIndices);
        }

        private static TNative[]? ConvertOptional<TSource, TNative>(
            IReadOnlyList<TSource>? source,
            int vertexCount,
            Func<TSource, TNative> convert,
            string channel,
            Func<TSource, bool> isValid)
        {
            if (source is null) return null;
            if (source.Count != vertexCount)
                throw new ArgumentException(
                    $"Mesh {channel} count must match its vertex count.", nameof(source));
            var result = new TNative[source.Count];
            for (var index = 0; index < source.Count; ++index)
            {
                if (!isValid(source[index]))
                    throw new ArgumentOutOfRangeException(
                        nameof(source), $"Mesh {channel} contains a non-finite value.");
                result[index] = convert(source[index]);
            }
            return result;
        }

        private static void ValidateFinite(IEnumerable<UnityVector3> values, string channel)
        {
            if (values.Any(value => !AreFinite(value.X, value.Y, value.Z)))
                throw new ArgumentOutOfRangeException(
                    nameof(values), $"Mesh {channel} contains a non-finite value.");
        }

        private static bool AreFinite(params float[] values) =>
            values.All(float.IsFinite);

        private static void ValidatePrimitiveCount(
            int count,
            ModMeshTopology topology,
            int subMesh)
        {
            var valid = topology switch
            {
                ModMeshTopology.Triangles => count >= 3 && count % 3 == 0,
                ModMeshTopology.Quads => count >= 4 && count % 4 == 0,
                ModMeshTopology.Lines => count >= 2 && count % 2 == 0,
                ModMeshTopology.LineStrip => count >= 2,
                ModMeshTopology.Points => count >= 1,
                _ => false,
            };
            if (!valid)
                throw new ArgumentException(
                    $"Submesh {subMesh} index count {count} is invalid for {topology}.");
        }
    }

    internal static void ValidateMeshGeometryForTests(ModMeshGeometry geometry) =>
        _ = ValidatedMeshGeometry.Create(geometry);

    private sealed class MeshBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint ResolveIcallDelegate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CreateMeshDelegate(nint managedMesh);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetChannelDelegate(
            nint mesh,
            int channel,
            int format,
            int dimension,
            nint values,
            int arraySize,
            int valuesStart,
            int valuesCount,
            int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetSubMeshCountDelegate(nint mesh, int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetIndicesDelegate(
            nint mesh,
            int subMesh,
            int topology,
            int indexFormat,
            nint indices,
            int arrayStart,
            int arraySize,
            [MarshalAs(UnmanagedType.I1)] bool calculateBounds,
            int baseVertex);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ClearDelegate(
            nint mesh,
            [MarshalAs(UnmanagedType.I1)] bool keepVertexLayout);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MeshFlagsDelegate(nint mesh, int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MeshActionDelegate(nint mesh);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UploadMeshDelegate(
            nint mesh,
            [MarshalAs(UnmanagedType.I1)] bool markNoLongerReadable);

        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _meshClass;
        private readonly nint _meshFilterClass;
        private readonly nint _vector2Class;
        private readonly nint _vector3Class;
        private readonly nint _vector4Class;
        private readonly nint _colorClass;
        private readonly nint _int32Class;
        private readonly nint _getSharedMesh;
        private readonly nint _setSharedMesh;
        private readonly nint _setName;
        private readonly nint _destroy;
        private readonly nint _objectImplicit;
        private readonly int _cachedPointerOffset;
        private readonly CreateMeshDelegate _create;
        private readonly SetChannelDelegate _setChannel;
        private readonly SetSubMeshCountDelegate _setSubMeshCount;
        private readonly SetIndicesDelegate _setIndices;
        private readonly ClearDelegate _clear;
        private readonly MeshFlagsDelegate _recalculateNormals;
        private readonly MeshFlagsDelegate _recalculateTangents;
        private readonly MeshFlagsDelegate _recalculateBounds;
        private readonly MeshActionDelegate _markDynamic;
        private readonly UploadMeshDelegate _upload;

        internal MeshBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            const string core = "UnityEngine.CoreModule.dll";
            _meshClass = RequireClass(api, core, "UnityEngine", "Mesh");
            _meshFilterClass = RequireClass(api, core, "UnityEngine", "MeshFilter");
            _vector2Class = RequireClass(api, core, "UnityEngine", "Vector2");
            _vector3Class = RequireClass(api, core, "UnityEngine", "Vector3");
            _vector4Class = RequireClass(api, core, "UnityEngine", "Vector4");
            _colorClass = RequireClass(api, core, "UnityEngine", "Color");
            _int32Class = RequireClass(api, "mscorlib.dll", "System", "Int32");
            var objectClass = RequireClass(api, core, "UnityEngine", "Object");
            _getSharedMesh = RequireMethod(api, _meshFilterClass, "get_sharedMesh", 0);
            _setSharedMesh = RequireSignature(
                api, _meshFilterClass, "set_sharedMesh", "UnityEngine.Mesh");
            _setName = RequireSignature(api, objectClass, "set_name", "System.String");
            _destroy = RequireSignature(api, objectClass, "Destroy", "UnityEngine.Object");
            _objectImplicit = RequireSignature(api, objectClass, "op_Implicit", "UnityEngine.Object");
            var cachedPointer = api.FindField(objectClass, "m_CachedPtr");
            if (cachedPointer == 0)
                throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
            _cachedPointerOffset = api.GetFieldOffset(cachedPointer);

            var resolvePointer = NativeLibrary.GetExport(
                api.GameAssemblyModule, "il2cpp_resolve_icall");
            var resolve = Marshal.GetDelegateForFunctionPointer<ResolveIcallDelegate>(resolvePointer);
            _create = Resolve<CreateMeshDelegate>(resolve, "UnityEngine.Mesh::Internal_Create");
            _setChannel = Resolve<SetChannelDelegate>(
                resolve, "UnityEngine.Mesh::SetArrayForChannelImpl_Injected");
            _setSubMeshCount = Resolve<SetSubMeshCountDelegate>(
                resolve, "UnityEngine.Mesh::set_subMeshCount_Injected");
            _setIndices = Resolve<SetIndicesDelegate>(
                resolve, "UnityEngine.Mesh::SetIndicesImpl_Injected");
            _clear = Resolve<ClearDelegate>(resolve, "UnityEngine.Mesh::ClearImpl_Injected");
            _recalculateNormals = Resolve<MeshFlagsDelegate>(
                resolve, "UnityEngine.Mesh::RecalculateNormalsImpl_Injected");
            _recalculateTangents = Resolve<MeshFlagsDelegate>(
                resolve, "UnityEngine.Mesh::RecalculateTangentsImpl_Injected");
            _recalculateBounds = Resolve<MeshFlagsDelegate>(
                resolve, "UnityEngine.Mesh::RecalculateBoundsImpl_Injected");
            _markDynamic = Resolve<MeshActionDelegate>(
                resolve, "UnityEngine.Mesh::MarkDynamicImpl_Injected");
            _upload = Resolve<UploadMeshDelegate>(
                resolve, "UnityEngine.Mesh::UploadMeshDataImpl_Injected");
        }

        internal nint Create(string name)
        {
            var mesh = _api.NewObject(_meshClass);
            _create(mesh);
            _ = _api.Invoke(
                _setName,
                mesh,
                Il2CppArgument.FromReference(_api.NewString($"OFS {name}")));
            return mesh;
        }

        internal void Apply(
            nint mesh,
            ValidatedMeshGeometry geometry,
            bool clear,
            bool markDynamic,
            bool uploadMeshData)
        {
            var native = NativePointer(mesh);
            if (clear) _clear(native, keepVertexLayout: false);
            SetChannel(native, 0, 3, _vector3Class, geometry.Vertices);
            if (geometry.Normals is not null)
                SetChannel(native, 1, 3, _vector3Class, geometry.Normals);
            if (geometry.Tangents is not null)
                SetChannel(native, 2, 4, _vector4Class, geometry.Tangents);
            if (geometry.Colors is not null)
                SetChannel(native, 3, 4, _colorClass, geometry.Colors);
            if (geometry.Uv0 is not null)
                SetChannel(native, 4, 2, _vector2Class, geometry.Uv0);

            _setSubMeshCount(native, geometry.SubMeshes.Length);
            for (var index = 0; index < geometry.SubMeshes.Length; ++index)
            {
                var subMesh = geometry.SubMeshes[index];
                var array = NewValueArray(_int32Class, subMesh.Indices);
                _setIndices(
                    native,
                    index,
                    (int)subMesh.Topology,
                    1,
                    array,
                    0,
                    subMesh.Indices.Length,
                    calculateBounds: false,
                    baseVertex: 0);
            }

            if (geometry.RecalculateNormals) _recalculateNormals(native, 0);
            if (geometry.RecalculateTangents) _recalculateTangents(native, 0);
            if (geometry.RecalculateBounds) _recalculateBounds(native, 0);
            if (markDynamic) _markDynamic(native);
            if (uploadMeshData) _upload(native, markNoLongerReadable: true);
        }

        internal nint GetSharedMesh(nint meshFilter)
        {
            RequireMeshFilter(meshFilter);
            return _api.Invoke(_getSharedMesh, meshFilter);
        }

        internal void SetSharedMesh(nint meshFilter, nint mesh)
        {
            RequireMeshFilter(meshFilter);
            if (mesh != 0 && (!IsObjectAlive(mesh) ||
                !_api.IsAssignableFrom(_meshClass, _api.GetObjectClass(mesh))))
                throw new ArgumentException("Unity object is not a live Mesh.", nameof(mesh));
            _ = _api.Invoke(
                _setSharedMesh,
                meshFilter,
                Il2CppArgument.FromReference(mesh));
        }

        internal bool IsObjectAlive(nint instance)
        {
            if (instance == 0) return false;
            var boxed = _api.Invoke(
                _objectImplicit, 0, Il2CppArgument.FromReference(instance));
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
        }

        internal void Destroy(nint instance)
        {
            if (instance == 0 || !IsObjectAlive(instance)) return;
            _ = _api.Invoke(_destroy, 0, Il2CppArgument.FromReference(instance));
        }

        private void SetChannel<T>(
            nint nativeMesh,
            int channel,
            int dimension,
            nint elementClass,
            T[] values) where T : unmanaged
        {
            var array = NewValueArray(elementClass, values);
            _setChannel(
                nativeMesh,
                channel,
                0,
                dimension,
                array,
                values.Length,
                0,
                values.Length,
                0);
        }

        private nint NewValueArray<T>(nint elementClass, T[] values) where T : unmanaged
        {
            var array = _api.NewArray(elementClass, checked((nuint)values.Length));
            if (values.Length == 0) return array;
            var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            Marshal.Copy(bytes, 0, array + (4 * nint.Size), bytes.Length);
            return array;
        }

        private nint NativePointer(nint unityObject)
        {
            var pointer = Marshal.ReadIntPtr(unityObject, _cachedPointerOffset);
            return pointer != 0
                ? pointer
                : throw new InvalidOperationException("Unity object has no native pointer.");
        }

        private void RequireMeshFilter(nint instance)
        {
            if (instance == 0 || !IsObjectAlive(instance) ||
                !_api.IsAssignableFrom(_meshFilterClass, _api.GetObjectClass(instance)))
                throw new ArgumentException("Unity object is not a live MeshFilter.", nameof(instance));
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

        private static nint RequireMethod(
            IUnsafeIl2CppApi api,
            nint klass,
            string name,
            int argumentCount)
        {
            var method = api.FindMethod(klass, name, argumentCount);
            return method != 0
                ? method
                : throw new MissingMethodException($"{name}/{argumentCount}");
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

        private static T Resolve<T>(ResolveIcallDelegate resolve, string name)
            where T : Delegate
        {
            var pointer = resolve(name);
            return pointer != 0
                ? Marshal.GetDelegateForFunctionPointer<T>(pointer)
                : throw new MissingMethodException($"Native Unity icall '{name}' was not resolved.");
        }
    }
}
