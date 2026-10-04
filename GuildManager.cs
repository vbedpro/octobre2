using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Leaderboards;
using Unity.Services.Leaderboards.Models;
using Unity.Services.CloudCode;
using Firebase.Functions;
using Firebase.Firestore;

// Core Guild system. Design summary (see chat history for full reasoning):
//
//   MIGRATED TO FIREBASE (Firestore + Cloud Functions) for guild lifecycle
//   (Create/Join/Leave/browse) — see CallFirebaseFunction below. This
//   replaces the old "guild = a Leaderboards entry owned by its leader"
//   design, which required awkward workarounds (TransferGuildLeadership,
//   memberPlayerIds arrays prone to clobbering) since Leaderboards has no
//   real transactions and only the entry owner can write to it. Firestore
//   has neither limitation: any authenticated member can safely read/have
//   writes routed through Cloud Functions using real transactions, and
//   members are their own subcollection documents, not a shared array.
//
//   Search/Matchmaking/Contest-progress are ALSO now migrated. Search
//   state changes (start/cancel) go through startSearch/cancelSearch;
//   matchmaking is fully server-driven via the scheduled matchmakingSweep
//   Cloud Function (no client trigger); contest resolution is likewise
//   server-driven via contestResolutionSweep. Every device gets pushed
//   updates instantly through a real-time Firestore listener on its own
//   guild document (see AttachGuildListener/OnGuildSnapshot below) instead
//   of polling — this is what actually fixes "other player's screen never
//   updated" and the two-guilds-stuck-searching bugs.
//
//   NOT YET DONE: the scalability hardening discussed separately (event-
//   driven contest resolution via Firestore triggers + denormalized
//   running totals; bucketing guilds by member count for matchmaking
//   instead of O(n^2) full comparison). Fine functionally at current
//   scale, flagged for before a big player-count ramp-up.
public class GuildManager : MonoBehaviour
{
    public static GuildManager Instance;

    [Header("UGS")]
    [Tooltip("Must match the GUILDS leaderboard ID created in the Unity Cloud Dashboard — separate from the player leaderboard.")]
    public string guildsLeaderboardId = "GuildsLeaderboard";

    [Header("Rules")]
    [Tooltip("Minimum BigBang (prestige) count required to create, join, or even view the Guilds tab.")]
    public int minBigBangsToUnlock = 10;
    [Tooltip("Maximum members per guild, leader included.")]
    public int maxMembers = 5;

    [Header("Fetch")]
    [Tooltip("How many guilds to fetch when browsing the Guilds list.")]
    public int topGuildCount = 999;

    public bool IsReady { get; private set; } = false;

    // ---- Local player's current guild affiliation (persisted, and what
    // gets copied into this player's own leaderboard metadata on submit) ----
    private const string GuildIdKey = "RabbitUniverse_GuildId";
    private const string GuildNameKey = "RabbitUniverse_GuildName";
    private const string GuildLogoShapeKey = "RabbitUniverse_GuildLogoShape";
    private const string GuildLogoColorKey = "RabbitUniverse_GuildLogoColor";
    private const string GuildLogoIconTintColorKey = "RabbitUniverse_GuildLogoIconTintColor";
    private const string GuildIsLeaderKey = "RabbitUniverse_GuildIsLeader";
    private const string GuildRankKey = "RabbitUniverse_GuildRank";
    private const string GuildContestsWonKey = "RabbitUniverse_GuildContestsWon";
    // Separate from GuildContestsWonKey above, which mirrors the CURRENT
    // guild's own win count (resets/changes whenever the player switches
    // or leaves a guild — see ClearLocalGuildStateOnly). This one is the
    // player's own PERSONAL lifetime total across every guild they've ever
    // been part of, never reset — this is what the Crowns Background
    // cosmetic achievements (AchievementsManager.ReportGuildContestsWon)
    // are meant to track, per the spec ("tied to the lifetime contest wins
    // of the player, rather than the win count of the guild he is
    // currently a member of").
    private const string MyLifetimeContestWinsKey = "RabbitUniverse_MyLifetimeContestWins";
    private const string GuildMemberCountKey = "RabbitUniverse_GuildMemberCount";
    private const string GuildLeaderIdKey = "RabbitUniverse_GuildLeaderId";
    private const string GuildSearchingForContestKey = "RabbitUniverse_GuildSearchingForContest";
    private const string GuildInContestKey = "RabbitUniverse_GuildInContest";
    private const string GuildContestOpponentIdKey = "RabbitUniverse_GuildContestOpponentId";
    private const string GuildContestOpponentNameKey = "RabbitUniverse_GuildContestOpponentName";
    private const string GuildContestOpponentLogoShapeKey = "RabbitUniverse_GuildContestOpponentLogoShape";
    private const string GuildContestOpponentLogoColorKey = "RabbitUniverse_GuildContestOpponentLogoColor";
    private const string GuildContestOpponentLogoIconTintColorKey = "RabbitUniverse_GuildContestOpponentLogoIconTintColor";
    private const string GuildContestOpponentLeaderIdKey = "RabbitUniverse_GuildContestOpponentLeaderId";
    private const string GuildContestObjectiveTypeKey = "RabbitUniverse_GuildContestObjectiveType";
    private const string GuildContestProgressBaselineKey = "RabbitUniverse_GuildContestProgressBaseline";
    private const string GuildContestRewardAmountKey = "RabbitUniverse_GuildContestRewardAmount";
    private const string GuildContestObjectiveTargetKey = "RabbitUniverse_GuildContestObjectiveTarget";
    private const string GuildContestPhaseKey = "RabbitUniverse_GuildContestPhase";
    private const string GuildContestResultKey = "RabbitUniverse_GuildContestResult";
    private const string GuildContestRewardPerMemberKey = "RabbitUniverse_GuildContestRewardPerMember";
    private const string GuildContestResultsUntilUnixKey = "RabbitUniverse_GuildContestResultsUntilUnix";

    private const int ContestRewardPerMember = 450; // keep in sync with index.js's ContestRewardPerMember
    private const string IsFakeTestContestActiveKey = "RabbitUniverse_IsFakeTestContestActive";

    public string MyGuildId { get; private set; }
    public string MyGuildName { get; private set; }
    public string MyGuildLogoShape { get; private set; }
    public string MyGuildLogoColor { get; private set; }
    public string MyGuildLogoIconTintColor { get; private set; }
    public bool IsGuildLeader { get; private set; }
    public bool HasGuild => !string.IsNullOrEmpty(MyGuildId);

    public int MyGuildRank { get; private set; }
    public int MyGuildContestsWon { get; private set; }
    [Tooltip("The player's own PERSONAL lifetime contest win count — never reset by switching/leaving guilds (unlike MyGuildContestsWon above). Feeds the Crowns Background cosmetic achievements.")]
    public int MyLifetimeContestWins { get; private set; }
    public int MyGuildMemberCount { get; private set; }
    [Tooltip("Cached so a NON-leader member can still tell Cloud Code whose entry to update — a client can only fetch/edit its OWN leaderboard entry directly, never an arbitrary other player's, which is exactly why Search Contest needs to go through Cloud Code's admin-level access in the first place.")]
    public string MyGuildLeaderId { get; private set; }
    public bool MySearchingForContest { get; private set; }
    public bool MyInContest { get; private set; }
    public string MyContestOpponentGuildId { get; private set; }
    public string MyContestOpponentGuildName { get; private set; }
    public string MyContestOpponentLogoShape { get; private set; }
    public string MyContestOpponentLogoColor { get; private set; }
    public string MyContestOpponentLogoIconTintColor { get; private set; }
    public string MyContestOpponentLeaderId { get; private set; }

    [Tooltip("The current contest's objective type (e.g. \"BreedRabbits\"). Decided once by TryMatchmakeGuild at match time, same for both guilds.")]
    public string MyContestObjectiveType { get; private set; }
    [Tooltip("Snapshot of the relevant lifetime stat taken the MOMENT this contest started — contest progress is always CURRENT minus this baseline, never a lifetime total.")]
    public long MyContestProgressBaseline { get; private set; }
    [Tooltip("The reward pool for the CURRENT contest, decided once at match time (450 x whichever guild had fewer members when matched) and fixed for the contest's duration.")]
    public long MyContestRewardAmount { get; private set; }
    [Tooltip("This contest's progress target — decided server-side once at match time (scaled from the shared matched member count, same one the reward pool uses; BreedRabbits additionally scales THIS guild's own total lifetime rabbits) and fixed for the contest's duration regardless of later roster changes. Replaces the old flat per-objective-type constant.")]
    public long MyContestObjectiveTarget { get; private set; }

