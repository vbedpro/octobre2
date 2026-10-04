using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Guilds tab's scrollable list of every guild. Virtualized the exact same
// way as LeaderBoardUI's top-999 list: Content is positioned/sized by hand
// (NOT Vertical Layout Group / Content Size Fitter), and only enough rows
// to cover the visible viewport (+ buffer) are ever instantiated, reused
// as you scroll.
//
// Fully real data — fetches guilds via GuildManager.FetchGuildsList() and
// member lists via GuildManager.GetGuildMembers(). The only non-real data
// left is GuildDevTools' manually-populated fake players, kept as an
// opt-in testing aid (see GuildDevTools.cs).
public class GuildsListUI : MonoBehaviour
{
    [Header("Prefab")]
    public GuildRowUI rowPrefab;

    [Header("Virtualized Scroll")]
    public ScrollRect scrollRect;
    public RectTransform viewport;
    public RectTransform content;
    [Tooltip("Must match Row Prefab's actual height in pixels.")]
    public float rowHeight = 90f;
    public float rowSpacing = 10f;
    [Tooltip("Extra rows instantiated above/below the visible area so scrolling doesn't flash blank rows for a frame.")]
    public int bufferRows = 2;
    [Tooltip("Extra empty space below the last row, so it can scroll fully into view instead of being clipped/unreachable at the bottom of the list.")]
    public float bottomPadding = 0f;

    [Header("Guild Info Panel")]
    public GuildInfoPanelUI infoPanel;
    [Tooltip("Same LeaderboardUI that manages the Global tab's top-999 list — needed to look up which players belong to a tapped guild.")]
    public LeaderboardUI leaderboardUI;

    float RowStride => rowHeight + rowSpacing;

    private readonly List<GuildManager.GuildInfo> currentGuilds = new List<GuildManager.GuildInfo>();

    // Exposed so other scripts (e.g. the "Join Random Guild" button) can
    // pick from whatever's already loaded, without a separate fetch.
    // Already sorted (Contests Won desc, tiebreak Lifetime Rabbits desc) —
    // index+1 is each guild's current display rank.
    public List<GuildManager.GuildInfo> CurrentGuilds => currentGuilds;
    private readonly List<GuildRowUI> pool = new List<GuildRowUI>();

    // True once the FIRST RefreshGuildsList() call (in Start()) has
    // completed — lets other scripts (e.g. LeaderboardTabController, for
    // a "loading..." text) know whether the guild rows are actually
    // populated yet, same idea as LeaderboardUI.HasLoadedOnce.
    public bool HasLoadedOnce { get; private set; } = false;

    async void Start()
    {
        if (scrollRect != null)
            scrollRect.onValueChanged.AddListener(_ => RefreshVisibleRows());

        // Wait for GuildManager to actually finish sign-in/init first —
        // FetchGuildsList() silently returns an EMPTY list (no error) if
        // called too early, and nothing here would ever retry afterward,
        // which is exactly what made a real guild appear "instantly
        // missing" right after switching to a fresh account.
        while (GuildManager.Instance == null || !GuildManager.Instance.IsReady)
            await Task.Delay(100);

        // Also wait for leaderboardUI's OWN first fetch to complete —
        // otherwise GetCurrentEntries() (used by ComputeGuildLifetimeRabbits/
        // ComputeRealMemberCount) is still empty at this point, since that's
        // a separate independent script with its own load timing. Without
        // this, every guild row would show 0 rabbits until leaderboardUI
        // happened to finish loading on its own, which is exactly what was
        // happening before this fix.
        while (leaderboardUI == null || !leaderboardUI.HasLoadedOnce)
            await Task.Delay(100);

        await RefreshGuildsList();
        HasLoadedOnce = true;
        GuildTotalsRefreshLoop(); // fire-and-forget background loop, guarded by isDestroyed below
    }

