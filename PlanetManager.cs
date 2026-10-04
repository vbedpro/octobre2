using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class PlanetManager : MonoBehaviour
{
    public static PlanetManager Instance { get; private set; }

    public enum Planet { Earth, Moon, Venus, Mars, Pluto }

    [Header("Planet Button (faded in, not SetActive'd — must stay active in Hierarchy)")]
    public GameObject planetButton;
    public float buttonFadeDuration = 0.4f;

    [Header("Settings Button (shifts right to make room once the planet button unlocks)")]
    public RectTransform settingsButtonRect;
    [Tooltip("Where the Settings button sits by default — same position the planet button occupies before it's unlocked.")]
    public Vector2 settingsButtonDefaultPosition;
    [Tooltip("Where the Settings button slides to once the planet button becomes visible, making room for it.")]
    public Vector2 settingsButtonShiftedPosition;
    public float settingsButtonSlideDuration = 0.4f;
    private Coroutine settingsButtonSlideCoroutine;

    [Header("Rabbit Road Material (different material per planet)")]
    [Tooltip("Each RabbitRoad's mesh gets assigned this material directly (via mr.material, an instance) — a distinct look per planet, not just a color tint.")]
    public Material earthRoadMaterial;
    public Material moonRoadMaterial;
    public Material venusRoadMaterial;
    public Material marsRoadMaterial;
    public Material plutoRoadMaterial;

    [Header("Truck & Garage (5 separate objects per planet, same position)")]
    [Tooltip("Each truck must have its own TruckController component. Only one truck/garage pair is ever active at a time.")]
    public GameObject earthTruck;
    public GameObject moonTruck;
    public GameObject venusTruck;
    public GameObject marsTruck;
    public GameObject plutoTruck;
    public GameObject earthGarage;
    public GameObject moonGarage;
    public GameObject venusGarage;
    public GameObject marsGarage;
    public GameObject plutoGarage;

    [Header("Ground Planes (one separate GameObject per planet, same position)")]
    [Tooltip("5 separate ground plane GameObjects, all occupying the same physical position, each with its own material/texture AND its own independent baked lightmap — this is what lets Earth's baked shadows (rocks/trees/flowers) exist ONLY on Earth's plane and never bleed onto the others, which real-time shadows or a single shared re-skinned plane can't achieve. Only one is ever active at a time. Order: Earth, Moon, Venus, Mars, Pluto.")]
    public GameObject earthGroundPlane;
    public GameObject moonGroundPlane;
    public GameObject venusGroundPlane;
    public GameObject marsGroundPlane;
    public GameObject plutoGroundPlane;

    [Header("Per-Planet Environment Decoration")]
    [Tooltip("Objects specific to each planet (rocks, trees, flowers, crystals, etc.) — each array is only shown while that planet is active, hidden on every other planet.")]
    public GameObject[] earthEnvironmentObjects;
    public GameObject[] moonEnvironmentObjects;
    public GameObject[] venusEnvironmentObjects;
    public GameObject[] marsEnvironmentObjects;
    public GameObject[] plutoEnvironmentObjects;

    [Header("Per-Planet Lighting (5 separate Light objects, same position — only one active at a time)")]
    [Tooltip("Configure each Light directly in the Inspector (color, intensity, rotation, temperature, etc.) exactly like normal — only one is ever active at once, matching the ground plane setup. Much easier to tune live and to set up before each planet's bake pass than editing shared fields in a script.")]
    public GameObject earthLight;
    public GameObject moonLight;
    public GameObject venusLight;
    public GameObject marsLight;
    public GameObject plutoLight;

    [Tooltip("A general fill light kept on for every planet except Pluto.")]
    public GameObject fillLight;

    [Tooltip("Optional — ambient/environment light tint per planet. Leave all white if you don't want to touch this.")]
    public Color earthAmbientColor = Color.white;
    public Color moonAmbientColor = Color.white;
    public Color venusAmbientColor = Color.white;
    public Color marsAmbientColor = Color.white;
    public Color plutoAmbientColor = Color.white;

    [Header("State")]
    public Planet currentPlanet = Planet.Earth;

    // index 0 = Earth (always true), 1 = Moon, 2 = Venus, 3 = Mars, 4 = Pluto
    private bool[] unlockedPlanets = new bool[5] { true, false, false, false, false };

    // One PlanetData per planet that has EVER been unlocked this session
    // (created fresh the moment it's first switched to). Earth's entry
    // isn't created here — Earth's "data" IS the live GameManager/
    // UpgradeManager state until you switch away from it for the first time.
    private readonly Dictionary<Planet, PlanetData> planetDataByPlanet = new Dictionary<Planet, PlanetData>();

    // Foxes paused (not destroyed) when their planet is switched away from —
    // reactivated exactly as they were when the player returns.
    private readonly Dictionary<Planet, List<GameObject>> frozenFoxesByPlanet = new Dictionary<Planet, List<GameObject>>();

    private CanvasGroup buttonCanvasGroup;
    private Coroutine fadeCoroutine;
    private bool buttonCurrentlyShown = false;
    private bool isSwitching = false;

    void Awake()
    {
        Instance = this;

        if (planetButton != null)
        {
            buttonCanvasGroup = planetButton.GetComponent<CanvasGroup>();
            if (buttonCanvasGroup == null)
                buttonCanvasGroup = planetButton.AddComponent<CanvasGroup>();

            buttonCanvasGroup.alpha = 0f;
            buttonCanvasGroup.interactable = false;
            buttonCanvasGroup.blocksRaycasts = false;
        }
    }

    IEnumerator Start()
    {
        yield return null;
        yield return null;
        SyncFromSavedLevel();
        AudioManager.Instance?.PlayMusicForPlanet(currentPlanet); // fresh players start here; RestoreAllPlanetData (if a save exists) corrects/crossfades this a moment later if needed
        AchievementsManager.Instance?.OnPlanetChanged(currentPlanet);
        StartCoroutine(BackgroundSimLoop());
    }

    [Header("Background Simulation (inactive planets keep producing)")]
    [Tooltip("How often, in real seconds, inactive planets get their reproduction caught up.")]
    public float backgroundSimInterval = 2f;

    private readonly Dictionary<Planet, float> lastSimTime = new Dictionary<Planet, float>();

    IEnumerator BackgroundSimLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(backgroundSimInterval);
            SimulateInactivePlanets();
        }
    }

    // Catches up reproduction for every UNLOCKED planet that ISN'T the one
    // currently being played, using the exact same "potential production
    // capped by remaining hut capacity" math RabbitHut.CalculateOfflineEarnings
    // already uses for offline earnings — just run continuously on a timer
    // instead of once at launch. This is what makes every unlocked planet's
    // rabbits contribute to lifetimeRabbits simultaneously, not just
    // whichever one you're actively looking at.
    void SimulateInactivePlanets()
    {
        for (int i = 0; i < unlockedPlanets.Length; i++)
        {
            Planet planet = (Planet)i;
            if (!unlockedPlanets[i]) continue;
            if (planet == currentPlanet) continue; // live huts already handle this one

            if (!lastSimTime.TryGetValue(planet, out float last))
            {
                lastSimTime[planet] = Time.time;
                continue; // first time seeing this planet — nothing to catch up yet
            }

            float elapsed = Time.time - last;
            if (elapsed <= 0f) continue;
            lastSimTime[planet] = Time.time;

            PlanetData data = GetOrCreatePlanetData(planet);
            ApplyBackgroundProduction(data, elapsed);
        }
    }

    void ApplyBackgroundProduction(PlanetData data, float elapsedSeconds)
    {
        ApplyBackgroundProduction(data, elapsedSeconds, ComputeReproductionRate(data));
    }

    // Overload that takes an explicit rate instead of deriving it —
    // used by offline-earnings math to run two sequential segments (one
    // at the Explosive Reproduction boosted rate, one at the normal rate)
    // instead of one flat rate for the whole elapsed window.
    void ApplyBackgroundProduction(PlanetData data, float elapsedSeconds, float rabbitsPerSecond)
    {
        if (rabbitsPerSecond <= 0f) return;

        long totalEarned = 0;

        for (int i = 0; i < data.hutCount && i < data.hutLevel.Length; i++)
        {
            long currentInHut = data.hutRabbits[i];
            if (currentInHut < 2) continue; // matches RabbitHut's own "need 2+ to breed" gate

            long capacity = ComputeHutCapacity(data, data.hutLevel[i]);
            long remainingCapacity = System.Math.Max(0, capacity - currentInHut);
            if (remainingCapacity <= 0) continue;

            long potential = (long)(rabbitsPerSecond * elapsedSeconds);
            long earned = System.Math.Min(potential, remainingCapacity);
            if (earned <= 0) continue;

            data.hutRabbits[i] += earned;
            totalEarned += earned;
        }

        if (totalEarned > 0)
        {
            data.totalRabbits += totalEarned;
            // Lifetime rabbits is GLOBAL (shared leaderboard stat, sums
            // across every planet) — bump it directly here since this
            // planet isn't running through GameManager.AddRabbits() right now.
            if (GameManager.Instance != null)
                GameManager.Instance.lifetimeRabbits += totalEarned;
        }
    }

    // Reproduction rate for a planet's OWN saved shop levels — mirrors
    // UpgradeManager.ApplyReproduction()'s formula, but reads levels from
    // this planet's saved PlanetData instead of the live active rows.
    // Row CONFIG (incrementPerLevel, category) is shared/static regardless
    // of planet — only currentLevel differs per planet — so we look up
    // each row's config from whatever's live in the scene, but the LEVEL
    // from the planet's own saved data.
    float ComputeReproductionRate(PlanetData data)
    {
        float baseRate = ComputeBaseReproductionRate(data);

        float explosiveMultiplier = ExplosiveReproductionBoost.GetMultiplierFor(data.planet);
        return baseRate * explosiveMultiplier;
    }

    // Same as above but WITHOUT the Explosive Reproduction multiplier —
    // used by offline-earnings math to compute "what the rate would be
    // without the boost," for whatever portion of an offline window falls
    // after the boost's expiry.
    public float ComputeBaseReproductionRate(PlanetData data)
    {
        float multiplier = 1f;
        ShopUpgradeRow[] rows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in rows)
        {
            if (row.isEpic) continue;
            if (row.category != UpgradeManager.UpgradeCategory.Reproduction) continue;

            int level = GetSavedLevel(data, row.gameObject.name);
            multiplier *= (1f + (level * row.incrementPerLevel) / 100f);
        }

        float permBuff = PrestigeManager.Instance != null
            ? PrestigeManager.Instance.ReproductionGameplayMultiplier()
            : 1f;

        // Quest buffs/debuffs intentionally NOT included here — those are
        // short-lived and tied to the active session's QuestManager state,
        // not meaningfully trackable per-inactive-planet.
        return 1f * multiplier * permBuff;
    }

    long ComputeHutCapacity(PlanetData data, int hutLevel)
    {
        if (HutUpgradeManager.Instance == null || HutUpgradeManager.Instance.levelCapacities == null) return 0;
        int index = Mathf.Clamp(hutLevel - 1, 0, HutUpgradeManager.Instance.levelCapacities.Length - 1);
        long baseCapacity = HutUpgradeManager.Instance.levelCapacities[index];

        float shopPct = 0f;
        ShopUpgradeRow[] rows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in rows)
        {
            if (row.isEpic) continue;
            if (row.category != UpgradeManager.UpgradeCategory.HutCapacity) continue;
            int level = GetSavedLevel(data, row.gameObject.name);
            shopPct += level * row.incrementPerLevel;
        }

        float prestigePct = PrestigeManager.Instance != null
            ? PrestigeManager.Instance.HutCapacityGameplayBonusFraction()
            : 0f;

        return (long)(baseCapacity * (1f + shopPct / 100f + prestigePct));
    }

    int GetSavedLevel(PlanetData data, string rowName)
    {
        for (int i = 0; i < data.commonUpgradeId.Count; i++)
            if (data.commonUpgradeId[i] == rowName) return data.commonUpgradeLevel[i];
        return 0;
    }

    PlanetData GetOrCreatePlanetData(Planet planet)
    {
        GameObject[] huts = UpgradeManager.Instance != null ? UpgradeManager.Instance.huts : null;
        int hutSlotCount = huts != null ? huts.Length : 0;

        if (!planetDataByPlanet.TryGetValue(planet, out PlanetData data))
        {
            data = new PlanetData(planet, hutSlotCount);
            planetDataByPlanet[planet] = data;
        }
        return data;
    }

    void SyncFromSavedLevel()
    {
        ShopUpgradeRow[] allRows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in allRows)
        {
            if (row.category == UpgradeManager.UpgradeCategory.ExtraFarm)
            {
                ApplyUnlocksUpTo(row.currentLevel);
                RefreshPlanetButtonVisibility(instant: true);
                return;
            }
        }
    }

    public void OnExtraFarmLevelChanged(int newLevel)
    {
        ApplyUnlocksUpTo(newLevel);
        RefreshPlanetButtonVisibility(instant: false);
    }

    void ApplyUnlocksUpTo(int level)
    {
        for (int i = 1; i <= level && i < unlockedPlanets.Length; i++)
            unlockedPlanets[i] = true;

        int unlockedCount = 0;
        foreach (bool unlocked in unlockedPlanets)
            if (unlocked) unlockedCount++;
        AchievementsManager.Instance?.ReportPlanetsUnlockedCount(unlockedCount);
    }

    public bool IsUnlocked(Planet planet) => unlockedPlanets[(int)planet];

    void RefreshPlanetButtonVisibility(bool instant)
    {
        bool shouldShow = unlockedPlanets[1];
        if (shouldShow == buttonCurrentlyShown) return;
        buttonCurrentlyShown = shouldShow;

        Vector2 targetSettingsPos = shouldShow ? settingsButtonShiftedPosition : settingsButtonDefaultPosition;

        if (buttonCanvasGroup == null)
        {
            ApplySettingsButtonPosition(targetSettingsPos, instant);
            return;
        }

        if (instant)
        {
            buttonCanvasGroup.alpha = shouldShow ? 1f : 0f;
            buttonCanvasGroup.interactable = shouldShow;
            buttonCanvasGroup.blocksRaycasts = shouldShow;
            ApplySettingsButtonPosition(targetSettingsPos, instant: true);
            return;
        }

        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeButton(shouldShow));
        ApplySettingsButtonPosition(targetSettingsPos, instant: false);
    }

    void ApplySettingsButtonPosition(Vector2 target, bool instant)
    {
        if (settingsButtonRect == null) return;

        if (instant)
        {
            settingsButtonRect.anchoredPosition = target;
            return;
        }

        if (settingsButtonSlideCoroutine != null) StopCoroutine(settingsButtonSlideCoroutine);
        settingsButtonSlideCoroutine = StartCoroutine(SlideSettingsButton(target));
    }

    IEnumerator SlideSettingsButton(Vector2 target)
    {
        Vector2 start = settingsButtonRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < settingsButtonSlideDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / settingsButtonSlideDuration;
            t = t * t * (3f - 2f * t);
            settingsButtonRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }
        settingsButtonRect.anchoredPosition = target;
    }

    IEnumerator FadeButton(bool show)
    {
        if (show)
        {
            buttonCanvasGroup.interactable = true;
            buttonCanvasGroup.blocksRaycasts = true;
        }

        float startAlpha = buttonCanvasGroup.alpha;
        float targetAlpha = show ? 1f : 0f;
        float elapsed = 0f;

        while (elapsed < buttonFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / buttonFadeDuration);
            buttonCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        buttonCanvasGroup.alpha = targetAlpha;

        if (!show)
        {
            buttonCanvasGroup.interactable = false;
            buttonCanvasGroup.blocksRaycasts = false;
        }

        fadeCoroutine = null;
    }

    // ── Planet switching ─────────────────────────────────────────────

    public void SwitchToPlanet(Planet target)
    {
        if (isSwitching) return;
        if (target == currentPlanet) return;
        if (!IsUnlocked(target)) return;

        // Auto-decline the Accept/Decline ad panel if it's still open —
        // leaving it open across a planet switch would be a confusing/
        // stuck state, and there's no meaningful "keep watching" option
        // once the player has already navigated away.
        if (AdNotificationManager.Instance != null && AdNotificationManager.Instance.IsPanelOpen)
            AdNotificationManager.Instance.OnDeclinePressed();

        isSwitching = true;
        Planet outgoingPlanet = currentPlanet;

        // Robotic Hatching boost: pause auto-hatching for the duration of
        // this transition (boost's own timer keeps running regardless).
        RoboticHatchingBoost.Instance?.PauseForTransition();

        // 1) Wipe in-transit RABBITS — a rabbit mid-walk-animation belonging
        //    to the outgoing planet shouldn't still be onscreen once we've
        //    switched to a different planet's data. Foxes are handled
        //    separately below (frozen, not destroyed).
        ClearInTransitEntities();
        FreezeFoxesForOutgoingPlanet(outgoingPlanet);

        // Fully reset every truck (all planets) — fixes a truck freezing
        // mid-route if the player switches planets while it's out on a
        // delivery, then comes back later.
        TruckController.ResetAllTrucks();

        // 2) Save the OUTGOING planet's live state into its PlanetData.
        SaveCurrentPlanetState(currentPlanet);

        // 3) Load the INCOMING planet's state into the live scene. currentPlanet
        //    must be updated FIRST — LoadPlanetState refreshes every shop row's
        //    displayed price, which reads currentPlanet to apply the correct
        //    per-planet price multiplier. If this were set after, prices would
        //    briefly (and incorrectly) recalculate using the OLD planet.
        currentPlanet = target;

        AudioManager.Instance?.PlayMusicForPlanet(target);
        AchievementsManager.Instance?.ReportPlanetSwitch();
        AchievementsManager.Instance?.OnPlanetChanged(target);
        LoadPlanetState(target);
        ThawFoxesForIncomingPlanet(target);

        lastSimTime.Remove(target); // now active — live huts handle it, not background sim
        lastSimTime[outgoingPlanet] = Time.time; // start counting from NOW, not from whenever it was last active
        isSwitching = false;

        // Robotic Hatching boost: resume auto-hatching now that the
        // transition to the new planet is fully complete.
        RoboticHatchingBoost.Instance?.ResumeAfterTransition();

        // Let PlanetColumnUI (if present) refresh the button's own icon.
        PlanetColumnUI columnUI = planetButton != null ? planetButton.GetComponent<PlanetColumnUI>() : null;
        if (columnUI != null) columnUI.RefreshButtonIcon();
    }

    void ClearInTransitEntities()
    {
        foreach (RabbitMover rabbit in FindObjectsByType<RabbitMover>(FindObjectsInactive.Include))
        {
            // Release the reserved spot on whatever hut this rabbit was
            // heading toward BEFORE destroying it — otherwise the
            // reservation permanently sticks, making that hut appear
            // "full" forever even with zero real rabbits inside, since
            // arrival (RabbitEntered) is what normally releases it and
            // that never gets to run for a rabbit destroyed mid-transit.
            if (rabbit.targetHut != null)
                rabbit.targetHut.ReleaseReservedSpot();

            Destroy(rabbit.gameObject);
        }
        foreach (BarnSpawner spawner in FindObjectsByType<BarnSpawner>(FindObjectsInactive.Include))
            spawner.OnButtonUp(); // stop any active hold-to-hatch
    }

    // Foxes belonging to the OUTGOING planet are PAUSED, not destroyed —
    // deactivating a GameObject stops its Update()/coroutines entirely and
    // preserves its exact transform/state for free, so it resumes exactly
    // where it left off when this planet becomes active again. This closes
    // an exploit where switching planets and back would silently wipe out
    // every fox for free. The spawner's own component is also disabled
    // (not the whole GameObject, so anything it visually renders stays
    // intact) so its internal spawn-timer freezes too, instead of either
    // continuing to count down unfairly or resetting to give free breathing
    // room — same "paused, not reset" treatment as the foxes themselves.
    void FreezeFoxesForOutgoingPlanet(Planet outgoing)
    {
        List<GameObject> frozen = new List<GameObject>();
        foreach (FoxMover fox in FindObjectsByType<FoxMover>(FindObjectsInactive.Include))
        {
            if (!fox.gameObject.activeSelf) continue; // already inactive — not this planet's live fox

            // FoxPaths are SHARED physical objects across every planet
            // (same as huts) — a frozen fox never reaches the end of its
            // path to naturally clear `occupied`, so without this, enough
            // frozen foxes would eventually mark every path occupied and
            // silently stop spawning on EVERY planet, not just this one.
            if (fox.path != null) fox.path.occupied = false;

            fox.gameObject.SetActive(false);
            frozen.Add(fox.gameObject);
        }
        frozenFoxesByPlanet[outgoing] = frozen;

        FoxSpawner spawner = FindAnyObjectByType<FoxSpawner>();
        if (spawner != null) spawner.enabled = false;
    }

    // Reactivates whatever foxes were frozen for this planet the last time
    // it was switched away from — exactly where they were, mid-path and
    // all. Also resumes the spawner's own timer from wherever it was
    // paused, rather than resetting it.
    void ThawFoxesForIncomingPlanet(Planet incoming)
    {
        if (frozenFoxesByPlanet.TryGetValue(incoming, out List<GameObject> frozen))
        {
            foreach (GameObject go in frozen)
            {
                if (go == null) continue;
                go.SetActive(true);

                // Re-claim the path now that this fox is active again, so
                // the spawner doesn't put a second fox on the same path
                // while this one resumes its journey.
                FoxMover fox = go.GetComponent<FoxMover>();
                if (fox != null && fox.path != null) fox.path.occupied = true;
            }
            frozenFoxesByPlanet.Remove(incoming);
        }

        FoxSpawner spawner = FindAnyObjectByType<FoxSpawner>();
        if (spawner != null) spawner.enabled = true;
    }

    void SaveCurrentPlanetState(Planet outgoing)
    {
        GameObject[] huts = UpgradeManager.Instance != null ? UpgradeManager.Instance.huts : null;
        int hutSlotCount = huts != null ? huts.Length : 0;

        if (!planetDataByPlanet.TryGetValue(outgoing, out PlanetData data))
        {
            data = new PlanetData(outgoing, hutSlotCount);
            planetDataByPlanet[outgoing] = data;
        }

        // Resources.
        if (GameManager.Instance != null)
        {
            data.totalRabbits = GameManager.Instance.totalRabbits;
            data.totalCoins = GameManager.Instance.totalCoins;
        }

        // Huts.
        data.hutCount = UpgradeManager.Instance != null ? UpgradeManager.Instance.hutCount : 1;
        if (huts != null)
        {
            if (data.hutLevel == null || data.hutLevel.Length != hutSlotCount) data.hutLevel = new int[hutSlotCount];
            if (data.hutRabbits == null || data.hutRabbits.Length != hutSlotCount) data.hutRabbits = new long[hutSlotCount];

            for (int i = 0; i < hutSlotCount; i++)
            {
                if (huts[i] == null) continue;
                HutUpgradable hu = huts[i].GetComponent<HutUpgradable>();
                RabbitHut rh = huts[i].GetComponent<RabbitHut>();
                data.hutLevel[i] = hu != null ? hu.level : 1;
                data.hutRabbits[i] = rh != null ? rh.GetCurrentRabbits() : 0;
            }
        }

        // Common shop upgrades (non-Epic only — Epic is global/permanent).
        data.commonUpgradeId.Clear();
        data.commonUpgradeLevel.Clear();
        ShopUpgradeRow[] rows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in rows)
        {
            if (row.isEpic) continue;
            // Extra Farm's own level is a GLOBAL unlock progression (same
            // 4 levels regardless of which planet's shop you're viewing —
            // per your earlier spec), so it's deliberately excluded from
            // per-planet save data and just left at whatever it already is.
            if (row.category == UpgradeManager.UpgradeCategory.ExtraFarm) continue;

            data.commonUpgradeId.Add(row.gameObject.name);
            data.commonUpgradeLevel.Add(row.currentLevel);
        }

        // Truck — store the ABSOLUTE moment the truck becomes ready as a
        // REAL-WORLD timestamp (not Time.time, which resets to 0 every
        // app launch and would make this meaningless after a quit/
        // relaunch). Works correctly both for switching planets within a
        // session and for quitting and coming back later.
        if (GameManager.Instance != null)
        {
            float remaining = GameManager.Instance.GetTruckTimeRemaining();
            data.truckReadyAtUnixSeconds = NowUnixSeconds() + remaining;
        }

        // Quest buffs/debuffs — capture whatever this planet's live
        // accumulated bonus currently is, so it's preserved exactly when
        // switching away (these are otherwise single global fields on
        // HutUpgradeManager/UpgradeManager, not naturally per-planet).
        if (HutUpgradeManager.Instance != null)
            data.questHutCapacityBonusPercent = HutUpgradeManager.Instance.GetQuestCapacityBonusPercent();
        if (UpgradeManager.Instance != null)
        {
            data.questTruckCapacityBonusPercent = UpgradeManager.Instance.GetQuestTruckCapacityBonusPercent();
            data.questReproductionBonusPercent = UpgradeManager.Instance.GetQuestReproductionBonusPercent();
            data.questHatchRateBonusPercent = UpgradeManager.Instance.GetQuestHatchRateBonus();
        }
    }

    void LoadPlanetState(Planet incoming)
    {
        Debug.Log($"[RabbitDiag] LoadPlanetState() called for {incoming}. Stack trace:\n{System.Environment.StackTrace}");
        GameObject[] huts = UpgradeManager.Instance != null ? UpgradeManager.Instance.huts : null;
        int hutSlotCount = huts != null ? huts.Length : 0;

        if (!planetDataByPlanet.TryGetValue(incoming, out PlanetData data))
        {
            // First time ever visiting this planet — brand new, empty state.
            data = new PlanetData(incoming, hutSlotCount);
            planetDataByPlanet[incoming] = data;
        }

        // Resources.
        if (GameManager.Instance != null)
        {
            GameManager.Instance.totalRabbits = data.totalRabbits;
            GameManager.Instance.totalCoins = data.totalCoins;
            GameManager.Instance.AddRabbits(0); // refresh HUD text, same trick SaveManager uses
            GameManager.Instance.AddCoins(0);
        }

        // [RabbitDiag] Confirm exactly what this planet's raw save data
        // actually contains, right as it's about to be applied to huts.
        {
            long loadedHutSum = 0;
            if (data.hutRabbits != null)
                foreach (long r in data.hutRabbits) loadedHutSum += r;
            Debug.Log($"[RabbitDiag] LOAD: planetData.totalRabbits={data.totalRabbits:N0}, sum(hutRabbits[])={loadedHutSum:N0}, hutRabbits.Length={(data.hutRabbits != null ? data.hutRabbits.Length : -1)}, hutSlotCount(live huts array)={hutSlotCount}");
        }

        // Common shop upgrade levels — MUST be restored BEFORE the huts
        // loop below. HutUpgradeManager.ApplyLoadedHutLevel() computes
        // each hut's capacity via ShopMultiplier(), which reads these
        // rows' currentLevel live — if that ran with rows still at their
        // pre-restore default level (0/1), capacity would be computed far
        // too small, and the hut population restore right after would
        // get silently CLAMPED DOWN to match that wrong, too-small
        // capacity — permanently destroying real progress. This exact
        // ordering bug is what caused hut occupancy to reset lower on
        // every single reload once the player had bought ANY hut
        // capacity upgrade (only then does the shop bonus meaningfully
        // change what capacity SHOULD be, creating something to
        // incorrectly clamp against).
        ShopUpgradeRow[] rows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in rows)
        {
            if (row.isEpic) continue;
            if (row.category == UpgradeManager.UpgradeCategory.ExtraFarm)
            {
                // Level itself stays global/untouched, but the row's
                // displayed MAX/buyable state depends on currentPlanet
                // (see ShopUpgradeRow.IsMaxed()) — without this refresh,
                // it would stay frozen at whatever it last showed on the
                // PREVIOUS planet instead of re-evaluating on arrival here.
                row.RefreshUIPublic();
                continue;
            }

            int foundLevel = 0;
            for (int i = 0; i < data.commonUpgradeId.Count; i++)
            {
                if (data.commonUpgradeId[i] == row.gameObject.name)
                {
                    foundLevel = data.commonUpgradeLevel[i];
                    break;
                }
            }
            row.currentLevel = foundLevel;
            row.RefreshUIPublic();
        }

        // Quest buffs/debuffs — restored here too, for the same reason as
        // shop levels above: SetQuestCapacityBonusPercent() internally
        // calls RecalculateAllCapacities(), which re-clamps every hut's
        // population — so it must run BEFORE the huts loop restores that
        // population, not after.
        if (HutUpgradeManager.Instance != null)
            HutUpgradeManager.Instance.SetQuestCapacityBonusPercent(data.questHutCapacityBonusPercent);
        if (UpgradeManager.Instance != null)
        {
            UpgradeManager.Instance.SetQuestTruckCapacityBonusPercent(data.questTruckCapacityBonusPercent);
            UpgradeManager.Instance.SetQuestReproductionBonusPercent(data.questReproductionBonusPercent);
            UpgradeManager.Instance.SetQuestHatchRateBonus(data.questHatchRateBonusPercent);
        }

        // Huts — activated slot count first, then level + population per hut.
        if (UpgradeManager.Instance != null)
            UpgradeManager.Instance.ApplyLoadedHutCount(Mathf.Max(1, data.hutCount));

        if (huts != null && data.hutLevel != null && data.hutRabbits != null)
        {
            for (int i = 0; i < huts.Length && i < data.hutLevel.Length; i++)
            {
                if (huts[i] == null) continue;
                HutUpgradable hu = huts[i].GetComponent<HutUpgradable>();
                RabbitHut rh = huts[i].GetComponent<RabbitHut>();

                if (hu != null && HutUpgradeManager.Instance != null)
                    HutUpgradeManager.Instance.ApplyLoadedHutLevel(hu, Mathf.Max(1, data.hutLevel[i]));

                if (rh != null && i < data.hutRabbits.Length)
                    rh.SetCurrentRabbits(data.hutRabbits[i]);
            }
        }

        // Recompute every derived stat (truck capacity, hatch speed, etc.)
        // now that shop levels reflect the incoming planet.
        if (UpgradeManager.Instance != null)
        {
            foreach (UpgradeManager.UpgradeCategory cat in System.Enum.GetValues(typeof(UpgradeManager.UpgradeCategory)))
                UpgradeManager.Instance.OnCategoryChanged(cat);
        }

        // Truck — resume this planet's own cooldown, correctly accounting
        // for real time that passed, whether from switching planets
        // within a session OR quitting and coming back later.
        if (GameManager.Instance != null)
        {
            float remaining = (float)System.Math.Max(0, data.truckReadyAtUnixSeconds - NowUnixSeconds());
            GameManager.Instance.SetTruckState(remaining <= 0f, remaining);
        }

        // Re-model huts already at max level to match the incoming planet.
        RefreshHutPlanetVisuals(incoming);
        StartCoroutine(ApplyRoadMaterialDeferred(incoming)); // deferred - ApplyLoadedHutCount above may have just activated new roads this frame
        ApplyTruckAndGarage(incoming);
        ApplyGroundPlane(incoming);
        RefreshEnvironmentDecoration(incoming);
        ApplyLighting(incoming);
    }

    // Activates only the incoming planet's own hut object set, deactivates
    // every other planet's set, and — critically — repoints
    // UpgradeManager.huts to the newly-activated array. This is what lets
    // every existing hut system (leveling, capacity, population tracking,
    // save/load restoration, tap-to-upgrade, rabbit/fox targeting) keep
    // working completely unchanged after this call — they all read
    // UpgradeManager.Instance.huts, so they automatically operate on
    // whichever planet's physical objects are now active, with zero
    // changes needed to any of those other scripts.
    // Huts are shared single objects again (not duplicated per planet) —
    // only the MAX-LEVEL visual model differs per planet, handled inside
    // HutUpgradable itself. This just tells every hut currently at max
    // level to re-check which planet it should be showing, so a hut
    // that's already maxed correctly updates its model the instant you
    // switch planets, without needing a full re-level.
    // Public entry point — always goes through the one-frame-deferred
    // version, since RabbitRoad.Start() (which assigns the road's DEFAULT
    // material) doesn't run synchronously the instant a road's GameObject
    // is activated — it's deferred to just before the next frame. Applying
    // our planet-specific material immediately after activation risks
    // Start() overwriting it a moment later. Waiting one frame guarantees
    // Start() has already run first.
    public void RefreshRoadMaterialForCurrentPlanet()
    {
        StartCoroutine(ApplyRoadMaterialDeferred(currentPlanet));
    }

    IEnumerator ApplyRoadMaterialDeferred(Planet planet)
    {
        yield return null;
        ApplyRoadMaterial(planet);
    }

    void ApplyTruckAndGarage(Planet planet)
    {
        GameObject activeTruck = planet switch
        {
            Planet.Earth => earthTruck,
            Planet.Moon => moonTruck,
            Planet.Venus => venusTruck,
            Planet.Mars => marsTruck,
            Planet.Pluto => plutoTruck,
            _ => earthTruck
        };

        // Activate/deactivate everything first.
        SetSingleActive(earthTruck, planet == Planet.Earth);
        SetSingleActive(moonTruck, planet == Planet.Moon);
        SetSingleActive(venusTruck, planet == Planet.Venus);
        SetSingleActive(marsTruck, planet == Planet.Mars);
        SetSingleActive(plutoTruck, planet == Planet.Pluto);

        SetSingleActive(earthGarage, planet == Planet.Earth);
        SetSingleActive(moonGarage, planet == Planet.Moon);
        SetSingleActive(venusGarage, planet == Planet.Venus);
        SetSingleActive(marsGarage, planet == Planet.Mars);
        SetSingleActive(plutoGarage, planet == Planet.Pluto);

        if (activeTruck == null)
        {
            Debug.LogWarning($"PlanetManager: no truck assigned for {planet} — SellManager.truck was NOT updated and may still point at a now-inactive truck from another planet.");
            return;
        }
        TruckController controller = activeTruck.GetComponent<TruckController>();
        if (controller == null)
        {
            Debug.LogWarning($"PlanetManager: {planet}'s truck object has no TruckController component — SellManager.truck was NOT updated.");
            return;
        }

        // Just preserve whatever rotation this truck ALREADY has (however
        // you rotated it in the Editor/prefab) as its permanent "home"
        // orientation. No separate configurable field — rotate the truck
        // object itself to look right, and it sticks. This still fixes
        // the original bug (Awake() only runs once, so without this,
        // a truck's cached home rotation could get stuck at whatever it
        // was the very first time it was ever activated, ignoring any
        // later changes) — it just does so WITHOUT overriding your value
        // with anything else.
        controller.SetHomeTransform(activeTruck.transform.position, activeTruck.transform.rotation);

        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null) sm.truck = controller;
    }

    static void SetSingleActive(GameObject go, bool active)
    {
        if (go != null) go.SetActive(active);
    }


    void ApplyRoadMaterial(Planet planet)
    {
        Material material = planet switch
        {
            Planet.Earth => earthRoadMaterial,
            Planet.Moon => moonRoadMaterial,
            Planet.Venus => venusRoadMaterial,
            Planet.Mars => marsRoadMaterial,
            Planet.Pluto => plutoRoadMaterial,
            _ => earthRoadMaterial
        };
        if (material == null) return; // leave whatever's already there if this planet's material isn't assigned yet

        RabbitRoad[] roads = FindObjectsByType<RabbitRoad>(FindObjectsInactive.Include);
        foreach (RabbitRoad road in roads)
        {
            MeshRenderer mr = road.GetComponent<MeshRenderer>();
            if (mr == null) continue; // hasn't Start()'d yet (never activated this session) — nothing to assign to
            mr.material = material; // instances a copy, same convention RabbitRoad itself uses
        }
    }

    void RefreshHutPlanetVisuals(Planet planet)
    {
        if (UpgradeManager.Instance == null || UpgradeManager.Instance.huts == null) return;

        foreach (GameObject hutGO in UpgradeManager.Instance.huts)
        {
            if (hutGO == null) continue;
            HutUpgradable hu = hutGO.GetComponent<HutUpgradable>();
            if (hu != null) hu.RefreshPlanetVisual(planet);
        }
    }

    // Activates only the incoming planet's own ground plane, deactivating
    // every other one. Each plane is a separate GameObject with its own
    // material AND its own independent baked lightmap — this is what keeps
    // Earth's baked decoration shadows (rocks/trees/flowers) from ever
    // bleeding onto the other planets' ground, since they were never part
    // of those planes' lighting bake in the first place.
    void ApplyGroundPlane(Planet planet)
    {
        SetGroundPlaneActive(earthGroundPlane, planet == Planet.Earth);
        SetGroundPlaneActive(moonGroundPlane, planet == Planet.Moon);
        SetGroundPlaneActive(venusGroundPlane, planet == Planet.Venus);
        SetGroundPlaneActive(marsGroundPlane, planet == Planet.Mars);
        SetGroundPlaneActive(plutoGroundPlane, planet == Planet.Pluto);
    }

    static void SetGroundPlaneActive(GameObject plane, bool active)
    {
        if (plane != null) plane.SetActive(active);
    }

    // Each planet's own decoration set is shown only while that planet is
    // active, hidden on every other planet.
    void RefreshEnvironmentDecoration(Planet planet)
    {
        SetObjectsActive(earthEnvironmentObjects, planet == Planet.Earth);
        SetObjectsActive(moonEnvironmentObjects, planet == Planet.Moon);
        SetObjectsActive(venusEnvironmentObjects, planet == Planet.Venus);
        SetObjectsActive(marsEnvironmentObjects, planet == Planet.Mars);
        SetObjectsActive(plutoEnvironmentObjects, planet == Planet.Pluto);
    }

    static void SetObjectsActive(GameObject[] objects, bool active)
    {
        if (objects == null) return;
        foreach (GameObject go in objects)
            if (go != null) go.SetActive(active);
    }

    // Swaps the Directional Light's color/intensity/rotation (and optional
    // ambient tint) to match this planet's own mood — these are all
    // REAL-TIME rendering settings, not baked data, so they're safe to
    // change at runtime freely without needing a re-bake. Only the STATIC
    // shadow/GI data (handled by ApplyGroundPlane's separate ground
    // objects) needs the multi-pass baking workflow.
    void ApplyLighting(Planet planet)
    {
        SetLightActive(earthLight, planet == Planet.Earth);
        SetLightActive(moonLight, planet == Planet.Moon);
        SetLightActive(venusLight, planet == Planet.Venus);
        SetLightActive(marsLight, planet == Planet.Mars);
        SetLightActive(plutoLight, planet == Planet.Pluto);

        // Fill light: on for every planet EXCEPT Pluto.
        SetLightActive(fillLight, planet != Planet.Pluto);

        Color ambient = planet switch
        {
            Planet.Earth => earthAmbientColor,
            Planet.Moon => moonAmbientColor,
            Planet.Venus => venusAmbientColor,
            Planet.Mars => marsAmbientColor,
            Planet.Pluto => plutoAmbientColor,
            _ => Color.white
        };
        RenderSettings.ambientLight = ambient;
    }

    static void SetLightActive(GameObject light, bool active)
    {
        if (light != null) light.SetActive(active);
    }

    // Called by PrestigeManager when a BigBang happens — wipes every
    // unlocked planet's saved data and locks them all again except Earth.
    // Called by QuestManager.ApplyOutcome — applies a quest buff/debuff to
    // a SPECIFIC planet (the quest's home planet), regardless of whether
    // the player is currently standing on it. If it IS the active planet,
    // we go through the live managers so the effect is felt immediately.
    // If it's a different, currently-inactive planet, we write straight
    // into that planet's saved PlanetData instead — it'll take effect the
    // next time the player switches there.
    public void ApplyQuestOutcome(Planet planet, QuestManager.BuffType type, float signedMagnitude)
    {
        bool isLive = planet == currentPlanet;
        PlanetData data = isLive ? null : GetOrCreatePlanetData(planet);

        switch (type)
        {
            case QuestManager.BuffType.HutCapacity:
                if (isLive) HutUpgradeManager.Instance?.ApplyQuestCapacityDelta(signedMagnitude);
                else data.questHutCapacityBonusPercent += signedMagnitude;
                break;

            case QuestManager.BuffType.TruckCapacity:
                if (isLive) UpgradeManager.Instance?.ApplyQuestTruckCapacityDelta(signedMagnitude);
                else data.questTruckCapacityBonusPercent += signedMagnitude;
                break;

            case QuestManager.BuffType.ReproductionRate:
                if (isLive) UpgradeManager.Instance?.ApplyQuestReproductionDelta(signedMagnitude);
                else data.questReproductionBonusPercent += signedMagnitude;
                break;

            case QuestManager.BuffType.HatchRate:
                if (isLive) UpgradeManager.Instance?.ApplyQuestHatchRateDelta(signedMagnitude);
                else data.questHatchRateBonusPercent += signedMagnitude;
                break;

            case QuestManager.BuffType.Coin:
                long coinDelta = (long)System.Math.Round(signedMagnitude);
                if (isLive)
                {
                    if (coinDelta < 0) GameManager.Instance?.ForceSpendCoins(-coinDelta);
                    else GameManager.Instance?.AddCoins(coinDelta);
                }
                else
                {
                    data.totalCoins = System.Math.Max(0, data.totalCoins + coinDelta);
                }
                break;

            // Carrot is intentionally NOT handled here — it's a GLOBAL
            // currency (per spec), applied directly by QuestManager itself
            // regardless of which planet the quest belongs to.
        }
    }

    // ── Save/Load API (used by SaveManager) ─────────────────────────────

    // Read-only view of every planet's data touched so far this session —
    // SaveManager iterates this to serialize every planet, not just the
    // currently active one.
    public Dictionary<Planet, PlanetData> GetAllPlanetData() => planetDataByPlanet;

    // Captures whatever's currently live (GameManager/UpgradeManager/huts/
    // quest bonuses/truck) into the ACTIVE planet's own PlanetData, without
    // actually switching away. Call this right before SaveManager writes
    // to disk, so the currently-active planet's in-progress numbers are
    // included correctly instead of only being captured on a real switch.
    public void SyncCurrentPlanetLiveState() => SaveCurrentPlanetState(currentPlanet);

    // Full restore from a save file: replaces every planet's data, re-syncs
    // which planets are unlocked (from the already-loaded Extra Farm shop
    // row), sets the active planet, and pushes that planet's data into the
    // live scene (GameManager/UpgradeManager/huts/quest bonuses/recolor).
    // Call this AFTER Epic upgrades and the Extra Farm level have already
    // been loaded into their ShopUpgradeRow components.
    public void RestoreAllPlanetData(Dictionary<Planet, PlanetData> restoredData, Planet activePlanet)
    {
        planetDataByPlanet.Clear();
        foreach (var kvp in restoredData)
            planetDataByPlanet[kvp.Key] = kvp.Value;

        lastSimTime.Clear();
        SyncFromSavedLevel(); // re-derive unlockedPlanets[] from the Extra Farm row's now-loaded level

        currentPlanet = activePlanet;
        LoadPlanetState(activePlanet); // pushes data into live GameManager/UpgradeManager/huts, recolors, recomputes
        AudioManager.Instance?.PlayMusicForPlanet(activePlanet);
        AchievementsManager.Instance?.OnPlanetChanged(activePlanet);

        RefreshPlanetButtonVisibility(instant: true);
        PlanetColumnUI columnUI = planetButton != null ? planetButton.GetComponent<PlanetColumnUI>() : null;
        columnUI?.RefreshButtonIcon();
    }

    // Catches up a SINGLE inactive planet's reproduction for the given
    // elapsed real-world duration (used for offline earnings on every
    // unlocked planet OTHER than the one that was active when the game
    // was closed — that one uses the more precise live-hut-based path in
    // SaveManager instead). Returns how many rabbits were actually earned,
    // for the offline earnings popup's combined total. Also resets this
    // planet's background-sim clock so the normal periodic simulator
    // doesn't try to re-catch-up the same elapsed time a second time.
    public long ApplyOfflineProduction(Planet planet, float elapsedSeconds)
    {
        PlanetData data = GetOrCreatePlanetData(planet);
        long before = data.totalRabbits;
        ApplyBackgroundProduction(data, elapsedSeconds);
        lastSimTime[planet] = Time.time;
        return data.totalRabbits - before;
    }

    // Same as above, but splits elapsedSeconds into two sequential
    // segments run at DIFFERENT rates — used when Explosive Reproduction
    // was active on this planet for only part of the offline window
    // (boostedSeconds at the 10x rate, the remainder at the normal rate).
    // Segments are applied in chronological order (boosted portion first)
    // so capacity limits are respected the same way they would be in
    // real time.
    public long ApplyOfflineProductionSplitRate(Planet planet, float boostedSeconds, float normalSeconds, float boostedRate, float normalRate)
    {
        PlanetData data = GetOrCreatePlanetData(planet);
        long before = data.totalRabbits;

        if (boostedSeconds > 0f && boostedRate > 0f)
            ApplyBackgroundProduction(data, boostedSeconds, boostedRate);
        if (normalSeconds > 0f && normalRate > 0f)
            ApplyBackgroundProduction(data, normalSeconds, normalRate);

        lastSimTime[planet] = Time.time;
        return data.totalRabbits - before;
    }

    // Convenience wrapper for the Explosive Reproduction offline-earnings
    // split specifically — computes both rates internally (base rate, and
    // base rate * boost multiplier) so callers don't need direct access
    // to a planet's PlanetData or its rate formula.
    public long ApplyOfflineProductionForExplosiveBoost(Planet planet, float boostedSeconds, float normalSeconds)
    {
        PlanetData data = GetOrCreatePlanetData(planet);
        float baseRate = ComputeBaseReproductionRate(data);
        float boostedRate = baseRate * ExplosiveReproductionBoost.Multiplier;
        return ApplyOfflineProductionSplitRate(planet, boostedSeconds, normalSeconds, boostedRate, baseRate);
    }

    // Real-world elapsed-time helper for the truck cooldown — Time.time
    // resets to 0 every app launch and can't be used for anything that
    // needs to persist correctly across a quit/relaunch.
    static double NowUnixSeconds()
    {
        return (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
    }

    // Sums totalRabbits across EVERY unlocked planet — the active planet's
    // live count (from GameManager, since that's the real-time authoritative
    // value for whichever planet is currently displayed) plus every other
    // unlocked planet's own saved PlanetData. Used by PrestigeManager so
    // the BigBang requirement considers the player's full empire, not just
    // whichever planet they happen to be standing on.
    public long GetCombinedRabbitsAcrossAllPlanets()
    {
        long sum = GameManager.Instance != null ? GameManager.Instance.totalRabbits : 0;

        foreach (Planet planet in System.Enum.GetValues(typeof(Planet)))
        {
            if (planet == currentPlanet) continue; // already counted via GameManager above
            if (!IsUnlocked(planet)) continue;
            if (planetDataByPlanet.TryGetValue(planet, out PlanetData data))
                sum += data.totalRabbits;
        }

        return sum;
    }

    // Highest CURRENT (not lifetime) rabbit count across any single
    // unlocked planet — same live-vs-saved-data split as
    // GetCombinedRabbitsAcrossAllPlanets() above, but taking the max
    // instead of the sum. Used for the "reach N rabbits on a single farm"
    // cosmetic achievements, so they update in real time regardless of
    // which planet the player is currently standing on.
    public long GetHighestSingleFarmRabbits()
    {
        long highest = GameManager.Instance != null ? GameManager.Instance.totalRabbits : 0;

        foreach (Planet planet in System.Enum.GetValues(typeof(Planet)))
        {
            if (planet == currentPlanet) continue; // already counted via GameManager above
            if (!IsUnlocked(planet)) continue;
            if (planetDataByPlanet.TryGetValue(planet, out PlanetData data))
                highest = System.Math.Max(highest, data.totalRabbits);
        }

        return highest;
    }

    public void ResetAllPlanets()
    {
        for (int i = 1; i < unlockedPlanets.Length; i++)
            unlockedPlanets[i] = false;

        planetDataByPlanet.Clear();
        lastSimTime.Clear();

        // Destroy any foxes that were frozen on a now-being-reset planet —
        // BigBang wipes everything, these shouldn't silently reappear if
        // that planet gets unlocked again later.
        foreach (List<GameObject> frozen in frozenFoxesByPlanet.Values)
            foreach (GameObject go in frozen)
                if (go != null) Destroy(go);
        frozenFoxesByPlanet.Clear();

        currentPlanet = Planet.Earth;

        AudioManager.Instance?.PlayMusicForPlanet(Planet.Earth);
        RefreshHutPlanetVisuals(Planet.Earth);
        StartCoroutine(ApplyRoadMaterialDeferred(Planet.Earth));
        ApplyTruckAndGarage(Planet.Earth);
        ApplyGroundPlane(Planet.Earth);
        RefreshEnvironmentDecoration(Planet.Earth);
        ApplyLighting(Planet.Earth);
        RefreshPlanetButtonVisibility(instant: true);

        PlanetColumnUI columnUI = planetButton != null ? planetButton.GetComponent<PlanetColumnUI>() : null;
        if (columnUI != null)
        {
            columnUI.RefreshButtonIcon();
            columnUI.CloseColumn();
        }
    }
}
