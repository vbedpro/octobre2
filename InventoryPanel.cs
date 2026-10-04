using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Populates the Inventory panel's square rows from whatever the player
// currently owns (InventoryManager) matched against the list of known
// BoostDefinition assets below. Slides in/out and participates in the
// project's existing "only one panel open at a time" convention — same
// pattern as SettingsPanelController/AchievementsPanelUI/etc.
public class InventoryPanel : MonoBehaviour
{
    // Same convention every other panel in this project uses
    // (ShopManager.shopOpen, SellManager.popupOpen, etc.).
    public static bool inventoryOpen = false;

    [Header("Known Boosts")]
    [Tooltip("Every BoostDefinition asset that can appear in this panel.")]
    public List<BoostDefinition> allDefinitions;

    [Header("Known Cosmetics")]
    [Tooltip("Every CosmeticDefinition asset that can appear in this panel — shown below the boost rows.")]
    public List<CosmeticDefinition> allCosmeticDefinitions;

    [Tooltip("If true, a row shows even at 0 owned (greyed count). If false, rows only appear once the player owns at least 1.")]
    public bool alwaysShowAllDefinitions = false;

    [System.Serializable]
    public class PlanetIconEntry
    {
        public PlanetManager.Planet planet;
        public Sprite icon;
    }

    [Header("Planet Icons (for planet-specific boost rows)")]
    public List<PlanetIconEntry> planetIcons;

    [Header("Prefab / Layout")]
    public InventoryItemRow rowPrefab;
    public Transform rowParent;

    [Header("Cosmetic Prefab (different visual than boost rows — Equip button instead of Use)")]
    public InventoryItemRow cosmeticRowPrefab;
    public Transform cosmeticRowParent;

    [Header("Slide")]
    public GameObject panelObject;
    public RectTransform panelRect;
    [Tooltip("Position while open, on screen.")]
    public Vector2 openPosition;
    [Tooltip("Position while closed, off-screen.")]
    public Vector2 closedPosition;
    public float slideDuration = 0.35f;

    [Header("Button Fades (hidden while inventory panel is open)")]
    [Tooltip("The button that opens the inventory panel itself.")]
    public CanvasGroup inventoryButtonCanvasGroup;
    public CanvasGroup leaderboardButtonCanvasGroup;
    public CanvasGroup achievementButtonCanvasGroup;
    public CanvasGroup bigBangButtonCanvasGroup;
    public CanvasGroup settingsButtonCanvasGroup;
    public float buttonFadeDuration = 0.25f;
    private Coroutine inventoryButtonFadeCo;
    private Coroutine leaderboardButtonFadeCo;
    private Coroutine achievementButtonFadeCo;
    private Coroutine bigBangButtonFadeCo;
    private Coroutine settingsButtonFadeCo;

    private Coroutine slideCoroutine;
    private readonly List<InventoryItemRow> pool = new List<InventoryItemRow>();
    private readonly List<InventoryItemRow> cosmeticPool = new List<InventoryItemRow>();

    void Start()
    {
        // NOTE: if this script lives on the panel object itself (which
        // starts inactive), Start() only actually runs the FIRST time
        // OpenPanel() activates it — meaning it would run right AFTER
        // OpenPanel() already opened things, immediately re-closing it.
        // Guard against that: only force-close here if the panel isn't
        // already mid-opening.
        if (!inventoryOpen)
        {
            if (panelObject != null) panelObject.SetActive(false);
            if (panelRect != null) panelRect.anchoredPosition = closedPosition;
        }
    }

    void OnEnable()
    {
        if (InventoryManager.Instance != null)
            InventoryManager.Instance.OnInventoryChanged += OnInventoryChanged;
        if (BoostActivationManager.Instance != null)
        {
            BoostActivationManager.Instance.OnBoostActivated += OnBoostActivated;
            BoostActivationManager.Instance.OnBoostExpired += OnBoostExpired;
        }
        if (CosmeticEquipManager.Instance != null)
            CosmeticEquipManager.Instance.OnEquipChanged += OnEquipChanged;
    }

    void OnDisable()
    {
        if (InventoryManager.Instance != null)
            InventoryManager.Instance.OnInventoryChanged -= OnInventoryChanged;
        if (BoostActivationManager.Instance != null)
        {
            BoostActivationManager.Instance.OnBoostActivated -= OnBoostActivated;
            BoostActivationManager.Instance.OnBoostExpired -= OnBoostExpired;
        }
        if (CosmeticEquipManager.Instance != null)
            CosmeticEquipManager.Instance.OnEquipChanged -= OnEquipChanged;
    }

    public Sprite GetIconFor(PlanetManager.Planet planet)
    {
        if (planetIcons == null) return null;
        foreach (PlanetIconEntry entry in planetIcons)
        {
            if (entry.planet == planet) return entry.icon;
        }
        return null;
    }

    // Wire your Inventory button's OnClick() to this.
    public void OpenPanel()
    {
        CloseOtherUI();

        inventoryOpen = true;
        if (panelObject != null) panelObject.SetActive(true);

        Refresh();

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideTo(openPosition));