    async void OnEnable()
    {
        // Repaint immediately with whatever's already cached, so reopening
        // the panel never looks blank/frozen while the fetch below runs.
        RefreshVisibleRows();

        // Then re-fetch for real. Other players' newly-created guilds
        // never showed up here until a full app restart, because the
        // periodic loop only recomputed totals for guilds ALREADY in
        // currentGuilds — it never re-pulled the guild list itself. This
        // catches up the moment the panel is reopened, instead of making
        // the player wait for the next periodic tick (or restart).
        if (GuildManager.Instance != null && GuildManager.Instance.IsReady
            && leaderboardUI != null && leaderboardUI.HasLoadedOnce)
        {
            await RefreshGuildsList();
            HasLoadedOnce = true;
        }
    }

    private bool isDestroyed = false;
    void OnDestroy() { isDestroyed = true; }

    // Real fetch from GuildManager's GuildsLeaderboard.
    public async Task RefreshGuildsList()
    {
        currentGuilds.Clear();
        if (GuildManager.Instance != null)
            currentGuilds.AddRange(await GuildManager.Instance.FetchGuildsList());

        // Compute REAL live totals immediately, not just on the periodic
        // loop's first (delayed) tick — otherwise every guild shows 0
        // lifetime rabbits until refreshInterval seconds have passed,
        // unlike the Global leaderboard which never has this gap.
        if (leaderboardUI != null)
        {
            foreach (GuildManager.GuildInfo guild in currentGuilds)
            {
                guild.lifetimeRabbits = ComputeGuildLifetimeRabbits(guild, leaderboardUI);
                guild.memberCount = ComputeRealMemberCount(guild, leaderboardUI);
            }
        }

        await PruneEmptyGuilds();

        SortGuilds();
        UpdateContentSize();
        RefreshVisibleRows();
        SyncOwnGuildRankIfPresent();
    }

    // Keeps every guild row's Lifetime Rabbits total and member count
    // genuinely live — refreshed on the SAME interval as the Global
    // leaderboard (read directly from leaderboardUI, so changing that
    // value keeps this in sync automatically). Runs as a plain async loop
    // (not a Coroutine) since it needs to await async pruning work.
    async void GuildTotalsRefreshLoop()
    {
        while (!isDestroyed)
        {
            float interval = leaderboardUI != null ? leaderboardUI.refreshInterval : 30f;
            await Task.Delay((int)(interval * 1000));
            if (isDestroyed) return;

            // Was RecomputeAllGuildTotals() — that only refreshed totals
            // for guilds ALREADY in currentGuilds, so a guild created by
            // another player never appeared here without a full app
            // restart. RefreshGuildsList() re-fetches from the server,
            // which is what actually catches new guilds, at the same
            // interval the Global leaderboard already refreshes at.
            await RefreshGuildsList();
        }
    }

    // Two-stage deletion, to guard against the top-999 cache's structural
    // blind spot (a real member outside that window would otherwise cause
    // a false-positive deletion):
    //   Stage 1 (cheap): the existing top-999 scan flags CANDIDATES that
    //     look empty — this is fast and runs for every guild, every tick.
    //   Stage 2 (expensive, rare): ONLY for candidates, an authoritative
    //     full-leaderboard scan (GuildManager.HasAnyMemberAsync) with an
    //     early exit the moment one member is found — cheap for populated
    //     guilds, only truly expensive for guilds that are actually empty.
    //   Grace period: a guild must be confirmed empty by Stage 2 across
    //     TWO SEPARATE refresh cycles (not just once) before it's actually
    //     deleted — protects against a transient scan hiccup.
    private readonly Dictionary<string, int> emptyConfirmationCounts = new Dictionary<string, int>();
    private const int RequiredEmptyConfirmations = 2;

