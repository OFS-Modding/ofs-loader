using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class ProfileManager
{
    private const int MaximumProfileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ProfileStatus> ListAsync(GameInstallation installation)
    {
        var installed = await DiscoverInstalledAsync(installation);
        var paths = GetPaths(installation);
        var activeDocument = await ReadAsync(paths.Active);
        var pendingDocument = await ReadAsync(paths.Pending);
        var active = activeDocument is null
            ? installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : activeDocument.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = pendingDocument?.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? active;
        var mods = installed.Values
            .OrderBy(manifest => manifest.Name, StringComparer.OrdinalIgnoreCase)
            .Select(manifest => new ProfileModStatus(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                active.Contains(manifest.Id),
                desired.Contains(manifest.Id)))
            .ToArray();
        return new ProfileStatus(
            paths.Active,
            paths.Pending,
            activeDocument is null,
            pendingDocument is not null,
            mods);
    }

    public static async Task<ProfileChangeResult> ChangeAsync(
        GameInstallation installation,
        string modId,
        bool enable)
    {
        var installed = await DiscoverInstalledAsync(installation);
        var paths = GetPaths(installation);
        var activeDocument = await ReadAsync(paths.Active);
        var pendingDocument = await ReadAsync(paths.Pending);
        var active = activeDocument is null
            ? installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : activeDocument.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = pendingDocument?.Enabled ?? active.ToArray();
        var resolution = enable
            ? ModProfileResolver.Enable(installed.Values, desired, modId)
            : ModProfileResolver.Disable(installed.Values, desired, modId);
        if (!resolution.Success)
        {
            throw new InvalidDataException(string.Join(" ", resolution.Errors));
        }

        var resolved = resolution.EnabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restartRequired = !resolved.SetEquals(active);
        if (restartRequired)
        {
            await WriteAsync(paths.Pending, resolution.EnabledIds);
        }
        else if (File.Exists(paths.Pending))
        {
            File.Delete(paths.Pending);
        }

        return new ProfileChangeResult(
            modId,
            enable,
            resolution.AffectedIds,
            restartRequired,
            restartRequired ? paths.Pending : null);
    }

    public static Task<ProfileDiscardResult> DiscardPendingAsync(GameInstallation installation)
    {
        var path = GetPaths(installation).Pending;
        var existed = File.Exists(path);
        if (existed) File.Delete(path);
        return Task.FromResult(new ProfileDiscardResult(path, existed));
    }

    public static async Task StageEnableResolvedAsync(
        GameInstallation installation,
        IEnumerable<string> ids)
    {
        var installed = await DiscoverInstalledAsync(installation);
        var paths = GetPaths(installation);
        var activeDocument = await ReadAsync(paths.Active);
        var pendingDocument = await ReadAsync(paths.Pending);
        if (activeDocument is null && pendingDocument is null)
        {
            return;
        }

        var active = activeDocument is null
            ? installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : activeDocument.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = pendingDocument?.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                      new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids) desired.Add(id);
        if (desired.SetEquals(active))
        {
            if (File.Exists(paths.Pending)) File.Delete(paths.Pending);
        }
        else
        {
            await WriteAsync(paths.Pending, desired);
        }
    }

    private static async Task<Dictionary<string, ModManifest>> DiscoverInstalledAsync(
        GameInstallation installation)
    {
        var root = Path.Combine(installation.GameDirectory, "OFS", "mods");
        Directory.CreateDirectory(root);
        var manifests = new List<ModManifest>();
        foreach (var path in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<ModManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException($"Manifest '{path}' deserialized to null.");
            var errors = ModManifestValidator.Validate(manifest);
            if (errors.Count != 0)
            {
                throw new InvalidDataException($"Manifest '{path}' is invalid: {string.Join(" ", errors)}");
            }
            manifests.Add(manifest);
        }
        var duplicates = manifests.GroupBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length != 0)
        {
            throw new InvalidDataException($"Duplicate installed mod ids: {string.Join(", ", duplicates)}.");
        }
        return manifests.ToDictionary(
            manifest => manifest.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<ProfileDocument?> ReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumProfileBytes)
        {
            throw new InvalidDataException($"Profile '{path}' is too large.");
        }
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<ProfileDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Profile '{path}' deserialized to null.");
        if (document.SchemaVersion != 1 || document.Enabled is null)
        {
            throw new InvalidDataException($"Profile '{path}' has an unsupported schema.");
        }
        if (document.Enabled.Count != document.Enabled.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidDataException($"Profile '{path}' contains duplicate ids.");
        }
        return document;
    }

    private static async Task WriteAsync(string path, IEnumerable<string> enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = new ProfileDocument(
            1,
            enabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
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

    private static ProfilePaths GetPaths(GameInstallation installation)
    {
        var root = Path.Combine(installation.GameDirectory, "OFS", "profiles");
        return new ProfilePaths(
            Path.Combine(root, "active.json"),
            Path.Combine(root, "pending.json"));
    }

    private sealed record ProfileDocument(int SchemaVersion, IReadOnlyList<string> Enabled);
    private sealed record ProfilePaths(string Active, string Pending);
}

internal sealed record ProfileModStatus(
    string Id,
    string Name,
    string Version,
    bool ActiveEnabled,
    bool DesiredEnabled);

internal sealed record ProfileStatus(
    string ActivePath,
    string PendingPath,
    bool ImplicitAllInstalled,
    bool RestartRequired,
    IReadOnlyList<ProfileModStatus> Mods);

internal sealed record ProfileChangeResult(
    string ModId,
    bool DesiredEnabled,
    IReadOnlyList<string> AffectedIds,
    bool RestartRequired,
    string? PendingPath);

internal sealed record ProfileDiscardResult(string PendingPath, bool Discarded);
