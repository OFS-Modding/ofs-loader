using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OFS.Loader;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed record CachedCatalogView(
    string Status,
    IReadOnlyList<ModCatalogEntry> Entries,
    ModCatalog? Catalog,
    bool IsOfficial = false);

internal static class ModCatalogCache
{
    private const int MaximumCatalogBytes = 12 * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    public static CachedCatalogView Load()
    {
        try
        {
            var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new InvalidOperationException("Game process directory is unavailable.");
            var path = Path.Combine(gameDirectory, "OFS", "cache", "catalog.signed.json");
            if (!File.Exists(path))
            {
                var legacy = Path.Combine(gameDirectory, "OFS", "cache", "catalog.json");
                return new CachedCatalogView(
                    File.Exists(legacy) ? "UNSIGNED CACHE - RESYNC REQUIRED" : "CATALOG NOT SYNCED",
                    [],
                    null);
            }
            var info = new FileInfo(path);
            if (info.Length > MaximumCatalogBytes)
            {
                return new CachedCatalogView("CATALOG CACHE TOO LARGE", [], null);
            }

            var envelope = JsonSerializer.Deserialize<SignedModCatalog>(File.ReadAllText(path));
            var trustPath = Path.Combine(gameDirectory, "OFS", "trust", "catalog-keys.json");
            if (!File.Exists(trustPath) || new FileInfo(trustPath).Length > 1024 * 1024)
            {
                return new CachedCatalogView("CATALOG KEY NOT TRUSTED", [], null);
            }
            var trustStore = JsonSerializer.Deserialize<ModCatalogTrustStore>(File.ReadAllText(trustPath));
            var verification = ModCatalogSignatures.Verify(envelope, trustStore);
            if (!verification.Success || verification.Catalog is null)
            {
                RuntimeLog.Write($"Signed catalog cache rejected: {string.Join(" ", verification.Errors)}");
                return new CachedCatalogView("CATALOG SIGNATURE INVALID", [], null);
            }
            var catalog = verification.Catalog;

            var fingerprint = ReadInstalledFingerprint(gameDirectory);
            if (fingerprint is not null &&
                !string.Equals(catalog.GameBuild, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Write(
                    $"Catalog targets game build {catalog.GameBuild}, installed build is {fingerprint}.");
                return new CachedCatalogView("CATALOG IS FOR ANOTHER BUILD", [], null);
            }
            if (!ModVersion.TryParse(catalog.FrameworkVersion, out var framework))
            {
                return new CachedCatalogView("CATALOG FRAMEWORK INVALID", [], null);
            }

            var compatible = catalog.Mods
                .Where(entry => ModCatalogValidator.IsCompatible(
                    entry,
                    fingerprint ?? catalog.GameBuild,
                    framework))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(entry => ParseVersion(entry.Version))
                    .First())
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RuntimeLog.Write(
                $"Signed catalog cache loaded: key={verification.KeyId}, " +
                $"fingerprint={verification.KeyFingerprint}, versions={catalog.Mods.Count}, " +
                $"compatibleMods={compatible.Length}.");
            return new CachedCatalogView(
                compatible.Length == 0 ? "NO COMPATIBLE MODS" : "READY",
                compatible,
                catalog,
                string.Equals(
                    verification.KeyId,
                    OfficialCatalogIdentity.KeyId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    verification.KeyFingerprint,
                    OfficialCatalogIdentity.PublicKeySha256,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            RuntimeLog.Write($"Catalog cache read failed: {exception.Message}");
            return new CachedCatalogView("CATALOG CACHE ERROR", [], null);
        }
    }

    public static async Task<CachedCatalogView> RefreshOfficialAsync()
    {
        try
        {
            var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new InvalidOperationException("Game process directory is unavailable.");
            var bytes = await DownloadOfficialEnvelopeAsync();
            var envelope = JsonSerializer.Deserialize<SignedModCatalog>(bytes)
                ?? throw new InvalidDataException("Official catalog deserialized to null.");
            var officialKey = OfficialCatalogIdentity.CreateTrustKey();
            var verification = ModCatalogSignatures.Verify(
                envelope,
                new ModCatalogTrustStore { Keys = [officialKey] });
            if (!verification.Success || verification.Catalog is null)
            {
                throw new InvalidDataException(
                    $"Official catalog signature is invalid: {string.Join(" ", verification.Errors)}");
            }

            var fingerprint = ReadInstalledFingerprint(gameDirectory);
            if (fingerprint is not null &&
                !string.Equals(
                    verification.Catalog.GameBuild,
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Official catalog targets {verification.Catalog.GameBuild}, installed build is {fingerprint}.");
            }

            var frameworkRoot = Path.Combine(gameDirectory, "OFS");
            var trustPath = Path.Combine(frameworkRoot, "trust", "catalog-keys.json");
            var existingTrust = await ReadTrustStoreAsync(trustPath);
            var mergedKeys = existingTrust.Keys
                .Where(key => !string.Equals(
                    key.Id,
                    OfficialCatalogIdentity.KeyId,
                    StringComparison.Ordinal))
                .Append(officialKey)
                .OrderBy(key => key.Id, StringComparer.Ordinal)
                .ToArray();
            var matchingOfficialKeys = existingTrust.Keys
                .Where(key => string.Equals(
                    key.Id,
                    OfficialCatalogIdentity.KeyId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingOfficialKeys.Length != 1 || matchingOfficialKeys[0] != officialKey)
            {
                await WriteAtomicJsonAsync(
                    trustPath,
                    existingTrust with { Keys = mergedKeys });
            }
            var cachePath = Path.Combine(frameworkRoot, "cache", "catalog.signed.json");
            if (!await CachedOfficialPayloadMatchesAsync(cachePath, envelope, officialKey))
            {
                await WriteAtomicBytesAsync(cachePath, bytes);
            }
            RuntimeLog.Write(
                $"Official catalog refreshed: key={verification.KeyId}, " +
                $"fingerprint={verification.KeyFingerprint}, versions={verification.Catalog.Mods.Count}.");
            return Load();
        }
        catch (Exception exception) when (exception is
            IOException or JsonException or UnauthorizedAccessException or
            HttpRequestException or InvalidDataException or CryptographicException)
        {
            RuntimeLog.Write($"Official catalog refresh failed; retaining trusted cache: {exception}");
            return Load();
        }
    }

    private static async Task<byte[]> DownloadOfficialEnvelopeAsync()
    {
        using var response = await Http.GetAsync(
            OfficialCatalogIdentity.Source,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("HTTPS downgrade detected for the official catalog.");
        }
        if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
        {
            throw new InvalidDataException("Official catalog exceeds the 12 MiB limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > MaximumCatalogBytes)
            {
                throw new InvalidDataException("Official catalog exceeds the 12 MiB limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return output.ToArray();
    }

    private static async Task<ModCatalogTrustStore> ReadTrustStoreAsync(string path)
    {
        if (!File.Exists(path)) return new ModCatalogTrustStore();
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw new InvalidDataException("Catalog trust store exceeds 1 MiB.");
        }
        var store = JsonSerializer.Deserialize<ModCatalogTrustStore>(await File.ReadAllBytesAsync(path))
            ?? throw new InvalidDataException("Catalog trust store deserialized to null.");
        var errors = ModCatalogSignatures.ValidateTrustStore(store);
        if (errors.Count != 0)
        {
            throw new InvalidDataException($"Catalog trust store is invalid: {string.Join(" ", errors)}");
        }
        return store;
    }

    private static async Task WriteAtomicJsonAsync<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        await WriteAtomicBytesAsync(path, bytes);
    }

    private static async Task WriteAtomicBytesAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            const int maximumMoveAttempts = 6;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt + 1 < maximumMoveAttempts)
                {
                    if (await FileMatchesAsync(path, bytes)) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)));
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> FileMatchesAsync(string path, ReadOnlyMemory<byte> expected)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length) return false;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var offset = 0;
            var buffer = new byte[Math.Min(64 * 1024, expected.Length)];
            while (offset < expected.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(
                    0,
                    Math.Min(buffer.Length, expected.Length - offset)));
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Span.Slice(offset, read)))
                    return false;
                offset += read;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> CachedOfficialPayloadMatchesAsync(
        string path,
        SignedModCatalog downloaded,
        ModCatalogTrustKey officialKey)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumCatalogBytes) return false;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var cached = await JsonSerializer.DeserializeAsync<SignedModCatalog>(stream);
            if (cached is null || !string.Equals(cached.Payload, downloaded.Payload, StringComparison.Ordinal))
                return false;
            var verification = ModCatalogSignatures.Verify(
                cached,
                new ModCatalogTrustStore { Keys = [officialKey] });
            return verification.Success;
        }
        catch (Exception exception) when (exception is IOException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "OFS-ModHub",
            ModManifestValidator.CurrentSdkVersion.ToString(3)));
        return client;
    }

    private static string? ReadInstalledFingerprint(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "OFS", "install-manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("gameFingerprint", out var property)
            ? property.GetString()
            : null;
    }

    private static ModVersion ParseVersion(string value)
    {
        _ = ModVersion.TryParse(value, out var version);
        return version;
    }
}
