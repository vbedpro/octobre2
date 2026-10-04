using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;

public class UpgradeManager : MonoBehaviour
{
    public static UpgradeManager Instance;

    public enum UpgradeCategory { HutCapacity, TruckCapacity, TruckFrequency, Reproduction, RabbitValue, HatchSpeed, ExtraFarm }

    [Header("New Hut (UNTOUCHED logic)")]
    public int hutCount = 1;
    public int maxHuts = 10;

    [Header("Huts")]
    public GameObject[] huts;

    [Header("Roads")]
    public GameObject[] rabbitRoads;

    [Header("New Hut UI")]
    public VerticalGaugeFill hutGauge;
    public TextMeshProUGUI hutCostText;
    public Image hutBuyImage;

    public Color affordableColor = new Color(0.2f, 0.8f, 0.2f);
    public Color notAffordableColor = new Color(0.5f, 0.5f, 0.5f);

    [Header("Hold To Buy Settings (New Hut)")]
    public float holdDelay = 0.25f;
    public float holdInterval = 0.075f;

    long baseTruckCapacity = 10;
    float baseTruckCooldown = 30f;
    long baseCoinsPerRabbit = 20;
    float baseHatchRate = 1f; // rabbits/sec — captured from BarnSpawner's real Inspector spawnInterval

    // Persistent bonuses applied by quest buffs/debuffs, on top of shop + prestige.
    private float questTruckCapacityBonusPercent = 0f;
    private float questReproductionBonusPercent = 0f;
    private float questHatchRateBonus = 0f; // flat, same units as GetCategoryBonus(HatchSpeed)

    int[] hutPrices = { 500, 1000, 2500, 5000, 10000, 25000, 200000, 1000000, 10000000 };

    private Coroutine holdCoroutine;

