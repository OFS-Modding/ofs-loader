using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class CatalogManager
{
    private const int MaximumCatalogBytes = 12 * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<CatalogValidationResult> ValidateAsync(string source)
    {
        var loaded = await LoadAsync(source);
        return new CatalogValidationResult(
            loaded.Source,
            loaded.Errors.Count == 0,
            loaded.Catalog,
            loaded.Catalog?.Mods.Count ?? 0,
            loaded.Envelope is not null,
            loaded.Envelope?.KeyId,
            loaded.Errors);
    }

    public static async Task<ModCatalogResolution> ResolveAsync(
        string source,
        string id,
        string version = "*")
    {
        var loaded = await LoadAsync(source);
        if (loaded.Catalog is null || loaded.Errors.Count != 0)
        {
            return new ModCatalogResolution(false, [], loaded.Errors);
        }
        return ModCatalogResolver.Resolve(
            loaded.Catalog,
            [new ModDependency { Id = id, Version = version }]);
    }

    public static async Task<CatalogSyncResult> SyncAsync(
        string source,
        GameInstallation installation)
    {
        var loaded = await LoadTrustedAsync(source, installation);
        if (loaded.Catalog is null || loaded.Errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", loaded.Errors));
        }
        await EnsureCatalogTargetsInstallationAsync(loaded.Catalog, installation);

        var destination = await CacheTrustedAsync(loaded.Envelope!, installation);

        return new CatalogSyncResult(
            loaded.Source,
            destination,
            loaded.Catalog.GameBuild,
            loaded.Catalog.FrameworkVersion,
            loaded.Catalog.Mods.Count,
            loaded.Catalog.GeneratedAtUtc,
            loaded.Envelope!.KeyId,
            loaded.KeyFingerprint!);
    }

    public static async Task<CatalogInstallResult> InstallAsync(
        string source,
        string id,
        GameInstallation installation)
    {
        var loaded = await LoadTrustedAsync(source, installation);
        if (loaded.Catalog is null || loaded.Errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", loaded.Errors));
        }
        await EnsureCatalogTargetsInstallationAsync(loaded.Catalog, installation);

        var resolution = ModCatalogResolver.Resolve(
            loaded.Catalog,
            [new ModDependency { Id = id }]);
        if (!resolution.Success)
        {
            throw new InvalidDataException(string.Join(" ", resolution.Errors));
        }

        var transactionRoot = Path.Combine(
            Path.GetTempPath(),
            "ofs-sdk-catalog",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        try
        {
            var downloads = new List<(ModCatalogEntry Entry, string Path)>();
            foreach (var entry in resolution.InstallOrder)
            {
                var path = Path.Combine(transactionRoot, $"{downloads.Count:D4}.ofmod");
                await DownloadPackageAsync(entry, path);
                var validation = await ModPackageManager.ValidateAsync(path);
                if (!validation.Valid || validation.Manifest is null)
                {
                    throw new InvalidDataException(
                        $"Downloaded package '{entry.Id}' is invalid: {string.Join(" ", validation.Errors)}");
                }
                if (!string.Equals(validation.Manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(validation.Manifest.Version, entry.Version, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Catalog/package identity mismatch: expected {entry.Id} {entry.Version}, " +
                        $"received {validation.Manifest.Id} {validation.Manifest.Version}.");
                }
                downloads.Add((entry, path));
            }

            var installed = new List<ModInstallResult>();
            foreach (var download in downloads)
            {
                installed.Add(await ModPackageManager.InstallAsync(download.Path, installation));
            }

            _ = await CacheTrustedAsync(loaded.Envelope!, installation);
            return new CatalogInstallResult(
                id,
                installed,
                "Installed dependency set. Restart the game to load code mods.");
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }
    }

    public static async Task<CatalogThumbnailCacheResult> CacheThumbnailAsync(
        string source,
        string id,
        GameInstallation installation)
    {
        var loaded = await LoadTrustedAsync(source, installation);
        if (loaded.Catalog is null || loaded.Errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", loaded.Errors));
        }
        await EnsureCatalogTargetsInstallationAsync(loaded.Catalog, installation);
        if (!ModVersion.TryParse(loaded.Catalog.FrameworkVersion, out var framework))
        {
            throw new InvalidDataException("Catalog frameworkVersion is invalid.");
        }
        var entry = loaded.Catalog.Mods
            .Where(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => ModCatalogValidator.IsCompatible(
                candidate,
                loaded.Catalog.GameBuild,
                framework))
            .OrderByDescending(candidate => ParseCatalogVersion(candidate.Version))
            .FirstOrDefault()
            ?? throw new InvalidDataException($"Catalog has no compatible entry for '{id}'.");
        var thumbnail = entry.Thumbnail
            ?? throw new InvalidDataException($"Catalog entry '{entry.Id}' has no thumbnail.");
        _ = await CacheTrustedAsync(loaded.Envelope!, installation);

        var cacheRoot = Path.Combine(
            installation.GameDirectory,
            "OFS",
            "cache",
            "thumbnails");
        var store = new CatalogThumbnailStore(cacheRoot, Http);
        var cached = await store.GetOrFetchAsync(thumbnail);
        return new CatalogThumbnailCacheResult(
            entry.Id,
            entry.Version,
            cached.Path,
            cached.Format.ToString().ToLowerInvariant(),
            cached.Width,
            cached.Height,
            cached.FromCache,
            thumbnail.Sha256.ToLowerInvariant());
    }

    private static async Task<CatalogLoadResult> LoadAsync(string source)
    {
        try
        {
            byte[] bytes;
            string resolvedSource;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                uri.Scheme is "https" or "http")
            {
                if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    return new CatalogLoadResult(
                        source, null, null, null, ["Catalog URL must use HTTPS."]);
                }
                bytes = await DownloadBytesAsync(uri, MaximumCatalogBytes);
                resolvedSource = uri.AbsoluteUri;
            }
            else
            {
                var path = Path.GetFullPath(source);
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return new CatalogLoadResult(
                        path, null, null, null, ["Catalog source does not exist."]);
                }
                if (info.Length > MaximumCatalogBytes)
                {
                    return new CatalogLoadResult(
                        path, null, null, null, ["Catalog exceeds the 12 MiB envelope limit."]);
                }
                bytes = await File.ReadAllBytesAsync(path);
                resolvedSource = path;
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("payload", out var payloadProperty))
            {
                var envelope = JsonSerializer.Deserialize<SignedModCatalog>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Signed catalog deserialized to null.");
                var payload = Convert.FromBase64String(payloadProperty.GetString() ?? string.Empty);
                if (payload.Length is 0 or > ModCatalogSignatures.MaximumPayloadBytes)
                {
                    throw new InvalidDataException("Signed catalog payload size is invalid.");
                }
                var signedCatalog = JsonSerializer.Deserialize<ModCatalog>(payload, JsonOptions);
                return new CatalogLoadResult(
                    resolvedSource,
                    signedCatalog,
                    envelope,
                    null,
                    ModCatalogValidator.Validate(signedCatalog));
            }

            var catalog = JsonSerializer.Deserialize<ModCatalog>(bytes, JsonOptions);
            return new CatalogLoadResult(
                resolvedSource,
                catalog,
                null,
                null,
                ModCatalogValidator.Validate(catalog));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return new CatalogLoadResult(
                source,
                null,
                null,
                null,
                [$"Catalog envelope is invalid: {exception.Message}"]);
        }
    }

    private static async Task<CatalogLoadResult> LoadTrustedAsync(
        string source,
        GameInstallation installation)
    {
        var loaded = await LoadAsync(source);
        if (loaded.Errors.Count != 0) return loaded;
        if (loaded.Envelope is null)
        {
            return loaded with
            {
                Catalog = null,
                Errors = ["Catalog sync/install requires a signed catalog envelope."],
            };
        }

        var trustStore = await CatalogTrustManager.LoadAsync(installation);
        var verified = ModCatalogSignatures.Verify(loaded.Envelope, trustStore);
        return verified.Success
            ? loaded with
            {
                Catalog = verified.Catalog,
                KeyFingerprint = verified.KeyFingerprint,
                Errors = [],
            }
            : loaded with { Catalog = null, Errors = verified.Errors };
    }

    private static async Task<string> CacheTrustedAsync(
        SignedModCatalog envelope,
        GameInstallation installation)
    {
        var cacheDirectory = Path.Combine(installation.GameDirectory, "OFS", "cache");
        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, "catalog.signed.json");
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions);
                await stream.FlushAsync();
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task DownloadPackageAsync(ModCatalogEntry entry, string destination)
    {
        var uri = new Uri(entry.Package.Url, UriKind.Absolute);
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        EnsureFinalHttps(response, entry.Id);
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != entry.Package.Bytes)
        {
            throw new InvalidDataException(
                $"Package '{entry.Id}' Content-Length is {contentLength}; expected {entry.Package.Bytes}.");
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
            throw new InvalidDataException(
                $"Package '{entry.Id}' integrity mismatch: bytes={total}, sha256={actualHash}.");
        }
    }

    private static async Task<byte[]> DownloadBytesAsync(Uri uri, int maximumBytes)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        EnsureFinalHttps(response, "catalog");
        if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
        {
            throw new InvalidDataException("Catalog exceeds the 8 MiB limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Catalog exceeds the 8 MiB limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return output.ToArray();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        var loopbackPin = Environment.GetEnvironmentVariable(
            "OFS_MANAGER_LOOPBACK_TLS_CERT_SHA256");
        if (!string.IsNullOrWhiteSpace(loopbackPin))
        {
            if (loopbackPin.Length != 64 || loopbackPin.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidOperationException(
                    "OFS_MANAGER_LOOPBACK_TLS_CERT_SHA256 must contain 64 hexadecimal characters.");
            }
            var expected = Convert.FromHexString(loopbackPin);
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (request.RequestUri?.IsLoopback != true || certificate is null ||
                    errors is not (SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors))
                {
                    return false;
                }
                var actual = SHA256.HashData(certificate.RawData);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            };
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "OFS-Manager",
            ModManifestValidator.CurrentSdkVersion.ToString(3)));
        return client;
    }

    private static void EnsureFinalHttps(HttpResponseMessage response, string subject)
    {
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"HTTPS downgrade detected while downloading {subject}.");
        }
    }

    private static async Task EnsureCatalogTargetsInstallationAsync(
        ModCatalog catalog,
        GameInstallation installation)
    {
        var build = await BuildScanner.ScanAsync(installation);
        if (!string.Equals(catalog.GameBuild, build.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Catalog targets game build '{catalog.GameBuild}', but the installation fingerprint is " +
                $"'{build.Fingerprint}'.");
        }
    }

    private static ModVersion ParseCatalogVersion(string value)
    {
        _ = ModVersion.TryParse(value, out var version);
        return version;
    }

    private sealed record CatalogLoadResult(
        string Source,
        ModCatalog? Catalog,
        SignedModCatalog? Envelope,
        string? KeyFingerprint,
        IReadOnlyList<string> Errors);
}

internal sealed record CatalogValidationResult(
    string Source,
    bool Valid,
    ModCatalog? Catalog,
    int VersionEntryCount,
    bool Signed,
    string? KeyId,
    IReadOnlyList<string> Errors);

internal sealed record CatalogSyncResult(
    string Source,
    string CachePath,
    string GameBuild,
    string FrameworkVersion,
    int VersionEntryCount,
    DateTimeOffset GeneratedAtUtc,
    string KeyId,
    string PublicKeySha256);

internal sealed record CatalogInstallResult(
    string RequestedId,
    IReadOnlyList<ModInstallResult> Installed,
    string Detail);

internal sealed record CatalogThumbnailCacheResult(
    string Id,
    string Version,
    string CachePath,
    string Format,
    int Width,
    int Height,
    bool FromCache,
    string Sha256);
