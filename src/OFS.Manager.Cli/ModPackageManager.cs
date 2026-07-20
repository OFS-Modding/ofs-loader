using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class ModPackageManager
{
    private const int MaximumFileCount = 4096;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<ModValidationResult> ValidateAsync(string source)
    {
        var fullSource = Path.GetFullPath(source);
        if (Directory.Exists(fullSource))
        {
            return await ValidateDirectoryAsync(fullSource);
        }
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException("Mod source does not exist.", fullSource);
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "ofs-sdk-validate",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await ExtractArchiveAsync(fullSource, temporaryRoot);
            return (await ValidateDirectoryAsync(temporaryRoot)) with { Source = fullSource };
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public static async Task<ModPackageResult> PackAsync(string sourceDirectory, string? outputPath)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var validation = await ValidateDirectoryAsync(source);
        if (!validation.Valid || validation.Manifest is null)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }

        var output = Path.GetFullPath(outputPath ?? Path.Combine(
            Environment.CurrentDirectory,
            $"{validation.Manifest.Id}-{validation.Manifest.Version}.ofmod"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var files = EnumerateSafeFiles(source)
            .Where(file => !string.Equals(file.FullName, output, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => Path.GetRelativePath(source, file.FullName), StringComparer.Ordinal)
            .ToArray();
        var temporaryOutput = output + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporaryOutput,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in files)
                {
                    var relativePath = NormalizeArchivePath(Path.GetRelativePath(source, file.FullName));
                    var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = DeterministicTimestamp;
                    await using var input = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var destination = entry.Open();
                    await input.CopyToAsync(destination);
                }
            }

            File.Move(temporaryOutput, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }

        return new ModPackageResult(
            output,
            validation.Manifest.Id,
            validation.Manifest.Version,
            files.Length,
            new FileInfo(output).Length,
            await ComputeSha256Async(output));
    }

    public static async Task<ModInstallResult> InstallAsync(
        string source,
        GameInstallation installation)
    {
        var fullSource = Path.GetFullPath(source);
        var frameworkRoot = Path.Combine(installation.GameDirectory, "OFS");
        var modsRoot = Path.Combine(frameworkRoot, "mods");
        var stagingRoot = Path.Combine(frameworkRoot, ".staging");
        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(stagingRoot);

        var transactionId = Guid.NewGuid().ToString("N");
        var payload = Path.Combine(stagingRoot, $"mod-payload-{transactionId}");
        Directory.CreateDirectory(payload);
        string? packageHash = null;
        try
        {
            if (Directory.Exists(fullSource))
            {
                await CopyDirectoryAsync(fullSource, payload);
            }
            else if (File.Exists(fullSource))
            {
                packageHash = await ComputeSha256Async(fullSource);
                await ExtractArchiveAsync(fullSource, payload);
            }
            else
            {
                throw new FileNotFoundException("Mod source does not exist.", fullSource);
            }

            var validation = await ValidateDirectoryAsync(payload);
            if (!validation.Valid || validation.Manifest is null)
            {
                throw new InvalidDataException(string.Join(" ", validation.Errors));
            }

            var target = ResolveContainedPath(modsRoot, validation.Manifest.Id);
            var backup = Path.Combine(stagingRoot, $"mod-backup-{transactionId}");
            var replacedExisting = Directory.Exists(target);
            if (replacedExisting)
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(payload, target);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }
                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            var result = new ModInstallResult(
                validation.Manifest.Id,
                validation.Manifest.Name,
                validation.Manifest.Version,
                target,
                replacedExisting,
                packageHash,
                "Installed. Restart the game to load code mods.");
            await ProfileManager.StageEnableResolvedAsync(
                installation,
                [validation.Manifest.Id]);
            return result;
        }
        finally
        {
            if (Directory.Exists(payload))
            {
                Directory.Delete(payload, recursive: true);
            }
        }
    }

    private static async Task<ModValidationResult> ValidateDirectoryAsync(string source)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var errors = new List<string>();
        var manifestPath = Path.Combine(source, "manifest.json");
        ModManifest? manifest = null;
        if (!File.Exists(manifestPath))
        {
            errors.Add("manifest.json must exist at the package root.");
        }
        else
        {
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<ModManifest>(stream);
                errors.AddRange(ModManifestValidator.Validate(manifest));
            }
            catch (JsonException exception)
            {
                errors.Add($"manifest.json is invalid JSON: {exception.Message}");
            }
        }

        FileInfo[] files = [];
        try
        {
            files = EnumerateSafeFiles(source).ToArray();
            if (files.Length > MaximumFileCount)
            {
                errors.Add($"Package has {files.Length} files; maximum is {MaximumFileCount}.");
            }
            if (files.Sum(file => file.Length) > MaximumExpandedBytes)
            {
                errors.Add($"Expanded package exceeds {MaximumExpandedBytes} bytes.");
            }
        }
        catch (InvalidDataException exception)
        {
            errors.Add(exception.Message);
        }

        if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            try
            {
                var assembly = ResolveContainedPath(source, manifest.Assembly);
                if (!File.Exists(assembly))
                {
                    errors.Add($"Entrypoint assembly '{manifest.Assembly}' does not exist.");
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add(exception.Message);
            }
        }

        return new ModValidationResult(
            source,
            errors.Count == 0,
            manifest,
            files.Length,
            files.Sum(file => file.Length),
            errors);
    }

    private static IEnumerable<FileInfo> EnumerateSafeFiles(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Links/reparse points are not allowed: '{path}'.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                continue;
            }

            var relativePath = NormalizeArchivePath(Path.GetRelativePath(root, path));
            if (!seen.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"Package contains case-insensitive duplicate path '{relativePath}'.");
            }
            yield return new FileInfo(path);
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumFileCount)
        {
            throw new InvalidDataException(
                $"Archive has {archive.Entries.Count} entries; maximum is {MaximumFileCount}.");
        }

        long expandedBytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrEmpty(normalized) || normalized.EndsWith('/'))
            {
                continue;
            }
            if (IsLink(entry))
            {
                throw new InvalidDataException($"Archive links are not allowed: '{entry.FullName}'.");
            }
            if (!seen.Add(normalized))
            {
                throw new InvalidDataException(
                    $"Archive contains duplicate path '{entry.FullName}'.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    $"Expanded archive exceeds {MaximumExpandedBytes} bytes.");
            }

            var destination = ResolveContainedPath(destinationRoot, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous);
            await input.CopyToAsync(output);
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination)
    {
        foreach (var file in EnumerateSafeFiles(source))
        {
            var relative = Path.GetRelativePath(source, file.FullName);
            var target = ResolveContainedPath(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = file.OpenRead();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous);
            await input.CopyToAsync(output);
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Absolute package path is not allowed: '{relativePath}'.");
        }
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path escapes its root: '{relativePath}'.");
        }
        return resolved;
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/');

    private static bool IsLink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixFileType == 0xA000 ||
               (windowsAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed record ModValidationResult(
    string Source,
    bool Valid,
    ModManifest? Manifest,
    int FileCount,
    long ExpandedBytes,
    IReadOnlyList<string> Errors);

internal sealed record ModPackageResult(
    string Package,
    string Id,
    string Version,
    int FileCount,
    long PackageBytes,
    string Sha256);

internal sealed record ModInstallResult(
    string Id,
    string Name,
    string Version,
    string Directory,
    bool ReplacedExisting,
    string? PackageSha256,
    string Detail);
