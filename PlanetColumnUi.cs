using UnityEngine;
using UnityEngine.UI;
using System.Collections;

// Attach to PlanetButton (sibling of BigBangButton). Tapping the button
// reveals 5 planet icons stacked in a column, animating each one in with a
// staggered fade + scale, bottom icon (Earth) first, working upward to
// Pluto. Tapping again reverses the order (top-to-bottom out). A white
// background panel behind the icons grows/shrinks in sync, anchored at the
// bottom so it grows upward on open and shrinks back down on close.
//
// All 5 planet icons are ALWAYS shown when expanded — locked ones are
// distinguished only by their lock overlay GameObject, never by dimming or
// tinting the icon itself.
public class PlanetColumnUI : MonoBehaviour
{
    [Header("Button")]
    public Button planetButton;
    [Tooltip("The icon shown ON the PlanetButton itself — always reflects whichever planet the player is currently playing on.")]
    public Image buttonIcon;

    [Header("Column Content (holds the 5 icon slots, Vertical Layout Group + Content Size Fitter)")]
    public RectTransform columnContent;

    [Header("Background Panel (white, sits BEHIND columnContent, anchored BOTTOM/pivot bottom)")]
    [Tooltip("A plain Image (white) whose RectTransform grows/shrinks height to sit behind the icons. Must be a sibling BELOW columnContent in the Hierarchy so it renders behind it, or otherwise pushed back in draw order.")]
    public RectTransform backgroundPanel;
    [Tooltip("Extra width/height padding added around the icon column's own size, so the white panel doesn't hug the icons too tightly.")]
    public Vector2 backgroundPadding = new Vector2(20f, 20f);

    [Header("Reveal Animation")]
    [Tooltip("How long each individual icon takes to fade/scale in.")]
    public float iconAnimDuration = 0.18f;
    [Tooltip("Delay between each icon starting its animation, bottom -> top on open, top -> bottom on close.")]
    public float staggerDelay = 0.06f;
    public Vector3 hiddenScale = new Vector3(0.4f, 0.4f, 1f);

    [System.Serializable]
    public class PlanetIconSlot
    {
        public PlanetManager.Planet planet;
        public Button button;
        public Image icon;
        public GameObject lockOverlay; // shown when this planet is NOT unlocked

        [HideInInspector] public CanvasGroup canvasGroup;
        [HideInInspector] public RectTransform rect;
        [HideInInspector] public Sprite originalSprite; // cached so we can restore it once unlocked
    }

    [Tooltip("Shown instead of a planet's real icon while it hasn't been purchased/unlocked yet.")]
    public Sprite unknownPlanetIcon;

    [Header("Planet Icons — order in this list should be BOTTOM to TOP (Earth first, Pluto last)")]
    public PlanetIconSlot[] planetIcons;

    private bool isOpen = false;
    private Coroutine revealCoroutine;
    private Coroutine backgroundCoroutine;

    void Start()
    {
        if (planetButton != null)
            planetButton.onClick.AddListener(ToggleColumn);

        foreach (PlanetIconSlot slot in planetIcons)
        {
            if (slot.icon != null)
            {
                slot.rect = slot.icon.GetComponent<RectTransform>();
                slot.originalSprite = slot.icon.sprite;
                slot.canvasGroup = slot.icon.GetComponent<CanvasGroup>();
                if (slot.canvasGroup == null)
                    slot.canvasGroup = slot.icon.gameObject.AddComponent<CanvasGroup>();
            }

            if (slot.button != null)
            {
                PlanetManager.Planet p = slot.planet; // capture for closure
                slot.button.onClick.AddListener(() => OnPlanetIconTapped(p));
            }
        }

        SetAllIconsHidden();
        RefreshLockOverlays();
        RefreshButtonIcon();

        if (columnContent != null)
            columnContent.gameObject.SetActive(false);

        if (backgroundPanel != null)
            backgroundPanel.sizeDelta = new Vector2(backgroundPanel.sizeDelta.x, 0f);
    }

    void OnEnable()
    {
        RefreshLockOverlays();
        RefreshButtonIcon();
    }

