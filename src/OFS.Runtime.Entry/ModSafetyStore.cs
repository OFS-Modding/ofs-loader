using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class ModSafetyStore
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string? _journalPath;
    private static string? _quarantinePath;
    private static ModQuarantineDocument _quarantine = new();
    private static ModLoadJournal? _currentJournal;
    private static bool _blockAll;
    private static bool _runtimeTrackingDisabled;
    private static int _runtimeCallbackThreadId;
    private static readonly Stack<ModLoadJournal> RuntimeCallbackStack = new();
    private static readonly Dictionary<string, RuntimeModSource> RuntimeModSources =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ClearedThisSession =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HotRuntimeCallbackBreadcrumb HotBreadcrumb = new();

    public static void Initialize(string gameDirectory)
    {
        lock (Gate)
        {
            var safetyRoot = Path.Combine(gameDirectory, "OFS", "safety");
            Directory.CreateDirectory(safetyRoot);
            _journalPath = Path.Combine(safetyRoot, "load-journal.json");
            _quarantinePath = Path.Combine(safetyRoot, "quarantine.json");
            _currentJournal = null;
            _blockAll = false;
            _runtimeTrackingDisabled = false;
            _runtimeCallbackThreadId = 0;
            RuntimeCallbackStack.Clear();
            RuntimeModSources.Clear();
            ClearedThisSession.Clear();

            try
            {
                _quarantine = ReadQuarantine(_quarantinePath);
                var recoveredDurable = RecoverInterruptedAttempt();
                var interruptedHot = HotBreadcrumb.Initialize(safetyRoot);
                if (interruptedHot is not null)
                {
                    if (recoveredDurable)
                    {
                        RuntimeLog.Write(
                            $"Ignored hot callback breadcrumb for '{interruptedHot.ModId}' " +
                            "because a more precise durable callback journal survived.");
                    }
                    else
                    {
                        _quarantine = ModSafetyDocuments.Recover(
                            interruptedHot,
                            _quarantine,
                            DateTimeOffset.UtcNow);
                        WriteQuarantine(_quarantine);
                        var entry = _quarantine.Entries.Single(value =>
                            string.Equals(
                                value.ModId,
                                interruptedHot.ModId,
                                StringComparison.OrdinalIgnoreCase));
                        RuntimeLog.Write(
                            $"Quarantined mod '{interruptedHot.ModId}' after interrupted hot phase " +
                            $"'{interruptedHot.Phase}' (occurrences={entry.Occurrences}, " +
                            $"previousPid={interruptedHot.ProcessId}).");
                    }
                }
                RuntimeLog.Write(
                    $"Mod safety store ready: quarantined={_quarantine.Entries.Count}, " +
                    $"path='{_quarantinePath}'.");
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                _quarantine = new ModQuarantineDocument();
                _blockAll = true;
                HotBreadcrumb.Disable();
                RuntimeLog.Write(
                    "Mod safety state is invalid or inaccessible; blocking all third-party mods: " +
                    exception.Message);
            }
        }
    }

    public static bool IsQuarantined(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            return _blockAll || _quarantine.Entries.Any(entry =>
                string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static bool WasClearedThisSession(string modId)
    {
        lock (Gate)
        {
            return ClearedThisSession.Contains(modId);
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            HotBreadcrumb.Disable();
            _currentJournal = null;
            _quarantine = new ModQuarantineDocument();
            _blockAll = false;
            _runtimeTrackingDisabled = false;
            _runtimeCallbackThreadId = 0;
            RuntimeCallbackStack.Clear();
            RuntimeModSources.Clear();
            ClearedThisSession.Clear();
        }
    }

    public static ModQuarantineEntry? GetEntry(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            return _quarantine.Entries.FirstOrDefault(entry =>
                string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string BeginAttempt(
        ModManifest manifest,
        string manifestPath,
        string assemblyPath)
    {
        lock (Gate)
        {
            EnsureInitialized();
            if (_currentJournal is not null)
            {
                throw new InvalidOperationException(
                    $"Load journal already tracks mod '{_currentJournal.ModId}'.");
            }
            var journal = new ModLoadJournal
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ModId = manifest.Id,
                Version = manifest.Version,
                Phase = "assembly-load",
                ManifestPath = Path.GetFullPath(manifestPath),
                AssemblyPath = Path.GetFullPath(assemblyPath),
                StartedAtUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
            };
            WriteJournal(journal);
            _currentJournal = journal;
            return journal.SessionId;
        }
    }

    public static void UpdateAttempt(string sessionId, string phase)
    {
        lock (Gate)
        {
            EnsureInitialized();
            if (_currentJournal is null ||
                !string.Equals(_currentJournal.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Load journal session does not match the active attempt.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(phase);
            var updated = _currentJournal with { Phase = phase };
            WriteJournal(updated);
            _currentJournal = updated;
        }
    }

    public static void CompleteAttempt(string sessionId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            if (_currentJournal is null)
            {
                return;
            }
            if (!string.Equals(_currentJournal.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cannot complete a different load journal session.");
            }

            // A durable completed marker makes a transient delete failure harmless.
            WriteJournal(_currentJournal with { Phase = "completed" });
            _currentJournal = null;
            TryDeleteJournal();
        }
    }

    public static void RegisterRuntimeMod(
        ModManifest manifest,
        string manifestPath,
        string assemblyPath)
    {
        lock (Gate)
        {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(manifest);
            var errors = ModManifestValidator.Validate(manifest);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
            RuntimeModSources[manifest.Id] = new RuntimeModSource(
                manifest.Version,
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(assemblyPath));
        }
    }

    public static IDisposable EnterRuntimeCallback(string modId, string phase)
    {
        lock (Gate)
        {
            EnsureInitialized();
            if (_blockAll || _runtimeTrackingDisabled ||
                !RuntimeModSources.TryGetValue(modId, out var source))
                return RuntimeCallbackLease.Empty;

            var threadId = Environment.CurrentManagedThreadId;
            if (_currentJournal is not null)
            {
                if (!_currentJournal.Phase.StartsWith("callback:", StringComparison.Ordinal) ||
                    _runtimeCallbackThreadId != threadId)
                    return RuntimeCallbackLease.Empty;
            }

            var normalizedPhase = NormalizeCallbackPhase(phase);
            var journal = new ModLoadJournal
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ModId = modId,
                Version = source.Version,
                Phase = $"callback:{normalizedPhase}",
                ManifestPath = source.ManifestPath,
                AssemblyPath = source.AssemblyPath,
                StartedAtUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
            };
            try
            {
                WriteJournal(journal);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _runtimeTrackingDisabled = true;
                RuntimeLog.Write(
                    "Runtime callback crash attribution disabled for this session: " +
                    exception.Message);
                return RuntimeCallbackLease.Empty;
            }

            if (_currentJournal is not null) RuntimeCallbackStack.Push(_currentJournal);
            else _runtimeCallbackThreadId = threadId;
            _currentJournal = journal;
            return new RuntimeCallbackLease(journal.SessionId);
        }
    }

    public static HotRuntimeCallbackLease EnterHotRuntimeCallback(
        string modId,
        string phase)
    {
        RuntimeModSource? source;
        lock (Gate)
        {
            EnsureInitialized();
            if (_blockAll || !RuntimeModSources.TryGetValue(modId, out source))
            {
                return default;
            }
        }
        return HotBreadcrumb.Enter(
            modId,
            source.Version,
            source.ManifestPath,
            source.AssemblyPath,
            phase);
    }

    public static bool Clear(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            if (_blockAll)
            {
                throw new InvalidOperationException(
                    "Safety state is corrupt; use the external CLI to clear all quarantine state.");
            }
            var remaining = _quarantine.Entries
                .Where(entry => !string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (remaining.Length == _quarantine.Entries.Count)
            {
                return false;
            }
            _quarantine = new ModQuarantineDocument { Entries = remaining };
            WriteQuarantine(_quarantine);
            ClearedThisSession.Add(modId);
            return true;
        }
    }

    private static bool RecoverInterruptedAttempt()
    {
        if (!File.Exists(_journalPath!))
        {
            return false;
        }
        var journal = ReadJournal(_journalPath!);
        if (journal.Phase is "completed" or "recovered")
        {
            TryDeleteJournal();
            return false;
        }

        _quarantine = ModSafetyDocuments.Recover(
            journal,
            _quarantine,
            DateTimeOffset.UtcNow);
        WriteQuarantine(_quarantine);
        WriteJournal(journal with { Phase = "recovered" });
        TryDeleteJournal();
        var entry = _quarantine.Entries.Single(value =>
            string.Equals(value.ModId, journal.ModId, StringComparison.OrdinalIgnoreCase));
        RuntimeLog.Write(
            $"Quarantined mod '{journal.ModId}' after interrupted phase '{journal.Phase}' " +
            $"(occurrences={entry.Occurrences}, previousPid={journal.ProcessId}).");
        return true;
    }

    private static ModLoadJournal ReadJournal(string path)
    {
        RequireDocumentSize(path);
        var journal = JsonSerializer.Deserialize<ModLoadJournal>(File.ReadAllText(path), JsonOptions);
        var errors = ModSafetyDocuments.Validate(journal);
        return errors.Count == 0
            ? journal!
            : throw new InvalidDataException(string.Join(" ", errors));
    }

    private static ModQuarantineDocument ReadQuarantine(string path)
    {
        if (!File.Exists(path))
        {
            return new ModQuarantineDocument();
        }
        RequireDocumentSize(path);
        var document = JsonSerializer.Deserialize<ModQuarantineDocument>(
            File.ReadAllText(path),
            JsonOptions);
        var errors = ModSafetyDocuments.Validate(document);
        return errors.Count == 0
            ? document!
            : throw new InvalidDataException(string.Join(" ", errors));
    }

    private static void WriteJournal(ModLoadJournal journal) =>
        WriteDocument(_journalPath!, journal);

    private static void WriteQuarantine(ModQuarantineDocument document) =>
        WriteDocument(_quarantinePath!, document);

    private static void WriteDocument<T>(string path, T document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void TryDeleteJournal()
    {
        try
        {
            if (File.Exists(_journalPath!)) File.Delete(_journalPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Write(
                $"Load journal cleanup deferred; durable terminal phase prevents false attribution: " +
                exception.Message);
        }
    }

    private static void ExitRuntimeCallback(string sessionId)
    {
        lock (Gate)
        {
            if (_currentJournal is null ||
                !string.Equals(_currentJournal.SessionId, sessionId, StringComparison.Ordinal))
                return;

            var completed = _currentJournal;
            try
            {
                if (RuntimeCallbackStack.Count != 0)
                {
                    var previous = RuntimeCallbackStack.Pop();
                    WriteJournal(previous);
                    _currentJournal = previous;
                    return;
                }

                WriteJournal(completed with { Phase = "completed" });
                _currentJournal = null;
                _runtimeCallbackThreadId = 0;
                TryDeleteJournal();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _currentJournal = null;
                _runtimeCallbackThreadId = 0;
                RuntimeCallbackStack.Clear();
                _runtimeTrackingDisabled = true;
                TryDeleteJournal();
                RuntimeLog.Write(
                    "Runtime callback journal cleanup failed; tracking disabled to avoid false attribution: " +
                    exception.Message);
            }
        }
    }

    private static string NormalizeCallbackPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var normalized = new string(phase
            .Select(character => char.IsControl(character) ? '_' : character)
            .Take(91)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("Runtime callback phase cannot be empty.", nameof(phase))
            : normalized;
    }

    private static void RequireDocumentSize(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Safety document '{path}' exceeds {MaximumDocumentBytes} bytes.");
        }
    }

    private static void EnsureInitialized()
    {
        if (_journalPath is null || _quarantinePath is null)
        {
            throw new InvalidOperationException("Mod safety store is not initialized.");
        }
    }

    private sealed record RuntimeModSource(
        string Version,
        string ManifestPath,
        string AssemblyPath);

    private sealed class RuntimeCallbackLease : IDisposable
    {
        internal static readonly RuntimeCallbackLease Empty = new(null);
        private string? _sessionId;

        internal RuntimeCallbackLease(string? sessionId) => _sessionId = sessionId;

        public void Dispose()
        {
            var sessionId = Interlocked.Exchange(ref _sessionId, null);
            if (sessionId is not null) ExitRuntimeCallback(sessionId);
        }
    }
}
