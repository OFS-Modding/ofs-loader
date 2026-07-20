using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(float X, float Y, float Width, float Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeVector2(float X, float Y);

    private readonly List<ModImage> _images = [];
    private ImageBridge? _imageBridge;

    public IReadOnlyList<IModImage> LoadedImages =>
        _images.Where(image => image.IsLoaded).Cast<IModImage>().ToArray();

    public IModImage LoadImage(
        string relativePath,
        ModImageLoadOptions? options = null)
    {
        EnsureMainThread();
        var source = ResolveContainedPath(relativePath);
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("Image does not exist.", source);
        if (info.Length is <= 0 or > ModImageLimits.MaximumSourceBytes)
            throw new InvalidDataException(
                $"Image source size {info.Length} is outside the 1.." +
                $"{ModImageLimits.MaximumSourceBytes} byte limit.");
        return LoadImageCore(
            Path.GetFileNameWithoutExtension(source),
            File.ReadAllBytes(source),
            source,
            options ?? new ModImageLoadOptions());
    }

    public IModImage LoadImageBytes(
        string name,
        ReadOnlyMemory<byte> bytes,
        ModImageLoadOptions? options = null)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (bytes.Length is <= 0 or > ModImageLimits.MaximumSourceBytes)
            throw new InvalidDataException(
                $"Image source size {bytes.Length} is outside the 1.." +
                $"{ModImageLimits.MaximumSourceBytes} byte limit.");
        return LoadImageCore(name, bytes.ToArray(), null, options ?? new ModImageLoadOptions());
    }

    private IModImage LoadImageCore(
        string fallbackName,
        byte[] bytes,
        string? sourcePath,
        ModImageLoadOptions options)
    {
        ValidateImageOptions(options);
        var inspected = CatalogThumbnailStore.InspectRaster(
            bytes,
            ModImageLimits.MaximumDimension,
            ModImageLimits.MaximumPixels,
            "Mod image");
        var name = string.IsNullOrWhiteSpace(options.Name) ? fallbackName : options.Name.Trim();
        var bridge = _imageBridge ??= new ImageBridge(unsafeApi);
        var pair = bridge.Decode(
            bytes,
            name,
            options.PivotX,
            options.PivotY,
            options.PixelsPerUnit,
            options.MarkNonReadable);
        var image = new ModImage(
            this,
            name,
            sourcePath,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            inspected.Format == CatalogThumbnailFormat.Png
                ? ModImageFormat.Png
                : ModImageFormat.Jpeg,
            pair.Width,
            pair.Height,
            pair.Texture,
            pair.Sprite);
        _images.Add(image);
        logger.Info(
            $"Loaded {image.Format} image '{image.Name}' ({image.Width}x{image.Height}, " +
            $"{image.SourceBytes} bytes, sha256={image.Sha256}).");
        return image;
    }

    private static void ValidateImageOptions(ModImageLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!float.IsFinite(options.PivotX) || options.PivotX is < 0f or > 1f ||
            !float.IsFinite(options.PivotY) || options.PivotY is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(
                nameof(options), "Image pivot coordinates must be finite values in [0, 1].");
        if (!float.IsFinite(options.PixelsPerUnit) ||
            options.PixelsPerUnit is <= 0f or > 100_000f)
            throw new ArgumentOutOfRangeException(
                nameof(options), "PixelsPerUnit must be finite and in (0, 100000].");
        if (options.Name is { Length: > 256 })
            throw new ArgumentException("Image names cannot exceed 256 characters.", nameof(options));
    }

    private void Remove(ModImage image) => _images.Remove(image);

    private void RemoveAllImages()
    {
        foreach (var image in _images.ToArray().Reverse())
        {
            try
            {
                image.Unload();
            }
            catch (Exception exception)
            {
                logger.Error(exception, $"Image '{image.Name}' rollback cleanup was incomplete.");
            }
        }
        _images.Clear();
    }

    private sealed class ModImage(
        ModAssets owner,
        string name,
        string? sourcePath,
        int sourceBytes,
        string sha256,
        ModImageFormat format,
        int width,
        int height,
        nint texture,
        nint sprite) : IModImage
    {
        private nint _texture = texture;
        private nint _sprite = sprite;

        public string OwnerId => owner.ownerId;
        public string Name { get; } = name;
        public string? SourcePath { get; } = sourcePath;
        public int SourceBytes { get; } = sourceBytes;
        public string Sha256 { get; } = sha256;
        public ModImageFormat Format { get; } = format;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public UnityObject Texture => new(_texture);
        public UnityObject Sprite => new(_sprite);
        public bool IsLoaded => _texture != 0 && _sprite != 0;

        public void Unload()
        {
            EnsureMainThread();
            if (_texture == 0 && _sprite == 0) return;
            Exception? failure = null;
            try
            {
                if (_sprite != 0) owner._imageBridge!.Destroy(_sprite);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                if (_texture != 0) owner._imageBridge!.Destroy(_texture);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            _sprite = 0;
            _texture = 0;
            owner.Remove(this);
            if (failure is not null)
                throw new InvalidOperationException(
                    $"Image '{Name}' cleanup was incomplete.", failure);
        }

        public void Dispose() => Unload();
    }

    private sealed class ImageBridge
    {
        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _textureClass;
        private readonly nint _spriteClass;
        private readonly nint _objectClass;
        private readonly nint _textureConstructor;
        private readonly nint _loadImage;
        private readonly nint _apply;
        private readonly nint _getWidth;
        private readonly nint _getHeight;
        private readonly nint _createSprite;
        private readonly nint _setName;
        private readonly nint _destroy;

        internal ImageBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            _textureClass = RequireClass(api, "UnityEngine.CoreModule.dll", "UnityEngine", "Texture2D");
            _spriteClass = RequireClass(api, "UnityEngine.CoreModule.dll", "UnityEngine", "Sprite");
            _objectClass = RequireClass(api, "UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            var imageConversionClass = RequireClass(
                api, "UnityEngine.ImageConversionModule.dll", "UnityEngine", "ImageConversion");
            _textureConstructor = RequireSignature(api, _textureClass, ".ctor", "System.Int32", "System.Int32");
            _loadImage = RequireSignature(
                api,
                imageConversionClass,
                "LoadImage",
                "UnityEngine.Texture2D",
                "System.Byte[]");
            _apply = RequireSignature(
                api, _textureClass, "Apply", "System.Boolean", "System.Boolean");
            _getWidth = RequireMethod(api, _textureClass, "get_width", 0);
            _getHeight = RequireMethod(api, _textureClass, "get_height", 0);
            _createSprite = RequireSignature(
                api,
                _spriteClass,
                "Create",
                "UnityEngine.Texture2D",
                "UnityEngine.Rect",
                "UnityEngine.Vector2",
                "System.Single");
            _setName = RequireSignature(api, _objectClass, "set_name", "System.String");
            _destroy = RequireSignature(api, _objectClass, "Destroy", "UnityEngine.Object");
        }

        internal (nint Texture, nint Sprite, int Width, int Height) Decode(
            byte[] bytes,
            string name,
            float pivotX,
            float pivotY,
            float pixelsPerUnit,
            bool markNonReadable)
        {
            nint texture = 0;
            nint sprite = 0;
            try
            {
                texture = _api.NewObject(_textureClass);
                if (texture == 0) throw new InvalidOperationException("Texture2D allocation returned null.");
                _ = _api.Invoke(
                    _textureConstructor,
                    texture,
                    Il2CppArgument.FromInt32(2),
                    Il2CppArgument.FromInt32(2));
                var loaded = _api.Invoke(
                    _loadImage,
                    0,
                    Il2CppArgument.FromReference(texture),
                    Il2CppArgument.FromReference(_api.NewByteArray(bytes)));
                if (!ReadBoolean(loaded))
                    throw new InvalidDataException("Unity ImageConversion.LoadImage rejected the image.");
                var width = ReadInt32(_api.Invoke(_getWidth, texture));
                var height = ReadInt32(_api.Invoke(_getHeight, texture));
                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"Unity decoded invalid dimensions {width}x{height}.");
                if (markNonReadable)
                {
                    _ = _api.Invoke(
                        _apply,
                        texture,
                        Il2CppArgument.FromBoolean(false),
                        Il2CppArgument.FromBoolean(true));
                }
                SetName(texture, $"OFS {name} Texture");
                sprite = _api.Invoke(
                    _createSprite,
                    0,
                    Il2CppArgument.FromReference(texture),
                    Il2CppArgument.FromValue(new NativeRect(0f, 0f, width, height)),
                    Il2CppArgument.FromValue(new NativeVector2(pivotX, pivotY)),
                    Il2CppArgument.FromSingle(pixelsPerUnit));
                if (sprite == 0) throw new InvalidOperationException("Sprite.Create returned null.");
                SetName(sprite, $"OFS {name} Sprite");
                return (texture, sprite, width, height);
            }
            catch
            {
                if (sprite != 0) TryDestroy(sprite);
                if (texture != 0) TryDestroy(texture);
                throw;
            }
        }

        internal void Destroy(nint instance) =>
            _ = _api.Invoke(_destroy, 0, Il2CppArgument.FromReference(instance));

        private void TryDestroy(nint instance)
        {
            try { Destroy(instance); }
            catch { }
        }

        private void SetName(nint instance, string name) =>
            _ = _api.Invoke(
                _setName,
                instance,
                Il2CppArgument.FromReference(_api.NewString(name)));

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
                : throw new InvalidDataException("Unity integer result could not be unboxed.");
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
                : throw new TypeLoadException($"{namespaze}.{name}");
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
                    $"{name}({string.Join(", ", parameters)})");
        }
    }
}
