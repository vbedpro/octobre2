using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

public class HutUpgradeManager : MonoBehaviour
{
    public static HutUpgradeManager Instance;
    public static bool popupOpen = false;

    [Header("Level Data (index 0 = level 1 base)")]
    public string[] levelNames;        // one name per level
    public long[] levelCapacities;     // actual capacity per level
    public long[] levelPrices;         // price to upgrade TO this level
    public Sprite[] levelPreviews;     // popup image per level

    [Header("Popup Slide")]
    public GameObject panel;
    public RectTransform panelRect;
    public float slideDuration = 0.4f;
    public float openX = 0f;
    public float closedX = -800f;

    [Header("Popup UI")]
    public TextMeshProUGUI hutNameText;
    public Image previewImage;
    [Tooltip("One scale slider per level, matching levelPreviews (index 0 = level 1). Auto-resizes to match levelPreviews' length. Drag any slider in the Inspector (works in Play Mode too) to dial in that level's preview size.")]
    [Range(0.1f, 3f)]
    public float[] previewScales;
    public TextMeshProUGUI levelText;
    public GaugeBar levelGauge;
    public TextMeshProUGUI occupancyNumberText;
    public TextMeshProUGUI capacityText;
    public Button upgradeButton;
    public Image upgradeButtonImage;
    public TextMeshProUGUI upgradeButtonText;
    public Color affordableColor = new Color(0.2f, 0.8f, 0.2f);
    public Color notAffordableColor = new Color(0.5f, 0.5f, 0.5f);

    [Header("Camera")]
    public Camera cam;

    [Header("Tap")]
    [Tooltip("Max finger travel that still counts as a tap, in inches (density-independent).")]
    public float tapMoveToleranceInches = 0.1f;
    [Tooltip("Used only if the device reports no DPI.")]
    public float tapMoveToleranceFallbackPixels = 35f;

    private HutUpgradable currentHut;
    private HutUpgradable pressedHut;
    private Vector2 pressPos;
    private int pressFingerId = -1;

    // Persistent % bonus applied by quest buffs/debuffs, on top of shop + prestige.
    private float questCapacityBonusPercent = 0f;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void OnEnable()  { EnhancedTouchSupport.Enable(); }
    void OnDisable() { EnhancedTouchSupport.Disable(); }

    void Start()
    {
        if (cam == null) cam = Camera.main;
        panel.SetActive(true);
        panelRect.anchoredPosition = new Vector2(closedX, panelRect.anchoredPosition.y);
        panel.SetActive(false); // starts genuinely closed, not just off-screen

        // apply each hut's starting level (shows base model + sets capacity)
        foreach (HutUpgradable h in FindObjectsByType<HutUpgradable>(FindObjectsInactive.Exclude))
            ApplyLevelTo(h, h.level);
    }

    void Update()
    {
        // while open, keep the upgrade button affordability live
        if (popupOpen)
        {
            UpdateButtonState();
            UpdateOccupancyText();
            return;
        }

        if (ShopManager.shopOpen || SellManager.popupOpen || AuthenticationPopUp.popupOpen || OfflineEarningsPopup.popupOpen) { pressedHut = null; pressFingerId = -1; return; }

        DetectTap();
    }

    // ── Tap detection (touch on mobile, mouse on PC) ──────────────────
    void DetectTap()
    {
        // ---- TOUCH ----
        if (Touchscreen.current != null)
        {
            var touches = ETouch.activeTouches;

            if (pressFingerId == -1)
            {
                for (int i = 0; i < touches.Count; i++)
                {
                    var t = touches[i];
                    if (t.phase != UnityEngine.InputSystem.TouchPhase.Began) continue;
                    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(t.touchId)) continue;

                    pressFingerId = t.touchId;
                    pressPos = t.screenPosition;
                    pressedHut = RaycastHut(pressPos);
                    return;
                }
                return;
            }

            for (int i = 0; i < touches.Count; i++)
            {
                var t = touches[i];
                if (t.touchId != pressFingerId) continue;

                if (t.phase == UnityEngine.InputSystem.TouchPhase.Ended)
                {
                    TryOpenFromTap(t.screenPosition);
                    pressFingerId = -1;
                    pressedHut = null;
                }
                else if (t.phase == UnityEngine.InputSystem.TouchPhase.Canceled)
                {
                    pressFingerId = -1;
                    pressedHut = null;
                }
                return;
            }

            // tracked finger vanished without an Ended event
            pressFingerId = -1;
            pressedHut = null;
            return;
        }

