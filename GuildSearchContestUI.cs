using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

// Wire this on BOTH GuildStatus_GuildMember and GuildMemberInfoPanel.
// Three modes, exactly one visible at a time:
//   - Passive:   Search Contest button
//   - Searching: Looking For Contest image
//   - Ongoing:   Ongoing Contest image (+ on GuildMemberInfoPanel only,
//                the rival guild's member list — leave those fields empty
//                on GuildStatus_GuildMember, it's too small for rows)
//
// Sync model: MIGRATED to Firebase. GuildManager attaches a real-time
// Firestore listener on the player's own guild document, so every field
// this UI reads off GuildManager.Instance (MySearchingForContest,
// MyInContest, MyContestOpponentGuildId, etc.) updates itself the instant
// ANY member's device changes it — no polling needed for search/match/
// contest state anymore. RivalMembersRefreshLoop below still polls, but
// only for the rival roster display (member list changes), on the same
// cadence as the leaderboard.
public class GuildSearchContestUI : MonoBehaviour
{
    [Header("Passive / Searching / Ongoing")]
    public Button searchContestButton;
    [Tooltip("Shows the remaining Results-phase countdown while the Search Contest button is greyed out during Results. Hidden the rest of the time — there's no other cooldown.")]
    public TextMeshProUGUI cooldownText;
    public GameObject lookingForContestImage;
    public GameObject ongoingContestImage;
    [Tooltip("Any other elements (e.g. a label) that should ALSO hide specifically during Ongoing Contest, alongside the button and Looking For Contest image.")]
    public List<GameObject> alsoHiddenDuringContest = new List<GameObject>();
    [Tooltip("Visible ONLY in Passive mode (alongside the Search Contest button).")]
    public GameObject passiveModeText;
    [Tooltip("Visible ONLY in Searching mode (alongside the Looking For Contest image).")]
    public GameObject searchingModeText;

    [Header("Friendly Guild Header (above your own members container)")]
    [Tooltip("Flat-color background image sitting BEHIND the shape icon — tinted with the guild's logoColor.")]
    public Image friendlyGuildLogoBackground;
    public Image friendlyGuildLogoIcon;
    public TMPro.TextMeshProUGUI friendlyGuildNameText;
    [Tooltip("Sprites in the SAME ORDER as GuildManager.GuildLogoShape (Circle, Square, Triangle, Star, Diamond) \u2014 shared with the rival header below.")]
    public Sprite[] shapeSprites;
    [Tooltip("Sprites in the SAME ORDER as GuildManager.GuildLogoBackground \u2014 shared with the rival header below.")]
    public Sprite[] backgroundSprites;

    [Header("Rival Guild Header (above the rival members container, ongoing contest only)")]
    [Tooltip("Flat-color background image sitting BEHIND the shape icon — tinted with the rival guild's logoColor.")]
    public Image rivalGuildLogoBackground;
    public Image rivalGuildLogoIcon;
    public TMPro.TextMeshProUGUI rivalGuildNameText;
    [Tooltip("Any other rival-related element (e.g. a 'Rival Guild' label) that should only be visible during an ongoing contest, same as the logo/name above.")]
    public GameObject rivalGuildLabelText;

    [Header("Friendly Participants (ongoing contest only — GuildMemberInfoPanel only)")]
    public GameObject membersContainerFriendlyContest;

    [Header("Contest Objective (gauge bars, reward, progress texts)")]
    public ContestObjectiveUI contestObjectiveUI;
    public ContestParticipantRowUI[] friendlyParticipantRowSlots = new ContestParticipantRowUI[5];
    public GameObject[] friendlyEmptySlotRowObjects = new GameObject[5];

    [Header("Rival Guild Members (GuildMemberInfoPanel only — leave empty on GuildStatus_GuildMember)")]
    public GameObject membersContainerRival;
    public LeaderboardRowUI[] rivalMemberRowSlots = new LeaderboardRowUI[5];
    public GameObject[] rivalEmptySlotRowObjects = new GameObject[5];

