using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using Unity.Services.Authentication;
using Unity.Services.Leaderboards;
using Unity.Services.Leaderboards.Models;

// Panel controller for the leaderboard. Wire LeaderBoardButton's OnClick to
// OpenLeaderboard(), and CancelButton_Leaderboard's OnClick to
// CloseLeaderboard().
//
// LEADERBOARD starts INACTIVE in the Hierarchy (as normal). Opening
// activates it and slides it in; closing slides it out and only then
// deactivates it — same deactivateAfter pattern as QuestSlidePanel.
//
// ── VIRTUALIZED SCROLLING ──────────────────────────────────────────
// Content is NOT managed by Vertical Layout Group / Content Size Fitter
// anymore — remove both from Content if they're still there, they will
// fight this script's manual positioning. Instead:
//   - Content's height is set manually to (entryCount * rowStride).
//   - Only enough row instances to cover the visible viewport (+ a small
//     buffer) are ever instantiated; as the list scrolls, those same
//     instances get repositioned and repopulated with different data
//     rather than new GameObjects being created per entry.
// This is what keeps a 999-row leaderboard cheap regardless of list size.
//
// Required Editor setup:
//   - Content's RectTransform: Anchor Preset = top-stretch
//     (anchorMin = (0,1), anchorMax = (1,1), pivot = (0.5,1)).
//   - Assign Scroll Rect (the ScrollView's ScrollRect), Viewport (the
//     Viewport child's RectTransform), and Content (same object as
//     before, now typed as RectTransform).
//   - Set Row Height to match your row prefab's actual pixel height, and
//     Row Spacing to whatever gap you want between rows.
public class LeaderboardUI : MonoBehaviour
{
    // Lightweight internal row data — real LeaderboardEntry results get
    // mapped into this right after fetching, and fake test data is built
    // directly as this type.
    public struct RowData
    {
        public string playerId;
        public string playerName;
        public int bigBangCount;
        public long lifetimeRabbits;
        public string country;
        public float latitude;
        public float longitude;
        public string equippedNickname;
        public int nicknameColorIndex;
        public string equippedBackground;
        public string guildId;
        public long contestProgress; // this player's individual progress toward the current guild contest objective, if any
    }

    // Used by the "growing bots" test — score/bigBang are computed purely
    // as a function of elapsed time since the test started, rather than
    // incrementally mutated each tick, so there's no accumulation drift.
    private class SimBot
    {
        public string playerId;
        public string playerName;
        public double initialScore;
        public double growthPerSecond;
        public float startTime;
        public int initialBigBang;
        public float bigBangInterval;
    }

    [Header("Panel")]
    public GameObject panel;
    public RectTransform panelRect;
    [Tooltip("Resets to the Global tab every time the leaderboard opens, so Content_Global and Content_Overview never end up active simultaneously.")]
    public LeaderboardTabController tabController;

    [Header("Slide Settings")]
    public float slideDuration = 0.4f;
    public float openX = 0f;
    public float closedX = 1400f;

    [Header("Virtualized Scroll")]
    public ScrollRect scrollRect;
    public RectTransform viewport;
    public RectTransform content;
    public LeaderboardRowUI rowPrefab;
    [Tooltip("Used instead of rowPrefab for whichever entry belongs to the local player, if they're inside the top-999.")]
    public LeaderboardRowUI personalizedRowPrefab;
    [Tooltip("Must match your row prefab's actual height in pixels.")]
    public float rowHeight = 90f;
    public float rowSpacing = 10f;
    [Tooltip("Extra rows instantiated above/below the visible area so scrolling doesn't flash blank rows for a frame.")]
    public int bufferRows = 2;

    [Header("Personal Row (pinned, always visible, shows TRUE global rank)")]
    [Tooltip("A separate LeaderboardRowUI instance placed OUTSIDE the ScrollRect, fixed at the bottom of the panel — not part of the virtualized list.")]
    public LeaderboardRowUI personalRow;

    [Header("Button Fades (hidden while leaderboard is open)")]
    [Tooltip("The button that opens the leaderboard itself.")]
    public CanvasGroup leaderboardButtonCanvasGroup;
    public CanvasGroup achievementButtonCanvasGroup;
    public CanvasGroup inventoryButtonCanvasGroup;
    public CanvasGroup bigBangButtonCanvasGroup;
    public CanvasGroup settingsButtonCanvasGroup;
    public float buttonFadeDuration = 0.25f;
    private Coroutine leaderboardButtonFadeCo;
    private Coroutine achievementButtonFadeCo;
    private Coroutine inventoryButtonFadeCo;
    private Coroutine bigBangButtonFadeCo;
    private Coroutine settingsButtonFadeCo;

