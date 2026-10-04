using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// The slide-in panel showing one guild's full info: name, logo, rank,
// member list (reusing LeaderboardRowUI, full cosmetic effects included),
// and a scrollable History list.
//
// Simple slide-in/out, same pattern as your other panels (Achievements,
// Inventory) — it slides in from the right and sits on top of the guild
// rows, which naturally hides them without needing to move anything else.
//
// HISTORY DATA: GenerateFakeHistory() below is a placeholder — there's no
// real join/leave/contest event log yet. Swap it out once that system
// exists; everything downstream (the scrollable list, row population)
// already works off the same GuildHistoryEvent list either way.
public class GuildInfoPanelUI : MonoBehaviour
{
    // Multiple instances of this script can exist at once (GuildInfoPanel
    // for browsing, GuildMemberInfoPanel for "My Guild") — a single
    // singleton Instance would let the second Awake() silently overwrite
    // the first, breaking CloseAllInstant() for whichever one lost. Track
    // every instance instead, so tab switches / closing the leaderboard
    // can reliably close ALL of them, not just whichever claimed Instance.
    private static readonly List<GuildInfoPanelUI> allInstances = new List<GuildInfoPanelUI>();

    // Lets other scripts (e.g. GuildStatusController) check whether a
    // panel is currently open and intentionally hiding things, before
    // blindly re-showing them.
    public static bool IsAnyPanelOpen => allInstances.Any(p => p.IsOpen);

    [Tooltip("Check this ONLY on panel instances whose 'Objects To Hide While Open' list actually includes a GuildStatus object (i.e. GuildMemberInfoPanel, not the browsing GuildInfoPanel) \u2014 lets GuildStatusController know specifically when it's safe/unsafe to update, instead of blocking on ANY panel being open.")]
    public bool hidesGuildStatus = false;

    // True only while a panel that ACTUALLY hides GuildStatus is open —
    // NOT true just because some other panel (like the browsing
    // GuildInfoPanel, which never touches GuildStatus) happens to be open.
    public static bool IsGuildStatusHiddenByOpenPanel => allInstances.Any(p => p.IsOpen && p.hidesGuildStatus);
    public bool IsOpen { get; private set; } = false;

    [Header("Panel Slide")]
    public RectTransform panelRect;
    public Vector2 openPosition;   // panel's on-screen position
    public Vector2 closedPosition; // panel's off-screen (right) position
    public float slideDuration = 0.35f;
    public GameObject panelObject;
    [Tooltip("Every one of these is hidden while this panel is open, and restored when closed normally (back button) IF the Guilds tab is still active. E.g. the columns header, and (for GuildMemberInfoPanel specifically) GuildStatus_GuildMember too.")]
    public List<GameObject> objectsToHideWhileOpen = new List<GameObject>();

    [Header("Header")]
    public TextMeshProUGUI guildNameText;
    public TextMeshProUGUI rankText;
    [Tooltip("Flat-color background image sitting BEHIND the shape icon — sprite selected per guild (NOT a color tint).")]
    public Image logoBackgroundImage;
    [Tooltip("The shape icon on top of the background — sprite changes per guild too.")]
    public Image logoShapeIcon;
    [Tooltip("Sprites in the SAME ORDER as GuildManager.GuildLogoShape (Circle, Square, Triangle, Star, Diamond).")]
    public Sprite[] shapeSprites;
    [Tooltip("Sprites in the SAME ORDER as GuildManager.GuildLogoBackground.")]
    public Sprite[] backgroundSprites;

    [Header("Members (max 5, not scrollable — pre-placed slots, hidden if unused)")]
    public LeaderboardRowUI[] memberRowSlots = new LeaderboardRowUI[5];
    [Tooltip("Pre-placed 'empty slot' rows, SAME LENGTH/ORDER as Member Row Slots \u2014 slot i shows this instead of memberRowSlots[i] whenever the guild has an open spot there.")]
    public GameObject[] emptySlotRowObjects = new GameObject[5];

