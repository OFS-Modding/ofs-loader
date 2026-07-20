using System.Diagnostics;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class ModSafetyManager
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ModQuarantineStatus> ListAsync(GameInstallation installation)
    {
        var paths = GetPaths(installation);
        var quarantine = await ReadQuarantineAsync(paths.Quarantine);
        var journal = await ReadJournalAsync(paths.Journal);
        var hotCallback = await ReadHotCallbackAsync(paths.HotCallback);
        return new ModQuarantineStatus(
            paths.Quarantine,
            paths.Journal,
            paths.HotCallback,
            journal,
            hotCallback,
            quarantine.Entries);
    }

    public static async Task<ModQuarantineClearResult> ClearAsync(
        GameInstallation installation,
        string modId)
    {
        EnsureGameClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var paths = GetPaths(installation);
        var clearAll = string.Equals(modId, "--all", StringComparison.OrdinalIgnoreCase);
        ModQuarantineDocument quarantine;
        ModLoadJournal? journal;
        ModLoadJournal? hotCallback;
        if (clearAll)
        {
            quarantine = File.Exists(paths.Quarantine)
                ? await TryReadForCountAsync(paths.Quarantine)
                : new ModQuarantineDocument();
            journal = null;
            hotCallback = await TryReadHotCallbackForClearAllAsync(paths.HotCallback);
        }
        else
        {
            quarantine = await ReadQuarantineAsync(paths.Quarantine);
            journal = await ReadJournalAsync(paths.Journal);
            hotCallback = await ReadHotCallbackAsync(paths.HotCallback);
        }

        var removedIds = clearAll
            ? quarantine.Entries.Select(entry => entry.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : quarantine.Entries
                .Where(entry => string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.ModId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = clearAll
            ? []
            : quarantine.Entries
                .Where(entry => !string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        var journalRemoved = clearAll ||
            (journal is not null && string.Equals(journal.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (journalRemoved && journal is not null)
        {
            removedIds.Add(journal.ModId);
        }
        var hotCallbackRemoved = clearAll ||
            (hotCallback is not null &&
             string.Equals(hotCallback.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (hotCallbackRemoved && hotCallback is not null)
        {
            removedIds.Add(hotCallback.ModId);
        }

        await WriteDocumentAsync(
            paths.Quarantine,
            new ModQuarantineDocument { Entries = remaining });
        if (journalRemoved && File.Exists(paths.Journal))
        {
            File.Delete(paths.Journal);
        }
        if (hotCallbackRemoved && File.Exists(paths.HotCallback))
        {
            File.Delete(paths.HotCallback);
        }

        return new ModQuarantineClearResult(
            paths.Quarantine,
            removedIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            journalRemoved,
            hotCallbackRemoved,
            "Cleared. Restart the game to retry any enabled mod.");
    }

    private static async Task<ModQuarantineDocument> TryReadForCountAsync(string path)
    {
        try
        {
            return await ReadQuarantineAsync(path);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // --all is the explicit recovery path for an invalid store.
            return new ModQuarantineDocument();
        }
    }

    private static async Task<ModQuarantineDocument> ReadQuarantineAsync(string path)
    {
        if (!File.Exists(path)) return new ModQuarantineDocument();
        RequireSize(path);
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<ModQuarantineDocument>(stream, JsonOptions);
        var errors = ModSafetyDocuments.Validate(document);
        return errors.Count == 0
            ? document!
            : throw new InvalidDataException(string.Join(" ", errors));
    }

    private static async Task<ModLoadJournal?> ReadJournalAsync(string path)
    {
        if (!File.Exists(path)) return null;
        RequireSize(path);
        await using var stream = File.OpenRead(path);
        var journal = await JsonSerializer.DeserializeAsync<ModLoadJournal>(stream, JsonOptions);
        var errors = ModSafetyDocuments.Validate(journal);
        return errors.Count == 0
            ? journal
            : throw new InvalidDataException(string.Join(" ", errors));
    }

    private static async Task<ModLoadJournal?> ReadHotCallbackAsync(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > HotCrashBreadcrumbCodec.FileSize)
        {
            throw new InvalidDataException(
                $"Hot callback breadcrumb exceeds {HotCrashBreadcrumbCodec.FileSize} bytes.");
        }
        byte[] bytes;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes);
        }
        if (HotCrashBreadcrumbCodec.TryReadActive(bytes, out var journal, out var error))
        {
            return journal;
        }
        return error.Length == 0
            ? null
            : throw new InvalidDataException(error);
    }

    private static async Task<ModLoadJournal?> TryReadHotCallbackForClearAllAsync(string path)
    {
        try
        {
            return await ReadHotCallbackAsync(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task WriteDocumentAsync<T>(string path, T document)
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
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
                await stream.FlushAsync();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureGameClosed()
    {
        if (Process.GetProcessesByName("Ore Factory Squad").Length != 0)
        {
            throw new IOException("Close Ore Factory Squad before changing quarantine state.");
        }
    }

    private static void RequireSize(string path)
    {
        if (new FileInfo(path).Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Safety document '{path}' exceeds {MaximumDocumentBytes} bytes.");
        }
    }

    private static SafetyPaths GetPaths(GameInstallation installation)
    {
        var root = Path.Combine(installation.GameDirectory, "OFS", "safety");
        return new SafetyPaths(
            Path.Combine(root, "quarantine.json"),
            Path.Combine(root, "load-journal.json"),
            Path.Combine(root, HotCrashBreadcrumbCodec.FileName));
    }

    private sealed record SafetyPaths(string Quarantine, string Journal, string HotCallback);
}

internal sealed record ModQuarantineStatus(
    string QuarantinePath,
    string JournalPath,
    string HotCallbackPath,
    ModLoadJournal? PendingLoadJournal,
    ModLoadJournal? PendingHotCallback,
    IReadOnlyList<ModQuarantineEntry> Entries);

internal sealed record ModQuarantineClearResult(
    string QuarantinePath,
    IReadOnlyList<string> RemovedModIds,
    bool PendingJournalRemoved,
    bool PendingHotCallbackRemoved,
    string Detail);