    [Header("Refresh")]
    public float refreshInterval = 30f;
    // True once the FIRST fetch (RefreshOnce) has completed, successfully
    // or not. Other scripts (e.g. GuildsListUI) that need real cached
    // entries via GetCurrentEntries() should wait on this before their
    // own first computation — otherwise they'd compute against an empty
    // cache if they happen to run before this list's own first load.
    public bool HasLoadedOnce { get; private set; } = false;

    [Header("Loading / Empty State (optional)")]
    public GameObject loadingIndicator;
    public GameObject emptyStateText;

    private List<RowData> currentEntries = new List<RowData>();

    // Lets other systems (the globe) mirror whatever the Global tab
    // currently has loaded — real fetched data during normal play, or
    // whatever a local fake-test tool populated during testing. Single
    // source of truth, no separate fetch/simulation needed elsewhere.
    public List<RowData> GetCurrentEntries() => currentEntries;
    private int ownIndex = -1; // index of local player within currentEntries, -1 if not present
    private List<SimBot> simBots;

    private readonly List<LeaderboardRowUI> pool = new List<LeaderboardRowUI>();
    private LeaderboardRowUI personalizedInlineInstance;

    private Coroutine refreshCoroutine;
    private Coroutine slideCoroutine;
    private bool positionInitialized = false;
    public static bool leaderboardOpen = false;

    float RowStride => rowHeight + rowSpacing;

    void Awake()
    {
        if (scrollRect != null)
            scrollRect.onValueChanged.AddListener(_ => RefreshVisibleRows());
    }

    // ── Open / Close ─────────────────────────────────────────────────

    // Hook this to LeaderBoardButton's OnClick.
    public void OpenLeaderboard()
    {
        if (!NetworkStatusManager.RequireWifiOrShowMessage())
            return;

        if (ShopManager.shopOpen)
            FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (SellManager.popupOpen)
            FindAnyObjectByType<SellManager>()?.CloseSellPopup();
        if (HutUpgradeManager.popupOpen)
            FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();
        if (PrestigeManager.popupOpen)
            FindAnyObjectByType<PrestigeManager>()?.CloseBigBangPopup();
        if (SettingsPanelController.settingsOpen)
            SettingsPanelController.Instance?.CloseSettings();
        if (AchievementsPanelUI.achievementsOpen)
            FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (InventoryPanel.inventoryOpen)
            FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn();

        // Always land on the Global tab, regardless of whatever state
        // Content_Global/Content_Overview were left in previously — fixes
        // both ever showing active simultaneously.
        if (tabController != null) tabController.ShowGlobalTab();

        if (panelRect == null && panel != null) panelRect = panel.GetComponent<RectTransform>();

        // First-ever open: snap to the closed position with no animation
        // before activating, since we don't know what position the object
        // was left at in the Editor.
        bool isFirstEverOpen = !positionInitialized;
        if (isFirstEverOpen && panelRect != null)
        {
            panelRect.anchoredPosition = new Vector2(closedX, panelRect.anchoredPosition.y);
            positionInitialized = true;
        }

        if (panel != null) panel.SetActive(true);

        // Hide the pinned personal row ONLY on the very first open after
        // launch, when it's still showing whatever placeholder values were
        // baked into the prefab in the Editor. On every subsequent open it
        // already holds real data from the last refresh — hiding it every
        // time (as a previous version of this did) made it flash blank on
        // every reopen instead of just the first.
        if (isFirstEverOpen && personalRow != null) personalRow.gameObject.SetActive(false);

        leaderboardOpen = true;
        SlideTo(openX, deactivateAfter: false);

        FadeLeaderboardButton(0f);
        FadeAchievementButton(0f);
        FadeInventoryButton(0f);
        FadeBigBangButton(0f);
        FadeSettingsButton(0f);

        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
        refreshCoroutine = StartCoroutine(RefreshLoop());
    }

