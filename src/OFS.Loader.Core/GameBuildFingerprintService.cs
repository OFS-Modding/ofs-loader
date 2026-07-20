using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OFS.Loader;

internal sealed record ScannedBuildArtifact(string RelativePath, long Size, string Sha256);

internal sealed record ScannedGameBuild(
    string BuildId,
    string BuildGuid,
    string Fingerprint,
    IReadOnlyList<ScannedBuildArtifact> Artifacts);

internal static class GameBuildFingerprintService
{
    internal const string SchemaVersion = "ofs-build-fingerprint/v1";
    private const string AppId = "4210580";
    private static readonly string[] FingerprintedArtifacts =
    [
        "GameAssembly.dll",
        "UnityPlayer.dll",
        Path.Combine("Ore Factory Squad_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
        Path.Combine("Ore Factory Squad_Data", "globalgamemanagers"),
    ];

    internal static async Task<ScannedGameBuild> ScanAsync(string gameDirectory, string? buildId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        buildId ??= ReadSteamBuildId(gameDirectory);
        if (string.IsNullOrWhiteSpace(buildId) || buildId.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("Steam BuildID is invalid.");

        var artifacts = new List<ScannedBuildArtifact>(FingerprintedArtifacts.Length);
        foreach (var relativePath in FingerprintedArtifacts)
        {
            var fullPath = Path.Combine(gameDirectory, relativePath);
            if (!File.Exists(fullPath))
                throw new IOException($"Required build artifact is missing: '{fullPath}'.");
            var info = new FileInfo(fullPath);
            await using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            artifacts.Add(new ScannedBuildArtifact(relativePath.Replace('\\', '/'), info.Length, hash));
        }

        var buildGuid = ReadBuildGuid(gameDirectory);
        return new ScannedGameBuild(
            buildId, buildGuid, ComputeCompositeFingerprint(buildId, buildGuid, artifacts),
            artifacts.AsReadOnly());
    }

    internal static string ReadSteamBuildId(string gameDirectory)
    {
        var steamApps = Directory.GetParent(gameDirectory)?.Parent?.FullName
            ?? throw new IOException($"Steam library could not be resolved from '{gameDirectory}'.");
        var manifest = Path.Combine(steamApps, $"appmanifest_{AppId}.acf");
        if (!File.Exists(manifest)) throw new IOException($"Steam manifest is missing: '{manifest}'.");
        var match = Regex.Match(
            File.ReadAllText(manifest), "\\\"buildid\\\"\\s+\\\"(?<id>[0-9]+)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["id"].Value
            : throw new InvalidDataException($"Steam BuildID is missing from '{manifest}'.");
    }

    private static string ReadBuildGuid(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "Ore Factory Squad_Data", "boot.config");
        var match = Regex.Match(
            File.ReadAllText(path), "^build-guid=(?<guid>[0-9a-fA-F]{32})$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["guid"].Value.ToLowerInvariant()
            : throw new InvalidDataException($"build-guid is missing from '{path}'.");
    }

    private static string ComputeCompositeFingerprint(
        string buildId, string buildGuid, IEnumerable<ScannedBuildArtifact> artifacts)
    {
        var canonical = new StringBuilder().Append(SchemaVersion).Append('\n')
            .Append("build-id:").Append(buildId).Append('\n')
            .Append("build-guid:").Append(buildGuid).Append('\n');
        foreach (var artifact in artifacts.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            canonical.Append(artifact.RelativePath).Append(':')
                .Append(artifact.Size).Append(':').Append(artifact.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
