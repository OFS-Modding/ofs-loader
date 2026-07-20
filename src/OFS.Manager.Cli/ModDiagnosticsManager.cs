using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OFS.Sdk;

namespace OFS.Manager;

internal static class ModDiagnosticsManager
{
    private const int MaximumReportBytes = 4 * 1024 * 1024;
    private const int MaximumMods = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<ModDiagnosticReadResult> ReadAsync(GameInstallation installation)
    {
        var directory = Path.Combine(installation.GameDirectory, "OFS", "diagnostics");
        var reportPath = Path.Combine(directory, "last-session.json");
        var previousPath = Path.Combine(directory, "previous-session.json");
        if (!File.Exists(reportPath))
        {
            return new ModDiagnosticReadResult(
                false,
                reportPath,
                previousPath,
                false,
                new ModDiagnosticCounts(0, 0, 0, 0, 0, 0),
                null,
                "No runtime diagnostic report exists. Start the game once with OFS installed.");
        }

        var info = new FileInfo(reportPath);
        if (info.Length is <= 0 or > MaximumReportBytes)
            throw new InvalidDataException(
                $"Runtime diagnostic report must be 1-{MaximumReportBytes} bytes.");
        RuntimeDiagnosticReport report;
        await using (var stream = new FileStream(
                         reportPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            report = await JsonSerializer.DeserializeAsync<RuntimeDiagnosticReport>(stream, JsonOptions)
                ?? throw new InvalidDataException("Runtime diagnostic report deserialized to null.");
        }
        Validate(report);

        var counts = new ModDiagnosticCounts(
            Count(report, ModStartupStatus.Loaded),
            Count(report, ModStartupStatus.Disabled),
            Count(report, ModStartupStatus.Quarantined),
            Count(report, ModStartupStatus.Rejected),
            Count(report, ModStartupStatus.Blocked),
            Count(report, ModStartupStatus.Failed));
        var current = Process.GetProcessesByName("Ore Factory Squad")
            .Any(process => process.Id == report.ProcessId);
        var detail = report.State == RuntimeStartupState.Loading
            ? "Startup did not reach the ready commit; inspect the last recorded phase and crash safety state."
            : counts.Problems == 0
                ? "Startup completed with no rejected, blocked, quarantined or failed mods."
                : $"Startup completed with {counts.Problems} mod problem(s).";
        return new ModDiagnosticReadResult(
            true,
            reportPath,
            previousPath,
            current,
            counts,
            report,
            detail);
    }

    private static int Count(RuntimeDiagnosticReport report, ModStartupStatus status) =>
        report.Mods.Count(value => value.Status == status);

    private static void Validate(RuntimeDiagnosticReport report)
    {
        var errors = new List<string>();
        if (report.SchemaVersion != RuntimeDiagnosticReport.CurrentSchemaVersion)
            errors.Add($"Unsupported schemaVersion {report.SchemaVersion}.");
        if (report.SessionId.Length != 32 || report.SessionId.Any(value => !char.IsAsciiHexDigit(value)))
            errors.Add("sessionId must be a 32-character hexadecimal value.");
        if (report.ProcessId <= 0) errors.Add("processId must be positive.");
        if (report.StartedAtUtc == default) errors.Add("startedAtUtc is required.");
        if (report.State == RuntimeStartupState.Ready && report.StartupCompletedAtUtc is null)
            errors.Add("A ready report requires startupCompletedAtUtc.");
        if (report.StartupCompletedAtUtc < report.StartedAtUtc)
            errors.Add("startupCompletedAtUtc cannot precede startedAtUtc.");
        if (report.DiscoveredManifestCount < 0)
            errors.Add("discoveredManifestCount cannot be negative.");
        if (report.Mods is null || report.Mods.Count > MaximumMods)
            errors.Add($"mods must contain at most {MaximumMods} entries.");
        else
        {
            if (report.State == RuntimeStartupState.Ready &&
                report.Mods.Count != report.DiscoveredManifestCount)
                errors.Add("A ready report must classify every discovered manifest.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in report.Mods)
            {
                if (!Enum.IsDefined(mod.Status)) errors.Add("A mod has an invalid status.");
                if (string.IsNullOrWhiteSpace(mod.ManifestPath) ||
                    !Path.IsPathFullyQualified(mod.ManifestPath) ||
                    !paths.Add(mod.ManifestPath))
                    errors.Add("Every mod requires a unique absolute manifestPath.");
                if (string.IsNullOrWhiteSpace(mod.Phase) || mod.Phase.Length > 100)
                    errors.Add("Every mod requires a phase of at most 100 characters.");
                if (mod.Message is null || mod.Message.Length > 4000)
                    errors.Add("A mod diagnostic message exceeds 4000 characters.");
                if (mod.Status != ModStartupStatus.Rejected && string.IsNullOrWhiteSpace(mod.Id))
                    errors.Add("Non-rejected mod diagnostics require an id.");
                if (mod.RelatedModIds is null || mod.RelatedModIds.Count > 256)
                    errors.Add("relatedModIds must contain at most 256 entries.");
            }
        }

        var environment = report.Environment;
        if (environment is null ||
            !Version.TryParse(environment.FrameworkVersion, out _) ||
            string.IsNullOrWhiteSpace(environment.GameVersion) ||
            string.IsNullOrWhiteSpace(environment.UnityVersion) ||
            environment.GameBuildFingerprint.Length != 64 ||
            environment.GameBuildFingerprint.Any(value => !char.IsAsciiHexDigit(value)) ||
            environment.Il2CppMetadataVersion <= 0 ||
            string.IsNullOrWhiteSpace(environment.ProcessArchitecture) ||
            environment.PointerSize is not (4 or 8))
        {
            errors.Add("environment contains invalid build or ABI facts.");
        }
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors.Distinct()));
    }
}

internal sealed record ModDiagnosticCounts(
    int Loaded,
    int Disabled,
    int Quarantined,
    int Rejected,
    int Blocked,
    int Failed)
{
    public int Problems => Quarantined + Rejected + Blocked + Failed;
}

internal sealed record ModDiagnosticReadResult(
    bool Available,
    string ReportPath,
    string PreviousReportPath,
    bool IsCurrentProcess,
    ModDiagnosticCounts Counts,
    RuntimeDiagnosticReport? Report,
    string Detail);