    [Header("Stats (below the member rows)")]
    public TextMeshProUGUI contestsWonText;

    [Header("History")]
    public Transform historyContent; // simple Vertical Layout Group container — no virtualization needed at this scale
    public GuildHistoryEventRowUI historyRowPrefab;
    [Tooltip("Sprites in the SAME ORDER as GuildEventType (MemberJoined, MemberLeft, ContestStarted, ContestWon, ContestLost, ContestRewardGranted).")]
    public Sprite[] eventIcons;

    [Header("Back Button")]
    public Button backButton;

    [Header("Join Button (GuildInfoPanel only — always visible, greyed/unclickable when not joinable)")]
    public Button joinButton;
    [Tooltip("Tinted grey/normal based on whether joining is currently possible. Can be the same Image the Button targets.")]
    public Image joinButtonBackground;
    public Color joinButtonEnabledColor = Color.white;
    public Color joinButtonDisabledColor = Color.gray;
    [Tooltip("Needed to check the guild's real member count before allowing a join.")]
    public LeaderboardUI leaderboardUI;
    [Tooltip("Lets this panel immediately update the browse list's member count/totals right after a join/leave, instead of waiting for the next periodic refresh.")]
    public GuildsListUI guildsListUI;

    [Header("Leave Button (GuildMemberInfoPanel only)")]
    public Button leaveButton;

    private GuildManager.GuildInfo activeGuild;
    private int activeRank;
    private int activeMemberCount;

    private Coroutine slideCoroutine;
    private readonly List<GuildHistoryEventRowUI> historyPool = new List<GuildHistoryEventRowUI>();

    public enum GuildEventType
    {
        MemberJoined,
        MemberLeft,
        ContestStarted,
        ContestWon,
        ContestLost,
        ContestRewardGranted
    }

    [System.Serializable]
    public class GuildHistoryEvent
    {
        public GuildEventType type;
        public string description;
        public System.DateTime date;
    }

    void Awake()
    {
        allInstances.Add(this);

        if (panelObject != null) panelObject.SetActive(false);
        if (panelRect != null) panelRect.anchoredPosition = closedPosition;
        if (backButton != null) backButton.onClick.AddListener(Close);
        if (joinButton != null) joinButton.onClick.AddListener(OnJoinPressed);
        if (leaveButton != null) leaveButton.onClick.AddListener(OnLeavePressed);
    }

    void OnDestroy()
    {
        allInstances.Remove(this);
    }

