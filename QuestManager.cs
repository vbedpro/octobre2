using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections;
using System.Collections.Generic;

public class QuestManager : MonoBehaviour
{
    public static QuestManager Instance;

    public enum QuestState { Idle, NotificationVisible, PopupOpen, ActiveChallenge }

    // For external systems (e.g. AdNotificationManager) that need to know
    // whether quests are currently "on cooldown" — no notification, popup,
    // or active challenge showing right now — without needing direct
    // access to the private state field itself.
    public bool IsQuestSystemIdle => state == QuestState.Idle;

    // True once the player has accepted or declined (including letting
    // the accept/decline timer run out) AT LEAST ONE quest, ever. Used to
    // gate AdNotificationManager's very first appearance — it shouldn't
    // show before the player has even seen how quests work.
    public bool HasRespondedToFirstQuest { get; private set; } = false;

    // Called by SaveManager on load, to restore this from a save instead
    // of always starting false on a fresh launch.
    public void RestoreHasRespondedToFirstQuest(bool value)
    {
        HasRespondedToFirstQuest = value;
    }

    public enum ObjectiveType
    {
        TapFoxes, SellBatches, SellRabbits, ReachOccupancy,
        UpgradeHuts, SpendCoins, BuyHuts, SpendCarrots, HatchRabbits
    }

    public enum BuffType { HutCapacity, TruckCapacity, ReproductionRate, HatchRate, Coin, Carrot }

    [System.Serializable]
    public struct ObjectiveRange
    {
        public long min;
        public long max;
        public ObjectiveRange(long min, long max) { this.min = min; this.max = max; }
    }

    [Header("Timing")]
    public float offerInterval = 135f;
    public float notificationWindow = 30f;
    public float acceptDeclineWindow = 30f;

    [Header("Reward / Punishment — Fixed Amounts")]
    public const int carrotAmount = 5;    // Carrot: always ±5
    private const float hatchRateAmount = 1f; // Hatch Rate: always ±1/sec

    [Header("Reward / Punishment — Percent Range (Hut/Truck/Reproduction)")]
    public float percentRangeMin = 3f;
    public float percentRangeMax = 7f;

    [Header("Reward / Punishment — Coin Formula (current rabbits x this)")]
    public float coinRabbitMultiplier = 1.4f;
    public float coinMinimumAmount = 400f; // used if the formula would produce less (e.g. 0 rabbits)

    [Header("Reward / Punishment Icons")]
    public Sprite coinIconSprite;
    public Sprite carrotIconSprite;
    public Sprite hutCapacityIconSprite;
    public Sprite truckCapacityIconSprite;
    public Sprite reproductionIconSprite;
    public Sprite hatchRateIconSprite;
    public Image buffIcon;   // Buff_Icon in QuestPopup (shown on success side)
    public Image debuffIcon; // Debuff_Icon in QuestPopup (shown on failure side)
    public Image activeBuffIcon;   // Buff_Icon in ActiveQuestPanel
    public Image activeDebuffIcon; // Debuff_Icon in ActiveQuestPanel

    [Header("UI — Notification")]
    public GameObject questNotification;
    public QuestSlidePanel notificationSlide;
    public Button notificationButton;
    public CanvasGroup notificationCanvasGroup;

    [Header("UI — QuestPopup (Accept/Decline)")]
    public GameObject questPopup;
    public QuestSlidePanel popupSlide;
    public QuestGaugeFill acceptDeclineGauge;
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI descriptionText;
    public TextMeshProUGUI timeToCompleteText;
    public TextMeshProUGUI buffPreviewText;
    public TextMeshProUGUI debuffPreviewText;
    public Button acceptButton;
    public Button declineButton;

    [Header("UI — ActiveQuestButton")]
    public GameObject activeQuestButton;
    public QuestSlidePanel activeButtonSlide;
    public Button activeQuestButtonComponent;
    public TextMeshProUGUI activeButtonCountdownText;
    public CanvasGroup activeButtonCanvasGroup;

    [Header("UI — ActiveQuestPanel")]
    public GameObject activeQuestPanel;
    public QuestSlidePanel activePanelSlide;
    public RectTransform activePanelRect;
    public QuestGaugeFill activeChallengeGauge;
    public TextMeshProUGUI activePanelCountdownText;
    public TextMeshProUGUI activeDescriptionText;
    public TextMeshProUGUI progressText;
    public Button cancelButtonQuest;
    [Tooltip("Shows which planet the active quest's home planet is — lets the player check at a glance even while viewing a different planet.")]
    public Image planetIcon;

    [Header("UI — Objective Progress Bar (GaugeBar-style, optional)")]
    [Tooltip("Reuses GaugeBar's SetFillInstant/SetFill logic for a 0-1 progress-toward-target bar.")]
    public GaugeBar objectiveProgressGauge;

    [Header("UI — Outcome Toast (top-right, shown when a quest ends)")]
    public QuestOutcomeToast outcomeToast;

    [Header("Dim Overlay (Accept/Decline stage only)")]
    public CanvasGroup dimOverlay;
    public float dimAlpha = 0.6f;
    public float dimFadeDuration = 0.4f;

    [Header("External Fade (hide while Shop/BigBang open)")]
    public float externalFadeDuration = 0.25f;

    [Header("Nav Button Fades (hidden while QuestPopup or ActiveQuestPanel is open)")]
    public CanvasGroup leaderboardButtonCanvasGroup;
    public CanvasGroup achievementButtonCanvasGroup;
    public CanvasGroup inventoryButtonCanvasGroup;
    public CanvasGroup bigBangButtonCanvasGroup;
    public CanvasGroup settingsButtonCanvasGroup;
    public float navButtonFadeDuration = 0.25f;
    private Coroutine navLeaderboardFadeCo;
    private Coroutine navAchievementFadeCo;
    private Coroutine navInventoryFadeCo;
    private Coroutine navBigBangFadeCo;
    private Coroutine navSettingsFadeCo;

    [Header("UI — Outcome Preview (shown inside ActiveQuestPanel too)")]
    public TextMeshProUGUI activeBuffPreviewText;
    public TextMeshProUGUI activeDebuffPreviewText;

    [Header("Coin Text Font Size (shrinks only when coin amount > 1000)")]
    public float coinTextShrunkSize = 28f;
    private float buffPreviewBaseSize, debuffPreviewBaseSize, activeBuffPreviewBaseSize, activeDebuffPreviewBaseSize;
    private bool coinSizeCached = false;

    [Header("Huts (for Reach Occupancy objective)")]
    public RabbitHut[] rabbitHuts;

    private QuestState state = QuestState.Idle;
    private float idleTimer = 0f;
    private float questShowGraceTimer = -1f; // -1 = not currently counting

    private float acceptDeclineTimer = 0f;
    private bool acceptDeclineRunning = false;

    private float notificationTimer = 0f;
    private bool notificationTimerRunning = false;

    private float challengeTimer = 0f;
    private bool challengeRunning = false;
    private bool panelIsOpen = false;

