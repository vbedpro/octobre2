using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

// Fetches this device's approximate location (country + lat/long) via
// ipwho.is, ONCE per install — cached permanently in PlayerPrefs, never
// re-queried on later sessions. If the player later travels, we don't
// care — this is a deliberate one-time snapshot, not live tracking.
//
// ipwho.is requires no signup/API key, free tier is 1,000 requests/day
// per client IP. (ipapi.co was the original plan, but their free tier
// has since tightened to require registration — ipwho.is uses identical
// JSON field names, so this was a one-line swap.)
//
// If the very first attempt fails (e.g. no network yet at boot), the
// result is NOT cached as permanent — it'll simply retry on the next
// session instead of being stuck blank forever.
public class LocationService : MonoBehaviour
{
    public static LocationService Instance;

    private const string HasLocationKey = "RabbitUniverse_HasLocation";
    private const string CountryCodeKey = "RabbitUniverse_CountryCode";
    private const string LatitudeKey = "RabbitUniverse_Latitude";
    private const string LongitudeKey = "RabbitUniverse_Longitude";

    public string CountryCode { get; private set; } = "";
    public float Latitude { get; private set; } = 0f;
    public float Longitude { get; private set; } = 0f;
    public bool IsReady { get; private set; } = false;

    [System.Serializable]
    private class IpWhoResponse
    {
        public bool success;
        public string country_code;
        public float latitude;
        public float longitude;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    void Start()
    {
        if (PlayerPrefs.HasKey(HasLocationKey))
        {
            CountryCode = PlayerPrefs.GetString(CountryCodeKey, "");
            Latitude = PlayerPrefs.GetFloat(LatitudeKey, 0f);
            Longitude = PlayerPrefs.GetFloat(LongitudeKey, 0f);
            IsReady = true;
        }
        else
        {
            StartCoroutine(FetchLocation());
        }
    }

    IEnumerator FetchLocation()
    {
        using (UnityWebRequest req = UnityWebRequest.Get("https://ipwho.is/"))
        {
            yield return req.SendWebRequest();

            bool success = false;

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    IpWhoResponse response = JsonUtility.FromJson<IpWhoResponse>(req.downloadHandler.text);
                    if (response.success)
                    {
                        CountryCode = response.country_code ?? "";
                        Latitude = response.latitude;
                        Longitude = response.longitude;
                        success = true;
                    }
                    else
                    {
                        Debug.LogWarning("LocationService: ipwho.is returned success=false for this IP.");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("LocationService: failed to parse ipwho.is response — " + e.Message);
                }
            }
            else
            {
                Debug.LogWarning("LocationService: ipwho.is request failed — " + req.error);
            }

            if (success)
            {
                PlayerPrefs.SetInt(HasLocationKey, 1);
                PlayerPrefs.SetString(CountryCodeKey, CountryCode);
                PlayerPrefs.SetFloat(LatitudeKey, Latitude);
                PlayerPrefs.SetFloat(LongitudeKey, Longitude);
                PlayerPrefs.Save();
            }
            // else: deliberately NOT caching — retries again next session
            // instead of being permanently stuck blank from one bad attempt.

            IsReady = true;
        }
    }

    // Right-click-able in the Inspector — clears the cached location so
    // you can re-test the fetch without it needing a fresh install.
    [ContextMenu("Reset Location (Testing)")]
    public void ResetLocation()
    {
        PlayerPrefs.DeleteKey(HasLocationKey);
        PlayerPrefs.DeleteKey(CountryCodeKey);
        PlayerPrefs.DeleteKey(LatitudeKey);
        PlayerPrefs.DeleteKey(LongitudeKey);
    }
}
