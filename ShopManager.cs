using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class ShopManager : MonoBehaviour
{
    public static bool shopOpen = false;

    [Header("Panel")]
    public GameObject shopPanel;
    public RectTransform shopRect;
    public float slideDuration = 0.4f;
    public float openX  = -200f;
    public float closedX = 600f;

    [Header("Vertical Positioning")]
    public RectTransform topPanel;
    public RectTransform hatchButton;

    private float openY   = 0f;
    private float closedY = 0f;

    [Header("Tabs")]
    public GameObject commonContent;
    public GameObject epicContent;
    public GameObject boostsContent;
    public Image commonTabImage;
    public Image epicTabImage;
    public Image boostsTabImage;
    public Color commonTabColor = new Color(0f, 0.5f, 1f);
    public Color epicTabColor   = new Color(1f, 0.5f, 0f);
    public Color boostsTabColor = new Color(0.6f, 0f, 0.8f);
    [Tooltip("Shown only while the Epic tab is active.")]
    public GameObject epicUpgradesInfoText;
    [Tooltip("Shown only while the Boosts tab is active.")]
    public GameObject boostsInfoText;

    [Header("Scroll")]
    public ScrollRect shopScrollRect;
    public RectTransform commonContentRect;
    public RectTransform epicContentRect;
    public RectTransform boostsContentRect;

    [Header("Panel Background Colors")]
    public Image shopPanelImage;
    public Image shopScrollViewImage;
    public Color commonBackgroundColor = new Color32(0x00, 0x5C, 0xCD, 0xFF);
    public Color epicBackgroundColor   = new Color32(0xE1, 0x5F, 0x00, 0xFF);
    public Color boostsBackgroundColor = new Color32(0x5C, 0x00, 0x7A, 0xFF);

    [Header("Button Fades (hidden while shop is open)")]
    public CanvasGroup leaderboardButtonCanvasGroup;
    public CanvasGroup achievementButtonCanvasGroup;
    public CanvasGroup inventoryButtonCanvasGroup;
    public CanvasGroup bigBangButtonCanvasGroup;
    public CanvasGroup settingsButtonCanvasGroup;
    public float leaderboardButtonFadeDuration = 0.25f;
    private Coroutine leaderboardFadeCo;
    private Coroutine achievementFadeCo;
    private Coroutine inventoryFadeCo;
    private Coroutine bigBangFadeCo;
    private Coroutine settingsFadeCo;

    void Start()
    {
        shopPanel.SetActive(true);
        CalculateOpenY();
        shopRect.anchoredPosition = new Vector2(closedX, openY);
        ShowCommonTab();
        shopPanel.SetActive(false); // starts genuinely closed, not just off-screen
    }

    void CalculateOpenY()
    {
        if (topPanel == null || hatchButton == null || shopRect == null) return;

        Canvas.ForceUpdateCanvases();

        Vector3[] topCorners   = new Vector3[4];
        Vector3[] hatchCorners = new Vector3[4];
        topPanel.GetWorldCorners(topCorners);
        hatchButton.GetWorldCorners(hatchCorners);

        RectTransform parent = (RectTransform)shopRect.parent;

        Vector2 topPanelBottomLocal = WorldToLocal(parent, topCorners[0]);
        Vector2 hatchButtonTopLocal = WorldToLocal(parent, hatchCorners[1]);

        openY   = (topPanelBottomLocal.y + hatchButtonTopLocal.y) / 2f;
        closedY = openY;

        float availableHeight = topPanelBottomLocal.y - hatchButtonTopLocal.y;
        float naturalHeight   = shopRect.rect.height;
        if (naturalHeight > 0f)
        {
            float scale = Mathf.Min(1f, (availableHeight * 0.95f) / naturalHeight);
            shopRect.localScale = new Vector3(scale, scale, 1f);
        }
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

    public void OpenShop()
    {
        if (SettingsPanelController.settingsOpen)
            SettingsPanelController.Instance?.CloseSettings();
        if (AchievementsPanelUI.achievementsOpen)
            FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();

        if (SellManager.popupOpen)
            FindAnyObjectByType<SellManager>().CloseSellPopup();

        if (HutUpgradeManager.popupOpen)
            FindAnyObjectByType<HutUpgradeManager>().ClosePopup();

        if (InventoryPanel.inventoryOpen)
            FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        if (LeaderboardUI.leaderboardOpen)
            FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();

        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn();

        if (QuestManager.Instance != null)
            QuestManager.Instance.SetExternallyHidden(true);

        shopOpen = true;
        shopPanel.SetActive(true); // in case CloseShop() deactivated it last time
        StopAllCoroutines();
        StartCoroutine(SlideTo(new Vector2(openX, openY), deactivateAfter: false));
        FadeLeaderboardButton(0f);
        FadeAchievementButton(0f);
        FadeInventoryButton(0f);
        FadeBigBangButton(0f);
        FadeSettingsButton(0f);

        if (shopScrollRect != null && TutorialManager.Instance != null)
            shopScrollRect.enabled = TutorialManager.Instance.IsShopScrollAllowed();

        if (TutorialManager.Instance != null)
            TutorialManager.Instance.ReportShopOpened();
    }

    // Called by the tutorial-target Close button ONLY — the normal
    // CloseShop() below is what everything else (back button, tapping
    // outside, etc.) calls, and is blocked during tutorial unless this is
    // specifically the "close the shop" step.
    public void CloseShop()
    {
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive
            && !TutorialManager.Instance.IsShopCloseAllowed())
            return;

        if (QuestManager.Instance != null)
            QuestManager.Instance.SetExternallyHidden(false);

        shopOpen = false;
        StopAllCoroutines();
        StartCoroutine(SlideTo(new Vector2(closedX, closedY), deactivateAfter: true));
        FadeLeaderboardButton(1f);
        FadeAchievementButton(1f);
        FadeInventoryButton(1f);
        FadeBigBangButton(1f);
        FadeSettingsButton(1f);

        if (TutorialManager.Instance != null)
            TutorialManager.Instance.ReportShopClosed();
    }

    public void ShowCommonTab()
    {
        commonContent.SetActive(true);
        epicContent.SetActive(false);
        if (boostsContent != null) boostsContent.SetActive(false);
        commonTabImage.color = commonTabColor;
        epicTabImage.color   = epicTabColor;
        if (boostsTabImage != null) boostsTabImage.color = boostsTabColor;
        if (epicUpgradesInfoText != null) epicUpgradesInfoText.SetActive(false);
        if (boostsInfoText != null) boostsInfoText.SetActive(false);

        if (shopScrollRect != null && commonContentRect != null)
        {
            shopScrollRect.content = commonContentRect;
            shopScrollRect.verticalNormalizedPosition = 1f;
        }

        if (shopPanelImage != null) shopPanelImage.color = commonBackgroundColor;
        if (shopScrollViewImage != null) shopScrollViewImage.color = commonBackgroundColor;
    }

    public void ShowEpicTab()
    {
        commonContent.SetActive(false);
        epicContent.SetActive(true);
        if (boostsContent != null) boostsContent.SetActive(false);
        commonTabImage.color = commonTabColor;
        epicTabImage.color   = epicTabColor;
        if (boostsTabImage != null) boostsTabImage.color = boostsTabColor;
        if (epicUpgradesInfoText != null) epicUpgradesInfoText.SetActive(true);
        if (boostsInfoText != null) boostsInfoText.SetActive(false);

        if (shopScrollRect != null && epicContentRect != null)
        {
            shopScrollRect.content = epicContentRect;
            shopScrollRect.verticalNormalizedPosition = 1f;
        }

        if (shopPanelImage != null) shopPanelImage.color = epicBackgroundColor;
        if (shopScrollViewImage != null) shopScrollViewImage.color = epicBackgroundColor;
    }

    public void ShowBoostsTab()
    {
        commonContent.SetActive(false);
        epicContent.SetActive(false);
        if (boostsContent != null) boostsContent.SetActive(true);
        commonTabImage.color = commonTabColor;
        epicTabImage.color   = epicTabColor;
        if (boostsTabImage != null) boostsTabImage.color = boostsTabColor;
        if (epicUpgradesInfoText != null) epicUpgradesInfoText.SetActive(false);
        if (boostsInfoText != null) boostsInfoText.SetActive(true);

        if (shopScrollRect != null && boostsContentRect != null)
        {
            shopScrollRect.content = boostsContentRect;
            shopScrollRect.verticalNormalizedPosition = 1f;
        }

        if (shopPanelImage != null) shopPanelImage.color = boostsBackgroundColor;
        if (shopScrollViewImage != null) shopScrollViewImage.color = boostsBackgroundColor;
    }

    void FadeAchievementButton(float targetAlpha)
    {
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        if (achievementButtonCanvasGroup == null) return;
        if (achievementFadeCo != null) StopCoroutine(achievementFadeCo);
        achievementFadeCo = StartCoroutine(FadeCanvasGroupRoutine(achievementButtonCanvasGroup, targetAlpha));
    }

    void FadeInventoryButton(float targetAlpha)
    {
        // Same tutorial-exclusivity guard as the leaderboard/achievement
        // buttons — skip our own fade while the tutorial is controlling
        // this button's visibility itself.
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        if (inventoryButtonCanvasGroup == null) return;
        if (inventoryFadeCo != null) StopCoroutine(inventoryFadeCo);
        inventoryFadeCo = StartCoroutine(FadeCanvasGroupRoutine(inventoryButtonCanvasGroup, targetAlpha));
    }

    void FadeLeaderboardButton(float targetAlpha)
    {
        // While the tutorial is active, it exclusively controls this
        // button's visibility (fully hidden the whole time) — skip our
        // own fade so we don't undo that, e.g. by fading it back visible
        // when the shop closes during the "close shop" tutorial step.
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        if (leaderboardButtonCanvasGroup == null) return;
        if (leaderboardFadeCo != null) StopCoroutine(leaderboardFadeCo);
        leaderboardFadeCo = StartCoroutine(FadeCanvasGroupRoutine(leaderboardButtonCanvasGroup, targetAlpha));
    }

    void FadeBigBangButton(float targetAlpha)
    {
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        if (bigBangButtonCanvasGroup == null) return;
        if (bigBangFadeCo != null) StopCoroutine(bigBangFadeCo);
        bigBangFadeCo = StartCoroutine(FadeCanvasGroupRoutine(bigBangButtonCanvasGroup, targetAlpha));
    }

    void FadeSettingsButton(float targetAlpha)
    {
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        if (settingsButtonCanvasGroup == null) return;
        if (settingsFadeCo != null) StopCoroutine(settingsFadeCo);
        settingsFadeCo = StartCoroutine(FadeCanvasGroupRoutine(settingsButtonCanvasGroup, targetAlpha));
    }

    // Generalized fade shared by all five buttons above — stop blocking
    // clicks/raycasts immediately when hiding, only start allowing them
    // once fully visible when showing.
    IEnumerator FadeCanvasGroupRoutine(CanvasGroup group, float targetAlpha)
    {
        bool hiding = targetAlpha < 0.5f;
        if (hiding)
        {
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        float startAlpha = group.alpha;
        float elapsed = 0f;
        while (elapsed < leaderboardButtonFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / leaderboardButtonFadeDuration);
            group.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }
        group.alpha = targetAlpha;

        if (!hiding)
        {
            group.interactable = true;
            group.blocksRaycasts = true;
        }
    }

    IEnumerator SlideTo(Vector2 target, bool deactivateAfter)
    {
        Vector2 start   = shopRect.anchoredPosition;
        float elapsed   = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t  = elapsed / slideDuration;
            t = t * t * (3f - 2f * t);
            shopRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }

        shopRect.anchoredPosition = target;

        if (deactivateAfter)
        {
            // Safety guard: if shopPanel were ever the SAME GameObject this
            // script lives on, disabling it here would kill this coroutine
            // host and silently break every future StartCoroutine() call
            // from OpenShop() (a disabled GameObject can't start coroutines
            // at all). Warn instead of risking that — same failure mode we
            // hit with an earlier TruckController fix.
            if (shopPanel == gameObject)
            {
                Debug.LogWarning("ShopManager: shopPanel is the SAME GameObject as this script — " +
                    "skipping auto-disable on close to avoid breaking future OpenShop() calls. " +
                    "Move ShopManager to a separate always-active parent, with shopPanel as a child, to fix this properly.");
            }
            else
            {
                shopPanel.SetActive(false);
            }
        }
    }
}