    // ---- Post-contest Results phase (24 hours, server-driven) ----
    [Tooltip("\"\" while not in a contest or actively in one; \"results\" for the 24-hour window after a contest ends, during which the final state stays frozen and winners can claim their reward.")]
    public string MyContestPhase { get; private set; } = "";
    public bool MyContestResultsPhase => MyContestPhase == "results";
    [Tooltip("\"won\" or \"lost\" once the current contest has ended — empty otherwise.")]
    public string MyContestResult { get; private set; } = "";
    public bool MyContestWon => MyContestResult == "won";
    public bool MyContestLost => MyContestResult == "lost";
    [Tooltip("Only meaningful for the winning guild — the CURRENT contest's total reward divided by however many members the winning guild had at the exact moment it won, rounded to the nearest whole carrot.")]
    public long MyContestRewardPerMember { get; private set; }
    [Tooltip("Unix ms timestamp of when the Results phase ends and this guild returns to fully passive — 0 if not currently in Results.")]
    public long MyContestResultsUntilUnix { get; private set; }

    public long MyContestProgress
    {
        get
        {
            if (!MyInContest || string.IsNullOrEmpty(MyContestObjectiveType)) return 0;
            return Math.Max(0, ComputeBaselineForObjective(MyContestObjectiveType) - MyContestProgressBaseline);
        }
    }

    [Tooltip("True only while a DEBUG fake-test contest (see SetFakeTestContest) is active — suppresses the real periodic poll from overwriting the fake state with real server data. Now persisted (was previously runtime-only), so it correctly survives closing/reopening the app for testing.")]
    public bool IsFakeTestContestActive { get; private set; } = false;

    public event Action OnSearchingForContestChanged;

    public void UpdateMyGuildStats(int rank, int contestsWon, int memberCount)
    {
        MyGuildRank = rank;
        MyGuildContestsWon = Math.Max(MyGuildContestsWon, contestsWon);
        MyGuildMemberCount = memberCount;
        SaveLocalGuildState();
        OnMyGuildChanged?.Invoke();
    }

    public event Action OnMyGuildChanged;

    // Called from OnGuildSnapshot below the moment it detects the current
    // guild's own win count genuinely ticked up while this player was a
    // member watching it happen — see _winTrackingBaseline for how a false
    // positive (e.g. just joining a guild that already has wins banked) is
    // avoided. Takes a count rather than always +1 in case more than one
    // win landed between two snapshots (e.g. app was briefly backgrounded).
    private void ReportContestWins(int count)
    {
        if (count <= 0) return;

        MyLifetimeContestWins += count;
        PlayerPrefs.SetInt(MyLifetimeContestWinsKey, MyLifetimeContestWins);
        PlayerPrefs.Save();

        AchievementsManager.Instance?.ReportGuildContestsWon(MyLifetimeContestWins);
    }

    // Tracks MyGuildContestsWon's value as of the last snapshot, SPECIFICALLY
    // for personal-win detection in OnGuildSnapshot below — kept separate
    // from MyGuildContestsWon itself (which the UI reads directly) so it
    // can be reset to "unknown" (-1) independently whenever the listener
    // (re)attaches to a guild. Without that reset, joining a DIFFERENT
    // guild that already has e.g. 5 wins banked would look like "this
    // player just won 5 contests" on the very first snapshot from that
    // guild — this baseline makes that first snapshot after any (re)attach
    // establish the starting point instead of ever counting as a win.
    private int _winTrackingBaseline = -1;

    // ---- Firebase Functions helper ----
    // Every Firestore-backed guild call goes through this. Returns the
    // function's response as a plain Dictionary<string, object> — the
    // Unity Functions SDK deserializes an object-shaped response this way.
    private async Task<Dictionary<string, object>> CallFirebaseFunction(string name, Dictionary<string, object> data)
    {
        var function = FirebaseFunctions.DefaultInstance.GetHttpsCallable(name);
        var result = await function.CallAsync(data);
        return result.Data as Dictionary<string, object>;
    }

    // ---- Real-time guild listener ----
    // Replaces the old poll-based RefreshMySearchStateAsync. As soon as ANY
    // device writes to guilds/{MyGuildId} (search state, a match, a contest
    // resolution, another member joining/leaving), every other device with
    // this listener attached gets pushed the update immediately — no more
    // waiting on a poll interval, and no more "other player's screen never
    // updated" bug, since there's nothing to poll anymore.
    private ListenerRegistration guildListenerRegistration;

    private void AttachGuildListener(string guildId)
    {
        DetachGuildListener();
        if (string.IsNullOrEmpty(guildId)) return;

        // Reset the personal-win-detection baseline every time the listener
        // (re)attaches — whether that's this same guild after an app
        // relaunch or a genuinely different guild after switching — so the
        // very next snapshot only ever establishes a starting point, never
        // gets mistaken for a win. See _winTrackingBaseline's declaration.
        _winTrackingBaseline = -1;

        DocumentReference docRef = FirebaseFirestore.DefaultInstance.Collection("guilds").Document(guildId);
        guildListenerRegistration = docRef.Listen(snapshot => OnGuildSnapshot(guildId, snapshot));
    }

    private void DetachGuildListener()
    {
        guildListenerRegistration?.Stop();
        guildListenerRegistration = null;
    }

    // Runs on the main thread (confirmed via Firestore Unity SDK docs) —
    // safe to touch game state directly here.
    private void OnGuildSnapshot(string guildId, DocumentSnapshot snapshot)
    {
        if (guildId != MyGuildId) return; // stale callback from a guild we've since left

        if (!snapshot.Exists)
        {
            // Guild was deleted server-side (e.g. auto-disbanded after the
            // last member left from another device). Clear local state to
            // match, but don't re-trigger LeaveGuild's own Firebase call.
            ClearLocalGuildStateOnly();
            OnMyGuildChanged?.Invoke();
            return;
        }

        var d = snapshot.ToDictionary();
        bool wasInContest = MyInContest;

        MyGuildName = d.GetValueOrDefault("name", MyGuildName) as string ?? MyGuildName;
        MyGuildLogoShape = d.GetValueOrDefault("logoShape", "") as string ?? "";
        MyGuildLogoColor = d.GetValueOrDefault("logoColor", "") as string ?? "";
        MyGuildLogoIconTintColor = d.GetValueOrDefault("logoIconTintColor", "") as string ?? "";
        MyGuildMemberCount = Convert.ToInt32(d.GetValueOrDefault("memberCount", MyGuildMemberCount));

        int newContestsWon = Convert.ToInt32(d.GetValueOrDefault("contestsWon", MyGuildContestsWon));
        // _winTrackingBaseline < 0 means this is the first snapshot since
        // the listener (re)attached — establish the baseline only, don't
        // count it as a win (see AttachGuildListener/the field's comment).
        if (_winTrackingBaseline >= 0 && newContestsWon > _winTrackingBaseline)
            ReportContestWins(newContestsWon - _winTrackingBaseline);
        _winTrackingBaseline = newContestsWon;
        MyGuildContestsWon = newContestsWon;
        MySearchingForContest = (bool)(d.GetValueOrDefault("searching", false) ?? false);
        MyInContest = (bool)(d.GetValueOrDefault("inContest", false) ?? false);
        MyContestOpponentGuildId = d.GetValueOrDefault("contestOpponentId", "") as string ?? "";
        MyContestObjectiveType = d.GetValueOrDefault("contestObjectiveType", "") as string ?? "";
        MyContestRewardAmount = Convert.ToInt64(d.GetValueOrDefault("contestRewardAmount", 0L));
        MyContestObjectiveTarget = Convert.ToInt64(d.GetValueOrDefault("contestObjectiveTarget", 0L));

        // contestPhase/contestResult/contestRewardPerMember/resultsUntil only
        // exist on the doc while a Results screen is (or was) active —
        // GetValueOrDefault naturally gives "" / 0 once resultsExpirySweep
        // deletes them server-side, which is exactly what returns this
        // guild to fully passive from the client's perspective too.
        MyContestPhase = d.GetValueOrDefault("contestPhase", "") as string ?? "";
        MyContestResult = d.GetValueOrDefault("contestResult", "") as string ?? "";
        MyContestRewardPerMember = Convert.ToInt64(d.GetValueOrDefault("contestRewardPerMember", 0L));
        // Stored server-side as a plain epoch-ms number (not a Firestore
        // Timestamp), so Convert.ToInt64 handles it directly.
        MyContestResultsUntilUnix = Convert.ToInt64(d.GetValueOrDefault("resultsUntil", 0L));

        bool justEnteredContest = MyInContest && !wasInContest;
        if (justEnteredContest)
        {
            MyContestProgressBaseline = ComputeBaselineForObjective(MyContestObjectiveType);
            // Own progress counter on the server also resets to 0 at match
            // time (matchmakingSweep), so start reporting fresh too.
            StartBreedRabbitsReportingIfNeeded();
        }
        else if (!MyInContest && wasInContest)
        {
            StopBreedRabbitsReporting();
        }

        // Opponent guild's display info (name/logo) isn't on our own guild
        // doc — fetch it once when we newly learn who it is.
        if (!string.IsNullOrEmpty(MyContestOpponentGuildId) && MyContestOpponentGuildId != _lastFetchedOpponentId)
        {
            _lastFetchedOpponentId = MyContestOpponentGuildId;
            _ = FetchOpponentGuildInfoAsync(MyContestOpponentGuildId);
        }
        else if (string.IsNullOrEmpty(MyContestOpponentGuildId))
        {
            _lastFetchedOpponentId = "";
            MyContestOpponentGuildName = "";
            MyContestOpponentLogoShape = "";
            MyContestOpponentLogoColor = "";
            MyContestOpponentLogoIconTintColor = "";
        }

        SaveLocalGuildState();
        OnSearchingForContestChanged?.Invoke();
        OnMyGuildChanged?.Invoke();
    }

