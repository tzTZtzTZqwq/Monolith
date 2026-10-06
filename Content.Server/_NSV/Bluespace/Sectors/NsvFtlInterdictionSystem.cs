using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Robust.Shared.GameObjects;

namespace Content.Server._NSV.Bluespace.Sectors;

/// <summary>
/// Marks an entity (normally an enemy ship's AI core) as an FTL interdictor: while it exists, is
/// powered, and is hostile to a shuttle on the same map, that shuttle cannot jump out (NSV13
/// FTL-011/012). Putting it on the core rather than the grid means destroying or disabling the core
/// lifts the interdiction — "take out the interdictor before you can leave".
/// </summary>
[RegisterComponent]
public sealed partial class NsvFtlInterdictorComponent : Component
{
}

/// <summary>
/// Gates every way a shuttle can leave its map — bluespace navigation (via
/// <see cref="NsvBluespaceSectorTravelSystem"/>) and the plain shuttle console FTL — on the absence of
/// a live hostile <see cref="NsvFtlInterdictorComponent"/> on the same map.
/// </summary>
public sealed partial class NsvFtlInterdictionSystem : EntitySystem
{
    [Dependency] private NsvBluespaceFactionSystem _factions = default!;

    /// <summary>
    /// Raised with the map whose interdiction may have changed (an interdictor appeared or went away),
    /// so displays like the navigation console can refresh their jump availability.
    /// </summary>
    public event Action<EntityUid>? InterdictionChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnConsoleFtlAttempt, before: new[] { typeof(ShuttleSystem) });
        SubscribeLocalEvent<NsvFtlInterdictorComponent, ComponentStartup>(OnInterdictorChanged);
        SubscribeLocalEvent<NsvFtlInterdictorComponent, ComponentShutdown>(OnInterdictorChanged);
    }

    public override void Shutdown()
    {
        InterdictionChanged = null;
        base.Shutdown();
    }

    /// <summary>
    /// Whether <paramref name="shuttleUid"/> is currently held in place by a hostile interdictor on its
    /// map. An unpowered interdictor (a disabled core) doesn't count; neither does one on the shuttle
    /// itself, or one whose faction isn't hostile to the shuttle's.
    /// </summary>
    public bool IsInterdicted(EntityUid shuttleUid, out string? reason)
    {
        reason = null;
        if (Transform(shuttleUid).MapUid is not { } mapUid)
            return false;

        var query = AllEntityQuery<NsvFtlInterdictorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid != mapUid ||
                xform.GridUid == shuttleUid ||
                TerminatingOrDeleted(uid) ||
                !this.IsPowered(uid, EntityManager) ||
                !_factions.IsHostile(uid, shuttleUid))
            {
                continue;
            }

            reason = Loc.GetString("nsv-ftl-interdicted");
            return true;
        }

        return false;
    }

    private void OnConsoleFtlAttempt(ref ConsoleFTLAttemptEvent args)
    {
        if (args.Cancelled || !IsInterdicted(args.Uid, out var reason))
            return;

        args.Cancelled = true;
        args.Reason = reason!;
    }

    private void OnInterdictorChanged<T>(EntityUid uid, NsvFtlInterdictorComponent component, T args)
    {
        if (Transform(uid).MapUid is { } mapUid)
            InterdictionChanged?.Invoke(mapUid);
    }
}
