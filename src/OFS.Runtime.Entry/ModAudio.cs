using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class ModAssets
{
    private static readonly List<ModAudioPlayback> AllAudioPlaybacks = [];
    private readonly List<ModAudioClip> _audioClips = [];
    private readonly List<ModAudioPlayback> _audioPlaybacks = [];
    private AudioBridge? _audioBridge;
    private UnityApi? _audioUnity;

    public IReadOnlyList<IModAudioClip> LoadedAudioClips =>
        _audioClips.Where(clip => clip.IsLoaded).Cast<IModAudioClip>().ToArray();

    public IReadOnlyList<IModAudioPlayback> ActiveAudioPlaybacks =>
        _audioPlaybacks.Where(playback => playback.IsAlive)
            .Cast<IModAudioPlayback>()
            .ToArray();

    public IModAudioClip LoadWav(
        string relativePath,
        ModAudioClipOptions? options = null)
    {
        EnsureMainThread();
        var source = ResolveContainedPath(relativePath);
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("WAV source does not exist.", source);
        if (info.Length is <= 0 or > ModAudioLimits.MaximumSourceBytes)
            throw new InvalidDataException(
                $"WAV source size {info.Length} is outside the 1.." +
                $"{ModAudioLimits.MaximumSourceBytes} byte limit.");
        return LoadWavCore(
            Path.GetFileNameWithoutExtension(source),
            File.ReadAllBytes(source),
            source,
            options ?? new ModAudioClipOptions());
    }

    public IModAudioClip LoadWavBytes(
        string name,
        ReadOnlyMemory<byte> bytes,
        ModAudioClipOptions? options = null)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (bytes.Length is <= 0 or > ModAudioLimits.MaximumSourceBytes)
            throw new InvalidDataException(
                $"WAV source size {bytes.Length} is outside the 1.." +
                $"{ModAudioLimits.MaximumSourceBytes} byte limit.");
        return LoadWavCore(name, bytes.ToArray(), null, options ?? new ModAudioClipOptions());
    }

    internal static void PollAudio()
    {
        EnsureMainThread();
        foreach (var playback in AllAudioPlaybacks.ToArray())
        {
            try
            {
                playback.Refresh();
            }
            catch (Exception exception)
            {
                playback.RecordPollingFailure(exception);
            }
        }
    }

    private IModAudioClip LoadWavCore(
        string fallbackName,
        byte[] bytes,
        string? sourcePath,
        ModAudioClipOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Name is { Length: > 256 })
            throw new ArgumentException("Audio clip names cannot exceed 256 characters.", nameof(options));
        var name = string.IsNullOrWhiteSpace(options.Name) ? fallbackName : options.Name.Trim();
        if (name.Length > 256)
            throw new ArgumentException("Audio clip names cannot exceed 256 characters.", nameof(options));

        var decoded = WaveAudioDecoder.Decode(bytes);
        var bridge = _audioBridge ??= new AudioBridge(unsafeApi);
        var clipPointer = bridge.CreateClip(name, decoded);
        var clip = new ModAudioClip(
            this,
            name,
            sourcePath,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            decoded,
            clipPointer);
        _audioClips.Add(clip);
        logger.Info(
            $"Loaded WAV clip '{name}' ({clip.Channels}ch, {clip.Frequency}Hz, " +
            $"{clip.BitsPerSample}-bit {clip.Encoding}, {clip.DurationSeconds:F3}s, " +
            $"sha256={clip.Sha256}).");
        return clip;
    }

    private IModAudioPlayback Play(
        ModAudioClip clip,
        UnityVector3? position,
        ModAudioPlaybackOptions options)
    {
        EnsureMainThread();
        if (!clip.IsLoaded) throw new ObjectDisposedException(clip.Name);
        ValidatePlaybackOptions(options);

        var bridge = _audioBridge ??= new AudioBridge(unsafeApi);
        var unity = _audioUnity ??= new UnityApi(unsafeApi);
        UnityObject gameObject = default;
        try
        {
            gameObject = unity.CreateGameObject($"OFS Audio [{ownerId}] {clip.Name}");
            if (position is UnityVector3 worldPosition)
                unity.SetTransform(
                    gameObject,
                    new UnityTransform(worldPosition, UnityQuaternion.Identity, UnityVector3.One));
            if (options.PersistAcrossScenes) unity.DontDestroyOnLoad(gameObject);
            var source = unity.AddComponent(
                gameObject,
                "UnityEngine.AudioModule.dll",
                "UnityEngine",
                "AudioSource");
            bridge.ConfigureAndPlay(source.Pointer, clip.Clip.Pointer, position is not null, options);
            var playback = new ModAudioPlayback(
                this,
                clip,
                bridge,
                unity,
                gameObject.Pointer,
                source.Pointer,
                position is not null,
                options);
            _audioPlaybacks.Add(playback);
            AllAudioPlaybacks.Add(playback);
            return playback;
        }
        catch
        {
            if (!gameObject.IsNull)
            {
                try { unity.Destroy(gameObject); }
                catch { }
            }
            throw;
        }
    }

    private static void ValidatePlaybackOptions(ModAudioPlaybackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!float.IsFinite(options.Volume) || options.Volume is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(options), "Volume must be finite and in [0, 1].");
        if (!float.IsFinite(options.Pitch) || options.Pitch is < -3f or > 3f)
            throw new ArgumentOutOfRangeException(nameof(options), "Pitch must be finite and in [-3, 3].");
        if (!float.IsFinite(options.MinDistance) || options.MinDistance < 0f ||
            !float.IsFinite(options.MaxDistance) || options.MaxDistance < options.MinDistance ||
            options.MaxDistance > 1_000_000f)
            throw new ArgumentOutOfRangeException(
                nameof(options), "Audio distances must be finite, ordered and at most 1,000,000.");
    }

    private void Remove(ModAudioClip clip) => _audioClips.Remove(clip);

    private void Remove(ModAudioPlayback playback)
    {
        _audioPlaybacks.Remove(playback);
        AllAudioPlaybacks.Remove(playback);
    }

    private void RemoveAllAudio()
    {
        foreach (var playback in _audioPlaybacks.ToArray().Reverse())
        {
            try { playback.Stop(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Audio playback for '{playback.RuntimeClip.Name}' cleanup failed.");
            }
        }
        foreach (var clip in _audioClips.ToArray().Reverse())
        {
            try { clip.Unload(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Audio clip '{clip.Name}' cleanup failed.");
            }
        }
        _audioPlaybacks.Clear();
        _audioClips.Clear();
    }

    private sealed class ModAudioClip(
        ModAssets owner,
        string name,
        string? sourcePath,
        int sourceBytes,
        string sha256,
        DecodedWaveAudio decoded,
        nint clip) : IModAudioClip
    {
        private nint _clip = clip;

        public string OwnerId => owner.ownerId;
        public string Name { get; } = name;
        public string? SourcePath { get; } = sourcePath;
        public int SourceBytes { get; } = sourceBytes;
        public string Sha256 { get; } = sha256;
        public ModWaveEncoding Encoding => decoded.Encoding;
        public int Channels => decoded.Channels;
        public int Frequency => decoded.Frequency;
        public int BitsPerSample => decoded.BitsPerSample;
        public int SampleFrames => decoded.SampleFrames;
        public double DurationSeconds => decoded.DurationSeconds;
        public UnityObject Clip => new(_clip);
        public bool IsLoaded => _clip != 0;
        public IReadOnlyList<IModAudioPlayback> ActivePlaybacks =>
            owner._audioPlaybacks
                .Where(playback => playback.IsAlive && ReferenceEquals(playback.RuntimeClip, this))
                .Cast<IModAudioPlayback>()
                .ToArray();

        public IModAudioPlayback Play2D(ModAudioPlaybackOptions? options = null) =>
            owner.Play(this, null, options ?? new ModAudioPlaybackOptions());

        public IModAudioPlayback Play3D(
            UnityVector3 position,
            ModAudioPlaybackOptions? options = null) =>
            owner.Play(this, position, options ?? new ModAudioPlaybackOptions());

        public void Unload()
        {
            EnsureMainThread();
            if (_clip == 0) return;
            Exception? failure = null;
            foreach (var playback in owner._audioPlaybacks
                         .Where(item => ReferenceEquals(item.RuntimeClip, this))
                         .ToArray()
                         .Reverse())
            {
                try { playback.Stop(); }
                catch (Exception exception) { failure ??= exception; }
            }
            try { owner._audioBridge!.Destroy(_clip); }
            catch (Exception exception) { failure ??= exception; }
            _clip = 0;
            owner.Remove(this);
            if (failure is not null)
                throw new InvalidOperationException(
                    $"Audio clip '{Name}' cleanup was incomplete.", failure);
        }

        public void Dispose() => Unload();
    }

    private sealed class ModAudioPlayback(
        ModAssets owner,
        ModAudioClip clip,
        AudioBridge bridge,
        UnityApi unity,
        nint gameObject,
        nint audioSource,
        bool is3D,
        ModAudioPlaybackOptions options) : IModAudioPlayback
    {
        private nint _gameObject = gameObject;
        private nint _audioSource = audioSource;
        private int _polls;
        private int _pollingFailures;
        private float _volume = options.Volume;
        private float _pitch = options.Pitch;

        internal ModAudioClip RuntimeClip => clip;
        public string OwnerId => owner.ownerId;
        public IModAudioClip AudioClip => clip;
        public UnityObject GameObject => new(_gameObject);
        public UnityObject AudioSource => new(_audioSource);
        public bool IsAlive
        {
            get
            {
                EnsureMainThread();
                return _gameObject != 0 && bridge.IsObjectAlive(_gameObject);
            }
        }
        public bool IsPlaying
        {
            get
            {
                EnsureMainThread();
                return IsAlive && bridge.IsPlaying(_audioSource);
            }
        }
        public bool Is3D { get; } = is3D;
        public bool Loop => options.Loop;
        public float Volume => _volume;
        public float Pitch => _pitch;

        public void SetVolume(float volume)
        {
            EnsureMainThread();
            if (!float.IsFinite(volume) || volume is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(volume));
            EnsureAlive();
            bridge.SetVolume(_audioSource, volume);
            _volume = volume;
        }

        public void SetPitch(float pitch)
        {
            EnsureMainThread();
            if (!float.IsFinite(pitch) || pitch is < -3f or > 3f)
                throw new ArgumentOutOfRangeException(nameof(pitch));
            EnsureAlive();
            bridge.SetPitch(_audioSource, pitch);
            _pitch = pitch;
        }

        public void Stop()
        {
            EnsureMainThread();
            if (_gameObject == 0 && _audioSource == 0) return;
            Exception? failure = null;
            try
            {
                if (_audioSource != 0 && bridge.IsObjectAlive(_audioSource))
                    bridge.Stop(_audioSource);
            }
            catch (Exception exception) { failure = exception; }
            try
            {
                if (_gameObject != 0 && bridge.IsObjectAlive(_gameObject))
                    unity.Destroy(new UnityObject(_gameObject));
            }
            catch (Exception exception) { failure ??= exception; }
            _gameObject = 0;
            _audioSource = 0;
            owner.Remove(this);
            if (failure is not null)
                throw new InvalidOperationException("Audio playback cleanup was incomplete.", failure);
        }

        public void Dispose() => Stop();

        internal void Refresh()
        {
            if (_gameObject == 0) return;
            if (!bridge.IsObjectAlive(_gameObject))
            {
                ReleaseDestroyed();
                return;
            }
            _pollingFailures = 0;
            ++_polls;
            if (options.AutoRelease && !options.Loop && _polls > 2 && !bridge.IsPlaying(_audioSource))
                Stop();
        }

        internal void RecordPollingFailure(Exception exception)
        {
            ++_pollingFailures;
            if (_pollingFailures == 1 || _pollingFailures == 10 || _pollingFailures % 300 == 0)
                owner.logger.Error(
                    exception,
                    $"Audio playback '{clip.Name}' polling failed {_pollingFailures} time(s).");
        }

        private void ReleaseDestroyed()
        {
            _gameObject = 0;
            _audioSource = 0;
            owner.Remove(this);
        }

        private void EnsureAlive()
        {
            if (!IsAlive) throw new ObjectDisposedException(nameof(ModAudioPlayback));
        }
    }

    private sealed class AudioBridge
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
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetAudioDataDelegate(
            nint clip,
            ref ManagedSpanWrapper samples,
            int samplesOffset);

        private readonly IUnsafeIl2CppApi _api;
        private readonly nint _createClip;
        private readonly int _createClipArgumentCount;
        private readonly nint _setData;
        private readonly SetAudioDataDelegate? _setDataNative;
        private readonly int _unityObjectCachedPointerOffset;
        private readonly nint _destroy;
        private readonly nint _objectImplicit;
        private readonly nint _setClip;
        private readonly nint _setVolume;
        private readonly nint _setPitch;
        private readonly nint _setLoop;
        private readonly nint _setSpatialBlend;
        private readonly nint _setMinDistance;
        private readonly nint _setMaxDistance;
        private readonly nint _setPlayOnAwake;
        private readonly nint _play;
        private readonly nint _stop;
        private readonly nint _isPlaying;

        internal AudioBridge(IUnsafeIl2CppApi api)
        {
            _api = api;
            var clipClass = RequireClass(api, "UnityEngine.AudioModule.dll", "UnityEngine", "AudioClip");
            var sourceClass = RequireClass(api, "UnityEngine.AudioModule.dll", "UnityEngine", "AudioSource");
            var objectClass = RequireClass(api, "UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            _createClip = FindCreateClip(api, clipClass, out _createClipArgumentCount);
            _setData = api.FindMethodBySignature(
                clipClass, "SetData", ["System.Single[]", "System.Int32"]);
            if (_setData == 0)
            {
                var cachedPointer = api.FindField(objectClass, "m_CachedPtr");
                if (cachedPointer == 0)
                    throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
                _unityObjectCachedPointerOffset = api.GetFieldOffset(cachedPointer);
                var resolvePointer = NativeLibrary.GetExport(
                    api.GameAssemblyModule, "il2cpp_resolve_icall");
                var resolve = Marshal.GetDelegateForFunctionPointer<ResolveIcallDelegate>(resolvePointer);
                var setDataPointer = resolve("UnityEngine.AudioClip::SetData_Injected");
                if (setDataPointer == 0)
                    throw new MissingMethodException(
                        "UnityEngine.AudioClip::SetData_Injected native binding");
                _setDataNative = Marshal.GetDelegateForFunctionPointer<SetAudioDataDelegate>(
                    setDataPointer);
            }
            _destroy = RequireSignature(api, objectClass, "Destroy", "UnityEngine.Object");
            _objectImplicit = RequireSignature(api, objectClass, "op_Implicit", "UnityEngine.Object");
            _setClip = RequireSignature(api, sourceClass, "set_clip", "UnityEngine.AudioClip");
            _setVolume = RequireSignature(api, sourceClass, "set_volume", "System.Single");
            _setPitch = RequireSignature(api, sourceClass, "set_pitch", "System.Single");
            _setLoop = RequireSignature(api, sourceClass, "set_loop", "System.Boolean");
            _setSpatialBlend = RequireSignature(
                api, sourceClass, "set_spatialBlend", "System.Single");
            _setMinDistance = RequireSignature(api, sourceClass, "set_minDistance", "System.Single");
            _setMaxDistance = RequireSignature(api, sourceClass, "set_maxDistance", "System.Single");
            _setPlayOnAwake = RequireSignature(
                api, sourceClass, "set_playOnAwake", "System.Boolean");
            _play = RequireMethod(api, sourceClass, "Play", 0);
            _stop = RequireMethod(api, sourceClass, "Stop", 0);
            _isPlaying = RequireMethod(api, sourceClass, "get_isPlaying", 0);
        }

        internal unsafe nint CreateClip(string name, DecodedWaveAudio decoded)
        {
            nint clip = 0;
            try
            {
                var arguments = new List<Il2CppArgument>
                {
                    Il2CppArgument.FromReference(_api.NewString(name)),
                    Il2CppArgument.FromInt32(decoded.SampleFrames),
                    Il2CppArgument.FromInt32(decoded.Channels),
                    Il2CppArgument.FromInt32(decoded.Frequency),
                    Il2CppArgument.FromBoolean(false),
                };
                while (arguments.Count < _createClipArgumentCount)
                    arguments.Add(Il2CppArgument.FromReference(0));
                clip = _api.Invoke(_createClip, 0, arguments.ToArray());
                if (clip == 0) throw new InvalidOperationException("AudioClip.Create returned null.");
                bool set;
                if (_setData != 0)
                {
                    set = ReadBoolean(_api.Invoke(
                        _setData,
                        clip,
                        Il2CppArgument.FromReference(_api.NewSingleArray(decoded.Samples)),
                        Il2CppArgument.FromInt32(0)));
                }
                else
                {
                    fixed (float* samples = decoded.Samples)
                    {
                        var nativeClip = Marshal.ReadIntPtr(
                            clip, _unityObjectCachedPointerOffset);
                        if (nativeClip == 0)
                            throw new InvalidOperationException(
                                "AudioClip has no native Unity object pointer.");
                        var span = new ManagedSpanWrapper(
                            (nint)samples, decoded.Samples.Length);
                        set = _setDataNative!(nativeClip, ref span, 0);
                    }
                }
                if (!set)
                    throw new InvalidDataException("Unity AudioClip.SetData rejected decoded samples.");
                return clip;
            }
            catch
            {
                if (clip != 0)
                {
                    try { Destroy(clip); }
                    catch { }
                }
                throw;
            }
        }

        internal void ConfigureAndPlay(
            nint source,
            nint clip,
            bool is3D,
            ModAudioPlaybackOptions options)
        {
            InvokeReference(_setClip, source, clip);
            SetVolume(source, options.Volume);
            SetPitch(source, options.Pitch);
            InvokeBoolean(_setLoop, source, options.Loop);
            InvokeSingle(_setSpatialBlend, source, is3D ? 1f : 0f);
            InvokeSingle(_setMinDistance, source, options.MinDistance);
            InvokeSingle(_setMaxDistance, source, options.MaxDistance);
            InvokeBoolean(_setPlayOnAwake, source, false);
            _ = _api.Invoke(_play, source);
        }

        internal bool IsObjectAlive(nint instance)
        {
            if (instance == 0) return false;
            return ReadBoolean(_api.Invoke(
                _objectImplicit, 0, Il2CppArgument.FromReference(instance)));
        }

        internal bool IsPlaying(nint source) =>
            source != 0 && ReadBoolean(_api.Invoke(_isPlaying, source));

        internal void SetVolume(nint source, float value) => InvokeSingle(_setVolume, source, value);
        internal void SetPitch(nint source, float value) => InvokeSingle(_setPitch, source, value);
        internal void Stop(nint source) => _ = _api.Invoke(_stop, source);
        internal void Destroy(nint instance) => InvokeReference(_destroy, 0, instance);

        private void InvokeReference(nint method, nint instance, nint value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromReference(value));

        private void InvokeSingle(nint method, nint instance, float value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromSingle(value));

        private void InvokeBoolean(nint method, nint instance, bool value) =>
            _ = _api.Invoke(method, instance, Il2CppArgument.FromBoolean(value));

        private bool ReadBoolean(nint boxed)
        {
            var value = boxed == 0 ? 0 : _api.Unbox(boxed);
            return value != 0 && Marshal.ReadByte(value) != 0;
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

        private static nint FindCreateClip(
            IUnsafeIl2CppApi api,
            nint clipClass,
            out int argumentCount)
        {
            var prefix = new[]
            {
                "System.String",
                "System.Int32",
                "System.Int32",
                "System.Int32",
                "System.Boolean",
            };
            foreach (var method in api.GetMethods(clipClass)
                         .Where(method => method.Name == "Create")
                         .OrderBy(method => method.Parameters.Count))
            {
                if (method.Parameters.Count is < 5 or > 7 ||
                    !method.Parameters.Take(5)
                        .Select(parameter => parameter.TypeName)
                        .SequenceEqual(prefix))
                    continue;
                argumentCount = method.Parameters.Count;
                return method.Pointer;
            }
            throw new MissingMethodException("UnityEngine.AudioClip.Create compatible overload");
        }
    }
}
