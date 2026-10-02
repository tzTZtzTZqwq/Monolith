using Content.Server._NSV.GameRule;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._NSV.Administration;

/// <summary>
/// Admin verb to designate the campaign flagship: the targeted entity (typically a ship's main
/// console) becomes the entity whose destruction ends the round in defeat. A stopgap until there is
/// automatic player-ship entry. Unlike the faction verbs this does not resolve to the grid — it
/// marks the exact entity clicked, so a specific console can be the flagship.
/// </summary>
public sealed partial class NsvCampaignFlagshipVerbSystem : EntitySystem
{
    [Dependency] private IAdminManager _admin = default!;
    [Dependency] private NsvCampaignRuleSystem _campaign = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    private static readonly SpriteSpecifier VerbIcon =
        new SpriteSpecifier.Texture(new("/Textures/Interface/gavel.svg.192dpi.png"));

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

        // No campaign, no flagship: keep the verb out of the menu when it can't do anything.
        if (!_campaign.HasActiveCampaign())
            return;

        var target = args.Target;
        var session = actor.PlayerSession;

        if (_campaign.IsFlagship(target))
        {
            Verb clear = new()
            {
                Text = Loc.GetString("nsv-campaign-flagship-verb-clear"),
                Category = VerbCategory.Admin,
                Icon = VerbIcon,
                Impact = LogImpact.High,
                Act = () =>
                {
                    _campaign.ClearFlagship(target);
                    _popup.PopupCursor(Loc.GetString("nsv-campaign-flagship-cleared", ("target", Name(target))), session);
                },
            };
            args.Verbs.Add(clear);
            return;
        }

        Verb set = new()
        {
            Text = Loc.GetString("nsv-campaign-flagship-verb-set"),
            Category = VerbCategory.Admin,
            Icon = VerbIcon,
            Impact = LogImpact.High,
            Act = () =>
            {
                _campaign.SetFlagship(target);
                _popup.PopupCursor(Loc.GetString("nsv-campaign-flagship-designated", ("target", Name(target))), session);
            },
        };
        args.Verbs.Add(set);
    }
}
