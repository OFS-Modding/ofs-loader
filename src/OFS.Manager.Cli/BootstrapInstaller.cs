using System.Security.Cryptography;
using System.Text.Json;

namespace OFS.Manager;

internal sealed record InstalledFileManifest(string RelativePath, long Size, string Sha256);

internal sealed record BootstrapInstallManifest(
    string SchemaVersion,
    string GameFingerprint,
    string? GameBuildId,
    IReadOnlyList<InstalledFileManifest> Files,
    DateTimeOffset InstalledAtUtc);

internal sealed record BootstrapStatus(
    string State,
    string GameDirectory,
    string? GameFingerprint,
    string? InstalledSha256,
    string? ExpectedSha256,
    string? Detail);

internal static class BootstrapInstaller
{
    private const string BootstrapRelativePath = "version.dll";
    private const string FrameworkDirectory = "OFS";
    private const string RuntimeRelativeDirectory = "OFS/runtime";
    private const string ManifestFileName = "install-manifest.json";
    private const string PendingManifestFileName = "install-manifest.pending.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<BootstrapStatus> GetStatusAsync(GameInstallation installation)
    {
        var paths = GetPaths(installation);
        var manifest = await TryReadManifestAsync(paths.Manifest);
        var pendingExists = File.Exists(paths.PendingManifest);

        if (pendingExists)
        {
            return new BootstrapStatus(
                "recovery-required",
                installation.GameDirectory,
                manifest?.GameFingerprint,
                await TryComputeInstalledBootstrapHash(installation),
                GetExpectedBootstrapHash(manifest),
                $"Pending transaction found at '{paths.PendingManifest}'.");
        }

        if (manifest is null)
        {
            var foreignHash = await TryComputeInstalledBootstrapHash(installation);
            return foreignHash is null
                ? new BootstrapStatus("not-installed", installation.GameDirectory, null, null, null, null)
                : new BootstrapStatus(
                    "foreign-file",
                    installation.GameDirectory,
                    null,
                    foreignHash,
                    null,
                    "A version.dll exists without an OFS install manifest.");
        }

        foreach (var file in manifest.Files)
        {
            var fullPath = ResolveInstalledPath(installation, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                return CreateManifestStatus(
                    "damaged",
                    installation,
                    manifest,
                    await TryComputeInstalledBootstrapHash(installation),
                    $"Installed file is missing: '{file.RelativePath}'.");
            }

            var installedHash = await ComputeSha256Async(fullPath);
            if (!string.Equals(installedHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return CreateManifestStatus(
                    "modified",
                    installation,
                    manifest,
                    await TryComputeInstalledBootstrapHash(installation),
                    $"Installed file hash changed: '{file.RelativePath}'.");
            }
        }

        var gameUpdated = manifest.GameBuildId is not null &&
            !string.Equals(manifest.GameBuildId, installation.BuildId, StringComparison.Ordinal);
        return CreateManifestStatus(
            gameUpdated ? "game-updated" : "installed",
            installation,
            manifest,
            await TryComputeInstalledBootstrapHash(installation),
            gameUpdated
                ? $"Steam BuildID changed from {manifest.GameBuildId} to {installation.BuildId}. " +
                  "Run scan and install a verified framework update before enabling native hooks."
                : null);
    }

    public static async Task<BootstrapStatus> InstallAsync(
        GameInstallation installation,
        string bootstrapArtifact,
        string runtimeArtifactDirectory)
    {
        ValidateArtifacts(bootstrapArtifact, runtimeArtifactDirectory);

        var before = await GetStatusAsync(installation);
        if (before.State == "recovery-required")
        {
            await ValidateAndClearInterruptedInstallAsync(installation);
            before = await GetStatusAsync(installation);
        }

        if (before.State is not ("not-installed" or "installed" or "game-updated" or "modified" or "damaged"))
        {
            throw new InvalidDataException(
                $"Bootstrap install refused because current state is '{before.State}': {before.Detail}");
        }

        var build = await BuildScanner.ScanAsync(installation);
        var sources = await BuildSourceManifestAsync(bootstrapArtifact, runtimeArtifactDirectory);
        var paths = GetPaths(installation);
        Directory.CreateDirectory(paths.Framework);
        Directory.CreateDirectory(Path.Combine(paths.Framework, "logs"));
        Directory.CreateDirectory(paths.Staging);

        var existingManifest = await TryReadManifestAsync(paths.Manifest);
        if (before.State == "installed" &&
            string.Equals(before.GameFingerprint, build.Fingerprint, StringComparison.OrdinalIgnoreCase) &&
            existingManifest is not null &&
            ManifestsMatch(existingManifest.Files, sources.Select(source => source.Manifest)))
        {
            return before with { Detail = "Framework already matches the requested artifacts and game build." };
        }

        var manifest = new BootstrapInstallManifest(
            "ofs-bootstrap-install/v2",
            build.Fingerprint,
            build.BuildId,
            sources.Select(source => source.Manifest).ToArray(),
            DateTimeOffset.UtcNow);

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDirectory = Path.Combine(paths.Staging, $"install-{transactionId}");
        Directory.CreateDirectory(transactionDirectory);
        await WriteManifestAsync(paths.PendingManifest, manifest);

        try
        {
            foreach (var source in sources)
            {
                var stagedPath = Path.Combine(
                    transactionDirectory,
                    source.Manifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                File.Copy(source.SourcePath, stagedPath, overwrite: false);

                var stagedHash = await ComputeSha256Async(stagedPath);
                if (!string.Equals(stagedHash, source.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Staged hash mismatch for '{source.Manifest.RelativePath}'.");
                }
            }

            foreach (var source in sources.Where(value => value.Manifest.RelativePath != BootstrapRelativePath))
            {
                PromoteStagedFile(installation, transactionDirectory, source.Manifest.RelativePath);
            }

            PromoteStagedFile(installation, transactionDirectory, BootstrapRelativePath);
            File.Move(paths.PendingManifest, paths.Manifest, overwrite: true);
        }
        finally
        {
            TryDeleteEmptyTransactionTree(transactionDirectory);
        }

        return await GetStatusAsync(installation);
    }

    private static async Task ValidateAndClearInterruptedInstallAsync(GameInstallation installation)
    {
        var paths = GetPaths(installation);
        var installedManifest = await TryReadManifestAsync(paths.Manifest)
            ?? throw new InvalidDataException(
                "Interrupted install has no prior manifest; automatic recovery is unsafe.");
        var pendingManifest = await TryReadManifestAsync(paths.PendingManifest)
            ?? throw new InvalidDataException("The pending install manifest is unreadable.");

        var installedBootstrap = installedManifest.Files.SingleOrDefault(
            file => string.Equals(file.RelativePath, BootstrapRelativePath, StringComparison.Ordinal));
        var pendingBootstrap = pendingManifest.Files.SingleOrDefault(
            file => string.Equals(file.RelativePath, BootstrapRelativePath, StringComparison.Ordinal));
        var bootstrapPath = Path.Combine(installation.GameDirectory, "version.dll");
        if (installedBootstrap is null || pendingBootstrap is null || !File.Exists(bootstrapPath))
        {
            throw new InvalidDataException(
                "Interrupted install cannot prove ownership of the bootstrap proxy.");
        }

        var currentHash = await ComputeSha256Async(bootstrapPath);
        if (!string.Equals(currentHash, installedBootstrap.Sha256, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentHash, pendingBootstrap.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Interrupted install found an unrecognized version.dll; automatic recovery refused.");
        }

        File.Delete(paths.PendingManifest);
    }

    public static async Task<BootstrapStatus> UninstallAsync(GameInstallation installation)
    {
        var status = await GetStatusAsync(installation);
        if (status.State == "not-installed")
        {
            return status with { Detail = "Nothing to uninstall." };
        }

        if (status.State is not ("installed" or "game-updated"))
        {
            throw new InvalidDataException(
                $"Bootstrap uninstall refused because current state is '{status.State}': {status.Detail}");
        }

        var paths = GetPaths(installation);
        var manifest = await TryReadManifestAsync(paths.Manifest)
            ?? throw new InvalidDataException("The OFS install manifest disappeared during uninstall.");

        foreach (var file in manifest.Files.OrderByDescending(value => value.RelativePath.Length))
        {
            File.Delete(ResolveInstalledPath(installation, file.RelativePath));
        }

        File.Delete(paths.Manifest);
        return await GetStatusAsync(installation);
    }

    private static void ValidateArtifacts(string bootstrapArtifact, string runtimeArtifactDirectory)
    {
        if (!File.Exists(bootstrapArtifact))
        {
            throw new IOException($"Bootstrap artifact not found: '{bootstrapArtifact}'.");
        }

        if (!Directory.Exists(runtimeArtifactDirectory) ||
            !File.Exists(Path.Combine(runtimeArtifactDirectory, "hostfxr.dll")) ||
            !File.Exists(Path.Combine(runtimeArtifactDirectory, "OFS.Runtime.Entry.dll")) ||
            !File.Exists(Path.Combine(runtimeArtifactDirectory, "OFS.Runtime.Entry.runtimeconfig.json")))
        {
            throw new IOException($"Published OFS runtime is incomplete: '{runtimeArtifactDirectory}'.");
        }
    }

    private static async Task<List<SourceFile>> BuildSourceManifestAsync(
        string bootstrapArtifact,
        string runtimeArtifactDirectory)
    {
        var files = new List<(string Source, string Relative)>
        {
            (bootstrapArtifact, BootstrapRelativePath),
        };

        files.AddRange(Directory
            .EnumerateFiles(runtimeArtifactDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (
                path,
                $"{RuntimeRelativeDirectory}/{Path.GetRelativePath(runtimeArtifactDirectory, path).Replace('\\', '/')}")));

        var sources = new List<SourceFile>(files.Count);
        foreach (var (source, relative) in files.OrderBy(value => value.Relative, StringComparer.Ordinal))
        {
            var info = new FileInfo(source);
            sources.Add(new SourceFile(
                source,
                new InstalledFileManifest(relative, info.Length, await ComputeSha256Async(source))));
        }

        return sources;
    }

    private static void PromoteStagedFile(
        GameInstallation installation,
        string transactionDirectory,
        string relativePath)
    {
        var stagedPath = Path.Combine(
            transactionDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var targetPath = ResolveInstalledPath(installation, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Move(stagedPath, targetPath, overwrite: true);
    }

    private static string ResolveInstalledPath(GameInstallation installation, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(installation.GameDirectory, normalized));
        var gameRoot = Path.GetFullPath(installation.GameDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Install manifest path escapes the game directory: '{relativePath}'.");
        }

        return fullPath;
    }

    private static bool ManifestsMatch(
        IReadOnlyList<InstalledFileManifest> left,
        IEnumerable<InstalledFileManifest> right) => left
        .OrderBy(value => value.RelativePath, StringComparer.Ordinal)
        .SequenceEqual(right.OrderBy(value => value.RelativePath, StringComparer.Ordinal));

    private static BootstrapStatus CreateManifestStatus(
        string state,
        GameInstallation installation,
        BootstrapInstallManifest manifest,
        string? installedBootstrapHash,
        string? detail) => new(
            state,
            installation.GameDirectory,
            manifest.GameFingerprint,
            installedBootstrapHash,
            GetExpectedBootstrapHash(manifest),
            detail);

    private static string? GetExpectedBootstrapHash(BootstrapInstallManifest? manifest) => manifest?.Files
        .FirstOrDefault(file => file.RelativePath == BootstrapRelativePath)
        ?.Sha256;

    private static Task<string?> TryComputeInstalledBootstrapHash(GameInstallation installation)
    {
        var path = Path.Combine(installation.GameDirectory, BootstrapRelativePath);
        return File.Exists(path)
            ? ComputeSha256Async(path).ContinueWith<string?>(task => task.Result, TaskScheduler.Default)
            : Task.FromResult<string?>(null);
    }

    private static async Task<BootstrapInstallManifest?> TryReadManifestAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BootstrapInstallManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException($"OFS install manifest is empty: '{path}'.");
    }

    private static async Task WriteManifestAsync(string path, BootstrapInstallManifest manifest)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions);
        await stream.FlushAsync();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void TryDeleteEmptyTransactionTree(string transactionDirectory)
    {
        if (!Directory.Exists(transactionDirectory))
        {
            return;
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(transactionDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(value => value.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(transactionDirectory).Any())
        {
            Directory.Delete(transactionDirectory);
        }
    }

    private static InstallPaths GetPaths(GameInstallation installation)
    {
        var framework = Path.Combine(installation.GameDirectory, FrameworkDirectory);
        return new InstallPaths(
            framework,
            Path.Combine(framework, ManifestFileName),
            Path.Combine(framework, PendingManifestFileName),
            Path.Combine(framework, "staging"));
    }

    private sealed record SourceFile(string SourcePath, InstalledFileManifest Manifest);

    private sealed record InstallPaths(
        string Framework,
        string Manifest,
        string PendingManifest,
        string Staging);
}