    private string _lastFetchedOpponentId = "";

    private async Task FetchOpponentGuildInfoAsync(string opponentGuildId)
    {
        try
        {
            DocumentSnapshot snap = await FirebaseFirestore.DefaultInstance.Collection("guilds").Document(opponentGuildId).GetSnapshotAsync();
            if (!snap.Exists || opponentGuildId != MyContestOpponentGuildId) return; // stale by the time it comes back

            var d = snap.ToDictionary();
            MyContestOpponentGuildName = d.GetValueOrDefault("name", "") as string ?? "";
            MyContestOpponentLogoShape = d.GetValueOrDefault("logoShape", "") as string ?? "";
            MyContestOpponentLogoColor = d.GetValueOrDefault("logoColor", "") as string ?? "";
            MyContestOpponentLogoIconTintColor = d.GetValueOrDefault("logoIconTintColor", "") as string ?? "";
            SaveLocalGuildState();
            OnMyGuildChanged?.Invoke();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: FetchOpponentGuildInfoAsync failed — {e.Message}");
        }
    }

    // Clears local state without calling the server — used when the guild
    // listener tells us the guild is already gone from another device.
    private void ClearLocalGuildStateOnly()
    {
        DetachGuildListener();
        MyGuildId = "";
        MyGuildName = "";
        MyGuildLogoShape = "";
        MyGuildLogoColor = "";
        MyGuildLogoIconTintColor = "";
        IsGuildLeader = false;
        MyGuildLeaderId = "";
        MyGuildRank = 0;
        MyGuildContestsWon = 0;
        MySearchingForContest = false;
        MyInContest = false;
        MyContestOpponentGuildId = "";
        MyContestOpponentGuildName = "";
        MyContestOpponentLogoShape = "";
        MyContestOpponentLogoColor = "";
        MyContestOpponentLogoIconTintColor = "";
        MyContestOpponentLeaderId = "";
        MyContestObjectiveType = "";
        MyContestRewardAmount = 0;
        MyContestObjectiveTarget = 0;
        MyContestProgressBaseline = 0;
        MyContestPhase = "";
        MyContestResult = "";
        MyContestRewardPerMember = 0;
        MyContestResultsUntilUnix = 0;
        IsFakeTestContestActive = false;
        SaveLocalGuildState();
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        LoadLocalGuildState();
    }

    IEnumerator Start()
    {
        while (LeaderboardManager.Instance == null || !LeaderboardManager.Instance.IsReady)
            yield return null;

        IsReady = true;

        // Re-sync once on launch — covers a returning player whose saved
        // lifetime total is already ahead of whatever AchievementsManager
        // currently has (e.g. this achievement just got wired up). Safe to
        // call every launch regardless: ReportGuildContestsWon only ever
        // RAISES the achievement's progress, never lowers it.
        AchievementsManager.Instance?.ReportGuildContestsWon(MyLifetimeContestWins);

        if (HasGuild)
        {
            AttachGuildListener(MyGuildId);
            if (MyInContest) StartBreedRabbitsReportingIfNeeded(); // resuming mid-contest (app reopened)
            _ = ClaimPendingRewardIfAnyAsync(); // pick up anything missed while offline during a Results window
        }
    }

    void LoadLocalGuildState()
    {
        MyGuildId = PlayerPrefs.GetString(GuildIdKey, "");
        MyGuildName = PlayerPrefs.GetString(GuildNameKey, "");
        MyGuildLogoShape = PlayerPrefs.GetString(GuildLogoShapeKey, "");
        MyGuildLogoColor = PlayerPrefs.GetString(GuildLogoColorKey, "");
        MyGuildLogoIconTintColor = PlayerPrefs.GetString(GuildLogoIconTintColorKey, "");
        IsGuildLeader = PlayerPrefs.GetInt(GuildIsLeaderKey, 0) == 1;
        MyGuildRank = PlayerPrefs.GetInt(GuildRankKey, 0);
        MyGuildContestsWon = PlayerPrefs.GetInt(GuildContestsWonKey, 0);
        // Loaded here (not inside ClearLocalGuildStateOnly/SaveLocalGuildState's
        // guild-switch reset cycle) because it must survive leaving or
        // switching guilds — see the key's declaration above for why.
        MyLifetimeContestWins = PlayerPrefs.GetInt(MyLifetimeContestWinsKey, 0);
        MyGuildMemberCount = PlayerPrefs.GetInt(GuildMemberCountKey, 1);
        MyGuildLeaderId = PlayerPrefs.GetString(GuildLeaderIdKey, "");
        MySearchingForContest = PlayerPrefs.GetInt(GuildSearchingForContestKey, 0) == 1;
        MyInContest = PlayerPrefs.GetInt(GuildInContestKey, 0) == 1;
        MyContestOpponentGuildId = PlayerPrefs.GetString(GuildContestOpponentIdKey, "");
        MyContestOpponentGuildName = PlayerPrefs.GetString(GuildContestOpponentNameKey, "");
        MyContestOpponentLogoShape = PlayerPrefs.GetString(GuildContestOpponentLogoShapeKey, "");
        MyContestOpponentLogoColor = PlayerPrefs.GetString(GuildContestOpponentLogoColorKey, "");
        MyContestOpponentLogoIconTintColor = PlayerPrefs.GetString(GuildContestOpponentLogoIconTintColorKey, "");
        MyContestOpponentLeaderId = PlayerPrefs.GetString(GuildContestOpponentLeaderIdKey, "");
        MyContestObjectiveType = PlayerPrefs.GetString(GuildContestObjectiveTypeKey, "");
        MyContestProgressBaseline = long.Parse(PlayerPrefs.GetString(GuildContestProgressBaselineKey, "0"));
        MyContestRewardAmount = long.Parse(PlayerPrefs.GetString(GuildContestRewardAmountKey, "0"));
        MyContestObjectiveTarget = long.Parse(PlayerPrefs.GetString(GuildContestObjectiveTargetKey, "0"));
        MyContestPhase = PlayerPrefs.GetString(GuildContestPhaseKey, "");
        MyContestResult = PlayerPrefs.GetString(GuildContestResultKey, "");
        MyContestRewardPerMember = long.Parse(PlayerPrefs.GetString(GuildContestRewardPerMemberKey, "0"));
        MyContestResultsUntilUnix = long.Parse(PlayerPrefs.GetString(GuildContestResultsUntilUnixKey, "0"));
        IsFakeTestContestActive = PlayerPrefs.GetInt(IsFakeTestContestActiveKey, 0) == 1;
    }

