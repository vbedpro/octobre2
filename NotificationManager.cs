using UnityEngine;
#if UNITY_ANDROID
using Unity.Notifications.Android;
#endif
#if UNITY_IOS
using Unity.Notifications.iOS;
#endif

// Requires the "Mobile Notifications" package (com.unity.mobile.notifications)
// installed via Package Manager. Handles local notification scheduling —
// starting with "your offline earnings expired, come claim your rabbits."
//
// Local notifications only know what THIS DEVICE already knows (they're
// scheduled entirely on-device, no server involved) — this is why this
// approach works for the offline-earnings case (the game already knows
// exactly when the cap will run out) but can't work for "another player
// took your rank" (that requires a server watching the leaderboard
// continuously, independent of whether your app is even open — a
// separate, bigger project).
public class NotificationManager : MonoBehaviour
{
    public static NotificationManager Instance { get; private set; }

    [Header("Android Channel (required once, on first launch)")]
    public string androidChannelId = "rabbit_universe_default";
    public string androidChannelName = "Rabbit Universe";
    public string androidChannelDescription = "Game reminders";

    [Header("Offline Earnings Expired Notification")]
    public string offlineEarningsTitle = "Rabbit Universe";
    public string offlineEarningsBody = "Your offline earnings have expired. Come claim your rabbits!";

    // Fixed IDs so we can cancel/replace a specific pending notification
    // rather than accumulating duplicates across repeated pause/quit cycles.
    private const int OfflineEarningsNotificationId = 1001;

    void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(gameObject);

#if UNITY_ANDROID
        var channel = new AndroidNotificationChannel
        {
            Id = androidChannelId,
            Name = androidChannelName,
            Importance = Importance.Default,
            Description = androidChannelDescription
        };
        AndroidNotificationCenter.RegisterNotificationChannel(channel);
#endif
    }

    void Start()
    {
        RequestPermission();
    }

    public void RequestPermission()
    {
#if UNITY_IOS
        // Just triggers the permission prompt on first call — iOS
        // requires explicit opt-in before any notification can show.
        StartCoroutine(RequestIOSAuthorization());
#endif
        // Android 13+ (API 33+) also requires a runtime permission
        // (POST_NOTIFICATIONS) — Unity's package handles requesting this
        // automatically as of recent package versions when a notification
        // is first scheduled; no extra code needed here for most setups.
        // If targeting API 33+ and notifications silently don't appear,
        // check that the Mobile Notifications package version is current.
    }

    // Called by SaveManager right when the app pauses/quits — schedules a
    // notification for exactly when the offline earnings cap will run out.
    public void ScheduleOfflineEarningsExpired(float delaySeconds)
    {
        CancelOfflineEarningsNotification(); // clear any previous pending one first

        if (delaySeconds <= 0f) return;

        System.DateTime fireTime = System.DateTime.Now.AddSeconds(delaySeconds);

#if UNITY_ANDROID
        var notification = new AndroidNotification
        {
            Title = offlineEarningsTitle,
            Text = offlineEarningsBody,
            FireTime = fireTime,
            ShouldAutoCancel = true // dismisses itself from the notification tray when tapped, instead of lingering
        };
        AndroidNotificationCenter.SendNotificationWithExplicitID(notification, androidChannelId, OfflineEarningsNotificationId);
#endif

#if UNITY_IOS
        var timeTrigger = new iOSNotificationTimeIntervalTrigger
        {
            TimeInterval = new System.TimeSpan(0, 0, Mathf.Max(1, Mathf.RoundToInt(delaySeconds))),
            Repeats = false
        };

        var notification = new iOSNotification
        {
            Identifier = OfflineEarningsNotificationId.ToString(),
            Title = offlineEarningsTitle,
            Body = offlineEarningsBody,
            ShowInForeground = false,
            Trigger = timeTrigger
        };
        iOSNotificationCenter.ScheduleNotification(notification);
#endif
    }

    // Called when the app resumes (player is back, no longer needs the
    // reminder) — cancels the pending notification so it doesn't fire
    // while they're actively playing.
    public void CancelOfflineEarningsNotification()
    {
#if UNITY_ANDROID
        AndroidNotificationCenter.CancelScheduledNotification(OfflineEarningsNotificationId);
#endif
#if UNITY_IOS
        iOSNotificationCenter.RemoveScheduledNotification(OfflineEarningsNotificationId.ToString());
#endif
    }

#if UNITY_IOS
    // iOS has no simple static "request permission" call — you build an
    // AuthorizationRequest, wait a frame at a time until it's finished,
    // then check whether the user granted it. This is standard for the
    // Unity Mobile Notifications package's iOS API.
    private System.Collections.IEnumerator RequestIOSAuthorization()
    {
        using (var req = new Unity.Notifications.iOS.AuthorizationRequest(
            AuthorizationOption.Alert | AuthorizationOption.Sound, true))
        {
            while (!req.IsFinished)
            {
                yield return null;
            }

            if (!req.Granted)
            {
                Debug.Log("iOS notification permission was not granted.");
            }
        }
    }
#endif
}
