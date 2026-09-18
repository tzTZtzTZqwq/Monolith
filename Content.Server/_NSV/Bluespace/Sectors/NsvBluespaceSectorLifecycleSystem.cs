using System.Linq;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared.Ghost;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NSV.Bluespace.Sectors;

public enum NsvBluespaceSectorWakeReason
{
    SectorAccess,
    Arrival,
    PlayerReconnect,
    EntityTransfer,
    MustRunTask,
    Reconciliation
}

public readonly record struct NsvBluespaceSectorRegistryEntry(
    EntityUid MapUid,
    MapId MapId,
    NsvBluespaceSectorState State,
    int ForeignGridCount,
    int PendingArrivalCount,
    int MustRunTaskBlockerCount,
    int ActiveLivingPlayers,
    int ActiveAiShips,
    bool HasPlayerFactionShip,
    TimeSpan? SleepEligibleSince,
    TimeSpan? SleepDeadline);

public sealed partial class NsvBluespaceSectorLifecycleSystem : EntitySystem
{
    private const float RegistryScanInterval = 5f;
    private const int AiShipCountCap = 12;
    private const int BaseSleepDelaySeconds = 30;
    private const int PerAiShipDelaySeconds = 10;
    private const int PlayerShipDelaySeconds = 90;
    private const int MinSleepDelaySeconds = 30;
    private const int MaxSleepDelaySeconds = 300;

    private static readonly ProtoId<NsvBluespaceFactionPrototype> PlayerFaction = "NSVPlayer";

    [Dependency] private NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private MapSystem _map = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<NetUserId, DisconnectedLivingCharacter> _disconnectedCharacters = new();
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _mustRunTaskBlockers = new();
    private Dictionary<EntityUid, NsvBluespaceSectorRegistryEntry> _registry = new();
    private EntityUid? _sleepTransitionOwner;
    private EntityUid? _wakeTransitionOwner;
    private float _scanAccumulator;

    internal TimeSpan DisconnectedPlayerGrace { get; set; } = TimeSpan.FromSeconds(30);
    internal Action<EntityUid>? MapPausedTestHook { get; set; }
    internal Action<EntityUid>? MapUnpausedTestHook { get; set; }

    public event Action<EntityUid>? SectorDisplayChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<ActorComponent, EntParentChangedMessage>(OnActorParentChanged);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
        RefreshRegistry();
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _disconnectedCharacters.Clear();
        _mustRunTaskBlockers.Clear();
        _registry.Clear();
        _sleepTransitionOwner = null;
        _wakeTransitionOwner = null;
        MapPausedTestHook = null;
        MapUnpausedTestHook = null;
        SectorDisplayChanged = null;
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        _scanAccumulator += frameTime;
        if (_scanAccumulator < RegistryScanInterval)
            return;