    // Real wall-clock deadline for the active challenge (Unix epoch
    // seconds), computed once when the challenge starts. challengeTimer
    // above is now DERIVED from this every frame (see Update()) instead
    // of being decremented by Time.deltaTime directly — Time.deltaTime
    // only advances while Update() is actually running, so a pure
    // deltaTime countdown silently FREEZES while the app is backgrounded
    // instead of tracking real elapsed time. An epoch-based deadline
    // keeps counting correctly regardless of whether the app was in the
    // foreground for that whole span.
    private long challengeDeadlineEpoch = 0;

    // ── Current quest data ──────────────────────────────────────────────
    private ObjectiveType currentObjectiveType;
    private BuffType currentBuffType;
    private float currentBuffMagnitude; // percent for capacity/repro types, flat units for hatch/carrot/coin
    private float currentChallengeDuration;
    private long currentTargetValue;      // target amount (count, coins, rabbits, or occupancy %)
    private long currentProgress;         // current progress toward target

    // The planet that was active when this quest was OFFERED (notification
    // shown). Fixed for the quest's whole lifetime — accepting, progressing,
    // and resolving are all scoped to this planet regardless of which
    // planet the player switches to while the quest is pending/active.
    private PlanetManager.Planet homePlanet;

    bool IsOnHomePlanet()
    {
        return PlanetManager.Instance != null && PlanetManager.Instance.currentPlanet == homePlanet;
    }

    // Tracks the last known home-planet-match state for the notification,
    // so RefreshPlanetGatedVisibility only reacts on the frame it actually
    // changes, instead of fighting the slide-in animation every frame.
    private bool lastNotificationPlanetMatch = true;

    // ── Objective bag (no repeats until all 9 have been used) ──────────
    private static readonly ObjectiveType[] AllObjectiveTypes =
    {
        ObjectiveType.TapFoxes, ObjectiveType.SellBatches, ObjectiveType.SellRabbits,
        ObjectiveType.ReachOccupancy, ObjectiveType.UpgradeHuts, ObjectiveType.SpendCoins,
        ObjectiveType.BuyHuts, ObjectiveType.SpendCarrots, ObjectiveType.HatchRabbits
    };
    private List<ObjectiveType> objectiveBag = new List<ObjectiveType>();

    private static readonly BuffType[] AllBuffTypes =
    {
        BuffType.HutCapacity, BuffType.TruckCapacity, BuffType.ReproductionRate,
        BuffType.HatchRate, BuffType.Coin, BuffType.Carrot
    };
    private List<BuffType> buffBag = new List<BuffType>();

    // ── Range tables: [ObjectiveType][durationTierIndex] -> range ──────
    // durationTierIndex: 0 = 30s, 1 = 60s, 2 = 90s
    // SellBatches has no 30s entry (index 0 unused — 30s never rolled for it).
    private static readonly Dictionary<ObjectiveType, ObjectiveRange[]> Ranges = new Dictionary<ObjectiveType, ObjectiveRange[]>
    {
        { ObjectiveType.TapFoxes,       new [] { new ObjectiveRange(2, 3),         new ObjectiveRange(5, 6),         new ObjectiveRange(8, 9) } },
        { ObjectiveType.SellBatches,    new [] { new ObjectiveRange(0, 0),         new ObjectiveRange(2, 3),         new ObjectiveRange(4, 4) } },
        { ObjectiveType.SellRabbits,    new [] { new ObjectiveRange(100, 30000),   new ObjectiveRange(200, 60000),   new ObjectiveRange(300, 90000) } },
        { ObjectiveType.ReachOccupancy, new [] { new ObjectiveRange(25, 30),       new ObjectiveRange(50, 60),       new ObjectiveRange(80, 90) } },
        { ObjectiveType.UpgradeHuts,    new [] { new ObjectiveRange(2, 2),         new ObjectiveRange(3, 4),         new ObjectiveRange(6, 7) } },
        { ObjectiveType.SpendCoins,     new [] { new ObjectiveRange(1000, 5000000),new ObjectiveRange(2000, 10000000),new ObjectiveRange(3000, 15000000) } },
        { ObjectiveType.BuyHuts,        new [] { new ObjectiveRange(2, 3),         new ObjectiveRange(4, 5),         new ObjectiveRange(6, 7) } },
        { ObjectiveType.SpendCarrots,   new [] { new ObjectiveRange(3, 4),         new ObjectiveRange(5, 6),         new ObjectiveRange(7, 8) } },
        { ObjectiveType.HatchRabbits,   new [] { new ObjectiveRange(56, 120),      new ObjectiveRange(112, 250),     new ObjectiveRange(170, 370) } },
    };

    private static readonly float[] DurationsByTier = { 30f, 60f, 90f };

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        if (questNotification != null) questNotification.SetActive(false);
        if (questPopup != null) questPopup.SetActive(false);
        if (activeQuestButton != null) activeQuestButton.SetActive(false);
        if (activeQuestPanel != null) activeQuestPanel.SetActive(false);

        if (acceptButton != null) acceptButton.onClick.AddListener(OnAcceptPressed);
        if (declineButton != null) declineButton.onClick.AddListener(OnDeclinePressed);
        if (notificationButton != null) notificationButton.onClick.AddListener(OnNotificationTapped);
        if (activeQuestButtonComponent != null) activeQuestButtonComponent.onClick.AddListener(OnActiveQuestButtonTapped);
        if (cancelButtonQuest != null) cancelButtonQuest.onClick.AddListener(OnCancelPanelPressed);

