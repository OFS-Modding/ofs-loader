using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using OFS.Loader;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModRuntimeInfo : IModRuntimeInfo
{
    private const uint Il2CppMetadataMagic = 0xFAB11BAF;
    private static readonly HashSet<string> VerifiedGameFingerprints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ac511ba1dd391bb6d7afdd70dde19b552244e194a7bfc6c0d1117a046047192f",
            "8370257f4d60c7b8def58be8804d8724d76b95639baf4f199a7d54ef75d6e782",
            "6433f32e22ce153dd0c9ffc273631d546ff9edf320f80fb2c74723f41b235014"
        };
    private readonly Func<bool> _isMainThread;

    internal ModRuntimeInfo(
        Version frameworkVersion,
        string gameVersion,
        string gameBuildFingerprint,
        string unityVersion,
        int il2CppMetadataVersion,
        string processArchitecture,
        int pointerSize,
        bool isVerifiedGameBuild,
        Func<bool> isMainThread)
    {
        FrameworkVersion = frameworkVersion;
        GameVersion = gameVersion;
        GameBuildFingerprint = gameBuildFingerprint;
        UnityVersion = unityVersion;
        Il2CppMetadataVersion = il2CppMetadataVersion;
        ProcessArchitecture = processArchitecture;
        PointerSize = pointerSize;
        IsVerifiedGameBuild = isVerifiedGameBuild;
        _isMainThread = isMainThread;
    }

    public Version FrameworkVersion { get; }
    public string GameVersion { get; }
    public string GameBuildFingerprint { get; }
    public string UnityVersion { get; }
    public int Il2CppMetadataVersion { get; }
    public string ProcessArchitecture { get; }
    public int PointerSize { get; }
    public bool IsVerifiedGameBuild { get; }
    public bool IsMainThread => _isMainThread();

    internal static ModRuntimeInfo Create(string gameDirectory, IUnsafeIl2CppApi api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(api);

        var fingerprint = ResolveCurrentGameFingerprint(gameDirectory);
        var gameVersion = ReadApplicationString(api, "get_version");
        var unityVersion = ReadApplicationString(api, "get_unityVersion");
        var metadataVersion = ReadMetadataVersion(Path.Combine(
            gameDirectory,
            "Ore Factory Squad_Data",
            "il2cpp_data",
            "Metadata",
            "global-metadata.dat"));
        var result = new ModRuntimeInfo(
            ModManifestValidator.CurrentSdkVersion,
            gameVersion,
            fingerprint,
            unityVersion,
            metadataVersion,
            RuntimeInformation.ProcessArchitecture.ToString(),
            IntPtr.Size,
            VerifiedGameFingerprints.Contains(fingerprint),
            () => ModRuntime.IsMainThread);
        RuntimeLog.Write(
            $"Runtime environment: framework={result.FrameworkVersion.ToString(3)}, " +
            $"game={result.GameVersion}, unity={result.UnityVersion}, " +
            $"metadata={result.Il2CppMetadataVersion}, architecture={result.ProcessArchitecture}, " +
            $"pointerSize={result.PointerSize}, fingerprint={result.GameBuildFingerprint}, " +
            $"verified={result.IsVerifiedGameBuild}.");
        return result;
    }

    internal static string ReadGameFingerprint(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "OFS", "install-manifest.json");
        if (!File.Exists(path)) return "unknown";
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("OFS install manifest has an invalid size.");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("gameFingerprint", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: 64 } fingerprint ||
            fingerprint.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("OFS install manifest has no valid game fingerprint.");
        }
        return fingerprint.ToLowerInvariant();
    }

    private static string ResolveCurrentGameFingerprint(string gameDirectory)
    {
        var installedFingerprint = ReadGameFingerprint(gameDirectory);
        try
        {
            var manifestPath = Path.Combine(gameDirectory, "OFS", "install-manifest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var installedBuildId = document.RootElement.TryGetProperty("gameBuildId", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            var currentBuildId = GameBuildFingerprintService.ReadSteamBuildId(gameDirectory);
            if (string.Equals(installedBuildId, currentBuildId, StringComparison.Ordinal))
                return installedFingerprint;

            var current = GameBuildFingerprintService.ScanAsync(gameDirectory, currentBuildId)
                .GetAwaiter().GetResult();
            RuntimeLog.Write(
                $"Game update detected at runtime: installedBuild={installedBuildId ?? "unknown"}, " +
                $"currentBuild={currentBuildId}, fingerprint={current.Fingerprint}.");
            return current.Fingerprint;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Current game build could not be fingerprinted safely: {exception.Message}");
            return "unknown";
        }
    }

    internal static int ReadMetadataVersion(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Il2CppMetadataMagic)
            throw new InvalidDataException("IL2CPP global metadata magic is invalid.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        return version > 0
            ? version
            : throw new InvalidDataException("IL2CPP metadata version is invalid.");
    }

    private static string ReadApplicationString(IUnsafeIl2CppApi api, string getter)
    {
        var application = api.FindClass(
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Application");
        var value = api.RuntimeInvoke(api.FindMethod(application, getter, 0), 0, 0);
        return api.ReadString(value);
    }
}
