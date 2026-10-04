using UnityEngine;

// Lives on the "GuildStatus" GameObject (underneath the Guilds list
// scrollview). Switches between the 3 pre-placed status prefab instances
// based on the player's current state:
//   - GuildStatus_Locked        — hasn't met the BigBang requirement yet
//   - GuildStatus_NoGuild       — unlocked, but not currently in a guild
//   - GuildStatus_GuildMember   — currently in a guild
//
// Refreshes whenever this becomes active (i.e. every time the Guilds tab
// opens, since GuildStatus itself is only active on that tab — see
// LeaderboardTabController) AND live, immediately, whenever the player's
// guild changes (join/leave/create) while already sitting on this tab.
public class GuildStatusController : MonoBehaviour
{
    public static GuildStatusController Instance { get; private set; }

    [Header("The 3 pre-placed prefab instances, as children of this GameObject")]
    public GameObject lockedStatus;
    public GameObject noGuildStatus;
    public GameObject guildMemberStatus;

    void Awake()
    {
        Instance = this;
    }

    void OnEnable()
    {
        Refresh();
        if (GuildManager.Instance != null)
            GuildManager.Instance.OnMyGuildChanged += Refresh;
    }

    void OnDisable()
    {
        if (GuildManager.Instance != null)
            GuildManager.Instance.OnMyGuildChanged -= Refresh;
    }

    public void Refresh()
    {
        // Skip only while a panel that ACTUALLY hides GuildStatus is open
        // (GuildMemberInfoPanel) — NOT just because some other panel (like
        // the browsing GuildInfoPanel, which never touches GuildStatus at
        // all) happens to be open. The earlier broader check incorrectly
        // blocked GuildStatus from updating immediately after joining a
        // guild via the browsing panel, where GuildStatus was visible and
        // should update the instant the join succeeds.
        if (GuildInfoPanelUI.IsGuildStatusHiddenByOpenPanel) return;

        bool unlocked = GuildManager.Instance != null && GuildManager.Instance.MeetsBigBangRequirement();
        bool hasGuild = GuildManager.Instance != null && GuildManager.Instance.HasGuild;

        if (lockedStatus != null) lockedStatus.SetActive(!unlocked);
        if (noGuildStatus != null) noGuildStatus.SetActive(unlocked && !hasGuild);
        if (guildMemberStatus != null) guildMemberStatus.SetActive(unlocked && hasGuild);
    }
}
