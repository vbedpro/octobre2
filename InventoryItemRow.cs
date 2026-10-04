using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One square slot in the Inventory panel. Populated by InventoryPanel with
// either a BoostDefinition or a CosmeticDefinition, plus however many the
// player owns.
public class InventoryItemRow : MonoBehaviour
{
    [Header("UI")]
    public Image iconImage;
    public TextMeshProUGUI countText;
    public TextMeshProUGUI descriptionText;
    public Button useButton;
    public Image useButtonImage;
    public TextMeshProUGUI useButtonLabel;
    public TextMeshProUGUI timerText;

    [Header("Planet Icon (top-left, only shown for active planet-specific boosts)")]
    public Image planetIconImage;

    [Header("Equip Button (cosmetic rows only)")]
    public Button equipButton;
    public Image equipButtonBackground;
    public TextMeshProUGUI equipButtonLabel;
    public Color equippedColor = new Color(0.8f, 0.2f, 0.2f);
    public Color unequippedColor = new Color(0.2f, 0.8f, 0.2f);
    public string equipText = "Equip";
    public string disableText = "Disable";

    [Header("Use Button Colors")]
    public Color readyColor  = new Color(0.2f, 0.8f, 0.2f);
    public Color activeColor = new Color(0.5f, 0.5f, 0.5f);

    private BoostDefinition definition;
    private CosmeticDefinition cosmeticDefinition;
    private InventoryPanel panel;

    // Cycles iconImage's sprite for animated cosmetics (icon PNG sequence),
    // same technique as CosmeticAchievementRowUI — static cosmetics just
    // paint once via def.icon.
    void OnEnable()
    {
        Canvas.willRenderCanvases += ApplyCosmeticIconFrame;
    }

    void OnDisable()
    {
        Canvas.willRenderCanvases -= ApplyCosmeticIconFrame;
    }

    void ApplyCosmeticIconFrame()
    {
        if (iconImage == null || cosmeticDefinition == null) return;
        CosmeticDefinition def = cosmeticDefinition;

        if (def.iconAnimationFrames != null && def.iconAnimationFrames.Length > 0)
        {
            float fps = Mathf.Max(def.iconFrameRate, 0.01f);
            int frameIndex = Mathf.FloorToInt(Time.time * fps) % def.iconAnimationFrames.Length;
            Sprite frame = def.iconAnimationFrames[frameIndex];
            if (frame != null && iconImage.sprite != frame)
                iconImage.sprite = frame;
        }
        else if (def.icon != null)
        {
            if (iconImage.sprite != def.icon)
                iconImage.sprite = def.icon;
        }
    }

    // Boost row — same as before.
    public void Setup(BoostDefinition def, int count, InventoryPanel owningPanel)
    {
        definition = def;
        cosmeticDefinition = null;
        panel = owningPanel;

        if (iconImage != null) iconImage.sprite = def.icon;
        if (descriptionText != null) descriptionText.text = def.description;

        // Cosmetics hide these; make sure a recycled row shows them again
        // when it goes back to displaying a boost.
        if (useButton != null) useButton.gameObject.SetActive(true);
        if (timerText != null) timerText.gameObject.SetActive(false);

        RefreshCount(count);
    }

    // Cosmetic row — no Use button, no timer, no activation state.
    public void Setup(CosmeticDefinition def, int count, InventoryPanel owningPanel)
    {
        definition = null;
        cosmeticDefinition = def;
        panel = owningPanel;

        if (iconImage != null) ApplyCosmeticIconFrame();
        if (descriptionText != null) descriptionText.text = def.description;
        if (timerText != null) timerText.gameObject.SetActive(false);
        if (planetIconImage != null) planetIconImage.gameObject.SetActive(false);

        if (equipButton != null)
        {
            equipButton.onClick.RemoveAllListeners();
            equipButton.onClick.AddListener(OnEquipPressed);
        }
        RefreshEquipVisual();

        RefreshCount(count);
    }

    void RefreshEquipVisual()
    {
        if (cosmeticDefinition == null) return;

        bool isEquipped = CosmeticEquipManager.Instance != null
            && CosmeticEquipManager.Instance.IsEquipped(cosmeticDefinition.itemId, cosmeticDefinition.cosmeticType);

        if (equipButtonBackground != null)
            equipButtonBackground.color = isEquipped ? equippedColor : unequippedColor;
        if (equipButtonLabel != null)
            equipButtonLabel.text = isEquipped ? disableText : equipText;
    }

    // Wire this to the row's Equip/Disable button OnClick() (cosmetic rows only).
    public void OnEquipPressed()
    {
        if (cosmeticDefinition == null || CosmeticEquipManager.Instance == null) return;
        CosmeticEquipManager.Instance.ToggleEquip(cosmeticDefinition.itemId, cosmeticDefinition.cosmeticType);
        RefreshEquipVisual();

        AudioManager.Instance?.PlayUiElementClick();
    }

    public void RefreshCount(int count)
    {
        if (countText != null) countText.text = "x" + count;
    }

    void Update()
    {
        // Cosmetic rows have nothing dynamic to update (no activation,
        // no timer, no planet-specific state) — skip all of this.
        if (definition == null) return;

        bool active = BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive(definition.itemId);

        // Use button and timer are mutually exclusive: button shows when
        // idle, timer shows when active.
        if (useButton != null) useButton.gameObject.SetActive(!active);

        if (timerText != null)
        {
            timerText.gameObject.SetActive(active);

            if (active)
            {
                float remaining = BoostActivationManager.Instance.GetTimeRemaining(definition.itemId);
                int minutes = Mathf.FloorToInt(remaining / 60f);
                int seconds = Mathf.FloorToInt(remaining % 60f);
                timerText.text = $"{minutes:00}:{seconds:00}";
            }
        }

        // Planet icon — only visible for a planet-specific boost that's
        // currently active, showing whichever planet it was activated on.
        if (planetIconImage != null)
        {
            PlanetManager.Planet? activePlanet = (active && definition.isPlanetSpecific && BoostActivationManager.Instance != null)
                ? BoostActivationManager.Instance.GetActivePlanetFor(definition.itemId)
                : null;

            if (activePlanet.HasValue)
            {
                Sprite icon = panel != null ? panel.GetIconFor(activePlanet.Value) : null;
                planetIconImage.sprite = icon;
                planetIconImage.gameObject.SetActive(icon != null);
            }
            else
            {
                planetIconImage.gameObject.SetActive(false);
            }
        }
    }

    // Wire this to the row's Use button OnClick().
    public void OnUsePressed()
    {
        if (definition == null || BoostActivationManager.Instance == null) return;
        BoostActivationManager.Instance.TryActivate(definition);
        // Count display updates next time InventoryPanel refreshes
        // (it listens to InventoryManager.OnInventoryChanged).

        // Called from code rather than wired on the prefab's Button
        // OnClick() — a prefab asset can't hold a reference to AudioManager
        // (a scene object), so AudioManager.Instance is resolved here at
        // runtime instead, same as every other SFX call in the project.
        AudioManager.Instance?.PlayBoostBought();
    }
}