    void SaveLocalGuildState()
    {
        PlayerPrefs.SetString(GuildIdKey, MyGuildId ?? "");
        PlayerPrefs.SetString(GuildNameKey, MyGuildName ?? "");
        PlayerPrefs.SetString(GuildLogoShapeKey, MyGuildLogoShape ?? "");
        PlayerPrefs.SetString(GuildLogoColorKey, MyGuildLogoColor ?? "");
        PlayerPrefs.SetString(GuildLogoIconTintColorKey, MyGuildLogoIconTintColor ?? "");
        PlayerPrefs.SetInt(GuildIsLeaderKey, IsGuildLeader ? 1 : 0);
        PlayerPrefs.SetInt(GuildRankKey, MyGuildRank);
        PlayerPrefs.SetInt(GuildContestsWonKey, MyGuildContestsWon);
        PlayerPrefs.SetInt(GuildMemberCountKey, MyGuildMemberCount);
        PlayerPrefs.SetString(GuildLeaderIdKey, MyGuildLeaderId ?? "");
        PlayerPrefs.SetInt(GuildSearchingForContestKey, MySearchingForContest ? 1 : 0);
        PlayerPrefs.SetInt(GuildInContestKey, MyInContest ? 1 : 0);
        PlayerPrefs.SetString(GuildContestOpponentIdKey, MyContestOpponentGuildId ?? "");
        PlayerPrefs.SetString(GuildContestOpponentNameKey, MyContestOpponentGuildName ?? "");
        PlayerPrefs.SetString(GuildContestOpponentLogoShapeKey, MyContestOpponentLogoShape ?? "");
        PlayerPrefs.SetString(GuildContestOpponentLogoColorKey, MyContestOpponentLogoColor ?? "");
        PlayerPrefs.SetString(GuildContestOpponentLogoIconTintColorKey, MyContestOpponentLogoIconTintColor ?? "");
        PlayerPrefs.SetString(GuildContestOpponentLeaderIdKey, MyContestOpponentLeaderId ?? "");
        PlayerPrefs.SetString(GuildContestObjectiveTypeKey, MyContestObjectiveType ?? "");
        PlayerPrefs.SetString(GuildContestProgressBaselineKey, MyContestProgressBaseline.ToString());
        PlayerPrefs.SetString(GuildContestRewardAmountKey, MyContestRewardAmount.ToString());
        PlayerPrefs.SetString(GuildContestObjectiveTargetKey, MyContestObjectiveTarget.ToString());
        PlayerPrefs.SetString(GuildContestPhaseKey, MyContestPhase ?? "");
        PlayerPrefs.SetString(GuildContestResultKey, MyContestResult ?? "");
        PlayerPrefs.SetString(GuildContestRewardPerMemberKey, MyContestRewardPerMember.ToString());
        PlayerPrefs.SetString(GuildContestResultsUntilUnixKey, MyContestResultsUntilUnix.ToString());
        PlayerPrefs.SetInt(IsFakeTestContestActiveKey, IsFakeTestContestActive ? 1 : 0);
        PlayerPrefs.Save();
    }

    public bool MeetsBigBangRequirement()
    {
        int count = PrestigeManager.Instance != null ? PrestigeManager.Instance.prestigeCount : 0;
        return count >= minBigBangsToUnlock;
    }

    [Serializable]
    public class GuildInfo
    {
        public string guildId;
        public string guildName;
        public string logoShape;
        public string logoColor;
        public string leaderId;
        public string leaderName;
        public int memberCount;
        public int contestsWon;
        public long lifetimeRabbits;
        public bool searchingForContest;
        public long searchQueueStartTimeUnix;
        public int searchMemberCountSnapshot;
        public bool inContest;
        public string contestOpponentGuildId;
        public string contestOpponentGuildName;
        public string contestOpponentLogoShape;
        public string contestOpponentLogoColor;
        public string contestOpponentLogoIconTintColor;
        public string contestOpponentLeaderId;
        public string contestObjectiveType;
        public long contestRewardAmount;
        [Tooltip("Hex color (e.g. \"#4271FF\"), or empty/white for no tint. Applied ONLY to the icon, separate from logoColor (which selects a background sprite).")]
        public string logoIconTintColor;
        [Tooltip("Directly maintained member ID list — the scalable alternative to discovering membership by scanning the whole player leaderboard. Updated on join/leave/create.")]
        public List<string> memberPlayerIds = new List<string>();
    }

    public enum GuildLogoShape
    {
        Circle,
        Square,
        Triangle,
        Star,
        Diamond,
        Shape6
    }

    public enum GuildLogoBackground
    {
        Background1,
        Background2,
        Background3,
        Background4,
        Background5,
        Background6
    }

    [Serializable]
    public class GuildMetadata
    {
        public string guildId;
        public string guildName;
        public string logoShape;
        public string logoColor;
        public string leaderId;
        public string leaderName;
        public int memberCount;
        public int contestsWon;
        public long lifetimeRabbits;
        public bool disbanded;
        public bool searchingForContest;
        public long searchQueueStartTimeUnix;
        public int searchMemberCountSnapshot;
        public bool inContest;
        public string contestOpponentGuildId;
        public string contestOpponentGuildName;
        public string contestOpponentLogoShape;
        public string contestOpponentLogoColor;
        public string contestOpponentLogoIconTintColor;
        public string contestOpponentLeaderId;
        public string contestObjectiveType;
        public long contestRewardAmount;
        public string logoIconTintColor;
        public List<string> memberPlayerIds = new List<string>();
    }

    // Guards against a rapid double/triple tap on "Create Guild" firing
    // multiple overlapping calls. HasGuild below is NOT enough on its own —
    // it only becomes true once MyGuildId is assigned near the bottom of
    // this method, i.e. AFTER the Cloud Function round-trip (the exact
    // ~1 second window the player can tap again in) — so a second call
    // made while the first is still in flight would see HasGuild == false
    // too and sail right past that check, creating its own separate guild
    // doc server-side (with the player as its only member, forever —
    // nothing ever calls leaveGuild on it since the client only tracks one
    // MyGuildId). This flag is set as literally the FIRST thing in the
    // method, before any other check, so no in-flight call can ever slip
    // through regardless of UI button timing.
    private bool _createGuildInFlight = false;

