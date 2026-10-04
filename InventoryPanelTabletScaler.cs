using UnityEngine;
using UnityEngine.UI;

// Dedicated tablet scaler for the Inventory panel only.
// Attach this directly to the Inventory panel's root GameObject (the one
// that is an ancestor of the ScrollRect's Viewport/Content) — not to
// InventoryPanel_BG unless that IS the panel root.
public class InventoryPanelTabletScaler : MonoBehaviour
{
    [Range(1f, 3f)]
    public float tabletAspectRatioThreshold = 1.6f;

    [Range(0.4f, 1f)]
    public float tabletScaleMultiplier = 0.75f;

    public float tabletVerticalOffset = 0f;

    [Header("Viewport Masks (optional)")]
    [Tooltip("Drag the RectMask2D on the boosts ScrollRect's Viewport here.")]
    public RectMask2D boostsViewportMask;
    public Vector4 boostsMaskPadding = Vector4.zero;

    [Tooltip("Drag the RectMask2D on the cosmetics ScrollRect's Viewport here.")]
    public RectMask2D cosmeticsViewportMask;
    public Vector4 cosmeticsMaskPadding = Vector4.zero;

    bool scaleApplied = false;
    Vector4 lastBoostsPadding;
    Vector4 lastCosmeticsPadding;

    void Awake()
    {
        float aspectRatio = Mathf.Max(Screen.width, Screen.height) / (float)Mathf.Min(Screen.width, Screen.height);

        if (aspectRatio < tabletAspectRatioThreshold)
        {
            transform.localScale *= tabletScaleMultiplier;

            RectTransform rect = transform as RectTransform;
            if (rect != null)
            {
                rect.anchoredPosition += new Vector2(0f, tabletVerticalOffset);
            }
            else
            {
                transform.position += new Vector3(0f, tabletVerticalOffset, 0f);
            }

            scaleApplied = true;
        }

        ApplyMaskPadding();
    }

    // Lets you drag the padding values in the Inspector while in Play Mode
    // and see the viewport clip update immediately, instead of stopping,
    // editing, and re-entering Play Mode each time.
    void Update()
    {
        if (boostsMaskPadding != lastBoostsPadding || cosmeticsMaskPadding != lastCosmeticsPadding)
        {
            ApplyMaskPadding();
        }
    }

    void ApplyMaskPadding()
    {
        if (boostsViewportMask != null)
            boostsViewportMask.padding = boostsMaskPadding;

        if (cosmeticsViewportMask != null)
            cosmeticsViewportMask.padding = cosmeticsMaskPadding;

        lastBoostsPadding = boostsMaskPadding;
        lastCosmeticsPadding = cosmeticsMaskPadding;
    }
}
