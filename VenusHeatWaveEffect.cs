using UnityEngine;
using System.Collections;

// Toggles a full-screen heat-shimmer distortion (HeatWaveDistortion.shader)
// while the player is on Venus. Cycle: every 10 seconds, the effect turns
// on and stays active for 5 seconds (fading in/out smoothly at the edges),
// then off for the remaining 5 seconds, repeating. Only ever active while
// currentPlanet == Venus — instantly cancelled the moment the player
// switches to any other planet.
public class VenusHeatwaveEffect : MonoBehaviour
{
    [Header("Cycle")]
    public float cycleInterval = 10f;
    public float activeDuration = 5f;
    [Tooltip("How long the fade in/out takes at the start/end of the active window, so the effect doesn't pop on/off abruptly.")]
    public float fadeDuration = 0.5f;

    [Header("Visual")]
    [Tooltip("The full-screen effect GameObject (a UI RawImage or fullscreen quad using HeatWaveDistortion material) — only active while the effect is actually running.")]
    public GameObject heatwaveEffectObject;
    [Tooltip("CanvasGroup on the same object as Heatwave Effect Object — its alpha fades in/out alongside the distortion intensity, so the whole effect visibly fades rather than popping on/off instantly.")]
    public CanvasGroup heatwaveCanvasGroup;
    [Tooltip("The material instance using the HeatWaveDistortion shader — its _Intensity gets animated.")]
    public Material heatwaveMaterial;
    [Tooltip("Peak distortion strength during the active window.")]
    public float maxIntensity = 0.01f;

    [Header("Light Tint")]
    [Tooltip("Venus's Light component — its color shifts toward Heatwave Light Color while the effect is active, in sync with the same fade.")]
    public Light venusLight;
    public Color heatwaveLightColor = new Color(1f, 0.55f, 0.45f);

    private Color originalLightColor;
    private bool originalLightColorCaptured = false;

    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

    private float cycleTimer = 0f;
    private bool isActive = false;
    private Coroutine cycleCoroutine;

    void CaptureOriginalLightColorOnce()
    {
        if (originalLightColorCaptured || venusLight == null) return;
        originalLightColor = venusLight.color;
        originalLightColorCaptured = true;
    }

    void Start()
    {
        if (heatwaveEffectObject != null) heatwaveEffectObject.SetActive(false);
        if (heatwaveMaterial != null) heatwaveMaterial.SetFloat(IntensityId, 0f);
        if (heatwaveCanvasGroup != null) heatwaveCanvasGroup.alpha = 0f;
        CaptureOriginalLightColorOnce();
    }

    void Update()
    {
        bool onVenus = PlanetManager.Instance != null && PlanetManager.Instance.currentPlanet == PlanetManager.Planet.Venus;

        if (!onVenus)
        {
            if (isActive || cycleTimer != 0f) ForceOff();
            return;
        }

        cycleTimer += Time.deltaTime;
        if (cycleTimer >= cycleInterval)
        {
            cycleTimer = 0f;
            StartHeatwave();
        }
    }

    void StartHeatwave()
    {
        if (cycleCoroutine != null) StopCoroutine(cycleCoroutine);
        cycleCoroutine = StartCoroutine(HeatwaveRoutine());
    }

    IEnumerator HeatwaveRoutine()
    {
        isActive = true;
        if (heatwaveEffectObject != null) heatwaveEffectObject.SetActive(true);

        yield return FadeEffect(0f, 1f, fadeDuration);

        float holdDuration = Mathf.Max(0f, activeDuration - fadeDuration * 2f);
        yield return new WaitForSeconds(holdDuration);

        yield return FadeEffect(1f, 0f, fadeDuration);

        if (heatwaveEffectObject != null) heatwaveEffectObject.SetActive(false);
        isActive = false;
        cycleCoroutine = null;
    }

    // Fades BOTH the CanvasGroup's opacity AND the shader's distortion
    // intensity together, from 0 (invisible, no warp) to 1 (fully visible,
    // full warp) or vice versa — this is what makes the whole effect
    // visibly fade in/out instead of just the warp strength changing while
    // the image pops fully opaque/transparent via SetActive.
    IEnumerator FadeEffect(float from, float to, float duration)
    {
        CaptureOriginalLightColorOnce();

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float value = Mathf.Lerp(from, to, t);

            if (heatwaveCanvasGroup != null) heatwaveCanvasGroup.alpha = value;
            if (heatwaveMaterial != null) heatwaveMaterial.SetFloat(IntensityId, value * maxIntensity);
            if (venusLight != null && originalLightColorCaptured) venusLight.color = Color.Lerp(originalLightColor, heatwaveLightColor, value);

            yield return null;
        }

        if (heatwaveCanvasGroup != null) heatwaveCanvasGroup.alpha = to;
        if (heatwaveMaterial != null) heatwaveMaterial.SetFloat(IntensityId, to * maxIntensity);
        if (venusLight != null && originalLightColorCaptured) venusLight.color = Color.Lerp(originalLightColor, heatwaveLightColor, to);
    }

    // Immediately cancels the effect and resets the cycle — used when the
    // player leaves Venus, so the heatwave never lingers onto another planet.
    void ForceOff()
    {
        if (cycleCoroutine != null) { StopCoroutine(cycleCoroutine); cycleCoroutine = null; }
        if (heatwaveEffectObject != null) heatwaveEffectObject.SetActive(false);
        if (heatwaveMaterial != null) heatwaveMaterial.SetFloat(IntensityId, 0f);
        if (heatwaveCanvasGroup != null) heatwaveCanvasGroup.alpha = 0f;
        if (venusLight != null && originalLightColorCaptured) venusLight.color = originalLightColor;
        isActive = false;
        cycleTimer = 0f;
    }
}