    // ---- Create ----  [MIGRATED to Firebase]
    public async Task<GuildInfo> CreateGuild(string guildName, string logoShape, string logoColor, string logoIconTintColor = "")
    {
        if (_createGuildInFlight)
        {
            Debug.LogWarning("GuildManager: CreateGuild already in flight — ignoring duplicate call.");
            return null;
        }
        _createGuildInFlight = true;

        try
        {
            if (!MeetsBigBangRequirement())
            {
                Debug.LogWarning($"GuildManager: needs {minBigBangsToUnlock} BigBangs to create a guild.");
                return null;
            }
            if (HasGuild)
            {
                Debug.LogWarning("GuildManager: already in a guild — leave it first.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(guildName))
            {
                Debug.LogWarning("GuildManager: guild name can't be empty.");
                return null;
            }

            string myPlayerId = AuthenticationService.Instance.PlayerId;
            string myPlayerName = AuthenticationService.Instance.PlayerName;

            Dictionary<string, object> response;
            try
            {
                var args = new Dictionary<string, object>
                {
                    { "guildName", guildName },
                    { "logoShape", logoShape },
                    { "logoColor", logoColor },
                    { "logoIconTintColor", logoIconTintColor },
                    { "playerName", myPlayerName }
                };
                response = await CallFirebaseFunction("createGuild", args);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GuildManager: CreateGuild (Firebase) failed — {e.Message}");
                return null;
            }

            if (response == null || !(bool)response["success"])
            {
                Debug.LogWarning("GuildManager: CreateGuild — server rejected the request.");
                return null;
            }

            string guildId = (string)response["guildId"];

            MyGuildId = guildId;
            MyGuildName = guildName;
            MyGuildLogoShape = logoShape;
            MyGuildLogoColor = logoColor;
            MyGuildLogoIconTintColor = logoIconTintColor;
            IsGuildLeader = true; // vestigial concept post-migration (Firestore has no single-owner limitation), kept for UI compatibility — creator just happens to be first member
            MyGuildLeaderId = myPlayerId;
            MyGuildRank = 0;
            MyGuildContestsWon = 0;
            MyGuildMemberCount = 1;
            MySearchingForContest = false;
            SaveLocalGuildState();
            AttachGuildListener(guildId);

            LeaderboardManager.Instance?.SubmitScoreNow();

            OnMyGuildChanged?.Invoke();

            return new GuildInfo
            {
                guildId = guildId,
                guildName = guildName,
                logoShape = logoShape,
                logoColor = logoColor,
                leaderId = myPlayerId,
                leaderName = myPlayerName,
                memberCount = 1,
                logoIconTintColor = logoIconTintColor,
                memberPlayerIds = new List<string> { myPlayerId }
            };
        }
        finally
        {
            _createGuildInFlight = false;
        }
    }

    // ---- Browse ----  [MIGRATED to Firebase]
    // listGuilds is now cursor-paginated server-side (100 guilds per page,
    // to keep each individual request cheap at scale) instead of one big
    // capped pull. This keeps fetching pages until it has topGuildCount
    // guilds or the server says there's nothing left (nextCursor == null),
    // so from the caller's perspective this still behaves like "give me
    // the top 999" — it just does it as several small requests instead of
    // one big one under the hood.
    public async Task<List<GuildInfo>> FetchGuildsList()
    {
        List<GuildInfo> result = new List<GuildInfo>();
        if (!IsReady) return result;

        object cursorMemberCount = null;
        object cursorGuildId = null;

        try
        {
            while (result.Count < topGuildCount)
            {
                var args = new Dictionary<string, object> { { "limit", 100 } };
                if (cursorMemberCount != null && cursorGuildId != null)
                {
                    args["startAfterMemberCount"] = cursorMemberCount;
                    args["startAfterGuildId"] = cursorGuildId;
                }

                Dictionary<string, object> response = await CallFirebaseFunction("listGuilds", args);
                if (response == null || !response.ContainsKey("guilds"))
                {
                    Debug.LogWarning("GuildManager: FetchGuildsList — response missing 'guilds' key entirely.");
                    break;
                }

                // Was: `response["guilds"] as List<object>` — the Firebase
                // Functions SDK doesn't always deserialize a nested JSON array
                // into exactly List<object>, and `as` fails SILENTLY (returns
                // null, no exception, no log) rather than throwing — which is
                // exactly what made guilds vanish on every later refresh with
                // zero error output. IEnumerable<object> is permissive enough
                // to accept whatever concrete collection type the SDK actually
                // uses, and we log loudly if it's still not enumerable at all.
                if (!(response["guilds"] is System.Collections.IEnumerable guildsRaw) || response["guilds"] is string)
                {
                    Debug.LogWarning($"GuildManager: FetchGuildsList — 'guilds' was type {response["guilds"]?.GetType().FullName ?? "null"}, not enumerable. Raw value: {response["guilds"]}");
                    break;
                }

                int pageCount = 0;
                foreach (var g in guildsRaw)
                {
                    pageCount++;
                    var d = g as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("guildId"))
                    {
                        Debug.LogWarning($"GuildManager: FetchGuildsList — skipped one entry, type was {g?.GetType().FullName ?? "null"}.");
                        continue;
                    }

                    result.Add(new GuildInfo
                    {
                        guildId = (string)d["guildId"],
                        guildName = d.GetValueOrDefault("name", "") as string,
                        logoShape = d.GetValueOrDefault("logoShape", "") as string,
                        logoColor = d.GetValueOrDefault("logoColor", "") as string,
                        logoIconTintColor = d.GetValueOrDefault("logoIconTintColor", "") as string,
                        memberCount = Convert.ToInt32(d.GetValueOrDefault("memberCount", 0)),
                        contestsWon = Convert.ToInt32(d.GetValueOrDefault("contestsWon", 0)),
                        searchingForContest = (bool)(d.GetValueOrDefault("searching", false)),
                    });
                }

                if (pageCount == 0) break; // empty page — nothing more to fetch

                // Advance the cursor from the server's own nextCursor, rather
                // than computing it locally — keeps the pagination logic
                // entirely server-owned.
                if (response.TryGetValue("nextCursor", out object nextCursorObj) && nextCursorObj is Dictionary<string, object> nextCursor)
                {
                    cursorMemberCount = nextCursor.GetValueOrDefault("startAfterMemberCount");
                    cursorGuildId = nextCursor.GetValueOrDefault("startAfterGuildId");
                }
                else
                {
                    break; // no more pages
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: FetchGuildsList (Firebase) failed — {e.Message}");
        }

        return result;
    }

    GuildMetadata ParseGuildMetadata(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonUtility.FromJson<GuildMetadata>(json); }
        catch { return null; }
    }

    [Serializable]
    private class HasAnyMemberResponse
    {
        public string guildId;
        public bool hasMember;
        public string reason;
    }

    [Serializable]
    private class DisbandGuildResponse
    {
        public bool success;
        public bool alreadyGone;
        public string reason;
    }

    // ---- DEBUG fake-test-contest tools (unchanged — purely local/client-side) ----
    [ContextMenu("DEBUG: Fake-Match Current Search With Random Guild")]
    public void SetFakeTestContest()
    {
        if (!MySearchingForContest)
        {
            Debug.LogWarning("GuildManager: SetFakeTestContest — not currently searching for a contest, nothing to fake-match against.");
            return;
        }

        System.Random rng = new System.Random();
        string[] fakeNames = { "Iron Rabbits", "Golden Warren", "Shadow Burrow", "Crimson Legion", "Ancient Order", "Wild Syndicate" };

        string fakeGuildId = "fake_test_opponent_" + rng.Next(100000, 999999);
        string fakeGuildName = fakeNames[rng.Next(fakeNames.Length)];
        int shapeCount = System.Enum.GetValues(typeof(GuildLogoShape)).Length;
        int bgCount = System.Enum.GetValues(typeof(GuildLogoBackground)).Length;
        string fakeShape = ((GuildLogoShape)rng.Next(shapeCount)).ToString();
        string fakeBackground = ((GuildLogoBackground)rng.Next(bgCount)).ToString();
        Color[] tintChoices = { Color.white, new Color32(0x42, 0x71, 0xFF, 0xFF), new Color32(0xFF, 0x4F, 0x00, 0xFF), new Color32(0x28, 0xE0, 0x25, 0xFF) };
        string fakeTint = "#" + ColorUtility.ToHtmlStringRGB(tintChoices[rng.Next(tintChoices.Length)]);

        GuildDevTools.PopulateGuildWithFakePlayers(fakeGuildId);

        MySearchingForContest = false;
        MyInContest = true;
        MyContestOpponentGuildId = fakeGuildId;
        MyContestOpponentGuildName = fakeGuildName;
        MyContestOpponentLogoShape = fakeShape;
        MyContestOpponentLogoColor = fakeBackground;
        MyContestOpponentLogoIconTintColor = fakeTint;
        MyContestOpponentLeaderId = "fake_no_real_leader";
        MyContestObjectiveType = "BreedRabbits";
        MyContestRewardAmount = ContestRewardPerMember * MyGuildMemberCount;
        // Debug-only stand-in target, not the real scaling formula (that's
        // server-side now, see index.js's computeObjectiveTarget) — just
        // something small enough to actually test a win with.
        MyContestObjectiveTarget = 50;
        MyContestProgressBaseline = ComputeBaselineForObjective(MyContestObjectiveType);
        IsFakeTestContestActive = true;
        SaveLocalGuildState();

        OnSearchingForContestChanged?.Invoke();
        Debug.Log($"GuildManager: fake-matched against '{fakeGuildName}' ({fakeGuildId}) for testing.");
    }

    [ContextMenu("DEBUG: Clear Fake Test Contest")]
    public async void ClearFakeTestContest()
    {
        if (!IsFakeTestContestActive) return;

        try
        {
            await SetSearchingForContestAsync(false);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: ClearFakeTestContest — failed to clear real search state on backend — {e.Message}");
        }

        IsFakeTestContestActive = false;
        MySearchingForContest = false;
        MyInContest = false;
        MyContestOpponentGuildId = "";
        MyContestOpponentGuildName = "";
        MyContestOpponentLogoShape = "";
        MyContestOpponentLogoColor = "";
        MyContestOpponentLogoIconTintColor = "";
        SaveLocalGuildState();

        OnSearchingForContestChanged?.Invoke();
        Debug.Log("GuildManager: fake test contest cleared.");
    }

    // ---- MIGRATED to Firebase ----
    // Any guild member can start/cancel a search — there's no leader-only
    // gate here or server-side; "leader" is just cosmetic post-migration.
    public async Task<bool> SetSearchingForContestAsync(bool searching)
    {
        if (!IsReady || !HasGuild) return false;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", MyGuildId } };
            await CallFirebaseFunction(searching ? "startSearch" : "cancelSearch", args);

            // No need to set MySearchingForContest / call OnSearchingForContestChanged
            // here — the real-time listener on our own guild doc will pick up
            // this exact write an instant later and update every open UI
            // element on every device, including this one, consistently.
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: SetSearchingForContestAsync (Firebase) failed — {e.Message}");
            return false;
        }
    }

    // ---- BreedRabbits periodic reporting ----
    // BreedRabbits is a continuous passive stat (rabbits breeding on their
    // own over time), not a discrete event like a hatch or a truck send —
    // there's no single call site to hook. Instead, while it's the active
    // contest objective, periodically report the delta since the last
    // report. Every other objective type is discrete and reported directly
    // from its own game event (see BarnSpawner, PrestigeManager,
    // QuestManager, TruckController, LeaderBoardUI).
    private Coroutine breedRabbitsReportCoroutine;
    private long _lastReportedBreedTotal;

    private void StartBreedRabbitsReportingIfNeeded()
    {
        if (MyContestObjectiveType != "BreedRabbits") return;
        StopBreedRabbitsReporting();
        _lastReportedBreedTotal = ComputeBaselineForObjective("BreedRabbits");
        breedRabbitsReportCoroutine = StartCoroutine(BreedRabbitsReportLoop());
    }

    private void StopBreedRabbitsReporting()
    {
        if (breedRabbitsReportCoroutine != null)
        {
            StopCoroutine(breedRabbitsReportCoroutine);
            breedRabbitsReportCoroutine = null;
        }
    }

    private IEnumerator BreedRabbitsReportLoop()
    {
        while (MyInContest && MyContestObjectiveType == "BreedRabbits")
        {
            yield return new WaitForSeconds(15f);

            if (!MyInContest || MyContestObjectiveType != "BreedRabbits") yield break;

            long currentTotal = ComputeBaselineForObjective("BreedRabbits");
            long delta = currentTotal - _lastReportedBreedTotal;
            if (delta > 0)
            {
                _lastReportedBreedTotal = currentTotal;
                _ = ReportContestProgressAsync("BreedRabbits", delta);
            }
        }
    }

    long ComputeBaselineForObjective(string objectiveType)
    {
        switch (objectiveType)
        {
            case "BreedRabbits":
                return GameManager.Instance != null ? GameManager.Instance.lifetimeRabbitsBred : 0;
            default:
                return 0;
        }
    }

    // TryMatchmakeAsync removed — matchmaking is now fully server-driven via
    // the scheduled matchmakingSweep Cloud Function. No client trigger needed.

    // ---- MIGRATED to Firebase ---- kept for compatibility with existing
    // call sites; leaderId param is now unused (any member can cancel).
    public async Task CancelMySearchAsync(string guildId, string leaderId = null)
    {
        if (!IsReady || string.IsNullOrEmpty(guildId)) return;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", guildId } };
            await CallFirebaseFunction("cancelSearch", args);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: CancelMySearchAsync (Firebase) failed — {e.Message}");
        }
    }

