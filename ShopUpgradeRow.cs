using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using System.Collections;

public class ShopUpgradeRow : MonoBehaviour
{
    [Header("Category")]
    public UpgradeManager.UpgradeCategory category;

    [Header("Epic")]
    [Tooltip("Epic rows are NOT reset on prestige.")]
    public bool isEpic = false;
    [Tooltip("Check this ONLY on the Tier 1 Truck Capacity row — reports tutorial step completion when purchased. Leave unchecked on every other row.")]
    public bool isTutorialBuyTarget = false;

    [Header("Level Settings")]
    public int maxLevel = 5;
    public float incrementPerLevel = 1f;

    [Header("Placeholder Pricing")]
    public long basePrice = 100;
    public float priceGrowth = 1.15f;

    [Header("UI")]
    public TextMeshProUGUI costText;
    public TextMeshProUGUI levelText;
    public Button buyButton;
    public Image buyButtonImage;
    public VerticalGaugeFill levelGauge;
    public Color affordableColor    = new Color(0.2f, 0.8f, 0.2f);
    public Color notAffordableColor = new Color(0.5f, 0.5f, 0.5f);

    [Header("Hold To Buy")]
    public float holdDelay    = 0.25f;
    public float holdInterval = 0.075f;

    [Header("On Level Purchased (optional hook, e.g. Extra Farm -> PlanetManager)")]
    public UnityEvent<int> onLevelPurchased;

    public int currentLevel = 0;

    private Coroutine holdCoroutine;

    void Start()
    {
        RefreshUI();
        if (levelGauge != null)
            levelGauge.SetFillInstant((float)currentLevel / maxLevel);
    }

    void Update()
    {
        bool affordable = !IsMaxed() && CanAfford();
        if (buyButton    != null) buyButton.interactable    = affordable;
        if (buyButtonImage != null) buyButtonImage.color    = affordable ? affordableColor : notAffordableColor;
    }

    public bool IsMaxed()
    {
        if (currentLevel >= maxLevel) return true;

        // Extra Farm can only be bought from the SPECIFIC planet matching
        // its next tier's prerequisite — level 0->1 (unlocks Moon) only
        // buyable from Earth, level 1->2 (unlocks Venus) only from Moon,
        // and so on. currentLevel's ordinal already matches the required
        // planet's ordinal (Earth=0, Moon=1, Venus=2, Mars=3), so this is
        // a direct comparison. Shows "MAX" (via the same display logic
        // below) whenever standing on the WRONG planet, not just when
        // truly fully maxed — prevents buying every planet's unlock from
        // Earth alone.
        if (category == UpgradeManager.UpgradeCategory.ExtraFarm && PlanetManager.Instance != null)
            return (int)PlanetManager.Instance.currentPlanet != currentLevel;

        return false;
    }
    public float Contribution => currentLevel * incrementPerLevel;

    long PriceForNextLevel()
    {
        double price = (double)basePrice * System.Math.Pow(priceGrowth, currentLevel);

        // Per-planet price scaling: each planet further from Earth costs
        // 50% more, compounding (Earth x1, Moon x1.5, Venus x2.25, Mars
        // x3.375, Pluto x5.0625). Only the STARTING price scales this way
        // — priceGrowth (the per-level multiplier) stays identical on
        // every planet, so relative level-to-level scaling is unaffected.
        // Extra Farm is deliberately excluded — it's a single GLOBAL
        // unlock progression, not a per-planet upgrade, so its price
        // must be identical no matter which planet's shop you're viewing.
        if (!isEpic && category != UpgradeManager.UpgradeCategory.ExtraFarm && PlanetManager.Instance != null)
        {
            int planetIndex = (int)PlanetManager.Instance.currentPlanet;
            price *= System.Math.Pow(1.5, planetIndex);
        }

        // Epic upgrade: Greedy Bargainer — -1% Common upgrade cost per level.
        // Only applies to Common rows, never to Epic rows themselves.
        if (!isEpic && EpicUpgradeManager.Instance != null)
        {
            int bargainerLevel = EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.GreedyBargainer);
            price *= System.Math.Max(0.01, 1.0 - bargainerLevel * 0.05);
        }

        // Clamp against long's own ceiling as a last resort (an upgrade
        // priced beyond ~9.2 quintillion isn't meaningfully different from
        // "as expensive as long allows" — this only prevents a NaN/Infinity
        // crash on truly absurd level/growth combinations, not normal play).
        if (double.IsNaN(price) || price >= long.MaxValue) return long.MaxValue;
        if (price <= 0) return 0;

        return (long)System.Math.Round(price);
    }

    bool CanAfford()
    {
        if (IsMaxed()) return false;
        return GameManager.Instance != null && GameManager.Instance.totalCoins >= PriceForNextLevel();
    }

    public void StartHolding()
    {
        StopHolding();
        holdCoroutine = StartCoroutine(HoldRoutine());
    }

    public void StopHolding()
    {
        if (holdCoroutine != null)
        {
            StopCoroutine(holdCoroutine);
            holdCoroutine = null;
        }
    }

    IEnumerator HoldRoutine()
    {
        TryBuy();
        yield return new WaitForSeconds(holdDelay);
        while (true)
        {
            TryBuy();
            yield return new WaitForSeconds(holdInterval);
        }
    }

    public void TryBuy()
    {
        if (IsMaxed()) return;
        long price = PriceForNextLevel();
        if (GameManager.Instance == null || !GameManager.Instance.SpendCoins(price)) return;

        currentLevel++;
        onLevelPurchased?.Invoke(currentLevel);
        AudioManager.Instance?.PlayCommonUpgradeBought();

        // #17 Color Fade Nickname / #18 Rainbow Nickname
        if (isEpic)
            AchievementsManager.Instance?.ReportEpicUpgradePurchased();
        else
            AchievementsManager.Instance?.ReportCommonUpgradePurchased();

        if (isTutorialBuyTarget && TutorialManager.Instance != null)
            TutorialManager.Instance.ReportTruckUpgradeBought();

        RefreshUI();

        if (levelGauge != null)
            levelGauge.SetFill((float)currentLevel / maxLevel);

        if (UpgradeManager.Instance != null)
            UpgradeManager.Instance.OnCategoryChanged(category);
    }

    void RefreshUI()
    {
        if (levelText != null) levelText.text = currentLevel + " / " + maxLevel;
        if (costText  != null) costText.text  = IsMaxed() ? "MAX" : GameManager.FormatNumber(PriceForNextLevel());
    }

    // Called by PrestigeManager after resetting currentLevel.
    public void RefreshUIPublic()
    {
        RefreshUI();
        if (levelGauge != null)
            levelGauge.SetFillInstant((float)currentLevel / maxLevel);
    }
}
