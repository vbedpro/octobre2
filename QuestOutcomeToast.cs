using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;

// Reuses two duplicated groups (copied from the existing QuestPopup buff/debuff
// icon+text, so styling/color/size already match) to briefly show the outcome
// of a quest — success shows the buff group, failure shows the debuff group.
// Called from QuestManager.ApplyOutcome().
public class QuestOutcomeToast : MonoBehaviour
{
    [Header("Fade Container")]
    public CanvasGroup canvasGroup;

    [Header("Success Group (duplicated Buff_Icon + text)")]
    public GameObject successGroup;
    public Image successIcon;
    public TextMeshProUGUI successText;

    [Header("Failure Group (duplicated Debuff_Icon + text)")]
    public GameObject failureGroup;
    public Image failureIcon;
    public TextMeshProUGUI failureText;

    [Header("Timing")]
    public float fadeInDuration = 0.25f;
    public float holdDuration = 2.5f;
    public float fadeOutDuration = 0.5f;

    private Coroutine current;

    void Awake()
    {
        if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
        if (canvasGroup != null) canvasGroup.alpha = 0f;
        if (successGroup != null) successGroup.SetActive(false);
        if (failureGroup != null) failureGroup.SetActive(false);
    }

    public void Show(Sprite icon, string amountText, bool isPunishment)
    {
        // Must activate first — Awake() (which sets up canvasGroup/hides both
        // sub-groups) and StartCoroutine() below both require this object to
        // already be active; neither can run on an inactive GameObject.
        gameObject.SetActive(true);

        if (successGroup != null) successGroup.SetActive(!isPunishment);
        if (failureGroup != null) failureGroup.SetActive(isPunishment);

        if (isPunishment)
        {
            if (failureIcon != null) failureIcon.sprite = icon;
            if (failureText != null) failureText.text = amountText;
        }
        else
        {
            if (successIcon != null) successIcon.sprite = icon;
            if (successText != null) successText.text = amountText;
        }

        if (current != null) StopCoroutine(current);
        current = StartCoroutine(Animate());
    }

    IEnumerator Animate()
    {
        if (canvasGroup != null) canvasGroup.blocksRaycasts = true;
        yield return Fade(0f, 1f, fadeInDuration);
        yield return new WaitForSeconds(holdDuration);
        yield return Fade(1f, 0f, fadeOutDuration);

        // Stop blocking clicks the instant it's fully invisible, and fully
        // deactivate so nothing about it can interfere with input at all.
        if (canvasGroup != null)
        {
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
        }
        gameObject.SetActive(false);

        current = null;
    }

    IEnumerator Fade(float from, float to, float duration)
    {
        if (canvasGroup == null) yield break;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t / duration));
            yield return null;
        }
        canvasGroup.alpha = to;
    }
}
