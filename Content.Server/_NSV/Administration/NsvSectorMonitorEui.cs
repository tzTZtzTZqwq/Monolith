using System.Linq;
using Content.Server._NSV.Bluespace.Sectors;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._NSV.Administration;
using Content.Shared._NSV.Bluespace.Sectors;
using Content.Shared._NSV.CCVar;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using static Content.Shared._NSV.Administration.NsvSectorMonitorEuiMsg;

namespace Content.Server._NSV.Administration;

public sealed partial class NsvSectorMonitorEui : BaseEui
{
    [Dependency] private IAdminManager _adminManager = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IGameTiming _timing = default!;

    public NsvSectorMonitorEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        _adminManager.OnPermsChanged += OnPermsChanged;
        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        var lifecycle = _entityManager.System<NsvBluespaceSectorLifecycleSystem>();
        var rows = new List<(int MapId, NsvSectorMonitorRow Row)>();
        var totalSectorCount = 0;
        var activeSectorCount = 0;
        var query = _entityManager.AllEntityQueryEnumerator<NsvBluespaceSectorInstanceComponent>();
        while (query.MoveNext(out var uid, out var sector))
        {
            totalSectorCount++;
            if (IsActiveSectorState(sector.State))
                activeSectorCount++;

            var nameLocId = string.Empty;
            var nameFallback = sector.TemplateId.ToString();
            if (_prototype.TryIndex<NsvBluespaceSectorTemplatePrototype>(sector.TemplateId, out var template))
                nameLocId = template.Name.ToString();

            var mustRunTaskBlockers = 0;
            int? sleepHoldSeconds = null;
            if (lifecycle.TryGetRegistryEntry(uid, out var entry))
            {
                mustRunTaskBlockers = entry.MustRunTaskBlockerCount;
                if (entry.SleepDeadline is { } deadline)
                {
                    sleepHoldSeconds = Math.Max(
                        0,
                        (int) Math.Ceiling((deadline - _timing.CurTime).TotalSeconds));
                }
            }

            rows.Add(((int) sector.MapId, new NsvSectorMonitorRow(
                nameLocId,
                nameFallback,
                $"{uid} / Map {sector.MapId}",
                sector.State.ToString(),
                sector.OwnedGrids.Count,
                sector.OwnedEntities.Count,
                sector.ForeignGrids.Count,
                sector.PendingArrivals.Count,
                mustRunTaskBlockers,
                sleepHoldSeconds)));
        }

        return new NsvSectorMonitorEuiState(
            rows.OrderBy(row => row.MapId)
                .Select(row => row.Row)
                .ToArray(),
            totalSectorCount,
            _configuration.GetCVar(NsvCCVars.BluespaceSectorTotalSoftCapacity),
            activeSectorCount,
            _configuration.GetCVar(NsvCCVars.BluespaceSectorActiveSoftCapacity));
    }

    private static bool IsActiveSectorState(NsvBluespaceSectorState state)
    {
        return state is
            NsvBluespaceSectorState.Applying or
            NsvBluespaceSectorState.Ready or
            NsvBluespaceSectorState.PreparingSleep or
            NsvBluespaceSectorState.Waking;
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!_adminManager.HasAdminFlag(Player, AdminFlags.Admin))
        {
            Close();
            return;
        }

        if (msg is RefreshRequest)
            StateDirty();
    }

    public override void Closed()
    {
        base.Closed();
        _adminManager.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player && !_adminManager.HasAdminFlag(Player, AdminFlags.Admin))
            Close();
    }
}