    void SetAllIconsHidden()
    {
        foreach (PlanetIconSlot slot in planetIcons)
        {
            if (slot.canvasGroup != null) slot.canvasGroup.alpha = 0f;
            if (slot.rect != null) slot.rect.localScale = hiddenScale;
        }
    }

    public void ToggleColumn()
    {
        if (isOpen) CloseColumn();
        else OpenColumn();
    }

    public void OpenColumn()
    {
        if (isOpen) return;
        isOpen = true;
        RefreshLockOverlays();

        CloseOtherUI();

        if (columnContent != null) columnContent.gameObject.SetActive(true);

        float totalDuration = TotalRevealDuration();

        if (revealCoroutine != null) StopCoroutine(revealCoroutine);
        revealCoroutine = StartCoroutine(RevealRoutine(opening: true));

        if (backgroundCoroutine != null) StopCoroutine(backgroundCoroutine);
        backgroundCoroutine = StartCoroutine(GrowBackground(totalDuration));
    }

    public void CloseColumn()
    {
        if (!isOpen) return;
        isOpen = false;

        float totalDuration = TotalRevealDuration();

        if (revealCoroutine != null) StopCoroutine(revealCoroutine);
        revealCoroutine = StartCoroutine(RevealRoutine(opening: false));

        if (backgroundCoroutine != null) StopCoroutine(backgroundCoroutine);
        backgroundCoroutine = StartCoroutine(ShrinkBackground(totalDuration));
    }

    float TotalRevealDuration()
    {
        int count = planetIcons != null ? planetIcons.Length : 0;
        return staggerDelay * Mathf.Max(0, count - 1) + iconAnimDuration;
    }

    // Closes every other open panel — same pattern ShopManager.OpenShop()/
    // SellManager.OpenSellPopup()/HutUpgradeManager.OpenFor() already use
    // before opening themselves.
    void CloseOtherUI()
    {
        if (ShopManager.shopOpen)
            FindAnyObjectByType<ShopManager>()?.CloseShop();

        if (SellManager.popupOpen)
            FindAnyObjectByType<SellManager>()?.CloseSellPopup();

        if (HutUpgradeManager.popupOpen)
            FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();

        if (InventoryPanel.inventoryOpen)
            FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        if (LeaderboardUI.leaderboardOpen)
            FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();

        if (PrestigeManager.popupOpen)
            FindAnyObjectByType<PrestigeManager>()?.CloseBigBangPopup();

        if (SettingsPanelController.settingsOpen)
            SettingsPanelController.Instance?.CloseSettings();

        if (AchievementsPanelUI.achievementsOpen)
            FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
    }

    // Measures columnContent's real laid-out height (forcing an immediate
    // layout pass, since Content Size Fitter normally only updates on the
    // next canvas refresh, which would be a frame too late here) and grows
    // the background panel to match it + padding, anchored at the bottom
    // so growth reads as "rising up" behind the icons as they appear.
    IEnumerator GrowBackground(float duration)
    {
        if (backgroundPanel == null) yield break;

        yield return null; // let columnContent's SetActive(true) take effect
        LayoutRebuilder.ForceRebuildLayoutImmediate(columnContent);

        float targetHeight = columnContent.rect.height + backgroundPadding.y;
        float targetWidth = columnContent.rect.width + backgroundPadding.x;
        backgroundPanel.sizeDelta = new Vector2(targetWidth, backgroundPanel.sizeDelta.y);

        yield return AnimateBackgroundHeight(targetHeight, duration);
    }

    IEnumerator ShrinkBackground(float duration)
    {
        if (backgroundPanel == null) yield break;
        yield return AnimateBackgroundHeight(0f, duration);
    }

    IEnumerator AnimateBackgroundHeight(float targetHeight, float duration)
    {
        float startHeight = backgroundPanel.sizeDelta.y;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            t = t * t * (3f - 2f * t); // smoothstep
            float h = Mathf.Lerp(startHeight, targetHeight, t);
            backgroundPanel.sizeDelta = new Vector2(backgroundPanel.sizeDelta.x, h);
            yield return null;
        }

