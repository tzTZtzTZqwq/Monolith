namespace Content.Server._NSV.GameRule.Components;

/// <summary>
/// Marks a ship system the campaign can't survive losing. While it sits on the campaign flagship, if
/// every system of its <see cref="Group"/> is destroyed or unpowered for <see cref="GracePeriod"/>
/// seconds, the campaign is lost. Restoring power to one (or installing a replacement) in time cancels
/// the countdown. Put it on any entity prototype to make that system critical — e.g. the bluespace
/// jump core.
/// </summary>
[RegisterComponent]
public sealed partial class NsvCampaignCriticalSystemComponent : Component
{
    /// <summary>
    /// Systems sharing a group back each other up: the group is online while any one of them is.
    /// </summary>
    [DataField]
    public string Group = "default";

    /// <summary>
    /// How the system is named in the countdown announcements.
    /// </summary>
    [DataField]
    public LocId Name = "nsv-campaign-critical-default";

    /// <summary>
    /// Seconds the group may stay offline before the campaign is lost.
    /// </summary>
    [DataField]
    public float GracePeriod = 180f;
}

/// <summary>
/// Per-campaign countdown for one critical group on the flagship. Created the first time a member of
/// the group is seen aboard, so a group whose last member was destroyed is still tracked.
/// </summary>
public sealed class NsvCampaignCriticalState
{
    public LocId Name;
    public float GracePeriod;

    // Seconds offline, or null while the group is online.
    public float? OfflineTime;

    // Whether the current outage has been announced (outages shorter than a power tick aren't).
    public bool OfflineAnnounced;

    // Whether the final-minute warning has gone out for the current outage.
    public bool FinalWarningSent;
}