    // ── Row registry — every ShopUpgradeRow registers itself here ──────
    private readonly Dictionary<UpgradeCategory, List<ShopUpgradeRow>> rowsByCategory = new Dictionary<UpgradeCategory, List<ShopUpgradeRow>>();

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        // Register every ShopUpgradeRow in the scene, including those on inactive GameObjects.
        // This is the authoritative registration — rows no longer need to self-register.
        rowsByCategory.Clear();
        ShopUpgradeRow[] allRows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in allRows)
            RegisterRow(row);
        UpdateHutGauge();
        UpdateHutCost();

        // Capture the real starting values from SellManager/GameManager instead
        // of using hardcoded defaults — respects whatever's set in the Inspector.
        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null)
        {
            baseCoinsPerRabbit = sm.coinsPerRabbit;
            baseTruckCapacity  = sm.maxSell;
        }
        if (GameManager.Instance != null)
            baseTruckCooldown = GameManager.Instance.truckCooldown;

        BarnSpawner spawnerStart = FindAnyObjectByType<BarnSpawner>();
        if (spawnerStart != null && spawnerStart.spawnInterval > 0f)
            baseHatchRate = 1f / spawnerStart.spawnInterval;
    }

    void Update()
    {
        UpdateHutButtonColor();
    }

    // ── Row registration / aggregation ──────────────────────────────────

    public bool IsRegistered(ShopUpgradeRow row)
    {
        if (!rowsByCategory.TryGetValue(row.category, out List<ShopUpgradeRow> list)) return false;
        return list.Contains(row);
    }

    public void RegisterRow(ShopUpgradeRow row)
    {
        if (!rowsByCategory.TryGetValue(row.category, out List<ShopUpgradeRow> list))
        {
            list = new List<ShopUpgradeRow>();
            rowsByCategory[row.category] = list;
        }
        if (!list.Contains(row))
            list.Add(row);
    }

    // Total bonus for a category = sum of (level * incrementPerLevel) across every row registered under it.
    public float GetCategoryBonus(UpgradeCategory category)
    {
        if (!rowsByCategory.TryGetValue(category, out List<ShopUpgradeRow> list)) return 0f;
        float total = 0f;
        foreach (ShopUpgradeRow row in list)
            total += row.Contribution;
        return total;
    }

    // Called by a ShopUpgradeRow right after it successfully buys a level.
    public void OnCategoryChanged(UpgradeCategory category)
    {
        switch (category)
        {
            case UpgradeCategory.HutCapacity:
                if (HutUpgradeManager.Instance != null)
                    HutUpgradeManager.Instance.RecalculateAllCapacities();
                break;

            case UpgradeCategory.TruckCapacity:
                SellManager sellManagerTC = FindAnyObjectByType<SellManager>();
                if (sellManagerTC != null)
                    sellManagerTC.maxSell = ComputeTruckCapacity();
                break;

            case UpgradeCategory.TruckFrequency:
                if (GameManager.Instance != null)
                    GameManager.Instance.truckCooldown = Mathf.Max(5f, baseTruckCooldown - GetCategoryBonus(category));
                break;

            case UpgradeCategory.Reproduction:
                ApplyReproduction();
                break;

            case UpgradeCategory.RabbitValue:
                SellManager sellManagerRV = FindAnyObjectByType<SellManager>();
                if (sellManagerRV != null)
                    sellManagerRV.coinsPerRabbit = Mathf.RoundToInt(baseCoinsPerRabbit * (1f + GetCategoryBonus(category) / 100f)) + PremiumFurBonus();
                break;

            case UpgradeCategory.HatchSpeed:
                BarnSpawner spawner = FindAnyObjectByType<BarnSpawner>();
                if (spawner != null)
                    spawner.spawnInterval = 1f / (baseHatchRate + GetCategoryBonus(category) + questHatchRateBonus + FasterHandsBonus());
                break;
        }
    }

    // ── New Hut — UNCHANGED behaviour, just renamed a few private helpers ──

    public void StartHolding(string upgradeKey)
    {
        StopHolding();
        holdCoroutine = StartCoroutine(HoldRoutine(upgradeKey));
    }

    public void StopHolding()
    {
        if (holdCoroutine != null)
        {
            StopCoroutine(holdCoroutine);
            holdCoroutine = null;
        }
    }

    IEnumerator HoldRoutine(string upgradeKey)
    {
        ExecuteBuy(upgradeKey);
        yield return new WaitForSeconds(holdDelay);
        while (true)
        {
            ExecuteBuy(upgradeKey);
            yield return new WaitForSeconds(holdInterval);
        }
    }

    void ExecuteBuy(string upgradeKey)
    {
        if (upgradeKey == "hut") TryBuyHut();
    }

    void UpdateHutButtonColor()
    {
        long coins = GameManager.Instance.totalCoins;
        bool affordable = hutCount < maxHuts && coins >= hutPrices[Mathf.Min(hutCount - 1, hutPrices.Length - 1)];
        if (hutBuyImage != null)
            hutBuyImage.color = affordable ? affordableColor : notAffordableColor;
    }

    public void TryBuyHut()
    {
        if (hutCount >= maxHuts) return;
        int price = hutPrices[hutCount - 1];
        if (!GameManager.Instance.SpendCoins(price)) return;

        if (hutCount < huts.Length && huts[hutCount] != null)
        {
            huts[hutCount].SetActive(true);

            // IMPORTANT: a freshly-activated hut has never gone through
            // HutUpgradeManager's level/capacity setup (that only happens
            // in its Start() loop for huts already active at that time,
            // or when a hut is manually upgraded/loaded from a save).
            // Without this, the new hut's maxCapacity is left at whatever
            // raw default sits on its RabbitHut component — showing a
            // broken/stuck 0% occupancy and effectively uncapped
            // population until the player upgrades it once (which is the
            // first time ApplyLevelTo() ever actually runs for it).
            // Explicitly initialize it to level 1 right now instead.
            HutUpgradable newHutUpgradable = huts[hutCount].GetComponent<HutUpgradable>();
            if (newHutUpgradable != null && HutUpgradeManager.Instance != null)
                HutUpgradeManager.Instance.ApplyLoadedHutLevel(newHutUpgradable, 1);
        }

        if (hutCount < rabbitRoads.Length && rabbitRoads[hutCount] != null)
        {
            rabbitRoads[hutCount].SetActive(true);
            // RabbitRoad.Start() only just ran for the first time (it was
            // inactive until this line), which assigns its own default
            // material — apply this planet's road material now that it
            // actually has a MeshRenderer to assign to.
            if (PlanetManager.Instance != null)
                PlanetManager.Instance.RefreshRoadMaterialForCurrentPlanet();
        }

        hutCount++;
        AchievementsManager.Instance?.ReportHutBuilt();

        if (QuestManager.Instance != null)
            QuestManager.Instance.ReportHutBought();

        ApplyReproduction();
        UpdateHutGauge(animate: true);
        UpdateHutCost();
    }

    // Base capacity = baseTruckCapacity + flat BigBang buff (unchanged, additive).
    // Shop levels then apply as a COMPOUNDING % on top of that base, same
    // pattern as reproduction: multiplier *= (1 + level% / 100) per row.
    long ComputeTruckCapacity()
    {
        long prestigeBuff = PrestigeManager.Instance != null
            ? (long)PrestigeManager.Instance.TotalTruckCapacityBuff
            : 0;
        long baseCapacity = baseTruckCapacity + prestigeBuff;

        double multiplier = 1.0;
        if (rowsByCategory.TryGetValue(UpgradeCategory.TruckCapacity, out var truckRows))
        {
            foreach (ShopUpgradeRow row in truckRows)
                multiplier *= System.Math.Pow(1.0 + row.incrementPerLevel / 100.0, row.currentLevel);
        }
        multiplier *= (1.0 + questTruckCapacityBonusPercent / 100.0);

        double result = baseCapacity * multiplier;
        if (double.IsNaN(result) || result >= long.MaxValue) return long.MaxValue;
        if (result < 0) return baseCapacity; // safety fallback, should not happen

        return (long)result;
    }

    // ── Quest buff/debuff hooks (called by QuestManager) ────────────────

    public bool WouldQuestTruckCapacityGoBelowBase(float deltaPercent)
    {
        double multiplier = 1.0;
        if (rowsByCategory.TryGetValue(UpgradeCategory.TruckCapacity, out var truckRows))
            foreach (ShopUpgradeRow row in truckRows)
                multiplier *= System.Math.Pow(1.0 + row.incrementPerLevel / 100.0, row.currentLevel);

        double predicted = multiplier * (1.0 + (questTruckCapacityBonusPercent + deltaPercent) / 100.0);
        return predicted < 1.0;
    }

    public void ApplyQuestTruckCapacityDelta(float deltaPercent)
    {
        double shopMultiplier = 1.0;
        if (rowsByCategory.TryGetValue(UpgradeCategory.TruckCapacity, out var truckRows))
            foreach (ShopUpgradeRow row in truckRows)
                shopMultiplier *= System.Math.Pow(1.0 + row.incrementPerLevel / 100.0, row.currentLevel);

        float newQuestPercent = questTruckCapacityBonusPercent + deltaPercent;
        // Hard floor: never let shopMultiplier * (1 + quest%/100) drop below 1.
        float minQuestPercent = (float)((1.0 / shopMultiplier - 1.0) * 100.0);
        if (newQuestPercent < minQuestPercent) newQuestPercent = minQuestPercent;

        questTruckCapacityBonusPercent = newQuestPercent;
        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null) sm.maxSell = ComputeTruckCapacity();
    }

    public bool WouldQuestReproductionGoBelowBase(float deltaPercent)
    {
        float multiplier = 1f;
        if (rowsByCategory.TryGetValue(UpgradeCategory.Reproduction, out var reproRows))
            foreach (ShopUpgradeRow row in reproRows)
                multiplier *= (1f + row.Contribution / 100f);

        float predicted = multiplier * (1f + (questReproductionBonusPercent + deltaPercent) / 100f);
        return predicted < 1f;
    }

    public void ApplyQuestReproductionDelta(float deltaPercent)
    {
        float shopMultiplier = 1f;
        if (rowsByCategory.TryGetValue(UpgradeCategory.Reproduction, out var reproRows))
            foreach (ShopUpgradeRow row in reproRows)
                shopMultiplier *= (1f + row.Contribution / 100f);

        float newQuestPercent = questReproductionBonusPercent + deltaPercent;
        // Hard floor: never let shopMultiplier * (1 + quest%/100) drop below 1.
        float minQuestPercent = (1f / shopMultiplier - 1f) * 100f;
        if (newQuestPercent < minQuestPercent) newQuestPercent = minQuestPercent;

        questReproductionBonusPercent = newQuestPercent;
        ApplyReproduction();
    }

    public bool WouldQuestHatchRateGoBelowBase(float deltaAmount)
    {
        // Base rate contributes 1; total bonus (shop + quest) must stay >= 0.
        float shopBonus = GetCategoryBonus(UpgradeCategory.HatchSpeed);
        float predicted = shopBonus + questHatchRateBonus + deltaAmount;
        return predicted < 0f;
    }

    public void ApplyQuestHatchRateDelta(float deltaAmount)
    {
        float shopBonus = GetCategoryBonus(UpgradeCategory.HatchSpeed);
        float newBonus = questHatchRateBonus + deltaAmount;
        // Hard floor: never let (shop + quest) bonus drop below 0.
        if (shopBonus + newBonus < 0f) newBonus = -shopBonus;

        questHatchRateBonus = newBonus;
        BarnSpawner spawner = FindAnyObjectByType<BarnSpawner>();
        if (spawner != null)
            spawner.spawnInterval = 1f / (baseHatchRate + shopBonus + questHatchRateBonus + FasterHandsBonus());
    }

    // Epic upgrade: Faster Hands — +1 rabbit/sec base hatch speed per level. Permanent.
    float FasterHandsBonus()
    {
        return EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.FasterHands) * 1f
            : 0f;
    }

    // Epic upgrade: Premium Fur — +1 coin per rabbit sold per level. Permanent.
    int PremiumFurBonus()
    {
        return EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.PremiumFur)
            : 0;
    }

    void ApplyReproduction()
    {
        float bonusPercent = GetCategoryBonus(UpgradeCategory.Reproduction);
        float rabbitsPerSecond = ComputeBaseReproductionRate();

        // Explosive Reproduction boost — 10x, locked to whichever planet
        // it was activated on. Since only the CURRENT planet's huts are
        // active in the scene at any time, multiplying here naturally
        // scopes the boost correctly: it only actually changes anything
        // when the boosted planet happens to be the one currently active.
        float explosiveMultiplier = PlanetManager.Instance != null
            ? ExplosiveReproductionBoost.GetMultiplierFor(PlanetManager.Instance.currentPlanet)
            : 1f;
        rabbitsPerSecond *= explosiveMultiplier;

        if (GameManager.Instance != null)
            GameManager.Instance.rabbitsPerSecond = 0;

        float permBuffForUnlockCheck = PrestigeManager.Instance != null
            ? PrestigeManager.Instance.ReproductionGameplayMultiplier()
            : 1f;

        RabbitHut[] allHuts = FindObjectsByType<RabbitHut>(FindObjectsInactive.Exclude);
        foreach (RabbitHut hut in allHuts)
        {
            hut.rabbitsPerTick = rabbitsPerSecond;
            if (bonusPercent > 0f || permBuffForUnlockCheck > 1f)
                hut.UnlockReproduction();
        }
    }

    // Reproduction rate from shop upgrades + prestige + quest buffs only —
    // deliberately EXCLUDES the Explosive Reproduction boost multiplier,
    // so offline-earnings math can compute "what would the rate have been
    // without the boost" for the portion of an offline window that falls
    // after the boost expired.
    public float ComputeBaseReproductionRate()
    {
        float multiplier = 1f;
        if (rowsByCategory.TryGetValue(UpgradeCategory.Reproduction, out var reproRows))
        {
            foreach (ShopUpgradeRow row in reproRows)
                multiplier *= (1f + row.Contribution / 100f);
        }

        float permBuff = PrestigeManager.Instance != null
            ? PrestigeManager.Instance.ReproductionGameplayMultiplier()
            : 1f;

        return 1f * multiplier * permBuff * (1f + questReproductionBonusPercent / 100f);
    }

    // Public wrapper so boost activation/expiry (which live outside this
    // class) can force an immediate recompute instead of waiting for the
    // next natural trigger (a purchase, a quest resolving, etc.).
    public void ApplyReproductionPublic() => ApplyReproduction();

    // Called by QuestManager when a BigBang happens — wipes any accumulated
    // quest buff/debuff on truck capacity, reproduction, and hatch rate
    // (won/lost quests don't survive prestige).
    public void ResetQuestBonuses()
    {
        questTruckCapacityBonusPercent = 0f;
        questReproductionBonusPercent = 0f;
        questHatchRateBonus = 0f;

        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null) sm.maxSell = ComputeTruckCapacity();

        ApplyReproduction();

        BarnSpawner spawner = FindAnyObjectByType<BarnSpawner>();
        if (spawner != null)
            spawner.spawnInterval = 1f / (baseHatchRate + GetCategoryBonus(UpgradeCategory.HatchSpeed) + questHatchRateBonus + FasterHandsBonus());
    }

    // Used by PlanetManager to save/restore this planet's own accumulated
    // quest buffs/debuffs when switching planets — NOT clamped against the
    // base-value floor like the ApplyQuest…Delta methods, since these just
    // restore an already-valid saved number, not apply a fresh quest.
    public float GetQuestTruckCapacityBonusPercent() => questTruckCapacityBonusPercent;
    public float GetQuestReproductionBonusPercent() => questReproductionBonusPercent;
    public float GetQuestHatchRateBonus() => questHatchRateBonus;

    public void SetQuestTruckCapacityBonusPercent(float value)
    {
        questTruckCapacityBonusPercent = value;
        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null) sm.maxSell = ComputeTruckCapacity();
    }

    public void SetQuestReproductionBonusPercent(float value)
    {
        questReproductionBonusPercent = value;
        ApplyReproduction();
    }

    public void SetQuestHatchRateBonus(float value)
    {
        questHatchRateBonus = value;
        BarnSpawner spawner = FindAnyObjectByType<BarnSpawner>();
        if (spawner != null)
            spawner.spawnInterval = 1f / (baseHatchRate + GetCategoryBonus(UpgradeCategory.HatchSpeed) + questHatchRateBonus + FasterHandsBonus());
    }

    // Called by PrestigeManager to reset huts back to just the first one.
    public void ResetHuts()
    {
        hutCount = 1;
        for (int i = 1; i < huts.Length; i++)
            if (huts[i] != null) huts[i].SetActive(false);
        for (int i = 1; i < rabbitRoads.Length; i++)
            if (rabbitRoads[i] != null) rabbitRoads[i].SetActive(false);
        UpdateHutGauge();
        UpdateHutCost();
    }

    // Re-applies all upgrade effects after a prestige reset,
    // with permanent buffs from PrestigeManager baked in.
    public void ApplyAllUpgradesAfterPrestige()
    {
        // Truck capacity with permanent buff.
        SellManager sm = FindAnyObjectByType<SellManager>();
        if (sm != null)
            sm.maxSell = ComputeTruckCapacity();

        // Truck frequency — unchanged by prestige.
        if (GameManager.Instance != null)
            GameManager.Instance.truckCooldown = baseTruckCooldown;

        // Rabbit value — back to base (shop resets, Premium Fur is permanent).
        if (sm != null)
            sm.coinsPerRabbit = baseCoinsPerRabbit + PremiumFurBonus();

        // Hatch speed — back to base (shop/quest reset to 0, but Faster Hands is permanent).
        BarnSpawner spawner = FindAnyObjectByType<BarnSpawner>();
        if (spawner != null)
            spawner.spawnInterval = 1f / (baseHatchRate + FasterHandsBonus());

        // Reproduction with permanent buff.
        ApplyReproduction();

        // Hut capacities with permanent buff.
        if (HutUpgradeManager.Instance != null)
            HutUpgradeManager.Instance.RecalculateAllCapacities();
    }

    // Called by SaveManager after every ShopUpgradeRow/EpicUpgradeRow level
    // has been restored from disk, and after hut count has been restored.
    // Activates the correct set of hut/road GameObjects for a loaded save.
    public void ApplyLoadedHutCount(int count)
    {
        count = Mathf.Clamp(count, 1, maxHuts);
        hutCount = count;

        for (int i = 1; i < huts.Length; i++)
            if (huts[i] != null) huts[i].SetActive(i < hutCount);

        for (int i = 1; i < rabbitRoads.Length; i++)
            if (rabbitRoads[i] != null) rabbitRoads[i].SetActive(i < hutCount);

        UpdateHutGauge();
        UpdateHutCost();
    }

    void UpdateHutGauge(bool animate = false)
    {
        if (hutGauge == null) return;
        float frac = (float)(hutCount - 1) / (maxHuts - 1);
        if (animate) hutGauge.SetFill(frac);
        else hutGauge.SetFillInstant(frac);
    }

    void UpdateHutCost()
    {
        if (hutCostText == null) return;
        bool maxed = hutCount >= maxHuts;
        hutCostText.text = maxed ? "MAX" : GameManager.FormatNumber(hutPrices[hutCount - 1]);
    }
}
