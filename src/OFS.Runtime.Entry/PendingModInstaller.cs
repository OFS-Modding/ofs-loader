using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class PendingModInstaller
{
    private const int MaximumFileCount = 4096;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static void Apply(string gameDirectory)
    {
        var frameworkRoot = Path.Combine(gameDirectory, "OFS");
        ApplyPendingRemovals(frameworkRoot);
        var pendingRoot = Path.Combine(frameworkRoot, "pending", "mods");
        if (!Directory.Exists(pendingRoot)) return;

        var modsRoot = Path.Combine(frameworkRoot, "mods");
        var stagingRoot = Path.Combine(frameworkRoot, ".staging");
        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(stagingRoot);
        foreach (var pending in Directory.EnumerateDirectories(pendingRoot)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = ValidatePayload(pending);
                var target = ResolveContainedPath(modsRoot, manifest.Id);
                var backup = Path.Combine(stagingRoot, $"startup-backup-{Guid.NewGuid():N}");
                if (Directory.Exists(target)) Directory.Move(target, backup);
                try
                {
                    Directory.Move(pending, target);
                }
                catch
                {
                    if (Directory.Exists(backup) && !Directory.Exists(target))
                    {
                        Directory.Move(backup, target);
                    }
                    throw;
                }
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                RuntimeLog.Write($"Activated pending mod '{manifest.Id}' {manifest.Version}.");
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"Pending mod activation failed for '{pending}': {exception}");
            }
        }
    }

    public static void StageUninstall(string modId)
    {
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("Game process directory is unavailable.");
        StageUninstall(gameDirectory, modId);
    }

    internal static void StageUninstall(string gameDirectory, string modId)
    {
        if (!ModManifestValidator.IsValidId(modId))
        {
            throw new InvalidDataException($"Invalid mod id '{modId}'.");
        }

        _ = ModProfileStore.Disable(modId);
        var removalRoot = Path.Combine(gameDirectory, "OFS", "pending", "removals");
        Directory.CreateDirectory(removalRoot);
        var marker = ResolveContainedPath(removalRoot, modId + ".remove");
        File.WriteAllText(marker, modId);
    }

    public static bool IsUninstallPending(string gameDirectory, string modId)
    {
        if (!ModManifestValidator.IsValidId(modId)) return false;
        var removalRoot = Path.Combine(gameDirectory, "OFS", "pending", "removals");
        return File.Exists(ResolveContainedPath(removalRoot, modId + ".remove"));
    }

    private static void ApplyPendingRemovals(string frameworkRoot)
    {
        var removalRoot = Path.Combine(frameworkRoot, "pending", "removals");
        if (!Directory.Exists(removalRoot)) return;

        var modsRoot = Path.Combine(frameworkRoot, "mods");
        var stagingRoot = Path.Combine(frameworkRoot, ".staging");
        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(stagingRoot);
        foreach (var marker in Directory.EnumerateFiles(removalRoot, "*.remove"))
        {
            try
            {
                var modId = File.ReadAllText(marker).Trim();
                if (!ModManifestValidator.IsValidId(modId) ||
                    !string.Equals(Path.GetFileNameWithoutExtension(marker), modId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Pending removal marker is invalid.");
                }
                var target = ResolveContainedPath(modsRoot, modId);
                if (Directory.Exists(target))
                {
                    var tombstone = Path.Combine(stagingRoot, $"remove-{Guid.NewGuid():N}");
                    Directory.Move(target, tombstone);
                    Directory.Delete(tombstone, recursive: true);
                }
                File.Delete(marker);
                RuntimeLog.Write($"Uninstalled pending mod '{modId}'.");
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"Pending mod removal failed for '{marker}': {exception}");
            }
        }
    }

    public static async Task<string> StageCatalogInstallAsync(ModCatalog catalog, string id)
    {
        var resolution = ModCatalogResolver.Resolve(catalog, [new ModDependency { Id = id }]);
        if (!resolution.Success)
        {
            throw new InvalidDataException(string.Join(" ", resolution.Errors));
        }

        return await StageResolvedAsync(
            resolution.InstallOrder,
            resolution.InstallOrder.Select(entry => entry.Id),
            []);
    }

    public static Task<string> StageNetworkRemediationAsync(NetworkRemediationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Success)
        {
            throw new InvalidOperationException(
                $"Cannot stage failed remediation plan: {string.Join(" ", plan.Errors)}");
        }
        return StageResolvedAsync(
            plan.InstallOrder,
            plan.EnableIds,
            plan.DisableIds);
    }

    private static async Task<string> StageResolvedAsync(
        IReadOnlyList<ModCatalogEntry> installOrder,
        IEnumerable<string> enableIds,
        IEnumerable<string> disableIds)
    {
        var enable = enableIds.ToArray();
        var disable = disableIds.ToArray();
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("Game process directory is unavailable.");
        var frameworkRoot = Path.Combine(gameDirectory, "OFS");
        var transaction = Path.Combine(
            frameworkRoot,
            ".staging",
            $"catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transaction);
        try
        {
            var payloads = new List<(ModCatalogEntry Entry, string Path)>();
            foreach (var entry in installOrder)
            {
                var archive = Path.Combine(transaction, $"{payloads.Count:D4}.ofmod");
                var payload = Path.Combine(transaction, $"payload-{payloads.Count:D4}");
                Directory.CreateDirectory(payload);
                await DownloadAsync(entry, archive);
                await ExtractAsync(archive, payload);
                var manifest = ValidatePayload(payload);
                if (!string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Catalog/package identity mismatch for '{entry.Id}' {entry.Version}.");
                }
                payloads.Add((entry, payload));
            }

            var pendingRoot = Path.Combine(frameworkRoot, "pending", "mods");
            Directory.CreateDirectory(pendingRoot);
            var promoted = new List<(string Target, string? Backup)>();
            try
            {
                foreach (var payload in payloads)
                {
                    var target = ResolveContainedPath(pendingRoot, payload.Entry.Id);
                    string? oldPending = null;
                    if (Directory.Exists(target))
                    {
                        oldPending = Path.Combine(transaction, $"old-{Guid.NewGuid():N}");
                        Directory.Move(target, oldPending);
                    }
                    try
                    {
                        Directory.Move(payload.Path, target);
                    }
                    catch
                    {
                        if (oldPending is not null &&
                            Directory.Exists(oldPending) &&
                            !Directory.Exists(target))
                        {
                            Directory.Move(oldPending, target);
                        }
                        throw;
                    }
                    promoted.Add((target, oldPending));
                }
                ModProfileStore.StageResolvedChanges(enable, disable);
            }
            catch
            {
                foreach (var item in promoted.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(item.Target))
                    {
                        Directory.Delete(item.Target, recursive: true);
                    }
                    if (item.Backup is not null && Directory.Exists(item.Backup))
                    {
                        Directory.Move(item.Backup, item.Target);
                    }
                }
                throw;
            }
            foreach (var item in promoted)
            {
                if (item.Backup is not null && Directory.Exists(item.Backup))
                {
                    Directory.Delete(item.Backup, recursive: true);
                }
            }
            var installedText = payloads.Count == 0
                ? "no package changes"
                : string.Join(", ", payloads.Select(payload =>
                    $"{payload.Entry.Id} {payload.Entry.Version}"));
            return $"{installedText}; enable={string.Join(',', enable)}; " +
                   $"disable={string.Join(',', disable)}";
        }
        finally
        {
            if (Directory.Exists(transaction)) Directory.Delete(transaction, recursive: true);
        }
    }

    private static async Task DownloadAsync(ModCatalogEntry entry, string destination)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "OFS-ModHub",
            ModManifestValidator.CurrentSdkVersion.ToString(3)));
        using var response = await client.GetAsync(
            new Uri(entry.Package.Url, UriKind.Absolute),
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"HTTPS downgrade detected for package '{entry.Id}'.");
        }
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != entry.Package.Bytes)
        {
            throw new InvalidDataException(
                $"Content-Length mismatch for '{entry.Id}': {contentLength} != {entry.Package.Bytes}.");
        }

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            total = checked(total + read);
            if (total > entry.Package.Bytes)
            {
                throw new InvalidDataException($"Package '{entry.Id}' exceeded its declared size.");
            }
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        await output.FlushAsync();
        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (total != entry.Package.Bytes ||
            !string.Equals(actualHash, entry.Package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package '{entry.Id}' failed SHA-256/size verification.");
        }
    }

    private static async Task ExtractAsync(string archivePath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumFileCount)
        {
            throw new InvalidDataException("Package contains too many entries.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.Length == 0 || normalized.EndsWith('/')) continue;
            if (!seen.Add(normalized))
            {
                throw new InvalidDataException($"Duplicate archive path '{entry.FullName}'.");
            }
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 ||
                ((FileAttributes)(entry.ExternalAttributes & 0xFFFF) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Archive link '{entry.FullName}' is forbidden.");
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("Expanded package is too large.");
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

    private static ModManifest ValidatePayload(string root)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Manifest deserialized to null.");
        var errors = ModManifestValidator.Validate(manifest);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        var assembly = ResolveContainedPath(root, manifest.Assembly);
        if (!File.Exists(assembly)) throw new InvalidDataException("Entrypoint assembly is missing.");
        return manifest;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Absolute path '{relativePath}' is forbidden.");
        }
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!result.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes its root.");
        }
        return result;
    }
}

