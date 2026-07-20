using OFS.Loader;

namespace OFS.Manager;

internal sealed record ArtifactFingerprint(string RelativePath, long Size, string Sha256);

internal sealed record GameBuildFingerprint(
    string SchemaVersion,
    string AppId,
    string ProductName,
    string BuildId,
    string BuildGuid,
    string GameDirectory,
    string Fingerprint,
    IReadOnlyList<ArtifactFingerprint> Artifacts);

internal static class BuildScanner
{
    public static async Task<GameBuildFingerprint> ScanAsync(GameInstallation installation)
    {
        var scan = await GameBuildFingerprintService.ScanAsync(
            installation.GameDirectory,
            installation.BuildId);

        return new GameBuildFingerprint(
            GameBuildFingerprintService.SchemaVersion,
            installation.AppId,
            installation.Name,
            scan.BuildId,
            scan.BuildGuid,
            installation.GameDirectory,
            scan.Fingerprint,
            scan.Artifacts.Select(value => new ArtifactFingerprint(
                value.RelativePath,
                value.Size,
                value.Sha256)).ToArray());
    }
}
