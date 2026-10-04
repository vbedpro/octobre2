using UnityEngine;

// Drives the brand-new-player tutorial. Starts the instant authentication
// succeeds (AuthenticationPopUp calls OnAuthenticationSuccess). Steps
// advance via the various ReportX() calls made by the relevant gameplay
// scripts (BarnSpawner, ShopManager, SellManager, ShopUpgradeRow).
//
// INPUT LOCKING: this script does NOT add guard-clauses to every
// interactive script in the game. Instead, it relies on a CanvasGroup
// architecture set up in the Editor:
//   - One MASTER CanvasGroup wraps your entire HUD Canvas (everything
//     except the world/camera view, which isn't UI and is unaffected).
//     Interactable/blocksRaycasts get set to FALSE for the whole tutorial.
//   - Each of the 7 tutorial target elements (hatch button, shop button,
//     the Tier 1 Truck Capacity buy button, shop close button, sell
//     button, sell slider, confirm sell button) gets its OWN small
//     CanvasGroup with "Ignore Parent Groups" checked — this lets it stay
//     interactable regardless of the master lock above it. Each target's
//     OWN CanvasGroup.interactable is then toggled true/false per step by
//     this script, so only the current step's element is ever clickable.
// Camera dragging is completely unaffected either way, since
// CameraController reads raw touch/mouse input directly, never routed
// through Canvas/EventSystem UI blocking at all.
public class TutorialManager : MonoBehaviour
{
    public static TutorialManager Instance { get; private set; }

    public enum TutorialStep
    {
        NotStarted,
        HatchRabbits,       // 1
        OpenShop,           // 2
        BuyTruckUpgrade,    // 3
        CloseShop,          // 4
        OpenSellPopup,      // 5
        SetSliderTo5,       // 6
        ConfirmSell,        // 7
        ShopAgain,          // 8 (tapping Shop here is what triggers 9/Completed)
        Completed
    }

    [Header("Hand Pointer")]
    public TutorialHandPointer handPointer;

    [Header("Master Lock (wraps the entire HUD Canvas)")]
    [Tooltip("Interactable/blocksRaycasts forced OFF for the whole tutorial duration — every target CanvasGroup below must have 'Ignore Parent Groups' checked to poke through this.")]
    public CanvasGroup masterLock;

    [Header("Step Targets — each needs its own CanvasGroup with 'Ignore Parent Groups' checked")]
    public RectTransform hatchButtonRect;
    public CanvasGroup hatchButtonGroup;

    public RectTransform shopButtonRect;
    public CanvasGroup shopButtonGroup;

    [Tooltip("The Tier 1 Truck Capacity row's buy button specifically — not the whole shop.")]
    public RectTransform truckUpgradeButtonRect;
    public CanvasGroup truckUpgradeButtonGroup;

    public RectTransform shopCloseButtonRect;
    public CanvasGroup shopCloseButtonGroup;

    public RectTransform sellButtonRect;
    public CanvasGroup sellButtonGroup;

    public RectTransform sellSliderRect;
    public CanvasGroup sellSliderGroup;

    public RectTransform confirmSellButtonRect;
    public CanvasGroup confirmSellButtonGroup;

    [Header("Hand Offsets Per Step (position offset from target, plus rotation)")]
    public Vector2 hatchButtonHandOffset = new Vector2(0f, -60f);
    public float hatchButtonHandRotation = 0f;

    public Vector2 shopButtonHandOffset = new Vector2(0f, -60f);
    public float shopButtonHandRotation = 0f;

    public Vector2 truckUpgradeHandOffset = new Vector2(0f, -60f);
    public float truckUpgradeHandRotation = 0f;

    public Vector2 shopCloseHandOffset = new Vector2(0f, -60f);
    public float shopCloseHandRotation = 0f;

    public Vector2 sellButtonHandOffset = new Vector2(0f, -60f);
    public float sellButtonHandRotation = 0f;

    public Vector2 sellSliderHandOffset = new Vector2(0f, -60f);
    public float sellSliderHandRotation = 0f;

    public Vector2 confirmSellHandOffset = new Vector2(0f, -60f);
    public float confirmSellHandRotation = 0f;

    [Header("Foxes")]
    [Tooltip("Disabled for the entire tutorial duration, re-enabled on completion.")]
    public FoxSpawner foxSpawner;

