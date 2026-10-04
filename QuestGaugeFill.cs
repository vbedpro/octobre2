using UnityEngine;
using UnityEngine.UI;

// Passive visual gauge — no internal timer. QuestManager owns the countdown
// and pushes the current fraction in every frame via SetFraction(). This
// avoids the gauge silently pausing whenever its GameObject (e.g. inside a
// panel) gets deactivated.
//
// Uses the same technique as GaugeBar: resizes this RectTransform's WIDTH
// directly instead of relying on Image.fillAmount. This avoids needing a
// "Filled" Image Type + sprite (which causes pixelation when stretched),
// and — as long as this object's anchor/pivot X is set to LEFT (0) — the
// bar shrinks from the right edge only, with the left edge staying fixed.
[RequireComponent(typeof(RectTransform))]
public class QuestGaugeFill : MonoBehaviour
{
    [Header("References")]
    public Image fillImage; // used only for color tint, not fillAmount

    [Header("Colors (3-stop gradient: full -> mid -> empty)")]
    [Tooltip("Color at fraction = 1 (full time remaining).")]
    public Color startColor = new Color32(0x17, 0xD1, 0x00, 0xFF); // 17D100 green
    [Tooltip("Color at fraction = 0.5 (halfway).")]
    public Color midColor   = new Color32(0xFD, 0xFF, 0x00, 0xFF); // FDFF00 yellow
    [Tooltip("Color at fraction = 0 (time's up).")]
    public Color endColor   = new Color32(0xE6, 0x1A, 0x1A, 0xFF); // E61A1A red

    [Header("Shrink Width (GaugeBar-style — left anchored, shrinks from the right)")]
    [Tooltip("Width at fraction = 1 (full time remaining). Leave 0 to auto-capture this object's own width on Awake.")]
    public float fullWidth = 0f;

    private RectTransform rect;

    void Awake()
    {
        rect = GetComponent<RectTransform>();
        if (fullWidth <= 0f && rect != null)
            fullWidth = rect.rect.width;
    }

    public void SetFraction(float frac)
    {
        frac = Mathf.Clamp01(frac);

        if (fillImage != null)
            fillImage.color = EvaluateGradient(frac);

        if (rect != null)
            rect.sizeDelta = new Vector2(fullWidth * frac, rect.sizeDelta.y);
    }

    Color EvaluateGradient(float frac)
    {
        // frac in [0.5, 1] -> lerp between midColor and startColor
        // frac in [0, 0.5] -> lerp between endColor and midColor
        if (frac >= 0.5f)
        {
            float t = (frac - 0.5f) / 0.5f; // 0 at mid, 1 at full
            return Color.Lerp(midColor, startColor, t);
        }
        else
        {
            float t = frac / 0.5f; // 0 at empty, 1 at mid
            return Color.Lerp(endColor, midColor, t);
        }
    }
}