        // ---- MOUSE ----
        var mouse = Mouse.current;
        if (mouse == null) return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            pressedHut = null;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            pressPos = mouse.position.ReadValue();
            pressedHut = RaycastHut(pressPos);
        }
        else if (mouse.leftButton.wasReleasedThisFrame)
        {
            TryOpenFromTap(mouse.position.ReadValue());
            pressedHut = null;
        }
    }

    void TryOpenFromTap(Vector2 releasePos)
    {
        if (pressedHut == null) return;
        bool moved = Vector2.Distance(pressPos, releasePos) > TapMoveTolerancePixels();
        if (moved) return;

        HutUpgradable releaseHut = RaycastHut(releasePos);
        if (releaseHut != null && releaseHut == pressedHut)
            OpenFor(releaseHut);
    }

    float TapMoveTolerancePixels()
    {
        float dpi = Screen.dpi;
        return dpi > 0f ? tapMoveToleranceInches * dpi : tapMoveToleranceFallbackPixels;
    }

    HutUpgradable RaycastHut(Vector2 screenPos)
    {
        if (cam == null) return null;
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
            return hit.collider.GetComponentInParent<HutUpgradable>();
        return null;
    }

    public void OpenFor(HutUpgradable hut)
    {
        if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive) return;

        AudioManager.Instance?.PlayUiElementClick();

        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>().CloseShop();
        if (SellManager.popupOpen) FindAnyObjectByType<SellManager>().CloseSellPopup();
        if (LeaderboardUI.leaderboardOpen) FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();
        if (SettingsPanelController.settingsOpen) SettingsPanelController.Instance?.CloseSettings();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        currentHut = hut;
        RefreshUI(false);
        popupOpen = true;
        panel.SetActive(true); // in case ClosePopup() deactivated it last time
        StopAllCoroutines();
        StartCoroutine(SlideTo(openX, deactivateAfter: false));
    }

    // hook this to CancelHutUpgradeButton
    public void ClosePopup()
    {
        popupOpen = false;
        StopAllCoroutines();
        StartCoroutine(SlideTo(closedX, deactivateAfter: true));
    }

    // hook this to UpgradeButton
    public void OnUpgradePressed()
    {
        if (currentHut == null) return;
        int cur = currentHut.level;
        int max = levelCapacities.Length;
        if (cur >= max) return;

        int next = cur + 1;
        long price = ApplyPlanetPriceMultiplier(levelPrices[next - 1]);
        if (!GameManager.Instance.SpendCoins(price)) return;

        ApplyLevelTo(currentHut, next);
        AudioManager.Instance?.PlayHutLevelUp();

        if (QuestManager.Instance != null)
            QuestManager.Instance.ReportHutUpgraded();

        RefreshUI(true);
    }

    // Remembers which level is currently shown, so OnValidate() (triggered
    // by dragging a slider in the Inspector) knows which entry applies —
    // it has no other way to know "what's currently displayed."
    private int currentPreviewIndex = 0;

    // Scales the preview image's RectTransform using THIS level's own
    // slider value. Called whenever the popup refreshes, and also from
    // OnValidate() below so dragging a slider updates it immediately —
    // including in Play Mode — without needing to reopen the popup.
    void ApplyPreviewScale(int index)
    {
        if (previewImage == null || previewScales == null) return;
        currentPreviewIndex = index;
        if (index < 0 || index >= previewScales.Length) return;
        previewImage.rectTransform.localScale = Vector3.one * previewScales[index];
    }

    void OnValidate()
    {
        // Keep previewScales the same length as levelPreviews, so every
        // level always has its own slider without needing to manually
        // resize the array whenever levels are added/removed.
        if (levelPreviews != null)
        {
            int targetLength = levelPreviews.Length;
            if (previewScales == null || previewScales.Length != targetLength)
            {
                float[] resized = new float[targetLength];
                for (int i = 0; i < targetLength; i++)
                    resized[i] = (previewScales != null && i < previewScales.Length) ? previewScales[i] : 1f;
                previewScales = resized;
            }
        }

        ApplyPreviewScale(currentPreviewIndex);
    }

    void RefreshUI(bool animateGauge = false)
    {
        if (currentHut == null) return;
        int cur = currentHut.level;
        int max = levelCapacities.Length;
        bool maxed = cur >= max;
        int next = maxed ? cur : cur + 1;
        int ni = next - 1;

        if (hutNameText != null) hutNameText.text = levelNames[ni];
        if (previewImage != null && levelPreviews.Length > ni && levelPreviews[ni] != null)
            previewImage.sprite = levelPreviews[ni];
        ApplyPreviewScale(ni);
        if (levelText != null) levelText.text = cur + " / " + max;
        float frac = (float)cur / max;
        if (levelGauge != null)
        {
            if (animateGauge) levelGauge.SetFill(frac);
            else levelGauge.SetFillInstant(frac);
        }
        if (capacityText != null)
            capacityText.text = GameManager.FormatCapacity(CapacityFor(cur)) + " \u2192 " + (maxed ? GameManager.FormatCapacity(CapacityFor(cur)) : GameManager.FormatCapacity(CapacityFor(next)));

        UpdateOccupancyText();
        UpdateButtonState();
    }

    void UpdateOccupancyText()
    {
        if (occupancyNumberText == null || currentHut == null) return;
        RabbitHut hut = currentHut.GetComponent<RabbitHut>();
        if (hut == null) return;
        int pct = hut.GetMaxCapacity() > 0
            ? Mathf.Min(100, (int)(hut.GetCurrentRabbits() * 100L / hut.GetMaxCapacity()))
            : 0;
        occupancyNumberText.text = pct + "%";

        Color customGreen = new Color(0f / 255f, 207f / 255f, 1f / 255f);
        if (pct >= 100)
            occupancyNumberText.color = Color.red;
        else if (pct >= 50)
            occupancyNumberText.color = Color.yellow;
        else
            occupancyNumberText.color = customGreen;

        bool yellow = pct >= 50 && pct < 100;
        occupancyNumberText.fontMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, yellow ? 0.15f : 0f);
        occupancyNumberText.fontMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
    }

    void UpdateButtonState()
    {
        if (currentHut == null) return;
        int cur = currentHut.level;
        int max = levelCapacities.Length;
        bool maxed = cur >= max;

        if (maxed)
        {
            if (upgradeButtonText != null) upgradeButtonText.text = "MAX";
            if (upgradeButton != null) upgradeButton.interactable = false;
            if (upgradeButtonImage != null) upgradeButtonImage.color = notAffordableColor;
            return;
        }

        long price = ApplyPlanetPriceMultiplier(levelPrices[cur]);   // price for next level
        bool canAfford = GameManager.Instance.totalCoins >= price;
        if (upgradeButtonText != null) upgradeButtonText.text = "Upgrade  $" + GameManager.FormatNumber(price);
        if (upgradeButton != null) upgradeButton.interactable = canAfford;
        if (upgradeButtonImage != null) upgradeButtonImage.color = canAfford ? affordableColor : notAffordableColor;
    }

    // Called by SaveManager to restore a hut's level from a saved game.
    public void ApplyLoadedHutLevel(HutUpgradable hut, int level)
    {
        ApplyLevelTo(hut, level);
    }

    void ApplyLevelTo(HutUpgradable hut, int level)
    {
        hut.SetLevel(level, CapacityFor(level));
    }

    // Final capacity for a level = level base * (1 + shop %)
    long CapacityFor(int level)
    {
        return (long)(levelCapacities[level - 1] * ShopMultiplier());
    }

    float ShopMultiplier()
    {
        float shopPct = (UpgradeManager.Instance != null)
            ? UpgradeManager.Instance.GetCategoryBonus(UpgradeManager.UpgradeCategory.HutCapacity) / 100f
            : 0f;
        float prestigePct = (PrestigeManager.Instance != null && PrestigeManager.Instance.prestigeCount > 0)
            ? PrestigeManager.Instance.HutCapacityGameplayBonusFraction()
            : 0f;
        return 1f + shopPct + prestigePct + (questCapacityBonusPercent / 100f);
    }

    // Called by QuestManager to check if applying a punish % would push total
    // capacity below the true non-upgraded base (i.e. multiplier < 1).
    public bool WouldQuestCapacityGoBelowBase(float deltaPercent)
    {
        float shopPct = (UpgradeManager.Instance != null)
            ? UpgradeManager.Instance.GetCategoryBonus(UpgradeManager.UpgradeCategory.HutCapacity) / 100f
            : 0f;
        float prestigePct = (PrestigeManager.Instance != null && PrestigeManager.Instance.prestigeCount > 0)
            ? PrestigeManager.Instance.HutCapacityGameplayBonusFraction()
            : 0f;
        float predicted = 1f + shopPct + prestigePct + ((questCapacityBonusPercent + deltaPercent) / 100f);
        return predicted < 1f;
    }

    // Called by QuestManager to actually apply a quest buff/debuff.
    public void ApplyQuestCapacityDelta(float deltaPercent)
    {
        float shopPct = (UpgradeManager.Instance != null)
            ? UpgradeManager.Instance.GetCategoryBonus(UpgradeManager.UpgradeCategory.HutCapacity) / 100f
            : 0f;
        float prestigePct = (PrestigeManager.Instance != null && PrestigeManager.Instance.prestigeCount > 0)
            ? PrestigeManager.Instance.HutCapacityGameplayBonusFraction()
            : 0f;

        float newQuestPercent = questCapacityBonusPercent + deltaPercent;
        // Hard floor: never let 1 + shopPct + prestigePct + quest%/100 drop below 1.
        float minQuestPercent = -(shopPct + prestigePct) * 100f;
        if (newQuestPercent < minQuestPercent) newQuestPercent = minQuestPercent;

        questCapacityBonusPercent = newQuestPercent;
        RecalculateAllCapacities();
    }

    // Called by the shop when hut-capacity % is bought: re-apply to every hut.
    public void RecalculateAllCapacities()
    {
        foreach (HutUpgradable h in FindObjectsByType<HutUpgradable>(FindObjectsInactive.Exclude))
            h.SetCapacity(CapacityFor(h.level));
    }

    // Called by QuestManager when a BigBang happens — wipes any accumulated
    // quest buff/debuff on hut capacity (won/lost quests don't survive prestige).
    public void ResetQuestCapacityBonus()
    {
        questCapacityBonusPercent = 0f;
        RecalculateAllCapacities();
    }

    // Used by PlanetManager to save/restore this planet's own accumulated
    // quest buff/debuff when switching planets — NOT clamped against the
    // base-value floor like ApplyQuestCapacityDelta, since this is just
    // restoring an already-valid saved number, not applying a fresh quest.
    // Per-planet price scaling: each planet further from Earth costs 50%
    // more, compounding (Earth x1, Moon x1.5, Venus x2.25, Mars x3.375,
    // Pluto x5.0625) — same rule as ShopUpgradeRow's shop upgrade pricing.
    // levelPrices[] itself stays untouched; only what the player actually
    // pays/sees is scaled.
    long ApplyPlanetPriceMultiplier(long basePrice)
    {
        if (PlanetManager.Instance == null) return basePrice;
        int planetIndex = (int)PlanetManager.Instance.currentPlanet;
        return (long)Mathf.Round(basePrice * Mathf.Pow(1.5f, planetIndex));
    }

    public float GetQuestCapacityBonusPercent() => questCapacityBonusPercent;
    public void SetQuestCapacityBonusPercent(float value)
    {
        questCapacityBonusPercent = value;
        RecalculateAllCapacities();
    }

    // Called by PrestigeManager to reset a hut back to level 1.
    public void ResetHutLevel(HutUpgradable hut)
    {
        ApplyLevelTo(hut, 1);
    }

    IEnumerator SlideTo(float targetX, bool deactivateAfter)
    {
        Vector2 start = panelRect.anchoredPosition;
        Vector2 target = new Vector2(targetX, start.y);
        float t = 0f;
        while (t < slideDuration)
        {
            t += Time.deltaTime;
            float p = t / slideDuration;
            p = p * p * (3f - 2f * p);
            panelRect.anchoredPosition = Vector2.Lerp(start, target, p);
            yield return null;
        }
        panelRect.anchoredPosition = target;

        // Fully off-screen now — stop paying per-frame cost for it (Canvas
        // rebuilds, any Update()/animations on its children, UI batching)
        // instead of leaving it enabled and just visually hidden.
        if (deactivateAfter) panel.SetActive(false);
    }
}
