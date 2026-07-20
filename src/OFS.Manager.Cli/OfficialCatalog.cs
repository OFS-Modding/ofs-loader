using OFS.Loader;

namespace OFS.Manager;

internal static class OfficialCatalog
{
    public static async Task<OfficialCatalogSyncResult> SyncAsync(GameInstallation installation)
    {
        var trust = await CatalogTrustManager.AddTrustedKeyPemAsync(
            installation,
            OfficialCatalogIdentity.KeyId,
            OfficialCatalogIdentity.PublicKeyPem);
        var catalog = await CatalogManager.SyncAsync(OfficialCatalogIdentity.Source, installation);
        return new OfficialCatalogSyncResult(
            OfficialCatalogIdentity.Source,
            trust.PublicKeySha256,
            catalog);
    }
}

internal sealed record OfficialCatalogSyncResult(
    string Source,
    string PublicKeySha256,
    CatalogSyncResult Catalog);
