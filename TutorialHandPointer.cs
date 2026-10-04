using UnityEngine;

public enum HandAnimStyle { Pulse, SlideLeftRight }

// A single reusable pointing-hand icon, repositioned and re-animated by
// TutorialManager for each step. Loops infinitely until ShowAt() is
// called again (new step) or Hide() is called (tutorial complete).
//
// IMPORTANT: continuously re-reads the target's LIVE position every frame
// in Update() — never just snapshots it once. Several tutorial targets
// (the shop's buy button, the sell popup's slider/confirm button, etc.)
// sit inside panels that slide/animate into view AFTER ShowAt() is
// called (e.g. the target starts off-screen while the shop panel is
// closed, then slides in). Snapshotting position once would leave the
// hand stuck at the target's STARTING position, not following it in.
public class TutorialHandPointer : MonoBehaviour
{
    public RectTransform handRect;

    [Header("Pulse Style (points at a fixed button)")]
    public float pulseDistance = 15f;
    public float pulseDuration = 0.6f;

    [Header("Slide Style (used for the sell slider step)")]
    public float slideDistance = 80f;
    public float slideDuration = 1f;

    private RectTransform currentTarget;
    private Vector2 currentOffset;
    private HandAnimStyle currentStyle;
    private bool isShowing = false;
    private float animTimer = 0f;

    public void ShowAt(RectTransform target, HandAnimStyle style, Vector2 offset, float rotationOffset)
    {
        if (target == null) return;

        currentTarget = target;
        currentOffset = offset;
        currentStyle = style;
        animTimer = 0f;
        isShowing = true;

        gameObject.SetActive(true);
        handRect.rotation = Quaternion.Euler(0f, 0f, rotationOffset);
        // Position gets set every frame in Update() below, not just here —
        // this initial call just avoids a one-frame pop at (0,0) before
        // the first Update() runs.
        UpdatePosition();
    }

    public void Hide()
    {
        isShowing = false;
        currentTarget = null;
        gameObject.SetActive(false);
    }

    void Update()
    {
        if (!isShowing || currentTarget == null) return;

        animTimer += Time.deltaTime;
        UpdatePosition();
    }

    // Re-reads currentTarget's LIVE position every call, then layers the
    // current animation style's offset on top — this is what makes the
    // hand correctly follow a target that's sliding/animating into view.
    void UpdatePosition()
    {
        Vector3 basePos = currentTarget.position + new Vector3(currentOffset.x, currentOffset.y, 0f);

        Vector3 animatedOffset = currentStyle == HandAnimStyle.SlideLeftRight
            ? GetSlideOffset()
            : GetPulseOffset();

        handRect.position = basePos + animatedOffset;
    }

    // Simple bob, looping forever — reads as "tap here."
    Vector3 GetPulseOffset()
    {
        float t = Mathf.PingPong(animTimer / pulseDuration * 2f, 1f);
        t = t * t * (3f - 2f * t); // smoothstep
        return Vector3.down * pulseDistance * t;
    }

    // Slides left-to-right and back, looping forever — used for the sell
    // slider step to indicate "drag this."
    Vector3 GetSlideOffset()
    {
        float t = Mathf.PingPong(animTimer / slideDuration, 1f);
        t = t * t * (3f - 2f * t); // smoothstep
        float x = Mathf.Lerp(-slideDistance * 0.5f, slideDistance * 0.5f, t);
        return new Vector3(x, 0f, 0f);
    }
}
