using System.Security.Cryptography;
using OFS.Sdk;

namespace OFS.Loader;

public static class OfficialCatalogIdentity
{
    public const string Source =
        "https://ofs-modding.github.io/ofs-mod-catalog/catalog.signed.json";
    public const string KeyId = "ofs.catalog.2026";
    public const string PublicKeySha256 =
        "f8ef1beb5b5a96764b84290ed300db695302b1c5cde122b4f7c6c56e3c7494f7";
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtn8JJQf4IXK2/0suO3WN2ZeX2Qig
        fOyfG4kMAcDzu9LQk4oSHFZOFVInU6tfCbrB9+tspMFHDudbIXNh29w9+Q==
        -----END PUBLIC KEY-----
        """;

    public static ModCatalogTrustKey CreateTrustKey()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        var trustKey = ModCatalogSignatures.ExportTrustKey(KeyId, key);
        if (!string.Equals(
                trustKey.Sha256,
                PublicKeySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Official catalog public key fingerprint mismatch.");
        }
        return trustKey;
    }
}