    async Task PruneEmptyGuilds()
    {
        if (leaderboardUI == null || GuildManager.Instance == null) return;

        List<GuildManager.GuildInfo> candidates = currentGuilds
            .Where(g => ComputeRealMemberCount(g, leaderboardUI) <= 0)
            .ToList();

        List<string> confirmedForDeletion = new List<string>();

        foreach (GuildManager.GuildInfo guild in candidates)
        {
            bool hasMember = await GuildManager.Instance.HasAnyMemberAsync(guild.guildId, guild.leaderId);
            Debug.Log($"[GuildsListUI] Prune candidate '{guild.guildName}' ({guild.guildId}) — Cloud Code says hasMember={hasMember}");

            if (hasMember)
            {
                emptyConfirmationCounts.Remove(guild.guildId); // turned out populated — reset any prior strikes
                continue;
            }

            int strikes = emptyConfirmationCounts.TryGetValue(guild.guildId, out int existing) ? existing + 1 : 1;
            emptyConfirmationCounts[guild.guildId] = strikes;
            Debug.Log($"[GuildsListUI] '{guild.guildName}' strike {strikes}/{RequiredEmptyConfirmations}");

            if (strikes >= RequiredEmptyConfirmations)
                confirmedForDeletion.Add(guild.guildId);
        }

        if (confirmedForDeletion.Count > 0)
        {
            List<string> actuallyDisbanded = new List<string>();

            foreach (string id in confirmedForDeletion)
            {
                GuildManager.GuildInfo guild = currentGuilds.FirstOrDefault(g => g.guildId == id);
                if (guild == null) continue;

                Debug.Log($"[GuildsListUI] Attempting disband — guildName='{guild.guildName}', guildId='{guild.guildId}', leaderId='{guild.leaderId}', leaderName='{guild.leaderName}'");

                bool success = await GuildManager.Instance.DisbandGuildAsync(guild.guildId, guild.leaderId);
                if (success)
                {
                    actuallyDisbanded.Add(id);
                }
                else
                {
                    // Server-side write failed — don't remove it locally,
                    // that would just make it silently reappear on the
                    // next fresh fetch while looking successful right now.
                    // Keep its strike count so it's retried next sweep
                    // instead of resetting to 0.
                    Debug.LogWarning($"GuildsListUI: DisbandGuildAsync failed for '{guild.guildName}' ({id}) — leaving it in the list, will retry next sweep.");
                }
            }

            if (actuallyDisbanded.Count > 0)
            {
                currentGuilds.RemoveAll(g => actuallyDisbanded.Contains(g.guildId));
                foreach (string id in actuallyDisbanded) emptyConfirmationCounts.Remove(id);
                Debug.Log($"GuildsListUI: disbanded {actuallyDisbanded.Count} guild(s), each confirmed empty by an authoritative full-leaderboard scan across {RequiredEmptyConfirmations} separate checks.");
            }
        }
    }

    async Task RecomputeAllGuildTotals()
    {
        if (leaderboardUI == null) return;

        for (int i = 0; i < currentGuilds.Count; i++)
        {
            currentGuilds[i].lifetimeRabbits = ComputeGuildLifetimeRabbits(currentGuilds[i], leaderboardUI);
            currentGuilds[i].memberCount = ComputeRealMemberCount(currentGuilds[i], leaderboardUI);
        }

        await PruneEmptyGuilds();

        SortGuilds();
        UpdateContentSize();
        RefreshVisibleRows();
        SyncOwnGuildRankIfPresent();
    }

    // Live count of actual members: real tagged players (via the same
    // leaderboard scan GetGuildMembers uses) + local player safety net +
    // any dev-tool fake players.
    public static int ComputeRealMemberCount(GuildManager.GuildInfo guild, LeaderboardUI leaderboardUI)
    {
        int count = 0;

        if (leaderboardUI != null)
        {
            foreach (LeaderboardUI.RowData row in leaderboardUI.GetCurrentEntries())
                if (row.guildId == guild.guildId) count++;
        }

        // Always count the local player as a member of THEIR OWN guild,
        // even if the cached top-999 scan above hasn't caught up yet
        // (e.g. moments after creating/joining, before the leaderboard
        // refetches) — without this, a guild you just created gets
        // deleted for appearing to have 0 members, despite you being in it.
        bool alreadyCountedSelf = leaderboardUI != null
            && leaderboardUI.GetCurrentEntries().Any(row => row.guildId == guild.guildId && row.playerId == Unity.Services.Authentication.AuthenticationService.Instance.PlayerId);
        if (!alreadyCountedSelf && GuildManager.Instance != null && GuildManager.Instance.MyGuildId == guild.guildId)
            count++;

        count += GuildDevTools.GetPersistedFakeMembers(guild.guildId).Count;

        return count;
    }

