using UnityEngine;
using System.Collections;

// Tracks, locally and with no network calls of its own, whether the
// device currently reports an active Wi-Fi connection — used to
// "Wi-Fi block" specific buttons (IAP, Free Ad reward, Leaderboard) that
// need a real connection to do anything useful.
//
// IMPORTANT: this deliberately checks for Wi-Fi SPECIFICALLY, not "any
// internet" — Application.internetReachability also reports
// ReachableViaCarrierDataNetwork for cellular data, which this treats the
// same as NotReachable, matching what was asked for (block unless on
// Wi-Fi). It's also only a local interface check: a device connected to a
// Wi-Fi router with no working internet behind it will still report
// ReachableViaLocalAreaNetwork here. Combine with the actual call's own
// failure handling (e.g. IAPManager's OnPurchaseFailed, GuildManager's
// null-return-on-failure) for genuine network errors — this is just the
// instant, local, "don't even try" gate.
public class NetworkStatusManager : MonoBehaviour
{
    public static NetworkStatusManager Instance { get; private set; }

    [Tooltip("How often (seconds) to re-check Application.internetReachability. Cheap, local check — no actual network traffic.")]
    public float pollIntervalSeconds = 5f;

    public static bool HasWifi { get; private set; } = false;

    // Fired any time the Wi-Fi state actually changes (not every poll tick) —
    // subscribe here if a UI element wants to refresh itself live instead of
    // just checking NetworkStatusManager.HasWifi at the moment of a tap.
    public static event System.Action<bool> OnWifiStatusChanged;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        CheckNow();
        StartCoroutine(PollLoop());
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollIntervalSeconds);
            CheckNow();
        }
    }

    void CheckNow()
    {
        bool nowHasWifi = Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork;
        if (nowHasWifi != HasWifi)
        {
            HasWifi = nowHasWifi;
            OnWifiStatusChanged?.Invoke(HasWifi);
        }
    }

    // Call this from any Wi-Fi-gated button's click handler, right before
    // doing the real thing it's supposed to do. Returns true if it's safe
    // to proceed; shows "Please connect to Wi-Fi" and returns false if not.
    public static bool RequireWifiOrShowMessage()
    {
        if (HasWifi) return true;

        CenterScreenMessage.Instance?.ShowMessage("Please connect to Wi-Fi");
        return false;
    }
}
