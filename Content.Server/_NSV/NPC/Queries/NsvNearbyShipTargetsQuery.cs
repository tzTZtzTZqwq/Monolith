using Content.Server.NPC.Queries.Queries;
using Content.Shared.Whitelist;

namespace Content.Server._NSV.NPC.Queries;

public sealed partial class NsvNearbyShipTargetsQuery : UtilityQuery
{
    [DataField]
    public float Range = 4000f;

    [DataField]
    public EntityWhitelist Blacklist = new();
}