        idleTimer = 0f;
        RefillBagIfEmpty();
    }

    void Update()
    {
        switch (state)
        {
            case QuestState.Idle:
                // Block until the tutorial has genuinely finished — not
                // just "not currently active", which would also be true
                // in the brief window before a brand-new player's
                // tutorial even starts.
                if (TutorialManager.Instance != null && TutorialManager.Instance.CurrentStep != TutorialManager.TutorialStep.Completed)
                    break;

                // Also pause while the AdNotification's bubble OR its
                // Accept/Decline panel is currently showing — don't let
                // quest idle time silently tick away underneath either
                // one, and don't let a quest notification pop up on top
                // of an untapped AdNotification bubble.
                if (AdNotificationManager.Instance != null && AdNotificationManager.Instance.IsShowingAnything)
                {
                    questShowGraceTimer = -1f; // reset — grace restarts fresh once it clears
                    break;
                }

                idleTimer += Time.deltaTime;
                if (idleTimer >= offerInterval)
                {
                    // Small padding once actually ready — avoids popping
                    // up the instant something else (e.g. an
                    // AdNotification) finishes, which felt pushy.
                    if (questShowGraceTimer < 0f) questShowGraceTimer = 0f;
                    questShowGraceTimer += Time.deltaTime;

                    if (questShowGraceTimer >= 5f)
                    {
                        idleTimer = 0f;
                        questShowGraceTimer = -1f;
                        ShowNotification();
                    }
                }
                break;

            case QuestState.NotificationVisible:
                // Timer is a manual real-time countdown (not Invoke()) —
                // this only controls whether the notification is
                // VISIBLE/tappable right now, which depends on being on
                // its home planet; the countdown itself is unaffected.
                RefreshPlanetGatedVisibility();
                if (notificationTimerRunning)
                {
                    notificationTimer -= Time.deltaTime;
                    if (notificationTimer <= 0f)
                    {
                        notificationTimerRunning = false;
                        NotificationTimeoutCheck();
                    }
                }
                break;

            case QuestState.PopupOpen:
                // Same planet gate — the accept/decline popup should never
                // be interactable while standing on a different planet
                // than the one the quest belongs to.
                RefreshPlanetGatedVisibility();
                if (acceptDeclineRunning)
                {
                    acceptDeclineTimer -= Time.deltaTime;
                    if (acceptDeclineGauge != null)
                        acceptDeclineGauge.SetFraction(acceptDeclineTimer / acceptDeclineWindow);

                    if (acceptDeclineTimer <= 0f)
                    {
                        acceptDeclineRunning = false;
                        OnAcceptDeclineTimeout();
                    }
                }
                break;

            case QuestState.ActiveChallenge:
                if (challengeRunning)
                {
                    long nowEpoch = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    challengeTimer = Mathf.Max(0f, challengeDeadlineEpoch - nowEpoch);

                    if (activeChallengeGauge != null)
                        activeChallengeGauge.SetFraction(challengeTimer / currentChallengeDuration);

                    UpdateActiveButtonCountdown();
                    UpdateActivePanelCountdown();
                    RefreshOccupancyProgress();

                    if (challengeTimer <= 0f)
                    {
                        challengeRunning = false;
                        OnChallengeTimeout();
                    }

                    if (panelIsOpen)
                        DetectOutsideTap();
                }
                break;
        }
    }

    // ── Outside-tap detection (closes ActiveQuestPanel without swallowing the tap) ──

    void DetectOutsideTap()
    {
        Vector2? pressPos = null;

        if (Touchscreen.current != null)
        {
            var touch = Touchscreen.current.primaryTouch;
            if (touch.press.wasPressedThisFrame)
                pressPos = touch.position.ReadValue();
        }

        if (pressPos == null && Mouse.current != null)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
                pressPos = Mouse.current.position.ReadValue();
        }

        if (pressPos == null) return;
        if (activePanelRect == null) return;

        bool insidePanel = RectTransformUtility.RectangleContainsScreenPoint(
            activePanelRect, pressPos.Value, null);

        if (!insidePanel)
            OnCancelPanelPressed();
    }

    // ── Idle → Notification ──────────────────────────────────────────

    // Hides the notification/popup the instant the player leaves this
    // quest's home planet (so a Mars-tailored quest can't be opened while
    // standing on Earth), and brings it back the instant they return.
    // Called every frame while NotificationVisible/PopupOpen — cheap
    // activeSelf checks below mean it only actually toggles on the frame
    // the planet match state actually changes.
    void RefreshPlanetGatedVisibility()
    {
        bool onHomePlanet = IsOnHomePlanet();

        if (state == QuestState.NotificationVisible)
        {
            // Instant opacity toggle here — NOT the slide animation (that's
            // reserved for the real show/hide transitions). Only acts on
            // the frame the match status actually CHANGES, so it doesn't
            // fight the slide-in animation's own alpha ramp every frame.
            if (onHomePlanet != lastNotificationPlanetMatch && notificationCanvasGroup != null)
            {
                notificationCanvasGroup.alpha = onHomePlanet ? 1f : 0f;
                notificationCanvasGroup.interactable = onHomePlanet;
                notificationCanvasGroup.blocksRaycasts = onHomePlanet;
            }
            lastNotificationPlanetMatch = onHomePlanet;
        }
        else if (state == QuestState.PopupOpen)
        {
            bool currentlyShown = questPopup != null && questPopup.activeSelf;
            if (onHomePlanet && !currentlyShown)
            {
                if (popupSlide != null) popupSlide.Show();
                else if (questPopup != null) questPopup.SetActive(true);
                if (dimOverlay != null) StartCoroutine(FadeDim(dimAlpha));
            }
            else if (!onHomePlanet && currentlyShown)
            {
                if (popupSlide != null) popupSlide.Hide(true);
                else if (questPopup != null) questPopup.SetActive(false);
                if (dimOverlay != null) StartCoroutine(FadeDim(0f));
            }
        }
    }

    void ShowNotification()
    {
        state = QuestState.NotificationVisible;

        // Locks in which planet this quest belongs to. GenerateQuestData()
        // reads GameManager.Instance.totalRabbits right after this, which
        // already reflects whichever planet is currently active — so
        // objective scaling automatically matches the home planet's own
        // stats (a freshly-unlocked planet won't roll a "sell 30000
        // rabbits" quest, since its rabbit count is still low).
        homePlanet = PlanetManager.Instance != null ? PlanetManager.Instance.currentPlanet : PlanetManager.Planet.Earth;
        lastNotificationPlanetMatch = true;

        GenerateQuestData();

        if (questNotification != null) questNotification.SetActive(true);
        if (notificationSlide != null) notificationSlide.Show();

        // A brand-new notification is ALWAYS on its own home planet right
        // now, so it must always start fully visible/interactable — even
        // if the PREVIOUS notification's CanvasGroup got left at alpha 0
        // because it timed out while the player was away on another planet.
        if (notificationCanvasGroup != null)
        {
            notificationCanvasGroup.alpha = 1f;
            notificationCanvasGroup.interactable = true;
            notificationCanvasGroup.blocksRaycasts = true;
        }

        notificationTimer = notificationWindow;
        notificationTimerRunning = true;
    }

    void NotificationTimeoutCheck()
    {
        if (state != QuestState.NotificationVisible) return;
        HideNotification();
        BackToIdle();
    }

    void HideNotification()
    {
        if (notificationSlide != null) notificationSlide.Hide(true);
        else if (questNotification != null) questNotification.SetActive(false);
    }

    void OnNotificationTapped()
    {
        if (state != QuestState.NotificationVisible) return;
        notificationTimerRunning = false;
        HideNotification();
        CloseAllOtherUI();
        AudioManager.Instance?.PlayQuestCountdown();
        OpenQuestPopup();
    }

    void CloseAllOtherUI()
    {
        if (ShopManager.shopOpen) FindAnyObjectByType<ShopManager>()?.CloseShop();
        if (SellManager.popupOpen) FindAnyObjectByType<SellManager>()?.CloseSellPopup();
        if (HutUpgradeManager.popupOpen) FindAnyObjectByType<HutUpgradeManager>()?.ClosePopup();
        if (LeaderboardUI.leaderboardOpen) FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();
        if (AchievementsPanelUI.achievementsOpen) FindAnyObjectByType<AchievementsPanelUI>()?.ClosePanel();
        if (SettingsPanelController.settingsOpen) SettingsPanelController.Instance?.CloseSettings();
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();
    }

    // ── QuestPopup (Accept/Decline) ──────────────────────────────────

    void OpenQuestPopup()
    {
        if (LeaderboardUI.leaderboardOpen)
            FindAnyObjectByType<LeaderboardUI>()?.CloseLeaderboard();

        state = QuestState.PopupOpen;

        RefreshPopupTexts();

        if (questPopup != null) questPopup.SetActive(true);
        if (popupSlide != null) popupSlide.Show();

        if (dimOverlay != null) StartCoroutine(FadeDim(dimAlpha));

        acceptDeclineTimer = acceptDeclineWindow;
        acceptDeclineRunning = true;
        if (acceptDeclineGauge != null) acceptDeclineGauge.SetFraction(1f);

        FadeNavButtonsOut();
    }

    void RefreshPopupTexts()
    {
        if (titleText != null) titleText.text = "New Quest!";
        if (descriptionText != null) descriptionText.text = BuildObjectiveDescription();
        if (timeToCompleteText != null) timeToCompleteText.text = "Time to complete: <b><color=#0070F5>" + FormatTime(currentChallengeDuration) + "</color></b>";
        if (buffPreviewText != null) buffPreviewText.text = RewardAmountText();
        if (debuffPreviewText != null) debuffPreviewText.text = PunishAmountText();
        ApplyRewardIcons();
    }

    void OnDeclinePressed()
    {
        if (state != QuestState.PopupOpen) return;
        HasRespondedToFirstQuest = true;
        AudioManager.Instance?.StopQuestCountdown();
        ClosePopup();
        BackToIdle();
    }

    void OnAcceptDeclineTimeout()
    {
        if (state != QuestState.PopupOpen) return;
        HasRespondedToFirstQuest = true;
        acceptDeclineRunning = false;
        AudioManager.Instance?.StopQuestCountdown();
        ClosePopup();
        BackToIdle();
    }

    void ClosePopup()
    {
        acceptDeclineRunning = false;
        if (popupSlide != null) popupSlide.Hide(true);
        else if (questPopup != null) questPopup.SetActive(false);

        if (dimOverlay != null) StartCoroutine(FadeDim(0f));

        FadeNavButtonsIn();
    }

    IEnumerator FadeDim(float targetAlpha)
    {
        float startAlpha = dimOverlay.alpha;
        float elapsed = 0f;

        while (elapsed < dimFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / dimFadeDuration);
            dimOverlay.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        dimOverlay.alpha = targetAlpha;
    }

    void OnAcceptPressed()
    {
        if (state != QuestState.PopupOpen) return;
        HasRespondedToFirstQuest = true;
        AudioManager.Instance?.StopQuestCountdown();
        AchievementsManager.Instance?.ReportQuestAccepted();
        ClosePopup();
        BeginActiveChallenge();
    }

    // ── Active Challenge ──────────────────────────────────────────────

    void BeginActiveChallenge()
    {
        state = QuestState.ActiveChallenge;
        currentProgress = 0;

        if (activeQuestButton != null) activeQuestButton.SetActive(true);

        // Defensive reset: guarantee the button's CanvasGroup always starts
        // a new challenge fully visible/interactable, regardless of whatever
        // fade state SetExternallyHidden may have left it in previously.
        if (activeButtonCanvasGroup != null)
        {
            activeButtonCanvasGroup.alpha = 1f;
            activeButtonCanvasGroup.interactable = true;
            activeButtonCanvasGroup.blocksRaycasts = true;
        }

        if (activeButtonSlide != null) activeButtonSlide.Show();

        challengeTimer = currentChallengeDuration;
        challengeDeadlineEpoch = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)currentChallengeDuration;
        challengeRunning = true;
        if (activeChallengeGauge != null) activeChallengeGauge.SetFraction(1f);
        if (objectiveProgressGauge != null) objectiveProgressGauge.SetFillInstant(0f);

        RefreshActivePanelTexts();
        RefreshProgressDisplay();
    }

    void OnActiveQuestButtonTapped()
    {
        if (state != QuestState.ActiveChallenge) return;

        if (SellManager.popupOpen)
        {
            SellManager sm = FindAnyObjectByType<SellManager>();
            if (sm != null) sm.CloseSellPopup();
        }
        if (InventoryPanel.inventoryOpen) FindAnyObjectByType<InventoryPanel>()?.ClosePanel();

        if (activeQuestPanel != null) activeQuestPanel.SetActive(true);
        if (activePanelSlide != null) activePanelSlide.Show();
        panelIsOpen = true;
        RefreshActivePanelTexts();

        FadeNavButtonsOut();
    }

    void OnCancelPanelPressed()
    {
        panelIsOpen = false;
        if (activePanelSlide != null) activePanelSlide.Hide(true);
        else if (activeQuestPanel != null) activeQuestPanel.SetActive(false);

        FadeNavButtonsIn();
    }

    void RefreshActivePanelTexts()
    {
        if (activeDescriptionText != null) activeDescriptionText.text = BuildObjectiveDescription();
        if (activeBuffPreviewText != null) activeBuffPreviewText.text = RewardAmountText();
        if (activeDebuffPreviewText != null) activeDebuffPreviewText.text = PunishAmountText();
        ApplyRewardIcons();
        RefreshPlanetIcon();
    }

    // Shows which planet the active quest's home planet is, so the player
    // can tell at a glance even if they've switched away from it. Hidden
    // entirely if the player only has Earth — pointless to show "which
    // planet" when there's only ever one option.
    void RefreshPlanetIcon()
    {
        if (planetIcon == null) return;

        bool hasMultiplePlanets = PlanetManager.Instance != null && PlanetManager.Instance.IsUnlocked(PlanetManager.Planet.Moon);
        planetIcon.gameObject.SetActive(hasMultiplePlanets);
        if (!hasMultiplePlanets) return;

        PlanetColumnUI columnUI = FindAnyObjectByType<PlanetColumnUI>();
        if (columnUI == null) return;

        Sprite sprite = columnUI.GetIconSprite(homePlanet);
        if (sprite != null) planetIcon.sprite = sprite;
    }

    void UpdateActiveButtonCountdown()
    {
        if (activeButtonCountdownText == null) return;
        activeButtonCountdownText.text = FormatTime(challengeTimer);
    }

    void UpdateActivePanelCountdown()
    {
        if (activePanelCountdownText == null) return;
        activePanelCountdownText.text = FormatTime(challengeTimer);
    }

    // ── Progress evaluation ─────────────────────────────────────────────
    // ReachOccupancy is the one "live snapshot" objective — it reads the
    // CURRENTLY DISPLAYED huts' occupancy, which only reflects whichever
    // planet is active right now. So it's only evaluated while the player
    // is actually on the quest's home planet; otherwise progress is frozen
    // at whatever it last was, not zeroed or based on the wrong planet's huts.
    //
    // Every other objective type advances only through the direct, gated
    // Report* calls below (called by SellManager/BarnSpawner/etc. wherever
    // the real action happens) — each one checks IsOnHomePlanet() itself,
    // so actions performed on a DIFFERENT planet never count toward this
    // quest, and hatching specifically requires being physically present
    // on the home planet (satisfied automatically, since hatching only
    // ever happens via the hold-to-hatch button on whichever farm is
    // currently displayed).

    void RefreshOccupancyProgress()
    {
        if (currentObjectiveType != ObjectiveType.ReachOccupancy) return;
        if (!IsOnHomePlanet()) return; // frozen while away — don't read the wrong planet's huts

        currentProgress = GetBestHutOccupancyPercent();
        RefreshProgressDisplay();
        CheckForSuccess();
    }

    void RefreshProgressDisplay()
    {
        if (progressText != null)
            progressText.text = FormatProgressPair(currentProgress, currentTargetValue, currentObjectiveType);

        if (objectiveProgressGauge != null)
        {
            float frac = currentTargetValue > 0 ? (float)currentProgress / currentTargetValue : 0f;
            objectiveProgressGauge.SetFillInstant(Mathf.Clamp01(frac));
        }
    }

    void CheckForSuccess()
    {
        if (currentProgress >= currentTargetValue)
        {
            challengeRunning = false;
            OnChallengeSuccess();
        }
    }

    long GetBestHutOccupancyPercent()
    {
        if (rabbitHuts == null || rabbitHuts.Length == 0) return 0;
        long best = 0;
        foreach (RabbitHut hut in rabbitHuts)
        {
            if (hut == null) continue;
            long cap = hut.GetMaxCapacity();
            if (cap <= 0) continue;
            long pct = hut.GetCurrentRabbits() * 100L / cap;
            if (pct > best) best = pct;
        }
        return best;
    }

    // Core gated-progress entry point — only counts toward the CURRENT
    // quest if it's actually running, matches this quest's objective type,
    // AND the player is on this quest's home planet right now.
    void ReportQuestProgress(ObjectiveType type, long amount)
    {
        if (state != QuestState.ActiveChallenge) return;
        if (currentObjectiveType != type) return;
        if (!IsOnHomePlanet()) return;

        currentProgress += amount;
        RefreshProgressDisplay();
        CheckForSuccess();
    }

    // Call this from wherever the real objective is satisfied, kept for
    // any objective type not covered by a dedicated Report* method below.
    public void ReportObjectiveProgress(int amount = 1)
    {
        if (state != QuestState.ActiveChallenge) return;
        if (!IsOnHomePlanet()) return;

        currentProgress += amount;
        RefreshProgressDisplay();
        CheckForSuccess();
    }

    void OnChallengeTimeout()
    {
        if (state != QuestState.ActiveChallenge) return;
        challengeRunning = false;
        OnChallengeFailure();
    }

    void OnChallengeSuccess()
    {
        challengeRunning = false;
        ApplyOutcome(isPunishment: false);
        AchievementsManager.Instance?.ReportQuestCompletedSuccessfully();
        _ = GuildManager.Instance?.ReportContestProgressAsync("CompleteQuests", 1);
        EndChallenge();
    }

    void OnChallengeFailure()
    {
        ApplyOutcome(isPunishment: true);
        AchievementsManager.Instance?.ReportAcceptedQuestFailed();
        EndChallenge();
    }

    // Called by SaveManager, right before it saves on quit/background —
    // if a quest was accepted and actively running, quitting counts as an
    // automatic failure, regardless of which planet the quest belongs to
    // or which planet the player is currently on. This makes sure the
    // debuff gets applied to the quest's home planet's saved data (via
    // the normal ApplyOutcome path) before the final save is written, so
    // it's correctly in place next time the player loads back in — not
    // silently discarded, and not left in permanent limbo.
    public void FailActiveQuestOnQuit()
    {
        if (state != QuestState.ActiveChallenge) return;
        challengeRunning = false;
        OnChallengeFailure();
    }

    void EndChallenge()
    {
        bool wasPanelOpen = panelIsOpen;
        panelIsOpen = false;
        if (activeButtonSlide != null) activeButtonSlide.Hide(true);
        else if (activeQuestButton != null) activeQuestButton.SetActive(false);

        if (activePanelSlide != null) activePanelSlide.Hide(true);
        else if (activeQuestPanel != null) activeQuestPanel.SetActive(false);

        // The panel might have been open when the challenge ended (success/
        // failure/timeout) without going through OnCancelPanelPressed — make
        // sure the nav buttons still get faded back in in that case.
        if (wasPanelOpen) FadeNavButtonsIn();

        BackToIdle();
    }

    // ── BigBang integration (called by PrestigeManager.ConfirmBigBang) ──
    //
    // 1) Wipes any accumulated quest buffs/debuffs (hut capacity, truck
    //    capacity, reproduction, hatch rate) — won/lost quests don't
    //    survive a BigBang.
    // 2) If a quest was accepted and actively running, it's fully
    //    neutralized: no reward, no punishment, and the "new quest" offer
    //    interval RESETS (starts a fresh full wait).
    // 3) If there's no active accepted quest (idle, or just a notification/
    //    popup shown but not yet accepted), the "new quest" interval is left
    //    completely untouched — it keeps counting through the BigBang.
    public void HandleBigBang()
    {
        if (HutUpgradeManager.Instance != null)
            HutUpgradeManager.Instance.ResetQuestCapacityBonus();
        if (UpgradeManager.Instance != null)
            UpgradeManager.Instance.ResetQuestBonuses();

        switch (state)
        {
            case QuestState.NotificationVisible:
                notificationTimerRunning = false;
                HideNotification();
                BackToIdle(resetIdleTimer: false); // not an accepted quest — keep countdown running
                break;

            case QuestState.PopupOpen:
                ClosePopup();
                BackToIdle(resetIdleTimer: false); // not an accepted quest — keep countdown running
                break;

            case QuestState.ActiveChallenge:
                challengeRunning = false;
                bool wasPanelOpen = panelIsOpen;
                panelIsOpen = false;
                if (activeButtonSlide != null) activeButtonSlide.Hide(true);
                else if (activeQuestButton != null) activeQuestButton.SetActive(false);
                if (activePanelSlide != null) activePanelSlide.Hide(true);
                else if (activeQuestPanel != null) activeQuestPanel.SetActive(false);
                if (wasPanelOpen) FadeNavButtonsIn();
                // No ApplyOutcome call — fully neutralized, no reward/punishment.
                BackToIdle(resetIdleTimer: true); // accepted quest was cancelled — reset countdown
                break;

            default: // Idle — nothing to cancel, timer keeps running untouched
                break;
        }
    }

    void BackToIdle(bool resetIdleTimer = true)
    {
        state = QuestState.Idle;
        if (resetIdleTimer) idleTimer = 0f;
        questShowGraceTimer = -1f;
    }

    // ── Objective bag + quest generation ─────────────────────────────

    void RefillBagIfEmpty()
    {
        if (objectiveBag.Count > 0) return;
        objectiveBag = new List<ObjectiveType>(AllObjectiveTypes);
        ShuffleBag();
    }

    void ShuffleBag()
    {
        for (int i = objectiveBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (objectiveBag[i], objectiveBag[j]) = (objectiveBag[j], objectiveBag[i]);
        }
    }

    ObjectiveType DrawObjectiveFromBag()
    {
        RefillBagIfEmpty();
        ObjectiveType picked = objectiveBag[0];
        objectiveBag.RemoveAt(0);
        return picked;
    }

    void RefillBuffBagIfEmpty()
    {
        if (buffBag.Count > 0) return;
        buffBag = new List<BuffType>(AllBuffTypes);
        ShuffleBuffBag();
    }

    void ShuffleBuffBag()
    {
        for (int i = buffBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (buffBag[i], buffBag[j]) = (buffBag[j], buffBag[i]);
        }
    }

    BuffType DrawBuffFromBag()
    {
        RefillBuffBagIfEmpty();
        BuffType picked = buffBag[0];
        buffBag.RemoveAt(0);
        return picked;
    }

    // Rolls the magnitude for a given buff type. Percent types roll a fresh
    // random value in [percentRangeMin, percentRangeMax] each quest. Hatch
    // Rate and Carrot are fixed. Coin scales with current total rabbits.
    float RollMagnitudeFor(BuffType type)
    {
        switch (type)
        {
            case BuffType.HutCapacity:
            case BuffType.TruckCapacity:
                return Random.Range(percentRangeMin, percentRangeMax);
            case BuffType.ReproductionRate:
                int avidBreederLevel = EpicUpgradeManager.Instance != null
                    ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.AvidBreeder)
                    : 0;
                // Shifts the whole roll range up per level instead of
                // multiplying the result — otherwise a low roll stays low.
                float shift = avidBreederLevel * 1f;
                return Random.Range(percentRangeMin + shift, percentRangeMax + shift);
            case BuffType.HatchRate:
                return hatchRateAmount;
            case BuffType.Carrot:
                float luckyCloverBonus = EpicUpgradeManager.Instance != null
                    ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.LuckyClover)
                    : 0f;
                // +1 carrot per 100 million CURRENT rabbits (not lifetime) —
                // this is what actually ties carrot quest rewards to
                // BigBang timing: a fresh 0-rabbit farm gets none of this
                // bonus, while a big established farm gets more. Applies
                // identically to the punishment side (failed/declined
                // quests spend more too), same as every other buff type.
                long currentRabbitsForBonus = GameManager.Instance != null ? GameManager.Instance.totalRabbits : 0;
                float billionBonus = currentRabbitsForBonus / 100_000_000f;
                return Mathf.Min(carrotAmount + luckyCloverBonus + billionBonus, 100f);
            case BuffType.Coin:
                long rabbits = GameManager.Instance != null ? GameManager.Instance.totalRabbits : 0;
                float coinFormula = rabbits * coinRabbitMultiplier;
                float coinResult = Mathf.Max(coinFormula, coinMinimumAmount);

                int generousRewardsLevel = EpicUpgradeManager.Instance != null
                    ? EpicUpgradeManager.Instance.GetLevel(EpicUpgradeManager.EpicUpgradeType.GenerousRewards)
                    : 0;
                return coinResult * (1f + generousRewardsLevel * 0.1f);
            default:
                return 0f;
        }
    }

    // Checks whether applying the PUNISH side (given magnitude) would push
    // the relevant stat below its true non-upgraded base. Only percent/rate
    // types have a floor; Coin/Carrot never need replacing.
    bool WouldGoBelowBase(BuffType type, float magnitude)
    {
        switch (type)
        {
            case BuffType.HutCapacity:
                return HutUpgradeManager.Instance != null &&
                       HutUpgradeManager.Instance.WouldQuestCapacityGoBelowBase(-magnitude);
            case BuffType.TruckCapacity:
                return UpgradeManager.Instance != null &&
                       UpgradeManager.Instance.WouldQuestTruckCapacityGoBelowBase(-magnitude);
            case BuffType.ReproductionRate:
                return UpgradeManager.Instance != null &&
                       UpgradeManager.Instance.WouldQuestReproductionGoBelowBase(-magnitude);
            case BuffType.HatchRate:
                return UpgradeManager.Instance != null &&
                       UpgradeManager.Instance.WouldQuestHatchRateGoBelowBase(-magnitude);
            default:
                return false; // Coin/Carrot have no floor concern
        }
    }

    // Picks the buff type for a new quest (and rolls its magnitude),
    // swapping to Coin/Carrot if the drawn type's punish side would push a
    // stat below its base value. The originally-drawn type still counts as
    // "used" for bag rotation fairness.
    BuffType PickBuffType()
    {
        BuffType drawn = DrawBuffFromBag();
        float magnitude = RollMagnitudeFor(drawn);

        if (WouldGoBelowBase(drawn, magnitude))
        {
            BuffType replacement = (Random.value < 0.5f) ? BuffType.Coin : BuffType.Carrot;
            currentBuffMagnitude = RollMagnitudeFor(replacement);
            return replacement;
        }

        currentBuffMagnitude = magnitude;
        return drawn;
    }

    void GenerateQuestData()
    {
        currentObjectiveType = DrawObjectiveFromBag();
        currentBuffType = PickBuffType();

        // Sell Batches can only roll 60s or 90s (no 30s data for it).
        int tier;
        if (currentObjectiveType == ObjectiveType.SellBatches)
            tier = Random.Range(1, 3); // 1 or 2 -> 60s or 90s
        else
            tier = Random.Range(0, 3); // 0,1,2 -> 30s, 60s, 90s

        currentChallengeDuration = DurationsByTier[tier];

        ObjectiveRange range = Ranges[currentObjectiveType][tier];

        long rabbits = GameManager.Instance != null ? GameManager.Instance.totalRabbits : 0;

        if (currentObjectiveType == ObjectiveType.SellRabbits)
        {
            long scaled = (long)(rabbits * 0.7f);
            currentTargetValue = System.Math.Clamp(scaled, range.min, range.max);
        }
        else if (currentObjectiveType == ObjectiveType.SpendCoins)
        {
            long scaled = (long)(rabbits * 2.2f);
            currentTargetValue = System.Math.Clamp(scaled, range.min, range.max);
        }
        else
        {
            currentTargetValue = (range.min == range.max)
                ? range.min
                : (long)Random.Range((float)range.min, (float)range.max + 1f);
        }
    }

    string BuildObjectiveDescription()
    {
        string x = "<b><color=#0070F5>" + currentTargetValue + "</color></b>";
        string xPct = "<b><color=#0070F5>" + currentTargetValue + "%</color></b>";

        switch (currentObjectiveType)
        {
            case ObjectiveType.TapFoxes:       return "Tap " + x + " foes.";
            case ObjectiveType.SellBatches:    return "Sell " + x + " batches of rabbits.";
            case ObjectiveType.SellRabbits:    return "Sell " + x + " rabbits.";
            case ObjectiveType.ReachOccupancy: return "Reach " + xPct + " occupancy on any hut.";
            case ObjectiveType.UpgradeHuts:    return "Upgrade your huts " + x + " times.";
            case ObjectiveType.SpendCoins:     return "Spend a total of <b><color=#0070F5>" + GameManager.FormatFullNumber(currentTargetValue) + "</color></b> coins.";
            case ObjectiveType.BuyHuts:        return "Buy " + x + " new huts.";
            case ObjectiveType.SpendCarrots:   return "Spend " + x + " golden carrots.";
            case ObjectiveType.HatchRabbits:   return "Hatch " + x + " new rabbits.";
            default: return "Complete the objective before time runs out.";
        }
    }

    // Last-resort formatter: guarantees max 3 characters (2-digit mantissa +
    // 1-letter suffix, e.g. "99K", "7T"). Used only when normal abbreviation
    // (GameManager.FormatNumber) still doesn't fit the budget.
    static string CompactTwoDigit(long value)
    {
        string[] suffixes = { "", "K", "M", "B", "T", "P" };
        double v = value;
        int idx = 0;
        while (v >= 100.0 && idx < suffixes.Length - 1)
        {
            v /= 1000.0;
            idx++;
        }
        long rounded = (long)System.Math.Round(v);
        if (rounded >= 100 && idx < suffixes.Length - 1) // rounding rollover, e.g. 99.6 -> 100
        {
            rounded /= 1000;
            idx++;
        }
        return rounded + suffixes[idx];
    }

    string FormatProgressPair(long progress, long target, ObjectiveType type)
    {
        long displayProgress = System.Math.Min(progress, target);

        if (type == ObjectiveType.ReachOccupancy)
            return displayProgress + "/" + target + "%"; // e.g. "80/90%" — max "100/100%" = 8 chars

        // Tier 1: raw digits, if they fit.
        string p = displayProgress.ToString();
        string t = target.ToString();
        string combined = p + " / " + t;
        if (combined.Length <= 9) return combined;

        // Tier 2: standard K/M/B/T abbreviation (up to ~5 chars per side).
        p = GameManager.FormatNumber(displayProgress);
        t = GameManager.FormatNumber(target);
        combined = p + " / " + t;
        if (combined.Length <= 9) return combined;

        // Tier 3: strict 2-digit-mantissa compaction, guaranteed to fit.
        p = CompactTwoDigit(displayProgress);
        t = CompactTwoDigit(target);
        return p + " / " + t;
    }

    // ── Reward / Punishment (placeholder: flat golden carrot) ──────────

    void ApplyOutcome(bool isPunishment)
    {
        if (GameManager.Instance == null) return;

        float signedMagnitude = isPunishment ? -currentBuffMagnitude : currentBuffMagnitude;

        // Carrots are the one GLOBAL currency (not per-planet, per spec) —
        // always applied directly regardless of home planet.
        if (currentBuffType == BuffType.Carrot)
        {
            long carrotAmountLong = (long)currentBuffMagnitude;
            if (isPunishment) GameManager.Instance.ForceSpendCarrots(carrotAmountLong);
            else GameManager.Instance.AddCarrots(carrotAmountLong);
        }
        else if (PlanetManager.Instance != null)
        {
            // Everything else (HutCapacity/TruckCapacity/ReproductionRate/
            // HatchRate/Coin) ALWAYS lands on the quest's home planet —
            // applied live if that's the planet currently active, or
            // written directly into that planet's saved PlanetData
            // otherwise. Never affects whichever planet the player
            // happens to be standing on right now if that's different.
            PlanetManager.Instance.ApplyQuestOutcome(homePlanet, currentBuffType, signedMagnitude);
        }

        // Show the top-right outcome toast ONLY if the player is currently
        // on the quest's home planet — never shown retroactively for a
        // planet they're not looking at, even though the effect above
        // always applies regardless.
        if (outcomeToast != null && IsOnHomePlanet())
        {
            string amountText = isPunishment ? PunishAmountText() : RewardAmountText();
            outcomeToast.Show(GetCurrentBuffIconSprite(), amountText, isPunishment);
        }
    }

    string RewardAmountText()
    {
        switch (currentBuffType)
        {
            case BuffType.HutCapacity:      return "+" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.TruckCapacity:    return "+" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.ReproductionRate: return "+" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.HatchRate:        return "+" + currentBuffMagnitude.ToString("0.#") + "/s";
            case BuffType.Coin:             return "+" + GameManager.FormatNumber((long)System.Math.Round(currentBuffMagnitude));
            default:                        return "+" + (long)currentBuffMagnitude;
        }
    }

    string PunishAmountText()
    {
        switch (currentBuffType)
        {
            case BuffType.HutCapacity:      return "-" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.TruckCapacity:    return "-" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.ReproductionRate: return "-" + currentBuffMagnitude.ToString("0.#") + "%";
            case BuffType.HatchRate:        return "-" + currentBuffMagnitude.ToString("0.#") + "/s";
            case BuffType.Coin:             return "-" + GameManager.FormatNumber((long)System.Math.Round(currentBuffMagnitude));
            default:                        return "-" + (long)currentBuffMagnitude;
        }
    }

    // Shared icon lookup for currentBuffType — used by ApplyRewardIcons()
    // (popup/panel previews) and by the outcome toast.
    Sprite GetCurrentBuffIconSprite()
    {
        switch (currentBuffType)
        {
            case BuffType.HutCapacity:      return hutCapacityIconSprite;
            case BuffType.TruckCapacity:    return truckCapacityIconSprite;
            case BuffType.ReproductionRate: return reproductionIconSprite;
            case BuffType.HatchRate:        return hatchRateIconSprite;
            case BuffType.Coin:             return coinIconSprite;
            default:                        return carrotIconSprite;
        }
    }

    void ApplyRewardIcons()
    {
        Sprite icon = GetCurrentBuffIconSprite();
        if (buffIcon != null) buffIcon.sprite = icon;
        if (debuffIcon != null) debuffIcon.sprite = icon;
        if (activeBuffIcon != null) activeBuffIcon.sprite = icon;
        if (activeDebuffIcon != null) activeDebuffIcon.sprite = icon;

        ApplyCoinTextSize();
    }

    // Shrinks the buff/debuff preview font size only when the current buff
    // is Coin AND the amount exceeds 1000 (large numbers can overflow).
    void ApplyCoinTextSize()
    {
        if (!coinSizeCached)
        {
            if (buffPreviewText != null) buffPreviewBaseSize = buffPreviewText.fontSize;
            if (debuffPreviewText != null) debuffPreviewBaseSize = debuffPreviewText.fontSize;
            if (activeBuffPreviewText != null) activeBuffPreviewBaseSize = activeBuffPreviewText.fontSize;
            if (activeDebuffPreviewText != null) activeDebuffPreviewBaseSize = activeDebuffPreviewText.fontSize;
            coinSizeCached = true;
        }

        bool shrink = currentBuffType == BuffType.Coin && currentBuffMagnitude > 1000f;

        SetSize(buffPreviewText, shrink ? coinTextShrunkSize : buffPreviewBaseSize);
        SetSize(debuffPreviewText, shrink ? coinTextShrunkSize : debuffPreviewBaseSize);
        SetSize(activeBuffPreviewText, shrink ? coinTextShrunkSize : activeBuffPreviewBaseSize);
        SetSize(activeDebuffPreviewText, shrink ? coinTextShrunkSize : activeDebuffPreviewBaseSize);
    }

    static void SetSize(TextMeshProUGUI text, float size)
    {
        if (text == null) return;
        text.fontSize = size;
    }

    // ── Utility ─────────────────────────────────────────────────────────

    static string FormatTime(float seconds)
    {
        int s = Mathf.CeilToInt(Mathf.Max(0f, seconds));
        int m = s / 60;
        int r = s % 60;
        return string.Format("{0}:{1:00}", m, r);
    }

    // ── External Fade (called by ShopManager / PrestigeManager) ─────────

    // Closes the QuestPopup (accept/decline) and/or the ActiveQuestPanel
    // purely VISUALLY, without touching the underlying quest/challenge
    // state — same as OnCancelPanelPressed's hide-without-cancel
    // behavior. Called by InventoryPanel (and vice versa) to keep only
    // one piece of UI open at a time.
    public void ForceCloseQuestUI()
    {
        if (state == QuestState.PopupOpen) ClosePopup();

        if (panelIsOpen)
        {
            panelIsOpen = false;
            if (activePanelSlide != null) activePanelSlide.Hide(true);
            else if (activeQuestPanel != null) activeQuestPanel.SetActive(false);
            FadeNavButtonsIn();
        }
    }

    private Coroutine notificationFadeCo;
    private Coroutine activeButtonFadeCo;

    public void SetExternallyHidden(bool hidden)
    {
        float target = hidden ? 0f : 1f;

        // If we're being asked to UN-hide, but the player isn't currently
        // on this notification's home planet, the planet gate wins — stay
        // hidden. Otherwise closing Shop/BigBang/etc. while away would
        // undo RefreshPlanetGatedVisibility's hide and make a wrong-planet
        // notification tappable again.
        bool planetGateBlocksReveal = !hidden && state == QuestState.NotificationVisible && !IsOnHomePlanet();
        if (planetGateBlocksReveal) target = 0f;

        // NOTE: previously these blocks were gated behind
        // `.gameObject.activeInHierarchy`. That meant if the panel got
        // deactivated (e.g. a challenge ended) while it happened to be
        // faded out (e.g. Shop/BigBang was open), the "un-hide" call would
        // silently skip restoring alpha/interactable/blocksRaycasts —
        // leaving the CanvasGroup stuck invisible and non-interactable for
        // the *next* quest, even though its GameObject was reactivated.
        // We now always update the CanvasGroup state and let the coroutine
        // run regardless of active state (it runs on this MonoBehaviour,
        // not on the panel, so that's safe).
        if (notificationCanvasGroup != null)
        {
            bool interactableNow = !hidden && !planetGateBlocksReveal;
            notificationCanvasGroup.interactable = interactableNow;
            notificationCanvasGroup.blocksRaycasts = interactableNow;
            if (notificationFadeCo != null) StopCoroutine(notificationFadeCo);
            notificationFadeCo = StartCoroutine(FadeCanvasGroup(notificationCanvasGroup, target));
        }

        if (activeButtonCanvasGroup != null)
        {
            activeButtonCanvasGroup.interactable = !hidden;
            activeButtonCanvasGroup.blocksRaycasts = !hidden;
            if (activeButtonFadeCo != null) StopCoroutine(activeButtonFadeCo);
            activeButtonFadeCo = StartCoroutine(FadeCanvasGroup(activeButtonCanvasGroup, target));
        }
    }

    IEnumerator FadeCanvasGroup(CanvasGroup cg, float targetAlpha)
    {
        float startAlpha = cg.alpha;
        float elapsed = 0f;

        while (elapsed < externalFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / externalFadeDuration);
            cg.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        cg.alpha = targetAlpha;
    }

    // ── Nav Button Fades (Leaderboard/Achievements/Inventory/BigBang/
    // Settings) — hidden while QuestPopup (accept/decline) or
    // ActiveQuestPanel is open, shown again once either closes. ─────────

    void FadeNavButtonsOut()
    {
        FadeNavButton(leaderboardButtonCanvasGroup, ref navLeaderboardFadeCo, 0f);
        FadeNavButton(achievementButtonCanvasGroup, ref navAchievementFadeCo, 0f);
        FadeNavButton(inventoryButtonCanvasGroup, ref navInventoryFadeCo, 0f);
        FadeNavButton(bigBangButtonCanvasGroup, ref navBigBangFadeCo, 0f);
        FadeNavButton(settingsButtonCanvasGroup, ref navSettingsFadeCo, 0f);
    }

    void FadeNavButtonsIn()
    {
        FadeNavButton(leaderboardButtonCanvasGroup, ref navLeaderboardFadeCo, 1f);
        FadeNavButton(achievementButtonCanvasGroup, ref navAchievementFadeCo, 1f);
        FadeNavButton(inventoryButtonCanvasGroup, ref navInventoryFadeCo, 1f);
        FadeNavButton(bigBangButtonCanvasGroup, ref navBigBangFadeCo, 1f);
        FadeNavButton(settingsButtonCanvasGroup, ref navSettingsFadeCo, 1f);
    }

    void FadeNavButton(CanvasGroup group, ref Coroutine co, float targetAlpha)
    {
        if (group == null) return;
        if (co != null) StopCoroutine(co);
        co = StartCoroutine(FadeNavCanvasGroupRoutine(group, targetAlpha));
    }

    IEnumerator FadeNavCanvasGroupRoutine(CanvasGroup group, float targetAlpha)
    {
        bool showing = targetAlpha > 0.5f;
        if (!showing)
        {
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        float startAlpha = group.alpha;
        float elapsed = 0f;
        while (elapsed < navButtonFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / navButtonFadeDuration);
            group.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }
        group.alpha = targetAlpha;

        if (showing)
        {
            group.interactable = true;
            group.blocksRaycasts = true;
        }
    }

    // ── Public reporting API — call these from other systems ───────────
    // These maintain lifetime running totals; the active quest (if any)
    // reads the delta against its baseline snapshot every frame.

    // Each of these only actually moves quest progress if a quest of the
    // matching objective type is currently active AND the player is on
    // that quest's home planet right now (see ReportQuestProgress). Calls
    // from other planets, or while no matching quest is active, are safe
    // harmless no-ops.
    public void ReportFoxTapped()                { ReportQuestProgress(ObjectiveType.TapFoxes, 1); }
    public void ReportBatchSold()                { ReportQuestProgress(ObjectiveType.SellBatches, 1); }
    public void ReportRabbitsSold(long amount)   { ReportQuestProgress(ObjectiveType.SellRabbits, amount); }
    public void ReportHutUpgraded()              { ReportQuestProgress(ObjectiveType.UpgradeHuts, 1); }
    public void ReportCoinsSpent(long amount)    { ReportQuestProgress(ObjectiveType.SpendCoins, amount); }
    public void ReportHutBought()                { ReportQuestProgress(ObjectiveType.BuyHuts, 1); }
    public void ReportCarrotsSpent(long amount)  { ReportQuestProgress(ObjectiveType.SpendCarrots, amount); }
    // Hatching specifically requires ACTIVE presence on the home planet —
    // satisfied automatically here, since this is only ever called by
    // BarnSpawner's hold-to-hatch button, which only exists for whichever
    // planet's farm is currently displayed.
    public void ReportRabbitsHatched(long amount){ ReportQuestProgress(ObjectiveType.HatchRabbits, amount); }
}
