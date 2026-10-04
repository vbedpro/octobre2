using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// Autosaves core game state to PlayerPrefs (as JSON) on an interval, on
// pause, and on quit. Loads on startup (one frame after every other
// manager's Start(), so our loaded values override their defaults instead
// of being overwritten by them).
//
// This is the source of truth needed for a future leaderboard: without it,
// every player's totalRabbits resets to 0 on relaunch.
//
// IMPORTANT: Common/Epic upgrade rows are matched by GameObject name (e.g.
// "Row_HutCapacity1"), NOT by category/type. There can be multiple rows
// sharing the same category (multiple tiers), so category alone isn't a
// unique key — every row's name in the scene must stay unique and stable
// across sessions for save/load to line up correctly.
public class SaveManager : MonoBehaviour
{
    public static SaveManager Instance;

    [Header("Autosave")]
    public float autosaveInterval = 30f;

    [Header("Offline Earnings")]
    [Tooltip("Baseline offline production window, free for every player. The Night Shift Epic upgrade adds +5 min per level ON TOP of this — see ApplyOfflineEarnings().")]
    public float offlineEarningsCapSeconds = 120f; // 2 minutes

    // Most recent offline earnings total, readable by LeaderboardManager
    // when it next submits a score (shortly after launch). Resets to 0
    // each time this calculation runs, including when it legitimately
    // earns nothing — stays 0 forever for players who never reach the
    // lifetimeRabbits >= 1 gate below.
    public static long LastOfflineEarnings = 0;

    // Fired once, right after loading, if any offline earnings were
    // awarded. Subscribe in Awake()/Start() — SaveManager deliberately
    // loads one frame late (see Start() below), so subscribers set up on
    // frame 0 are guaranteed to be listening before this fires.
    public static event System.Action<long> OnOfflineEarningsCalculated;

    private const string SaveKey = "RabbitUniverse_SaveData_v1";
    // A second, separate PlayerPrefs key holding the last known-GOOD save —
    // only updated right after a save that we know parsed back correctly.
    // If the primary save is ever found corrupt on load, we fall back to
    // this instead of silently starting the player over from zero.
    private const string BackupSaveKey = "RabbitUniverse_SaveData_v1_backup";
    // If a load ever fails on BOTH the primary and the backup, the corrupt
    // string is preserved here (never overwritten) so it can be inspected/
    // recovered manually later, instead of being silently destroyed by
    // the next autosave.
    private const string CorruptSaveKey = "RabbitUniverse_SaveData_v1_corrupt";

    // True once LoadGame() has run and EITHER successfully loaded existing
    // data OR confirmed there was genuinely no save to load (fresh player).
    // SaveGame() refuses to run until this is true, specifically to
    // prevent ever overwriting a real save with blank/default state after
    // a failed parse.
    private bool safeToSave = false;

    [System.Serializable]
    public class SaveData
    {
        // ── Global (shared across every planet) ─────────────────────
        public long lifetimeRabbits;
        public long lifetimeRabbitsBred;
        public long totalCarrots;
        public int prestigeCount;

        // Extra Farm is a GLOBAL unlock progression (same 4 levels
        // regardless of which planet's shop you're viewing), so it's
        // saved here directly rather than inside any one planet's data.
        public int extraFarmLevel;

        // Epic upgrades — keyed by GameObject name (unique per row). Global/permanent.
        public string[] epicUpgradeId;
        public int[] epicUpgradeLevel;

        // Which planet was active when this save was written, and every
        // planet's own independent farm state (resources, huts, shop
        // levels, truck cooldown, quest buffs/debuffs) — see PlanetData.
        public string currentPlanet;
        public PlanetData[] planets;

        // Real-world UTC timestamp (ISO 8601) of when this save was
        // written — used to compute offline earnings on next load.
        public string lastSaveTimestampUtc;

