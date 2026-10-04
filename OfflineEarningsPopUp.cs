using UnityEngine;
using TMPro;
using System.Collections;

// Simple "welcome back" popup shown once, on launch, if the player earned
// any offline rabbits. Subscribes to SaveManager.OnOfflineEarningsCalculated
// on Awake() — must be attached to an always-active GameObject (same
// requirement as other popups in this project) so it's guaranteed to be
// listening before SaveManager fires the event one frame later.
//
// Appears ALREADY fully visible/open on launch (no slide-in animation) —
// snaps straight to QuestSlidePanel's open position instead of animating
// there. Closing it, however, DOES slide out, reusing QuestSlidePanel —
// the same component every other popup in this project already uses —
// so it matches the rest of the UI's exit behavior.
public class OfflineEarningsPopup : MonoBehaviour
{
    public static bool popupOpen = false;

    [Header("Panel")]
    [Tooltip("Attach QuestSlidePanel to the SAME object this popup slides — reused here purely for its animated Hide(), since the open state is snapped instantly rather than animated in.")]
    public QuestSlidePanel slidePanel;
    public TextMeshProUGUI amountText;

    [Header("Info Text (\"You have X minutes of offline earnings.\")")]
    [Tooltip("The 'X minutes' portion is colored via rich text and updates based on Night Shift level — 0 levels = 30 minutes, +5 min per level.")]
    public TextMeshProUGUI infoText;
    public Color minutesColor = new Color(0f, 0.44f, 1f);

    [Header("Dim Overlay")]
    public CanvasGroup dimOverlay;
    public float dimAlpha = 0.6f;
    public float dimFadeDuration = 0.4f;
    private Coroutine dimCoroutine;

    void Awake()
    {
        if (slidePanel != null) slidePanel.gameObject.SetActive(false);
        SaveManager.OnOfflineEarningsCalculated += Show;
    }

    void OnDestroy()
    {
        SaveManager.OnOfflineEarningsCalculated -= Show;
    }

    void Show(long amountEarned)
    {
        if (amountText != null)
            amountText.text = GameManager.FormatFullNumber(amountEarned);

        UpdateInfoText();

        if (slidePanel != null)
        {
            // Snap directly to the open position — no slide-in animation,
            // per design. gameObject.SetActive() must happen before
            // touching rect, same ordering requirement every other popup
            // in this project follows.
            slidePanel.gameObject.SetActive(true);
            if (slidePanel.rect != null)
                slidePanel.rect.anchoredPosition = slidePanel.openPos;
        }

        popupOpen = true;
        if (dimOverlay != null)
        {
            dimOverlay.blocksRaycasts = true;
            dimOverlay.interactable = true;
        }
        FadeDim(dimAlpha);
    }

    void UpdateInfoText()
    {
        if (infoText == null) return;

        float baseCapSeconds = SaveManager.Instance != null ? SaveManager.Instance.offlineEarningsCapSeconds : 1800f;
        int nightShiftLevel = EpicUpgradeManager.Instance != null
            ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.NightShift)
            : 0;
        float totalSeconds = baseCapSeconds + (nightShiftLevel * 5f * 60f);
        int totalMinutes = Mathf.RoundToInt(totalSeconds / 60f);

        string hex = ColorUtility.ToHtmlStringRGB(minutesColor);
        infoText.text = "You have <color=#" + hex + ">" + totalMinutes + " minutes</color> of offline earnings.";
    }

    // Hook this to the OK/close button.
    public void Close()
    {
        popupOpen = false;
        if (dimOverlay != null)
        {
            dimOverlay.blocksRaycasts = false;
            dimOverlay.interactable = false;
        }
        FadeDim(0f);

        if (slidePanel != null)
            slidePanel.Hide(deactivateAfter: true); // animated slide-out, matching every other popup
    }

    void FadeDim(float targetAlpha)
    {
        if (dimOverlay == null) return;
        if (dimCoroutine != null) StopCoroutine(dimCoroutine);
        dimCoroutine = StartCoroutine(FadeDimRoutine(targetAlpha));
    }

    IEnumerator FadeDimRoutine(float targetAlpha)
    {
        float startAlpha = dimOverlay.alpha;
        float elapsed = 0f;
        while (elapsed < dimFadeDuration)
        {
            elapsed += Time.deltaTime;
            dimOverlay.alpha = Mathf.Lerp(startAlpha, targetAlpha, Mathf.Clamp01(elapsed / dimFadeDuration));
            yield return null;
        }
        dimOverlay.alpha = targetAlpha;
    }
}