    [Header("Rival Contest Participants (ongoing contest only — shown ALONGSIDE the full-roster rows above, not instead of them)")]
    public GameObject membersContainerRivalContest;
    public ContestParticipantRowUI[] rivalParticipantRowSlots = new ContestParticipantRowUI[5];
    public GameObject[] rivalParticipantEmptySlotRowObjects = new GameObject[5];
    [Tooltip("Needed to scan for the rival guild's real members.")]
    public LeaderboardUI leaderboardUI;
    [Tooltip("Lets a contest win immediately refresh the browse list's cached copy of this guild's contestsWon, instead of waiting for that list's own periodic refresh to catch up.")]
    public GuildsListUI guildsListUI;

    private Coroutine rivalMembersCoroutine;
    private Coroutine cooldownTickCoroutine;
    private Coroutine waitForReadyCoroutine;
    private string lastRivalGuildId; // avoids re-populating every tick if the opponent hasn't changed

    void OnEnable()
    {
        Refresh();

        if (GuildManager.Instance != null)
        {
            GuildManager.Instance.OnMyGuildChanged += Refresh;
            GuildManager.Instance.OnSearchingForContestChanged += Refresh;
        }

        if (searchContestButton != null)
            searchContestButton.onClick.AddListener(OnSearchContestPressed);

        if (contestObjectiveUI != null)
            contestObjectiveUI.OnClaimRewardClicked += OnClaimRewardClicked;

        rivalMembersCoroutine = StartCoroutine(RivalMembersRefreshLoop());
        cooldownTickCoroutine = StartCoroutine(CooldownTickLoop());
    }

    void OnDisable()
    {
        if (GuildManager.Instance != null)
        {
            GuildManager.Instance.OnMyGuildChanged -= Refresh;
            GuildManager.Instance.OnSearchingForContestChanged -= Refresh;
        }

        if (searchContestButton != null)
            searchContestButton.onClick.RemoveListener(OnSearchContestPressed);

        if (contestObjectiveUI != null)
            contestObjectiveUI.OnClaimRewardClicked -= OnClaimRewardClicked;

        if (rivalMembersCoroutine != null) { StopCoroutine(rivalMembersCoroutine); rivalMembersCoroutine = null; }
        if (cooldownTickCoroutine != null) { StopCoroutine(cooldownTickCoroutine); cooldownTickCoroutine = null; }
        if (waitForReadyCoroutine != null) { StopCoroutine(waitForReadyCoroutine); waitForReadyCoroutine = null; }
    }

    // Separate loop, using the SAME interval as the Global leaderboard
    // (leaderboardUI.refreshInterval) — keeps the rival member list live,
    // reflecting joins/leaves on the rival's side, on the same cadence as
    // everything else in the game that refreshes from live leaderboard data.
    IEnumerator RivalMembersRefreshLoop()
    {
        while (true)
        {
            float interval = leaderboardUI != null ? leaderboardUI.refreshInterval : 30f;
            yield return new WaitForSeconds(interval);

            // Keep refreshing through the Results phase too, not just while
            // MyInContest — the frozen rosters/progress don't change, but
            // this is also what re-checks reward claim state (in case the
            // player claimed from a different device).
            // Single read of MyContestResultsPhase, reused below for
            // RefreshRewardClaimState — same reasoning as in Refresh(): a
            // second, later, independent re-read of this property is what
            // let the "ongoing contest" label flicker back on for a frame.
            bool loopResultsPhase = GuildManager.Instance != null && GuildManager.Instance.MyContestResultsPhase;

            if (GuildManager.Instance != null && (GuildManager.Instance.MyInContest || loopResultsPhase))
            {
                _ = PopulateRivalMembersAndParticipants(); // fire-and-forget — real path is async
                _ = PopulateFriendlyParticipants(); // fire-and-forget — needs self-insertion, so it's async
                _ = PopulateContestObjective();
                _ = RefreshRewardClaimState(loopResultsPhase);
                // Contest resolution itself now happens server-side
                // (contestResolutionSweep) — this UI just displays whatever
                // GuildManager's real-time listener already picked up.
            }
        }
    }