        _scanAccumulator %= RegistryScanInterval;
        RefreshRegistry();
    }

    public void RefreshRegistry()
    {
        PruneMustRunTaskBlockers();

        var registry = new Dictionary<EntityUid, NsvBluespaceSectorRegistryEntry>();
        var aggregations = new Dictionary<EntityUid, SectorAggregation>();
        var sectorQuery = EntityManager.AllEntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (sectorQuery.MoveNext(out var mapUid, out var sector))
        {
            registry.Add(mapUid, new NsvBluespaceSectorRegistryEntry(
                mapUid,
                sector.MapId,
                sector.State,
                sector.ForeignGrids.Count,
                sector.PendingArrivals.Count,
                GetMustRunTaskBlockerCount(mapUid),
                0,
                0,
                false,
                null,
                null));
            aggregations.Add(mapUid, new SectorAggregation());
        }

        AggregateOnlinePlayers(aggregations);
        AggregateDisconnectedPlayers(aggregations);
        AggregateAiShips(aggregations);
        AggregatePlayerFactionShips(aggregations);

        var sleepCandidates = new List<EntityUid>();
        var wakeCandidates = new List<EntityUid>();
        var sleepingReconciliations = new List<EntityUid>();
        var displayChanges = new HashSet<EntityUid>();
        var now = _timing.CurTime;
        foreach (var mapUid in registry.Keys.ToArray())
        {
            var entry = registry[mapUid];
            var aggregation = aggregations[mapUid];
            var activeLivingPlayers = aggregation.LivingPlayers.Count;
            var activeAiShips = aggregation.AiShips.Count;
            var state = entry.State;
            var eligibleSince = default(TimeSpan?);
            var deadline = default(TimeSpan?);
            var hasSleepBlocker = HasSleepBlocker(
                activeLivingPlayers,
                entry.PendingArrivalCount,
                entry.MustRunTaskBlockerCount);

            if (state is NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep)
            {
                if (hasSleepBlocker)
                {
                    if (state == NsvBluespaceSectorState.PreparingSleep &&
                        TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
                    {
                        sector.State = NsvBluespaceSectorState.Ready;
                        displayChanges.Add(mapUid);
                    }

                    state = NsvBluespaceSectorState.Ready;
                }
                else
                {
                    _registry.TryGetValue(mapUid, out var previous);
                    var previousEligibleSince = previous.State is NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep
                        ? previous.SleepEligibleSince
                        : null;
                    var previousDeadline = previous.State is NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep
                        ? previous.SleepDeadline
                        : null;
                    eligibleSince = previousEligibleSince ?? now;
                    var candidate = eligibleSince.Value + CalculateSleepDelay(activeAiShips, aggregation.HasPlayerFactionShip);
                    deadline = previousDeadline is { } existingDeadline && existingDeadline > candidate
                        ? existingDeadline
                        : candidate;

                    if (state == NsvBluespaceSectorState.Ready &&
                        TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
                    {
                        sector.State = NsvBluespaceSectorState.PreparingSleep;
                        state = NsvBluespaceSectorState.PreparingSleep;
                        displayChanges.Add(mapUid);
                    }

                    if (state == NsvBluespaceSectorState.PreparingSleep && deadline <= now)
                        sleepCandidates.Add(mapUid);
                }
            }
            else if (state == NsvBluespaceSectorState.Sleeping)
            {
                if (hasSleepBlocker)
                    wakeCandidates.Add(mapUid);
                else if (!_map.IsPaused(entry.MapId))
                    sleepingReconciliations.Add(mapUid);
            }

            registry[mapUid] = entry with
            {
                State = state,
                ActiveLivingPlayers = activeLivingPlayers,
                ActiveAiShips = activeAiShips,
                HasPlayerFactionShip = aggregation.HasPlayerFactionShip,
                SleepEligibleSince = eligibleSince,
                SleepDeadline = deadline,
            };
        }

        _registry = registry;

        foreach (var mapUid in displayChanges)
            SectorDisplayChanged?.Invoke(mapUid);

        foreach (var mapUid in wakeCandidates)
            RequestWake(mapUid, NsvBluespaceSectorWakeReason.Reconciliation, null, out _);

        foreach (var mapUid in sleepingReconciliations)
        {
            if (TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) &&
                sector.State == NsvBluespaceSectorState.Sleeping &&
                !_map.IsPaused(sector.MapId))
            {
                _map.SetPaused(sector.MapId, true);
            }
        }

        foreach (var mapUid in sleepCandidates)
            TryCommitSleep(mapUid);
    }

    public bool TryGetRegistryEntry(EntityUid mapUid, out NsvBluespaceSectorRegistryEntry entry)
    {
        return _registry.TryGetValue(mapUid, out entry);
    }

    public bool RegisterMustRunTaskBlocker(EntityUid mapUid, EntityUid owner, out string? failure)
    {
        failure = null;
        if (!owner.IsValid() || TerminatingOrDeleted(owner))
        {
            failure = "The must-run task owner is invalid.";
            return false;
        }

        if (TerminatingOrDeleted(mapUid) || !HasComp<NsvBluespaceSectorInstanceComponent>(mapUid))
        {
            failure = "The bluespace sector is unavailable.";
            return false;
        }

        if (!_mustRunTaskBlockers.TryGetValue(mapUid, out var blockers))
        {
            blockers = new HashSet<EntityUid>();
            _mustRunTaskBlockers.Add(mapUid, blockers);
        }

        if (!blockers.Add(owner))
            return true;

        if (RequestWake(mapUid, NsvBluespaceSectorWakeReason.MustRunTask, owner, out failure))
        {
            UpdateMustRunTaskBlockerCount(mapUid);
            return true;
        }

        blockers.Remove(owner);
        if (blockers.Count == 0)
            _mustRunTaskBlockers.Remove(mapUid);
        return false;
    }

    public bool UnregisterMustRunTaskBlocker(EntityUid mapUid, EntityUid owner)
    {
        if (!_mustRunTaskBlockers.TryGetValue(mapUid, out var blockers) || !blockers.Remove(owner))
            return false;

        if (blockers.Count == 0)
            _mustRunTaskBlockers.Remove(mapUid);
        UpdateMustRunTaskBlockerCount(mapUid);
        return true;
    }

    public bool HasMustRunTaskBlockers(EntityUid mapUid)
    {
        return GetMustRunTaskBlockerCount(mapUid) != 0;
    }

    public bool RequestWake(
        EntityUid mapUid,
        NsvBluespaceSectorWakeReason reason,
        EntityUid? requester,
        out string? failure)
    {
        failure = null;
        if (TerminatingOrDeleted(mapUid) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
        {
            failure = "The bluespace sector is unavailable.";
            return false;
        }

        if (sector.State == NsvBluespaceSectorState.Waking || _wakeTransitionOwner == mapUid)
            return true;

        switch (sector.State)
        {
            case NsvBluespaceSectorState.Ready:
                if (!_map.IsPaused(sector.MapId))
                    return true;
                return TryWakePausedSector(mapUid, sector, reason, requester, out failure);
            case NsvBluespaceSectorState.PreparingSleep:
                if (_sleepTransitionOwner == mapUid)
                    _sleepTransitionOwner = null;
                if (_map.IsPaused(sector.MapId))
                    return TryWakePausedSector(mapUid, sector, reason, requester, out failure);
                sector.TransitionEpoch++;
                SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Ready);
                return true;
            case NsvBluespaceSectorState.Sleeping:
                return TryWakePausedSector(mapUid, sector, reason, requester, out failure);
            case NsvBluespaceSectorState.Applying:
                failure = "The bluespace sector is still being prepared.";
                return false;
            case NsvBluespaceSectorState.Draining:
                failure = "The bluespace sector is being destroyed.";
                return false;
            case NsvBluespaceSectorState.Failed:
                failure = "The bluespace sector is unavailable.";
                return false;
            default:
                failure = $"The bluespace sector cannot wake from {sector.State}.";
                return false;
        }
    }

    internal bool TryCommitSleep(EntityUid mapUid)
    {
        if (_sleepTransitionOwner != null ||
            !_registry.ContainsKey(mapUid) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) ||
            sector.State != NsvBluespaceSectorState.PreparingSleep)
        {
            return false;
        }

        _sleepTransitionOwner = mapUid;
        var transitionEpoch = ++sector.TransitionEpoch;
        try
        {
            if (HasAuthoritativeSleepBlocker(mapUid, sector))
            {
                SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Ready);
                return false;
            }

            if (_map.IsPaused(sector.MapId))
            {
                RollbackSleep(mapUid);
                return false;
            }

            _map.SetPaused(sector.MapId, true);
            var testHook = MapPausedTestHook;
            MapPausedTestHook = null;
            testHook?.Invoke(mapUid);

            if (_sleepTransitionOwner != mapUid ||
                TerminatingOrDeleted(mapUid) ||
                !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out sector) ||
                sector.TransitionEpoch != transitionEpoch ||
                sector.State != NsvBluespaceSectorState.PreparingSleep ||
                !_map.IsPaused(sector.MapId) ||
                HasAuthoritativeSleepBlocker(mapUid, sector))
            {
                RollbackSleep(mapUid);
                return false;
            }

            SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Sleeping);
            return true;
        }
        finally
        {
            if (_sleepTransitionOwner == mapUid)
                _sleepTransitionOwner = null;
        }
    }

    private bool TryWakePausedSector(
        EntityUid mapUid,
        NsvBluespaceSectorInstanceComponent sector,
        NsvBluespaceSectorWakeReason reason,
        EntityUid? requester,
        out string? failure)
    {
        failure = null;
        if (_wakeTransitionOwner != null)
        {
            failure = _wakeTransitionOwner == mapUid
                ? null
                : "Another bluespace sector transition is in progress.";
            return _wakeTransitionOwner == mapUid;
        }

        if (_sleepTransitionOwner == mapUid)
            _sleepTransitionOwner = null;

        _wakeTransitionOwner = mapUid;
        var transitionEpoch = ++sector.TransitionEpoch;
        SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Waking);
        try
        {
            if (_map.IsPaused(sector.MapId))
                _map.SetPaused(sector.MapId, false);

            var testHook = MapUnpausedTestHook;
            MapUnpausedTestHook = null;
            testHook?.Invoke(mapUid);

            if (_wakeTransitionOwner != mapUid ||
                TerminatingOrDeleted(mapUid) ||
                !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var currentSector) ||
                currentSector.TransitionEpoch != transitionEpoch ||
                currentSector.State != NsvBluespaceSectorState.Waking ||
                _map.IsPaused(currentSector.MapId))
            {
                RollbackWake(mapUid);
                failure = "The bluespace sector wake transition was interrupted.";
                return false;
            }

            SetLifecycleState(mapUid, currentSector, NsvBluespaceSectorState.Ready);
            Logger.DebugS(
                "nsv.bluespace",
                $"Woke sector {ToPrettyString(mapUid)} for {reason} requested by {requester?.ToString() ?? "system"}.");
            return true;
        }
        finally
        {
            if (_wakeTransitionOwner == mapUid)
                _wakeTransitionOwner = null;
        }
    }

    private bool HasAuthoritativeSleepBlocker(
        EntityUid mapUid,
        NsvBluespaceSectorInstanceComponent sector)
    {
        if (sector.PendingArrivals.Count != 0 || HasMustRunTaskBlockers(mapUid))
            return true;

        var aggregations = new Dictionary<EntityUid, SectorAggregation>
        {
            [mapUid] = new SectorAggregation(),
        };
        AggregateOnlinePlayers(aggregations);
        AggregateDisconnectedPlayers(aggregations);
        return aggregations[mapUid].LivingPlayers.Count != 0;
    }

    private void RollbackSleep(EntityUid mapUid)
    {
        if (TerminatingOrDeleted(mapUid) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector))
        {
            return;
        }

        if (_map.IsPaused(sector.MapId))
            _map.SetPaused(sector.MapId, false);

        if (sector.State is NsvBluespaceSectorState.Ready or NsvBluespaceSectorState.PreparingSleep)
            SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Ready);
    }

    private void RollbackWake(EntityUid mapUid)
    {
        if (TerminatingOrDeleted(mapUid) ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) ||
            sector.State != NsvBluespaceSectorState.Waking)
        {
            return;
        }

        if (!_map.IsPaused(sector.MapId))
            _map.SetPaused(sector.MapId, true);
        SetLifecycleState(mapUid, sector, NsvBluespaceSectorState.Sleeping);
    }

    private void SetLifecycleState(
        EntityUid mapUid,
        NsvBluespaceSectorInstanceComponent sector,
        NsvBluespaceSectorState state)
    {
        var stateChanged = sector.State != state;
        sector.State = state;
        if (_registry.TryGetValue(mapUid, out var entry))
        {
            _registry[mapUid] = entry with
            {
                State = state,
                PendingArrivalCount = sector.PendingArrivals.Count,
                MustRunTaskBlockerCount = GetMustRunTaskBlockerCount(mapUid),
                SleepEligibleSince = null,
                SleepDeadline = null,
            };
        }

        if (stateChanged)
            SectorDisplayChanged?.Invoke(mapUid);
    }

    private void UpdateMustRunTaskBlockerCount(EntityUid mapUid)
    {
        if (_registry.TryGetValue(mapUid, out var entry))
        {
            _registry[mapUid] = entry with
            {
                MustRunTaskBlockerCount = GetMustRunTaskBlockerCount(mapUid),
            };
        }

        SectorDisplayChanged?.Invoke(mapUid);
    }

    private int GetMustRunTaskBlockerCount(EntityUid mapUid)
    {
        return _mustRunTaskBlockers.TryGetValue(mapUid, out var blockers) ? blockers.Count : 0;
    }

    private void PruneMustRunTaskBlockers()
    {
        foreach (var (mapUid, blockers) in _mustRunTaskBlockers.ToArray())
        {
            if (TerminatingOrDeleted(mapUid) || !HasComp<NsvBluespaceSectorInstanceComponent>(mapUid))
            {
                _mustRunTaskBlockers.Remove(mapUid);
                continue;
            }

            blockers.RemoveWhere(uid => TerminatingOrDeleted(uid));
            if (blockers.Count == 0)
                _mustRunTaskBlockers.Remove(mapUid);
        }
    }

    private static bool HasSleepBlocker(
        int activeLivingPlayers,
        int pendingArrivalCount,
        int mustRunTaskBlockerCount)
    {
        return activeLivingPlayers != 0 || pendingArrivalCount != 0 || mustRunTaskBlockerCount != 0;
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        HandlePlayerStatusChanged(args.Session, args.NewStatus);
    }

    internal void HandlePlayerStatusChanged(ICommonSession session, SessionStatus newStatus)
    {
        if (newStatus == SessionStatus.InGame)
        {
            _disconnectedCharacters.Remove(session.UserId);
            if (session.AttachedEntity is { } entity && IsActiveGameplayCharacter(entity))
                TryWakeForEntity(entity, NsvBluespaceSectorWakeReason.PlayerReconnect);
            return;
        }

        if (newStatus != SessionStatus.Disconnected)
            return;

        if (session.AttachedEntity is { } disconnectedEntity && IsActiveGameplayCharacter(disconnectedEntity))
        {
            _disconnectedCharacters[session.UserId] = new DisconnectedLivingCharacter(
                disconnectedEntity,
                _timing.CurTime);
        }
        else
        {
            _disconnectedCharacters.Remove(session.UserId);
        }
    }

    private void OnPlayerAttached(PlayerAttachedEvent args)
    {
        if (IsActiveGameplayCharacter(args.Entity))
            TryWakeForEntity(args.Entity, NsvBluespaceSectorWakeReason.PlayerReconnect);
    }

    private void OnActorParentChanged(Entity<ActorComponent> ent, ref EntParentChangedMessage args)
    {
        if (IsActiveGameplayCharacter(ent.Owner))
            TryWakeForEntity(ent.Owner, NsvBluespaceSectorWakeReason.EntityTransfer);
    }

    private void TryWakeForEntity(EntityUid entity, NsvBluespaceSectorWakeReason reason)
    {
        if (!TryComp<TransformComponent>(entity, out var transform) ||
            transform.MapUid is not { } mapUid ||
            !TryComp<NsvBluespaceSectorInstanceComponent>(mapUid, out var sector) ||
            sector.State is not (NsvBluespaceSectorState.PreparingSleep or
                NsvBluespaceSectorState.Sleeping or
                NsvBluespaceSectorState.Waking))
        {
            return;
        }

        RequestWake(mapUid, reason, entity, out _);
    }

    private void AggregateOnlinePlayers(Dictionary<EntityUid, SectorAggregation> aggregations)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;

            _disconnectedCharacters.Remove(session.UserId);
            if (session.AttachedEntity is not { } entity ||
                !IsActiveGameplayCharacter(entity) ||
                !TryGetSectorAggregation(entity, aggregations, out var aggregation))
            {
                continue;
            }

            aggregation.LivingPlayers.Add(session.UserId);
        }
    }

    private void AggregateDisconnectedPlayers(Dictionary<EntityUid, SectorAggregation> aggregations)
    {
        var now = _timing.CurTime;
        foreach (var (userId, disconnected) in _disconnectedCharacters.ToArray())
        {
            if (!IsWithinDisconnectedPlayerGrace(disconnected.DisconnectedAt, now) ||
                !IsActiveGameplayCharacter(disconnected.Entity))
            {
                _disconnectedCharacters.Remove(userId);
                continue;
            }

            if (TryGetSectorAggregation(disconnected.Entity, aggregations, out var aggregation))
                aggregation.LivingPlayers.Add(userId);
        }
    }

    private void AggregateAiShips(Dictionary<EntityUid, SectorAggregation> aggregations)
    {
        var query = EntityManager.AllEntityQueryEnumerator<NsvBluespaceShipAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var transform))
        {
            if (transform.MapUid is not { } mapUid ||
                transform.GridUid is not { } gridUid ||
                !aggregations.TryGetValue(mapUid, out var aggregation))
            {
                continue;
            }

            aggregation.AiShips.Add(gridUid);
        }
    }

    private void AggregatePlayerFactionShips(Dictionary<EntityUid, SectorAggregation> aggregations)
    {
        var query = EntityManager.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var gridUid, out _, out var transform))
        {
            if (transform.MapUid is not { } mapUid ||
                !aggregations.TryGetValue(mapUid, out var aggregation) ||
                !_factions.TryGetFaction(gridUid, out var faction) ||
                faction != PlayerFaction)
            {
                continue;
            }

            aggregation.HasPlayerFactionShip = true;
        }
    }

    private bool TryGetSectorAggregation(
        EntityUid entity,
        Dictionary<EntityUid, SectorAggregation> aggregations,
        out SectorAggregation aggregation)
    {
        if (TryComp<TransformComponent>(entity, out var transform) &&
            transform.MapUid is { } mapUid &&
            aggregations.TryGetValue(mapUid, out var found))
        {
            aggregation = found;
            return true;
        }

        aggregation = default!;
        return false;
    }

    /// <summary>
    /// Returns whether this entity is a physically present gameplay character whose simulation must remain active.
    /// Ghosts never qualify, including interactive admin observers. Entities without a mob state, such as preview
    /// observers, also do not qualify. Remote view subscriptions and eye targets are intentionally not considered.
    /// </summary>
    private bool IsActiveGameplayCharacter(EntityUid entity)
    {
        return !TerminatingOrDeleted(entity) &&
               !HasComp<GhostComponent>(entity) &&
               TryComp<MobStateComponent>(entity, out var mobState) &&
               mobState.CurrentState is MobState.Alive or MobState.Critical;
    }

    private bool IsWithinDisconnectedPlayerGrace(TimeSpan disconnectedAt, TimeSpan now)
    {
        return now - disconnectedAt < DisconnectedPlayerGrace;
    }

    private static TimeSpan CalculateSleepDelay(int activeAiShips, bool hasPlayerFactionShip)
    {
        var seconds = BaseSleepDelaySeconds +
                      Math.Min(activeAiShips, AiShipCountCap) * PerAiShipDelaySeconds +
                      (hasPlayerFactionShip ? PlayerShipDelaySeconds : 0);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinSleepDelaySeconds, MaxSleepDelaySeconds));
    }

    private sealed class SectorAggregation
    {
        public readonly HashSet<NetUserId> LivingPlayers = new();
        public readonly HashSet<EntityUid> AiShips = new();
        public bool HasPlayerFactionShip;
    }

    private readonly record struct DisconnectedLivingCharacter(
        EntityUid Entity,
        TimeSpan DisconnectedAt);
}