    // Hook this to CancelButton_Leaderboard's OnClick.
    public void CloseLeaderboard()
    {
        GuildInfoPanelUI.CloseAllInstant();
        GuildCreationPanelUI.Instance?.CloseInstant();

        leaderboardOpen = false;
        SlideTo(closedX, deactivateAfter: true);

        FadeLeaderboardButton(1f);
        FadeAchievementButton(1f);
        FadeInventoryButton(1f);
        FadeBigBangButton(1f);
        FadeSettingsButton(1f);

        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
        refreshCoroutine = null;
    }

    void FadeLeaderboardButton(float targetAlpha)
    {
        if (leaderboardButtonCanvasGroup == null) return;
        if (leaderboardButtonFadeCo != null) StopCoroutine(leaderboardButtonFadeCo);
        leaderboardButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(leaderboardButtonCanvasGroup, targetAlpha));
    }

    void FadeAchievementButton(float targetAlpha)
    {
        if (achievementButtonCanvasGroup == null) return;
        if (achievementButtonFadeCo != null) StopCoroutine(achievementButtonFadeCo);
        achievementButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(achievementButtonCanvasGroup, targetAlpha));
    }

    void FadeInventoryButton(float targetAlpha)
    {
        if (inventoryButtonCanvasGroup == null) return;
        if (inventoryButtonFadeCo != null) StopCoroutine(inventoryButtonFadeCo);
        inventoryButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(inventoryButtonCanvasGroup, targetAlpha));
    }

    void FadeBigBangButton(float targetAlpha)
    {
        if (bigBangButtonCanvasGroup == null) return;
        if (bigBangButtonFadeCo != null) StopCoroutine(bigBangButtonFadeCo);
        bigBangButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(bigBangButtonCanvasGroup, targetAlpha));
    }

    void FadeSettingsButton(float targetAlpha)
    {
        if (settingsButtonCanvasGroup == null) return;
        if (settingsButtonFadeCo != null) StopCoroutine(settingsButtonFadeCo);
        settingsButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(settingsButtonCanvasGroup, targetAlpha));
    }

    // Shared by all five buttons above — stop blocking clicks/raycasts
    // immediately when hiding, only start allowing them once fully
    // visible when showing. Same shape used across the other panels.
    IEnumerator FadeCanvasGroupRoutine(CanvasGroup group, float targetAlpha)
    {
        bool showing = targetAlpha > 0.5f;
        if (!showing)
        {
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        float startAlpha = group.alpha;
        float elapsed = 0f;
        while (elapsed < buttonFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / buttonFadeDuration);
            group.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }
        group.alpha = targetAlpha;

        if (showing)
        {
            group.interactable = true;
            group.blocksRaycasts = true;
        }
    }

    void SlideTo(float targetX, bool deactivateAfter)
    {
        if (panelRect == null) return;
        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideRoutine(targetX, deactivateAfter));
    }

    IEnumerator SlideRoutine(float targetX, bool deactivateAfter)
    {
        Vector2 start = panelRect.anchoredPosition;
        Vector2 target = new Vector2(targetX, start.y);
        float elapsed = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / slideDuration);
            t = t * t * (3f - 2f * t); // smoothstep, matches Shop/Sell/Hut popups
            panelRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }

        panelRect.anchoredPosition = target;

        if (deactivateAfter && panel != null)
            panel.SetActive(false);
    }

    // ── Refresh loop ─────────────────────────────────────────────────

    IEnumerator RefreshLoop()
    {
        while (true)
        {
            yield return RefreshOnce();
            yield return new WaitForSeconds(refreshInterval);
        }
    }

    IEnumerator RefreshOnce()
    {
        if (loadingIndicator != null) loadingIndicator.SetActive(true);

        var task = LeaderboardManager.Instance != null
            ? LeaderboardManager.Instance.FetchTopScoresAsync()
            : null;

        if (task == null)
        {
            if (loadingIndicator != null) loadingIndicator.SetActive(false);
            if (emptyStateText != null) emptyStateText.SetActive(true);
            HasLoadedOnce = true;
            yield break;
        }

        while (!task.IsCompleted) yield return null;

        if (loadingIndicator != null) loadingIndicator.SetActive(false);

        List<LeaderboardEntry> rawEntries = task.Result;
        (int rank, RowData row)? ownFromList = null;

        if (rawEntries == null || rawEntries.Count == 0)
        {
            if (emptyStateText != null) emptyStateText.SetActive(true);
            currentEntries.Clear();
            ownIndex = -1;
            UpdateContentSize();
            RefreshVisibleRows();
        }
        else
        {
            if (emptyStateText != null) emptyStateText.SetActive(false);

            string localPlayerId = LeaderboardManager.Instance != null && LeaderboardManager.Instance.IsReady
                ? AuthenticationService.Instance.PlayerId
                : null;

            currentEntries.Clear();
            ownIndex = -1;
            for (int i = 0; i < rawEntries.Count; i++)
            {
                RowData rd = ToRowData(rawEntries[i]);
                currentEntries.Add(rd);
                if (localPlayerId != null && rd.playerId == localPlayerId)
                {
                    ownIndex = i;
                    ownFromList = (i + 1, rd);
                }
            }

            UpdateContentSize();
            RefreshVisibleRows();
        }

        // Fetch + populate the pinned personal row. If the player was
        // already found inside the top-999 list, reuse that EXACT same
        // data instead of making a separate GetPlayerScoreAsync call.
        yield return RefreshPersonalRow(ownFromList);

        HasLoadedOnce = true;
    }

    public RowData ToRowData(LeaderboardEntry entry)
    {
        LeaderboardScoreMetadata meta = ParseMetadata(entry.Metadata);
        return new RowData
        {
            playerId = entry.PlayerId,
            playerName = entry.PlayerName,
            bigBangCount = meta.bigBangCount,
            lifetimeRabbits = meta.lifetimeRabbits,
            country = meta.country,
            latitude = meta.latitude,
            longitude = meta.longitude,
            equippedNickname = meta.equippedNickname,
            nicknameColorIndex = meta.nicknameColorIndex,
            equippedBackground = meta.equippedBackground,
            guildId = meta.guildId
        };
    }

    void UpdateContentSize()
    {
        if (content == null) return;
        float totalHeight = currentEntries.Count * RowStride;
        content.sizeDelta = new Vector2(content.sizeDelta.x, totalHeight);
    }

    // ── Virtualization core ──────────────────────────────────────────
    // Only instantiates/repositions enough rows to cover the visible
    // viewport (+ buffer), reusing the same pooled instances as you
    // scroll rather than spawning one GameObject per data entry.
    void RefreshVisibleRows()
    {
        if (content == null || viewport == null || currentEntries.Count == 0)
        {
            foreach (LeaderboardRowUI r in pool)
                r.gameObject.SetActive(false);
            if (personalizedInlineInstance != null)
                personalizedInlineInstance.gameObject.SetActive(false);
            return;
        }

        float scrollOffset = Mathf.Max(0f, content.anchoredPosition.y);
        float viewportHeight = viewport.rect.height;

        int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scrollOffset / RowStride) - bufferRows);
        int visibleCount = Mathf.CeilToInt(viewportHeight / RowStride) + bufferRows * 2 + 1;
        int lastVisible = Mathf.Min(currentEntries.Count - 1, firstVisible + visibleCount);

        // Grow the pool if needed for this visible window; never shrink,
        // just deactivate what's unused below.
        int neededPoolSize = Mathf.Max(0, lastVisible - firstVisible + 1);
        while (pool.Count < neededPoolSize)
        {
            LeaderboardRowUI r = Instantiate(rowPrefab, content);
            SetupRowRectTransform(r.GetComponent<RectTransform>());
            pool.Add(r);
        }

        int poolIndex = 0;
        bool personalizedPlaced = false;

        for (int dataIndex = firstVisible; dataIndex <= lastVisible; dataIndex++)
        {
            RowData data = currentEntries[dataIndex];
            bool isOwn = dataIndex == ownIndex;

            if (isOwn && personalizedRowPrefab != null)
            {
                if (personalizedInlineInstance == null)
                {
                    personalizedInlineInstance = Instantiate(personalizedRowPrefab, content);
                    SetupRowRectTransform(personalizedInlineInstance.GetComponent<RectTransform>());
                }

                PositionRow(personalizedInlineInstance.GetComponent<RectTransform>(), dataIndex);
                personalizedInlineInstance.gameObject.SetActive(true);
                personalizedInlineInstance.Set(
                    dataIndex + 1, CountryFlagDatabase.GetFlag(data.country), data.playerName, data.bigBangCount, data.lifetimeRabbits, true, data.equippedNickname, data.equippedBackground);
                personalizedPlaced = true;
                continue; // this data index is covered by the personalized instance, not a pool slot
            }

            if (poolIndex >= pool.Count) continue; // safety net, shouldn't trigger given sizing above
            LeaderboardRowUI row = pool[poolIndex];
            poolIndex++;

            PositionRow(row.GetComponent<RectTransform>(), dataIndex);
            row.gameObject.SetActive(true);
            row.Set(dataIndex + 1, CountryFlagDatabase.GetFlag(data.country), data.playerName, data.bigBangCount, data.lifetimeRabbits, false, data.equippedNickname, data.equippedBackground, data.nicknameColorIndex);
        }

        // Hide unused pool slots (e.g. window shrank, or own-row consumed one slot).
        for (int i = poolIndex; i < pool.Count; i++)
            pool[i].gameObject.SetActive(false);

        if (!personalizedPlaced && personalizedInlineInstance != null)
            personalizedInlineInstance.gameObject.SetActive(false);
    }

    void SetupRowRectTransform(RectTransform rt)
    {
        if (rt == null) return;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, rowHeight);
    }

    void PositionRow(RectTransform rt, int dataIndex)
    {
        if (rt == null) return;
        rt.anchoredPosition = new Vector2(0f, -dataIndex * RowStride);
    }

    // ── Personal pinned row ───────────────────────────────────────────

    IEnumerator RefreshPersonalRow((int rank, RowData row)? ownFromList)
    {
        if (personalRow == null) yield break;
        if (LeaderboardTabController.overviewActive || LeaderboardTabController.guildsActive) yield break;

        RowData own;
        int displayRank;

        if (ownFromList.HasValue)
        {
            // Reuse the exact data already fetched for the top-999 list —
            // guarantees the pinned row and the inline row can never disagree.
            displayRank = ownFromList.Value.rank;
            own = ownFromList.Value.row;
        }
        else
        {
            // Not in the top-999 (or list fetch failed) — fall back to a
            // dedicated call to get the player's true global rank.
            var task = LeaderboardManager.Instance != null
                ? LeaderboardManager.Instance.FetchOwnScoreAsync()
                : null;

            if (task == null)
            {
                personalRow.gameObject.SetActive(false);
                yield break;
            }

            while (!task.IsCompleted) yield return null;

            LeaderboardEntry fetched = task.Result;
            if (fetched == null)
            {
                personalRow.gameObject.SetActive(false);
                yield break;
            }

            own = ToRowData(fetched);
            // GetPlayerScoreAsync's Rank field is 0-indexed (confirmed via
            // the "not in top 999" fake test, which is the only way to
            // isolate this specific fallback path from the reuse-from-list
            // path — those two don't necessarily share the same indexing
            // convention despite being different SDK endpoints).
            displayRank = fetched.Rank + 1;
        }

        // Re-check here (not just at the top) — the tab can change WHILE
        // the async fetch above was in flight, and this is what was
        // letting the personal row pop back up on the Guilds tab a few
        // seconds after switching away.
        if (LeaderboardTabController.overviewActive || LeaderboardTabController.guildsActive)
        {
            personalRow.gameObject.SetActive(false);
            yield break;
        }

        personalRow.gameObject.SetActive(true);
        personalRow.Set(displayRank, CountryFlagDatabase.GetFlag(own.country), own.playerName, own.bigBangCount, own.lifetimeRabbits, true, own.equippedNickname, own.equippedBackground, own.nicknameColorIndex);
        AchievementsManager.Instance?.ReportGlobalRank(displayRank);

        // Guild "ClimbRanks" contest objective — reports how many rank
        // POSITIONS improved since the last refresh (lower displayRank =
        // better). First reading after opening/(re)loading just sets the
        // baseline, since there's nothing to compare it against yet.
        if (lastKnownGlobalRank != int.MinValue)
        {
            int improvement = lastKnownGlobalRank - displayRank;
            if (improvement > 0)
                _ = GuildManager.Instance?.ReportContestProgressAsync("ClimbRanks", improvement);
        }
        lastKnownGlobalRank = displayRank;
    }

    private int lastKnownGlobalRank = int.MinValue;

    LeaderboardScoreMetadata ParseMetadata(string json)
    {
        if (string.IsNullOrEmpty(json)) return new LeaderboardScoreMetadata();
        try
        {
            LeaderboardScoreMetadata parsed = JsonUtility.FromJson<LeaderboardScoreMetadata>(json);
            return parsed ?? new LeaderboardScoreMetadata();
        }
        catch
        {
            return new LeaderboardScoreMetadata();
        }
    }

    // ── Local-only testing tool — never touches the real leaderboard ───

    [ContextMenu("TEST: Populate 2000 Fake Rows (I'm NOT in the top 999)")]
    public void TestPopulateFakeBots() => RunFakeTest(includeSelf: false);

    [ContextMenu("TEST: Populate 2000 Fake Rows (I'm ranked #500)")]
    public void TestPopulateFakeBotsWithSelfIncluded() => RunFakeTest(includeSelf: true);

    [ContextMenu("TEST: 5 Fixed Bots (1M/5M/20M/30M/100M) + Me, flat 5000/sec")]
    public void TestFiveFixedBotsWithSelf()
    {
        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);

        long[] startScores = { 1000000, 5000000, 20000000, 30000000, 100000000 };
        fakeGrowthEntries = new List<FakeGrowthEntry>(startScores.Length);

        for (int i = 0; i < startScores.Length; i++)
        {
            fakeGrowthEntries.Add(new FakeGrowthEntry
            {
                playerId = "FIXEDBOT_" + i,
                playerName = "FixedBot" + (i + 1),
                initialScore = startScores[i],
                growthPerSecond = 5000, // flat rate, as requested — same for all 5 regardless of starting size
                startTime = Time.time,
                bigBangCount = 0
            });
        }

        bool selfIncluded = LeaderboardManager.Instance != null && LeaderboardManager.Instance.IsReady;
        RebuildFromGrowthEntries(selfIncluded);
        refreshCoroutine = StartCoroutine(FakeTestRefreshLoop(selfIncluded));
    }

    // 1500 fake entries with real-city locations, capped to the top 999 —
    // exactly mirroring how real data behaves. Populates currentEntries
    // directly (the shared source of truth), so the globe automatically
    // mirrors this the next time it refreshes, with zero changes needed
    // on the Globe side — it already just reads whatever's here.
    [ContextMenu("TEST: 1500 Fake Entries From Real Cities (Global + Globe)")]
    public void TestPopulate1500FromRealCities()
    {
        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);

        var cities = new (string name, string country, float lat, float lon)[]
        {
            ("New York", "US", 40.7128f, -74.0060f),
            ("Los Angeles", "US", 34.0522f, -118.2437f),
            ("Chicago", "US", 41.8781f, -87.6298f),
            ("Toronto", "CA", 43.6532f, -79.3832f),
            ("Mexico City", "MX", 19.4326f, -99.1332f),
            ("Sao Paulo", "BR", -23.5505f, -46.6333f),
            ("Buenos Aires", "AR", -34.6037f, -58.3816f),
            ("London", "GB", 51.5074f, -0.1278f),
            ("Paris", "FR", 48.8566f, 2.3522f),
            ("Berlin", "DE", 52.5200f, 13.4050f),
            ("Madrid", "ES", 40.4168f, -3.7038f),
            ("Rome", "IT", 41.9028f, 12.4964f),
            ("Amsterdam", "NL", 52.3676f, 4.9041f),
            ("Moscow", "RU", 55.7558f, 37.6173f),
            ("Istanbul", "TR", 41.0082f, 28.9784f),
            ("Cairo", "EG", 30.0444f, 31.2357f),
            ("Lagos", "NG", 6.5244f, 3.3792f),
            ("Johannesburg", "ZA", -26.2041f, 28.0473f),
            ("Dubai", "AE", 25.2048f, 55.2708f),
            ("Mumbai", "IN", 19.0760f, 72.8777f),
            ("New Delhi", "IN", 28.6139f, 77.2090f),
            ("Bangkok", "TH", 13.7563f, 100.5018f),
            ("Singapore", "SG", 1.3521f, 103.8198f),
            ("Jakarta", "ID", -6.2088f, 106.8456f),
            ("Tokyo", "JP", 35.6762f, 139.6503f),
            ("Seoul", "KR", 37.5665f, 126.9780f),
            ("Shanghai", "CN", 31.2304f, 121.4737f),
            ("Sydney", "AU", -33.8688f, 151.2093f)
        };

        System.Random rng = new System.Random();
        List<RowData> fake = new List<RowData>(1500);

        for (int i = 0; i < 1500; i++)
        {
            var city = cities[i % cities.Length];
            long score = (long)(rng.NextDouble() * 999999999999999.0);
            fake.Add(new RowData
            {
                playerId = "CITYBOT1500_" + i,
                playerName = "Bot" + i,
                bigBangCount = rng.Next(0, 1000),
                lifetimeRabbits = score,
                country = city.country,
                latitude = city.lat,
                longitude = city.lon
            });
        }

        fake.Sort((a, b) => b.lifetimeRabbits.CompareTo(a.lifetimeRabbits));
        currentEntries = fake.GetRange(0, Mathf.Min(999, fake.Count));
        ownIndex = -1;

        if (emptyStateText != null) emptyStateText.SetActive(false);
        UpdateContentSize();
        RefreshVisibleRows();
    }

    [ContextMenu("TEST: Stop Fake Test, Resume Live Data")]
    public void TestStopAndResumeLive()
    {
        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);
        refreshCoroutine = StartCoroutine(RefreshLoop());
    }

    [ContextMenu("TEST: 1500 Growing Bots (ranks shift over time)")]
    public void TestPopulateGrowingBots()
    {
        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);

        System.Random rng = new System.Random();
        simBots = new List<SimBot>(1500);

        // Log-uniform spread across the full 10,000–250,000,000 range, so
        // starting values actually cover every order of magnitude instead
        // of clustering near the top (which plain linear random would do
        // across a range this wide).
        double logMin = System.Math.Log10(10000.0);
        double logMax = System.Math.Log10(250000000.0);

        for (int i = 0; i < 1500; i++)
        {
            double logScore = logMin + rng.NextDouble() * (logMax - logMin);
            double startScore = System.Math.Pow(10.0, logScore);

            simBots.Add(new SimBot
            {
                playerId = "SIMBOT_" + i,
                playerName = "Bot" + i,
                initialScore = startScore,
                growthPerSecond = rng.NextDouble() * 5000.0, // 0–5000 rabbits/sec, random per bot
                startTime = Time.time,
                initialBigBang = rng.Next(0, 50),
                bigBangInterval = 20f + (float)rng.NextDouble() * 70f // staggered, 20–90s per bot
            });
        }

        if (emptyStateText != null) emptyStateText.SetActive(false);
        refreshCoroutine = StartCoroutine(GrowingBotsLoop());
    }

    IEnumerator GrowingBotsLoop()
    {
        while (true)
        {
            (int rank, RowData row)? ownFromList = RecomputeGrowingBots();
            yield return RefreshPersonalRow(ownFromList);
            yield return new WaitForSeconds(refreshInterval);
        }
    }

    (int rank, RowData row)? RecomputeGrowingBots()
    {
        if (simBots == null) return null;

        List<RowData> updated = new List<RowData>(simBots.Count + 1);
        foreach (SimBot bot in simBots)
        {
            float elapsed = Time.time - bot.startTime;
            long score = (long)(bot.initialScore + bot.growthPerSecond * elapsed);
            int bigBang = bot.initialBigBang + Mathf.FloorToInt(elapsed / bot.bigBangInterval);

            updated.Add(new RowData
            {
                playerId = bot.playerId,
                playerName = bot.playerName,
                bigBangCount = bigBang,
                lifetimeRabbits = score
            });
        }

        // Insert the real local player using their actual live stats, so
        // their position emerges from genuinely competing against the
        // simulated bots instead of falling back to disconnected real
        // server data (which only has your real handful of competitors,
        // not these bots).
        string localPlayerId = null;
        if (LeaderboardManager.Instance != null && LeaderboardManager.Instance.IsReady)
        {
            localPlayerId = AuthenticationService.Instance.PlayerId;
            updated.Add(new RowData
            {
                playerId = localPlayerId,
                playerName = AuthenticationService.Instance.PlayerName,
                bigBangCount = PrestigeManager.Instance != null ? PrestigeManager.Instance.prestigeCount : 0,
                lifetimeRabbits = GameManager.Instance != null ? GameManager.Instance.lifetimeRabbits : 0
            });
        }

        // Re-sort every cycle — this is what actually produces the
        // visible rank/position shuffling, since bots grow at different
        // random speeds.
        updated.Sort((a, b) => b.lifetimeRabbits.CompareTo(a.lifetimeRabbits));

        // Determine true rank BEFORE capping, so the personal row stays
        // accurate even if you're currently ranked below the rendered
        // top-999 window.
        int trueOwnIndex = -1;
        (int rank, RowData row)? ownFromList = null;
        for (int i = 0; i < updated.Count; i++)
        {
            if (localPlayerId != null && updated[i].playerId == localPlayerId)
            {
                trueOwnIndex = i;
                ownFromList = (i + 1, updated[i]);
                break;
            }
        }

        List<RowData> capped = updated.Count > 999 ? updated.GetRange(0, 999) : updated;
        currentEntries = capped;
        ownIndex = (trueOwnIndex >= 0 && trueOwnIndex < capped.Count) ? trueOwnIndex : -1;

        UpdateContentSize();
        RefreshVisibleRows();

        return ownFromList;
    }

    private class FakeGrowthEntry
    {
        public string playerId;
        public string playerName;
        public double initialScore;
        public double growthPerSecond;
        public float startTime;
        public int bigBangCount;
    }
    private List<FakeGrowthEntry> fakeGrowthEntries;

    void RunFakeTest(bool includeSelf)
    {
        if (refreshCoroutine != null) StopCoroutine(refreshCoroutine);

        System.Random rng = new System.Random();
        fakeGrowthEntries = new List<FakeGrowthEntry>(2000);

        for (int i = 0; i < 2000; i++)
        {
            long score = (long)(rng.NextDouble() * 999999999999999.0);
            fakeGrowthEntries.Add(new FakeGrowthEntry
            {
                playerId = "FAKE_" + i,
                playerName = "Bot" + i,
                initialScore = score,
                // % of the bot's OWN score per second, so small and large
                // starting scores grow at proportionally similar rates
                // instead of a flat amount exploding small bots.
                growthPerSecond = score * (rng.NextDouble() * 0.01),
                startTime = Time.time,
                bigBangCount = rng.Next(0, 1000)
            });
        }

        bool selfIncluded = includeSelf && LeaderboardManager.Instance != null && LeaderboardManager.Instance.IsReady;

        RebuildFromGrowthEntries(selfIncluded);

        refreshCoroutine = StartCoroutine(FakeTestRefreshLoop(selfIncluded));
    }

    // Recomputes every fake bot's current score from elapsed time, rebuilds
    // and RE-SORTS currentEntries fresh each call — this is what produces
    // real leaderboard-style churn (ranks actually shifting over time as
    // some bots overtake others), not just numbers ticking up in place.
    (int rank, RowData row)? RebuildFromGrowthEntries(bool includeSelf)
    {
        List<RowData> fake = new List<RowData>(fakeGrowthEntries.Count + 1);
        float now = Time.time;

        foreach (FakeGrowthEntry entry in fakeGrowthEntries)
        {
            float elapsed = now - entry.startTime;
            long currentScore = (long)(entry.initialScore + entry.growthPerSecond * elapsed);
            fake.Add(new RowData
            {
                playerId = entry.playerId,
                playerName = entry.playerName,
                bigBangCount = entry.bigBangCount,
                lifetimeRabbits = currentScore
            });
        }

        string localPlayerId = null;
        if (includeSelf)
        {
            localPlayerId = AuthenticationService.Instance.PlayerId;
            fake.Add(new RowData
            {
                playerId = localPlayerId,
                playerName = AuthenticationService.Instance.PlayerName,
                bigBangCount = PrestigeManager.Instance != null ? PrestigeManager.Instance.prestigeCount : 0,
                lifetimeRabbits = GameManager.Instance != null ? GameManager.Instance.lifetimeRabbits : 0
            });
        }

        fake.Sort((a, b) => b.lifetimeRabbits.CompareTo(a.lifetimeRabbits));

        currentEntries = fake.GetRange(0, Mathf.Min(999, fake.Count));

        ownIndex = -1;
        (int rank, RowData row)? ownFromList = null;
        if (localPlayerId != null)
        {
            for (int i = 0; i < currentEntries.Count; i++)
            {
                if (currentEntries[i].playerId == localPlayerId)
                {
                    ownIndex = i;
                    ownFromList = (i + 1, currentEntries[i]);
                    break;
                }
            }
        }

        if (emptyStateText != null) emptyStateText.SetActive(false);
        UpdateContentSize();
        RefreshVisibleRows();

        return ownFromList;
    }

    // Recomputes and re-sorts every fake bot's live score each cycle, so
    // ranks genuinely shift over time — real leaderboard-style churn.
    IEnumerator FakeTestRefreshLoop(bool includeSelf)
    {
        while (true)
        {
            (int rank, RowData row)? ownFromList = RebuildFromGrowthEntries(includeSelf);
            yield return RefreshPersonalRow(ownFromList);
            yield return new WaitForSeconds(refreshInterval);
        }
    }
}
