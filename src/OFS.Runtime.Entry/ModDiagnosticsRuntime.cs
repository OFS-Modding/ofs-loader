using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class ModDiagnosticsRuntime
{
    private const int MaximumMessageLength = 4000;
    private const int MaximumPreviousBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly Dictionary<string, RuntimeModDiagnostic> Mods =
        new(StringComparer.OrdinalIgnoreCase);
    private static string? _reportPath;
    private static string? _previousPath;
    private static RuntimeDiagnosticReport? _report;

    internal static RuntimeDiagnosticReport? CurrentReport => _report;

    internal static void Begin(
        string gameDirectory,
        IModRuntimeInfo environment,
        int discoveredManifestCount)
    {
        try
        {
            var directory = Path.Combine(gameDirectory, "OFS", "diagnostics");
            Directory.CreateDirectory(directory);
            _reportPath = Path.Combine(directory, "last-session.json");
            _previousPath = Path.Combine(directory, "previous-session.json");
            RotatePrevious();
            Mods.Clear();
            _report = new RuntimeDiagnosticReport(
                RuntimeDiagnosticReport.CurrentSchemaVersion,
                Guid.NewGuid().ToString("N"),
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                null,
                RuntimeStartupState.Loading,
                new RuntimeEnvironmentSnapshot(
                    environment.FrameworkVersion.ToString(3),
                    environment.GameVersion,
                    environment.GameBuildFingerprint,
                    environment.UnityVersion,
                    environment.Il2CppMetadataVersion,
                    environment.ProcessArchitecture,
                    environment.PointerSize,
                    environment.IsVerifiedGameBuild),
                discoveredManifestCount,
                []);
            Persist();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            _reportPath = null;
            _previousPath = null;
            _report = null;
            RuntimeLog.Write($"Runtime diagnostics unavailable: {exception.Message}");
        }
    }

    internal static void Record(
        string manifestPath,
        ModManifest? manifest,
        ModStartupStatus status,
        string phase,
        string message,
        IReadOnlyList<string>? relatedModIds = null)
    {
        if (_report is null || _reportPath is null) return;
        try
        {
            var fullPath = Path.GetFullPath(manifestPath);
            Mods[fullPath] = new RuntimeModDiagnostic(
                manifest?.Id,
                manifest?.Name,
                manifest?.Version,
                fullPath,
                status,
                phase,
                Truncate(message),
                (relatedModIds ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Persist();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            RuntimeLog.Write($"Runtime diagnostic update failed: {exception.Message}");
        }
    }

    internal static void Complete()
    {
        if (_report is null || _reportPath is null) return;
        try
        {
            _report = _report with
            {
                State = RuntimeStartupState.Ready,
                StartupCompletedAtUtc = DateTimeOffset.UtcNow,
            };
            Persist();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Write($"Runtime diagnostic completion failed: {exception.Message}");
        }
    }

    private static void Persist()
    {
        var report = _report ?? throw new InvalidOperationException("Diagnostic report is not active.");
        var path = _reportPath ?? throw new InvalidOperationException("Diagnostic path is not active.");
        report = report with
        {
            Mods = Mods.Values
                .OrderBy(value => value.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ManifestPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        _report = report;
        WriteAtomic(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private static void RotatePrevious()
    {
        if (_reportPath is null || _previousPath is null || !File.Exists(_reportPath)) return;
        try
        {
            var info = new FileInfo(_reportPath);
            if (info.Length is <= 0 or > MaximumPreviousBytes) return;
            var content = File.ReadAllText(_reportPath, Encoding.UTF8);
            var previous = JsonSerializer.Deserialize<RuntimeDiagnosticReport>(content, JsonOptions);
            if (previous is null ||
                previous.SchemaVersion != RuntimeDiagnosticReport.CurrentSchemaVersion ||
                previous.SessionId.Length != 32 ||
                previous.SessionId.Any(value => !char.IsAsciiHexDigit(value)))
            {
                RuntimeLog.Write("Previous runtime diagnostic report was invalid and was not rotated.");
                return;
            }
            WriteAtomic(_previousPath, content);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Write(
                $"Previous runtime diagnostic report could not be rotated: {exception.Message}");
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumMessageLength ? value : value[..MaximumMessageLength];
}
