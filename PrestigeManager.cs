using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class PrestigeManager : MonoBehaviour
{
    public static PrestigeManager Instance;

    [Header("Prestige State")]
    public int prestigeCount = 0;
    public long firstUnlockThreshold = 10000000;

    public long NextUnlockThreshold()
    {
        long threshold = firstUnlockThreshold;
        for (int i = 0; i < prestigeCount; i++)
            threshold = (long)(threshold * 1.1f);
        return threshold;
    }

    [Header("Permanent Buffs Per Prestige")]
    public float reproductionBuffPerPrestige = 5f;
    public float hutCapacityBuffPerPrestige   = 5f;
    public float truckCapacityBuffPerPrestige = 10f;

    public float BloodlineBoostBonusPercent()
    {
        return EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.BloodlineBoost) * 1f
            : 0f;
    }

    public float DomainExpansionBonusPercent()
    {
        return EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.DomainExpansion) * 10f
            : 0f;
    }

    public float EffectiveReproductionBuffPerPrestige()
    {
        return reproductionBuffPerPrestige + BloodlineBoostBonusPercent();
    }

    public float EffectiveHutCapacityBuffPerPrestige()
    {
        return hutCapacityBuffPerPrestige + DomainExpansionBonusPercent();
    }

    public float EffectiveTruckCapacityBuffPerPrestige()
    {
        float epicBonus = EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.ExtraWheels) * 3f
            : 0f;
        return truckCapacityBuffPerPrestige + epicBonus;
    }

    public float TotalReproductionBuff  => prestigeCount * EffectiveReproductionBuffPerPrestige();
    public float TotalHutCapacityBuff   => prestigeCount * EffectiveHutCapacityBuffPerPrestige();
    public float TotalTruckCapacityBuff => prestigeCount * EffectiveTruckCapacityBuffPerPrestige();

    public float ReproductionGameplayMultiplier()
    {
        if (prestigeCount <= 0) return 1f;
        float linear = (reproductionBuffPerPrestige / 100f) * prestigeCount;
        float linearEpicBonus = (BloodlineBoostBonusPercent() / 100f) * prestigeCount;
        return 1f + linear + linearEpicBonus;
    }

    public float HutCapacityGameplayBonusFraction()
    {
        if (prestigeCount <= 0) return 0f;
        float linear = (hutCapacityBuffPerPrestige / 100f) * prestigeCount;
        float linearEpicBonus = (DomainExpansionBonusPercent() / 100f) * prestigeCount;
        return linear + linearEpicBonus;
    }

    [Header("Cost")]
    public long carrotCost = 20;

    public long NextCarrotCost()
    {
        return carrotCost + (prestigeCount * 3);
    }

    [Header("Popup Slide")]
    public RectTransform popupReadyRect;
    public RectTransform popupNotReadyRect;
    public float slideDuration = 0.4f;
    public float openX   =    0f;
    public float closedX = -1400f;

    [Header("Popup - Rabbit Requirement Text")]
    public TextMeshProUGUI rabbitRequirementText;

    [Header("Popup - Carrot Cost Text")]
    public TextMeshProUGUI carrotCostText;
    public TextMeshProUGUI carrotCostTextNotReady;

    [Header("Popup Ready - Current Buff Texts")]
    public TextMeshProUGUI reproductionBuffText;
    public TextMeshProUGUI hutCapacityBuffText;
    public TextMeshProUGUI truckCapacityBuffText;

    [Header("Popup NotReady - Current Buff Texts")]
    public TextMeshProUGUI reproductionBuffTextNotReady;
    public TextMeshProUGUI hutCapacityBuffTextNotReady;
    public TextMeshProUGUI truckCapacityBuffTextNotReady;

    [Header("Popup - Buttons")]
    public Button confirmButton;
    public Image confirmButtonImage;
    public Color affordableColor   = new Color(0.2f, 0.8f, 0.2f);
    public Color unaffordableColor = new Color(0.8f, 0.2f, 0.2f);

    [Header("Dim Overlay")]
    public CanvasGroup dimOverlay;
    public float dimAlpha = 0.6f;

    [Header("BigBang Transition")]
    public CanvasGroup transitionPanel;
    public TextMeshProUGUI transitionCounterText;
    [Tooltip("How far (in UI pixels) the old/new counter numbers slide during the roll-up transition.")]
    public float counterRollOffset = 40f;
    public float fadeInDuration  = 0.5f;
    public float holdDuration    = 1.2f;
    public float fadeOutDuration = 0.8f;

    [Header("BigBang Button")]
    public Button bigBangButton;
    public Image bigBangButtonImage;

    [Header("TopPanel")]
    public TextMeshProUGUI prestigeCountText;

    [Header("Nav Button Fades (hidden while BigBang popup — ready or not-ready — is open)")]
    public CanvasGroup leaderboardButtonCanvasGroup;
    public CanvasGroup achievementButtonCanvasGroup;
    public CanvasGroup inventoryButtonCanvasGroup;
    public CanvasGroup bigBangButtonCanvasGroupFade;
    public CanvasGroup settingsButtonCanvasGroup;
    public float navButtonFadeDuration = 0.25f;
    private Coroutine navLeaderboardFadeCo;
    private Coroutine navAchievementFadeCo;
    private Coroutine navInventoryFadeCo;
    private Coroutine navBigBangFadeCo;
    private Coroutine navSettingsFadeCo;

    public static bool popupOpen = false;
    private bool popupIsOpen = false;
    private bool showingReady = false;
    [HideInInspector] public bool isTransitioning = false;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        if (popupReadyRect    != null) { popupReadyRect.anchoredPosition    = new Vector2(closedX, popupReadyRect.anchoredPosition.y);    popupReadyRect.gameObject.SetActive(false); }
        if (popupNotReadyRect != null) { popupNotReadyRect.anchoredPosition = new Vector2(closedX, popupNotReadyRect.anchoredPosition.y); popupNotReadyRect.gameObject.SetActive(false); }
        UpdateAllUI();
    }

    void Update()
    {
        UpdateBigBangButton();

        if (popupIsOpen)
        {
            bool shouldBeReady = HasEnoughRabbits();

            if (shouldBeReady && !showingReady)
            {
                showingReady = true;
                if (popupNotReadyRect != null)
                {
                    popupNotReadyRect.anchoredPosition = new Vector2(closedX, popupNotReadyRect.anchoredPosition.y);
                    popupNotReadyRect.gameObject.SetActive(false);
                }
                if (popupReadyRect != null)
                {
                    popupReadyRect.gameObject.SetActive(true);
                    popupReadyRect.anchoredPosition = new Vector2(openX, popupReadyRect.anchoredPosition.y);
                }
            }
            else if (!shouldBeReady && showingReady)
            {
                showingReady = false;
                if (popupReadyRect != null)
                {
                    popupReadyRect.anchoredPosition = new Vector2(closedX, popupReadyRect.anchoredPosition.y);
                    popupReadyRect.gameObject.SetActive(false);
                }
                if (popupNotReadyRect != null)
                {
                    popupNotReadyRect.gameObject.SetActive(true);
                    popupNotReadyRect.anchoredPosition = new Vector2(openX, popupNotReadyRect.anchoredPosition.y);
                }
            }

            UpdatePopup();
        }
    }

    public void UpdateBigBangButton()
    {
        if (bigBangButton == null) return;
        bigBangButton.interactable = !isTransitioning;
        if (bigBangButtonImage != null)
            bigBangButtonImage.color = isTransitioning ? new Color(0.5f, 0.5f, 0.5f) : Color.white;
    }

    void UpdatePopup()
    {
        long cost = NextCarrotCost();
        bool canAfford = GameManager.Instance.totalCarrots >= cost;
        if (confirmButton      != null) confirmButton.interactable = canAfford;
        if (confirmButtonImage != null)
            confirmButtonImage.color = canAfford ? affordableColor : unaffordableColor;
        if (carrotCostText != null)
            carrotCostText.text = cost.ToString();
        if (carrotCostTextNotReady != null)
            carrotCostTextNotReady.text = cost.ToString();

        UpdateBuffTexts();
        UpdateRabbitRequirementText();
    }

    void UpdateBuffTexts()
    {
        float reproCompounded = (ReproductionGameplayMultiplier() - 1f) * 100f;

        float hutCompounded = HutCapacityGameplayBonusFraction() * 100f;

        string repro  = "+" + FormatBuff(reproCompounded) + "%";
        string hut    = "+" + FormatBuff(hutCompounded) + "%";
        string truck  = "+" + TotalTruckCapacityBuff;

        if (reproductionBuffText          != null) reproductionBuffText.text          = repro;
        if (hutCapacityBuffText           != null) hutCapacityBuffText.text           = hut;
        if (truckCapacityBuffText         != null) truckCapacityBuffText.text         = truck;
        if (reproductionBuffTextNotReady  != null) reproductionBuffTextNotReady.text  = repro;
        if (hutCapacityBuffTextNotReady   != null) hutCapacityBuffTextNotReady.text   = hut;
        if (truckCapacityBuffTextNotReady != null) truckCapacityBuffTextNotReady.text = truck;
    }

    void UpdateRabbitRequirementText()
    {
        if (rabbitRequirementText != null)
            rabbitRequirementText.text = "Reach " + FormatThreshold(NextUnlockThreshold()) + " total rabbits to Big Bang.";
    }

    static string FormatBuff(float value)
    {
        if (value >= 1_000_000_000_000f) return (value / 1_000_000_000_000f).ToString("0.##") + "T";
        if (value >= 1_000_000_000f)     return (value / 1_000_000_000f).ToString("0.##") + "B";
        if (value >= 1_000_000f)         return (value / 1_000_000f).ToString("0.##") + "M";
        if (value >= 10_000f)            return (value / 1_000f).ToString("0.##") + "K";
        return value.ToString("0.##");
    }

    static string FormatThreshold(long number)
    {
        if (number < 1_000_000L)         return number.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        if (number < 1_000_000_000L)     return (number / 1_000_000f).ToString("0.##") + "M";
        if (number < 1_000_000_000_000L) return (number / 1_000_000_000f).ToString("0.##") + "B";
        return (number / 1_000_000_000_000f).ToString("0.##") + "T";
    }

    public void RefreshUIPublic()
    {
        UpdateAllUI();
    }

    void UpdatePrestigeCountText()
    {
        if (prestigeCountText != null)
            prestigeCountText.text = prestigeCount.ToString();
    }

    void UpdateAllUI()
    {
        UpdateBuffTexts();
        UpdateRabbitRequirementText();
        UpdatePrestigeCountText();
        if (carrotCostText != null)
            carrotCostText.text = NextCarrotCost().ToString();
        if (carrotCostTextNotReady != null)
            carrotCostTextNotReady.text = NextCarrotCost().ToString();
    }

    bool HasEnoughRabbits()
    {
        long combined = PlanetManager.Instance != null
            ? PlanetManager.Instance.GetCombinedRabbitsAcrossAllPlanets()
            : GameManager.Instance.totalRabbits;

        return combined >= NextUnlockThreshold();
    }

    public bool IsBigBangAvailable()
    {
        if (GameManager.Instance == null) return false;
        return HasEnoughRabbits() && GameManager.Instance.totalCarrots >= NextCarrotCost();
    }

    public void OpenBigBangPopup()
    {
        if (isTransitioning) return;

        if (LeaderboardUI.leaderboardOpen)
            FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();

        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn();
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();
        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();

        UpdateAllUI();
        popupIsOpen  = true;
        popupOpen    = true;
        showingReady = HasEnoughRabbits();

        if (QuestManager.Instance != null)
            QuestManager.Instance.SetExternallyHidden(true);

        StopAllCoroutines();
        StartCoroutine(FadeDim(dimAlpha));
        if (showingReady)
        {
            if (popupReadyRect != null) popupReadyRect.gameObject.SetActive(true); // in case a previous close deactivated it
            StartCoroutine(SlideTo(popupReadyRect,    openX,   deactivateAfter: false));
            StartCoroutine(SlideTo(popupNotReadyRect, closedX, deactivateAfter: true));
        }
        else
        {
            if (popupNotReadyRect != null) popupNotReadyRect.gameObject.SetActive(true); // in case a previous close deactivated it
            StartCoroutine(SlideTo(popupNotReadyRect, openX,   deactivateAfter: false));
            StartCoroutine(SlideTo(popupReadyRect,    closedX, deactivateAfter: true));
        }

        FadeNavButtonsOut();
    }

    public void CloseBigBangPopup()
    {
        popupIsOpen = false;
        popupOpen   = false;

        if (QuestManager.Instance != null)
            QuestManager.Instance.SetExternallyHidden(false);

        StopAllCoroutines();
        StartCoroutine(FadeDim(0f));
        StartCoroutine(SlideTo(popupReadyRect,    closedX, deactivateAfter: true));
        StartCoroutine(SlideTo(popupNotReadyRect, closedX, deactivateAfter: true));

        FadeNavButtonsIn();
    }

    public void ConfirmBigBang()
    {
        if (!HasEnoughRabbits()) return;
        long cost = NextCarrotCost();
        if (GameManager.Instance.totalCarrots < cost) return;

        if (PlanetManager.Instance != null)
        {
            int totalPlanetCount = System.Enum.GetValues(typeof(PlanetManager.Planet)).Length;
            int unlockedCount = 0;
            foreach (PlanetManager.Planet p in System.Enum.GetValues(typeof(PlanetManager.Planet)))
            {
                if (PlanetManager.Instance.IsUnlocked(p)) unlockedCount++;
            }
            AchievementsManager.Instance?.ReportBigBangWithPlanetsUnlocked(unlockedCount, totalPlanetCount);
        }

        AudioManager.Instance?.PlayBigBangInitiate();

        int prestigeIncrement = (BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive("boost_multitimeline_bigbangs"))
            ? 3 : 1;

        AchievementsManager.Instance?.ReportBigBang(prestigeIncrement);
        _ = GuildManager.Instance?.ReportContestProgressAsync("InitiateBigBangs", prestigeIncrement);

        isTransitioning = true;

        RoboticHatchingBoost.Instance?.PauseForTransition();

        KillAllFoxes();
        KillAllRabbitsInTransit();

        TruckController.ResetAllTrucks();

        if (QuestManager.Instance != null)
            QuestManager.Instance.HandleBigBang();

        GameManager.Instance.SpendCarrots(cost);
        int oldPrestigeCount = prestigeCount;
        prestigeCount += prestigeIncrement;

        CloseBigBangPopup();
        StartCoroutine(BigBangTransition(oldPrestigeCount));
    }

    void KillAllFoxes()
    {
        foreach (FoxMover fox in FindObjectsByType<FoxMover>(FindObjectsInactive.Include))
            Destroy(fox.gameObject);

        FoxSpawner spawner = FindAnyObjectByType<FoxSpawner>();
        if (spawner != null) spawner.ResetSpawner();
    }

    void KillAllRabbitsInTransit()
    {
        foreach (RabbitMover rabbit in FindObjectsByType<RabbitMover>(FindObjectsInactive.Include))
            Destroy(rabbit.gameObject);
    }

    IEnumerator BigBangTransition(int oldPrestigeCount)
    {
        if (transitionCounterText != null)
            transitionCounterText.text = oldPrestigeCount.ToString();

        if (transitionPanel != null)
        {
            transitionPanel.gameObject.SetActive(true);
            transitionPanel.alpha = 0f;
            transitionPanel.blocksRaycasts = true;
            transitionPanel.interactable   = false;
        }
        if (dimOverlay != null) StartCoroutine(FadeDim(0f));

        float elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            if (transitionPanel != null)
                transitionPanel.alpha = Mathf.Clamp01(elapsed / fadeInDuration);
            yield return null;
        }
        if (transitionPanel != null) transitionPanel.alpha = 1f;

        PerformReset();

        float countUpDelay = holdDuration * 0.3f;
        float remainingHold = Mathf.Max(0f, holdDuration - countUpDelay);

        yield return new WaitForSeconds(countUpDelay);
        yield return StartCoroutine(AnimateCounterTickUp(oldPrestigeCount, prestigeCount));
        yield return new WaitForSeconds(remainingHold);

        elapsed = 0f;
        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            if (transitionPanel != null)
                transitionPanel.alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(elapsed / fadeOutDuration));
            yield return null;
        }

        if (transitionPanel != null)
        {
            transitionPanel.alpha = 0f;
            transitionPanel.blocksRaycasts = false;
            transitionPanel.gameObject.SetActive(false);
        }

        isTransitioning = false;

        RoboticHatchingBoost.Instance?.ResumeAfterTransition();
    }

    IEnumerator AnimateCounterTickUp(int fromValue, int toValue)
    {
        if (transitionCounterText == null) yield break;

        RectTransform originalRect = transitionCounterText.rectTransform;
        Vector2 basePos = originalRect.anchoredPosition;

        TextMeshProUGUI outgoing = Instantiate(transitionCounterText, transitionCounterText.transform.parent);
        outgoing.text = fromValue.ToString();
        outgoing.rectTransform.anchoredPosition = basePos;
        outgoing.color = transitionCounterText.color;

        transitionCounterText.text = toValue.ToString();
        AudioManager.Instance?.PlayBigBangNumberSwitch();
        Color incomingColor = transitionCounterText.color;
        transitionCounterText.color = new Color(incomingColor.r, incomingColor.g, incomingColor.b, 0f);
        originalRect.anchoredPosition = basePos + new Vector2(0f, -counterRollOffset);

        float duration = 0.35f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);

            outgoing.rectTransform.anchoredPosition = basePos + new Vector2(0f, counterRollOffset * eased);
            Color oc = outgoing.color;
            outgoing.color = new Color(oc.r, oc.g, oc.b, 1f - eased);

            originalRect.anchoredPosition = basePos + new Vector2(0f, -counterRollOffset * (1f - eased));
            transitionCounterText.color = new Color(incomingColor.r, incomingColor.g, incomingColor.b, eased);

            yield return null;
        }

        originalRect.anchoredPosition = basePos;
        transitionCounterText.color = incomingColor;
        Destroy(outgoing.gameObject);
    }

    public void ResetForTutorialRestart()
    {
        PerformReset();
    }

    void PerformReset()
    {
        GameManager.Instance.totalRabbits = 0;
        GameManager.Instance.totalCoins   = 0;

        RabbitHut[] allHuts = FindObjectsByType<RabbitHut>(FindObjectsInactive.Include);
        foreach (RabbitHut hut in allHuts)
            hut.RemoveRabbits(hut.GetCurrentRabbits());

        ShopUpgradeRow[] allRows = FindObjectsByType<ShopUpgradeRow>(FindObjectsInactive.Include);
        foreach (ShopUpgradeRow row in allRows)
        {
            if (!row.isEpic)
            {
                row.currentLevel = 0;
                row.RefreshUIPublic();
            }
        }

        if (UpgradeManager.Instance != null)
            UpgradeManager.Instance.ResetHuts();

        HutUpgradable[] allHutUpgradables = FindObjectsByType<HutUpgradable>(FindObjectsInactive.Include);
        foreach (HutUpgradable hut in allHutUpgradables)
        {
            if (HutUpgradeManager.Instance != null)
                HutUpgradeManager.Instance.ResetHutLevel(hut);
        }

        if (UpgradeManager.Instance != null)
            UpgradeManager.Instance.ApplyAllUpgradesAfterPrestige();

        if (PlanetManager.Instance != null)
            PlanetManager.Instance.ResetAllPlanets();

        GameManager.Instance.AddRabbits(0);
        GameManager.Instance.AddCoins(0);

        UpdateAllUI();
    }

    IEnumerator SlideTo(RectTransform rect, float targetX, bool deactivateAfter)
    {
        if (rect == null) yield break;
        Vector2 start  = rect.anchoredPosition;
        Vector2 target = new Vector2(targetX, start.y);
        float elapsed  = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / slideDuration);
            t = t * t * (3f - 2f * t);
            rect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }

        rect.anchoredPosition = target;

        // Fully off-screen now — stop paying per-frame cost (Update() on
        // children, Canvas rebuilds, UI batching) for a popup that's just
        // sitting invisible instead of genuinely closed.
        if (deactivateAfter)
        {
            // Safety guard: never disable the SAME GameObject this script
            // lives on — that would kill this coroutine host and silently
            // break every future StartCoroutine() call. Same failure mode
            // hit earlier with TruckController; warn instead of risking it.
            if (rect.gameObject == gameObject)
                Debug.LogWarning($"PrestigeManager: {rect.name} is the SAME GameObject as this script — skipping auto-disable to avoid breaking future Open/Close calls.");
            else
                rect.gameObject.SetActive(false);
        }
    }

    IEnumerator FadeDim(float targetAlpha)
    {
        if (dimOverlay == null) yield break;
        float startAlpha = dimOverlay.alpha;
        float elapsed    = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.Clamp01(elapsed / slideDuration);
            dimOverlay.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        dimOverlay.alpha = targetAlpha;
    }

    // ── Nav Button Fades (Leaderboard/Achievements/Inventory/BigBang/
    // Settings) — hidden while BigBangPopup_Ready or BigBangPopup_NotReady
    // is open, shown again once closed. "bigBangButtonCanvasGroupFade" is
    // separate from "bigBangButton"/"bigBangButtonImage" above (those are
    // the enabled/disabled-during-transition visuals, not a fade target).

    void FadeNavButtonsOut()
    {
        FadeNavButton(leaderboardButtonCanvasGroup, ref navLeaderboardFadeCo, 0f);
        FadeNavButton(achievementButtonCanvasGroup, ref navAchievementFadeCo, 0f);
        FadeNavButton(inventoryButtonCanvasGroup, ref navInventoryFadeCo, 0f);
        FadeNavButton(bigBangButtonCanvasGroupFade, ref navBigBangFadeCo, 0f);
        FadeNavButton(settingsButtonCanvasGroup, ref navSettingsFadeCo, 0f);
    }

    void FadeNavButtonsIn()
    {
        FadeNavButton(leaderboardButtonCanvasGroup, ref navLeaderboardFadeCo, 1f);
        FadeNavButton(achievementButtonCanvasGroup, ref navAchievementFadeCo, 1f);
        FadeNavButton(inventoryButtonCanvasGroup, ref navInventoryFadeCo, 1f);
        FadeNavButton(bigBangButtonCanvasGroupFade, ref navBigBangFadeCo, 1f);
        FadeNavButton(settingsButtonCanvasGroup, ref navSettingsFadeCo, 1f);
    }

    void FadeNavButton(CanvasGroup group, ref Coroutine co, float targetAlpha)
    {
        if (group == null) return;
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(FadeNavCanvasGroupRoutine(group, targetAlpha));
    }

    IEnumerator FadeNavCanvasGroupRoutine(CanvasGroup group, float targetAlpha)
    {
        bool showing = targetAlpha > 0.5f;
        if (!showing)
        {
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        float startAlpha = group.alpha;
        float elapsed = 0f;
        while (elapsed < navButtonFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / navButtonFadeDuration);
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
}