internal static class RuntimeCatalogInstaller
{
    private static int _busy;

    public static void BeginInstall(ModCatalog catalog, string id, Action<string> updateStatus)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            updateStatus("BUSY");
            return;
        }
        updateStatus("DOWNLOADING");
        _ = Task.Run(async () =>
        {
            try
            {
                var staged = await PendingModInstaller.StageCatalogInstallAsync(catalog, id);
                RuntimeLog.Write($"Catalog install staged for restart: {staged}.");
                ModRuntime.EnqueueMainThread(() => updateStatus("RESTART REQUIRED"));
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"Catalog install failed for '{id}': {exception}");
                ModRuntime.EnqueueMainThread(() => updateStatus("INSTALL FAILED"));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    public static void BeginRemediation(
        NetworkRemediationPlan plan,
        Action<string> updateStatus)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(updateStatus);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            updateStatus("BUSY");
            return;
        }
        updateStatus("APPLYING JOIN FIX");
        _ = Task.Run(async () =>
        {
            try
            {
                var staged = await PendingModInstaller.StageNetworkRemediationAsync(plan);
                RuntimeLog.Write($"Network remediation staged for restart: {staged}.");
                ModRuntime.EnqueueMainThread(() => updateStatus("JOIN FIX READY - RESTART"));
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"Network remediation staging failed: {exception}");
                ModRuntime.EnqueueMainThread(() => updateStatus("JOIN FIX FAILED"));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }
}
