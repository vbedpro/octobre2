using UnityEngine;
using UnityEngine.UI;

// Lives on GuildStatus_NoGuild's "Join Random Guild" button. Picks a
// random guild from whatever's already loaded in the Guilds list, joins
// it, and opens its info (TEMPORARY — reusing GuildInfoPanelUI as a
// stand-in for GuildMemberInfoPanel until that prefab/script exists).
public class JoinRandomGuildButton : MonoBehaviour
{
    public Button button;
    public GuildsListUI guildsListUI;
    public LeaderboardUI leaderboardUI;
    public GuildInfoPanelUI infoPanel; // TEMPORARY stand-in — swap for GuildMemberInfoPanel later
    [Tooltip("GuildStatus_NoGuild itself — hidden the instant the panel opens optimistically (GuildStatusController normally only swaps this once the join actually confirms, which would otherwise leave it showing underneath the panel for however long the join takes), and restored if the join fails.")]
    public GameObject guildStatusNoGuildObject;

    void OnEnable()
    {
        if (button != null) button.onClick.AddListener(OnClick);
    }

    void OnDisable()
    {
        if (button != null) button.onClick.RemoveListener(OnClick);
    }

    async void OnClick()
    {
        if (GuildManager.Instance == null || guildsListUI == null) return;

        var guilds = guildsListUI.CurrentGuilds;
        if (guilds == null || guilds.Count == 0)
        {
            Debug.LogWarning("JoinRandomGuildButton: no guilds available to join.");
            return;
        }

        int index = Random.Range(0, guilds.Count);
        GuildManager.GuildInfo guild = guilds[index];
        int rank = index + 1; // list is already sorted, matches display rank

        Debug.Log($"[JoinRandomGuildButton] Picked guilds[{index}] out of {guilds.Count} — guildId='{guild.guildId}', guildName='{guild.guildName}', memberCount={guild.memberCount}");

        // Optimistic: disable immediately so the button visibly reacts to
        // the press instead of appearing dead during a Cloud Function cold
        // start. Also open the info panel right away with cached data and
        // empty member slots — it fills in for real below once the join
        // actually succeeds, same pattern as GuildMemberStatusUI's "My
        // Guild" button.
        if (button != null) button.interactable = false;
        if (guildStatusNoGuildObject != null) guildStatusNoGuildObject.SetActive(false);
        if (infoPanel != null)
            infoPanel.Show(rank, guild, new System.Collections.Generic.List<(int, LeaderboardUI.RowData)>());

        bool success = await GuildManager.Instance.JoinGuild(guild, leaderboardUI, rank);
        Debug.Log($"[JoinRandomGuildButton] JoinGuild returned success={success}. GuildManager now has MyGuildId='{GuildManager.Instance.MyGuildId}', MyGuildName='{GuildManager.Instance.MyGuildName}'");

        if (button != null) button.interactable = true;

        if (!success)
        {
            // Roll back — nothing actually happened, so close the panel we
            // optimistically opened rather than leave it showing a guild
            // the player never joined, and bring GuildStatus_NoGuild back.
            if (infoPanel != null) infoPanel.Close();
            if (guildStatusNoGuildObject != null) guildStatusNoGuildObject.SetActive(true);
            return;
        }

        // GuildStatusController already swaps to GuildStatus_GuildMember
        // automatically via OnMyGuildChanged — this just fills the panel
        // we already opened with the real member list.
        if (infoPanel != null)
        {
            var members = await GuildsListUI.GetMembersIncludingSelf(guild, leaderboardUI);
            infoPanel.PopulateMembersIfShowing(guild.guildId, members);
        }
    }
}
