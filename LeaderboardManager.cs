using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Leaderboards;
using Unity.Services.Leaderboards.Models;

// Handles Unity Gaming Services init/sign-in, periodic score submission
// (the player's lifetimeRabbits, used for ranking, plus extra display
// data carried in Metadata), and on-demand top-scores fetching.
//
// Requires the "Authentication" and "Leaderboards" UGS packages, and a
// project linked to Unity Cloud (Project Settings > Services).
//
// The leaderboard on the Dashboard should be configured with Update Type =
// Overwrite ("Latest score"). lifetimeRabbits never decreases, so this
// mostly behaves like "Keep Best" would anyway, but Overwrite keeps this
// robust if the ranking criteria ever changes back to a fluctuating stat.
public class LeaderboardManager : MonoBehaviour
{
    public static LeaderboardManager Instance;

    // Same key AuthenticationPopUp.cs uses — duplicated here since that
    // one's private, kept as an identical literal rather than exposing it
    // just for this one check.
    private const string HasSetNicknameKey = "RabbitUniverse_HasSetNickname";

    [Header("UGS")]
    [Tooltip("Must match the Leaderboard ID created in the Unity Cloud Dashboard.")]
    public string leaderboardId = "Leaderboard_V1";

    [Header("Score Submission")]
    [Tooltip("How often (seconds) this player's current rabbit count is pushed to the leaderboard.")]
    public float submitInterval = 30f;

    [Header("Fetch")]
    [Tooltip("Leaderboard displays a maximum of 999 ranked players.")]
    public int topEntryCount = 999;

    public bool IsReady { get; private set; } = false;

    private bool isSubmitting = false;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    void OnEnable()
    {
        if (CosmeticEquipManager.Instance != null)
            CosmeticEquipManager.Instance.OnEquipChanged += OnEquipChanged;
    }

    void OnDisable()
    {
        if (CosmeticEquipManager.Instance != null)
            CosmeticEquipManager.Instance.OnEquipChanged -= OnEquipChanged;
    }

    // Resubmit right away on equip/unequip, instead of waiting up to
    // submitInterval seconds — other players should see a new equip
    // change promptly rather than stale for a while.
    void OnEquipChanged(CosmeticType type)
    {
        if (IsReady) SubmitScoreNow();
    }

    async void Start()
    {
        await InitializeAsync();
        StartCoroutine(SubmitLoop());
    }

    async Task InitializeAsync()
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            // Also sign into Firebase, matching this same Player ID — needed
            // for the new guild/contest backend.
            if (FirebaseBridge.Instance != null)
                await FirebaseBridge.Instance.SignInToFirebase();