    void Refresh()
    {
        // GuildManager.IsReady only flips true once sign-in (UGS +
        // Firebase) has actually finished — right after a cold launch it
        // can still be false for the first second or two. Firing any of
        // the GetGuild*Async/FetchOpponentGuildInfoAsync calls before that
        // hits Firestore with no auth attached yet and fails with "Missing
        // or insufficient permissions", which is what left this panel's
        // fields blank. Wait for IsReady instead of calling those now, and
        // retry automatically the moment it flips.
        if (GuildManager.Instance == null || !GuildManager.Instance.IsReady)
        {
            if (waitForReadyCoroutine == null)
                waitForReadyCoroutine = StartCoroutine(WaitForReadyThenRefresh());
            return;
        }

        CheckForNewContestWin();

        bool searching = GuildManager.Instance != null && GuildManager.Instance.MySearchingForContest;
        bool inContest = GuildManager.Instance != null && GuildManager.Instance.MyInContest;
        bool resultsPhase = GuildManager.Instance != null && GuildManager.Instance.MyContestResultsPhase;
        // Ongoing contest AND the 24-hour Results screen afterward share the
        // same layout (rosters, objective panel, rival header) — the only
        // difference between them is what ContestObjectiveUI.RefreshResultsState
        // does with the labels/reward image below.
        bool showContestUI = inContest || resultsPhase;
        bool passive = !searching && !showContestUI;

        // The Search Contest button is now visible (greyed out, see
        // UpdateCooldownDisplay) during Results too, not just true Passive —
        // only actually hidden while searching or while a contest is
        // actively ongoing.
        if (searchContestButton != null)
        {
            searchContestButton.gameObject.SetActive(!searching && !inContest);
            UpdateCooldownDisplay();
        }
        if (lookingForContestImage != null) lookingForContestImage.SetActive(searching && !showContestUI);
        if (ongoingContestImage != null) ongoingContestImage.SetActive(showContestUI);
        if (passiveModeText != null) passiveModeText.SetActive(passive);
        if (searchingModeText != null) searchingModeText.SetActive(searching && !showContestUI);

        foreach (GameObject obj in alsoHiddenDuringContest)
            if (obj != null) obj.SetActive(!showContestUI);

        // Friendly header — always reflects the player's own current guild.
        SetLogo(friendlyGuildLogoBackground, friendlyGuildLogoIcon, GuildManager.Instance != null ? GuildManager.Instance.MyGuildLogoShape : null, GuildManager.Instance != null ? GuildManager.Instance.MyGuildLogoColor : null, GuildManager.Instance != null ? GuildManager.Instance.MyGuildLogoIconTintColor : null);
        if (friendlyGuildNameText != null) friendlyGuildNameText.text = GuildManager.Instance != null ? GuildManager.Instance.MyGuildName : "";

        // Rival header — visible/populated ONLY while the contest is
        // actively ongoing. Hidden the instant Results phase starts (not
        // just when Results ends) so the Search Contest button (now shown
        // greyed-out during Results, see above) has room/visual sense.
        if (rivalGuildLogoBackground != null) rivalGuildLogoBackground.gameObject.SetActive(inContest);
        if (rivalGuildLogoIcon != null) rivalGuildLogoIcon.gameObject.SetActive(inContest);
        if (rivalGuildNameText != null) rivalGuildNameText.gameObject.SetActive(inContest);
        if (rivalGuildLabelText != null) rivalGuildLabelText.SetActive(inContest);

        if (inContest)
        {
            SetLogo(rivalGuildLogoBackground, rivalGuildLogoIcon, GuildManager.Instance.MyContestOpponentLogoShape, GuildManager.Instance.MyContestOpponentLogoColor, GuildManager.Instance.MyContestOpponentLogoIconTintColor);
            if (rivalGuildNameText != null) rivalGuildNameText.text = GuildManager.Instance.MyContestOpponentGuildName;
        }

        // Same deal — rival full-roster container disappears as soon as
        // Results starts, not when it ends.
        if (membersContainerRival != null) membersContainerRival.SetActive(inContest);
        if (membersContainerRivalContest != null) membersContainerRivalContest.SetActive(showContestUI);
        if (membersContainerFriendlyContest != null) membersContainerFriendlyContest.SetActive(showContestUI);
        if (contestObjectiveUI != null) contestObjectiveUI.gameObject.SetActive(showContestUI);

        if (showContestUI)
        {
            _ = PopulateRivalMembersAndParticipants();
            _ = PopulateFriendlyParticipants();
            _ = PopulateContestObjective();
            _ = RefreshRewardClaimState(resultsPhase);
        }
        else
        {
            lastRivalGuildId = null; // force a fresh populate next time a contest starts

            // Explicitly clear Contest Ended / winners_label / losers_label /
            // reward-claim visuals here rather than relying on
            // contestObjectiveUI.gameObject.SetActive(false) above to hide
            // them — those label/image GameObjects aren't necessarily
            // children of contestObjectiveUI's own transform, so deactivating
            // its GameObject doesn't guarantee they go with it. Without this,
            // they stayed visible ("underneath everything") even after the
            // Results phase expired and the panel returned to passive.
            contestObjectiveUI?.HideAllResultsUI();
        }
    }

