using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModSaveData : IModSaveData
{
    private const string MetadataFileName = ".ofs-schema.json";

    private readonly object _gate = new();
    private readonly string _ownerId;
    private readonly string _savesRoot;
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private SaveMigrationPlanDefinition? _migrationPlan;
    private string? _transactionDirectory;

    public ModSaveData(string ownerId, string? savesRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _ownerId = ownerId;
        _savesRoot = savesRoot is null ? GetDefaultSavesRoot() : Path.GetFullPath(savesRoot);
    }

    public int? CurrentSlot { get; private set; }

    public string? CurrentDirectory => CurrentSlot is int slot
        ? _transactionDirectory ?? GetOwnerDirectory(slot)
        : null;

    public int? SchemaVersion { get; private set; }

    public SaveMigrationResult LastMigration { get; private set; } =
        new(SaveMigrationStatus.NotConfigured);

    public void SetCurrentSlot(int slot)
    {
        if (slot is < 0 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Save slot must be between 0 and 9999.");
        }

        lock (_gate)
        {
            CurrentSlot = slot;
            SchemaVersion = null;
            LastMigration = new SaveMigrationResult(
                _migrationPlan is null
                    ? SaveMigrationStatus.NotConfigured
                    : SaveMigrationStatus.Pending);
        }
    }

    public void RegisterMigrationPlan(SaveMigrationPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = ValidatePlan(definition);

        lock (_gate)
        {
            if (_migrationPlan is not null)
            {
                throw new InvalidOperationException(
                    "A save migration plan is already registered for this mod.");
            }

            _migrationPlan = normalized;
            LastMigration = new SaveMigrationResult(SaveMigrationStatus.Pending);
        }
    }

    public bool Exists(string relativePath)
    {
        var path = Resolve(relativePath);
        lock (_gate)
        {
            return File.Exists(path);
        }
    }

    public T Load<T>(string relativePath, Func<T> createDefault)
    {
        ArgumentNullException.ThrowIfNull(createDefault);
        var path = Resolve(relativePath);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return createDefault();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, _options)
                ?? throw new InvalidDataException(
                    $"Save sidecar '{relativePath}' deserialized to null.");
        }
    }

    public void Save<T>(string relativePath, T value)
    {
        var path = Resolve(relativePath);
        lock (_gate)
        {
            WriteJsonAtomic(path, value);
        }
    }

    internal SaveMigrationResult ApplyRegisteredMigrations()
    {
        lock (_gate)
        {
            if (_migrationPlan is null)
            {
                SchemaVersion = null;
                return LastMigration = new SaveMigrationResult(
                    SaveMigrationStatus.NotConfigured);
            }

            if (CurrentSlot is not int slot)
            {
                return LastMigration = Failed(
                    null,
                    _migrationPlan.CurrentVersion,
                    "No save slot is active.");
            }

            var liveDirectory = GetOwnerDirectory(slot);
            try
            {
                var storedVersion = ReadStoredVersion(liveDirectory);
                var hasUserData = HasUserData(liveDirectory);
                var fromVersion = storedVersion ??
                    (hasUserData
                        ? _migrationPlan.LegacyVersion
                        : _migrationPlan.CurrentVersion);

                SchemaVersion = fromVersion;
                if (fromVersion > _migrationPlan.CurrentVersion)
                {
                    return LastMigration = Failed(
                        fromVersion,
                        _migrationPlan.CurrentVersion,
                        $"Save schema {fromVersion} is newer than supported schema " +
                        $"{_migrationPlan.CurrentVersion}; downgrade was refused.");
                }

                if (storedVersion == _migrationPlan.CurrentVersion)
                {
                    return LastMigration = new SaveMigrationResult(
                        SaveMigrationStatus.UpToDate,
                        fromVersion,
                        _migrationPlan.CurrentVersion);
                }

                var targetStatus = fromVersion == _migrationPlan.CurrentVersion
                    ? SaveMigrationStatus.Initialized
                    : SaveMigrationStatus.Migrated;

                ExecuteTransaction(
                    liveDirectory,
                    () => ApplySteps(fromVersion, _migrationPlan));

                SchemaVersion = _migrationPlan.CurrentVersion;
                return LastMigration = new SaveMigrationResult(
                    targetStatus,
                    fromVersion,
                    _migrationPlan.CurrentVersion);
            }
            catch (Exception exception)
            {
                return LastMigration = Failed(
                    SchemaVersion,
                    _migrationPlan.CurrentVersion,
                    exception.Message);
            }
        }
    }

    private void ApplySteps(int fromVersion, SaveMigrationPlanDefinition plan)
    {
        var stepsByVersion = plan.Steps.ToDictionary(step => step.FromVersion);
        var version = fromVersion;
        while (version < plan.CurrentVersion)
        {
            var step = stepsByVersion[version];
            step.Apply(new MigrationContext(this, step.FromVersion, step.ToVersion));
            version = step.ToVersion;
        }
    }

    private void ExecuteTransaction(string liveDirectory, Action apply)
    {
        var parentDirectory = Path.GetDirectoryName(liveDirectory)
            ?? throw new InvalidOperationException("Save sidecar directory has no parent.");
        Directory.CreateDirectory(parentDirectory);

        var transactionId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{_ownerId}.migration-{transactionId}");
        var backupDirectory = Path.Combine(
            parentDirectory,
            $".{_ownerId}.backup-{transactionId}");

        try
        {
            if (Directory.Exists(liveDirectory))
            {
                CopyDirectory(liveDirectory, stagingDirectory);
            }
            else
            {
                Directory.CreateDirectory(stagingDirectory);
            }

            _transactionDirectory = stagingDirectory;
            apply();
            WriteJsonAtomic(
                Path.Combine(stagingDirectory, MetadataFileName),
                new SaveSchemaMetadata(_migrationPlan!.CurrentVersion));
            _transactionDirectory = null;

            PromoteDirectory(liveDirectory, stagingDirectory, backupDirectory);
        }
        finally
        {
            _transactionDirectory = null;
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void PromoteDirectory(
        string liveDirectory,
        string stagingDirectory,
        string backupDirectory)
    {
        var movedLive = false;
        if (Directory.Exists(liveDirectory))
        {
            Directory.Move(liveDirectory, backupDirectory);
            movedLive = true;
        }

        try
        {
            Directory.Move(stagingDirectory, liveDirectory);
        }
        catch
        {
            if (movedLive &&
                Directory.Exists(backupDirectory) &&
                !Directory.Exists(liveDirectory))
            {
                Directory.Move(backupDirectory, liveDirectory);
            }

            throw;
        }

        if (movedLive)
        {
            TryDeleteDirectory(backupDirectory);
        }
    }

    private int? ReadStoredVersion(string liveDirectory)
    {
        var metadataPath = Path.Combine(liveDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        using var stream = File.OpenRead(metadataPath);
        var metadata = JsonSerializer.Deserialize<SaveSchemaMetadata>(stream, _options)
            ?? throw new InvalidDataException("Save schema metadata deserialized to null.");
        if (metadata.SchemaVersion < 0)
        {
            throw new InvalidDataException("Save schema version cannot be negative.");
        }

        return metadata.SchemaVersion;
    }

    private static bool HasUserData(string liveDirectory)
    {
        if (!Directory.Exists(liveDirectory))
        {
            return false;
        }

        var metadataPath = Path.GetFullPath(Path.Combine(liveDirectory, MetadataFileName));
        return Directory
            .EnumerateFiles(liveDirectory, "*.json", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Any(path => !string.Equals(
                path,
                metadataPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private bool Delete(string relativePath)
    {
        var path = Resolve(relativePath);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!string.Equals(Path.GetExtension(relativePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Save sidecar paths must use the .json extension.");
        }
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Save sidecar paths must be relative.");
        }
        var ownerDirectory = CurrentDirectory
            ?? throw new InvalidOperationException(
                "No save slot is active. Use SaveCompleted/LoadCompleted before accessing sidecars.");
        var root = Path.GetFullPath(ownerDirectory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(ownerDirectory, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Save sidecar path escapes its mod directory.");
        }
        if (string.Equals(
                resolved,
                Path.GetFullPath(Path.Combine(ownerDirectory, MetadataFileName)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{MetadataFileName}' is reserved by the framework.");
        }
        return resolved;
    }

    private string GetOwnerDirectory(int slot) => Path.Combine(
        _savesRoot,
        $"OFS_{slot:D4}",
        "Mods",
        _ownerId);

    private static string GetDefaultSavesRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.GetFullPath(Path.Combine(local, "..", "LocalLow"));
        return Path.Combine(localLow, "threeW", "Ore Factory Squad", "Saves");
    }

    private void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, _options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SaveMigrationPlanDefinition ValidatePlan(
        SaveMigrationPlanDefinition definition)
    {
        if (definition.CurrentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Current save schema version cannot be negative.");
        }
        if (definition.LegacyVersion < 0 ||
            definition.LegacyVersion > definition.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Legacy save schema version must be between zero and CurrentVersion.");
        }
        ArgumentNullException.ThrowIfNull(definition.Steps);

        var steps = definition.Steps.ToArray();
        var byFromVersion = new Dictionary<int, SaveMigrationStepDefinition>();
        foreach (var step in steps)
        {
            if (step is null)
            {
                throw new ArgumentException("Save migration steps cannot contain null.", nameof(definition));
            }
            if (step.FromVersion < 0 || step.ToVersion <= step.FromVersion)
            {
                throw new ArgumentException(
                    "Each save migration step must advance from a non-negative version.",
                    nameof(definition));
            }
            ArgumentNullException.ThrowIfNull(step.Apply);
            if (!byFromVersion.TryAdd(step.FromVersion, step))
            {
                throw new ArgumentException(
                    $"Save migration version {step.FromVersion} has multiple outgoing steps.",
                    nameof(definition));
            }
        }

        var visited = 0;
        var version = definition.LegacyVersion;
        while (version < definition.CurrentVersion)
        {
            if (!byFromVersion.TryGetValue(version, out var step))
            {
                throw new ArgumentException(
                    $"Save migration chain has no step from version {version}.",
                    nameof(definition));
            }
            if (step.ToVersion > definition.CurrentVersion)
            {
                throw new ArgumentException(
                    $"Save migration step {step.FromVersion}->{step.ToVersion} exceeds " +
                    $"CurrentVersion {definition.CurrentVersion}.",
                    nameof(definition));
            }

            version = step.ToVersion;
            visited++;
        }

        if (visited != steps.Length)
        {
            throw new ArgumentException(
                "Save migration plan contains unreachable or extraneous steps.",
                nameof(definition));
        }

        return definition with { Steps = steps };
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A completed promotion must not be reported as failed only because an
            // orphaned backup could not be cleaned. The next process can remove it.
        }
    }

    private static SaveMigrationResult Failed(int? fromVersion, int toVersion, string error) =>
        new(SaveMigrationStatus.Failed, fromVersion, toVersion, error);

    private sealed record SaveSchemaMetadata(int SchemaVersion);

    private sealed class MigrationContext(
        ModSaveData owner,
        int fromVersion,
        int toVersion) : IModSaveMigrationContext
    {
        public int FromVersion { get; } = fromVersion;
        public int ToVersion { get; } = toVersion;

        public bool Exists(string relativePath) => owner.Exists(relativePath);

        public T Load<T>(string relativePath, Func<T> createDefault) =>
            owner.Load(relativePath, createDefault);

        public void Save<T>(string relativePath, T value) =>
            owner.Save(relativePath, value);

        public bool Delete(string relativePath) => owner.Delete(relativePath);
    }
}