    // Keeps GuildManager's cached MyGuildRank/ContestsWon/MemberCount
    // (used by GuildStatus_GuildMember's display) in sync with the guild's
    // ACTUAL current position in this sorted list — ranks shift over time
    // as totals change, not just at creation/join time.
    void SyncOwnGuildRankIfPresent()
    {
        if (GuildManager.Instance == null || !GuildManager.Instance.HasGuild) return;

        int index = currentGuilds.FindIndex(g => g.guildId == GuildManager.Instance.MyGuildId);
        if (index == -1) return;

        GuildManager.Instance.UpdateMyGuildStats(index + 1, currentGuilds[index].contestsWon, currentGuilds[index].memberCount);
    }

    // Real total: sums every player currently self-tagged with this
    // guildId, plus any dev-tool fake players persisted for it.
    public static long ComputeGuildLifetimeRabbits(GuildManager.GuildInfo guild, LeaderboardUI leaderboardUI)
    {
        long total = 0;

        if (leaderboardUI != null)
        {
            foreach (LeaderboardUI.RowData row in leaderboardUI.GetCurrentEntries())
                if (row.guildId == guild.guildId) total += row.lifetimeRabbits;
        }

        foreach (var member in GuildDevTools.GetPersistedFakeMembers(guild.guildId))
            total += member.row.lifetimeRabbits;

        return total;
    }

    // Ranking rule: Total Won Guild Contests first (higher = better rank);
    // ties broken by lifetime rabbits (higher = better rank).
    void SortGuilds()
    {
        currentGuilds.Sort((a, b) =>
        {
            int contestCompare = b.contestsWon.CompareTo(a.contestsWon);
            if (contestCompare != 0) return contestCompare;
            return b.lifetimeRabbits.CompareTo(a.lifetimeRabbits);
        });
    }

    void UpdateContentSize()
    {
        if (content == null) return;
        // rowHeight per item + rowSpacing BETWEEN items only (not after the
        // last one) — using count * RowStride here would add one extra
        // trailing rowSpacing past the final row, which is exactly what
        // let the list scroll past the last guild.
        float totalHeight = currentGuilds.Count > 0
            ? currentGuilds.Count * rowHeight + (currentGuilds.Count - 1) * rowSpacing + bottomPadding
            : 0f;
        content.sizeDelta = new Vector2(content.sizeDelta.x, totalHeight);
    }

