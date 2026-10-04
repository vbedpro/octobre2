using UnityEngine;
using System.Collections;

// Plays the "traveling between planets" transition: fast zoom-out (ignores
// normal camera zoom limits) + fade-to-black happen simultaneously, then
// once the screen is fully black, the actual planet switch happens
// (PlanetManager.SwitchToPlanet — data swap, hut recolor, etc.), then the
// screen fades back in while zooming back to a normal view. Deliberately
// mirrors the feel of BigBangAnimator's transition.
public class PlanetTransitionController : MonoBehaviour
{
    public static PlanetTransitionController Instance { get; private set; }

    [Header("References")]
    public CameraController cameraController;
    [Tooltip("Full-screen black Image with a CanvasGroup, sitting above all other UI.")]
    public CanvasGroup fadeOverlay;

    [Header("Zoom Out (ignores normal min/max zoom limits)")]
    [Tooltip("How much bigger than the current zoom to zoom out to. 1.6 = 60% further out.")]
    public float zoomOutMultiplier = 1.6f;
    public float zoomOutDuration = 0.25f;

    [Header("Fade")]
    public float fadeOutDuration = 0.25f; // to black — runs alongside zoom-out
    public float fadeInDuration = 0.35f;  // back from black — runs alongside zoom-in

    [Header("Zoom In (return to a normal view)")]
    public float zoomInDuration = 0.4f;

    private bool isTransitioning;
    public static bool IsTransitioning => Instance != null && Instance.isTransitioning;

    void Awake()
    {
        Instance = this;
        if (fadeOverlay != null)
        {
            fadeOverlay.alpha = 0f;
            fadeOverlay.gameObject.SetActive(false);
        }
    }

    public void PlayTransition(PlanetManager.Planet target)
    {
        if (isTransitioning) return;
        if (PlanetManager.Instance == null || !PlanetManager.Instance.IsUnlocked(target)) return;
        if (target == PlanetManager.Instance.currentPlanet) return;

        AudioManager.Instance?.PlayPlanetSwitch();

        StartCoroutine(TransitionRoutine(target));
    }

    IEnumerator TransitionRoutine(PlanetManager.Planet target)
    {
        isTransitioning = true;

        if (cameraController != null) cameraController.externalControl = true;
        if (fadeOverlay != null) fadeOverlay.gameObject.SetActive(true);

        float startZoom = GetCamZoom();
        float outZoom = startZoom * zoomOutMultiplier;

        // ── Zoom out + fade to black, simultaneously ──────────────────
        float duration = Mathf.Max(zoomOutDuration, fadeOutDuration);
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float zoomT = Mathf.Clamp01(elapsed / zoomOutDuration);
            zoomT = zoomT * zoomT * (3f - 2f * zoomT); // smoothstep
            SetCamZoom(Mathf.Lerp(startZoom, outZoom, zoomT));

            if (fadeOverlay != null)
            {
                float fadeT = Mathf.Clamp01(elapsed / fadeOutDuration);
                fadeOverlay.alpha = fadeT;
            }

            yield return null;
        }
        SetCamZoom(outZoom);
        if (fadeOverlay != null) fadeOverlay.alpha = 1f;

        // ── Screen is fully black — swap the actual farm now ──────────
        PlanetManager.Instance.SwitchToPlanet(target);
        yield return null; // let hut recolor / hut level changes apply before revealing

        // ── Fade back in + zoom back in, simultaneously ───────────────
        duration = Mathf.Max(zoomInDuration, fadeInDuration);
        elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float zoomT = Mathf.Clamp01(elapsed / zoomInDuration);
            zoomT = zoomT * zoomT * (3f - 2f * zoomT);
            SetCamZoom(Mathf.Lerp(outZoom, startZoom, zoomT));

            if (fadeOverlay != null)
            {
                float fadeT = Mathf.Clamp01(elapsed / fadeInDuration);
                fadeOverlay.alpha = 1f - fadeT;
            }

            yield return null;
        }
        SetCamZoom(startZoom);
        if (fadeOverlay != null)
        {
            fadeOverlay.alpha = 0f;
            fadeOverlay.gameObject.SetActive(false);
        }

        if (cameraController != null)
        {
            cameraController.SetZoomTargetImmediate(startZoom);
            cameraController.externalControl = false;
        }

        isTransitioning = false;
    }

    float GetCamZoom()
    {
        if (cameraController == null || cameraController.cam == null) return 10f;
        return cameraController.cam.orthographic
            ? cameraController.cam.orthographicSize
            : cameraController.cam.fieldOfView;
    }

    void SetCamZoom(float value)
    {
        if (cameraController == null || cameraController.cam == null) return;
        if (cameraController.cam.orthographic) cameraController.cam.orthographicSize = value;
        else cameraController.cam.fieldOfView = value;
    }
}
