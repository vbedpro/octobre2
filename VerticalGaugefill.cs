using UnityEngine;
using System.Collections;

// Vertical counterpart to GaugeBar — same technique (resizing a RectTransform
// directly instead of relying on Image.fillAmount), but fills bottom -> top
// instead of left -> right.
//
// SETUP: this object's Fill child must be anchored/pivoted at the BOTTOM
// (anchorMin.y = 0, anchorMax.y = 0, pivot.y = 0) so growing its height
// only extends upward from a fixed bottom edge.
[RequireComponent(typeof(RectTransform))]
public class VerticalGaugeFill : MonoBehaviour
{
    [Tooltip("The fill bar. Its anchor + pivot must be on the BOTTOM.")]
    public RectTransform fill;

    [Tooltip("Height at 100%. Leave 0 to use this object's own height.")]
    public float fullHeight = 0f;

    public float animDuration = 0.3f;

    private Coroutine anim;

    private float FullHeight => fullHeight > 0f ? fullHeight : ((RectTransform)transform).rect.height;

    // Snap immediately (used on initial setup / refresh without animation).
    public void SetFillInstant(float fraction)
    {
        fraction = Mathf.Clamp01(fraction);
        if (anim != null) { StopCoroutine(anim); anim = null; }
        if (fill != null)
            fill.sizeDelta = new Vector2(fill.sizeDelta.x, FullHeight * fraction);
    }

    // Animate the grow/shrink (used when a level is bought).
    public void SetFill(float fraction)
    {
        fraction = Mathf.Clamp01(fraction);
        if (anim != null) StopCoroutine(anim);
        anim = StartCoroutine(Animate(fraction));
    }

    private IEnumerator Animate(float target)
    {
        float startH = fill != null ? fill.sizeDelta.y : 0f;
        float endH = FullHeight * target;
        float t = 0f;
        while (t < animDuration)
        {
            t += Time.deltaTime;
            float p = t / animDuration;
            p = p * p * (3f - 2f * p);   // ease in-out
            if (fill != null)
                fill.sizeDelta = new Vector2(fill.sizeDelta.x, Mathf.Lerp(startH, endH, p));
            yield return null;
        }
        if (fill != null) fill.sizeDelta = new Vector2(fill.sizeDelta.x, endH);
        anim = null;
    }
}