    IEnumerator WaitForReadyThenRefresh()
    {
        while (GuildManager.Instance == null || !GuildManager.Instance.IsReady)
            yield return null;

        waitForReadyCoroutine = null;
        Refresh();
    }

    void SetLogo(Image background, Image icon, string shapeName, string backgroundName, string iconTintHex)
    {
        if (!string.IsNullOrEmpty(shapeName) && System.Enum.TryParse(shapeName, out GuildManager.GuildLogoShape shape))
        {
            int index = (int)shape;
            if (icon != null && shapeSprites != null && index >= 0 && index < shapeSprites.Length)
            {
                icon.sprite = shapeSprites[index];
                icon.color = (!string.IsNullOrEmpty(iconTintHex) && ColorUtility.TryParseHtmlString(iconTintHex, out Color tint)) ? tint : Color.white;
            }
        }

        if (!string.IsNullOrEmpty(backgroundName) && System.Enum.TryParse(backgroundName, out GuildManager.GuildLogoBackground bg))
        {
            int index = (int)bg;
            if (background != null && backgroundSprites != null && index >= 0 && index < backgroundSprites.Length)
            {
                background.sprite = backgroundSprites[index];
                background.color = Color.white;
            }
        }
    }

    // Fetches the rival guild's members ONCE and populates BOTH the
    // full-roster rows and the smaller contest-progress rows from the
    // same result — avoids fetching twice. Uses the scalable real lookup
    // (GetGuildMembersWithTrueRanksAsync) for genuine contests, but falls
    // back to the old scan-based GetGuildMembers() specifically during a
    // fake test contest — fake opponents have no real backend entry the
    // scalable functions could query, but DO have dev-tool fake players
    // that only the old scan-based path (merging GuildDevTools data) sees.
    async Task PopulateRivalMembersAndParticipants()
    {
        if (GuildManager.Instance == null) return;
        if (!GuildManager.Instance.MyInContest && !GuildManager.Instance.MyContestResultsPhase) return;

        string opponentId = GuildManager.Instance.MyContestOpponentGuildId;
        if (string.IsNullOrEmpty(opponentId)) return;

        List<(int rank, LeaderboardUI.RowData row)> members;
        if (GuildManager.Instance.IsFakeTestContestActive)
        {
            // Fake opponents (dev tool only) have no real Firestore doc for
            // the real query below to find — this old scan-based path is
            // the only one that sees GuildDevTools' locally-injected fakes.
            members = GuildManager.Instance.GetGuildMembers(opponentId, leaderboardUI);
        }
        else
        {
            members = await GuildManager.Instance.GetGuildMembersWithTrueRanksAsync(opponentId, leaderboardUI);
        }

        // ---- Full-roster rows ----
        for (int i = 0; i < rivalMemberRowSlots.Length; i++)
        {
            bool occupied = i < members.Count;

            if (rivalMemberRowSlots[i] != null)
            {
                rivalMemberRowSlots[i].gameObject.SetActive(occupied);
                if (occupied)
                {
                    var (rank, row) = members[i];
                    rivalMemberRowSlots[i].Set(rank, CountryFlagDatabase.GetFlag(row.country), row.playerName, row.bigBangCount, row.lifetimeRabbits, false, row.equippedNickname, row.equippedBackground, row.nicknameColorIndex);
                }
            }

            if (i < rivalEmptySlotRowObjects.Length && rivalEmptySlotRowObjects[i] != null)
                rivalEmptySlotRowObjects[i].SetActive(!occupied);
        }

        // ---- Smaller contest-progress rows ----
        for (int i = 0; i < rivalParticipantRowSlots.Length; i++)
        {
            bool occupied = i < members.Count;

            if (rivalParticipantRowSlots[i] != null)
            {
                rivalParticipantRowSlots[i].gameObject.SetActive(occupied);
                if (occupied)
                    rivalParticipantRowSlots[i].Set(members[i].row);
            }

            if (i < rivalParticipantEmptySlotRowObjects.Length && rivalParticipantEmptySlotRowObjects[i] != null)
                rivalParticipantEmptySlotRowObjects[i].SetActive(!occupied);
        }

        lastRivalGuildId = opponentId;
    }

