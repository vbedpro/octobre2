using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class SettingsPanelController : MonoBehaviour
{
    public static SettingsPanelController Instance { get; private set; }

    // Matches the same static-flag convention every other panel in this
    // project uses (ShopManager.shopOpen, SellManager.popupOpen, etc.) —
    // CameraController already checks a list of these to decide whether
    // to block camera drag while any panel is open.
    public static bool settingsOpen = false;

    [Header("Panel")]
    public GameObject settingsPanel;
    public RectTransform settingsPanelRect;
    [Tooltip("Position while open, on screen.")]
    public Vector2 openPosition;
    [Tooltip("Position while closed, off-screen (e.g. same X, larger Y so it slides down off the top, or however your other panels are set up).")]
    public Vector2 closedPosition;
    public float slideDuration = 0.35f;

    private Coroutine slideCoroutine;

    [Header("Audio")]
    public Slider musicVolumeSlider;
    public Slider sfxVolumeSlider;

    [Header("Language")]
    public TMP_Dropdown languageDropdown;

    [Header("Legal")]
    [Tooltip("Must be a fully-hosted, publicly reachable URL (e.g. a page on your own website) — see LanguageManager.cs comments for why this can't just be plain text in the app.")]
    public string privacyPolicyUrl = "https://yourwebsite.com/privacy-policy";
    public string termsOfServiceUrl = "https://yourwebsite.com/terms-of-service";

    [Header("Support")]
    public string supportEmail = "vbedapple@gmail.com";

    void Start()
    {
        Instance = this;
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (settingsPanelRect != null) settingsPanelRect.anchoredPosition = closedPosition;

        // Audio — initialize sliders from AudioManager's current values,
        // then wire them to push changes back live.
        if (AudioManager.Instance != null)
        {
            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.SetValueWithoutNotify(AudioManager.Instance.GetMusicVolume());
                musicVolumeSlider.onValueChanged.AddListener(OnMusicVolumeChanged);
            }
            if (sfxVolumeSlider != null)
            {
                sfxVolumeSlider.SetValueWithoutNotify(AudioManager.Instance.GetSfxVolume());
                sfxVolumeSlider.onValueChanged.AddListener(OnSfxVolumeChanged);
            }
        }

        // Language — populate dropdown from LanguageManager's supported list.
        if (languageDropdown != null && LanguageManager.Instance != null)
        {
            languageDropdown.ClearOptions();
            languageDropdown.AddOptions(LanguageManager.Instance.GetLanguageDisplayNames());
            languageDropdown.SetValueWithoutNotify((int)LanguageManager.Instance.CurrentLanguage);
            languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
        }
    }

    // Wire this to the Settings button's OnClick().
    public void OpenSettings()
    {
        CloseOtherUI();

        settingsOpen = true;
        if (settingsPanel != null) settingsPanel.SetActive(true);

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideTo(openPosition));
    }

    // Wire this to the panel's own Close button's OnClick().
    public void CloseSettings()
    {
        settingsOpen = false;

        if (slideCoroutine != null) StopCoroutine(slideCoroutine);
        slideCoroutine = StartCoroutine(SlideToThenDeactivate(closedPosition));
    }

    IEnumerator SlideTo(Vector2 target)
    {
        if (settingsPanelRect == null) yield break;

        Vector2 start = settingsPanelRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / slideDuration;
            t = t * t * (3f - 2f * t);
            settingsPanelRect.anchoredPosition = Vector2.Lerp(start, target, t);
            yield return null;
        }
        settingsPanelRect.anchoredPosition = target;
    }

    IEnumerator SlideToThenDeactivate(Vector2 target)
    {
        yield return SlideTo(target);
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }

    // Closes every other panel before Settings opens — same convention
    // several other panels in this project already follow.
    void CloseOtherUI()
    {
        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (SellManager.popupOpen)
        {
            FindAnyObjectByType<SellManager>()?.CloseSellPopup();
            // If the tutorial had already advanced past "open the sell
            // popup" and was waiting on the confirm button inside it, force-
            // closing the popup here would otherwise strand the player with
            // no unlocked button at all — step the tutorial back so the
            // sell button re-opens as an option.
            FindAnyObjectByType<TutorialManager>()?.NotifySellPopupClosedExternally();
        }
        if (HutUpgradeManager.popupOpen) FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();
        if (LeaderboardUI.leaderboardOpen) FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();
    }

    void OnMusicVolumeChanged(float value) => AudioManager.Instance?.SetMusicVolume(value);
    void OnSfxVolumeChanged(float value) => AudioManager.Instance?.SetSfxVolume(value);

    void OnLanguageChanged(int index) => LanguageManager.Instance?.SetLanguage((LanguageManager.Language)index);

    // Wire these to the Privacy Policy / Terms of Service buttons' OnClick().
    public void OpenPrivacyPolicy() => Application.OpenURL(privacyPolicyUrl);
    public void OpenTermsOfService() => Application.OpenURL(termsOfServiceUrl);

    // Opens the device's default mail app with a new message already
    // addressed to the support email — mailto: is a standard URL scheme,
    // handled by Application.OpenURL the same way as any other link.
    public void OpenSupportEmail() => Application.OpenURL($"mailto:{supportEmail}");
}
