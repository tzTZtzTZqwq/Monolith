using Content.Server._NSV.Bluespace.Sectors;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._NSV.Bluespace.Strategy;

/// <summary>
/// The strategy layer's abstract (data-state) AI-vs-AI combat driver: the other previously-missing
/// caller of the registry's combat primitives. On a timer it finds dormant strategic nodes where two
/// mutually-hostile factions' data ships sit parked together and resolves one dice exchange between
/// them, so the world the player hasn't reached keeps grinding itself down. It only ever reads and
/// writes data-state records via the registry — never a live grid — and never touches campaign score
/// or threat: AI attrition between NPC factions is invisible to the player's scoreboard.
/// </summary>
public sealed class NsvAbstractCombatSystem : EntitySystem
{
    [Dependency] private NsvFleetRegistrySystem _fleets = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _protos = default!;
    [Dependency] private IRobustRandom _random = default!;

    private float _accumulator;

    public override void Update(float frameTime)
    {
        var interval = _cfg.GetCVar(NsvCCVars.StrategyCombatInterval);
        if (interval <= 0f)
            return;

        _accumulator += frameTime;
        if (_accumulator < interval)
            return;

        _accumulator %= interval;
        ResolveAll();
    }

    /// <summary>
    /// One combat pass: for every dormant node holding at least two parked ships, resolve a single
    /// exchange between a mutually-hostile pair. Internal so the integration test can drive a pass
    /// without pumping <see cref="Update"/>.
    /// </summary>
    internal void ResolveAll()
    {
        var byNode = new Dictionary<NsvFleetNodeKey, List<string>>();
        foreach (var ship in _fleets.Ships)
        {
            if (ship.State != NsvFleetShipState.Available || ship.DataNode is not { } node)
                continue;

            if (!byNode.TryGetValue(node, out var list))
                byNode[node] = list = new List<string>();

            list.Add(ship.Id);
        }

        if (byNode.Count == 0)
            return;

        var sectorStates = BuildSectorStates();
        foreach (var (node, ships) in byNode)
        {
            if (ships.Count < 2 || !CanResolveAt(node, sectorStates))
                continue;

            ResolveNode(ships);
        }
    }

    /// <summary>
    /// Node key -> lifecycle state for every existing sector instance, so combat can require a node be
    /// fully dormant (never-visited nodes have no entry; visited-then-slept nodes read Sleeping).
    /// </summary>
    private Dictionary<NsvFleetNodeKey, NsvBluespaceSectorState> BuildSectorStates()
    {
        var states = new Dictionary<NsvFleetNodeKey, NsvBluespaceSectorState>();
        var query = AllEntityQuery<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out _, out var sector))
            states[new NsvFleetNodeKey(sector.StarmapId, sector.NodeId)] = sector.State;

        return states;
    }

    /// <summary>
    /// Whether combat may resolve at <paramref name="node"/>: the registry invariant (no Live ship, no
    /// active encounter) must hold, and if the node has a materialized sector it must be fully Sleeping
    /// — a Ready or mid-transition sector is the live layer's to own, so we stay out.
    /// </summary>
    private bool CanResolveAt(NsvFleetNodeKey node, Dictionary<NsvFleetNodeKey, NsvBluespaceSectorState> sectorStates)
    {
        if (!_fleets.CanResolveAbstractCombat(node))
            return false;

        return !sectorStates.TryGetValue(node, out var state) || state == NsvBluespaceSectorState.Sleeping;
    }

    /// <summary>
    /// Resolves at most one exchange at a node: shuffles the ships for random pairing, then hits the
    /// first mutually-hostile pair found and stops. One exchange per node per tick keeps attrition
    /// gradual over several passes rather than wiping a node in one go.
    /// </summary>
    private void ResolveNode(List<string> ships)
    {
        _random.Shuffle(ships);
        for (var i = 0; i < ships.Count; i++)
        {
            for (var j = i + 1; j < ships.Count; j++)
            {
                if (!AreHostile(ships[i], ships[j]))
                    continue;

                ResolveExchange(ships[i], ships[j]);
                return;
            }
        }
    }

    /// <summary>
    /// One dice exchange: the side with the higher power-scaled roll wins and the loser takes a fixed
    /// fraction of its full complement in floors (at least one, so a fight never stalls). Ties break
    /// toward the higher-power side winning, so a turretless (zero-power) ship always loses — its score
    /// is a hard zero, and even when the armed side rolls a zero the power tie-break still sinks the
    /// turretless one. Two turretless ships cannot fight, so nothing happens. A loser reduced to zero
    /// integrity is destroyed.
    /// </summary>
    private void ResolveExchange(string a, string b)
    {
        var powerA = _fleets.GetDataCombatPower(a);
        var powerB = _fleets.GetDataCombatPower(b);
        if (powerA <= 0f && powerB <= 0f)
            return;

        var scoreA = powerA * _random.NextFloat();
        var scoreB = powerB * _random.NextFloat();
        string loser;
        if (scoreA > scoreB)
            loser = b;
        else if (scoreB > scoreA)
            loser = a;
        else
            loser = powerA <= powerB ? a : b; // exact tie: the weaker (lower-power) side loses

        if (!_fleets.TryGetShip(loser, out var loserShip))
            return;

        var fraction = _cfg.GetCVar(NsvCCVars.StrategyCombatDamageFraction);
        var floorsLost = Math.Max(1, (int) MathF.Ceiling(loserShip.FullComplementFloors.Count * fraction));
        _fleets.TryApplyAbstractDamage(loser, floorsLost, out _);

        if (_fleets.TryGetShip(loser, out var after) && after.DataTargetFloorCount <= 0)
            _fleets.TryDestroyDataShip(loser, out _);
    }

    private bool AreHostile(string a, string b)
    {
        if (!_fleets.TryGetShip(a, out var shipA) || !_fleets.TryGetShip(b, out var shipB) ||
            shipA.Faction is not { } factionA || shipB.Faction is not { } factionB)
        {
            return false;
        }

        return IsHostile(factionA, factionB) || IsHostile(factionB, factionA);
    }

    /// <summary>
    /// Faction hostility read straight from the faction prototype's relations, not the live faction
    /// system: parked ships sit on the faction-less holding map, so the sector faction resolver would
    /// see no factions at all. The record's opaque tag is the only faction authority here.
    /// </summary>
    private bool IsHostile(string source, string target)
    {
        return _protos.TryIndex<NsvBluespaceFactionPrototype>(source, out var proto) &&
               proto.Relations.TryGetValue(target, out var relation) &&
               relation == NsvBluespaceFactionRelation.Hostile;
    }
}
