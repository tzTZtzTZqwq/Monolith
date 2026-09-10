using Content.Server._NSV.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;

namespace Content.Server._NSV.Shuttles;

/// <summary>
/// Folds the mass of anchored entities (walls, machines, anchored structures) into the grid body's
/// physics mass, so a more heavily built ship is physically heavier for FTL, propulsion and mobility.
///
/// The grid's <c>_mass</c> is engine-owned and recomputed as <c>Σ fixture.Density × fixture.Area</c>
/// on every fixture change, so the only stable content-side lever is the tile fixture density.
/// We track the summed <c>FixturesMass</c> of anchored children in <see cref="NsvGridAnchoredMassComponent"/>
/// and raise every tile fixture's density to <c>TileDensityMultiplier + AnchoredMass / totalArea</c>,
/// which makes <c>Σ density × area = 0.5 × area + AnchoredMass</c> — the base tile mass plus the anchored mass.
///
/// Anchor/re-anchor/tile events only mark the grid dirty; the actual recompute runs in <see cref="Update"/>.
/// Deferring avoids startup ordering (pre-anchored entities raise their event before physics fixtures are ready)
/// and lets us overwrite <see cref="ShuttleSystem"/>'s base density after it has run for the same fixture change.
/// </summary>
public sealed class NsvGridMassSystem : EntitySystem
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly FixtureSystem _fixtures = default!;

    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<MapGridComponent> _gridQuery;

    // Grids whose anchored-mass total must be rescanned from their children.
    private readonly HashSet<EntityUid> _massDirty = new();

    // Grids whose tile fixture density must be reapplied from the current anchored-mass total.
    private readonly HashSet<EntityUid> _densityDirty = new();

    public override void Initialize()
    {
        base.Initialize();

        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();

        SubscribeLocalEvent<TransformComponent, AnchorStateChangedEvent>(OnAnchorStateChanged);
        SubscribeLocalEvent<TransformComponent, ReAnchorEvent>(OnReAnchor);
        SubscribeLocalEvent<MapGridComponent, GridFixtureChangeEvent>(OnGridFixtureChange);
    }

    private void OnAnchorStateChanged(EntityUid uid, TransformComponent xform, ref AnchorStateChangedEvent args)
    {
        // On detach the parent is still set when this fires, so GridUid is valid; the child is gone by the
        // next Update so the rescan naturally drops its mass. Anchoring likewise just needs a rescan.
        var grid = args.Transform.GridUid;
        if (grid != null && _gridQuery.HasComp(grid.Value))
            _massDirty.Add(grid.Value);
    }

    private void OnReAnchor(EntityUid uid, TransformComponent xform, ref ReAnchorEvent args)
    {
        if (_gridQuery.HasComp(args.OldGrid))
            _massDirty.Add(args.OldGrid);
        if (_gridQuery.HasComp(args.Grid))
            _massDirty.Add(args.Grid);
    }

    private void OnGridFixtureChange(EntityUid uid, MapGridComponent grid, GridFixtureChangeEvent args)
    {
        // A tile change reset the affected fixtures to ShuttleSystem's base density; re-fold anchored mass in.
        if (HasComp<NsvGridAnchoredMassComponent>(uid))
            _densityDirty.Add(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_massDirty.Count == 0 && _densityDirty.Count == 0)
            return;

        foreach (var grid in _massDirty)
        {
            if (RecomputeMass(grid))
                _densityDirty.Add(grid);
        }
        _massDirty.Clear();

        foreach (var grid in _densityDirty)
            ReapplyDensity(grid);
        _densityDirty.Clear();
    }

    /// <summary>
    /// Rescans the grid's anchored children and stores the summed <c>FixturesMass</c>.
    /// Returns true if the grid is valid and its density should be reapplied.
    /// </summary>
    private bool RecomputeMass(EntityUid grid)
    {
        if (!_gridQuery.HasComp(grid) || !_xformQuery.TryComp(grid, out var gridXform))
            return false;

        var sum = 0.0;
        var enumerator = gridXform.ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (!_xformQuery.TryComp(child, out var childXform) || !childXform.Anchored)
                continue;

            if (_physicsQuery.TryComp(child, out var physics) && physics.FixturesMass > 0f)
                sum += physics.FixturesMass;
        }

        var comp = EnsureComp<NsvGridAnchoredMassComponent>(grid);
        comp.AnchoredMass = sum;
        return true;
    }

    /// <summary>
    /// Rewrites every tile fixture's density to fold the current anchored mass into the grid body mass.
    /// </summary>
    private void ReapplyDensity(EntityUid grid)
    {
        if (!TryComp<NsvGridAnchoredMassComponent>(grid, out var comp)
            || !TryComp<FixturesComponent>(grid, out var manager)
            || !_physicsQuery.TryComp(grid, out var body))
            return;

        var totalArea = 0f;
        foreach (var fixture in manager.Fixtures.Values)
        {
            var data = new MassData();
            FixtureSystem.GetMassData(fixture.Shape, ref data, 1f);
            totalArea += data.Mass;
        }

        var extra = totalArea > 0f ? (float) (comp.AnchoredMass / totalArea) : 0f;
        var density = ShuttleSystem.TileDensityMultiplier + extra;

        var changed = false;
        foreach (var (id, fixture) in manager.Fixtures)
        {
            if (fixture.Density == density)
                continue;

            _physics.SetDensity(grid, id, fixture, density, false, manager);
            changed = true;
        }

        if (changed)
            _fixtures.FixtureUpdate(grid, manager: manager, body: body);
    }
}