    [Header("Leaderboard Button")]
    [Tooltip("Same CanvasGroup ShopManager already uses to fade this button — completely hidden (alpha 0, non-interactable) for the entire tutorial, restored once finished.")]
    public CanvasGroup leaderboardButtonCanvasGroup;

    [Header("Achievements Button")]
    [Tooltip("Same treatment as the leaderboard button — completely hidden for the entire tutorial.")]
    public CanvasGroup achievementButtonCanvasGroup;

    [Header("Inventory Button")]
    [Tooltip("Same treatment as the leaderboard/achievements buttons — completely hidden for the entire tutorial.")]
    public CanvasGroup inventoryButtonCanvasGroup;

    [Header("Settings Button")]
    [Tooltip("Stays fully available/visible regardless of tutorial state — needs its own CanvasGroup with 'Ignore Parent Groups' checked so the master lock doesn't touch it at all, but this ALSO explicitly force-keeps it interactable every tick as a safety net.")]
    public CanvasGroup settingsButtonCanvasGroup;
    [Tooltip("The Settings PANEL's own CanvasGroup (its contents — sliders, dropdown, etc.) — also needs Ignore Parent Groups checked, and is force-kept interactable the same way the button is, so the panel is actually usable if opened during the tutorial.")]
    public CanvasGroup settingsPanelCanvasGroup;

    private const string StepKey = "RabbitUniverse_TutorialStep";

    public TutorialStep CurrentStep { get; private set; } = TutorialStep.NotStarted;
    public bool IsActive => CurrentStep != TutorialStep.NotStarted && CurrentStep != TutorialStep.Completed;

