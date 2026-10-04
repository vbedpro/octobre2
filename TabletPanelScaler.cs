using UnityEngine;

// Attach directly to any UI PANEL (not the whole Canvas) — shrinks (and
// optionally repositions) only that one panel on tablet-shaped screens
// (iPads especially), leaving every other UI element, and phones
// entirely, completely untouched. Reusable across multiple panels —
// e.g. the Leaderboard and the Shop both use this, each with their own
// independent tuning via the fields below.
public class TabletPanelScaler : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Screens with an aspect ratio (long side / short side) BELOW this are treated as tablet-shaped. Typical phones are ~2.0-2.2, iPads are ~1.33-1.5 — 1.6 is a safe dividing line.")]
    public float tabletAspectRatioThreshold = 1.6f;

    [Header("Tablet Shrink")]
    [Tooltip("Uniform scale applied to this panel ONLY on tablet-shaped screens. 0.75 = 75% size.")]
    [Range(0.4f, 1f)]
    public float tabletScaleMultiplier = 0.75f;

    [Tooltip("Vertical offset (in the panel's local units) applied ONLY on tablet-shaped screens, after scaling. Positive moves up, negative moves down.")]
    public float tabletVerticalOffset = 0f;

    void Awake()
    {
        float longSide = Mathf.Max(Screen.width, Screen.height);
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float aspectRatio = longSide / shortSide;

        bool isTablet = aspectRatio < tabletAspectRatioThreshold;
        if (!isTablet) return; // phones: leave this panel exactly as designed

        transform.localScale *= tabletScaleMultiplier;

        RectTransform rect = transform as RectTransform;
        if (rect != null)
            rect.anchoredPosition += new Vector2(0f, tabletVerticalOffset);
        else
            transform.position += new Vector3(0f, tabletVerticalOffset, 0f);
    }
}
