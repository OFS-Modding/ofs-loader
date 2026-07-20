using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class ModRuntime
{
    private static readonly List<LoadedMod> Mods = new();
    private static readonly ConcurrentQueue<Action> MainThreadQueue = new();
    private static int _mainThreadId;
    private static int _modLoaderThreadId;
    private static bool _loadingMods;
    private static UnsafeIl2CppApi? _unsafeApi;
    private static ModRuntimeInfo? _runtimeInfo;

    public static IReadOnlyList<ModInfo> LoadedMods => Mods.Select(mod => mod.Info).ToArray();
    public static bool IsMainThread =>
        _mainThreadId != 0 && Environment.CurrentManagedThreadId == _mainThreadId;
    internal static bool CanUseLocalMessages =>
        IsMainThread || (_loadingMods &&
            Environment.CurrentManagedThreadId == _modLoaderThreadId);

    public static void Initialize(Il2CppProbeResult probe)
    {
        _unsafeApi = new UnsafeIl2CppApi(
            probe.GameAssemblyModule,
            probe.Domain,
            probe.Images);
        InputRuntime.Initialize(_unsafeApi);
        InteractionRuntime.Initialize(_unsafeApi);
        NetworkMessageRuntime.Initialize(_unsafeApi);
        ModMessageBus.Initialize();

        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The game process path has no directory.");
        var modsDirectory = Path.Combine(gameDirectory, "OFS", "mods");
        var configRoot = Path.Combine(gameDirectory, "OFS", "config");
        _runtimeInfo = ModRuntimeInfo.Create(gameDirectory, _unsafeApi);
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(configRoot);
        PendingModInstaller.Apply(gameDirectory);
        ModSafetyStore.Initialize(gameDirectory);
        ModProfileStore.Initialize(gameDirectory);

        RuntimeLog.Write($"Mod loader scanning '{modsDirectory}'.");
        var manifests = Directory
            .EnumerateFiles(modsDirectory, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ModDiagnosticsRuntime.Begin(gameDirectory, _runtimeInfo, manifests.Length);

        var enabledManifests = manifests.Where(path =>
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path));
                if (manifest is not null && ModSafetyStore.IsQuarantined(manifest.Id))
                {
                    var entry = ModSafetyStore.GetEntry(manifest.Id);
                    RuntimeLog.Write(entry is null
                        ? $"Mod '{manifest.Id}' blocked by safety fail-closed mode."
                        : $"Mod '{manifest.Id}' skipped because it is quarantined after phase " +
                          $"'{entry.Phase}' ({entry.Occurrences} occurrence(s)).");
                    ModDiagnosticsRuntime.Record(
                        path,
                        manifest,
                        ModStartupStatus.Quarantined,
                        "safety",
                        entry is null
                            ? "Blocked by safety fail-closed mode."
                            : $"Quarantined after phase '{entry.Phase}' " +
                              $"({entry.Occurrences} occurrence(s)): {entry.Reason}");
                    return false;
                }
                if (manifest is not null && ModProfileStore.IsEnabled(manifest.Id)) return true;
                if (manifest is not null)
                {
                    RuntimeLog.Write($"Mod '{manifest.Id}' disabled by active profile.");
                    ModDiagnosticsRuntime.Record(
                        path,
                        manifest,
                        ModStartupStatus.Disabled,
                        "profile",
                        "Disabled by the active mod profile.");
                }
            }
            catch
            {
                // ResolveLoadOrder emits the authoritative manifest diagnostic.
                return true;
            }
            return false;
        }).ToArray();
        var loadOrder = ResolveLoadOrder(enabledManifests);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _modLoaderThreadId = Environment.CurrentManagedThreadId;
        _loadingMods = true;
        try
        {
            foreach (var discovered in loadOrder)
            {
                try
                {
                    var unavailable = discovered.Manifest.Dependencies
                        .Where(dependency => !dependency.Optional && !loadedIds.Contains(dependency.Id))
                        .Select(dependency => dependency.Id)
                        .ToArray();
                    if (unavailable.Length != 0)
                    {
                        RuntimeLog.Write(
                            $"Mod '{discovered.Manifest.Id}' skipped because dependencies failed to load: " +
                            string.Join(", ", unavailable));
                        ModDiagnosticsRuntime.Record(
                            discovered.Path,
                            discovered.Manifest,
                            ModStartupStatus.Blocked,
                            "dependency-load",
                            "Required dependencies did not load successfully.",
                            unavailable);
                        continue;
                    }
                    LoadMod(discovered.Path, gameDirectory, configRoot, seenIds);
                    loadedIds.Add(discovered.Manifest.Id);
                    ModDiagnosticsRuntime.Record(
                        discovered.Path,
                        discovered.Manifest,
                        ModStartupStatus.Loaded,
                        "load",
                        "Loaded successfully.");
                }
                catch (Exception exception)
                {
                    RuntimeLog.Write($"Mod load failed for '{discovered.Path}': {exception}");
                    ModDiagnosticsRuntime.Record(
                        discovered.Path,
                        discovered.Manifest,
                        ModStartupStatus.Failed,
                        "load",
                        FormatDiagnosticException(exception));
                }
            }
        }
        finally
        {
            _loadingMods = false;
            _modLoaderThreadId = 0;
        }

        RuntimeLog.Write($"Mod loader complete: discovered={manifests.Length}, loaded={Mods.Count}.");
        NetworkCompatibilityRuntime.Configure(
            _unsafeApi,
            gameDirectory,
            Mods.Select(mod => mod.Manifest));
        ModDiagnosticsRuntime.Complete();
    }

    private static IReadOnlyList<DiscoveredMod> ResolveLoadOrder(IEnumerable<string> manifestPaths)
    {
        var discovered = new List<DiscoveredMod>();
        foreach (var path in manifestPaths)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Manifest deserialized to null.");
                ValidateManifest(manifest);
                _ = ModVersion.TryParse(manifest.Version, out var version);
                discovered.Add(new DiscoveredMod(path, manifest, version));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                RuntimeLog.Write($"Mod discovery rejected '{path}': {exception.Message}");
                ModDiagnosticsRuntime.Record(
                    path,
                    null,
                    ModStartupStatus.Rejected,
                    "discovery",
                    FormatDiagnosticException(exception));
            }
        }

        var candidates = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in discovered.GroupBy(mod => mod.Manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            var versions = group.ToArray();
            if (versions.Length != 1)
            {
                RuntimeLog.Write(
                    $"Mod id '{group.Key}' is duplicated in {versions.Length} directories; all copies skipped.");
                foreach (var duplicate in versions)
                {
                    ModDiagnosticsRuntime.Record(
                        duplicate.Path,
                        duplicate.Manifest,
                        ModStartupStatus.Rejected,
                        "duplicate-id",
                        $"Mod id '{group.Key}' is duplicated in {versions.Length} directories.");
                }
                continue;
            }
            candidates[group.Key] = versions[0];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in candidates.Values.ToArray())
            {
                foreach (var dependency in candidate.Manifest.Dependencies.Where(value => !value.Optional))
                {
                    if (!candidates.TryGetValue(dependency.Id, out var installed) ||
                        !ModVersionRange.TryParse(dependency.Version, out var range) ||
                        range is null ||
                        !range.Contains(installed.Version))
                    {
                        RuntimeLog.Write(
                            $"Mod '{candidate.Manifest.Id}' skipped: dependency '{dependency.Id}' " +
                            $"range '{dependency.Version}' is missing or incompatible.");
                        ModDiagnosticsRuntime.Record(
                            candidate.Path,
                            candidate.Manifest,
                            ModStartupStatus.Blocked,
                            "dependency-resolution",
                            $"Dependency '{dependency.Id}' range '{dependency.Version}' " +
                            "is missing or incompatible.",
                            [dependency.Id]);
                        candidates.Remove(candidate.Manifest.Id);
                        changed = true;
                        break;
                    }
                }
            }
        }

        var indegree = candidates.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = candidates.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Values)
        {
            foreach (var dependency in candidate.Manifest.Dependencies.Where(value => !value.Optional))
            {
                indegree[candidate.Manifest.Id]++;
                dependents[dependency.Id].Add(candidate.Manifest.Id);
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var order = new List<DiscoveredMod>();
        while (ready.Count != 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(candidates[id]);
            foreach (var dependent in dependents[id])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0) ready.Add(dependent);
            }
        }

        if (order.Count != candidates.Count)
        {
            var cyclic = candidates.Keys.Except(
                order.Select(mod => mod.Manifest.Id),
                StringComparer.OrdinalIgnoreCase).ToArray();
            RuntimeLog.Write($"Dependency cycle detected; skipped: {string.Join(", ", cyclic)}.");
            foreach (var id in cyclic)
            {
                var candidate = candidates[id];
                ModDiagnosticsRuntime.Record(
                    candidate.Path,
                    candidate.Manifest,
                    ModStartupStatus.Blocked,
                    "dependency-cycle",
                    "Dependency cycle prevents this mod from loading.",
                    cyclic.Where(value => !string.Equals(
                        value,
                        id,
                        StringComparison.OrdinalIgnoreCase)).ToArray());
            }
        }
        return order;
    }

    private static string FormatDiagnosticException(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    public static void NotifyMainMenuReady()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        // IOFSMod.Load can run before Unity's first EventSystem.Update. Drain work
        // queued during loading before publishing the first main-thread lifecycle event.
        PumpMainThread();
        foreach (var mod in Mods)
        {
            mod.Events.RaiseMainMenuReady(new MainMenuApi(mod.Info.Id));
        }
    }

    public static void NotifySceneLoaded(SceneEvent scene)
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        RuntimeLog.Write(
            $"Scene loaded: handle={scene.Handle}, name='{scene.Name ?? "<unknown>"}', " +
            $"mode={scene.LoadMode?.ToString() ?? "<unknown>"}, rawMode={scene.RawLoadMode}.");
        GameplayUiRuntime.NotifySceneLoaded(scene);
        SaveLifecycleRuntime.NotifySceneLoaded(scene);
        foreach (var mod in Mods)
        {
            mod.Events.RaiseSceneLoaded(scene);
        }
    }

    public static void NotifySceneUnloaded(SceneEvent scene)
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        if (string.Equals(scene.Name, "Main Menu", StringComparison.Ordinal))
        {
            UnityUiRuntime.NotifyMainMenuUnloaded();
        }
        GameplayUiRuntime.NotifySceneUnloaded(scene);
        RuntimeLog.Write(
            $"Scene unloaded: handle={scene.Handle}, name='{scene.Name ?? "<unknown>"}'.");
        SaveLifecycleRuntime.NotifySceneUnloaded(scene);
        foreach (var mod in Mods)
        {
            mod.Events.RaiseSceneUnloaded(scene);
        }
    }

    public static void NotifyFrameUpdate(FrameEvent frame)
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        ModAssets.PollScenes();
        ModAssets.PollAudio();
        ModAssets.PollMeshes();
        ModAssets.PollMaterials();
        PhysicsApi.PollAll();
        InputRuntime.Poll(frame);
        NetworkMessageRuntime.Poll(frame);
        NetworkCompatibilityRuntime.Poll(frame);
        SaveLifecycleRuntime.Poll(frame);
        foreach (var mod in Mods)
        {
            mod.Events.RaiseFrameUpdate(frame);
        }
    }

    public static void NotifyContentReady()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        RuntimeLog.Write("ItemSOManager is ready; publishing ContentReady.");
        GameContent.BeginBuildingRegistration();
        try
        {
            foreach (var mod in Mods)
            {
                var transactionStarted = false;
                try
                {
                    mod.Content.BeginTransaction();
                    transactionStarted = true;
                    mod.Events.RaiseContentReady();
                    mod.Content.CommitTransaction();
                }
                catch (Exception exception)
                {
                    if (transactionStarted)
                    {
                        RollbackLoad(
                            mod.Logger,
                            "ContentReady content transaction",
                            mod.Content.RollbackTransaction);
                    }
                    mod.Logger.Error(
                        exception,
                        "ContentReady failed; this mod's content changes were rolled back.");
                }
            }
        }
        finally
        {
            GameContent.EndBuildingRegistration();
            RuntimeLog.Write("ContentReady registration window sealed.");
        }
    }

    public static void NotifySaveCompleted(int slot) =>
        NotifySaveLifecycle(slot, isLoad: false);

    public static void NotifyLoadCompleted(int slot) =>
        NotifySaveLifecycle(slot, isLoad: true);

    private static void NotifySaveLifecycle(int slot, bool isLoad)
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        RuntimeLog.Write($"Vanilla {(isLoad ? "load" : "save")} completed for slot {slot}.");
        foreach (var mod in Mods)
        {
            mod.SaveData.SetCurrentSlot(slot);
            var migration = mod.SaveData.ApplyRegisteredMigrations();
            if (migration.Status == SaveMigrationStatus.Failed)
            {
                RuntimeLog.Write(
                    $"Mod '{mod.Info.Id}' save migration failed for slot {slot}: " +
                    migration.Error);
                continue;
            }
            if (migration.Status is SaveMigrationStatus.Initialized or SaveMigrationStatus.Migrated)
            {
                RuntimeLog.Write(
                    $"Mod '{mod.Info.Id}' save schema {migration.Status.ToString().ToLowerInvariant()}: " +
                    $"{migration.FromVersion}->{migration.ToVersion} for slot {slot}.");
            }

            var saveEvent = new SaveEvent(slot, mod.SaveData.CurrentDirectory!);
            if (isLoad)
            {
                mod.Events.RaiseLoadCompleted(saveEvent);
            }
            else
            {
                mod.Events.RaiseSaveCompleted(saveEvent);
            }
        }
    }

    public static void PumpMainThread()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        while (MainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                RuntimeLog.Write($"Scheduled mod action failed: {exception}");
            }
        }
    }

    internal static void EnqueueMainThread(Action action) => MainThreadQueue.Enqueue(action);

    private static void LoadMod(
        string manifestPath,
        string gameDirectory,
        string configRoot,
        HashSet<string> seenIds)
    {
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ModManifest>(json)
            ?? throw new InvalidDataException("Manifest deserialized to null.");
        ValidateManifest(manifest);
        if (!seenIds.Add(manifest.Id))
        {
            throw new InvalidDataException($"Duplicate mod id '{manifest.Id}'.");
        }

        var modDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Manifest has no parent directory.");
        var assemblyPath = ResolveContainedPath(modDirectory, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("Mod entry assembly does not exist.", assemblyPath);
        }

        var version = Version.Parse(manifest.Version);
        var info = new ModInfo(
            manifest.Id,
            manifest.Name,
            version,
            manifest.Description,
            manifest.Author,
            modDirectory);
        var logger = new ModLogger(info.Id);
        var events = new ModEvents(info.Id, logger);
        var scheduler = new MainThreadScheduler(info.Id);
        var messages = new ModMessageBus(info.Id, logger);
        var unsafeApi = _unsafeApi
            ?? throw new InvalidOperationException("Unsafe API was not initialized.");
        var runtimeInfo = _runtimeInfo
            ?? throw new InvalidOperationException("Runtime information was not initialized.");
        var unity = new UnityApi(unsafeApi);
        var assets = new ModAssets(info.Id, modDirectory, unsafeApi, logger);
        var hooks = new ModHooks(info.Id, logger, unsafeApi);
        var world = new WorldApi(info.Id, unity, events, logger);
        world.Attach();
        var physics = new PhysicsApi(info.Id, unity, unsafeApi, logger);
        var npcs = new NpcApi(info.Id, world, unity, unsafeApi, events, logger);
        var interactions = new InteractionApi(info.Id, unsafeApi, events, logger);
        var entities = new EntityApi(info.Id, world, unity, interactions, events, logger);
        var network = new NetworkApi(
            info.Id, unity, unsafeApi, npcs, entities, events, logger);
        network.Attach();
        var content = new GameContent(
            info.Id,
            unsafeApi,
            unity,
            () => network.IsServerActive || network.IsClientActive,
            () => network.IsServerActive,
            events,
            logger);
        var mechanics = new ModMechanics(info.Id, events, logger);
        mechanics.Attach();
        var input = new ModInput(info.Id, logger);
        var configDirectory = Path.Combine(configRoot, info.Id);
        var config = new ModConfig(configDirectory);
        var localization = new LocalizationApi(info.Id, unsafeApi);
        var dialogues = new DialogueApi(info.Id, unsafeApi, localization, events, logger);
        var gameplayUi = new GameplayUiApi(info.Id, unsafeApi, events, logger);
        var registry = new ModRegistry(GetLoadedModDescriptors);
        var saveData = new ModSaveData(info.Id);
        var context = new ModContext(
            info,
            runtimeInfo,
            gameDirectory,
            modDirectory,
            configDirectory,
            logger,
            events,
            scheduler,
            unity,
            assets,
            content,
            world,
            physics,
            entities,
            npcs,
            interactions,
            dialogues,
            gameplayUi,
            registry,
            messages,
            network,
            mechanics,
            input,
            config,
            localization,
            saveData,
            hooks,
            unsafeApi);
        Directory.CreateDirectory(context.ConfigDirectory);

        var loadContext = new ModLoadContext(assemblyPath);
        IOFSMod instance;
        var attempt = ModSafetyStore.BeginAttempt(manifest, manifestPath, assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            ModSafetyStore.UpdateAttempt(attempt, "entrypoint-create");
            var entryType = assembly.GetType(manifest.EntryPoint, throwOnError: true, ignoreCase: false)
                ?? throw new TypeLoadException(manifest.EntryPoint);
            if (!typeof(IOFSMod).IsAssignableFrom(entryType))
            {
                throw new InvalidDataException(
                    $"Entrypoint '{manifest.EntryPoint}' does not implement {nameof(IOFSMod)}.");
            }

            instance = (IOFSMod?)Activator.CreateInstance(entryType)
                ?? throw new InvalidOperationException("Mod entrypoint could not be constructed.");
            ModSafetyStore.UpdateAttempt(attempt, "mod-load");
            instance.Load(context);
            content.CommitTransaction();
        }
        catch
        {
            RollbackLoad(logger, "content", content.RollbackTransaction);
            RollbackLoad(logger, "local messages", messages.RemoveAll);
            RollbackLoad(logger, "mechanics", mechanics.RemoveAll);
            RollbackLoad(logger, "input", input.RemoveAll);
            RollbackLoad(logger, "network", network.RemoveAll);
            RollbackLoad(logger, "entities", entities.RemoveAll);
            RollbackLoad(logger, "NPCs", npcs.RemoveAll);
            RollbackLoad(logger, "physics", physics.RemoveAll);
            RollbackLoad(logger, "world", world.RemoveAll);
            RollbackLoad(logger, "assets", assets.RemoveAll);
            RollbackLoad(logger, "interactions", () => interactions.RemoveAll());
            RollbackLoad(
                logger,
                "dialogues",
                () => dialogues.CloseAll(DialogueCloseReason.Error));
            RollbackLoad(logger, "gameplay UI", gameplayUi.RemoveAll);
            RollbackLoad(logger, "hooks", hooks.RemoveAll);
            RollbackLoad(logger, "localization", localization.RemoveAll);
            throw;
        }
        finally
        {
            ModSafetyStore.CompleteAttempt(attempt);
        }
        ModSafetyStore.RegisterRuntimeMod(manifest, manifestPath, assemblyPath);
        Mods.Add(new LoadedMod(
            info,
            manifest,
            instance,
            events,
            content,
            assets,
            logger,
            saveData,
            loadContext));
        logger.Info($"Loaded {info.Name} {info.Version}.");
    }

    private static void RollbackLoad(IModLogger logger, string subsystem, Action rollback)
    {
        try
        {
            rollback();
        }
        catch (Exception exception)
        {
            logger.Error(exception, $"{subsystem} rollback was incomplete.");
        }
    }

    private static IReadOnlyList<LoadedModDescriptor> GetLoadedModDescriptors() => Mods
        .Select(loaded => new LoadedModDescriptor(
            loaded.Info,
            Version.Parse(loaded.Manifest.SdkVersion),
            loaded.Manifest.Multiplayer,
            Array.AsReadOnly((loaded.Manifest.Capabilities ?? []).ToArray()),
            Array.AsReadOnly((loaded.Manifest.Dependencies ?? [])
                .Select(value => value with { })
                .ToArray())))
        .ToArray();

    private static void ValidateManifest(ModManifest manifest)
    {
        var errors = ModManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Manifest paths must be relative.");
        }
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest path escapes the mod directory.");
        }
        return resolved;
    }

    private sealed record LoadedMod(
        ModInfo Info,
        ModManifest Manifest,
        IOFSMod Instance,
        ModEvents Events,
        GameContent Content,
        ModAssets Assets,
        ModLogger Logger,
        ModSaveData SaveData,
        ModLoadContext LoadContext);

    private sealed record DiscoveredMod(string Path, ModManifest Manifest, ModVersion Version);

    private sealed class ModLoadContext(string assemblyPath)
        : AssemblyLoadContext($"OFS.Mod:{Path.GetFileNameWithoutExtension(assemblyPath)}", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);
        private readonly string _directory = Path.GetDirectoryName(assemblyPath)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IOFSMod).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                return typeof(IOFSMod).Assembly;
            }

            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            var localFallback = Path.Combine(_directory, $"{assemblyName.Name}.dll");
            var path = resolved ?? (File.Exists(localFallback) ? localFallback : null);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }

    private sealed record ModContext(
        ModInfo Mod,
        IModRuntimeInfo Runtime,
        string GameDirectory,
        string ModDirectory,
        string ConfigDirectory,
        IModLogger Log,
        IModEvents Events,
        IMainThreadScheduler MainThread,
        IUnityApi Unity,
        IModAssets Assets,
        IGameContent Content,
        IWorldApi World,
        IModPhysicsApi Physics,
        IEntityApi Entities,
        INpcApi Npcs,
        IInteractionApi Interactions,
        IDialogueApi Dialogues,
        IGameplayUiApi GameplayUi,
        IModRegistry Mods,
        IModMessageBus Messages,
        INetworkApi Network,
        IModMechanics Mechanics,
        IModInput Input,
        IModConfig Config,
        ILocalizationApi Localization,
        IModSaveData SaveData,
        IModHooks Hooks,
        IUnsafeIl2CppApi UnsafeIl2Cpp) : IModContext;

    private sealed class ModEvents(string ownerId, ModLogger logger) : IModEvents
    {
        public event Action<IMainMenuApi>? MainMenuReady;
        public event Action<SceneEvent>? SceneLoaded;
        public event Action<SceneEvent>? SceneUnloaded;
        public event Action? ContentReady;
        public event Action<SaveEvent>? SaveCompleted;
        public event Action<SaveEvent>? LoadCompleted;
        public event Action<FrameEvent>? FrameUpdate;

        public void RaiseMainMenuReady(IMainMenuApi api)
        {
            Raise(MainMenuReady, api, "MainMenuReady");
        }

        public void RaiseSceneLoaded(SceneEvent scene) =>
            Raise(SceneLoaded, scene, "SceneLoaded");

        public void RaiseSceneUnloaded(SceneEvent scene) =>
            Raise(SceneUnloaded, scene, "SceneUnloaded");

        public void RaiseFrameUpdate(FrameEvent frame) =>
            Raise(FrameUpdate, frame, "FrameUpdate", durable: false);

        public void RaiseContentReady()
        {
            foreach (var handler in ContentReady?.GetInvocationList() ?? [])
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    ownerId,
                    "event:ContentReady");
                ((Action)handler)();
            }
        }

        public void RaiseSaveCompleted(SaveEvent saveEvent) =>
            Raise(SaveCompleted, saveEvent, "SaveCompleted");

        public void RaiseLoadCompleted(SaveEvent saveEvent) =>
            Raise(LoadCompleted, saveEvent, "LoadCompleted");

        private void Raise<T>(
            Action<T>? eventHandler,
            T value,
            string eventName,
            bool durable = true)
        {
            foreach (var handler in eventHandler?.GetInvocationList() ?? [])
            {
                try
                {
                    if (durable)
                    {
                        using var callback = ModSafetyStore.EnterRuntimeCallback(
                            ownerId,
                            $"event:{eventName}");
                        ((Action<T>)handler)(value);
                    }
                    else
                    {
                        using var callback = ModSafetyStore.EnterHotRuntimeCallback(
                            ownerId,
                            $"event:{eventName}");
                        ((Action<T>)handler)(value);
                    }
                }
                catch (Exception exception)
                {
                    logger.Error(exception, $"{eventName} handler failed.");
                }
            }
        }
    }

    private sealed class MainThreadScheduler(string ownerId) : IMainThreadScheduler
    {
        public bool IsMainThread =>
            ModRuntime.IsMainThread;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            MainThreadQueue.Enqueue(() =>
            {
                using var callback = ModSafetyStore.EnterRuntimeCallback(
                    ownerId,
                    "scheduler:main-thread");
                action();
            });
        }
    }

    private sealed class MainMenuApi(string ownerId) : IMainMenuApi
    {
        public IMenuButton AddButton(MainMenuButtonDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            return UnityUiRuntime.AddExternalMainMenuButton(ownerId, definition);
        }

        public IMenuPanel AddPanel(MainMenuPanelDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            return UnityUiRuntime.AddExternalMainMenuPanel(ownerId, definition);
        }
    }

    internal sealed class ModLogger(string id) : IModLogger
    {
        public void Trace(string message) => Write("TRACE", message);
        public void Info(string message) => Write("INFO", message);
        public void Warning(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);
        public void Error(Exception exception, string message) =>
            Write("ERROR", $"{message} {exception}");

        private void Write(string level, string message) =>
            RuntimeLog.Write($"[{id}] [{level}] {message}");
    }
}