    [Serializable]
    private class TransferLeadershipResponse
    {
        public bool success;
        public string newLeaderId;
        public string newLeaderName;
        public string reason;
    }

    public async Task<bool> TransferGuildLeadershipAsync(string guildId, string oldLeaderId)
    {
        if (!IsReady || string.IsNullOrEmpty(guildId) || string.IsNullOrEmpty(oldLeaderId)) return false;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", guildId }, { "oldLeaderId", oldLeaderId } };
            TransferLeadershipResponse response = await CloudCodeService.Instance.CallEndpointAsync<TransferLeadershipResponse>("TransferGuildLeadership", args);

            if (!response.success)
                Debug.Log($"GuildManager: TransferGuildLeadershipAsync — {response.reason}");

            return response.success;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: TransferGuildLeadershipAsync (Cloud Code) failed — {e.Message}");
            return false;
        }
    }

    public async Task<bool> DisbandGuildAsync(string guildId, string leaderId)
    {
        if (!IsReady || string.IsNullOrEmpty(guildId) || string.IsNullOrEmpty(leaderId)) return false;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", guildId }, { "leaderId", leaderId } };
            DisbandGuildResponse response = await CloudCodeService.Instance.CallEndpointAsync<DisbandGuildResponse>("DisbandGuild", args);

            if (!response.success)
                Debug.LogWarning($"GuildManager: DisbandGuildAsync failed for guild {guildId} — {response.reason}");

            return response.success;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: DisbandGuildAsync (Cloud Code) failed for guild {guildId} — {e.Message}");
            return false;
        }
    }

    [Serializable]
    private class AddRemoveMemberResponse
    {
        public bool success;
        public int memberCount;
        public int remainingCount;
        public string reason;
    }

    [Serializable]
    private class MemberInfo
    {
        public string playerId;
        public string playerName;
        public int rank;
        public long score;
        public string country;
        public int bigBangCount;
        public long lifetimeRabbits;
        public string equippedNickname;
        public string equippedBackground;
        public long contestProgress;
    }

    [Serializable]
    private class GetMembersWithRanksResponse
    {
        public bool success;
        public MemberInfo[] members;
        public string reason;
    }

    // ---- Join ----  [MIGRATED to Firebase]
    public async Task<bool> JoinGuild(GuildInfo guild, LeaderboardUI leaderboardUI, int guildRank = 0)
    {
        if (!MeetsBigBangRequirement())
        {
            Debug.LogWarning($"GuildManager: needs {minBigBangsToUnlock} BigBangs to join a guild.");
            return false;
        }
        if (HasGuild)
        {
            Debug.LogWarning("GuildManager: already in a guild — leave it first.");
            return false;
        }
        if (guild == null || string.IsNullOrEmpty(guild.guildId))
        {
            Debug.LogWarning("GuildManager: invalid guild.");
            return false;
        }
        if (guild.memberCount >= maxMembers)
        {
            Debug.LogWarning("GuildManager: this guild is full.");
            return false;
        }

        string myPlayerId = AuthenticationService.Instance.PlayerId;
        string myPlayerName = AuthenticationService.Instance.PlayerName;

        Dictionary<string, object> response;
        try
        {
            var args = new Dictionary<string, object>
            {
                { "guildId", guild.guildId },
                { "playerName", myPlayerName }
            };
            response = await CallFirebaseFunction("joinGuild", args);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: JoinGuild (Firebase) failed — {e.Message}");
            return false;
        }

        if (response == null || !(bool)response["success"])
        {
            Debug.LogWarning("GuildManager: JoinGuild — server rejected the request.");
            return false;
        }

        MyGuildId = guild.guildId;
        MyGuildName = guild.guildName;
        MyGuildLogoShape = guild.logoShape;
        MyGuildLogoColor = guild.logoColor;
        MyGuildLogoIconTintColor = guild.logoIconTintColor;
        IsGuildLeader = false;
        MyGuildLeaderId = guild.leaderId; // vestigial post-migration, kept for compatibility with not-yet-migrated search/contest code
        MyGuildRank = guildRank;
        MyGuildContestsWon = guild.contestsWon;
        MyGuildMemberCount = guild.memberCount + 1;
        MySearchingForContest = guild.searchingForContest;
        SaveLocalGuildState();
        AttachGuildListener(guild.guildId);

        LeaderboardManager.Instance?.SubmitScoreNow();

        OnMyGuildChanged?.Invoke();

        return true;
    }

    // ---- Leave ----  [MIGRATED to Firebase]
    public async void LeaveGuild()
    {
        if (!HasGuild) return;

        string leftGuildId = MyGuildId;
        DetachGuildListener();
        StopBreedRabbitsReporting();

        try
        {
            var args = new Dictionary<string, object> { { "guildId", leftGuildId } };
            await CallFirebaseFunction("leaveGuild", args);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: LeaveGuild (Firebase) failed — {e.Message}");
            // Still clear local state below even on network failure — the
            // player expects to leave immediately from their own device's
            // perspective; a failed server call here would otherwise trap
            // them in a guild they think they've left.
        }

        MyGuildId = "";
        MyGuildName = "";
        MyGuildLogoShape = "";
        MyGuildLogoColor = "";
        MyGuildLogoIconTintColor = "";
        IsGuildLeader = false;
        MyGuildLeaderId = "";
        MyGuildRank = 0;
        MyGuildContestsWon = 0;
        MySearchingForContest = false;
        MyInContest = false;
        MyContestOpponentGuildId = "";
        MyContestOpponentGuildName = "";
        MyContestOpponentLogoShape = "";
        MyContestOpponentLogoColor = "";
        MyContestOpponentLogoIconTintColor = "";
        MyContestOpponentLeaderId = "";
        MyContestObjectiveType = "";
        MyContestRewardAmount = 0;
        MyContestObjectiveTarget = 0;
        MyContestProgressBaseline = 0;
        MyContestPhase = "";
        MyContestResult = "";
        MyContestRewardPerMember = 0;
        MyContestResultsUntilUnix = 0;
        IsFakeTestContestActive = false;
        SaveLocalGuildState();

        LeaderboardManager.Instance?.SubmitScoreNow();

        OnMyGuildChanged?.Invoke();
    }

    // ---- MIGRATED to Firebase ----
    public async Task<bool> HasAnyMemberAsync(string guildId, string leaderId = null)
    {
        if (string.IsNullOrEmpty(guildId)) return false;
        try
        {
            var membersSnap = await FirebaseFirestore.DefaultInstance
                .Collection("guilds").Document(guildId).Collection("members")
                .Limit(1).GetSnapshotAsync();
            return membersSnap.Count > 0;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: HasAnyMemberAsync failed — {e.Message}");
            return true; // fail-safe: assume populated on error
        }
    }

    // Reads the guild's real members subcollection from Firestore — this is
    // the SERVER'S source of truth for contestProgress (incremented by
    // ReportContestProgressAsync), unlike each player's own leaderboard
    // metadata, which is only ever accurate for the BreedRabbits objective
    // (that's the only one MyContestProgress's local baseline-diff covers)
    // and only refreshes every submitInterval seconds. leaderboardUI, if
    // provided, is used purely to enrich the display (bigBangCount,
    // lifetimeRabbits, country, cosmetics) from its cache, keyed by
    // playerId — Firestore member docs only store what the guild backend
    // itself needs (playerName, contestProgress), not the full profile.
    public async Task<List<(int rank, LeaderboardUI.RowData row)>> GetGuildMembersWithTrueRanksAsync(string guildId, LeaderboardUI leaderboardUI = null)
    {
        var result = new List<(int, LeaderboardUI.RowData)>();
        if (string.IsNullOrEmpty(guildId)) return result;

        try
        {
            var membersSnap = await FirebaseFirestore.DefaultInstance
                .Collection("guilds").Document(guildId).Collection("members").GetSnapshotAsync();

            Dictionary<string, LeaderboardUI.RowData> cachedById = null;
            if (leaderboardUI != null)
            {
                cachedById = new Dictionary<string, LeaderboardUI.RowData>();
                foreach (var row in leaderboardUI.GetCurrentEntries())
                    cachedById[row.playerId] = row;
            }

            var rows = new List<LeaderboardUI.RowData>();
            foreach (var doc in membersSnap.Documents)
            {
                var d = doc.ToDictionary();
                string playerId = doc.Id;
                string playerName = d.GetValueOrDefault("playerName", "Player") as string ?? "Player";
                long contestProgress = Convert.ToInt64(d.GetValueOrDefault("contestProgress", 0L));

                LeaderboardUI.RowData row = (cachedById != null && cachedById.TryGetValue(playerId, out LeaderboardUI.RowData cached))
                    ? cached
                    : new LeaderboardUI.RowData { playerId = playerId };

                row.playerName = playerName; // Firestore's copy is the freshest
                row.contestProgress = contestProgress; // server value always wins — this is what fixes progress staying stuck at 0
                rows.Add(row);
            }

            // Highest contributor first — most useful ordering inside a contest panel.
            rows.Sort((a, b) => b.contestProgress.CompareTo(a.contestProgress));
            for (int i = 0; i < rows.Count; i++)
                result.Add((i + 1, rows[i]));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: GetGuildMembersWithTrueRanksAsync failed — {e.Message}");
        }

        return result;
    }

    // Reports progress toward the current contest objective — call this
    // wherever the client tracks a stat that counts toward the contest.
    // objectiveType must match MyContestObjectiveType EXACTLY, or the call
    // is silently ignored — this is what prevents e.g. a rabbit hatch from
    // accidentally counting toward a "SendTrucks" contest just because
    // some caller forgot to check which objective is currently active.
    //
    // SCALABILITY: progress is no longer sent one Cloud Function call per
    // game event (e.g. one call per hatched rabbit — at scale that's
    // millions of invocations). It's buffered locally and flushed as ONE
    // call every ContestProgressFlushSeconds, plus immediately on app
    // pause/quit so nothing is lost. Callers are unchanged.
    private const float ContestProgressFlushSeconds = 10f;
    private long _pendingContestProgress;
    private string _pendingContestObjectiveType;
    private string _pendingContestGuildId;
    private Coroutine _contestProgressFlushCoroutine;
    private bool _contestProgressFlushInFlight;

    public Task ReportContestProgressAsync(string objectiveType, long amount)
    {
        if (!IsReady || !HasGuild || !MyInContest || amount <= 0) return Task.CompletedTask;
        if (objectiveType != MyContestObjectiveType) return Task.CompletedTask;

        // Contest/guild changed since the last buffered amount — drop the
        // stale buffer rather than crediting it to the wrong contest.
        if (_pendingContestObjectiveType != objectiveType || _pendingContestGuildId != MyGuildId)
        {
            _pendingContestProgress = 0;
            _pendingContestObjectiveType = objectiveType;
            _pendingContestGuildId = MyGuildId;
        }

        _pendingContestProgress += amount;

        if (_contestProgressFlushCoroutine == null)
            _contestProgressFlushCoroutine = StartCoroutine(ContestProgressFlushLoop());

        return Task.CompletedTask;
    }

    private IEnumerator ContestProgressFlushLoop()
    {
        yield return new WaitForSeconds(ContestProgressFlushSeconds);
        _contestProgressFlushCoroutine = null;
        _ = FlushContestProgressAsync();
    }

    private async Task FlushContestProgressAsync()
    {
        if (_contestProgressFlushInFlight || _pendingContestProgress <= 0) return;

        // Only send if that contest is still the active one.
        if (!IsReady || !MyInContest || _pendingContestGuildId != MyGuildId || _pendingContestObjectiveType != MyContestObjectiveType)
        {
            _pendingContestProgress = 0;
            return;
        }

        long amount = _pendingContestProgress;
        _pendingContestProgress = 0;
        _contestProgressFlushInFlight = true;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", MyGuildId }, { "amount", (double)amount } };
            await CallFirebaseFunction("reportContestProgress", args);
        }
        catch (Exception e)
        {
            // Put it back so the next flush retries it.
            _pendingContestProgress += amount;
            if (_contestProgressFlushCoroutine == null && isActiveAndEnabled)
                _contestProgressFlushCoroutine = StartCoroutine(ContestProgressFlushLoop());
            Debug.LogWarning($"GuildManager: contest progress flush failed — {e.Message}");
        }
        finally
        {
            _contestProgressFlushInFlight = false;
        }
    }

    void OnApplicationQuit()
    {
        _ = FlushContestProgressAsync();
    }

    // Keeps this guild's denormalized totalLifetimeRabbits (server-side,
    // guilds/{id}.totalLifetimeRabbits) roughly in sync with this player's
    // own lifetime rabbit count — server computes the delta against this
    // player's own last-reported value, so it's safe to call repeatedly
    // with the current running total rather than an increment. Needed so
    // matchmakingSweep can scale a guild's BreedRabbits contest target off
    // its OWN total lifetime rabbits at match time (see index.js). Call
    // this periodically while in a guild — LeaderboardManager's existing
    // score-submission loop is the natural place, same cadence as the
    // leaderboard itself.
    //
    // SCALABILITY: throttled to at most once per LifetimeRabbitsReportSeconds
    // and only when the value actually changed — it's only read at match
    // time, so a few minutes of staleness is harmless, and this cuts the
    // call volume ~10x versus every leaderboard submit.
    private const float LifetimeRabbitsReportSeconds = 300f;
    private float _lastLifetimeRabbitsReportTime = -999999f;
    private long _lastReportedLifetimeRabbits = -1;
    private string _lastReportedLifetimeRabbitsGuildId;

    public async Task ReportLifetimeRabbitsAsync(long lifetimeRabbits)
    {
        if (!IsReady || !HasGuild || lifetimeRabbits < 0) return;

        bool guildChanged = _lastReportedLifetimeRabbitsGuildId != MyGuildId;
        if (!guildChanged)
        {
            if (lifetimeRabbits == _lastReportedLifetimeRabbits) return;
            if (Time.realtimeSinceStartup - _lastLifetimeRabbitsReportTime < LifetimeRabbitsReportSeconds) return;
        }

        _lastLifetimeRabbitsReportTime = Time.realtimeSinceStartup;
        _lastReportedLifetimeRabbits = lifetimeRabbits;
        _lastReportedLifetimeRabbitsGuildId = MyGuildId;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", MyGuildId }, { "lifetimeRabbits", (double)lifetimeRabbits } };
            await CallFirebaseFunction("reportLifetimeRabbits", args);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: ReportLifetimeRabbitsAsync (Firebase) failed — {e.Message}");
        }
    }

    // Sums every member's contestProgress field directly from Firestore —
    // used for an on-demand accurate read (e.g. opening the contest panel).
    // For live UI ticking, MyContestProgress (local baseline diff) is cheaper.
    public async Task<long> GetGuildContestProgressAsync(string guildId, string leaderId = null)
    {
        if (string.IsNullOrEmpty(guildId)) return 0;
        try
        {
            var membersSnap = await FirebaseFirestore.DefaultInstance
                .Collection("guilds").Document(guildId).Collection("members").GetSnapshotAsync();

            long total = 0;
            foreach (var m in membersSnap.Documents)
            {
                var d = m.ToDictionary();
                total += Convert.ToInt64(d.GetValueOrDefault("contestProgress", 0L));
            }
            return total;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: GetGuildContestProgressAsync failed — {e.Message}");
            return 0;
        }
    }

    // ---- Contest reward claiming (Results phase) ----
    // Called when the winning-guild player taps their (now green/clickable)
    // ContestReward image. The server is the sole authority on whether this
    // is allowed and for how much — this just relays that decision. The
    // caller (UI) is responsible for actually crediting the returned amount
    // via GameManager.Instance.AddCarrots(amount) on success.
    public async Task<(bool success, bool alreadyClaimed, long amount)> ClaimContestRewardAsync()
    {
        if (!IsReady || !HasGuild) return (false, false, 0);

        try
        {
            var args = new Dictionary<string, object> { { "guildId", MyGuildId } };
            Dictionary<string, object> response = await CallFirebaseFunction("claimContestReward", args);

            if (response == null || !(bool)response["success"]) return (false, false, 0);

            bool alreadyClaimed = response.TryGetValue("alreadyClaimed", out object ac) && ac is bool acb && acb;
            long amount = Convert.ToInt64(response.GetValueOrDefault("amount", 0L));
            return (true, alreadyClaimed, amount);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: ClaimContestRewardAsync (Firebase) failed — {e.Message}");
            return (false, false, 0);
        }
    }

    // Reads the LOCAL PLAYER's own claimedReward flag directly from
    // Firestore — used to correctly show an already-claimed state (the
    // checkmark, not the clickable reward) if they claimed from a
    // different device, or are just reopening this panel after claiming.
    public async Task<bool> GetMyContestRewardClaimedAsync(string guildId)
    {
        if (string.IsNullOrEmpty(guildId)) return false;

        try
        {
            string myPlayerId = AuthenticationService.Instance.PlayerId;
            DocumentSnapshot snap = await FirebaseFirestore.DefaultInstance
                .Collection("guilds").Document(guildId).Collection("members").Document(myPlayerId).GetSnapshotAsync();
            if (!snap.Exists) return false;

            var d = snap.ToDictionary();
            return d.TryGetValue("claimedReward", out object v) && v is bool claimed && claimed;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: GetMyContestRewardClaimedAsync failed — {e.Message}");
            return false;
        }
    }

    // ---- Auto-claim: contest reward missed during the Results window ----
    // If the player didn't open the app while their guild's 24-hour
    // Results phase was still up, resultsExpirySweep (server-side) already
    // stashed their unclaimed share as pendingRewardAmount on their own
    // member doc, right before clearing that guild's Results fields. This
    // reads it back (and clears it) the moment this session starts. Both
    // anti-abuse requirements — still a member of the guild that won, and
    // hasn't already claimed (manually or via a previous auto-claim) — are
    // enforced server-side by claimPendingReward itself; see index.js.
    public async Task ClaimPendingRewardIfAnyAsync()
    {
        if (!IsReady || !HasGuild) return;

        try
        {
            var args = new Dictionary<string, object> { { "guildId", MyGuildId } };
            Dictionary<string, object> response = await CallFirebaseFunction("claimPendingReward", args);
            if (response == null || !(bool)response["success"]) return;

            long amount = Convert.ToInt64(response.GetValueOrDefault("amount", 0L));
            if (amount > 0)
                GameManager.Instance?.AddCarrots(amount);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GuildManager: ClaimPendingRewardIfAnyAsync failed — {e.Message}");
        }
    }

    [Serializable]
    private class CheckWinnerResponse
    {
        public bool success;
        public string winnerGuildId;
        public string reason;
    }

    public async Task<string> CheckContestWinnerAsync()
    {
        return null; // migrated in next pass — contest resolution now happens server-side via contestResolutionSweep anyway
    }

    public List<(int rank, LeaderboardUI.RowData row)> GetGuildMembers(string guildId, LeaderboardUI leaderboardUI)
    {
        List<(int rank, LeaderboardUI.RowData row)> result = new List<(int, LeaderboardUI.RowData)>();
        if (string.IsNullOrEmpty(guildId)) return result;

        if (leaderboardUI != null)
        {
            List<LeaderboardUI.RowData> entries = leaderboardUI.GetCurrentEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].guildId == guildId)
                    result.Add((i + 1, entries[i]));
            }
        }

        result.AddRange(GuildDevTools.GetPersistedFakeMembers(guildId));

        return result;
    }

    void OnApplicationPause(bool paused)
    {
        // The real-time Firestore listener keeps receiving updates even
        // while backgrounded on most platforms, but re-attaching on resume
        // is a cheap safety net in case the connection was dropped.
        if (paused)
        {
            // Don't lose buffered contest progress when backgrounded.
            _ = FlushContestProgressAsync();
            return;
        }

        if (IsReady && HasGuild)
            AttachGuildListener(MyGuildId);
    }

    void OnDestroy()
    {
        DetachGuildListener();
    }
}