        FadeInventoryButton(0f);
        FadeLeaderboardButton(0f);
        FadeAchievementButton(0f);
        FadeBigBangButton(0f);
        FadeSettingsButton(0f);
    }

    // Wire your panel's Close/X button OnClick() to this.
    public void ClosePanel()
    {
        inventoryOpen = false;

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideToThenDeactivate(closedPosition));

        FadeInventoryButton(1f);
        FadeLeaderboardButton(1f);
        FadeAchievementButton(1f);
        FadeBigBangButton(1f);
        FadeSettingsButton(1f);
    }

    // Closes every other panel before Inventory opens — same convention
    // every other panel in this project already follows for each other.
    void CloseOtherUI()
    {
        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (SellManager.popupOpen) FindAnyObjectByType<SellManager>()?.CloseSellPopup();
        if (HutUpgradeManager.popupOpen) FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();
        if (LeaderboardUI.leaderboardOpen) FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (SettingsPanelController.settingsOpen) SettingsPanelController.Instance?.CloseSettings();
        if (PrestigeManager.popupOpen) FindAnyObjectByType<PrestigeManager>()?.CloseBigBangPopup();
        FindAnyObjectByType<PlanetColumnUI>()?.CloseColumn(); // safe no-op if already closed
        FindAnyObjectByType<QuestManager>()?.ForceCloseQuestUI(); // closes QuestPopup + ActiveQuestPanel visually only
    }

    void FadeInventoryButton(float targetAlpha)
    {
        if (inventoryButtonCanvasGroup == null) return;
        if (inventoryButtonFadeCo != null) StopCoroutine(inventoryButtonFadeCo);
        inventoryButtonFadeCo = StartCoroutine(FadeCanvasGroupRoutine(inventoryButtonCanvasGroup, targetAlpha));
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

    IEnumerator SlideTo(Vector2 target)
    {
        if (panelRect == null) yield break;

        Vector2 start = panelRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / slideDuration;
            t = t * t * (3f - 2f * t);
            panelRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }
        panelRect.anchoredPosition = target;
    }

    IEnumerator SlideToThenDeactivate(Vector2 target)
    {
        yield return SlideTo(target);
        if (panelObject != null) panelObject.SetActive(false);
    }

    void OnInventoryChanged(string itemId)
    {
        Refresh();
    }

    void OnBoostExpired(string itemId)
    {
        Refresh();
    }

    void OnBoostActivated(string itemId)
    {
        Refresh();
    }

    void OnEquipChanged(CosmeticType type)
    {
        Refresh();
    }

    void Refresh()
    {
        if (InventoryManager.Instance == null || rowPrefab == null || rowParent == null) return;

        int visibleIndex = 0;

        foreach (BoostDefinition def in allDefinitions)
        {
            if (def == null || string.IsNullOrEmpty(def.itemId)) continue;

            int count = InventoryManager.Instance.GetCount(def.itemId);
            bool isActive = BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive(def.itemId);

            // Keep showing the row while the boost is actively running
            // (so its countdown stays visible), even if using it just
            // dropped the owned count to 0. Only actually hides once BOTH
            // the count is 0 AND the boost isn't currently active.
            if (count <= 0 && !isActive && !alwaysShowAllDefinitions) continue;

            InventoryItemRow row = GetOrCreateRow(visibleIndex);
            row.gameObject.SetActive(true);
            row.Setup(def, count, this);
            visibleIndex++;
        }

        // Hide any leftover pooled boost rows from a previous, larger refresh.
        for (int i = visibleIndex; i < pool.Count; i++)
            pool[i].gameObject.SetActive(false);

        // Cosmetics — separate prefab (Equip button instead of Use), own
        // pool, shown below boosts, only while owned. Sorted by
        // displayOrder so the layout is always the same regardless of the
        // order the player actually unlocked them in.
        int cosmeticVisibleIndex = 0;
        if (allCosmeticDefinitions != null)
        {
            allCosmeticDefinitions.Sort((a, b) =>
            {
                int orderA = a != null ? a.displayOrder : int.MaxValue;
                int orderB = b != null ? b.displayOrder : int.MaxValue;
                return orderA.CompareTo(orderB);
            });

            foreach (CosmeticDefinition def in allCosmeticDefinitions)
            {
                if (def == null || string.IsNullOrEmpty(def.itemId)) continue;

                int count = InventoryManager.Instance.GetCount(def.itemId);
                if (count <= 0 && !alwaysShowAllDefinitions) continue;

                InventoryItemRow row = GetOrCreateCosmeticRow(cosmeticVisibleIndex);
                row.gameObject.SetActive(true);
                row.Setup(def, count, this);
                cosmeticVisibleIndex++;
            }
        }

        for (int i = cosmeticVisibleIndex; i < cosmeticPool.Count; i++)
            cosmeticPool[i].gameObject.SetActive(false);
    }

    InventoryItemRow GetOrCreateRow(int index)
    {
        if (index < pool.Count) return pool[index];

        InventoryItemRow row = Instantiate(rowPrefab, rowParent);
        pool.Add(row);
        return row;
    }

    InventoryItemRow GetOrCreateCosmeticRow(int index)
    {
        if (index < cosmeticPool.Count) return cosmeticPool[index];

        // Cosmetics have their own scroll viewport (COSMETICS_ROW), separate
        // from the boost row parent.
        InventoryItemRow row = Instantiate(cosmeticRowPrefab, cosmeticRowParent);
        cosmeticPool.Add(row);
        return row;
    }
}
