using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class GameContent
{
    private sealed class MiningNodeRegistry : IMiningNodeRegistry
    {
        private readonly IUnsafeIl2CppApi _api;
        private readonly IUnityApi _unity;
        private readonly Func<bool> _isServerActive;
        private readonly nint _itemClass;
        private readonly nint _unityObjectClass;
        private readonly nint _isNodeField;
        private readonly nint _getGameObject;
        private readonly nint _getPieceCount;
        private readonly nint _getPieceHealth;
        private readonly nint _forceBreakPiece;
        private readonly nint _objectImplicit;

        public MiningNodeRegistry(
            IUnsafeIl2CppApi api,
            IUnityApi unity,
            Func<bool> isServerActive)
        {
            _api = api;
            _unity = unity;
            _isServerActive = isServerActive;
            _itemClass = RequireClass(api, "T_Item");
            _unityObjectClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Object");
            var componentClass = RequireUnityClass(
                api,
                "UnityEngine.CoreModule.dll",
                "UnityEngine",
                "Component");
            _isNodeField = RequireField(api, _itemClass, "isNode");
            _getGameObject = RequireMethod(api, componentClass, "get_gameObject", 0);
            _getPieceCount = RequireMethod(api, _itemClass, "GetPieceCount", 0);
            _getPieceHealth = RequireMethod(api, _itemClass, "GetPieceHealth", 1);
            _forceBreakPiece = api.FindMethodBySignature(
                _itemClass,
                "Server_ForceBreakPiece",
                [
                    "System.Int32",
                    "UnityEngine.Vector3",
                    "Mirror.NetworkConnectionToClient",
                    "System.Single",
                    "System.Boolean"
                ]);
            _objectImplicit = api.FindMethodBySignature(
                _unityObjectClass,
                "op_Implicit",
                ["UnityEngine.Object"]);
            if (_forceBreakPiece == 0 || _objectImplicit == 0)
                throw new MissingMethodException(
                    "The vanilla server mining-node operations are unavailable.");
        }

        public IReadOnlyList<IMiningNode> GetLoaded(bool activeOnly = true)
        {
            EnsureMainThread();
            return _unity.FindComponents(
                    "Assembly-CSharp.dll",
                    string.Empty,
                    "T_Item",
                    activeOnly)
                .Where(IsNode)
                .Select(CreateHandle)
                .Cast<IMiningNode>()
                .ToArray();
        }

        public bool TryGet(UnityObject gameObject, out IMiningNode node)
        {
            EnsureMainThread();
            var component = _unity.TryGetComponent(
                gameObject,
                "Assembly-CSharp.dll",
                string.Empty,
                "T_Item");
            if (!component.IsNull && IsNode(component))
            {
                node = CreateHandle(component);
                return true;
            }
            node = default!;
            return false;
        }

        private MiningNodeHandle CreateHandle(UnityObject component)
        {
            var gameObject = new UnityObject(_api.RuntimeInvoke(
                _getGameObject,
                component.Pointer,
                0));
            return new MiningNodeHandle(this, gameObject, component);
        }

        private bool IsNode(UnityObject component) =>
            !component.IsNull &&
            IsAlive(component) &&
            _api.ReadBoolean(component.Pointer, _isNodeField);

        private bool IsAlive(UnityObject value)
        {
            if (value.IsNull) return false;
            var boxed = _api.Invoke(
                _objectImplicit,
                0,
                Il2CppArgument.FromReference(value.Pointer));
            var unboxed = _api.Unbox(boxed);
            return unboxed != 0 && Marshal.ReadByte(unboxed) != 0;
        }

        private int GetPieceCount(UnityObject component) =>
            ReadInt(_api.RuntimeInvoke(_getPieceCount, component.Pointer, 0));

        private int GetPieceHealth(UnityObject component, int index) =>
            ReadInt(_api.Invoke(
                _getPieceHealth,
                component.Pointer,
                Il2CppArgument.FromInt32(index)));

        private bool MineNextPiece(UnityObject component, UnityObject gameObject)
        {
            EnsureMainThread();
            if (!_isServerActive())
                throw new InvalidOperationException(
                    "Mining nodes may only be changed by the active Mirror server/host.");
            if (!IsNode(component)) return false;
            var count = GetPieceCount(component);
            for (var index = 0; index < count; ++index)
            {
                if (GetPieceHealth(component, index) <= 0) continue;
                var position = _unity.GetTransform(gameObject).Position;
                _ = _api.Invoke(
                    _forceBreakPiece,
                    component.Pointer,
                    Il2CppArgument.FromInt32(index),
                    Il2CppArgument.FromValue(new NativeVector3(position.X, position.Y, position.Z)),
                    Il2CppArgument.FromReference(0),
                    Il2CppArgument.FromSingle(0f),
                    Il2CppArgument.FromBoolean(false));
                return true;
            }
            return false;
        }

        private int ReadInt(nint boxed)
        {
            var value = _api.Unbox(boxed);
            return value == 0
                ? throw new InvalidDataException("Vanilla mining operation returned null.")
                : Marshal.ReadInt32(value);
        }

        private readonly record struct NativeVector3(float X, float Y, float Z);

        private sealed class MiningNodeHandle(
            MiningNodeRegistry owner,
            UnityObject gameObject,
            UnityObject component) : IMiningNode
        {
            public UnityObject GameObject => gameObject;
            public UnityObject Component => component;
            public bool IsAlive => owner.IsNode(component);
            public int PieceCount => owner.GetPieceCount(component);
            public IReadOnlyList<int> PieceHealth => Enumerable
                .Range(0, PieceCount)
                .Select(index => owner.GetPieceHealth(component, index))
                .ToArray();
            public bool MineNextPieceServer() => owner.MineNextPiece(component, gameObject);
        }
    }
}
