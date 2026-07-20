using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class PhysicsApi : IModPhysicsApi
{
    private static readonly List<PhysicsApi> AllApis = [];
    private readonly string _ownerId;
    private readonly IUnityApi _unity;
    private readonly IUnsafeIl2CppApi _unsafeApi;
    private readonly ModRuntime.ModLogger _logger;
    private PhysicsBridge? _bridge;
    private readonly List<ModCollider> _colliders = [];
    private readonly List<ModRigidbody> _rigidbodies = [];

    internal PhysicsApi(
        string ownerId,
        IUnityApi unity,
        IUnsafeIl2CppApi unsafeApi,
        ModRuntime.ModLogger logger)
    {
        _ownerId = ownerId;
        _unity = unity;
        _unsafeApi = unsafeApi;
        _logger = logger;
        AllApis.Add(this);
    }

    public IReadOnlyList<IModCollider> Colliders
    {
        get
        {
            EnsureMainThread();
            Poll();
            return _colliders.Cast<IModCollider>().ToArray();
        }
    }

    public IReadOnlyList<IModRigidbody> Rigidbodies
    {
        get
        {
            EnsureMainThread();
            Poll();
            return _rigidbodies.Cast<IModRigidbody>().ToArray();
        }
    }

    public IModBoxCollider AddBoxCollider(
        UnityObject gameObject,
        ModBoxColliderDefinition? definition = null)
    {
        EnsureMainThread();
        definition ??= new ModBoxColliderDefinition();
        ValidateVector(definition.Center, nameof(definition.Center));
        ValidatePositiveVector(definition.Size, nameof(definition.Size));
        var component = AddComponent(gameObject, "BoxCollider");
        try
        {
            var result = new ModBoxCollider(this, gameObject.Pointer, component.Pointer);
            result.Center = definition.Center;
            result.Size = definition.Size;
            ApplyCommon(result, definition.IsTrigger, definition.Enabled);
            _colliders.Add(result);
            return result;
        }
        catch
        {
            DestroySilently(component.Pointer);
            throw;
        }
    }

    public IModSphereCollider AddSphereCollider(
        UnityObject gameObject,
        ModSphereColliderDefinition? definition = null)
    {
        EnsureMainThread();
        definition ??= new ModSphereColliderDefinition();
        ValidateVector(definition.Center, nameof(definition.Center));
        ValidatePositive(definition.Radius, nameof(definition.Radius));
        var component = AddComponent(gameObject, "SphereCollider");
        try
        {
            var result = new ModSphereCollider(this, gameObject.Pointer, component.Pointer);
            result.Center = definition.Center;
            result.Radius = definition.Radius;
            ApplyCommon(result, definition.IsTrigger, definition.Enabled);
            _colliders.Add(result);
            return result;
        }
        catch
        {
            DestroySilently(component.Pointer);
            throw;
        }
    }

    public IModCapsuleCollider AddCapsuleCollider(
        UnityObject gameObject,
        ModCapsuleColliderDefinition? definition = null)
    {
        EnsureMainThread();
        definition ??= new ModCapsuleColliderDefinition();
        ValidateVector(definition.Center, nameof(definition.Center));
        ValidatePositive(definition.Radius, nameof(definition.Radius));
        ValidatePositive(definition.Height, nameof(definition.Height));
        ValidateEnum(definition.Direction, nameof(definition.Direction));
        var component = AddComponent(gameObject, "CapsuleCollider");
        try
        {
            var result = new ModCapsuleCollider(this, gameObject.Pointer, component.Pointer);
            result.Center = definition.Center;
            result.Radius = definition.Radius;
            result.Height = definition.Height;
            result.Direction = definition.Direction;
            ApplyCommon(result, definition.IsTrigger, definition.Enabled);
            _colliders.Add(result);
            return result;
        }
        catch
        {
            DestroySilently(component.Pointer);
            throw;
        }
    }

    public IModMeshCollider AddMeshCollider(
        UnityObject gameObject,
        ModMeshColliderDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Mesh.IsNull)
            throw new ArgumentException("A MeshCollider requires a non-null Mesh.", nameof(definition));
        if (definition.IsTrigger && !definition.Convex)
            throw new ArgumentException("A trigger MeshCollider must be convex.", nameof(definition));
        Bridge.RequireMesh(definition.Mesh.Pointer);
        var component = AddComponent(gameObject, "MeshCollider");
        try
        {
            var result = new ModMeshCollider(this, gameObject.Pointer, component.Pointer);
            result.SharedMesh = definition.Mesh;
            result.Convex = definition.Convex;
            ApplyCommon(result, definition.IsTrigger, definition.Enabled);
            _colliders.Add(result);
            return result;
        }
        catch
        {
            DestroySilently(component.Pointer);
            throw;
        }
    }

    public IModRigidbody AddRigidbody(
        UnityObject gameObject,
        ModRigidbodyDefinition? definition = null)
    {
        EnsureMainThread();
        RequireGameObject(gameObject);
        definition ??= new ModRigidbodyDefinition();
        ValidateRigidbodyDefinition(definition);
        var existing = _unity.TryGetComponent(
            gameObject, PhysicsBridge.PhysicsAssembly, "UnityEngine", "Rigidbody");
        if (!existing.IsNull)
            throw new InvalidOperationException("The GameObject already has a Rigidbody.");
        var component = AddComponent(gameObject, "Rigidbody");
        try
        {
            var result = new ModRigidbody(this, gameObject.Pointer, component.Pointer)
            {
                Mass = definition.Mass,
                LinearDamping = definition.LinearDamping,
                AngularDamping = definition.AngularDamping,
                UseGravity = definition.UseGravity,
                IsKinematic = definition.IsKinematic,
                DetectCollisions = definition.DetectCollisions,
                Constraints = definition.Constraints,
                CollisionDetection = definition.CollisionDetection,
                Interpolation = definition.Interpolation,
            };
            _rigidbodies.Add(result);
            return result;
        }
        catch
        {
            DestroySilently(component.Pointer);
            throw;
        }
    }

    public bool CheckSphere(
        UnityVector3 center,
        float radius,
        int layerMask = ModPhysicsLayers.DefaultRaycast,
        ModQueryTriggerInteraction queryTriggers = ModQueryTriggerInteraction.UseGlobal)
    {
        EnsureMainThread();
        ValidateVector(center, nameof(center));
        ValidatePositive(radius, nameof(radius));
        ValidateEnum(queryTriggers, nameof(queryTriggers));
        return Bridge.CheckSphere(center, radius, layerMask, queryTriggers);
    }

    public bool CheckBox(
        UnityVector3 center,
        UnityVector3 halfExtents,
        UnityQuaternion orientation,
        int layerMask = ModPhysicsLayers.DefaultRaycast,
        ModQueryTriggerInteraction queryTriggers = ModQueryTriggerInteraction.UseGlobal)
    {
        EnsureMainThread();
        ValidateVector(center, nameof(center));
        ValidatePositiveVector(halfExtents, nameof(halfExtents));
        ValidateQuaternion(orientation, nameof(orientation));
        ValidateEnum(queryTriggers, nameof(queryTriggers));
        return Bridge.CheckBox(center, halfExtents, orientation, layerMask, queryTriggers);
    }

    public bool CheckCapsule(
        UnityVector3 start,
        UnityVector3 end,
        float radius,
        int layerMask = ModPhysicsLayers.DefaultRaycast,
        ModQueryTriggerInteraction queryTriggers = ModQueryTriggerInteraction.UseGlobal)
    {
        EnsureMainThread();
        ValidateVector(start, nameof(start));
        ValidateVector(end, nameof(end));
        ValidatePositive(radius, nameof(radius));
        ValidateEnum(queryTriggers, nameof(queryTriggers));
        return Bridge.CheckCapsule(start, end, radius, layerMask, queryTriggers);
    }

    public bool Raycast(
        UnityVector3 origin,
        UnityVector3 direction,
        out ModRaycastHit hit,
        float maxDistance = float.PositiveInfinity,
        int layerMask = ModPhysicsLayers.DefaultRaycast,
        ModQueryTriggerInteraction queryTriggers = ModQueryTriggerInteraction.UseGlobal)
    {
        EnsureMainThread();
        ValidateVector(origin, nameof(origin));
        ValidateVector(direction, nameof(direction));
        if (LengthSquared(direction) <= float.Epsilon)
            throw new ArgumentOutOfRangeException(nameof(direction), "Ray direction cannot be zero.");
        if (float.IsNaN(maxDistance) || maxDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));
        ValidateEnum(queryTriggers, nameof(queryTriggers));
        return Bridge.Raycast(origin, direction, maxDistance, layerMask, queryTriggers, out hit);
    }

    public void SyncTransforms()
    {
        EnsureMainThread();
        Bridge.SyncTransforms();
    }

    internal static void PollAll()
    {
        EnsureMainThread();
        foreach (var api in AllApis.ToArray()) api.Poll();
    }

    internal void RemoveAll()
    {
        if (_rigidbodies.Count == 0 && _colliders.Count == 0)
        {
            AllApis.Remove(this);
            return;
        }
        EnsureMainThread();
        foreach (var body in _rigidbodies.ToArray().Reverse()) Remove(body, destroy: true);
        foreach (var collider in _colliders.ToArray().Reverse()) Remove(collider, destroy: true);
        AllApis.Remove(this);
    }

    internal PhysicsBridge Bridge => _bridge ??= new PhysicsBridge(_unsafeApi);
    internal string OwnerId => _ownerId;

    private void Remove(ModCollider collider, bool destroy)
    {
        EnsureMainThread();
        if (!_colliders.Remove(collider)) return;
        var pointer = collider.Release();
        if (destroy && pointer != 0 && Bridge.IsObjectAlive(pointer)) Bridge.Destroy(pointer);
    }

    private void Remove(ModRigidbody body, bool destroy)
    {
        EnsureMainThread();
        if (!_rigidbodies.Remove(body)) return;
        var pointer = body.Release();
        if (destroy && pointer != 0 && Bridge.IsObjectAlive(pointer)) Bridge.Destroy(pointer);
    }

    private void Poll()
    {
        foreach (var body in _rigidbodies.ToArray())
            if (!body.IsAlive) Remove(body, destroy: false);
        foreach (var collider in _colliders.ToArray())
            if (!collider.IsAlive) Remove(collider, destroy: false);
    }

    private UnityObject AddComponent(UnityObject gameObject, string className)
    {
        RequireGameObject(gameObject);
        var component = _unity.AddComponent(
            gameObject, PhysicsBridge.PhysicsAssembly, "UnityEngine", className);
        if (component.IsNull)
            throw new InvalidOperationException($"Unity did not create {className}.");
        return component;
    }

    private static void ApplyCommon(
        ModCollider collider,
        bool isTrigger,
        bool enabled)
    {
        collider.IsTrigger = isTrigger;
        collider.Enabled = enabled;
    }

    private void DestroySilently(nint component)
    {
        if (component == 0) return;
        try { if (Bridge.IsObjectAlive(component)) Bridge.Destroy(component); }
        catch (Exception exception) { _logger.Error(exception, "Physics component rollback failed."); }
    }

    private static void RequireGameObject(UnityObject gameObject)
    {
        if (gameObject.IsNull) throw new ArgumentException("GameObject is null.", nameof(gameObject));
    }

    internal static void ValidateRigidbodyDefinition(ModRigidbodyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidatePositive(definition.Mass, nameof(definition.Mass));
        ValidateNonNegative(definition.LinearDamping, nameof(definition.LinearDamping));
        ValidateNonNegative(definition.AngularDamping, nameof(definition.AngularDamping));
        ValidateEnum(definition.CollisionDetection, nameof(definition.CollisionDetection));
        ValidateEnum(definition.Interpolation, nameof(definition.Interpolation));
        const ModRigidbodyConstraints valid = ModRigidbodyConstraints.FreezeAll;
        if ((definition.Constraints & ~valid) != 0)
            throw new ArgumentOutOfRangeException(nameof(definition.Constraints));
    }

    internal static void ValidateColliderDefinitionForTests(object definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        switch (definition)
        {
            case ModBoxColliderDefinition box:
                ValidateVector(box.Center, nameof(box.Center));
                ValidatePositiveVector(box.Size, nameof(box.Size));
                break;
            case ModSphereColliderDefinition sphere:
                ValidateVector(sphere.Center, nameof(sphere.Center));
                ValidatePositive(sphere.Radius, nameof(sphere.Radius));
                break;
            case ModCapsuleColliderDefinition capsule:
                ValidateVector(capsule.Center, nameof(capsule.Center));
                ValidatePositive(capsule.Radius, nameof(capsule.Radius));
                ValidatePositive(capsule.Height, nameof(capsule.Height));
                ValidateEnum(capsule.Direction, nameof(capsule.Direction));
                break;
            case ModMeshColliderDefinition mesh:
                if (mesh.Mesh.IsNull) throw new ArgumentException("Mesh is null.", nameof(definition));
                if (mesh.IsTrigger && !mesh.Convex)
                    throw new ArgumentException("A trigger MeshCollider must be convex.", nameof(definition));
                break;
            default:
                throw new ArgumentException("Unknown collider definition.", nameof(definition));
        }
    }

    internal static void ValidateVector(UnityVector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "Vector components must be finite.");
    }

    internal static void ValidateQuaternion(UnityQuaternion value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W) ||
            value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W <=
            float.Epsilon)
            throw new ArgumentOutOfRangeException(name, "Quaternion must be finite and non-zero.");
    }

    internal static void ValidatePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(name);
    }

    internal static void ValidateNonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidatePositiveVector(UnityVector3 value, string name)
    {
        ValidateVector(value, name);
        if (value.X <= 0f || value.Y <= 0f || value.Z <= 0f)
            throw new ArgumentOutOfRangeException(name, "Vector components must be positive.");
    }

    internal static void ValidateEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name);
    }

    private static float LengthSquared(UnityVector3 value) =>
        value.X * value.X + value.Y * value.Y + value.Z * value.Z;

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException(
                "Physics API calls must run on the Unity main thread. Use context.MainThread.Post().");
    }

    private abstract class ModCollider(
        PhysicsApi owner,
        nint gameObject,
        nint collider,
        ModColliderKind kind) : IModCollider
    {
        private nint _collider = collider;
        protected PhysicsBridge Bridge => owner.Bridge;
        protected nint Pointer => RequireAlive();
        public string OwnerId => owner.OwnerId;
        public UnityObject GameObject { get; } = new(gameObject);
        public UnityObject Collider => new(_collider);
        public ModColliderKind Kind { get; } = kind;
        public bool IsAlive => _collider != 0 && Bridge.IsObjectAlive(_collider);
        public bool Enabled
        {
            get => Bridge.GetEnabled(Pointer);
            set => Bridge.SetEnabled(Pointer, value);
        }
        public virtual bool IsTrigger
        {
            get => Bridge.GetIsTrigger(Pointer);
            set => Bridge.SetIsTrigger(Pointer, value);
        }
        public void Remove() => owner.Remove(this, destroy: true);
        public void Dispose() => Remove();
        internal nint Release()
        {
            var value = _collider;
            _collider = 0;
            return value;
        }
        protected nint RequireAlive()
        {
            EnsureMainThread();
            if (!IsAlive) throw new ObjectDisposedException(GetType().Name);
            return _collider;
        }
    }

    private sealed class ModBoxCollider(PhysicsApi owner, nint gameObject, nint collider)
        : ModCollider(owner, gameObject, collider, ModColliderKind.Box), IModBoxCollider
    {
        public UnityVector3 Center
        {
            get => Bridge.GetBoxCenter(Pointer);
            set { ValidateVector(value, nameof(value)); Bridge.SetBoxCenter(Pointer, value); }
        }
        public UnityVector3 Size
        {
            get => Bridge.GetBoxSize(Pointer);
            set { ValidatePositiveVector(value, nameof(value)); Bridge.SetBoxSize(Pointer, value); }
        }
    }

    private sealed class ModSphereCollider(PhysicsApi owner, nint gameObject, nint collider)
        : ModCollider(owner, gameObject, collider, ModColliderKind.Sphere), IModSphereCollider
    {
        public UnityVector3 Center
        {
            get => Bridge.GetSphereCenter(Pointer);
            set { ValidateVector(value, nameof(value)); Bridge.SetSphereCenter(Pointer, value); }
        }
        public float Radius
        {
            get => Bridge.GetSphereRadius(Pointer);
            set { ValidatePositive(value, nameof(value)); Bridge.SetSphereRadius(Pointer, value); }
        }
    }

    private sealed class ModCapsuleCollider(PhysicsApi owner, nint gameObject, nint collider)
        : ModCollider(owner, gameObject, collider, ModColliderKind.Capsule), IModCapsuleCollider
    {
        public UnityVector3 Center
        {
            get => Bridge.GetCapsuleCenter(Pointer);
            set { ValidateVector(value, nameof(value)); Bridge.SetCapsuleCenter(Pointer, value); }
        }
        public float Radius
        {
            get => Bridge.GetCapsuleRadius(Pointer);
            set { ValidatePositive(value, nameof(value)); Bridge.SetCapsuleRadius(Pointer, value); }
        }
        public float Height
        {
            get => Bridge.GetCapsuleHeight(Pointer);
            set { ValidatePositive(value, nameof(value)); Bridge.SetCapsuleHeight(Pointer, value); }
        }
        public ModCapsuleDirection Direction
        {
            get => (ModCapsuleDirection)Bridge.GetCapsuleDirection(Pointer);
            set { ValidateEnum(value, nameof(value)); Bridge.SetCapsuleDirection(Pointer, (int)value); }
        }
    }

    private sealed class ModMeshCollider(PhysicsApi owner, nint gameObject, nint collider)
        : ModCollider(owner, gameObject, collider, ModColliderKind.Mesh), IModMeshCollider
    {
        public UnityObject SharedMesh
        {
            get => new(Bridge.GetMeshColliderMesh(Pointer));
            set
            {
                if (!value.IsNull) Bridge.RequireMesh(value.Pointer);
                Bridge.SetMeshColliderMesh(Pointer, value.Pointer);
            }
        }
        public bool Convex
        {
            get => Bridge.GetMeshColliderConvex(Pointer);
            set
            {
                if (!value && IsTrigger)
                    throw new InvalidOperationException("A trigger MeshCollider must remain convex.");
                Bridge.SetMeshColliderConvex(Pointer, value);
            }
        }
        public override bool IsTrigger
        {
            get => base.IsTrigger;
            set
            {
                if (value && !Convex)
                    throw new InvalidOperationException("A trigger MeshCollider must be convex.");
                base.IsTrigger = value;
            }
        }
    }

    private sealed class ModRigidbody(PhysicsApi owner, nint gameObject, nint rigidbody)
        : IModRigidbody
    {
        private nint _rigidbody = rigidbody;
        private PhysicsBridge Bridge => owner.Bridge;
        private nint Pointer => RequireAlive();
        public string OwnerId => owner.OwnerId;
        public UnityObject GameObject { get; } = new(gameObject);
        public UnityObject Rigidbody => new(_rigidbody);
        public bool IsAlive => _rigidbody != 0 && Bridge.IsObjectAlive(_rigidbody);
        public float Mass
        {
            get => Bridge.GetRigidbodyFloat(Pointer, RigidbodyFloatProperty.Mass);
            set { ValidatePositive(value, nameof(value)); Bridge.SetRigidbodyFloat(Pointer, RigidbodyFloatProperty.Mass, value); }
        }
        public bool UseGravity
        {
            get => Bridge.GetRigidbodyBool(Pointer, RigidbodyBoolProperty.UseGravity);
            set => Bridge.SetRigidbodyBool(Pointer, RigidbodyBoolProperty.UseGravity, value);
        }
        public bool IsKinematic
        {
            get => Bridge.GetRigidbodyBool(Pointer, RigidbodyBoolProperty.IsKinematic);
            set => Bridge.SetRigidbodyBool(Pointer, RigidbodyBoolProperty.IsKinematic, value);
        }
        public float LinearDamping
        {
            get => Bridge.GetRigidbodyFloat(Pointer, RigidbodyFloatProperty.LinearDamping);
            set { ValidateNonNegative(value, nameof(value)); Bridge.SetRigidbodyFloat(Pointer, RigidbodyFloatProperty.LinearDamping, value); }
        }
        public float AngularDamping
        {
            get => Bridge.GetRigidbodyFloat(Pointer, RigidbodyFloatProperty.AngularDamping);
            set { ValidateNonNegative(value, nameof(value)); Bridge.SetRigidbodyFloat(Pointer, RigidbodyFloatProperty.AngularDamping, value); }
        }
        public bool DetectCollisions
        {
            get => Bridge.GetRigidbodyBool(Pointer, RigidbodyBoolProperty.DetectCollisions);
            set => Bridge.SetRigidbodyBool(Pointer, RigidbodyBoolProperty.DetectCollisions, value);
        }
        public ModRigidbodyConstraints Constraints
        {
            get => (ModRigidbodyConstraints)Bridge.GetRigidbodyInt(Pointer, RigidbodyIntProperty.Constraints);
            set
            {
                if ((value & ~ModRigidbodyConstraints.FreezeAll) != 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                Bridge.SetRigidbodyInt(Pointer, RigidbodyIntProperty.Constraints, (int)value);
            }
        }
        public ModCollisionDetectionMode CollisionDetection
        {
            get => (ModCollisionDetectionMode)Bridge.GetRigidbodyInt(Pointer, RigidbodyIntProperty.CollisionDetection);
            set { ValidateEnum(value, nameof(value)); Bridge.SetRigidbodyInt(Pointer, RigidbodyIntProperty.CollisionDetection, (int)value); }
        }
        public ModRigidbodyInterpolation Interpolation
        {
            get => (ModRigidbodyInterpolation)Bridge.GetRigidbodyInt(Pointer, RigidbodyIntProperty.Interpolation);
            set { ValidateEnum(value, nameof(value)); Bridge.SetRigidbodyInt(Pointer, RigidbodyIntProperty.Interpolation, (int)value); }
        }
        public UnityVector3 LinearVelocity
        {
            get => Bridge.GetRigidbodyVector(Pointer, angular: false);
            set { ValidateVector(value, nameof(value)); Bridge.SetRigidbodyVector(Pointer, angular: false, value); }
        }
        public UnityVector3 AngularVelocity
        {
            get => Bridge.GetRigidbodyVector(Pointer, angular: true);
            set { ValidateVector(value, nameof(value)); Bridge.SetRigidbodyVector(Pointer, angular: true, value); }
        }
        public bool IsSleeping => Bridge.IsSleeping(Pointer);
        public void AddForce(UnityVector3 force, ModForceMode mode = ModForceMode.Force)
        {
            ValidateVector(force, nameof(force)); ValidateEnum(mode, nameof(mode));
            Bridge.AddForce(Pointer, force, mode);
        }
        public void AddTorque(UnityVector3 torque, ModForceMode mode = ModForceMode.Force)
        {
            ValidateVector(torque, nameof(torque)); ValidateEnum(mode, nameof(mode));
            Bridge.AddTorque(Pointer, torque, mode);
        }
        public void AddForceAtPosition(
            UnityVector3 force,
            UnityVector3 position,
            ModForceMode mode = ModForceMode.Force)
        {
            ValidateVector(force, nameof(force)); ValidateVector(position, nameof(position));
            ValidateEnum(mode, nameof(mode)); Bridge.AddForceAtPosition(Pointer, force, position, mode);
        }
        public void MovePosition(UnityVector3 position)
        {
            ValidateVector(position, nameof(position)); Bridge.MovePosition(Pointer, position);
        }
        public void MoveRotation(UnityQuaternion rotation)
        {
            ValidateQuaternion(rotation, nameof(rotation)); Bridge.MoveRotation(Pointer, rotation);
        }
        public void Sleep() => Bridge.Sleep(Pointer);
        public void WakeUp() => Bridge.WakeUp(Pointer);
        public void Remove() => owner.Remove(this, destroy: true);
        public void Dispose() => Remove();
        internal nint Release()
        {
            var value = _rigidbody;
            _rigidbody = 0;
            return value;
        }
        private nint RequireAlive()
        {
            EnsureMainThread();
            if (!IsAlive) throw new ObjectDisposedException(nameof(IModRigidbody));
            return _rigidbody;
        }
    }

    internal enum RigidbodyFloatProperty { Mass, LinearDamping, AngularDamping }
    internal enum RigidbodyBoolProperty { UseGravity, IsKinematic, DetectCollisions }
    internal enum RigidbodyIntProperty { Constraints, CollisionDetection, Interpolation }

    internal sealed class PhysicsBridge
    {
        internal const string PhysicsAssembly = "UnityEngine.PhysicsModule.dll";
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _objectClass;
        private readonly nint _meshClass;
        private readonly nint _colliderClass;
        private readonly nint _boxClass;
        private readonly nint _sphereClass;
        private readonly nint _capsuleClass;
        private readonly nint _meshColliderClass;
        private readonly nint _rigidbodyClass;
        private readonly nint _destroy;
        private readonly nint _objectImplicit;
        private readonly nint _findObjectFromInstanceId;
        private readonly nint _getGameObject;
        private readonly nint _getEnabled;
        private readonly nint _setEnabled;
        private readonly nint _getTrigger;
        private readonly nint _setTrigger;
        private readonly nint _boxGetCenter;
        private readonly nint _boxSetCenter;
        private readonly nint _boxGetSize;
        private readonly nint _boxSetSize;
        private readonly nint _sphereGetCenter;
        private readonly nint _sphereSetCenter;
        private readonly nint _sphereGetRadius;
        private readonly nint _sphereSetRadius;
        private readonly nint _capsuleGetCenter;
        private readonly nint _capsuleSetCenter;
        private readonly nint _capsuleGetRadius;
        private readonly nint _capsuleSetRadius;
        private readonly nint _capsuleGetHeight;
        private readonly nint _capsuleSetHeight;
        private readonly nint _capsuleGetDirection;
        private readonly nint _capsuleSetDirection;
        private readonly nint _meshGetSharedMesh;
        private readonly nint _meshSetSharedMesh;
        private readonly nint _meshGetConvex;
        private readonly nint _meshSetConvex;
        private readonly Dictionary<RigidbodyFloatProperty, (nint Get, nint Set)> _bodyFloats;
        private readonly Dictionary<RigidbodyBoolProperty, (nint Get, nint Set)> _bodyBools;
        private readonly Dictionary<RigidbodyIntProperty, (nint Get, nint Set)> _bodyInts;
        private readonly nint _bodyGetLinearVelocity;
        private readonly nint _bodySetLinearVelocity;
        private readonly nint _bodyGetAngularVelocity;
        private readonly nint _bodySetAngularVelocity;
        private readonly nint _addForce;
        private readonly nint _addTorque;
        private readonly nint _addForceAtPosition;
        private readonly nint _movePosition;
        private readonly nint _moveRotation;
        private readonly nint _sleep;
        private readonly nint _isSleeping;
        private readonly nint _wakeUp;
        private readonly nint _checkSphere;
        private readonly nint _checkBox;
        private readonly nint _checkCapsule;
        private readonly nint _raycast;
        private readonly nint _syncTransforms;

        internal PhysicsBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            const string core = "UnityEngine.CoreModule.dll";
            _objectClass = RequireClass(api, core, "UnityEngine", "Object");
            var componentClass = RequireClass(api, core, "UnityEngine", "Component");
            _meshClass = RequireClass(api, core, "UnityEngine", "Mesh");
            _colliderClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "Collider");
            _boxClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "BoxCollider");
            _sphereClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "SphereCollider");
            _capsuleClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "CapsuleCollider");
            _meshColliderClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "MeshCollider");
            _rigidbodyClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "Rigidbody");
            var physicsClass = RequireClass(api, PhysicsAssembly, "UnityEngine", "Physics");
            _destroy = RequireSignature(api, _objectClass, "Destroy", "UnityEngine.Object");
            _objectImplicit = RequireSignature(api, _objectClass, "op_Implicit", "UnityEngine.Object");
            _findObjectFromInstanceId = RequireSignature(
                api, _objectClass, "FindObjectFromInstanceID", "UnityEngine.EntityId");
            _getGameObject = RequireMethod(api, componentClass, "get_gameObject", 0);
            _getEnabled = RequireMethod(api, _colliderClass, "get_enabled", 0);
            _setEnabled = RequireSignature(api, _colliderClass, "set_enabled", "System.Boolean");
            _getTrigger = RequireMethod(api, _colliderClass, "get_isTrigger", 0);
            _setTrigger = RequireSignature(api, _colliderClass, "set_isTrigger", "System.Boolean");
            (_boxGetCenter, _boxSetCenter) = VectorProperty(api, _boxClass, "center");
            (_boxGetSize, _boxSetSize) = VectorProperty(api, _boxClass, "size");
            (_sphereGetCenter, _sphereSetCenter) = VectorProperty(api, _sphereClass, "center");
            (_sphereGetRadius, _sphereSetRadius) = FloatProperty(api, _sphereClass, "radius");
            (_capsuleGetCenter, _capsuleSetCenter) = VectorProperty(api, _capsuleClass, "center");
            (_capsuleGetRadius, _capsuleSetRadius) = FloatProperty(api, _capsuleClass, "radius");
            (_capsuleGetHeight, _capsuleSetHeight) = FloatProperty(api, _capsuleClass, "height");
            (_capsuleGetDirection, _capsuleSetDirection) = IntProperty(api, _capsuleClass, "direction", "System.Int32");
            _meshGetSharedMesh = RequireMethod(api, _meshColliderClass, "get_sharedMesh", 0);
            _meshSetSharedMesh = RequireSignature(api, _meshColliderClass, "set_sharedMesh", "UnityEngine.Mesh");
            (_meshGetConvex, _meshSetConvex) = BoolProperty(api, _meshColliderClass, "convex");
            _bodyFloats = new Dictionary<RigidbodyFloatProperty, (nint, nint)>
            {
                [RigidbodyFloatProperty.Mass] = FloatProperty(api, _rigidbodyClass, "mass"),
                [RigidbodyFloatProperty.LinearDamping] = FloatProperty(api, _rigidbodyClass, "linearDamping"),
                [RigidbodyFloatProperty.AngularDamping] = FloatProperty(api, _rigidbodyClass, "angularDamping"),
            };
            _bodyBools = new Dictionary<RigidbodyBoolProperty, (nint, nint)>
            {
                [RigidbodyBoolProperty.UseGravity] = BoolProperty(api, _rigidbodyClass, "useGravity"),
                [RigidbodyBoolProperty.IsKinematic] = BoolProperty(api, _rigidbodyClass, "isKinematic"),
                [RigidbodyBoolProperty.DetectCollisions] = BoolProperty(api, _rigidbodyClass, "detectCollisions"),
            };
            _bodyInts = new Dictionary<RigidbodyIntProperty, (nint, nint)>
            {
                [RigidbodyIntProperty.Constraints] = IntProperty(
                    api, _rigidbodyClass, "constraints", "UnityEngine.RigidbodyConstraints"),
                [RigidbodyIntProperty.CollisionDetection] = IntProperty(
                    api, _rigidbodyClass, "collisionDetectionMode", "UnityEngine.CollisionDetectionMode"),
                [RigidbodyIntProperty.Interpolation] = IntProperty(
                    api, _rigidbodyClass, "interpolation", "UnityEngine.RigidbodyInterpolation"),
            };
            (_bodyGetLinearVelocity, _bodySetLinearVelocity) = VectorProperty(api, _rigidbodyClass, "linearVelocity");
            (_bodyGetAngularVelocity, _bodySetAngularVelocity) = VectorProperty(api, _rigidbodyClass, "angularVelocity");
            _addForce = RequireSignature(api, _rigidbodyClass, "AddForce", "UnityEngine.Vector3", "UnityEngine.ForceMode");
            _addTorque = RequireSignature(api, _rigidbodyClass, "AddTorque", "UnityEngine.Vector3", "UnityEngine.ForceMode");
            _addForceAtPosition = RequireSignature(
                api, _rigidbodyClass, "AddForceAtPosition", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.ForceMode");
            _movePosition = RequireSignature(api, _rigidbodyClass, "MovePosition", "UnityEngine.Vector3");
            _moveRotation = RequireSignature(api, _rigidbodyClass, "MoveRotation", "UnityEngine.Quaternion");
            _sleep = RequireMethod(api, _rigidbodyClass, "Sleep", 0);
            _isSleeping = RequireMethod(api, _rigidbodyClass, "IsSleeping", 0);
            _wakeUp = RequireMethod(api, _rigidbodyClass, "WakeUp", 0);
            _checkSphere = RequireSignature(
                api, physicsClass, "CheckSphere", "UnityEngine.Vector3", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
            _checkBox = RequireSignature(
                api, physicsClass, "CheckBox", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Int32", "UnityEngine.QueryTriggerInteraction");
            _checkCapsule = RequireSignature(
                api, physicsClass, "CheckCapsule", "UnityEngine.Vector3", "UnityEngine.Vector3", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
            _raycast = RequireSignature(
                api, physicsClass, "Raycast", "UnityEngine.Vector3", "UnityEngine.Vector3", "UnityEngine.RaycastHit", "System.Single", "System.Int32", "UnityEngine.QueryTriggerInteraction");
            _syncTransforms = RequireMethod(api, physicsClass, "SyncTransforms", 0);
        }

        internal bool IsObjectAlive(nint instance) => instance != 0 && ReadBool(
            _api.Invoke(_objectImplicit, 0, Il2CppArgument.FromReference(instance)));
        internal void Destroy(nint instance) =>
            _ = _api.Invoke(_destroy, 0, Il2CppArgument.FromReference(instance));
        internal void RequireMesh(nint mesh)
        {
            if (!IsObjectAlive(mesh) || !_api.IsAssignableFrom(_meshClass, _api.GetObjectClass(mesh)))
                throw new ArgumentException("Unity object is not a live Mesh.", nameof(mesh));
        }
        internal bool GetEnabled(nint collider) => ReadBool(_api.Invoke(_getEnabled, collider));
        internal void SetEnabled(nint collider, bool value) => SetBool(_setEnabled, collider, value);
        internal bool GetIsTrigger(nint collider) => ReadBool(_api.Invoke(_getTrigger, collider));
        internal void SetIsTrigger(nint collider, bool value) => SetBool(_setTrigger, collider, value);
        internal UnityVector3 GetBoxCenter(nint value) => GetVector(_boxGetCenter, value);
        internal void SetBoxCenter(nint value, UnityVector3 vector) => SetVector(_boxSetCenter, value, vector);
        internal UnityVector3 GetBoxSize(nint value) => GetVector(_boxGetSize, value);
        internal void SetBoxSize(nint value, UnityVector3 vector) => SetVector(_boxSetSize, value, vector);
        internal UnityVector3 GetSphereCenter(nint value) => GetVector(_sphereGetCenter, value);
        internal void SetSphereCenter(nint value, UnityVector3 vector) => SetVector(_sphereSetCenter, value, vector);
        internal float GetSphereRadius(nint value) => GetFloat(_sphereGetRadius, value);
        internal void SetSphereRadius(nint value, float radius) => SetFloat(_sphereSetRadius, value, radius);
        internal UnityVector3 GetCapsuleCenter(nint value) => GetVector(_capsuleGetCenter, value);
        internal void SetCapsuleCenter(nint value, UnityVector3 vector) => SetVector(_capsuleSetCenter, value, vector);
        internal float GetCapsuleRadius(nint value) => GetFloat(_capsuleGetRadius, value);
        internal void SetCapsuleRadius(nint value, float radius) => SetFloat(_capsuleSetRadius, value, radius);
        internal float GetCapsuleHeight(nint value) => GetFloat(_capsuleGetHeight, value);
        internal void SetCapsuleHeight(nint value, float height) => SetFloat(_capsuleSetHeight, value, height);
        internal int GetCapsuleDirection(nint value) => GetInt(_capsuleGetDirection, value);
        internal void SetCapsuleDirection(nint value, int direction) => SetInt(_capsuleSetDirection, value, direction);
        internal nint GetMeshColliderMesh(nint collider) => _api.Invoke(_meshGetSharedMesh, collider);
        internal void SetMeshColliderMesh(nint collider, nint mesh) =>
            _ = _api.Invoke(_meshSetSharedMesh, collider, Il2CppArgument.FromReference(mesh));
        internal bool GetMeshColliderConvex(nint collider) => ReadBool(_api.Invoke(_meshGetConvex, collider));
        internal void SetMeshColliderConvex(nint collider, bool value) => SetBool(_meshSetConvex, collider, value);
        internal float GetRigidbodyFloat(nint body, RigidbodyFloatProperty property) => GetFloat(_bodyFloats[property].Get, body);
        internal void SetRigidbodyFloat(nint body, RigidbodyFloatProperty property, float value) => SetFloat(_bodyFloats[property].Set, body, value);
        internal bool GetRigidbodyBool(nint body, RigidbodyBoolProperty property) => ReadBool(_api.Invoke(_bodyBools[property].Get, body));
        internal void SetRigidbodyBool(nint body, RigidbodyBoolProperty property, bool value) => SetBool(_bodyBools[property].Set, body, value);
        internal int GetRigidbodyInt(nint body, RigidbodyIntProperty property) => GetInt(_bodyInts[property].Get, body);
        internal void SetRigidbodyInt(nint body, RigidbodyIntProperty property, int value) => SetInt(_bodyInts[property].Set, body, value);
        internal UnityVector3 GetRigidbodyVector(nint body, bool angular) => GetVector(angular ? _bodyGetAngularVelocity : _bodyGetLinearVelocity, body);
        internal void SetRigidbodyVector(nint body, bool angular, UnityVector3 value) => SetVector(angular ? _bodySetAngularVelocity : _bodySetLinearVelocity, body, value);
        internal void AddForce(nint body, UnityVector3 force, ModForceMode mode) => InvokeVectorMode(_addForce, body, force, mode);
        internal void AddTorque(nint body, UnityVector3 torque, ModForceMode mode) => InvokeVectorMode(_addTorque, body, torque, mode);
        internal void AddForceAtPosition(nint body, UnityVector3 force, UnityVector3 position, ModForceMode mode) =>
            _ = _api.Invoke(_addForceAtPosition, body, VectorArgument(force), VectorArgument(position), Il2CppArgument.FromInt32((int)mode));
        internal void MovePosition(nint body, UnityVector3 position) =>
            _ = _api.Invoke(_movePosition, body, VectorArgument(position));
        internal void MoveRotation(nint body, UnityQuaternion rotation) =>
            _ = _api.Invoke(_moveRotation, body, Il2CppArgument.FromValue(new NativeQuaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)));
        internal void Sleep(nint body) => _ = _api.Invoke(_sleep, body);
        internal bool IsSleeping(nint body) => ReadBool(_api.Invoke(_isSleeping, body));
        internal void WakeUp(nint body) => _ = _api.Invoke(_wakeUp, body);
        internal bool CheckSphere(UnityVector3 center, float radius, int layerMask, ModQueryTriggerInteraction query) =>
            ReadBool(_api.Invoke(_checkSphere, 0, VectorArgument(center), Il2CppArgument.FromSingle(radius), Il2CppArgument.FromInt32(layerMask), Il2CppArgument.FromInt32((int)query)));
        internal bool CheckBox(UnityVector3 center, UnityVector3 halfExtents, UnityQuaternion orientation, int layerMask, ModQueryTriggerInteraction query) =>
            ReadBool(_api.Invoke(_checkBox, 0, VectorArgument(center), VectorArgument(halfExtents), Il2CppArgument.FromValue(new NativeQuaternion(orientation.X, orientation.Y, orientation.Z, orientation.W)), Il2CppArgument.FromInt32(layerMask), Il2CppArgument.FromInt32((int)query)));
        internal bool CheckCapsule(UnityVector3 start, UnityVector3 end, float radius, int layerMask, ModQueryTriggerInteraction query) =>
            ReadBool(_api.Invoke(_checkCapsule, 0, VectorArgument(start), VectorArgument(end), Il2CppArgument.FromSingle(radius), Il2CppArgument.FromInt32(layerMask), Il2CppArgument.FromInt32((int)query)));
        internal void SyncTransforms() => _ = _api.Invoke(_syncTransforms, 0);

        internal unsafe bool Raycast(
            UnityVector3 origin,
            UnityVector3 direction,
            float maxDistance,
            int layerMask,
            ModQueryTriggerInteraction query,
            out ModRaycastHit hit)
        {
            var nativeOrigin = NativeVector3.From(origin);
            var nativeDirection = NativeVector3.From(direction);
            NativeRaycastHit nativeHit = default;
            var nativeDistance = maxDistance;
            var nativeLayerMask = layerMask;
            var nativeQuery = (int)query;
            nint* parameters = stackalloc nint[6];
            parameters[0] = (nint)(&nativeOrigin);
            parameters[1] = (nint)(&nativeDirection);
            parameters[2] = (nint)(&nativeHit);
            parameters[3] = (nint)(&nativeDistance);
            parameters[4] = (nint)(&nativeLayerMask);
            parameters[5] = (nint)(&nativeQuery);
            var didHit = ReadBool(_api.RuntimeInvoke(_raycast, 0, (nint)parameters));
            if (!didHit)
            {
                hit = new ModRaycastHit(
                    UnityObject.Null, UnityObject.Null, UnityVector3.Zero, UnityVector3.Zero,
                    0f, -1, UnityVector3.Zero);
                return false;
            }
            var collider = nativeHit.ColliderEntityId == 0
                ? 0
                : _api.Invoke(
                    _findObjectFromInstanceId,
                    0,
                    Il2CppArgument.FromUInt64(nativeHit.ColliderEntityId));
            var gameObject = collider == 0 ? 0 : _api.Invoke(_getGameObject, collider);
            var uv = nativeHit.Uv;
            hit = new ModRaycastHit(
                new UnityObject(collider),
                new UnityObject(gameObject),
                nativeHit.Point.ToPublic(),
                nativeHit.Normal.ToPublic(),
                nativeHit.Distance,
                unchecked((int)nativeHit.FaceId),
                new UnityVector3(1f - uv.Y - uv.X, uv.X, uv.Y));
            return true;
        }

        private void InvokeVectorMode(nint method, nint body, UnityVector3 vector, ModForceMode mode) =>
            _ = _api.Invoke(method, body, VectorArgument(vector), Il2CppArgument.FromInt32((int)mode));
        private UnityVector3 GetVector(nint method, nint instance) => ReadValue<NativeVector3>(_api.Invoke(method, instance)).ToPublic();
        private void SetVector(nint method, nint instance, UnityVector3 value) =>
            _ = _api.Invoke(method, instance, VectorArgument(value));
        private float GetFloat(nint method, nint instance) => ReadValue<float>(_api.Invoke(method, instance));
        private void SetFloat(nint method, nint instance, float value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromSingle(value));
        private int GetInt(nint method, nint instance) => ReadValue<int>(_api.Invoke(method, instance));
        private void SetInt(nint method, nint instance, int value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromInt32(value));
        private void SetBool(nint method, nint instance, bool value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromBoolean(value));
        private static Il2CppArgument VectorArgument(UnityVector3 value) =>
            Il2CppArgument.FromValue(NativeVector3.From(value));
        private bool ReadBool(nint boxed) => ReadValue<byte>(boxed) != 0;
        private T ReadValue<T>(nint boxed) where T : unmanaged
        {
            var pointer = boxed == 0 ? 0 : _api.Unbox(boxed);
            return pointer != 0
                ? Marshal.PtrToStructure<T>(pointer)
                : throw new InvalidDataException($"Unity {typeof(T).Name} result could not be unboxed.");
        }

        private static (nint Get, nint Set) VectorProperty(IUnsafeIl2CppApi api, nint klass, string name) =>
            (RequireMethod(api, klass, $"get_{name}", 0), RequireSignature(api, klass, $"set_{name}", "UnityEngine.Vector3"));
        private static (nint Get, nint Set) FloatProperty(IUnsafeIl2CppApi api, nint klass, string name) =>
            (RequireMethod(api, klass, $"get_{name}", 0), RequireSignature(api, klass, $"set_{name}", "System.Single"));
        private static (nint Get, nint Set) BoolProperty(IUnsafeIl2CppApi api, nint klass, string name) =>
            (RequireMethod(api, klass, $"get_{name}", 0), RequireSignature(api, klass, $"set_{name}", "System.Boolean"));
        private static (nint Get, nint Set) IntProperty(IUnsafeIl2CppApi api, nint klass, string name, string type) =>
            (RequireMethod(api, klass, $"get_{name}", 0), RequireSignature(api, klass, $"set_{name}", type));
        private static nint RequireClass(IUnsafeIl2CppApi api, string assembly, string namespaze, string name)
        {
            var klass = api.FindClass(assembly, namespaze, name);
            return klass != 0 ? klass : throw new TypeLoadException($"{namespaze}.{name}");
        }
        private static nint RequireMethod(IUnsafeIl2CppApi api, nint klass, string name, int count)
        {
            var method = api.FindMethod(klass, name, count);
            return method != 0 ? method : throw new MissingMethodException($"{name}/{count}");
        }
        private static nint RequireSignature(IUnsafeIl2CppApi api, nint klass, string name, params string[] parameters)
        {
            var method = api.FindMethodBySignature(klass, name, parameters);
            return method != 0
                ? method
                : throw new MissingMethodException($"{name}({string.Join(", ", parameters)})");
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativeVector2(float X, float Y);
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativeVector3(float X, float Y, float Z)
        {
            internal static NativeVector3 From(UnityVector3 value) => new(value.X, value.Y, value.Z);
            internal UnityVector3 ToPublic() => new(X, Y, Z);
        }
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativeQuaternion(float X, float Y, float Z, float W);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRaycastHit
        {
            internal NativeVector3 Point;
            internal NativeVector3 Normal;
            internal uint FaceId;
            internal float Distance;
            internal NativeVector2 Uv;
            internal ulong ColliderEntityId;
        }
    }
}
