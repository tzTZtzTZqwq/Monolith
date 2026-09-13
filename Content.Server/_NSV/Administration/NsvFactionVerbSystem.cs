using Content.Server._NSV.Bluespace.Sectors;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Administration;

/// <summary>
/// Admin verbs to set the NSV bluespace faction of a grid (or any entity). Targeting a tile or
/// anything on a ship resolves to that ship's grid. SetFaction requires the entity to be on a
/// sector map, so on other maps the verbs report the failure instead of silently doing nothing.
/// </summary>
public sealed class NsvFactionVerbSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly NsvBluespaceFactionSystem _factions = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private static readonly (string Label, string Faction)[] Options =
    {
        ("Federal", "NSVFederal"),
        ("Hostile", "NSVHostile"),
        ("Player", "NSVPlayer"),
        ("Neutral", "NSVNeutral"),
    };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetVerbsEvent<Verb>>(GetVerbs);
    }

    private void GetVerbs(GetVerbsEvent<Verb> args)
    {
        if (!TryComp(args.User, out ActorComponent? actor))
            return;

        if (!_admin.HasAdminFlag(actor.PlayerSession, AdminFlags.Admin))
            return;

        // Setting faction on the grid is what makes the whole ship hostile/friendly, so resolve
        // tile and entity targets to their grid first.
        var target = args.Target;
        if (Transform(target).GridUid is { } grid)
            target = grid;

        foreach (var (label, faction) in Options)
        {
            var proto = faction;
            Verb verb = new()
            {
                Text = Loc.GetString("nsv-faction-verb-set", ("faction", label)),
                Category = VerbCategory.Admin,
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/gavel.svg.192dpi.png")),
                Impact = LogImpact.Medium,
                Act = () =>
                {
                    if (_factions.SetFaction(target, proto))
                        return;

                    _popup.PopupCursor($"Cannot set faction: {Name(target)} is not on a bluespace sector map.", actor.PlayerSession);
                },
            };
            args.Verbs.Add(verb);
        }

        Verb clear = new()
        {
            Text = Loc.GetString("nsv-faction-verb-clear"),
            Category = VerbCategory.Admin,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/gavel.svg.192dpi.png")),
            Impact = LogImpact.Medium,
            Act = () =>
            {
                if (_factions.ClearFaction(target))
                    return;

                _popup.PopupCursor($"Cannot clear faction: {Name(target)} is not on a bluespace sector map.", actor.PlayerSession);
            },
        };
        args.Verbs.Add(clear);
    }
}
