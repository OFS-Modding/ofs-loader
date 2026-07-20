using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class EntityApi : IEntityApi
{
    private readonly string _ownerId;
    private readonly IWorldApi _world;
    private readonly IUnityApi _unity;
    private readonly IInteractionApi _interactions;
    private readonly IModLogger _logger;
    private readonly Dictionary<string, DefinitionRegistration> _definitions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entity> _entities = [];
    private readonly EntityBehaviorRuntime _behaviors;
    private long _interactionSequence;

    public EntityApi(
        string ownerId,
        IWorldApi world,
        IUnityApi unity,
        IInteractionApi interactions,
        IModEvents events,
        IModLogger logger)
    {
        _ownerId = ownerId;
        _world = world;
        _unity = unity;
        _interactions = interactions;
        _logger = logger;
        _behaviors = new EntityBehaviorRuntime(ownerId, events, logger);
        events.SceneUnloaded += OnSceneUnloaded;
    }

    public IEntityDefinitionRegistration RegisterDefinition(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = definition with
        {
            Variants = (definition.Variants ?? []).ToArray(),
            Behaviors = (definition.Behaviors ?? []).ToArray(),
        };
        ValidateDefinition(normalized);
        if (_definitions.ContainsKey(normalized.Id))
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' already registered entity definition '{normalized.Id}'.");
        var registration = new DefinitionRegistration(this, _ownerId, normalized);
        _definitions.Add(normalized.Id, registration);
        _logger.Info(
            $"Registered entity definition '{registration.QualifiedId}' with " +
            $"{registration.VariantIds.Count} variant(s), " +
            $"{normalized.Behaviors?.Count ?? 0} behavior(s), " +
            $"interactive={normalized.Interaction is not null}.");
        return registration;
    }

    public IEntity Spawn(EntitySpawnDefinition definition)
    {
        EnsureMainThread();
        var resolved = Resolve(definition);
        var spawned = _world.Spawn(new PrefabSpawnDefinition(
            resolved.Prefab,
            definition.Position,
            definition.Rotation,
            definition.Parent,
            resolved.Name,
            resolved.Persistent,
            resolved.Active));
        return AttachResolved(
            resolved,
            spawned.GameObject,
            () => spawned.IsSpawned,
            spawned.Despawn);
    }

    internal ResolvedEntitySpawn Resolve(EntitySpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DefinitionId);
        if (!_definitions.TryGetValue(definition.DefinitionId, out var registration) ||
            !registration.IsRegistered)
            throw new KeyNotFoundException(
                $"Mod '{_ownerId}' has no active entity definition '{definition.DefinitionId}'.");
        var visual = registration.Resolve(definition.VariantId);
        return new ResolvedEntitySpawn(
            registration,
            definition.VariantId,
            visual.Prefab,
            definition.Name ?? visual.DisplayName ?? registration.Definition.DisplayName,
            definition.Persistent ?? registration.Definition.Persistent,
            definition.Active ?? registration.Definition.Active);
    }

    internal IReadOnlyList<UnityObject> GetDefinitionPrefabs(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        if (!_definitions.TryGetValue(definitionId, out var registration) || !registration.IsRegistered)
            throw new KeyNotFoundException(
                $"Mod '{_ownerId}' has no active entity definition '{definitionId}'.");
        return new[] { registration.Definition.Prefab }
            .Concat((registration.Definition.Variants ?? []).Select(value => value.Prefab))
            .DistinctBy(value => value.Pointer)
            .ToArray();
    }

    internal IEntity AttachResolved(
        ResolvedEntitySpawn resolved,
        UnityObject gameObject,
        Func<bool> isSpawned,
        Action despawn)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(isSpawned);
        ArgumentNullException.ThrowIfNull(despawn);
        if (gameObject.IsNull) throw new ArgumentException("Entity GameObject is null.");
        var entity = new Entity(
            this,
            _ownerId,
            resolved.Registration.Definition.Id,
            resolved.VariantId,
            resolved.Persistent,
            gameObject,
            isSpawned,
            despawn,
            _unity);
        _entities.Add(entity);
        try
        {
            if (resolved.Registration.Definition.Interaction is { } interaction)
                entity.SetInteraction(CreateInteraction(entity, interaction, resolved.Name));
            foreach (var behavior in resolved.Registration.Definition.Behaviors ?? [])
                _ = AttachBehavior(entity, behavior);
            _logger.Info(
                $"Attached entity '{resolved.Registration.QualifiedId}' variant=" +
                $"'{resolved.VariantId ?? "<default>"}' to 0x{entity.GameObject.Pointer:X}.");
            return entity;
        }
        catch
        {
            entity.Despawn();
            throw;
        }
    }

    internal sealed record ResolvedEntitySpawn(
        DefinitionRegistration Registration,
        string? VariantId,
        UnityObject Prefab,
        string? Name,
        bool Persistent,
        bool Active);

    public IEntityBehavior AttachBehavior(IEntity entity, EntityBehaviorDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(entity.OwnerId, _ownerId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' cannot attach a behavior to entity owned by '{entity.OwnerId}'.");
        if (!entity.IsSpawned)
            throw new ObjectDisposedException(nameof(entity), "Cannot attach behavior to a despawned entity.");
        return _behaviors.Attach(entity, definition);
    }

    internal void RemoveDefinition(DefinitionRegistration registration)
    {
        if (!registration.IsRegistered) return;
        if (!_definitions.TryGetValue(registration.Id, out var current) ||
            !ReferenceEquals(current, registration))
            throw new InvalidOperationException("Entity definition registration ownership changed.");
        _definitions.Remove(registration.Id);
        registration.MarkUnregistered();
    }

    internal void OnEntityDespawning(Entity entity, bool destroyWorld)
    {
        if (!_entities.Remove(entity)) return;
        _behaviors.DetachAll(entity);
        entity.ReleaseInteraction();
        if (destroyWorld) entity.ReleaseWorld();
    }

    internal void RemoveAll()
    {
        foreach (var entity in _entities.ToArray().Reverse())
        {
            try { entity.Despawn(); }
            catch (Exception exception) { _logger.Error(exception, "Entity rollback cleanup failed."); }
        }
        foreach (var definition in _definitions.Values) definition.MarkUnregistered();
        _definitions.Clear();
        _behaviors.RemoveAll();
    }

    private IInteractionRegistration CreateInteraction(
        Entity entity,
        EntityInteractionDefinition definition,
        string? variantDisplayName)
    {
        var localId = $"entity-{_interactionSequence++:x}";
        return _interactions.Register(new InteractionDefinition(
            localId,
            entity.GameObject,
            definition.Primary is null
                ? null
                : native => definition.Primary(new EntityInteractionEvent(
                    entity, InteractionButton.Primary, native)),
            definition.Secondary is null
                ? null
                : native => definition.Secondary(new EntityInteractionEvent(
                    entity, InteractionButton.Secondary, native)),
            definition.PrimaryHandling,
            definition.SecondaryHandling,
            definition.DisplayName ?? variantDisplayName,
            definition.DisplayNameIsLocalizationTerm,
            definition.PrimaryMode,
            definition.PrimaryPrompt,
            definition.PrimaryHoldSeconds,
            definition.SecondaryMode,
            definition.SecondaryPrompt,
            definition.SecondaryHoldSeconds,
            entity.Persistent));
    }

    private void OnSceneUnloaded(SceneEvent _)
    {
        foreach (var entity in _entities.Where(value => !value.Persistent).ToArray())
            OnEntityDespawning(entity, destroyWorld: false);
    }

    internal static void ValidateDefinition(EntityDefinition definition)
    {
        ValidateId(definition.Id, "Entity definition");
        if (definition.Prefab.IsNull)
            throw new ArgumentException("Entity definition prefab is null.", nameof(definition));
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in definition.Variants ?? [])
        {
            ArgumentNullException.ThrowIfNull(variant);
            ValidateId(variant.Id, "Entity visual variant");
            if (!variants.Add(variant.Id))
                throw new ArgumentException($"Entity variant '{variant.Id}' is duplicated.");
            if (variant.Prefab.IsNull)
                throw new ArgumentException($"Entity variant '{variant.Id}' has a null prefab.");
        }
        var behaviors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var behavior in definition.Behaviors ?? [])
        {
            ArgumentNullException.ThrowIfNull(behavior);
            ValidateBehavior(behavior);
            if (!behaviors.Add(behavior.Id))
                throw new ArgumentException($"Entity behavior '{behavior.Id}' is duplicated.");
        }
        if (definition.Interaction is { Primary: null, Secondary: null })
            throw new ArgumentException("Entity interaction needs a primary or secondary callback.");
    }

    internal static void ValidateBehavior(EntityBehaviorDefinition definition)
    {
        ValidateId(definition.Id, "Entity behavior");
        if (definition.Started is null && definition.Update is null && definition.Stopped is null)
            throw new ArgumentException(
                $"Entity behavior '{definition.Id}' must define at least one callback.");
    }

    private static void ValidateId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException(
                $"{kind} id must contain 1-100 ASCII letters, digits, '.', '_' or '-'.");
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
            throw new InvalidOperationException("Entity calls must run on Unity's main thread.");
    }

    internal sealed class DefinitionRegistration : IEntityDefinitionRegistration
    {
        private readonly EntityApi _api;
        private readonly Dictionary<string, EntityVisualVariantDefinition> _variants;

        internal DefinitionRegistration(EntityApi api, string ownerId, EntityDefinition definition)
        {
            _api = api;
            Definition = definition;
            Id = definition.Id;
            QualifiedId = $"{ownerId}:{definition.Id}";
            _variants = (definition.Variants ?? []).ToDictionary(
                value => value.Id, StringComparer.OrdinalIgnoreCase);
            VariantIds = _variants.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal EntityDefinition Definition { get; }
        public string Id { get; }
        public string QualifiedId { get; }
        public IReadOnlyList<string> VariantIds { get; }
        public bool IsRegistered { get; private set; } = true;

        internal EntityVisualVariantDefinition Resolve(string? variantId)
        {
            if (string.IsNullOrWhiteSpace(variantId))
                return new EntityVisualVariantDefinition("<default>", Definition.Prefab, Definition.DisplayName);
            return _variants.TryGetValue(variantId, out var variant)
                ? variant
                : throw new KeyNotFoundException(
                    $"Entity definition '{QualifiedId}' has no variant '{variantId}'.");
        }

        public void Unregister() => _api.RemoveDefinition(this);
        public void Dispose() => Unregister();
        internal void MarkUnregistered() => IsRegistered = false;
    }

    internal sealed class Entity(
        EntityApi api,
        string ownerId,
        string definitionId,
        string? variantId,
        bool persistent,
        UnityObject gameObject,
        Func<bool> isSpawned,
        Action despawn,
        IUnityApi unity) : IEntity
    {
        private IInteractionRegistration? _interaction;
        public string OwnerId { get; } = ownerId;
        public string DefinitionId { get; } = definitionId;
        public string? VariantId { get; } = variantId;
        public UnityObject GameObject { get; } = gameObject;
        public bool Persistent { get; } = persistent;
        public bool IsSpawned => isSpawned();
        public UnityTransform Transform
        {
            get => unity.GetTransform(GameObject);
            set => unity.SetTransform(GameObject, value);
        }
        public IInteractionRegistration? Interaction => _interaction;

        internal void SetInteraction(IInteractionRegistration interaction) => _interaction = interaction;
        internal void ReleaseInteraction()
        {
            _interaction?.Dispose();
            _interaction = null;
        }
        internal void ReleaseWorld() => despawn();

        public void Despawn()
        {
            if (!IsSpawned) return;
            api.OnEntityDespawning(this, destroyWorld: true);
        }

        public void Dispose() => Despawn();
    }
}

