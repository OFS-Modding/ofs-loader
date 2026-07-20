using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed record InstalledModState(
    ModManifest Manifest,
    string Directory,
    bool ActiveEnabled,
    bool DesiredEnabled,
    bool Loaded,
    bool Quarantined,
    bool RetryOnRestart);

internal sealed record ModProfileChange(
    string ModId,
    bool DesiredEnabled,
    IReadOnlyList<string> AffectedIds,
    bool RestartRequired);

internal static class ModProfileStore
{
    private const int MaximumProfileBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string? _gameDirectory;
    private static string? _activePath;
    private static string? _pendingPath;
    private static HashSet<string>? _activeEnabled;
    private static HashSet<string>? _pendingEnabled;

    public static void Initialize(string gameDirectory)
    {
        lock (Gate)
        {
            _gameDirectory = gameDirectory;
            var profileRoot = Path.Combine(gameDirectory, "OFS", "profiles");
            Directory.CreateDirectory(profileRoot);
            _activePath = Path.Combine(profileRoot, "active.json");
            _pendingPath = Path.Combine(profileRoot, "pending.json");
            ApplyPendingProfile();
            try
            {
                _activeEnabled = ReadProfile(_activePath);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                _activeEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                RuntimeLog.Write(
                    $"Active mod profile rejected; entering zero-mod safe mode: {exception.Message}");
            }
            _pendingEnabled = null;
            RuntimeLog.Write(_activeEnabled is null
                ? "Mod profile: implicit all-installed mode."
                : $"Mod profile loaded: enabled={_activeEnabled.Count}.");
        }
    }

