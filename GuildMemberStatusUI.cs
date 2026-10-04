using UnityEngine;
using TMPro;
using System.Collections.Generic;

// Lives on GuildStatus_GuildMember. Populates the player's own guild's
// name/rank/contests-won, and opens the (currently reused GuildInfoPanelUI,
// standing in for the not-yet-built GuildMemberInfoPanel) "My Guild"
// button.
public class GuildMemberStatusUI : MonoBehaviour
{
    [Header("Text")]
    public TextMeshProUGUI guildNameText;
    public TextMeshProUGUI guildRankText;
    public TextMeshProUGUI contestsWonText;

    [Header("My Guild Button")]
    public UnityEngine.UI.Button myGuildButton;
    [Tooltip("Small warning icon next to myGuildButton — visible only while the player's guild is in an ongoing contest, hidden otherwise.")]
    public GameObject ongoingContestWarningIcon;

    [Header("Panel (TEMPORARY — reusing GuildInfoPanelUI until GuildMemberInfoPanel exists)")]
    public GuildInfoPanelUI infoPanel;
    public LeaderboardUI leaderboardUI;

    void OnEnable()
    {
        Refresh();
        if (GuildManager.Instance != null)
        {
            GuildManager.Instance.OnMyGuildChanged += Refresh;
            // Entering/leaving an ongoing contest doesn't necessarily fire
            // OnMyGuildChanged, so the warning icon also needs this one —
            // same pair GuildSearchContestUI listens to for contest state.
            GuildManager.Instance.OnSearchingForContestChanged += Refresh;
        }

        if (myGuildButton != null)
            myGuildButton.onClick.AddListener(OpenMyGuild);
    }

    void OnDisable()
    {
        if (GuildManager.Instance != null)
        {
            GuildManager.Instance.OnMyGuildChanged -= Refresh;
            GuildManager.Instance.OnSearchingForContestChanged -= Refresh;
        }

        if (myGuildButton != null)
            myGuildButton.onClick.RemoveListener(OpenMyGuild);
    }

    void Refresh()
    {
        if (GuildManager.Instance == null || !GuildManager.Instance.HasGuild)
        {
            if (ongoingContestWarningIcon != null) ongoingContestWarningIcon.SetActive(false);
            return;
        }

        if (guildNameText != null) guildNameText.text = GuildManager.Instance.MyGuildName;
        if (guildRankText != null) guildRankText.text = "#" + Mathf.Max(GuildManager.Instance.MyGuildRank, 1);
        if (contestsWonText != null) contestsWonText.text = GuildManager.Instance.MyGuildContestsWon.ToString();
        // Logo (shape + background) is owned by GuildSearchContestUI now,
        // not here — this script used to also set it, which was writing
        // conflicting sprites onto the same Image object.

        // Visible only while actually in an ongoing contest — NOT during
        // the 24-hour Results phase afterward (that's a resolved contest,
        // nothing left to warn about), and not while merely searching.
        if (ongoingContestWarningIcon != null) ongoingContestWarningIcon.SetActive(GuildManager.Instance.MyInContest);
    }

    async void OpenMyGuild()
    {
        if (GuildManager.Instance == null || !GuildManager.Instance.HasGuild || infoPanel == null) return;

        GuildManager.GuildInfo guild = new GuildManager.GuildInfo
        {
            guildId = GuildManager.Instance.MyGuildId,
            guildName = GuildManager.Instance.MyGuildName,
            logoShape = GuildManager.Instance.MyGuildLogoShape,
            logoColor = GuildManager.Instance.MyGuildLogoColor,
            logoIconTintColor = GuildManager.Instance.MyGuildLogoIconTintColor,
            contestsWon = GuildManager.Instance.MyGuildContestsWon,
            memberCount = GuildManager.Instance.MyGuildMemberCount
        };

        // Optimistic: open the panel INSTANTLY with whatever's already
        // cached (name, logo, rank, contests won — all local, no network
        // call needed for any of it), with empty member slots for a
        // moment, then fill the real member list in once the fetch below
        // comes back. Feels instant instead of waiting on that fetch
        // before the panel even appears.
        infoPanel.Show(GuildManager.Instance.MyGuildRank, guild, new List<(int, LeaderboardUI.RowData)>());

        // TEMPORARY: same reasoning as GuildsListUI — fake guilds have no
        // real players tagged with their guildId yet. GetMembersIncludingSelf
        // swaps in the local player's real data (this IS their own guild,
        // so it always applies here).
        var members = await GuildsListUI.GetMembersIncludingSelf(guild, leaderboardUI);
        infoPanel.PopulateMembersIfShowing(guild.guildId, members);
    }
}