    // Detects a contest win by watching MyGuildContestsWon for an increase
    // — contestResolutionSweep updates it server-side, and the real-time
    // listener picks that up and fires Refresh() shortly after. Immediate
    // refresh here so the browse list's cached contestsWon for this guild
    // doesn't wait for its own next periodic tick.
    private int lastKnownContestsWon = -1;

    void CheckForNewContestWin()
    {
        if (GuildManager.Instance == null) return;
        int current = GuildManager.Instance.MyGuildContestsWon;
        bool increased = lastKnownContestsWon >= 0 && current > lastKnownContestsWon;

        // Update BEFORE triggering the refresh chain below. RefreshSingleGuildTotals
        // ends up re-firing this same method reentrantly (via GuildManager's
        // stats-changed event → GuildSearchContestUI.Refresh() → this method again).
        // If lastKnownContestsWon were still stale at that point, the reentrant call
        // would see the same "increase" and call RefreshSingleGuildTotals again,
        // forever — that's what caused the stack overflow.
        lastKnownContestsWon = current;

        if (increased)
            guildsListUI?.RefreshSingleGuildTotals(GuildManager.Instance.MyGuildId);
        // NOTE: the player's own PERSONAL lifetime contest-win counter
        // (feeding the Crowns Background cosmetic achievements) is tracked
        // in GuildManager.OnGuildSnapshot instead of here — this script is
        // wired onto BOTH GuildStatus_GuildMember and GuildMemberInfoPanel
        // (see this file's header comment), so if both happen to be active
        // at once, detecting it here would double-count the same win.
        // GuildManager's Firestore listener is a true singleton and fires
        // exactly once per real update, so that's the safe place for it.
    }

    async Task PopulateContestObjective()
    {
        if (contestObjectiveUI == null || GuildManager.Instance == null) return;

        bool inContest = GuildManager.Instance.MyInContest;
        bool resultsPhase = GuildManager.Instance.MyContestResultsPhase;
        if (!inContest && !resultsPhase) return;

        // While a contest is ONGOING, no member doc gets touched once it
        // resolves (ReportContestProgressAsync is gated on MyInContest), so
        // re-reading this during the Results phase naturally returns the
        // exact frozen final numbers — no special-casing needed here for
        // the "freeze progress" requirement.
        long friendlyProgress = await GuildManager.Instance.GetGuildContestProgressAsync(GuildManager.Instance.MyGuildId, GuildManager.Instance.MyGuildLeaderId);
        long rivalProgress = await GuildManager.Instance.GetGuildContestProgressAsync(GuildManager.Instance.MyContestOpponentGuildId, GuildManager.Instance.MyContestOpponentLeaderId);

        // During Results, the WINNING guild's members see their own
        // per-player share instead of the total pool; everyone else
        // (ongoing, or the losing guild) sees the total reward as before.
        bool showPerMemberReward = resultsPhase && GuildManager.Instance.MyContestWon;
        long displayReward = showPerMemberReward ? GuildManager.Instance.MyContestRewardPerMember : GuildManager.Instance.MyContestRewardAmount;

        contestObjectiveUI.Refresh(friendlyProgress, rivalProgress, GuildManager.Instance.MyContestObjectiveType, displayReward, GuildManager.Instance.MyContestObjectiveTarget);
    }