    public static bool IsEnabled(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            return _activeEnabled is null || _activeEnabled.Contains(modId);
        }
    }

    public static IReadOnlyList<InstalledModState> GetInstalledMods(
        IReadOnlyCollection<ModInfo> loadedMods)
    {
        lock (Gate)
        {
            var installed = DiscoverInstalled();
            var loaded = loadedMods.Select(mod => mod.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var active = EffectiveActive(installed);
            var desired = _pendingEnabled ?? active;
            return installed.Values
                .OrderBy(mod => mod.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                .Select(mod => new InstalledModState(
                    mod.Manifest,
                    mod.Directory,
                    active.Contains(mod.Manifest.Id),
                    desired.Contains(mod.Manifest.Id),
                    loaded.Contains(mod.Manifest.Id),
                    ModSafetyStore.IsQuarantined(mod.Manifest.Id),
                    ModSafetyStore.WasClearedThisSession(mod.Manifest.Id)))
                .ToArray();
        }
    }

    public static ModProfileChange Toggle(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            var installed = DiscoverInstalled();
            if (!installed.ContainsKey(modId))
            {
                throw new InvalidOperationException($"Installed mod '{modId}' was not found.");
            }

            var active = EffectiveActive(installed);
            var desired = new HashSet<string>(
                _pendingEnabled ?? active,
                StringComparer.OrdinalIgnoreCase);
            var enable = !desired.Contains(modId);
            var resolution = enable
                ? ModProfileResolver.Enable(
                    installed.Values.Select(mod => mod.Manifest), desired, modId)
                : ModProfileResolver.Disable(
                    installed.Values.Select(mod => mod.Manifest), desired, modId);
            if (!resolution.Success)
            {
                throw new InvalidOperationException(string.Join(" ", resolution.Errors));
            }
            desired = resolution.EnabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (desired.SetEquals(active))
            {
                _pendingEnabled = null;
                if (File.Exists(_pendingPath!)) File.Delete(_pendingPath!);
            }
            else
            {
                WriteProfile(_pendingPath!, desired);
                _pendingEnabled = desired;
            }

            return new ModProfileChange(
                modId,
                enable,
                resolution.AffectedIds,
                !desired.SetEquals(active));
        }
    }

    public static void StageEnableWithDependencies(string modId)
    {
        lock (Gate)
        {
            EnsureInitialized();
            var installed = DiscoverInstalled();
            if (!installed.ContainsKey(modId)) return;
            var active = EffectiveActive(installed);
            var desired = new HashSet<string>(
                _pendingEnabled ?? active,
                StringComparer.OrdinalIgnoreCase);
            var resolution = ModProfileResolver.Enable(
                installed.Values.Select(mod => mod.Manifest),
                desired,
                modId);
            if (!resolution.Success)
            {
                throw new InvalidOperationException(string.Join(" ", resolution.Errors));
            }
            desired = resolution.EnabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            WriteProfile(_pendingPath!, desired);
            _pendingEnabled = desired;
        }
    }

    public static void StageEnableResolved(IEnumerable<string> modIds)
        => StageResolvedChanges(modIds, []);

    public static void StageResolvedChanges(
        IEnumerable<string> enableIds,
        IEnumerable<string> disableIds)
    {
        ArgumentNullException.ThrowIfNull(enableIds);
        ArgumentNullException.ThrowIfNull(disableIds);
        lock (Gate)
        {
            EnsureInitialized();
            var installed = DiscoverInstalled();
            var active = EffectiveActive(installed);
            var desired = new HashSet<string>(
                _pendingEnabled ?? active,
                StringComparer.OrdinalIgnoreCase);
            var enable = ValidateResolvedIds(enableIds, nameof(enableIds));
            var disable = ValidateResolvedIds(disableIds, nameof(disableIds));
            if (enable.Overlaps(disable))
            {
                throw new InvalidDataException(
                    "Resolved profile cannot enable and disable the same mod id.");
            }
            desired.ExceptWith(disable);
            desired.UnionWith(enable);
            if (desired.SetEquals(active))
            {
                _pendingEnabled = null;
                if (File.Exists(_pendingPath!)) File.Delete(_pendingPath!);
            }
            else
            {
                WriteProfile(_pendingPath!, desired);
                _pendingEnabled = desired;
            }
        }
    }

    private static HashSet<string> ValidateResolvedIds(
        IEnumerable<string> ids,
        string parameterName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!ModManifestValidator.IsValidId(id) || !result.Add(id))
            {
                throw new InvalidDataException(
                    $"Resolved profile {parameterName} contains invalid/duplicate id '{id}'.");
            }
        }
        return result;
    }

    private static void ApplyPendingProfile()
    {
        if (!File.Exists(_pendingPath!)) return;
        try
        {
            _ = ReadProfile(_pendingPath!)
                ?? throw new InvalidDataException("pending profile cannot use implicit mode.");
            File.Move(_pendingPath!, _activePath!, overwrite: true);
            RuntimeLog.Write("Activated pending mod profile before assembly discovery.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            RuntimeLog.Write($"Pending mod profile rejected and left unapplied: {exception.Message}");
        }
    }

    private static Dictionary<string, DiscoveredManifest> DiscoverInstalled()
    {
        EnsureInitialized();
        var modsRoot = Path.Combine(_gameDirectory!, "OFS", "mods");
        Directory.CreateDirectory(modsRoot);
        var candidates = new List<DiscoveredManifest>();
        foreach (var path in Directory.EnumerateFiles(modsRoot, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Manifest deserialized to null.");
                var errors = ModManifestValidator.Validate(manifest);
                if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
                candidates.Add(new DiscoveredManifest(Path.GetDirectoryName(path)!, manifest));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                RuntimeLog.Write($"Installed profile discovery rejected '{path}': {exception.Message}");
            }
        }

        return candidates
            .GroupBy(mod => mod.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> EffectiveActive(
        IReadOnlyDictionary<string, DiscoveredManifest> installed) =>
        _activeEnabled is null
            ? installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_activeEnabled, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? ReadProfile(string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > MaximumProfileBytes) throw new InvalidDataException("Mod profile is too large.");
        var profile = JsonSerializer.Deserialize<ModProfileDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Mod profile deserialized to null.");
        if (profile.SchemaVersion != 1) throw new InvalidDataException("Unsupported mod profile schema.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile.Enabled is null)
        {
            throw new InvalidDataException("Mod profile enabled must be an array.");
        }
        foreach (var id in profile.Enabled)
        {
            if (!IsValidModId(id) || !result.Add(id))
            {
                throw new InvalidDataException($"Invalid or duplicate enabled mod id '{id}'.");
            }
        }
        return result;
    }

    private static void WriteProfile(string path, IEnumerable<string> enabled)
    {
        var document = new ModProfileDocument(
            1,
            enabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
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

    private static void EnsureInitialized()
    {
        if (_gameDirectory is null || _activePath is null || _pendingPath is null)
        {
            throw new InvalidOperationException("Mod profile store is not initialized.");
        }
    }

    private static bool IsValidModId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 3 and <= 80 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private sealed record DiscoveredManifest(string Directory, ModManifest Manifest);
    private sealed record ModProfileDocument(int SchemaVersion, IReadOnlyList<string> Enabled);
}
