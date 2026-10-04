using UnityEngine;
using System.Collections;

// Generic slide-in/out helper, same easing pattern as SellManager/ShopManager.
// Attach to any RectTransform (popup, panel, or button) that needs to slide
// on/off screen. Call Show() / Hide() from QuestManager.
public class QuestSlidePanel : MonoBehaviour
{
    [Header("Positions")]
    public RectTransform rect;
    public Vector2 openPos;
    public Vector2 closedPos;
    public float slideDuration = 0.4f;

    [Header("Start State")]
    public bool startClosed = true;

    private Coroutine current;

    public bool IsOpen { get; private set; }

    private bool initialized = false;

    void Awake()
    {
        if (rect == null) rect = GetComponent<RectTransform>();
        InitializePosition();
    }

    void InitializePosition()
    {
        if (initialized) return;
        initialized = true;

        if (startClosed)
        {
            rect.anchoredPosition = closedPos;
            IsOpen = false;
        }
        else
        {
            rect.anchoredPosition = openPos;
            IsOpen = true;
        }
    }

    public void Show()
    {
        if (rect == null) rect = GetComponent<RectTransform>();
        gameObject.SetActive(true);
        if (rect == null) return; // nothing to animate, but at least don't throw
        SlideTo(openPos);
        IsOpen = true;
    }

    public void Hide(bool deactivateAfter = false)
    {
        if (rect == null) rect = GetComponent<RectTransform>();
        if (rect == null) return; // still nothing to move — bail safely instead of throwing

        if (!gameObject.activeInHierarchy)
        {
            rect.anchoredPosition = closedPos;
            IsOpen = false;
            return;
        }

        SlideTo(closedPos, deactivateAfter);
        IsOpen = false;
    }

    void SlideTo(Vector2 target, bool deactivateAfter = false)
    {
        if (current != null) StopCoroutine(current);
        current = StartCoroutine(SlideRoutine(target, deactivateAfter));
    }

    IEnumerator SlideRoutine(Vector2 target, bool deactivateAfter)
    {
        Vector2 start = rect.anchoredPosition;
        float elapsed = 0f;

        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / slideDuration);
            t = t * t * (3f - 2f * t); // smoothstep, matches existing popups
            rect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }

        rect.anchoredPosition = target;
        if (deactivateAfter) gameObject.SetActive(false);
    }
}
