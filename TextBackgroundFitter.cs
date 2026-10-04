using UnityEngine;
using TMPro;

// Resizes Background Rect's width to hug Target Text's actual rendered
// width (+ padding), so a short "3" gets a small white panel and a long
// nickname gets a wide one — instead of one fixed-size panel that's too
// wide for short values or too narrow for long ones.
//
// Add ONE of these per element (Rank, Nickname, BigBangs, Rabbits) on both
// the Row and PersonalRow prefabs. Skip it for the Country flag icon —
// flags are a fixed size regardless of country, so that panel can just
// stay a plain static-size Image with no script needed.
//
// SETUP: Background Rect's anchor/pivot should match Target Text's
// horizontal center (so it grows/shrinks evenly on both sides). Simplest:
// give Background Rect the same X anchor position as the text, pivot.x =
// 0.5, and let this script drive sizeDelta.x every frame.
public class TextBackgroundFitter : MonoBehaviour
{
    public TMP_Text targetText;
    public RectTransform backgroundRect;

    [Tooltip("Extra width added on each side beyond the text's actual rendered width.")]
    public float horizontalPadding = 10f;
    [Tooltip("Panel never shrinks below this width, even for very short text (e.g. a single digit).")]
    public float minWidth = 0f;

    // Runs via Canvas.willRenderCanvases (same technique used for the
    // nickname/background cosmetics) instead of Update(), so it always
    // reads the FINAL text width for this frame — after any TMP layout
    // rebuild — rather than possibly reading a stale width from before
    // the text changed this same frame.
    void OnEnable()
    {
        Canvas.willRenderCanvases += Fit;
    }

    void OnDisable()
    {
        Canvas.willRenderCanvases -= Fit;
    }

    void Fit()
    {
        if (targetText == null || backgroundRect == null) return;

        float width = Mathf.Max(minWidth, targetText.preferredWidth + horizontalPadding * 2f);
        Vector2 sizeDelta = backgroundRect.sizeDelta;
        if (!Mathf.Approximately(sizeDelta.x, width))
            backgroundRect.sizeDelta = new Vector2(width, sizeDelta.y);
    }
}