internal sealed class EntityBehaviorRuntime
{
    private readonly string _ownerId;
    private readonly IModEvents _events;
    private readonly IModLogger _logger;
    private readonly List<Controller> _controllers = [];
    private long _sequence;
    private bool _attached = true;

    internal EntityBehaviorRuntime(string ownerId, IModEvents events, IModLogger logger)
    {
        _ownerId = ownerId;
        _events = events;
        _logger = logger;
        _events.FrameUpdate += OnFrameUpdate;
    }

    internal IEntityBehavior Attach(IEntity entity, EntityBehaviorDefinition definition)
    {
        EntityApi.ValidateBehavior(definition);
        if (!_attached) throw new ObjectDisposedException(nameof(EntityBehaviorRuntime));
        if (_controllers.Any(value => value.IsAttached && ReferenceEquals(value.Entity, entity) &&
            string.Equals(value.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Entity 0x{entity.GameObject.Pointer:X} already has behavior '{definition.Id}' " +
                $"owned by '{_ownerId}'.");
        var controller = new Controller(this, entity, definition, _logger, _sequence++);
        _controllers.Add(controller);
        _controllers.Sort(static (left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        });
        controller.InvokeStarted();
        _logger.Info(
            $"Attached entity behavior '{definition.Id}' to 0x{entity.GameObject.Pointer:X} " +
            $"at order {definition.Order}.");
        return controller;
    }

    internal void DetachAll(IEntity entity)
    {
        foreach (var controller in _controllers.Where(value =>
                     ReferenceEquals(value.Entity, entity)).ToArray()) controller.Detach();
    }

    internal void RemoveAll()
    {
        foreach (var controller in _controllers.ToArray().Reverse()) controller.Detach();
        if (_attached)
        {
            _events.FrameUpdate -= OnFrameUpdate;
            _attached = false;
        }
    }

    private void OnFrameUpdate(FrameEvent frame)
    {
        foreach (var controller in _controllers.ToArray())
        {
            if (!controller.Entity.IsSpawned) controller.Detach();
            else controller.InvokeUpdate(frame);
        }
    }

    private void Remove(Controller controller)
    {
        if (!controller.IsAttached) return;
        _controllers.Remove(controller);
        controller.MarkDetached();
        controller.InvokeStopped();
    }

    private sealed class Controller(
        EntityBehaviorRuntime runtime,
        IEntity entity,
        EntityBehaviorDefinition definition,
        IModLogger logger,
        long sequence) : IEntityBehavior
    {
        public string Id { get; } = definition.Id;
        public IEntity Entity { get; } = entity;
        public int Order { get; } = definition.Order;
        public bool Enabled { get; set; } = true;
        public bool IsAttached { get; private set; } = true;
        internal long Sequence { get; } = sequence;
        public void Detach() => runtime.Remove(this);
        public void Dispose() => Detach();
        internal void MarkDetached() => IsAttached = false;
        internal void InvokeStarted()
        {
            if (definition.Started is not null) Invoke(() => definition.Started(Entity), "Started");
        }
        internal void InvokeUpdate(FrameEvent frame)
        {
            if (Enabled && IsAttached && definition.Update is not null)
                Invoke(() => definition.Update(Entity, frame), "Update");
        }
        internal void InvokeStopped()
        {
            if (definition.Stopped is null) return;
            try { definition.Stopped(Entity); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Entity behavior '{Id}' failed during Stopped.");
            }
        }
        private void Invoke(Action callback, string phase)
        {
            try { callback(); }
            catch (Exception exception)
            {
                logger.Error(exception, $"Entity behavior '{Id}' failed during {phase}.");
                if (definition.DisableOnException)
                {
                    Enabled = false;
                    logger.Warning($"Entity behavior '{Id}' was disabled after an exception.");
                }
            }
        }
    }
}