        backgroundPanel.sizeDelta = new Vector2(backgroundPanel.sizeDelta.x, targetHeight);
        backgroundCoroutine = null;
    }

    IEnumerator RevealRoutine(bool opening)
    {
        // Bottom-to-top order on open (index 0 = Earth = bottom, first).
        // Top-to-bottom order on close (reverse the list).
        int count = planetIcons.Length;
        for (int i = 0; i < count; i++)
        {
            int index = opening ? i : (count - 1 - i);
            PlanetIconSlot slot = planetIcons[index];
            StartCoroutine(AnimateIcon(slot, opening));
            yield return new WaitForSeconds(staggerDelay);
        }

        // Wait for the last icon's own animation to finish before fully
        // deactivating the content object on close.
        yield return new WaitForSeconds(iconAnimDuration);

        if (!opening && columnContent != null)
            columnContent.gameObject.SetActive(false);

        revealCoroutine = null;
    }

    IEnumerator AnimateIcon(PlanetIconSlot slot, bool showing)
    {
        if (slot.canvasGroup == null || slot.rect == null) yield break;

        float startAlpha = slot.canvasGroup.alpha;
        float targetAlpha = showing ? 1f : 0f;
        Vector3 startScale = slot.rect.localScale;
        Vector3 targetScale = showing ? Vector3.one : hiddenScale;

        float elapsed = 0f;
        while (elapsed < iconAnimDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / iconAnimDuration);
            t = t * t * (3f - 2f * t); // smoothstep
            slot.canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            slot.rect.localScale = Vector3.Lerp(startScale, targetScale, t);
            yield return null;
        }

        slot.canvasGroup.alpha = targetAlpha;
        slot.rect.localScale = targetScale;
    }

    void OnPlanetIconTapped(PlanetManager.Planet planet)
    {
        if (PlanetManager.Instance == null) return;
        if (!PlanetManager.Instance.IsUnlocked(planet)) return; // locked — no-op

        if (PlanetTransitionController.Instance != null)
            PlanetTransitionController.Instance.PlayTransition(planet);
        else
            PlanetManager.Instance.SwitchToPlanet(planet); // fallback if no transition controller is set up

        CloseColumn();
    }

    // Only toggles the lock overlay GameObject — never touches icon color/alpha.
    void RefreshLockOverlays()
    {
        if (PlanetManager.Instance == null) return;

        foreach (PlanetIconSlot slot in planetIcons)
        {
            bool unlocked = PlanetManager.Instance.IsUnlocked(slot.planet);

            if (slot.lockOverlay != null)
                slot.lockOverlay.SetActive(!unlocked);

            // Show the unknown-planet icon while locked, the real one once
            // unlocked — no opacity/dimming involved at all. Button stays
            // fully "normal looking" regardless of lock state; tapping a
            // locked planet is already a no-op via OnPlanetIconTapped's own
            // IsUnlocked check, so interactable doesn't need to reflect
            // lock state (and leaving it true avoids Unity's automatic
            // Disabled Color tint dimming the icon on top of our sprite swap).
            if (slot.icon != null && unknownPlanetIcon != null)
                slot.icon.sprite = unlocked ? slot.originalSprite : unknownPlanetIcon;
        }
    }

    // Updates the PlanetButton's own icon to match whichever planet is
    // currently active (PlanetManager.currentPlanet), by reusing the same
    // sprite already assigned to that planet's slot in the column.
    // Looks up the sprite already assigned to a given planet's icon slot —
    // used by QuestManager to show which planet an active quest belongs to.
    public Sprite GetIconSprite(PlanetManager.Planet planet)
    {
        foreach (PlanetIconSlot slot in planetIcons)
            if (slot.planet == planet && slot.originalSprite != null)
                return slot.originalSprite;
        return null;
    }

    public void RefreshButtonIcon()
    {
        if (PlanetManager.Instance == null || buttonIcon == null) return;

        PlanetManager.Planet current = PlanetManager.Instance.currentPlanet;
        foreach (PlanetIconSlot slot in planetIcons)
        {
            if (slot.planet == current && slot.originalSprite != null)
            {
                buttonIcon.sprite = slot.originalSprite;
                break;
            }
        }
    }
}
