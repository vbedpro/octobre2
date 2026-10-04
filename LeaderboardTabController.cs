using UnityEngine;
using UnityEngine.UI;

// Simple tab switcher for the leaderboard panel's content areas. Wire each
// tab button's OnClick to the matching Show...() method.
public class LeaderboardTabController : MonoBehaviour
{
    public GameObject contentGlobal;
    public GameObject contentOverview;
    public GameObject contentGuilds;

    [Header("Column Headers Per Tab")]
    public GameObject globalColumnsInfo;
    public GameObject guildsColumnsInfo;
    [Tooltip("Underneath the guild list's scrollview — holds whichever of the 3 GuildStatus prefabs applies. Active only while on the Guilds tab.")]
    public GameObject guildStatus;

    [Header("Background Color Per Tab")]
    public Image backgroundImage;
    public Color globalBackgroundColor = Color.white;
    public Color overviewBackgroundColor = new Color(0.05f, 0.05f, 0.15f);
    public Color guildsBackgroundColor = new Color(0.05f, 0.05f, 0.15f);

    [Header("Personal Row (shared slot — shows local player on Global, hidden on Overview until a dot is tapped)")]
    public GameObject personalRowObject;
    [Tooltip("Hides any dot-tap info still showing when the player switches back to the Global tab.")]
    public GlobeInfoPopup globeInfoPopup;

    [Header("Rows-Still-Loading Text (Global + Guilds tabs only)")]
    [Tooltip("Shown while the leaderboard is open AND the current tab's rows haven't finished loading yet (Global: LeaderboardUI.HasLoadedOnce; Guilds: GuildsListUI.HasLoadedOnce). Never shown on the Overview tab.")]
    public GameObject loadingText;
    [Tooltip("Needed to read HasLoadedOnce for the Global tab's player rows.")]
    public LeaderboardUI leaderboardUI;
    [Tooltip("Needed to read HasLoadedOnce for the Guilds tab's guild rows.")]
    public GuildsListUI guildsListUI;

    // Checked by LeaderboardUI's own refresh loop so it doesn't overwrite
    // the personal row's slot with local player data while the Overview
    // tab is using that same slot to display a tapped dot's info instead.
    public static bool overviewActive = false;
    // Same idea as overviewActive — lets other scripts (like LeaderBoardUI's
    // personal-row refresh) know the Guilds tab is showing, so they can
    // skip work/visibility meant only for Global.
    public static bool guildsActive = false;

    void Update()
    {
        RefreshLoadingText();
    }

    // Shown only while the leaderboard panel is actually open, AND only on
    // the Global or Guilds tab (never Overview), AND only until that tab's
    // own rows have finished their first load.
    void RefreshLoadingText()
    {
        if (loadingText == null) return;

        if (!LeaderboardUI.leaderboardOpen)
        {
            loadingText.SetActive(false);
            return;
        }

        bool shouldShow;
        if (overviewActive)
        {
            shouldShow = false;
        }
        else if (guildsActive)
        {
            shouldShow = guildsListUI == null || !guildsListUI.HasLoadedOnce;
        }
        else // Global tab
        {
            shouldShow = leaderboardUI == null || !leaderboardUI.HasLoadedOnce;
        }

        if (loadingText.activeSelf != shouldShow)
            loadingText.SetActive(shouldShow);
    }

    public void ShowGlobalTab()
    {
        GuildInfoPanelUI.CloseAllInstant();
        GuildCreationPanelUI.Instance?.CloseInstant();
        if (contentGlobal != null) contentGlobal.SetActive(true);
        if (contentOverview != null) contentOverview.SetActive(false);
        if (contentGuilds != null) contentGuilds.SetActive(false);
        if (backgroundImage != null) backgroundImage.color = globalBackgroundColor;
        if (globeInfoPopup != null) globeInfoPopup.Hide();
        if (personalRowObject != null) personalRowObject.SetActive(true);
        if (globalColumnsInfo != null) globalColumnsInfo.SetActive(true);
        if (guildsColumnsInfo != null) guildsColumnsInfo.SetActive(false);
        if (guildStatus != null) guildStatus.SetActive(false);
        overviewActive = false;
        guildsActive = false;
        RefreshLoadingText();
    }

    public void ShowOverviewTab()
    {
        GuildInfoPanelUI.CloseAllInstant();
        GuildCreationPanelUI.Instance?.CloseInstant();
        if (contentOverview != null) contentOverview.SetActive(true);
        if (contentGlobal != null) contentGlobal.SetActive(false);
        if (contentGuilds != null) contentGuilds.SetActive(false);
        if (backgroundImage != null) backgroundImage.color = overviewBackgroundColor;
        if (personalRowObject != null) personalRowObject.SetActive(false);
        if (globalColumnsInfo != null) globalColumnsInfo.SetActive(false);
        if (guildsColumnsInfo != null) guildsColumnsInfo.SetActive(false);
        if (guildStatus != null) guildStatus.SetActive(false);
        overviewActive = true;
        guildsActive = false;
        RefreshLoadingText();
    }

    public void ShowGuildsTab()
    {
        GuildInfoPanelUI.CloseAllInstant();
        GuildCreationPanelUI.Instance?.CloseInstant();
        if (contentGuilds != null) contentGuilds.SetActive(true);
        if (contentGlobal != null) contentGlobal.SetActive(false);
        if (contentOverview != null) contentOverview.SetActive(false);
        if (backgroundImage != null) backgroundImage.color = guildsBackgroundColor;
        if (globeInfoPopup != null) globeInfoPopup.Hide();
        if (personalRowObject != null) personalRowObject.SetActive(false);
        if (globalColumnsInfo != null) globalColumnsInfo.SetActive(false);
        if (guildsColumnsInfo != null) guildsColumnsInfo.SetActive(true);
        if (guildStatus != null) guildStatus.SetActive(true);
        overviewActive = false;
        guildsActive = true;
        RefreshLoadingText();
    }
}
