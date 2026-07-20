using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class CatalogTrustManager
{
    private const int MaximumSignedCatalogBytes = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<CatalogKeyGenerationResult> GenerateAsync(
        string keyId,
        string privateKeyPath,
        string publicKeyPath)
    {
        var privatePath = Path.GetFullPath(privateKeyPath);
        var publicPath = Path.GetFullPath(publicKeyPath);
        EnsureNewDestination(privatePath);
        EnsureNewDestination(publicPath);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustKey = ModCatalogSignatures.ExportTrustKey(keyId, key);
        await WriteNewTextAsync(privatePath, key.ExportPkcs8PrivateKeyPem());
        try
        {
            await WriteNewTextAsync(publicPath, key.ExportSubjectPublicKeyInfoPem());
        }
        catch
        {
            File.Delete(privatePath);
            throw;
        }
        return new CatalogKeyGenerationResult(
            keyId,
            privatePath,
            publicPath,
            trustKey.Sha256,
            "Keep the private key offline. Only the public key belongs in a trust store.");
    }

    public static async Task<CatalogSigningResult> SignAsync(
        string catalogPath,
        string privateKeyPath,
        string keyId,
        string? destinationPath)
    {
        var source = Path.GetFullPath(catalogPath);
        var privatePath = Path.GetFullPath(privateKeyPath);
        var destination = Path.GetFullPath(destinationPath ?? source + ".signed.json");
        if (!File.Exists(source)) throw new FileNotFoundException("Catalog does not exist.", source);
        if (!File.Exists(privatePath)) throw new FileNotFoundException("Private key does not exist.", privatePath);
        if (new FileInfo(source).Length > ModCatalogSignatures.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Catalog exceeds the 8 MiB signing limit.");
        }

        var catalogBytes = await File.ReadAllBytesAsync(source);
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(privatePath));
        var envelope = ModCatalogSignatures.Sign(catalogBytes, keyId, key);
        await WriteAtomicJsonAsync(destination, envelope);
        var publicKey = ModCatalogSignatures.ExportTrustKey(keyId, key);
        return new CatalogSigningResult(
            source,
            destination,
            keyId,
            publicKey.Sha256,
            catalogBytes.LongLength);
    }

    public static async Task<CatalogSignatureVerificationResult> VerifyAsync(
        string signedCatalogPath,
        string publicKeyPath,
        string? expectedKeyId = null)
    {
        var signedPath = Path.GetFullPath(signedCatalogPath);
        var publicPath = Path.GetFullPath(publicKeyPath);
        var envelope = await ReadEnvelopeAsync(signedPath);
        var keyId = expectedKeyId ?? envelope.KeyId;
        if (!string.Equals(keyId, envelope.KeyId, StringComparison.Ordinal))
        {
            return new CatalogSignatureVerificationResult(
                signedPath,
                false,
                envelope.KeyId,
                null,
                null,
                [$"Envelope keyId '{envelope.KeyId}' does not match expected '{keyId}'."]);
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(publicPath));
        var trustKey = ModCatalogSignatures.ExportTrustKey(keyId, key);
        var result = ModCatalogSignatures.Verify(
            envelope,
            new ModCatalogTrustStore { Keys = [trustKey] });
        return new CatalogSignatureVerificationResult(
            signedPath,
            result.Success,
            result.KeyId,
            result.KeyFingerprint,
            result.Catalog,
            result.Errors);
    }

    public static async Task<CatalogTrustChangeResult> AddTrustedKeyAsync(
        GameInstallation installation,
        string keyId,
        string publicKeyPath)
    {
        var publicPath = Path.GetFullPath(publicKeyPath);
        return await AddTrustedKeyPemAsync(
            installation,
            keyId,
            await File.ReadAllTextAsync(publicPath));
    }

    public static async Task<CatalogTrustChangeResult> AddTrustedKeyPemAsync(
        GameInstallation installation,
        string keyId,
        string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var added = ModCatalogSignatures.ExportTrustKey(keyId, key);
        var store = await LoadAsync(installation);
        var keys = store.Keys
            .Where(existing => !string.Equals(existing.Id, keyId, StringComparison.Ordinal))
            .Append(added)
            .OrderBy(existing => existing.Id, StringComparer.Ordinal)
            .ToArray();
        var changed = !store.Keys.Any(existing =>
            string.Equals(existing.Id, keyId, StringComparison.Ordinal) &&
            string.Equals(existing.Sha256, added.Sha256, StringComparison.OrdinalIgnoreCase));
        var updated = store with { Keys = keys };
        await SaveAsync(installation, updated);
        return new CatalogTrustChangeResult(
            keyId,
            added.Sha256,
            changed,
            GetTrustPath(installation),
            changed ? "Trusted public key added or replaced." : "Trusted public key already present.");
    }

    public static async Task<CatalogTrustListResult> ListAsync(GameInstallation installation)
    {
        var store = await LoadAsync(installation);
        return new CatalogTrustListResult(GetTrustPath(installation), store.Keys);
    }

    public static async Task<CatalogTrustChangeResult> RemoveTrustedKeyAsync(
        GameInstallation installation,
        string keyId)
    {
        var store = await LoadAsync(installation);
        var keys = store.Keys
            .Where(existing => !string.Equals(existing.Id, keyId, StringComparison.Ordinal))
            .ToArray();
        var removed = keys.Length != store.Keys.Count;
        if (removed)
        {
            await SaveAsync(installation, store with { Keys = keys });
        }
        return new CatalogTrustChangeResult(
            keyId,
            string.Empty,
            removed,
            GetTrustPath(installation),
            removed ? "Trusted public key removed." : "Trusted public key was not present.");
    }

    public static async Task<ModCatalogTrustStore> LoadAsync(GameInstallation installation)
    {
        var path = GetTrustPath(installation);
        if (!File.Exists(path)) return new ModCatalogTrustStore();
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw new InvalidDataException("Catalog trust store exceeds 1 MiB.");
        }
        var store = JsonSerializer.Deserialize<ModCatalogTrustStore>(
            await File.ReadAllBytesAsync(path),
            JsonOptions);
        var errors = ModCatalogSignatures.ValidateTrustStore(store);
        if (errors.Count != 0)
        {
            throw new InvalidDataException($"Catalog trust store is invalid: {string.Join(" ", errors)}");
        }
        return store!;
    }

    public static async Task<SignedModCatalog> ReadEnvelopeAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Signed catalog does not exist.", path);
        if (new FileInfo(path).Length > MaximumSignedCatalogBytes)
        {
            throw new InvalidDataException("Signed catalog exceeds the 12 MiB limit.");
        }
        return JsonSerializer.Deserialize<SignedModCatalog>(
                   await File.ReadAllBytesAsync(path),
                   JsonOptions)
               ?? throw new InvalidDataException("Signed catalog deserialized to null.");
    }

    private static async Task SaveAsync(
        GameInstallation installation,
        ModCatalogTrustStore store) =>
        await WriteAtomicJsonAsync(GetTrustPath(installation), store);

    private static string GetTrustPath(GameInstallation installation) =>
        Path.Combine(installation.GameDirectory, "OFS", "trust", "catalog-keys.json");

    private static async Task WriteAtomicJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                await stream.FlushAsync();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureNewDestination(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException($"Refusing to overwrite existing key file '{path}'.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private static async Task WriteNewTextAsync(string path, string value)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = Encoding.ASCII.GetBytes(value);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}

internal sealed record CatalogKeyGenerationResult(
    string KeyId,
    string PrivateKeyPath,
    string PublicKeyPath,
    string PublicKeySha256,
    string Detail);

internal sealed record CatalogSigningResult(
    string Source,
    string Destination,
    string KeyId,
    string PublicKeySha256,
    long PayloadBytes);

internal sealed record CatalogSignatureVerificationResult(
    string Source,
    bool Valid,
    string? KeyId,
    string? PublicKeySha256,
    ModCatalog? Catalog,
    IReadOnlyList<string> Errors);

internal sealed record CatalogTrustChangeResult(
    string KeyId,
    string PublicKeySha256,
    bool Changed,
    string TrustStorePath,
    string Detail);

internal sealed record CatalogTrustListResult(
    string TrustStorePath,
    IReadOnlyList<ModCatalogTrustKey> Keys);