        // Achievement progress — every achievement's raw stat value and
        // claimed-tier state, matched by id on restore.
        public AchievementsManager.AchievementSaveEntry[] achievements;
        public InventoryManager.InventorySaveEntry[] inventory;
        public BoostActivationManager.BoostSaveEntry[] activeBoosts;
        public CosmeticEquipManager.EquipSaveEntry[] equippedCosmetics;
        public CosmeticEquipManager.ActivationCountEntry[] cosmeticActivationCounts;
        public string explosiveReproductionPlanet;
        public long explosiveReproductionExpiryEpoch;
        public long freeAdRewardCooldownEndEpoch;
        public bool hasRespondedToFirstQuest;
        public bool giftBoxIsSpawned;
        public string giftBoxCountry;
        public float giftBoxLat;
        public float giftBoxLon;
        public long giftBoxNextSpawnEpoch;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    IEnumerator Start()
    {
        // Wait one frame so every other manager's own Start() (which sets
        // default / level-1 state) has already run before we override it.
        yield return null;

        LoadGame();

        // Guarantee a save file exists from the very first session, so the
        // leaderboard has something to read even before the first autosave
        // tick — but ONLY if LoadGame() actually confirmed it's safe to
        // (either genuinely fresh, or successfully loaded). If a save
        // existed but failed to parse, safeToSave stays false and THIS
        // CALL IS SKIPPED — otherwise we'd immediately overwrite the only
        // copy of the player's real progress with blank defaults.
        if (safeToSave)
            SaveGame();
        else
            Debug.LogError("SaveManager: existing save failed to load and could not be recovered from backup. " +
                "Refusing to autosave over it — the corrupt data has been preserved under '" + CorruptSaveKey + "' for inspection.");

        StartCoroutine(AutosaveLoop());
    }

