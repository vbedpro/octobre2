using UnityEngine;
using System.Collections;

// Alternates the "losing signal" screen effect (PlutoSignalLoss.shader) on
// and off while the player is on Pluto: signal is fine for offDuration
// seconds, then drops for onDuration seconds (scanlines/VHS/vignette
// fading in, holding, then fading back out as "signal is regained"),
// repeating indefinitely. Unlike the Venus heatwave (which re-triggers on
// a fixed interval regardless of the previous activation's length), this
// is a straightforward sequential loop — off, then on, then off again —
// so the two states never overlap regardless of how their durations compare.
public class PlutoSignalLossEffect : MonoBehaviour
{
    [Header("Cycle")]
    [Tooltip("How long the signal stays clear before dropping out.")]
    public float offDuration = 10f;
    [Tooltip("How long the signal-loss effect stays active before clearing again.")]
    public float onDuration = 20f;
    [Tooltip("How long the fade in/out takes at the start/end of the active window.")]
    public float fadeDuration = 0.6f;

    [Header("Visual")]
    [Tooltip("The full-screen effect GameObject (a UI RawImage using PlutoSignalLoss material) — only active while the effect is actually running.")]
    public GameObject effectObject;
    [Tooltip("CanvasGroup on the same object as Effect Object — its alpha fades in/out alongside the shader intensity.")]
    public CanvasGroup effectCanvasGroup;
    [Tooltip("The material instance using the PlutoSignalLoss shader — its _Intensity gets animated.")]
    public Material effectMaterial;
    [Tooltip("Peak intensity during the active window (0-1). This is what the effect fades UP TO, not a fixed 1.0 — matches the material's own _Intensity slider range.")]
    [Range(0f, 1f)]
    public float maxIntensity = 0.2f;

    [Header("Signal Lost Text")]
    [Tooltip("Bottom-right 'SIGNAL LOST' text — flashes on/off (hard blink, not a smooth fade) the whole time the effect is active.")]
    public TMPro.TextMeshProUGUI signalLostText;
    [Tooltip("Low opacity when visible during the blink 'on' phase.")]
    [Range(0f, 1f)]
    public float signalLostMaxAlpha = 0.5f;
    [Tooltip("How long each on/off blink phase lasts, in seconds.")]
    public float blinkInterval = 0.4f;

    private Coroutine blinkCoroutine;

    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

    private Coroutine loopCoroutine;
    private bool wasOnPluto = false;

    void Start()
    {
        SetImmediate(0f);
    }

    void Update()
    {
        bool onPluto = PlanetManager.Instance != null && PlanetManager.Instance.currentPlanet == PlanetManager.Planet.Pluto;

        if (onPluto && !wasOnPluto)
        {
            // Just arrived on Pluto — start the cycle fresh from "signal clear".
            if (loopCoroutine != null) StopCoroutine(loopCoroutine);
            loopCoroutine = StartCoroutine(CycleLoop());
        }
        else if (!onPluto && wasOnPluto)
        {
            // Left Pluto — cancel immediately, never lingers onto another planet.
            if (loopCoroutine != null) { StopCoroutine(loopCoroutine); loopCoroutine = null; }
            SetImmediate(0f);
        }

        wasOnPluto = onPluto;
    }

    IEnumerator CycleLoop()
    {
        while (true)
        {
            // Signal clear.
            yield return new WaitForSeconds(offDuration);

            // Signal drops — fade in, hold, fade out.
            if (effectObject != null) effectObject.SetActive(true);

            yield return FadeTo(1f, fadeDuration);

            StartBlinking();

            float holdDuration = Mathf.Max(0f, onDuration - fadeDuration * 2f);
            yield return new WaitForSeconds(holdDuration);

            StopBlinking();

            yield return FadeTo(0f, fadeDuration);

            if (effectObject != null) effectObject.SetActive(false);
        }
    }

    void StartBlinking()
    {
        if (signalLostText == null) return;
        if (blinkCoroutine != null) StopCoroutine(blinkCoroutine);
        blinkCoroutine = StartCoroutine(BlinkRoutine());
    }

    void StopBlinking()
    {
        if (blinkCoroutine != null) { StopCoroutine(blinkCoroutine); blinkCoroutine = null; }
        SetTextAlpha(0f);
    }

    // Hard on/off blink — not a smooth fade, a literal square-wave flicker,
    // like a real "REC"/"SIGNAL LOST" camera overlay indicator.
    IEnumerator BlinkRoutine()
    {
        while (true)
        {
            SetTextAlpha(signalLostMaxAlpha);
            yield return new WaitForSeconds(blinkInterval);
            SetTextAlpha(0f);
            yield return new WaitForSeconds(blinkInterval);
        }
    }

    void SetTextAlpha(float alpha)
    {
        if (signalLostText == null) return;
        Color c = signalLostText.color;
        signalLostText.color = new Color(c.r, c.g, c.b, alpha);
    }

    IEnumerator FadeTo(float target, float duration)
    {
        float start = effectCanvasGroup != null ? effectCanvasGroup.alpha : 0f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float value = Mathf.Lerp(start, target, t);

            // Alpha controls the overlay's own visibility (full 0-1 range).
            // Intensity controls how strong the shader math is (scanlines/
            // vignette/noise/aberration), capped at maxIntensity — these
            // are deliberately separate, otherwise a low maxIntensity would
            // also make the overlay itself look see-through on top of the
            // effect being weak, doubling up the "too faint" problem.
            if (effectCanvasGroup != null) effectCanvasGroup.alpha = value;
            if (effectMaterial != null) effectMaterial.SetFloat(IntensityId, value * maxIntensity);

            yield return null;
        }

        if (effectCanvasGroup != null) effectCanvasGroup.alpha = target;
        if (effectMaterial != null) effectMaterial.SetFloat(IntensityId, target * maxIntensity);
    }

    void SetImmediate(float value)
    {
        if (effectObject != null) effectObject.SetActive(value > 0f);
        if (effectCanvasGroup != null) effectCanvasGroup.alpha = value;
        if (effectMaterial != null) effectMaterial.SetFloat(IntensityId, value);
        StopBlinking();
    }
}
