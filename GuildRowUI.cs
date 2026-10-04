using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections;

// One row in the Guilds list. Matches this hierarchy:
// GuildRow
//  ├─ Rank_Text
//  ├─ Logo (shield background + a shape icon, tinted per-guild)
//  │   ├─ ShieldBackground_Icon (fixed, same sprite every row)
//  │   └─ Shape_Icon (sprite swapped + tinted based on the guild's logoShape/logoColor)
//  ├─ GuildName_Text
//  ├─ ContestsWon_Text
//  ├─ LifetimeRabbits_Text
//  └─ Arrow_Icon (static, just indicates the row is tappable)
//
// The Button's own Transition is set to NONE (in code, below) — its
// automatic Highlighted state otherwise tints ANY row the pointer merely
// passes OVER while dragging to scroll, not just the one actually being
// pressed. We drive the press-tint ourselves instead, using the same
// Normal/Pressed colors already configured on the Button component, but
// only in response to a genuine press-and-release on THIS row — never
// from a drag passing over it.
//
// IMPORTANT: this does NOT implement IBeginDragHandler/IDragHandler/
// IEndDragHandler. An earlier version manually forwarded drag events to
// the parent ScrollRect to "help" scrolling work — that turned out to be
// the actual cause of a worse bug (the ScrollRect failing to snap back
// after an extreme overscroll drag). Unity already natively bubbles drag
// events up to the nearest ancestor that handles them (the ScrollRect)
// without any help — a plain Button doesn't block that on its own. Manually
// relaying events to ScrollRect's OnBeginDrag/OnDrag/OnEndDrag fed it
// unexpected internal state, since those methods are meant to be invoked
// BY Unity's EventSystem directly on the ScrollRect's own GameObject, not
// manually relayed from an unrelated child component.
public class GuildRowUI : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    [Header("Text")]
    public TextMeshProUGUI rankText;
    public TextMeshProUGUI guildNameText;
    public TextMeshProUGUI contestsWonText;
    public TextMeshProUGUI lifetimeRabbitsText;

    // Same thresholds/sizes as LeaderboardRowUI's rabbitsText, so the
    // Guilds tab's totals shrink identically to the Global leaderboard's.
    private const float RabbitsShortSize = 30f; // 0–13 characters (incl. commas)
    private const float RabbitsLongSize  = 25f; // 14 characters
    private const float RabbitsXLSize    = 21f; // 15+ characters
    private const int RabbitsLongThreshold = 14;
    private const int RabbitsXLThreshold   = 15;

    [Header("Logo")]
    [Tooltip("Flat-color background image sitting BEHIND the shape icon — sprite selected per guild (NOT a color tint).")]
    public Image logoBackgroundImage;
    [Tooltip("The shape icon on top of the background — sprite changes per guild too.")]
    public Image shapeIcon;
    [Tooltip("Sprites in the SAME ORDER as the GuildManager.GuildLogoShape enum (Circle, Square, Triangle, Star, Diamond).")]
    public Sprite[] shapeSprites;
    [Tooltip("Sprites in the SAME ORDER as the GuildManager.GuildLogoBackground enum.")]
    public Sprite[] backgroundSprites;

    [Header("Tap to view")]
    public Button rowButton;

    private GuildManager.GuildInfo currentGuild;
    private int currentRank;
    private bool isPressed = false;
    private Coroutine pressTintCoroutine;
    [Tooltip("Delay before showing the pressed tint. If a drag starts before this elapses, the tint never shows at all — eliminates the brief flash on what turns out to be a scroll instead of a tap.")]
    public float pressTintDelay = 0.08f;

    // Wire this up once — GuildsListUI calls SetOnClick per-row after
    // pooling/repositioning it, so this doesn't need to be re-wired on
    // every Set() call the way the button listener itself does.
    public System.Action<int, GuildManager.GuildInfo> onRowClicked;

    void OnDisable()
    {
        // Extra safety — if this row gets hidden (scrolled out of view)
        // mid-press, make sure nothing lingers to misfire once it's
        // reused/reactivated later.
        isPressed = false;
        if (pressTintCoroutine != null) { StopCoroutine(pressTintCoroutine); pressTintCoroutine = null; }
    }

    void Awake()
    {
        if (rowButton != null)
        {
            // Kill the built-in Highlighted/Pressed auto-tinting — it
            // responds to hover/passing-over, which is exactly what we
            // don't want during scroll. We drive the tint manually below,
            // reusing the same Normal/Pressed colors already set on the
            // Button in the Inspector.
            rowButton.transition = Selectable.Transition.None;
            rowButton.onClick.AddListener(() => onRowClicked?.Invoke(currentRank, currentGuild));
        }
    }

    public void Set(int rank, GuildManager.GuildInfo guild)
    {
        currentGuild = guild;
        currentRank = rank;

        // Force a clean visual state every time this pooled row gets
        // reused for different data — otherwise a delayed press-tint
        // coroutine started on the PREVIOUS occupant (before this row got
        // recycled mid-scroll) can fire late and incorrectly tint whatever
        // guild is showing here now, with no release event to ever clear
        // it.
        isPressed = false;
        if (pressTintCoroutine != null) { StopCoroutine(pressTintCoroutine); pressTintCoroutine = null; }
        if (rowButton != null) ApplyTint(rowButton.colors.normalColor);

        if (rankText != null) rankText.text = "#" + rank;
        if (guildNameText != null) guildNameText.text = guild.guildName;
        if (contestsWonText != null) contestsWonText.text = guild.contestsWon.ToString();
        if (lifetimeRabbitsText != null)
        {
            string formatted = GameManager.FormatFullNumber(guild.lifetimeRabbits);
            lifetimeRabbitsText.text = formatted;
            if (formatted.Length < RabbitsLongThreshold) lifetimeRabbitsText.fontSize = RabbitsShortSize;
            else if (formatted.Length < RabbitsXLThreshold) lifetimeRabbitsText.fontSize = RabbitsLongSize;
            else lifetimeRabbitsText.fontSize = RabbitsXLSize;
        }

        SetLogo(guild.logoShape, guild.logoColor, guild.logoIconTintColor);
    }

    void SetLogo(string shapeName, string backgroundName, string iconTintHex)
    {
        if (System.Enum.TryParse(shapeName, out GuildManager.GuildLogoShape shape))
        {
            int index = (int)shape;
            if (shapeIcon != null && shapeSprites != null && index >= 0 && index < shapeSprites.Length)
            {
                shapeIcon.sprite = shapeSprites[index];
                shapeIcon.color = (!string.IsNullOrEmpty(iconTintHex) && ColorUtility.TryParseHtmlString(iconTintHex, out Color tint)) ? tint : Color.white;
            }
        }

        if (System.Enum.TryParse(backgroundName, out GuildManager.GuildLogoBackground bg))
        {
            int index = (int)bg;
            if (logoBackgroundImage != null && backgroundSprites != null && index >= 0 && index < backgroundSprites.Length)
            {
                logoBackgroundImage.sprite = backgroundSprites[index];
                logoBackgroundImage.color = Color.white; // background is never tinted, only the icon is
            }
        }
    }

    // ---- Manual press visual (replaces Button's disabled auto-transition) ----
    public void OnPointerDown(PointerEventData eventData)
    {
        isPressed = true;

        // Don't tint immediately — wait a beat first. If this turns out to
        // be a drag, the pointer naturally leaves this row (OnPointerExit
        // below cancels the tint) well before this delay elapses in
        // practice, eliminating the brief flash before a scroll is
        // recognized.
        if (pressTintCoroutine != null) StopCoroutine(pressTintCoroutine);
        pressTintCoroutine = StartCoroutine(DelayedPressTint());
    }

    IEnumerator DelayedPressTint()
    {
        yield return new WaitForSeconds(pressTintDelay);
        if (isPressed) ApplyTint(rowButton.colors.pressedColor);
        pressTintCoroutine = null;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isPressed = false;
        if (pressTintCoroutine != null) { StopCoroutine(pressTintCoroutine); pressTintCoroutine = null; }
        ApplyTint(rowButton.colors.normalColor);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        // Pointer sliding off this row (e.g. mid-drag) without a proper
        // release — same as an ordinary Button visually reverting once the
        // pointer leaves, but only for a row that was actually pressed.
        if (isPressed)
        {
            isPressed = false;
            if (pressTintCoroutine != null) { StopCoroutine(pressTintCoroutine); pressTintCoroutine = null; }
            ApplyTint(rowButton.colors.normalColor);
        }
    }

    void ApplyTint(Color color)
    {
        if (rowButton != null && rowButton.targetGraphic != null)
            rowButton.targetGraphic.color = color;
    }
}
