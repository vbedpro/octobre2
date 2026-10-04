using UnityEngine;

// While active: manual hatching is disabled (BarnSpawner.OnButtonDown
// refuses to start), and rabbits instead hatch automatically at a fixed
// rate. Auto-hatching pauses the instant a planet transition starts and
// resumes once it's finished — the boost's own timer keeps counting the
// whole time regardless (it's global, not planet-locked).
public class RoboticHatchingBoost : MonoBehaviour
{
    public static RoboticHatchingBoost Instance { get; private set; }

    public const string ItemId = "boost_robotic_hatching";

    [Tooltip("How many rabbits get auto-hatched per second while this boost is active.")]
    public float rabbitsPerSecond = 2f;

    [Tooltip("The shared barn spawner that actually spawns rabbits.")]
    public BarnSpawner barnSpawner;

    private float accumulator = 0f;
    private bool pausedForTransition = false;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Update()
    {
        if (pausedForTransition) return;
        if (barnSpawner == null) return;
        if (BoostActivationManager.Instance == null || !BoostActivationManager.Instance.IsActive(ItemId)) return;

        accumulator += Time.deltaTime * rabbitsPerSecond;
        while (accumulator >= 1f)
        {
            barnSpawner.AutoHatchOne();
            accumulator -= 1f;
        }
    }

    public static bool IsManualHatchingBlocked()
    {
        return BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive(ItemId);
    }

    // Wire from BoostActivationManager.ApplyBoostEffect.
    public void Activate()
    {
        accumulator = 0f; // avoid an instant burst from time accrued before activation
    }

    public void Deactivate()
    {
        accumulator = 0f;
    }

    // Called by PlanetManager right as a planet switch begins/ends —
    // pauses auto-hatching for the duration of the transition without
    // touching the boost's own timer.
    public void PauseForTransition() => pausedForTransition = true;
    public void ResumeAfterTransition() => pausedForTransition = false;
}