    // ---- Results-phase reward claim ----
    private bool rewardClaimedCache = false;
    private bool claimInFlight = false;

    // resultsPhase is passed in from Refresh()'s own single read of
    // GuildManager.Instance.MyContestResultsPhase, taken at the exact same
    // instant as the showContestUI/ongoingContestLabel decision above —
    // this method used to re-read MyContestResultsPhase itself a few
    // `await`s later, and any incoming Firestore snapshot landing in that
    // narrow gap between the two reads could make them briefly disagree,
    // which is what caused "ongoing contest" to flash back on for a frame
    // during Results. Reusing one shared value closes that window.
    async Task RefreshRewardClaimState(bool resultsPhase)
    {
        if (contestObjectiveUI == null || GuildManager.Instance == null) return;

        bool won = GuildManager.Instance.MyContestWon;
        bool lost = GuildManager.Instance.MyContestLost;

        // Apply the ongoing/ended + winner/loser labels RIGHT AWAY, with
        // whatever claimed-state is already cached — these must never sit
        // waiting on the network round-trip below. Without this, if
        // contestObjectiveUI's GameObject had just been reactivated (e.g.
        // the panel was just opened) it stayed at whatever label state it
        // was left in from before, for however long GetMyContestRewardClaimedAsync
        // took to come back — that stale window is what looked like
        // "ongoing contest" briefly flashing on right after opening the
        // panel during Results.
        contestObjectiveUI.RefreshResultsState(resultsPhase, won, lost, rewardClaimedCache);

        if (resultsPhase && won && !claimInFlight)
        {
            // Re-check the server's own record — catches a claim already
            // made from a different device, or just reopening this panel.
            // Only the claimable-vs-checkmark reward visual waits on this;
            // it's refreshed again once it resolves.
            rewardClaimedCache = await GuildManager.Instance.GetMyContestRewardClaimedAsync(GuildManager.Instance.MyGuildId);
            contestObjectiveUI.RefreshResultsState(resultsPhase, won, lost, rewardClaimedCache);
        }
        else if (!resultsPhase)
        {
            rewardClaimedCache = false; // reset, ready for the NEXT contest
        }
    }

    async void OnClaimRewardClicked()
    {
        if (GuildManager.Instance == null || claimInFlight || rewardClaimedCache) return;
        claimInFlight = true;

        // Optimistic: show the claimed (checkmark) state immediately so the
        // tap feels instant and a double-tap can't fire two claims while
        // the first is still in flight.
        contestObjectiveUI?.RefreshResultsState(true, true, false, true);

        var (success, alreadyClaimed, amount) = await GuildManager.Instance.ClaimContestRewardAsync();
        claimInFlight = false;

        if (!success)
        {
            // Roll back — the claim genuinely failed (network error, etc.),
            // not just "already claimed" — let them try again.
            rewardClaimedCache = false;
            contestObjectiveUI?.RefreshResultsState(true, true, false, false);
            Debug.LogWarning("GuildSearchContestUI: ClaimContestRewardAsync failed.");
            return;
        }

        rewardClaimedCache = true;
        if (!alreadyClaimed && amount > 0)
            GameManager.Instance?.AddCarrots(amount);

        contestObjectiveUI?.RefreshResultsState(true, true, false, true);
    }

