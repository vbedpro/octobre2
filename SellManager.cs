using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class SellManager : MonoBehaviour
{
    [Header("Settings")]
    public long coinsPerRabbit = 20;
    public long minSell = 5;
    public long maxSell = 10;
    public long increment = 5;

    [Header("Sell Button")]
    public Button sellButton;
    public Image sellButtonImage;
    public Color readyColor = new Color(0.2f, 0.5f, 1f);
    public Color notReadyColor = new Color(0.5f, 0.5f, 0.5f);

    [Header("Confirm Sell Button")]
    public Button confirmSellButton;
    public Image confirmSellButtonImage;
    public Color confirmReadyColor = new Color(0.2f, 0.8f, 0.2f);
    public Color confirmNotReadyColor = new Color(0.5f, 0.5f, 0.5f);

    [Header("Popup")]
    public GameObject sellPopup;
    public RectTransform sellPopupRect;
    public Slider sellSlider;
    public TextMeshProUGUI sellCountText;
    public TextMeshProUGUI coinsEarnedText;

    [Header("Slide Settings")]
    public float openX = 0f;
    public float closedX = -600f;
    public float slideDuration = 0.4f;

    [Header("Vertical Positioning")]
    public RectTransform topPanel;
    public RectTransform hatchButton;

    [Header("Huts")]
    public RabbitHut[] rabbitHuts;

    [Header("Truck")]
    public TruckController truck;
    [Tooltip("Where the batch-sell carrot bonus floating icon spawns — position this at/above the garage.")]
    public Transform garagePosition;

    public static bool popupOpen = false;
    private long currentSellAmount = 5;

    void Start()
    {
        sellPopup.SetActive(false);
        StartCoroutine(InitAfterLayout());
    }

    System.Collections.IEnumerator InitAfterLayout()
    {
        yield return null; // wait one frame for layout to build
        float openY = CalculateOpenY();
        sellPopupRect.anchoredPosition = new Vector2(closedX, openY);
    }

    float CalculateOpenY()
    {
        if (topPanel == null || hatchButton == null || sellPopupRect == null)
            return sellPopupRect.anchoredPosition.y;

        Canvas.ForceUpdateCanvases();

        Vector3[] topCorners   = new Vector3[4];
        Vector3[] hatchCorners = new Vector3[4];
        topPanel.GetWorldCorners(topCorners);
        hatchButton.GetWorldCorners(hatchCorners);

        RectTransform parent = (RectTransform)sellPopupRect.parent;
        Vector2 topPanelBottomLocal  = WorldToLocal(parent, topCorners[0]);
        Vector2 hatchButtonTopLocal  = WorldToLocal(parent, hatchCorners[1]);



        return (topPanelBottomLocal.y + hatchButtonTopLocal.y) / 2f;
    }

    Vector2 WorldToLocal(RectTransform parent, Vector3 worldPoint)
    {
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parent,
            RectTransformUtility.WorldToScreenPoint(null, worldPoint),
            null,
            out localPoint
        );
        return localPoint;
    }

    void Update()
    {
        UpdateSellButton();
        UpdateConfirmButton();
    }

    void UpdateSellButton()
    {
        bool canSell = GameManager.Instance.truckAvailable;
        sellButton.interactable = canSell;
        sellButtonImage.color = canSell ? readyColor : notReadyColor;
    }

    void UpdateConfirmButton()
    {
        bool canConfirm = GameManager.Instance.totalRabbits >= currentSellAmount;
        if (confirmSellButton != null)
            confirmSellButton.interactable = canConfirm;
        if (confirmSellButtonImage != null)
            confirmSellButtonImage.color = canConfirm ? confirmReadyColor : confirmNotReadyColor;
    }

    public void OpenSellPopup()
    {
        if (!GameManager.Instance.truckAvailable) return;

        if (SettingsPanelController.settingsOpen)
            SettingsPanelController.Instance?.CloseSettings();
        if (AchievementsPanelUI.achievementsOpen)
            FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();

        if (ShopManager.shopOpen)
            FindAnyObjectByType<ShopManager>().CloseShop();

        if (InventoryPanel.inventoryOpen)
            FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        if (HutUpgradeManager.popupOpen)
            FindAnyObjectByType<HutUpgradeManager>().ClosePopup();

        if (LeaderboardUI.leaderboardOpen)
            FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();

        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn();

        currentSellAmount = minSell;

        long actualMax = System.Math.Max(minSell, maxSell);
        int steps = (int)((actualMax - minSell) / increment);
        steps = System.Math.Max(steps, 1);

        sellSlider.minValue = 0;
        sellSlider.maxValue = steps;
        sellSlider.wholeNumbers = true;
        sellSlider.value = 0;

        popupOpen = true;
        sellPopup.SetActive(true); // in case CloseSellPopup() deactivated it last time
        StopAllCoroutines();
        float openY = sellPopupRect.anchoredPosition.y;
        StartCoroutine(SlideTo(sellPopupRect, new Vector2(openX, openY), deactivateAfter: false));
        UpdatePopupUI();

        if (TutorialManager.Instance != null)
            TutorialManager.Instance.ReportSellPopupOpened();
    }

    public void CloseSellPopup()
    {
        popupOpen = false;
        StopAllCoroutines();
        float openY = sellPopupRect.anchoredPosition.y;
        StartCoroutine(SlideTo(sellPopupRect, new Vector2(closedX, openY), deactivateAfter: true));
    }

    public void OnSliderChanged(float value)
    {
        currentSellAmount = minSell + ((long)Mathf.RoundToInt(value) * increment);
        currentSellAmount = System.Math.Clamp(currentSellAmount, minSell, maxSell);
        UpdatePopupUI();

        if (TutorialManager.Instance != null)
            TutorialManager.Instance.ReportSliderChanged(currentSellAmount);
    }

    public void ConfirmSell()
    {
        if (GameManager.Instance.totalRabbits < currentSellAmount) return;

        long coinsEarned = currentSellAmount * coinsPerRabbit;

        if (BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive("boost_gold_rush"))
            coinsEarned *= 2;

        GameManager.Instance.AddCoins(coinsEarned);
        GameManager.Instance.AddRabbits(-currentSellAmount);
        AchievementsManager.Instance?.ReportRabbitsSold(currentSellAmount);

        if (TutorialManager.Instance != null)
            TutorialManager.Instance.ReportSellConfirmed();
        GameManager.Instance.TruckUsed();

        long totalCarrotsEarned = 0;

        if (EpicUpgradeManager.Instance != null)
        {
            int goldenRabbitsLevel = EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.GoldenRabbits);
            if (goldenRabbitsLevel > 0)
                totalCarrotsEarned += goldenRabbitsLevel * 1;
        }

        totalCarrotsEarned += GetPlanetCarrotBonus();

        if (totalCarrotsEarned > 0)
        {
            GameManager.Instance.AddCarrots(totalCarrotsEarned);

            if (FloatingIconManager.Instance != null && garagePosition != null)
                FloatingIconManager.Instance.ShowCarrotGain(garagePosition.position, totalCarrotsEarned);
        }

        if (QuestManager.Instance != null)
        {
            QuestManager.Instance.ReportRabbitsSold(currentSellAmount);
            QuestManager.Instance.ReportBatchSold();
        }

        DistributeSoldRabbits(currentSellAmount);

        if (truck != null) truck.Dispatch();

        CloseSellPopup();
    }

    long GetPlanetCarrotBonus()
    {
        if (PlanetManager.Instance == null) return 0;
        return (long)PlanetManager.Instance.currentPlanet;
    }

    void DistributeSoldRabbits(long amount)
    {
        System.Collections.Generic.List<RabbitHut> activeHuts = new System.Collections.Generic.List<RabbitHut>();
        foreach (RabbitHut hut in rabbitHuts)
        {
            if (hut != null && hut.gameObject.activeInHierarchy && hut.GetCurrentRabbits() > 0)
                activeHuts.Add(hut);
        }

        if (activeHuts.Count == 0) return;

        activeHuts.Sort((a, b) => b.GetCurrentRabbits().CompareTo(a.GetCurrentRabbits()));

        int index = 0;
        while (amount > 0 && index < activeHuts.Count)
        {
            long currentLevel = activeHuts[index].GetCurrentRabbits();
            if (currentLevel <= 0) break;

            int groupEnd = index;
            while (groupEnd + 1 < activeHuts.Count && activeHuts[groupEnd + 1].GetCurrentRabbits() == currentLevel)
                groupEnd++;
            int groupSize = groupEnd - index + 1;

            long nextLevel = (groupEnd + 1 < activeHuts.Count) ? activeHuts[groupEnd + 1].GetCurrentRabbits() : 0;

            long headroom = currentLevel - nextLevel;
            long maxRemovableThisPass = headroom * groupSize;

            long toRemove = System.Math.Min(amount, maxRemovableThisPass);

            long perHut = toRemove / groupSize;
            long remainder = toRemove % groupSize;
            for (int i = index; i <= groupEnd; i++)
            {
                long take = perHut + (i - index < remainder ? 1 : 0);
                if (take > 0) activeHuts[i].RemoveRabbits(take);
            }

            amount -= toRemove;

            if (toRemove < maxRemovableThisPass) break;
        }
    }

    void UpdatePopupUI()
    {
        if (sellCountText != null)
            sellCountText.text = GameManager.FormatFullNumber(currentSellAmount) + " rabbits";
        if (coinsEarnedText != null)
        {
            long previewCoins = currentSellAmount * coinsPerRabbit;
            if (BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive("boost_gold_rush"))
                previewCoins *= 2;
            coinsEarnedText.text = GameManager.FormatNumberThreshold(previewCoins);
        }
        UpdateConfirmButton();
    }

    IEnumerator SlideTo(RectTransform rect, Vector2 target, bool deactivateAfter)
    {
        Vector2 start = rect.anchoredPosition;
        float elapsed = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / slideDuration;
            t = t * t * (3f - 2f * t);
            rect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }

        rect.anchoredPosition = target;

        if (deactivateAfter)
        {
            // Safety guard: never disable the SAME GameObject this script
            // lives on — that would kill this coroutine host and silently
            // break every future StartCoroutine() call. Same failure mode
            // hit earlier with TruckController; warn instead of risking it.
            if (rect.gameObject == gameObject)
                Debug.LogWarning($"SellManager: {rect.name} is the SAME GameObject as this script — skipping auto-disable to avoid breaking future Open/Close calls.");
            else
                rect.gameObject.SetActive(false);
        }
    }
}