    // slidingContent param removed — the panel no longer moves anything
    // else, it just slides in on top and covers the rows naturally.
    public void Show(int rank, GuildManager.GuildInfo guild, List<(int rank, LeaderboardUI.RowData row)> members)
    {
        // Opening one of these panels (GuildInfoPanel / GuildMemberInfoPanel)
        // closes the other one instantly if it happened to be open —
        // they're never meant to be stacked on top of each other.
        // Uses the normal animated Close() (not CloseInstant) — both
        // slides run at the same time, finishing together since they
        // share the same slideDuration, instead of the other panel
        // snapping shut instantly.
        foreach (GuildInfoPanelUI other in allInstances)
            if (other != this && other.IsOpen) other.Close();

        // Activate FIRST, then populate. TMP text components that haven't
        // run Awake()/OnEnable() yet (i.e. this panel has never been shown
        // before) can throw a NullReferenceException in UpdateVertexData
        // if .text is assigned while still inactive — this is exactly what
        // caused "GuildInfoPanel sometimes won't open," and why opening
        // your OWN guild's panel first (which warms up the same shared TMP
        // components) made the very next open work fine.
        if (panelObject != null) panelObject.SetActive(true);

        activeGuild = guild;
        activeRank = rank;
        activeMemberCount = members.Count;
        RefreshJoinButton();

        // ---- Header ----
        if (guildNameText != null) guildNameText.text = guild.guildName;
        if (rankText != null) rankText.text = "#" + Mathf.Max(rank, 1);
        SetLogo(guild.logoShape, guild.logoColor, guild.logoIconTintColor);
        if (contestsWonText != null) contestsWonText.text = guild.contestsWon.ToString();

        // ---- Members (highest-ranked first — members list is already in
        // that order coming from GuildManager.GetGuildMembers, since it
        // scans the top list top-down) ----
        PopulateMembers(members);

        // ---- History (placeholder) ----
        PopulateHistory(GenerateFakeHistory(guild.guildId));

        foreach (GameObject obj in objectsToHideWhileOpen)
            if (obj != null) obj.SetActive(false);
        IsOpen = true;

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideTo(openPosition));
    }

    public void Close()
    {
        IsOpen = false;
        // Only restore these if we're still actually on the Guilds tab —
        // if a tab switch already happened, that tab's own Show...Tab()
        // call already manages their visibility.
        if (LeaderboardTabController.guildsActive)
        {
            foreach (GameObject obj in objectsToHideWhileOpen)
                if (obj != null) obj.SetActive(true);

            // The restore above is a blind "turn everything back on,"
            // which can incorrectly re-show GuildStatus_GuildMember even
            // if the player just LEFT their guild while this panel was
            // open (GuildStatusController already correctly switched to
            // NoGuild via the leave event — this just re-asserts that
            // correct state in case the blind restore above clobbered it).
            GuildStatusController.Instance?.Refresh();
        }

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideToThenDeactivate(closedPosition));
    }

    // Called when switching tabs, or closing the whole leaderboard panel,
    // while this panel happens to be open — snaps it shut immediately
    // instead of playing the slide animation. Safe to call even if the
    // panel isn't currently open (no-op).
    public void CloseInstant()
    {
        if (!IsOpen) return;

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);

        if (panelRect != null) panelRect.anchoredPosition = closedPosition;
        if (panelObject != null) panelObject.SetActive(false);

        IsOpen = false;
    }

    // Closes EVERY GuildInfoPanelUI instance that's currently open (both
    // GuildInfoPanel and GuildMemberInfoPanel) — use this instead of
    // calling CloseInstant() on a single Instance reference, since there
    // isn't just one anymore.
    public static void CloseAllInstant()
    {
        foreach (GuildInfoPanelUI panel in allInstances)
            panel.CloseInstant();
    }

    // ---- Join / Leave ----

    // Always visible (per design), just toggles interactable + tint:
    //   - Player already in ANY guild -> always grey/unclickable, regardless
    //     of whether THIS guild is full.
    //   - Player has no guild, and this guild is full -> grey/unclickable.
    //   - Player has no guild, and this guild has room -> clickable.
    void RefreshJoinButton()
    {
        if (joinButton == null) return;

        bool playerHasGuild = GuildManager.Instance != null && GuildManager.Instance.HasGuild;
        bool meetsRequirement = GuildManager.Instance != null && GuildManager.Instance.MeetsBigBangRequirement();
        int maxMembers = GuildManager.Instance != null ? GuildManager.Instance.maxMembers : 5;
        bool guildFull = activeMemberCount >= maxMembers;
        bool canJoin = meetsRequirement && !playerHasGuild && !guildFull;

        joinButton.interactable = canJoin;
        if (joinButtonBackground != null)
            joinButtonBackground.color = canJoin ? joinButtonEnabledColor : joinButtonDisabledColor;
    }

    async void OnJoinPressed()
    {
        if (activeGuild == null || GuildManager.Instance == null) return;

        // Optimistic: disable immediately so the button visibly reacts to
        // the press instead of appearing to do nothing during a Cloud
        // Function cold start. RefreshJoinButton() below restores the
        // correct state either way (grants access on success, or reverts
        // to clickable on failure).
        if (joinButton != null) joinButton.interactable = false;

        bool success = await GuildManager.Instance.JoinGuild(activeGuild, leaderboardUI, activeRank);
        if (success)
        {
            // Re-fetch and repopulate immediately — otherwise the panel
            // keeps showing its pre-join snapshot until closed/reopened.
            var freshMembers = await GuildsListUI.GetMembersIncludingSelf(activeGuild, leaderboardUI);
            activeMemberCount = freshMembers.Count;
            PopulateMembers(freshMembers);
            RefreshJoinButton();

            guildsListUI?.RefreshSingleGuildTotals(activeGuild.guildId);
        }
        else
        {
            Debug.LogWarning("GuildInfoPanelUI: join failed — reverting button.");
            RefreshJoinButton();
        }
    }

    // Used by callers that open this panel optimistically (with an empty
    // placeholder member list) and fill the real list in once it's
    // fetched. Guards against a slower, stale fetch overwriting a
    // DIFFERENT guild the player has since opened (e.g. rapid taps between
    // two guilds) by checking this is still the guild being shown.
    public void PopulateMembersIfShowing(string guildId, List<(int rank, LeaderboardUI.RowData row)> members)
    {
        if (!IsOpen || activeGuild == null || activeGuild.guildId != guildId) return;
        activeMemberCount = members.Count;
        PopulateMembers(members);
        RefreshJoinButton();
    }

    void PopulateMembers(List<(int rank, LeaderboardUI.RowData row)> members)
    {
        for (int i = 0; i < memberRowSlots.Length; i++)
        {
            bool occupied = i < members.Count;

            if (memberRowSlots[i] != null)
            {
                memberRowSlots[i].gameObject.SetActive(occupied);
                if (occupied)
                {
                    var (memberRank, row) = members[i];
                    memberRowSlots[i].Set(memberRank, CountryFlagDatabase.GetFlag(row.country), row.playerName, row.bigBangCount, row.lifetimeRabbits, false, row.equippedNickname, row.equippedBackground, row.nicknameColorIndex);
                }
            }

            if (i < emptySlotRowObjects.Length && emptySlotRowObjects[i] != null)
                emptySlotRowObjects[i].SetActive(!occupied);
        }
    }

    void OnLeavePressed()
    {
        if (GuildManager.Instance == null) return;

        string leftGuildId = activeGuild != null ? activeGuild.guildId : null;
        GuildManager.Instance.LeaveGuild();

        if (!string.IsNullOrEmpty(leftGuildId))
            guildsListUI?.RefreshSingleGuildTotals(leftGuildId);

        Close(); // this guild no longer applies to the player — close the panel
    }

    // Right-click this component's header in the Inspector (or use the ⋮
    // menu) while this panel is open and showing a guild, to populate
    // whichever guild you're CURRENTLY VIEWING with 1-4 random fake
    // players — persists across restarts. Only has an effect on real
    // guilds (not the procedural "fake_" browse guilds, which already
    // have their own deterministic fake members).
    [ContextMenu("DEBUG: Populate Currently Viewed Guild With Fake Players")]
    async void PopulateCurrentGuildWithFakePlayers()
    {
        if (activeGuild == null || string.IsNullOrEmpty(activeGuild.guildId))
        {
            Debug.LogWarning("GuildInfoPanelUI: no guild currently being viewed — open a guild's panel first, then try again.");
            return;
        }

        GuildDevTools.PopulateGuildWithFakePlayers(activeGuild.guildId);

        // Repaint with the newly persisted fakes (+ self, if this happens
        // to be the player's own guild) — same refresh path as joining.
        var freshMembers = await GuildsListUI.GetMembersIncludingSelf(activeGuild, leaderboardUI);
        activeMemberCount = freshMembers.Count;
        PopulateMembers(freshMembers);
        RefreshJoinButton();

        Debug.Log($"GuildInfoPanelUI: populated '{activeGuild.guildName}' with fake players.");
    }

    void SetLogo(string shapeName, string backgroundName, string iconTintHex)
    {
        if (System.Enum.TryParse(shapeName, out GuildManager.GuildLogoShape shape))
        {
            int index = (int)shape;
            if (logoShapeIcon != null && shapeSprites != null && index >= 0 && index < shapeSprites.Length)
            {
                logoShapeIcon.sprite = shapeSprites[index];
                logoShapeIcon.color = (!string.IsNullOrEmpty(iconTintHex) && ColorUtility.TryParseHtmlString(iconTintHex, out Color tint)) ? tint : Color.white;
            }
        }

        if (System.Enum.TryParse(backgroundName, out GuildManager.GuildLogoBackground bg))
        {
            int index = (int)bg;
            if (logoBackgroundImage != null && backgroundSprites != null && index >= 0 && index < backgroundSprites.Length)
            {
                logoBackgroundImage.sprite = backgroundSprites[index];
                logoBackgroundImage.color = Color.white;
            }
        }
    }

    // ---- History population (real rendering — swap the DATA source
    // later, not this part) ----
    void PopulateHistory(List<GuildHistoryEvent> events)
    {
        // History is paused until the news feed system exists — skip
        // gracefully instead of crashing (and blocking the whole panel
        // from opening) if the prefab isn't assigned right now.
        if (historyRowPrefab == null) return;

        while (historyPool.Count < events.Count)
        {
            GuildHistoryEventRowUI r = Instantiate(historyRowPrefab, historyContent);
            historyPool.Add(r);
        }

        for (int i = 0; i < historyPool.Count; i++)
        {
            if (i < events.Count)
            {
                GuildHistoryEvent e = events[i];
                Sprite icon = (eventIcons != null && (int)e.type < eventIcons.Length) ? eventIcons[(int)e.type] : null;
                historyPool[i].gameObject.SetActive(true);
                historyPool[i].Set(icon, e.description, e.date);
            }
            else
            {
                historyPool[i].gameObject.SetActive(false);
            }
        }
    }

    // ---- TEMPORARY fake history — deterministic per guild (seeded by
    // guildId) so the same guild shows the same placeholder history every
    // time you reopen it, instead of randomizing on every click. ----
    List<GuildHistoryEvent> GenerateFakeHistory(string guildId)
    {
        List<GuildHistoryEvent> events = new List<GuildHistoryEvent>();
        System.Random rng = new System.Random(guildId != null ? guildId.GetHashCode() : 0);

        int count = rng.Next(6, 16);
        System.DateTime cursor = System.DateTime.Today;

        string[] fakeNames = { "Alex", "Sam", "Jordan", "Casey", "Riley", "Morgan" };

        for (int i = 0; i < count; i++)
        {
            cursor = cursor.AddDays(-rng.Next(1, 10));
            GuildEventType type = (GuildEventType)rng.Next(0, System.Enum.GetValues(typeof(GuildEventType)).Length);
            string name = fakeNames[rng.Next(fakeNames.Length)];

            string description = type switch
            {
                GuildEventType.MemberJoined => $"{name} joined the guild",
                GuildEventType.MemberLeft => $"{name} left the guild",
                GuildEventType.ContestStarted => "A new contest has started",
                GuildEventType.ContestWon => "The guild won a contest!",
                GuildEventType.ContestLost => "The guild lost a contest",
                GuildEventType.ContestRewardGranted => "Contest reward granted",
                _ => "Guild event"
            };

            events.Add(new GuildHistoryEvent { type = type, description = description, date = cursor });
        }

        return events;
    }

    // ---- Slide (mirrors AchievementsPanelUI's smoothstep ease) ----
    IEnumerator SlideTo(Vector2 target)
    {
        if (panelRect == null) yield break;

        Vector2 start = panelRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / slideDuration;
            t = t * t * (3f - 2f * t); // smoothstep
            panelRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }
        panelRect.anchoredPosition = target;
    }

    IEnumerator SlideToThenDeactivate(Vector2 target)
    {
        yield return SlideTo(target);
        if (panelObject != null) panelObject.SetActive(false);
    }
}