    async Task PopulateFriendlyParticipants()
    {
        if (GuildManager.Instance == null) return;
        if (!GuildManager.Instance.MyInContest && !GuildManager.Instance.MyContestResultsPhase) return;
        if (string.IsNullOrEmpty(GuildManager.Instance.MyGuildId)) return;

        // Same real Firestore-backed lookup as the rival roster — the
        // player's own membership doc always exists in Firestore (they're
        // a real member), so there's no separate "include self" case to
        // handle anymore like the old GuildsListUI.GetMembersIncludingSelf
        // needed to for the Leaderboards-array-based system.
        var members = await GuildManager.Instance.GetGuildMembersWithTrueRanksAsync(GuildManager.Instance.MyGuildId, leaderboardUI);

        for (int i = 0; i < friendlyParticipantRowSlots.Length; i++)
        {
            bool occupied = i < members.Count;

            if (friendlyParticipantRowSlots[i] != null)
            {
                friendlyParticipantRowSlots[i].gameObject.SetActive(occupied);
                if (occupied)
                    friendlyParticipantRowSlots[i].Set(members[i].row);
            }

            if (i < friendlyEmptySlotRowObjects.Length && friendlyEmptySlotRowObjects[i] != null)
                friendlyEmptySlotRowObjects[i].SetActive(!occupied);
        }
    }

    // Ticks once a second purely to update the LIVE countdown display and
    // button interactable state — separate from the much slower network
    // poll (15-30s), which would make the countdown visibly choppy.
    IEnumerator CooldownTickLoop()
    {
        while (true)
        {
            UpdateCooldownDisplay();
            yield return new WaitForSeconds(1f);
        }
    }

    void UpdateCooldownDisplay()
    {
        if (GuildManager.Instance == null || searchContestButton == null) return;

        // Only relevant while the button is actually meant to be visible
        // (passive mode, or now also Results mode) — during an active
        // search or contest it's hidden entirely by Refresh() already.
        if (!searchContestButton.gameObject.activeSelf) return;

        bool resultsPhase = GuildManager.Instance.MyContestResultsPhase;

        if (resultsPhase)
        {
            // Greyed out and unclickable for the whole Results phase,
            // counting down to when Results ends.
            searchContestButton.interactable = false;

            long remainingMs = GuildManager.Instance.MyContestResultsUntilUnix - System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(remainingMs / 1000f));

            if (cooldownText != null)
            {
                int hours = totalSeconds / 3600;
                int minutes = (totalSeconds % 3600) / 60;
                int seconds = totalSeconds % 60;
                cooldownText.gameObject.SetActive(true);
                cooldownText.text = $"{hours:D2}:{minutes:D2}:{seconds:D2}";
            }
            return;
        }

        // No other cooldown exists anymore — once Results ends
        // (resultsExpirySweep clears it server-side), the guild is fully
        // passive and can search again immediately.
        searchContestButton.interactable = true;
        if (cooldownText != null) cooldownText.gameObject.SetActive(false);
    }

    async void OnSearchContestPressed()
    {
        if (GuildManager.Instance == null) return;

        // Optimistic: switch to the "Looking For Contest" visual right
        // away instead of leaving the button looking dead for however
        // long the Cloud Function's cold start takes. Refresh() at the
        // end resyncs with whatever the real state turned out to be —
        // if the call actually failed, that puts us back to Passive.
        if (searchContestButton != null) searchContestButton.interactable = false;
        ShowSearchingImmediately();

        try
        {
            await GuildManager.Instance.SetSearchingForContestAsync(true);
        }
        finally
        {
            Refresh();
        }
    }

    void ShowSearchingImmediately()
    {
        if (searchContestButton != null) searchContestButton.gameObject.SetActive(false);
        if (lookingForContestImage != null) lookingForContestImage.SetActive(true);
        if (passiveModeText != null) passiveModeText.SetActive(false);
        if (searchingModeText != null) searchingModeText.SetActive(true);
    }
}