    IEnumerator AutosaveLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(autosaveInterval);
            if (safeToSave) SaveGame();
        }
    }

    // Timestamp captured the instant the app is backgrounded — used on
    // resume to check how long it was actually paused for, so a short
    // background (checking a notification, switching tabs briefly)
    // doesn't unnecessarily trigger offline-earnings math, while a
    // genuine 15+ second background does — matching what happens on a
    // full app close/reopen, without requiring an actual close.
    private System.DateTime? pausedAtUtc = null;
    [Tooltip("Minimum real seconds backgrounded before offline earnings apply on resume, without requiring a full app close.")]
    public float minBackgroundSecondsForOfflineEarnings = 15f;

    void OnApplicationPause(bool paused)
    {
        if (!paused)
        {
            // Player is back — cancel the pending reminder so it doesn't
            // fire while they're actively playing.
            NotificationManager.Instance?.CancelOfflineEarningsNotification();

            // Apply offline earnings for the background span itself if it
            // was long enough — this is what lets a simple background
            // (locking the phone, switching apps) grant offline earnings
            // too, not just a full close-and-relaunch.
            if (safeToSave && pausedAtUtc.HasValue)
            {
                double backgroundSeconds = (System.DateTime.UtcNow - pausedAtUtc.Value).TotalSeconds;
                if (backgroundSeconds >= minBackgroundSecondsForOfflineEarnings)
                {
                    // Long enough away to count as "offline mode" — any UI
                    // the player had open when they left is stale by now
                    // (and would otherwise sit on top of / behind whatever
                    // the offline-earnings popup shows next), so snap it
                    // all shut before applying earnings.
                    CloseAllOpenPanelsOnResume();
                    ApplyOfflineEarnings(pausedAtUtc.Value.ToString("o"));
                }
            }
            pausedAtUtc = null;

            return;
        }

        // CRITICAL: only save if LoadGame() has actually finished and
        // confirmed real (or genuinely fresh) data is loaded. Without this
        // guard, backgrounding the app (e.g. locking the phone) BEFORE
        // Start()'s one-frame-delayed LoadGame() call has run would save
        // straight over the real save file with untouched default state —
        // wiping all progress. This is exactly what was happening on
        // Android, where OnApplicationPause can fire essentially instantly
        // on backgrounding, before Unity's frame loop even reaches
        // Start(). iOS apparently allows at least one frame to run first,
        // which is why this never reproduced there.
        if (!safeToSave) return;

        pausedAtUtc = System.DateTime.UtcNow;

        // NOTE: does NOT fail the active quest here — backgrounding the
        // app (locking the phone, switching tabs) is not the same as
        // quitting. The quest's own timer (which keeps counting down in
        // real time regardless of whether the app is in the foreground,
        // since Unity simply doesn't run Update() while paused — no
        // frames means no time silently lost either way) is what decides
        // whether it's actually still valid once the player returns. This
        // used to force-fail ANY active quest on every single background,
        // even a few seconds — that was the bug.
        SaveGame();
        NotificationManager.Instance?.ScheduleOfflineEarningsExpired(GetEffectiveOfflineCapSeconds());
    }

    // Closes every panel that has its own "am I open" tracking, the same
    // way PrestigeManager/QuestManager already do before opening their own
    // popups on top of everything else. Safe to call even if none of these
    // happen to be open right now — every Close.../CloseInstant() here is a
    // documented no-op in that case.
    void CloseAllOpenPanelsOnResume()
    {
        if (LeaderboardUI.leaderboardOpen) FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();
        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (SellManager.popupOpen) FindAnyObjectByType<SellManager>()?.CloseSellPopup();
        if (HutUpgradeManager.popupOpen) FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();
        if (SettingsPanelController.settingsOpen) SettingsPanelController.Instance?.CloseSettings();
        if (PrestigeManager.popupOpen) FindAnyObjectByType<PrestigeManager>()?.CloseBigBangPopup();

        // Guild sub-panels and the guild-creation panel don't expose a
        // single static "open" flag the same way — their own Close.../
        // CloseInstant() methods already no-op safely when not open, so
        // just call them unconditionally.
        GuildInfoPanelUI.CloseAllInstant();
        FindAnyObjectByType<GuildCreationPanelUI>()?.CloseInstant();
        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn();
    }

    void OnApplicationQuit()
    {
        // Same guard as OnApplicationPause above — never save over a not-
        // yet-loaded state.
        if (!safeToSave) return;

        // Genuinely quitting DOES fail an active quest — unlike a plain
        // background/pause above, there's no "coming back to a still-
        // running timer" here.
        QuestManager.Instance?.FailActiveQuestOnQuit();
        SaveGame();
        NotificationManager.Instance?.ScheduleOfflineEarningsExpired(GetEffectiveOfflineCapSeconds());
    }

    // Same Night Shift-adjusted cap used by the actual offline earnings
    // calculation elsewhere in this file — kept as one shared helper so
    // the notification timing can never drift out of sync with the real
    // cap duration.
    float GetEffectiveOfflineCapSeconds()
    {
        int nightShiftLevel = EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.NightShift)
            : 0;
        return offlineEarningsCapSeconds + (nightShiftLevel * 5f * 60f);
    }

    // Right-click-able in the Inspector during Play mode — simulates
    // having been away for the given number of minutes, without needing
    // to actually quit and relaunch. Useful for testing the offline
    // earnings popup and payout math directly.
    [ContextMenu("TEST: Simulate 30 Min Offline")]
    public void TestSimulateOfflineEarnings()
    {
        string fakeTimestamp = System.DateTime.UtcNow.AddMinutes(-30).ToString("o");
        ApplyOfflineEarnings(fakeTimestamp);
    }

    // ── Save ──────────────────────────────────────────────────────────

    [ContextMenu("Save Now")]
    public void SaveGame()
    {
        if (GameManager.Instance == null || PlanetManager.Instance == null) return;

        // Capture whatever's currently live into the ACTIVE planet's own
        // PlanetData first, so it's included below even if the player
        // never explicitly switched away from it this session.
        PlanetManager.Instance.SyncCurrentPlanetLiveState();

        // [RabbitDiag] Confirm the sync actually captured live hut state
        // correctly, right before it gets serialized.
        {
            var syncedData = PlanetManager.Instance.GetAllPlanetData();
            if (syncedData.TryGetValue(PlanetManager.Instance.currentPlanet, out PlanetData syncedActive))
            {
                long syncedHutSum = 0;
                if (syncedActive.hutRabbits != null)
                    foreach (long r in syncedActive.hutRabbits) syncedHutSum += r;
                Debug.Log($"[RabbitDiag] SAVE: after sync — planetData.totalRabbits={syncedActive.totalRabbits:N0}, sum(hutRabbits[])={syncedHutSum:N0}, hutRabbits.Length={(syncedActive.hutRabbits != null ? syncedActive.hutRabbits.Length : -1)}, live GameManager.totalRabbits={GameManager.Instance.totalRabbits:N0}");
            }
        }

        SaveData data = new SaveData();
        data.lifetimeRabbits = GameManager.Instance.lifetimeRabbits;
        data.lifetimeRabbitsBred = GameManager.Instance.lifetimeRabbitsBred;
        data.totalCarrots    = GameManager.Instance.totalCarrots;
        data.prestigeCount   = PrestigeManager.Instance != null ? PrestigeManager.Instance.prestigeCount : 0;

        // Extra Farm level — global unlock progression, saved directly
        // rather than inside any one planet's data.
        ShopUpgradeRow[] allRows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in allRows)
        {
            if (row.category == UpgradeManager.UpgradeCategory.ExtraFarm)
            {
                data.extraFarmLevel = row.currentLevel;
                break;
            }
        }

        // Epic upgrades — keyed by GameObject name, global/permanent.
        EpicUpgradeRow[] epicRows = FindObjectsByType<EpicUpgradeRow>(FindObjectsInactive.Include);
        List<string> epicIdList = new List<string>();
        List<int> epicLevelList = new List<int>();
        foreach (EpicUpgradeRow row in epicRows)
        {
            epicIdList.Add(row.gameObject.name);
            epicLevelList.Add(row.currentLevel);
        }
        data.epicUpgradeId    = epicIdList.ToArray();
        data.epicUpgradeLevel = epicLevelList.ToArray();

        // Every planet's own independent farm state.
        data.currentPlanet = PlanetManager.Instance.currentPlanet.ToString();
        var allPlanetData = PlanetManager.Instance.GetAllPlanetData();
        data.planets = new PlanetData[allPlanetData.Count];
        int i = 0;
        foreach (var kvp in allPlanetData)
            data.planets[i++] = kvp.Value;

        data.lastSaveTimestampUtc = System.DateTime.UtcNow.ToString("o");

        if (AchievementsManager.Instance != null)
            data.achievements = AchievementsManager.Instance.GetSaveData().ToArray();
            if (InventoryManager.Instance != null)
                data.inventory = InventoryManager.Instance.GetSaveData().ToArray();
            if (BoostActivationManager.Instance != null)
                data.activeBoosts = BoostActivationManager.Instance.GetSaveData().ToArray();
            if (CosmeticEquipManager.Instance != null)
                data.equippedCosmetics = CosmeticEquipManager.Instance.GetSaveData().ToArray();
                data.cosmeticActivationCounts = CosmeticEquipManager.Instance.GetActivationSaveData().ToArray();
            if (ExplosiveReproductionBoost.Instance != null)
            {
                data.explosiveReproductionPlanet = ExplosiveReproductionBoost.Instance.GetSavePlanetName();
                data.explosiveReproductionExpiryEpoch = ExplosiveReproductionBoost.Instance.GetSaveExpiryEpoch();
            }
            if (FreeAdRewardButton.Instance != null)
                data.freeAdRewardCooldownEndEpoch = FreeAdRewardButton.Instance.GetSaveCooldownEndEpoch();
            if (QuestManager.Instance != null)
                data.hasRespondedToFirstQuest = QuestManager.Instance.HasRespondedToFirstQuest;
            if (GlobeGiftBoxManager.Instance != null)
            {
                data.giftBoxIsSpawned = GlobeGiftBoxManager.Instance.GetSaveIsSpawned();
                data.giftBoxCountry = GlobeGiftBoxManager.Instance.GetSaveCountry();
                data.giftBoxLat = GlobeGiftBoxManager.Instance.GetSaveLat();
                data.giftBoxLon = GlobeGiftBoxManager.Instance.GetSaveLon();
                data.giftBoxNextSpawnEpoch = GlobeGiftBoxManager.Instance.GetSaveNextSpawnEpoch();
            }

        // Defensively strip any NaN/Infinity that may have crept into a
        // float/double field (e.g. from extreme late-game buff compounding)
        // BEFORE serializing — a single such value turns the whole JSON
        // blob unparseable, which is what caused total save loss before
        // this fix. This does not change any player-visible numbers under
        // normal play; it only fires if something already went wrong
        // upstream, converting a would-be corrupting value back to 0
        // instead of letting it poison the entire save file.
        SanitizeForSave(data);

        string json = JsonUtility.ToJson(data);

        // Verify the JSON we're about to write actually parses back
        // correctly before committing it anywhere. If it doesn't (should
        // be effectively impossible after sanitizing above, but this is
        // the actual safety net), abort the save entirely rather than
        // write something we already know is broken.
        try
        {
            SaveData verify = JsonUtility.FromJson<SaveData>(json);
            if (verify == null) throw new System.Exception("Parsed save came back null");
        }
        catch (System.Exception e)
        {
            Debug.LogError("SaveManager: refusing to write a save that fails to round-trip parse: " + e.Message);
            return;
        }

        // Promote whatever was previously the primary save to the backup
        // slot BEFORE overwriting it — so there's always one prior known-
        // good save available even if something about THIS save turns out
        // to be wrong in a way sanitization/round-trip checking couldn't catch.
        if (PlayerPrefs.HasKey(SaveKey))
            PlayerPrefs.SetString(BackupSaveKey, PlayerPrefs.GetString(SaveKey));

        PlayerPrefs.SetString(SaveKey, json);
        PlayerPrefs.Save();

        safeToSave = true;
        Debug.Log($"SaveManager: save written successfully. currentPlanet={data.currentPlanet}, prestigeCount={data.prestigeCount}, lifetimeRabbits={data.lifetimeRabbits}, planetsInSave={data.planets.Length}");
    }

    // Replaces any NaN/Infinity found in the numeric fields that could
    // plausibly carry one (percentage buffs, timestamps) with a safe
    // fallback value, recursively across every planet entry.
    void SanitizeForSave(SaveData data)
    {
        if (data.planets == null) return;
        foreach (PlanetData p in data.planets)
        {
            if (p == null) continue;
            p.questHutCapacityBonusPercent   = SanitizeFloat(p.questHutCapacityBonusPercent);
            p.questTruckCapacityBonusPercent = SanitizeFloat(p.questTruckCapacityBonusPercent);
            p.questReproductionBonusPercent  = SanitizeFloat(p.questReproductionBonusPercent);
            p.questHatchRateBonusPercent     = SanitizeFloat(p.questHatchRateBonusPercent);
            p.truckReadyAtUnixSeconds        = SanitizeDouble(p.truckReadyAtUnixSeconds);
        }
    }

    static float SanitizeFloat(float value) =>
        (float.IsNaN(value) || float.IsInfinity(value)) ? 0f : value;

    static double SanitizeDouble(double value) =>
        (double.IsNaN(value) || double.IsInfinity(value)) ? 0 : value;

    // ── Load ──────────────────────────────────────────────────────────

    static SaveData TryParse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonUtility.FromJson<SaveData>(json);
        }
        catch
        {
            return null;
        }
    }

    // Wipes ONLY the save-data keys (primary, backup, corrupt) — leaves
    // every other PlayerPrefs key untouched (authentication, nickname,
    // tutorial step, etc.). Used when a player abandons the tutorial
    // mid-way and comes back — TutorialManager wipes progress this way
    // rather than a blanket PlayerPrefs.DeleteAll(), which would also
    // force them through authentication/nickname setup again.
    public void WipeSaveDataOnly()
    {
        PlayerPrefs.DeleteKey(SaveKey);
        PlayerPrefs.DeleteKey(BackupSaveKey);
        PlayerPrefs.DeleteKey(CorruptSaveKey);
        PlayerPrefs.Save();
        safeToSave = true;
    }

    public void LoadGame()
    {
        if (!PlayerPrefs.HasKey(SaveKey))
        {
            // Genuinely nothing to load (fresh install) — safe to save from here.
            Debug.Log("SaveManager: no existing save found — starting fresh.");
            safeToSave = true;
            AchievementsManager.Instance?.CheckLoginStreak();
            return;
        }

        string json = PlayerPrefs.GetString(SaveKey);
        SaveData data = TryParse(json);

        if (data == null)
        {
            Debug.LogWarning("SaveManager: primary save failed to parse — trying backup.");
            if (PlayerPrefs.HasKey(BackupSaveKey))
            {
                string backupJson = PlayerPrefs.GetString(BackupSaveKey);
                data = TryParse(backupJson);
                if (data != null)
                    Debug.LogWarning("SaveManager: recovered from backup save successfully.");
            }
        }

        if (data == null)
        {
            // Both primary and backup are unusable. DO NOT proceed as if
            // this were a fresh game — that's exactly the "everything
            // silently resets to 0" failure mode this fix exists to
            // prevent. Preserve the corrupt data for manual inspection,
            // and leave safeToSave FALSE so nothing autosaves over it.
            if (!PlayerPrefs.HasKey(CorruptSaveKey))
                PlayerPrefs.SetString(CorruptSaveKey, json);
            PlayerPrefs.Save();
            safeToSave = false;
            return;
        }

        safeToSave = true;
        Debug.Log($"SaveManager: loaded save successfully. currentPlanet={data.currentPlanet}, prestigeCount={data.prestigeCount}, lifetimeRabbits={data.lifetimeRabbits}, planetsInSave={(data.planets != null ? data.planets.Length : 0)}, lastSaveTimestampUtc={data.lastSaveTimestampUtc}");

        // 0) Boosts — MUST be restored before anything below that computes
        //    a live reproduction rate (Epic upgrades, planet data, shop
        //    levels can all trigger UpgradeManager.ApplyReproduction()),
        //    otherwise Explosive Reproduction's multiplier would be
        //    missing the first time a rate gets computed this session.
        if (BoostActivationManager.Instance != null && data.activeBoosts != null)
            BoostActivationManager.Instance.RestoreSaveData(new List<BoostActivationManager.BoostSaveEntry>(data.activeBoosts));
        ExplosiveReproductionBoost.Instance?.RestoreSaveData(data.explosiveReproductionPlanet, data.explosiveReproductionExpiryEpoch);
        FreeAdRewardButton.Instance?.RestoreSaveData(data.freeAdRewardCooldownEndEpoch);
        QuestManager.Instance?.RestoreHasRespondedToFirstQuest(data.hasRespondedToFirstQuest);
        GlobeGiftBoxManager.Instance?.RestoreSaveData(data.giftBoxIsSpawned, data.giftBoxCountry, data.giftBoxLat, data.giftBoxLon, data.giftBoxNextSpawnEpoch);

        // 1) Global resources.
        if (GameManager.Instance != null)
        {
            GameManager.Instance.lifetimeRabbits = data.lifetimeRabbits;
            GameManager.Instance.lifetimeRabbitsBred = data.lifetimeRabbitsBred;
            GameManager.Instance.totalCarrots    = data.totalCarrots;
            GameManager.Instance.AddCarrots(0); // refresh HUD text
        }

        // 2) Prestige.
        if (PrestigeManager.Instance != null)
        {
            PrestigeManager.Instance.prestigeCount = data.prestigeCount;
            PrestigeManager.Instance.RefreshUIPublic();
        }

        // 3) Epic upgrades, matched by GameObject name. Must happen BEFORE
        //    PlanetManager restores planet data below, since per-planet
        //    stat recomputation (step 5) depends on Epic bonuses already
        //    being in place.
        if (data.epicUpgradeId != null)
        {
            EpicUpgradeRow[] epicRows = FindObjectsByType<EpicUpgradeRow>(FindObjectsInactive.Include);
            foreach (EpicUpgradeRow row in epicRows)
            {
                for (int i = 0; i < data.epicUpgradeId.Length; i++)
                {
                    if (data.epicUpgradeId[i] == row.gameObject.name)
                    {
                        row.currentLevel = data.epicUpgradeLevel[i];
                        row.RefreshUIPublic();
                        if (EpicUpgradeManager.Instance != null)
                            EpicUpgradeManager.Instance.OnEpicUpgradeChanged(row.upgradeType, row.currentLevel);
                        break;
                    }
                }
            }
        }

        // 4) Extra Farm level — global unlock progression. Must happen
        //    BEFORE PlanetManager restores planet data, since it re-derives
        //    which planets are unlocked from this row's level.
        ShopUpgradeRow[] allRows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in allRows)
        {
            if (row.category == UpgradeManager.UpgradeCategory.ExtraFarm)
            {
                row.currentLevel = data.extraFarmLevel;
                row.RefreshUIPublic();
                break;
            }
        }

        // 5) Every planet's own farm data — resources, huts, shop levels,
        //    truck cooldown, quest bonuses. PlanetManager.RestoreAllPlanetData
        //    handles pushing the ACTIVE planet's data into the live
        //    GameManager/UpgradeManager/huts and recomputing every derived
        //    stat (equivalent to the old steps 3–7 in the previous version
        //    of this method, now owned by PlanetManager since it already
        //    has this exact logic for normal planet switching).
        PlanetManager.Planet activePlanet = PlanetManager.Planet.Earth;
        if (!string.IsNullOrEmpty(data.currentPlanet))
            System.Enum.TryParse(data.currentPlanet, out activePlanet);

        Dictionary<PlanetManager.Planet, PlanetData> restoredPlanets = new Dictionary<PlanetManager.Planet, PlanetData>();
        if (data.planets != null)
        {
            foreach (PlanetData pd in data.planets)
            {
                if (pd == null) continue;
                restoredPlanets[pd.planet] = pd;
            }
        }

        if (PlanetManager.Instance != null)
            PlanetManager.Instance.RestoreAllPlanetData(restoredPlanets, activePlanet);

        // 6) Offline earnings — computed AFTER every planet's real
        //    huts/levels/rates are restored above, so it respects the
        //    exact same rules real-time production would. Covers EVERY
        //    unlocked planet, not just whichever one was active when the
        //    game was closed.
        ApplyOfflineEarnings(data.lastSaveTimestampUtc);

        RepairRabbitCountDesyncIfNeeded();

        // 7) Achievement / inventory progress — restored by id, so a
        //    future reordering of the Inspector list doesn't scramble a
        //    returning player's data.
        if (AchievementsManager.Instance != null && data.achievements != null)
            AchievementsManager.Instance.RestoreSaveData(new List<AchievementsManager.AchievementSaveEntry>(data.achievements));
        if (InventoryManager.Instance != null && data.inventory != null)
            InventoryManager.Instance.RestoreSaveData(new List<InventoryManager.InventorySaveEntry>(data.inventory));
        if (CosmeticEquipManager.Instance != null)
            CosmeticEquipManager.Instance.RestoreSaveData(data.equippedCosmetics != null ? new List<CosmeticEquipManager.EquipSaveEntry>(data.equippedCosmetics) : null);
            CosmeticEquipManager.Instance.RestoreActivationSaveData(data.cosmeticActivationCounts != null ? new List<CosmeticEquipManager.ActivationCountEntry>(data.cosmeticActivationCounts) : null);

        // 8) Login streak's date-comparison logic runs LAST, now that its
        //    real previous currentStatValue has actually been restored —
        //    running this any earlier (e.g. in AchievementsManager.Start())
        //    would always operate on a fresh default of 0 instead.
        AchievementsManager.Instance?.CheckLoginStreak();
    }

    void ApplyOfflineEarnings(string lastSaveTimestampUtc)
    {        if (string.IsNullOrEmpty(lastSaveTimestampUtc)) return;
        if (UpgradeManager.Instance == null || UpgradeManager.Instance.huts == null) return;
        // Never show this to a brand-new player who hasn't produced a
        // single rabbit yet — lifetimeRabbits is a permanent, never-
        // decreasing stat, so this only skips genuinely fresh accounts,
        // not players who later sold/lost rabbits.
        if (GameManager.Instance == null || GameManager.Instance.lifetimeRabbits < 1) return;

        System.DateTime lastSave;
        if (!System.DateTime.TryParse(lastSaveTimestampUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out lastSave))
            return;

        double elapsedSecondsRaw = (System.DateTime.UtcNow - lastSave.ToUniversalTime()).TotalSeconds;
        Debug.Log($"[RabbitDiag] ApplyOfflineEarnings: lastSave={lastSave:o}, now={System.DateTime.UtcNow:o}, elapsedSecondsRaw={elapsedSecondsRaw:N1}s ({elapsedSecondsRaw / 60.0:N1} min)");
        if (elapsedSecondsRaw <= 0) return; // clock skew or same-session reload — nothing to award

        // AdNotification's whole cycle resets back to "can appear
        // immediately" whenever the player genuinely comes back after
        // being away — same behavior as a fresh launch, per design.
        AdNotificationManager.Instance?.ResetForOfflineReturn();

        // Epic upgrade: Night Shift — +5 minutes of offline earnings cap
        // per level, permanent (never resets on prestige, same as every
        // other Epic upgrade). Extends the free 30-minute baseline; does
        // not replace or scale it. Same cap applies to every planet.
        float effectiveCapSeconds = GetEffectiveOfflineCapSeconds();
        float elapsedSeconds = (float)System.Math.Min(elapsedSecondsRaw, effectiveCapSeconds);

        // ── Explosive Reproduction split, if applicable ──────────────────
        // Figure out, in real seconds, how much of THIS offline window
        // (lastSave -> now, before capping) the boost was still running
        // for on whichever planet it was locked to. Everything after that
        // point runs at the normal rate instead — this is what prevents
        // an expired-days-ago boost from crediting the entire offline
        // window at 10x.
        PlanetManager.Planet? explosivePlanet = null;
        float explosiveBoostedSeconds = 0f;
        if (ExplosiveReproductionBoost.Instance != null)
        {
            var (planet, expiryEpoch) = ExplosiveReproductionBoost.Instance.GetPlanetAndExpiryForOfflineMath();
            if (planet.HasValue && expiryEpoch > 0)
            {
                long lastSaveEpoch = new System.DateTimeOffset(lastSave.ToUniversalTime()).ToUnixTimeSeconds();
                float secondsUntilExpiry = expiryEpoch - lastSaveEpoch;
                if (secondsUntilExpiry > 0f)
                {
                    explosivePlanet = planet.Value;
                    explosiveBoostedSeconds = Mathf.Clamp(secondsUntilExpiry, 0f, elapsedSeconds);
                }
            }
        }
        float explosiveNormalSeconds = elapsedSeconds - explosiveBoostedSeconds;

        // ── Active planet (the one already pushed into live huts above) ──
        // Uses the precise real-hut-based calculation, since those are
        // actual GameObjects with exact current capacity/rates.
        long activePlanetEarned = 0;
        bool activePlanetIsBoosted = PlanetManager.Instance != null
            && explosivePlanet.HasValue
            && explosivePlanet.Value == PlanetManager.Instance.currentPlanet;

        float activeBaseRate = activePlanetIsBoosted && UpgradeManager.Instance != null
            ? UpgradeManager.Instance.ComputeBaseReproductionRate()
            : 0f;
        float activeBoostedRate = activeBaseRate * ExplosiveReproductionBoost.Multiplier;

        foreach (GameObject hutGO in UpgradeManager.Instance.huts)
        {
            if (hutGO == null || !hutGO.activeInHierarchy) continue;
            RabbitHut hut = hutGO.GetComponent<RabbitHut>();
            if (hut == null) continue;

            long earned;
            if (activePlanetIsBoosted)
            {
                // Chronological order matters — boosted segment first,
                // then normal, so capacity is consumed the same way it
                // would have been in real time.
                long boostedEarned = hut.CalculateOfflineEarnings(explosiveBoostedSeconds, activeBoostedRate);
                if (boostedEarned > 0) hut.ApplyOfflineEarnings(boostedEarned);

                long normalEarned = hut.CalculateOfflineEarnings(explosiveNormalSeconds, activeBaseRate);
                if (normalEarned > 0) hut.ApplyOfflineEarnings(normalEarned);

                earned = boostedEarned + normalEarned;
            }
            else
            {
                earned = hut.CalculateOfflineEarnings(elapsedSeconds);
                if (earned > 0) hut.ApplyOfflineEarnings(earned);
            }

            activePlanetEarned += earned;
        }

        // ── Every OTHER unlocked planet ──────────────────────────────
        // These aren't live GameObjects right now, so they use
        // PlanetManager's capped-by-capacity background-production math
        // (same formula the in-session periodic simulator uses) instead.
        // ApplyOfflineProduction already writes straight into that
        // planet's OWN PlanetData.totalRabbits — it does NOT touch
        // GameManager.totalRabbits (that belongs to the active planet
        // only) or lifetimeRabbits (handled below).
        long otherPlanetsEarned = 0;
        if (PlanetManager.Instance != null)
        {
            PlanetManager.Planet active = PlanetManager.Instance.currentPlanet;
            foreach (PlanetManager.Planet planet in System.Enum.GetValues(typeof(PlanetManager.Planet)))
            {
                if (planet == active) continue;
                if (!PlanetManager.Instance.IsUnlocked(planet)) continue;

                if (explosivePlanet.HasValue && explosivePlanet.Value == planet)
                {
                    otherPlanetsEarned += PlanetManager.Instance.ApplyOfflineProductionForExplosiveBoost(
                        planet, explosiveBoostedSeconds, explosiveNormalSeconds);
                }
                else
                {
                    otherPlanetsEarned += PlanetManager.Instance.ApplyOfflineProduction(planet, elapsedSeconds);
                }
            }
        }

        long totalEarned = activePlanetEarned + otherPlanetsEarned;
        LastOfflineEarnings = totalEarned;

        if (GameManager.Instance != null)
        {
            // Active planet: AddRabbits bumps BOTH totalRabbits (the live
            // active-planet counter, matching what the huts above already
            // received individually) AND the global lifetimeRabbits.
            // Both paths here are PURELY hut reproduction (offline breeding),
            // so lifetimeRabbitsBred gets the same amounts.
            if (activePlanetEarned > 0)
            {
                GameManager.Instance.AddRabbits(activePlanetEarned);
                GameManager.Instance.lifetimeRabbitsBred += activePlanetEarned;
            }

            // Other planets: their own totalRabbits already got bumped
            // directly inside ApplyOfflineProduction — only lifetimeRabbits
            // (global, sums across every planet) still needs it, and
            // NOT totalRabbits (that would wrongly credit them to
            // whichever planet is currently active).
            if (otherPlanetsEarned > 0)
            {
                GameManager.Instance.lifetimeRabbits += otherPlanetsEarned;
                GameManager.Instance.lifetimeRabbitsBred += otherPlanetsEarned;
            }
        }

        OnOfflineEarningsCalculated?.Invoke(totalEarned);
    }

    // Diagnostic-only right now — logs a warning but does NOT overwrite
    // totalRabbits. An earlier version of this auto-corrected the value,
    // but that turned out to be actively destructive: it could wipe out
    // genuinely legitimate progress if hutSum was read before huts had
    // fully finished restoring from the save (or if some other bug is
    // still live and not yet understood). Never auto-repair player
    // progress based on an unproven theory — only report it.
    void RepairRabbitCountDesyncIfNeeded()
    {
        if (GameManager.Instance == null || UpgradeManager.Instance == null || UpgradeManager.Instance.huts == null) return;

        long hutSum = 0;
        foreach (GameObject hutGO in UpgradeManager.Instance.huts)
        {
            if (hutGO == null || !hutGO.activeInHierarchy) continue;
            RabbitHut hut = hutGO.GetComponent<RabbitHut>();
            if (hut != null) hutSum += hut.GetCurrentRabbits();
        }

        long total = GameManager.Instance.totalRabbits;
        long gap = total - hutSum;

        if (gap > 1000)
        {
            Debug.LogWarning($"SaveManager: DESYNC DETECTED (not auto-corrected) — totalRabbits={total:N0}, hutSum={hutSum:N0}, gap={gap:N0}. Investigating cause — no data was changed.");
        }
    }

    // Call this from a "New Game" / wipe-save debug button if you ever need one.
    // Also right-click-able directly on the SaveManager component in the
    // Inspector (Editor and Play mode) via the gear icon / context menu.
    [ContextMenu("Delete Save")]
    public void DeleteSave()
    {
        PlayerPrefs.DeleteKey(SaveKey);
    }
}