            IsReady = true;
        }
        catch (Exception e)
        {
            Debug.LogError("LeaderboardManager: UGS init failed — " + e.Message);
        }
    }

    IEnumerator SubmitLoop()
    {
        // Small delay so GameManager/SaveManager have loaded the real
        // totalRabbits value before we ever submit a score.
        yield return new WaitForSeconds(2f);
        while (true)
        {
            if (IsReady) SubmitScoreNow();
            yield return new WaitForSeconds(submitInterval);
        }
    }

    // Submits immediately whenever the app goes to background or closes,
    // instead of leaving the leaderboard stale by up to submitInterval
    // seconds for the entire time the player is offline. This doesn't
    // (and can't) make other players see this player's score continue
    // rising while their app is genuinely closed — that would need
    // server-side logic — but it does close the gap between "true score
    // at the moment of closing" and "what was last actually submitted."
    void OnApplicationPause(bool paused)
    {
        if (paused && IsReady) SubmitScoreNow();
    }

    void OnApplicationQuit()
    {
        if (IsReady) SubmitScoreNow();
    }

    // Fire-and-forget submission of the player's lifetime rabbit count
    // (used for ranking/sorting) plus extra row data via Metadata.
    public async void SubmitScoreNow()
    {
        if (!IsReady || GameManager.Instance == null) return;
        if (isSubmitting) return;

        // Never submit a score before the player has actually set a
        // nickname. Every anonymous UGS sign-in (which happens fresh on
        // every test install/reinstall/cleared app data) would otherwise
        // immediately submit a junk 0-score entry the instant the game
        // launches, polluting the leaderboard with orphaned test entries
        // that never represent a real player.
        if (!PlayerPrefs.HasKey(HasSetNicknameKey)) return;

        isSubmitting = true;
        try
        {
            double score = (double)GameManager.Instance.lifetimeRabbits;

            LeaderboardScoreMetadata metadata = new LeaderboardScoreMetadata
            {
                bigBangCount    = PrestigeManager.Instance != null ? PrestigeManager.Instance.prestigeCount : 0,
                lifetimeRabbits = GameManager.Instance.lifetimeRabbits,
                country         = LocationService.Instance != null && LocationService.Instance.IsReady ? LocationService.Instance.CountryCode : "",
                latitude        = LocationService.Instance != null && LocationService.Instance.IsReady ? LocationService.Instance.Latitude : 0f,
                longitude       = LocationService.Instance != null && LocationService.Instance.IsReady ? LocationService.Instance.Longitude : 0f,
                lastOfflineEarnings = SaveManager.LastOfflineEarnings,
                equippedNickname    = CosmeticEquipManager.Instance != null ? (CosmeticEquipManager.Instance.GetEquipped(CosmeticType.Nickname) ?? "") : "",
                nicknameColorIndex  = CosmeticEquipManager.Instance != null && !string.IsNullOrEmpty(CosmeticEquipManager.Instance.GetEquipped(CosmeticType.Nickname))
                                        ? CosmeticEquipManager.Instance.GetActivationCount(CosmeticEquipManager.Instance.GetEquipped(CosmeticType.Nickname))
                                        : 0,
                equippedBackground  = CosmeticEquipManager.Instance != null ? (CosmeticEquipManager.Instance.GetEquipped(CosmeticType.Background) ?? "") : "",
                guildId          = GuildManager.Instance != null ? (GuildManager.Instance.MyGuildId ?? "") : "",
                guildName        = GuildManager.Instance != null ? (GuildManager.Instance.MyGuildName ?? "") : "",
                guildLogoShape   = GuildManager.Instance != null ? (GuildManager.Instance.MyGuildLogoShape ?? "") : "",
                guildLogoColor   = GuildManager.Instance != null ? (GuildManager.Instance.MyGuildLogoColor ?? "") : "",
                contestProgress  = GuildManager.Instance != null ? GuildManager.Instance.MyContestProgress : 0
            };

            await LeaderboardsService.Instance.AddPlayerScoreAsync(
                leaderboardId,
                score,
                new AddPlayerScoreOptions { Metadata = metadata }
            );

            // Same cadence as the leaderboard score itself — keeps the
            // guild's denormalized totalLifetimeRabbits fresh server-side,
            // which is what lets a guild's BreedRabbits contest target
            // scale off its own members' real lifetime rabbit count at
            // match time (see GuildManager.ReportLifetimeRabbitsAsync).
            if (GuildManager.Instance != null && GuildManager.Instance.HasGuild)
                _ = GuildManager.Instance.ReportLifetimeRabbitsAsync(GameManager.Instance.lifetimeRabbits);
        }
        catch (Exception e)
        {
            Debug.LogWarning("LeaderboardManager: score submit failed — " + e.Message);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    // Returns top entries (already sorted by the service, metadata included), or null on failure.
    public async Task<List<LeaderboardEntry>> FetchTopScoresAsync()
    {
        if (!IsReady) return null;
        try
        {
            var response = await LeaderboardsService.Instance.GetScoresAsync(
                leaderboardId,
                new GetScoresOptions { Limit = topEntryCount, IncludeMetadata = true }
            );
            return new List<LeaderboardEntry>(response.Results);
        }
        catch (Exception e)
        {
            Debug.LogWarning("LeaderboardManager: fetch failed — " + e.Message);
            return null;
        }
    }
    // Returns the local player's own entry (score, TRUE global rank, and
    // metadata) regardless of whether they're inside the top-999 list.
    // Returns null if the player hasn't submitted a score yet, or on failure.
    public async Task<LeaderboardEntry> FetchOwnScoreAsync()
    {
        if (!IsReady) return null;
        try
        {
            return await LeaderboardsService.Instance.GetPlayerScoreAsync(
                leaderboardId,
                new GetPlayerScoreOptions { IncludeMetadata = true }
            );
        }
        catch (Exception e)
        {
            Debug.LogWarning("LeaderboardManager: own-score fetch failed — " + e.Message);
            return null;
        }
    }
}

// Extra per-score data that doesn't fit the base score/name fields.
// Serialized to/from JSON automatically by the Leaderboards SDK.
[Serializable]
public class LeaderboardScoreMetadata
{
    public int bigBangCount;
    public long lifetimeRabbits;
    public string country; // ISO country code (e.g. "FR")
    public float latitude;
    public float longitude;
    public long lastOfflineEarnings;

    // Currently equipped cosmetics — empty string means nothing equipped
    // for that slot. Read by anyone viewing this player's leaderboard
    // row/profile, so cosmetics stay visible even while the player is offline.
    public string equippedNickname;
    // Which color this player has cycled the Colored Nickname cosmetic to
    // (activation count at time of last submit — same 1-based/modulo
    // scheme LeaderboardRowUI already uses locally). Without this, every
    // OTHER player's row fell back to CosmeticEquipManager.Instance
    // (the LOCAL device's own cycling history) to pick a color, which is
    // why every row but your own always rendered as color index 0 (blue)
    // regardless of what that player actually had equipped.
    public int nicknameColorIndex;
    public string equippedBackground;

    // Guild affiliation — self-tagged on the PLAYER's own entry (never
    // written by anyone else), which is what makes instant, leader-offline
    // joins possible: joining a guild only ever requires writing to your
    // own score entry. guildId empty/null means "no guild". guildName,
    // guildLogoShape, and guildLogoColor are a denormalized COPY of the
    // guild's own display info at the time of joining — cheap for anyone
    // viewing this player's row to show "member of [Guild Name]" without
    // a second fetch, but can go stale if the guild is later renamed/
    // restyled until this player's metadata resubmits (e.g. next
    // submitInterval tick) or GuildManager explicitly refreshes it.
    public string guildId;
    public string guildName;
    public string guildLogoShape;
    public string guildLogoColor; // hex string, e.g. "#3388FF"

    // This player's own progress toward the CURRENT guild contest
    // objective (if any) — a delta computed client-side against a
    // baseline snapshotted when the contest started, not a lifetime
    // total. Guild-wide contest progress is the SUM of every member's
    // value here, computed server-side via GetGuildContestProgress.
    public long contestProgress;
}