    // ── Virtualization core ──────────────────────────────────────────
    void RefreshVisibleRows()
    {
        if (content == null || viewport == null || currentGuilds.Count == 0)
        {
            foreach (GuildRowUI r in pool) r.gameObject.SetActive(false);
            return;
        }

        float scrollOffset = Mathf.Max(0f, content.anchoredPosition.y);
        float viewportHeight = viewport.rect.height;

        int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scrollOffset / RowStride) - bufferRows);
        int visibleCount = Mathf.CeilToInt(viewportHeight / RowStride) + bufferRows * 2 + 1;
        int lastVisible = Mathf.Min(currentGuilds.Count - 1, firstVisible + visibleCount);

        int neededPoolSize = Mathf.Max(0, lastVisible - firstVisible + 1);
        while (pool.Count < neededPoolSize)
        {
            GuildRowUI r = Instantiate(rowPrefab, content);
            SetupRowRectTransform(r.GetComponent<RectTransform>());
            r.onRowClicked = (rank, guild) => OpenGuildInfo(rank, guild);
            pool.Add(r);
        }

        int poolIndex = 0;
        for (int dataIndex = firstVisible; dataIndex <= lastVisible; dataIndex++)
        {
            GuildRowUI row = pool[poolIndex];
            poolIndex++;

            PositionRow(row.GetComponent<RectTransform>(), dataIndex);
            row.gameObject.SetActive(true);
            row.Set(dataIndex + 1, currentGuilds[dataIndex]);
        }

        for (int i = poolIndex; i < pool.Count; i++)
            pool[i].gameObject.SetActive(false);
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

    // Injects a newly-created (or updated) guild into the browse list
    // immediately, instead of waiting for the next full refresh cycle.
    // Recomputes just ONE guild's live totals/member count and repaints
    // immediately — used right after a join/leave so the browse list
    // doesn't wait for the next periodic refresh to reflect it.
    public void RefreshSingleGuildTotals(string guildId)
    {
        if (leaderboardUI == null) return;

        GuildManager.GuildInfo guild = currentGuilds.FirstOrDefault(g => g.guildId == guildId);
        if (guild == null) return;

        guild.lifetimeRabbits = ComputeGuildLifetimeRabbits(guild, leaderboardUI);
        guild.memberCount = ComputeRealMemberCount(guild, leaderboardUI);

        // contestsWon can't be derived from a client-side scan the way the
        // other two can — but if this happens to be the local player's OWN
        // guild, GuildManager's cached value IS authoritative right after
        // a win, so pull it in directly instead of leaving this stale.
        if (GuildManager.Instance != null && guildId == GuildManager.Instance.MyGuildId)
            guild.contestsWon = GuildManager.Instance.MyGuildContestsWon;

        SortGuilds();
        RefreshVisibleRows();
        SyncOwnGuildRankIfPresent();
    }

    public void AddOrUpdateGuild(GuildManager.GuildInfo guild)
    {
        if (guild == null || string.IsNullOrEmpty(guild.guildId)) return;

        int index = currentGuilds.FindIndex(g => g.guildId == guild.guildId);
        if (index >= 0) currentGuilds[index] = guild;
        else currentGuilds.Add(guild);

        SortGuilds();
        UpdateContentSize();
        RefreshVisibleRows();
        SyncOwnGuildRankIfPresent();
    }

    async void OpenGuildInfo(int rank, GuildManager.GuildInfo guild)
    {
        AudioManager.Instance?.PlayUiElementClick();

        List<(int rank, LeaderboardUI.RowData row)> members = await GetMembersIncludingSelf(guild, leaderboardUI);
        if (infoPanel != null) infoPanel.Show(rank, guild, members);
    }

    // Real member list (via GuildManager.GetGuildMembers) + any dev-tool
    // fake players + a safety-net insertion of the local player if this
    // is their own guild and they aren't already reflected in the (maybe
    // stale) leaderboard scan.
    public static async Task<List<(int rank, LeaderboardUI.RowData row)>> GetMembersIncludingSelf(GuildManager.GuildInfo guild, LeaderboardUI leaderboardUI)
    {
        List<(int rank, LeaderboardUI.RowData row)> members;

        // Real guilds have a genuine leaderId to query directly (scalable,
        // no scanning). Fake dev-tool guilds don't — fall back to the old
        // scan-based fetch (still correctly finds cached top-999 real
        // members too, just isn't scalable — acceptable for guilds that
        // are themselves already just for local testing).
        if (GuildManager.Instance != null && !string.IsNullOrEmpty(guild.leaderId) && !guild.leaderId.StartsWith("fake_"))
        {
            members = await GuildManager.Instance.GetGuildMembersWithTrueRanksAsync(guild.guildId, leaderboardUI);
        }
        else
        {
            members = GuildManager.Instance != null
                ? GuildManager.Instance.GetGuildMembers(guild.guildId, leaderboardUI)
                : new List<(int, LeaderboardUI.RowData)>();
        }

        members.AddRange(GuildDevTools.GetPersistedFakeMembers(guild.guildId));

        bool isOwnGuild = GuildManager.Instance != null && GuildManager.Instance.MyGuildId == guild.guildId;
        if (isOwnGuild && leaderboardUI != null && LeaderboardManager.Instance != null)
        {
            bool alreadyPresent = members.Any(m => m.row.playerId == Unity.Services.Authentication.AuthenticationService.Instance.PlayerId);
            if (!alreadyPresent)
            {
                var fetched = await LeaderboardManager.Instance.FetchOwnScoreAsync();
                if (fetched != null)
                {
                    LeaderboardUI.RowData ownRow = leaderboardUI.ToRowData(fetched);
                    int ownRank = fetched.Rank + 1; // 0-indexed, same convention as the personal row fetch

                    int maxMembers = GuildManager.Instance.maxMembers;
                    members.Add((ownRank, ownRow));
                    if (members.Count > maxMembers)
                        members = members.OrderBy(m => m.rank).Take(maxMembers).ToList();
                }
            }
        }

        members = members.OrderBy(m => m.rank).ToList();
        return members;
    }
}