    private int hatchCount = 0;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        LoadStep();
        ApplyStepVisualsAndLocks();
    }

    // Called by AuthenticationPopUp once sign-in succeeds. Only actually
    // starts the tutorial for a genuinely brand-new player.
    public void OnAuthenticationSuccess()
    {
        if (CurrentStep == TutorialStep.NotStarted)
            AdvanceTo(TutorialStep.HatchRabbits);
    }

    [Header("Fresh-Restart Defaults")]
    [Tooltip("What totalCoins gets set to when restarting from an incomplete tutorial (matches GameManager's own brand-new-player default).")]
    public long freshStartCoins = 90;

    void LoadStep()
    {
        string saved = PlayerPrefs.GetString(StepKey, TutorialStep.NotStarted.ToString());
        if (!System.Enum.TryParse(saved, out TutorialStep step)) step = TutorialStep.NotStarted;

        // If the player left mid-tutorial (anything other than NotStarted
        // or Completed), restart EVERYTHING — not just the tutorial steps,
        // but all actual progress (rabbits, coins, huts, shop levels,
        // planets) — back to a genuinely fresh start. Authentication
        // itself is untouched — that's handled entirely separately by
        // AuthenticationPopUp (HasSetNicknameKey), so a returning
        // authenticated player never sees that flow again regardless.
        //
        // This only ever triggers for a player who never FINISHED the
        // tutorial in the first place (by construction — CurrentStep can
        // only be mid-way if the tutorial started and hasn't completed),
        // so wiping progress here can never affect a legitimate veteran
        // player's save.
        if (step != TutorialStep.NotStarted && step != TutorialStep.Completed)
        {
            RestartWithFullReset();
            step = TutorialStep.HatchRabbits;
        }

        CurrentStep = step;
    }

    // Must run BEFORE SaveManager's own LoadGame() call, which is
    // deliberately delayed by one frame (SaveManager.Start() does
    // `yield return null;` first) so it can override every other
    // manager's default Start() values. Since this runs THIS frame
    // (TutorialManager.Start() is not delayed), wiping the save file
    // here means LoadGame() finds nothing to load next frame and takes
    // its "fresh install" path — which never touches GameManager fields
    // at all — so the values we set here are left alone afterward.
    void RestartWithFullReset()
    {
        if (PrestigeManager.Instance != null)
            PrestigeManager.Instance.ResetForTutorialRestart(); // rabbits, coins(→0), huts, shop levels

        if (PlanetManager.Instance != null)
            PlanetManager.Instance.ResetAllPlanets(); // unlocked planets, trucks, roads back to Earth-only

        if (GameManager.Instance != null)
        {
            GameManager.Instance.totalCoins = freshStartCoins; // PerformReset above zeroes this — override to the real starting value
            GameManager.Instance.totalCarrots = 0;

            // Force a UI refresh — setting the fields directly (above)
            // doesn't trigger one on its own, since only AddCoins/
            // AddRabbits/AddCarrots do that. Same zero-amount trick
            // PlanetManager already uses elsewhere for this exact reason.
            GameManager.Instance.AddCoins(0);
            GameManager.Instance.AddRabbits(0);
            GameManager.Instance.AddCarrots(0);
        }

        SaveManager sm = FindAnyObjectByType<SaveManager>();
        if (sm != null) sm.WipeSaveDataOnly();
    }

    void SaveStep()
    {
        PlayerPrefs.SetString(StepKey, CurrentStep.ToString());
        PlayerPrefs.Save();
    }

    void AdvanceTo(TutorialStep step)
    {
        CurrentStep = step;
        SaveStep();
        ApplyStepVisualsAndLocks();
    }

    // Called by anything that force-closes the sell popup out from under
    // the tutorial (currently: SettingsPanelController.CloseOtherUI, since
    // opening Settings slides the sell popup away). Without this, a player
    // who opens Settings during the ConfirmSell step gets stuck: the sell
    // button that would let them reopen the popup is locked (the tutorial
    // still thinks the popup is open), and the confirm button inside the
    // now-closed popup is unreachable. Stepping back to OpenSellPopup
    // re-unlocks the sell button so they can simply reopen it and continue.
    public void NotifySellPopupClosedExternally()
    {
        if (CurrentStep == TutorialStep.ConfirmSell)
        {
            AdvanceTo(TutorialStep.OpenSellPopup);
        }
    }

    // Applies the master lock, unlocks only the current step's target
    // CanvasGroup, and repositions/re-styles the hand pointer.
    void ApplyStepVisualsAndLocks()
    {
        bool active = IsActive;

        if (masterLock != null)
        {
            masterLock.blocksRaycasts = !active;
        }

        if (foxSpawner != null) foxSpawner.enabled = !active;

        if (leaderboardButtonCanvasGroup != null)
        {
            leaderboardButtonCanvasGroup.alpha = active ? 0f : 1f;
            leaderboardButtonCanvasGroup.interactable = !active;
            leaderboardButtonCanvasGroup.blocksRaycasts = !active;
        }

        if (achievementButtonCanvasGroup != null)
        {
            achievementButtonCanvasGroup.alpha = active ? 0f : 1f;
            achievementButtonCanvasGroup.interactable = !active;
            achievementButtonCanvasGroup.blocksRaycasts = !active;
        }

        if (inventoryButtonCanvasGroup != null)
        {
            inventoryButtonCanvasGroup.alpha = active ? 0f : 1f;
            inventoryButtonCanvasGroup.interactable = !active;
            inventoryButtonCanvasGroup.blocksRaycasts = !active;
        }

        // Settings stays ALWAYS available, tutorial or not — explicitly
        // forced every time this runs, regardless of active/inactive.
        if (settingsButtonCanvasGroup != null)
        {
            settingsButtonCanvasGroup.alpha = 1f;
            settingsButtonCanvasGroup.interactable = true;
            settingsButtonCanvasGroup.blocksRaycasts = true;
        }

        if (settingsPanelCanvasGroup != null)
        {
            settingsPanelCanvasGroup.alpha = 1f;
            settingsPanelCanvasGroup.interactable = true;
            settingsPanelCanvasGroup.blocksRaycasts = true;
        }

        // Lock every target down first, then unlock only the current one.
        SetGroupInteractable(hatchButtonGroup, false);
        SetGroupInteractable(shopButtonGroup, false);
        SetGroupInteractable(truckUpgradeButtonGroup, false);
        SetGroupInteractable(shopCloseButtonGroup, false);
        SetGroupInteractable(sellButtonGroup, false);
        SetGroupInteractable(sellSliderGroup, false);
        SetGroupInteractable(confirmSellButtonGroup, false);

        if (!active)
        {
            if (handPointer != null) handPointer.Hide();

            // Every target above was just locked down (blocksRaycasts =
            // false) as part of the normal "reset before applying this
            // step" sequence — but since they all have Ignore Parent
            // Groups checked, restoring the MASTER lock alone doesn't
            // re-enable them; they need to be explicitly unlocked here too,
            // or they'd stay permanently unclickable forever after the
            // tutorial ends.
            SetGroupInteractable(hatchButtonGroup, true);
            SetGroupInteractable(shopButtonGroup, true);
            SetGroupInteractable(truckUpgradeButtonGroup, true);
            SetGroupInteractable(shopCloseButtonGroup, true);
            SetGroupInteractable(sellButtonGroup, true);
            SetGroupInteractable(sellSliderGroup, true);
            SetGroupInteractable(confirmSellButtonGroup, true);
            return;
        }

        switch (CurrentStep)
        {
            case TutorialStep.HatchRabbits:
                hatchCount = 0;
                SetGroupInteractable(hatchButtonGroup, true);
                handPointer?.ShowAt(hatchButtonRect, HandAnimStyle.Pulse, hatchButtonHandOffset, hatchButtonHandRotation);
                break;

            case TutorialStep.OpenShop:
                SetGroupInteractable(shopButtonGroup, true);
                handPointer?.ShowAt(shopButtonRect, HandAnimStyle.Pulse, shopButtonHandOffset, shopButtonHandRotation);
                break;

            case TutorialStep.BuyTruckUpgrade:
                SetGroupInteractable(truckUpgradeButtonGroup, true);
                handPointer?.ShowAt(truckUpgradeButtonRect, HandAnimStyle.Pulse, truckUpgradeHandOffset, truckUpgradeHandRotation);
                break;

            case TutorialStep.CloseShop:
                SetGroupInteractable(shopCloseButtonGroup, true);
                handPointer?.ShowAt(shopCloseButtonRect, HandAnimStyle.Pulse, shopCloseHandOffset, shopCloseHandRotation);
                break;

            case TutorialStep.OpenSellPopup:
                SetGroupInteractable(sellButtonGroup, true);
                handPointer?.ShowAt(sellButtonRect, HandAnimStyle.Pulse, sellButtonHandOffset, sellButtonHandRotation);
                break;

            case TutorialStep.ConfirmSell:
                SetGroupInteractable(confirmSellButtonGroup, true);
                handPointer?.ShowAt(confirmSellButtonRect, HandAnimStyle.Pulse, confirmSellHandOffset, confirmSellHandRotation);
                break;
        }
    }

    // Only touches blocksRaycasts, deliberately NEVER interactable — Unity's
    // Selectable/Button automatically plays its "Disabled Color" tint
    // whenever any ANCESTOR CanvasGroup.interactable is false (even though
    // we never touch alpha ourselves), which made every blocked button look
    // dimmed/ugly. blocksRaycasts alone correctly blocks clicks without
    // triggering that visual side effect — buttons stay looking completely
    // normal, they just don't respond to taps while blocked.
    static void SetGroupInteractable(CanvasGroup group, bool value)
    {
        if (group == null) return;
        group.blocksRaycasts = value;
    }

    // ── Called by gameplay scripts to report step completion ───────────

    public void ReportRabbitHatched()
    {
        if (CurrentStep != TutorialStep.HatchRabbits) return;
        hatchCount++;
        if (hatchCount >= 5) AdvanceTo(TutorialStep.OpenShop);
    }

    public void ReportShopOpened()
    {
        if (CurrentStep == TutorialStep.OpenShop) AdvanceTo(TutorialStep.BuyTruckUpgrade);
    }

    public void ReportTruckUpgradeBought()
    {
        if (CurrentStep != TutorialStep.BuyTruckUpgrade) return;
        AdvanceTo(TutorialStep.CloseShop);
    }

    public void ReportShopClosed()
    {
        if (CurrentStep != TutorialStep.CloseShop) return;
        AdvanceTo(TutorialStep.OpenSellPopup);
    }

    public void ReportSellPopupOpened()
    {
        if (CurrentStep != TutorialStep.OpenSellPopup) return;
        AdvanceTo(TutorialStep.ConfirmSell);
    }

    // No longer used in the flow — the slider already defaults to the
    // minimum sell amount (5), so there's nothing left for the player to
    // actually set. Left as a harmless no-op rather than removing it and
    // the enum value entirely, since SellManager still calls this on
    // every slider change; kept trivial/inert.
    public void ReportSliderChanged(long amount) { }

    public void ReportSellConfirmed()
    {
        if (CurrentStep != TutorialStep.ConfirmSell) return;
        AdvanceTo(TutorialStep.Completed);
    }

    // Used by ShopManager/SellManager to know whether THEY specifically
    // are allowed to close/scroll right now (the master lock alone isn't
    // enough for panels that need to stay OPEN with only one button
    // inside them unlocked, like the shop during BuyTruckUpgrade/CloseShop).
    public bool IsShopCloseAllowed() => CurrentStep == TutorialStep.CloseShop;
    public bool IsShopScrollAllowed() => !IsActive;
}
