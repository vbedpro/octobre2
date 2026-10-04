using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in the leaderboard list. Matches this hierarchy:
// LeaderboardRow
//  ├─ Rank_Text
//  ├─ Country_Icon
//  ├─ Name_Text
//  ├─ BlackLine1
//  ├─ BigBangs
//  │   ├─ BigBangs_Icon
//  │   └─ BigBangs_Text
//  ├─ BlackLine2
//  └─ TotalRabbits
//      ├─ Rabbit_Icon
//      └─ Rabbits_Text
//
// Also used for the player's own PINNED row at the bottom of the panel —
// same component, same Set() call, just always passed isOwnRow=true and a
// true global rank that isn't limited to the top-999 list.
public class LeaderboardRowUI : MonoBehaviour
{
    [Header("Rank — font size scales with digit count of the number (not counting '#')")]
    public TextMeshProUGUI rankText;
    private const float Rank1To3DigitSize = 42f;
    private const float Rank4DigitSize    = 32f;
    private const float Rank5DigitSize    = 28f;

    [Header("Country (placeholder sprite until flags are wired up)")]
    public Image countryIcon;

    [Header("Row Background (hidden unless a Background cosmetic is equipped)")]
    public Image backgroundImage;
    [Tooltip("Nickname font material used whenever a Background cosmetic is equipped (e.g. a stroke/outline tuned for busier backgrounds). Reverts to the default nickname material when no Background cosmetic is equipped.")]
    public Material nameMaterialWithBackground;

    [Header("Player Name (forced max 15 characters)")]
    public TextMeshProUGUI nameText;
    private const int MaxNameLength = 15;
    [Tooltip("Golden Shine cosmetic: width of the bright sweeping band, in CHARACTERS (not a fraction of the name). Keeping this in character units is what keeps the sweep looking smooth on short names (2-4 characters) instead of individual letters flashing on/off one at a time.")]
    public float goldenShineBandWidthChars = 1.25f;

    [Header("BigBang Count — font size scales with digit count")]
    public TextMeshProUGUI bigBangText;
    private const float BigBang1DigitSize     = 32f;
    private const float BigBang2DigitSize     = 28f;
    private const float BigBang3PlusDigitSize = 24f;

    [Header("Lifetime Rabbits — font size scales with formatted length")]
    public TextMeshProUGUI rabbitsText;
    private const float RabbitsShortSize = 30f; // 0–13 characters (incl. commas)
    private const float RabbitsLongSize  = 25f; // 14 characters
    private const float RabbitsXLSize    = 21f; // 15+ characters
    private const int RabbitsLongThreshold = 14;
    private const int RabbitsXLThreshold   = 15;

    [Header("Own-Row Highlight")]
    [Tooltip("Outline component on this row's background — enabled only when this row belongs to the local player.")]
    public Outline rowOutline;
    public Color ownRowColor = new Color32(0x00, 0x35, 0xF5, 0xFF);

    private Color defaultRankColor;
    private bool defaultColorCaptured = false;
    private Material defaultNameMaterial;

    // rank: 1-based. For top-999 list rows this is the row's position in
    // the sorted list; for the pinned personal row this is the player's
    // TRUE global rank, which can exceed 999.
    // countryFlag: pass null to leave whatever placeholder sprite is already on the prefab.
    // playerName: nickname, truncated defensively to 15 chars even though entry should already be enforced.
    // bigBangCount: player's prestige count.
    // lifetimeRabbits: player's cumulative all-time hatched rabbits (not current stock).
    // isOwnRow: true only for the row belonging to the local player — tints the rank text blue and enables the outline.
    // equippedNicknameId: itemId of this player's equipped Nickname cosmetic (from leaderboard metadata), or null/empty for none.
    // remoteNicknameColorIndex: the color THIS PLAYER actually cycled to (from their own submitted metadata) — used for every row except isOwnRow, where the local live count is used instead for immediate feedback.
    // equippedBackgroundId: itemId of this player's equipped Background cosmetic, or null/empty for none.
    public void Set(int rank, Sprite countryFlag, string playerName, int bigBangCount, long lifetimeRabbits, bool isOwnRow, string equippedNicknameId = null, string equippedBackgroundId = null, int remoteNicknameColorIndex = 0)
    {
        CaptureDefaultColorOnce();
        SetRank(rank);
        SetCountry(countryFlag);
        SetName(playerName);
        SetNicknameStyle(equippedNicknameId, isOwnRow, remoteNicknameColorIndex);
        SetBackgroundStyle(equippedBackgroundId);
        SetBigBangs(bigBangCount);
        SetRabbits(lifetimeRabbits);
        SetOwnRow(isOwnRow);
    }

    void CaptureDefaultColorOnce()
    {
        if (defaultColorCaptured || rankText == null) return;
        defaultRankColor = rankText.color;
        if (nameText != null) defaultNameMaterial = nameText.fontSharedMaterial;
        defaultColorCaptured = true;
    }

    // Currently applied Background cosmetic, if any (null = default white background).
    private CosmeticDefinition activeBackgroundCosmetic;

    // Applies the equipped Background cosmetic (static sprite or animated
    // Applies the equipped Background cosmetic (static sprite or animated
    // frame cycle), or HIDES the background image entirely if none is
    // equipped / cosmetic not found — no cosmetic means no visible panel
    // at all, not a plain white fallback.
    void SetBackgroundStyle(string equippedBackgroundId)
    {
        if (backgroundImage == null) return;

        CosmeticDefinition def = CosmeticDatabase.Instance != null
            ? CosmeticDatabase.Instance.GetById(equippedBackgroundId)
            : null;

        if (def == null || def.cosmeticType != CosmeticType.Background)
        {
            activeBackgroundCosmetic = null;
            backgroundImage.enabled = false;
            if (nameText != null && nameText.fontSharedMaterial != defaultNameMaterial)
                nameText.fontSharedMaterial = defaultNameMaterial;
            return;
        }

        activeBackgroundCosmetic = def;
        backgroundImage.enabled = true;

        // Only use the special material when the player has BOTH a
        // Background cosmetic AND a Nickname cosmetic equipped — pairing a
        // cosmetic background with the plain default nickname text keeps
        // the normal material instead.
        bool bothEquipped = activeNicknameCosmetic != null;
        Material targetMaterial = (bothEquipped && nameMaterialWithBackground != null) ? nameMaterialWithBackground : defaultNameMaterial;
        if (nameText != null && nameText.fontSharedMaterial != targetMaterial)
            nameText.fontSharedMaterial = targetMaterial;

        ApplyBackgroundFrame(); // paint immediately instead of waiting for next render
    }

    // Called every frame (via Canvas.willRenderCanvases, same as the
    // nickname styling) — no-op for Static backgrounds after the first
    // paint, cycles frames for Animated ones based on Time.time so it
    // stays continuous no matter how often Set() re-runs.
    void ApplyBackgroundFrame()
    {
        if (backgroundImage == null || activeBackgroundCosmetic == null) return;
        CosmeticDefinition def = activeBackgroundCosmetic;

        if (def.backgroundAnimationFrames != null && def.backgroundAnimationFrames.Length > 0)
        {
            float fps = Mathf.Max(def.backgroundFrameRate, 0.01f);
            int frameIndex = Mathf.FloorToInt(Time.time * fps) % def.backgroundAnimationFrames.Length;
            Sprite frame = def.backgroundAnimationFrames[frameIndex];
            if (frame != null && backgroundImage.sprite != frame)
                backgroundImage.sprite = frame;
        }
        else if (def.backgroundStaticSprite != null)
        {
            if (backgroundImage.sprite != def.backgroundStaticSprite)
                backgroundImage.sprite = def.backgroundStaticSprite;
        }
    }

    [Tooltip("Color shown when no Nickname cosmetic is equipped. Written directly into the text mesh, same as the cosmetics, so it renders correctly regardless of the font material's vertex color setting.")]
    public Color32 defaultNicknameColor = Color.black;

    // Applies the equipped Nickname cosmetic's style, or reverts to the
    // default black nickname if none is equipped / cosmetic not found.
    private CosmeticDefinition activeNicknameCosmetic;
    // Which cycling-color index to actually render for THIS row. For the
    // local player's own row (isOwnRow), we still read the live count from
    // CosmeticEquipManager directly for instant feedback the moment they
    // cycle. For every other player's row, that same local manager only
    // knows about the CURRENT DEVICE's own cycling history — querying it
    // for someone else's row always returned 0 (blue), regardless of what
    // color that player actually has equipped. Use their own submitted
    // remoteNicknameColorIndex instead.
    private bool activeNicknameIsOwnRow;
    private int activeNicknameRemoteColorIndex;
    void SetNicknameStyle(string equippedNicknameId, bool isOwnRow = false, int remoteColorIndex = 0)
    {
        if (nameText == null) return;

        CosmeticDefinition def = CosmeticDatabase.Instance != null
            ? CosmeticDatabase.Instance.GetById(equippedNicknameId)
            : null;

        if (def == null || def.cosmeticType != CosmeticType.Nickname)
        {
            activeNicknameCosmetic = null;
            nameText.enableVertexGradient = false;
            if (nameText.fontSharedMaterial != defaultNameMaterial) nameText.fontSharedMaterial = defaultNameMaterial;
            return;
        }

        activeNicknameCosmetic = def;
        activeNicknameIsOwnRow = isOwnRow;
        activeNicknameRemoteColorIndex = remoteColorIndex;
        if (nameText.fontSharedMaterial != defaultNameMaterial) nameText.fontSharedMaterial = defaultNameMaterial;
        ApplyNicknameFrame(); // paint immediately instead of waiting for next Update
    }

    // Repaints via Canvas.willRenderCanvases instead of Update(). This event
    // fires right before the frame actually renders — AFTER any mesh
    // regeneration ScrollRect/layout triggers while scrolling. Repainting
    // in Update() instead could lose that race and show one frame of the
    // font's raw (white) vertex colors, causing a flicker while scrolling.
    void OnEnable()
    {
        Canvas.willRenderCanvases += ApplyNicknameFrame;
        Canvas.willRenderCanvases += ApplyBackgroundFrame;
    }

    void OnDisable()
    {
        Canvas.willRenderCanvases -= ApplyNicknameFrame;
        Canvas.willRenderCanvases -= ApplyBackgroundFrame;
    }

    void ApplyNicknameFrame()
    {
        if (nameText == null) return;

        if (activeNicknameCosmetic == null)
        {
            // No cosmetic equipped — keep reasserting black every frame,
            // same as the cosmetics below, since TMP can silently reset
            // vertex colors on its own (e.g. mesh regenerated when the row
            // gets reactivated after a tab switch).
            ApplySolidVertexColor(defaultNicknameColor);
            return;
        }

        CosmeticDefinition def = activeNicknameCosmetic;

        switch (def.nicknameStyle)
        {
            case NicknameStyleType.Static:
                Color32 staticColor = def.nicknameTopColor;
                if (def.cyclingColors != null && def.cyclingColors.Length > 0)
                {
                    // count starts at 1 on first-ever activation, so index
                    // 0 (blue) is used first, index 1 (orange) on the next
                    // reactivation, and so on, looping via modulo. Own row
                    // uses the live local count (instant feedback); other
                    // rows use that player's own submitted color index.
                    int count = activeNicknameIsOwnRow && CosmeticEquipManager.Instance != null
                        ? CosmeticEquipManager.Instance.GetActivationCount(def.itemId)
                        : activeNicknameRemoteColorIndex;
                    int index = count > 0 ? (count - 1) % def.cyclingColors.Length : 0;
                    staticColor = def.cyclingColors[index];
                }
                ApplySolidVertexColor(staticColor);
                break;

            case NicknameStyleType.GoldenShine:
                ApplyGoldenShine(def);
                break;

            case NicknameStyleType.ColorFade:
                ApplyColorFade(def);
                break;

            case NicknameStyleType.Rainbow:
                ApplyRainbow(def);
                break;
        }
    }

    // Base gold color everywhere, with a bright band that sweeps left to
    // right once every shineIntervalSeconds, taking shineDurationSeconds
    // to cross. Driven by Time.time (not a per-instance accumulator) so
    // it stays continuous no matter how often Set()/SetNicknameStyle gets
    // called again (leaderboard refreshes, live popup updates, etc.).
    void ApplyGoldenShine(CosmeticDefinition def)
    {
        nameText.ForceMeshUpdate();
        TMP_TextInfo info = nameText.textInfo;
        int charCount = info.characterCount;
        if (charCount == 0) return;

        float cyclePos = Mathf.Repeat(Time.time, Mathf.Max(def.shineIntervalSeconds, 0.01f));
        // sweepT: 0..1 while the shine is actively crossing, else -1 (not sweeping this cycle).
        float sweepT = cyclePos <= def.shineDurationSeconds
            ? cyclePos / Mathf.Max(def.shineDurationSeconds, 0.01f)
            : -1f;

        for (int i = 0; i < charCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;

            Color32 col = def.nicknameTopColor;
            if (sweepT >= 0f)
            {
                // Work in CHARACTER units, not normalized 0..1 text position
                // (see PermanentGoldenShineText.ApplyFrame for the full
                // reasoning) — on a short nickname (2-4 characters), the old
                // normalized band (width 0.15 out of 0..1) was narrower than
                // the gap between adjacent characters, so only one letter
                // was ever lit at a time: it looked like a one-by-one flash
                // instead of a smooth sweep. A fixed character-width band
                // keeps the same physical width regardless of name length.
                float sweepPosChars = sweepT * Mathf.Max(charCount - 1, 0);
                float distChars = Mathf.Abs(i - sweepPosChars);
                float brightness = Mathf.Clamp01(1f - distChars / Mathf.Max(goldenShineBandWidthChars, 0.01f));
                col = Color32.Lerp(def.nicknameTopColor, def.shineColor, brightness);
            }

            SetCharacterColor(info, ch, col);
        }

        SafeUpdateVertexData();
    }

    // Whole nickname shifts together through blue -> purple -> red ->
    // orange -> yellow -> green -> blue, looping every fadeLoopSeconds.
    // Time.time-based — continuous regardless of how often Set() re-runs.
    void ApplyColorFade(CosmeticDefinition def)
    {
        Color32[] sequence =
        {
            new Color32(60, 120, 255, 255),  // blue
            new Color32(160, 60, 220, 255),  // purple
            new Color32(230, 50, 50, 255),   // red
            new Color32(255, 140, 30, 255),  // orange
            new Color32(255, 220, 40, 255),  // yellow
            new Color32(70, 200, 90, 255),   // green
        };

        float loop = Mathf.Max(def.fadeLoopSeconds, 0.01f);
        float t = Mathf.Repeat(Time.time, loop) / loop * sequence.Length;
        int i0 = Mathf.FloorToInt(t) % sequence.Length;
        int i1 = (i0 + 1) % sequence.Length;
        float localT = t - Mathf.Floor(t);

        Color32 col = Color32.Lerp(sequence[i0], sequence[i1], localT);
        ApplySolidVertexColor(col);
    }

    // Red/orange -> yellow/green -> blue/purple band pattern, animated so
    // it travels from left to right (colors appear at the start and crawl
    // rightward), looping every rainbowTravelSeconds. Time.time-based —
    // continuous regardless of how often Set() re-runs.
    void ApplyRainbow(CosmeticDefinition def)
    {
        nameText.ForceMeshUpdate();
        TMP_TextInfo info = nameText.textInfo;
        int charCount = info.characterCount;
        if (charCount == 0) return;

        float travel = Mathf.Max(def.rainbowTravelSeconds, 0.01f);
        // Shifts the hue pattern's phase over time so it crawls rightward.
        float phase = Mathf.Repeat(Time.time, travel) / travel;

        for (int i = 0; i < charCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;

            float charPos = charCount <= 1 ? 0f : (float)i / (charCount - 1);
            // Hue cycles across the name's length, offset by phase (subtracting
            // phase makes new colors enter from the left and travel right).
            // Mathf.Repeat makes this exactly seamless across the phase wrap.
            float hue = Mathf.Repeat(charPos - phase, 1f);
            Color32 col = Color.HSVToRGB(hue, 0.85f, 1f);

            SetCharacterColor(info, ch, col);
        }

        SafeUpdateVertexData();
    }

    // Writes one solid color into every visible character's vertex colors —
    // used for the default (no cosmetic) state, Static, and Color Fade.
    // Uses the same direct mesh-write mechanism as the animated cosmetics,
    // since the font material's vertex color setup only renders correctly
    // through this path (not through the plain nameText.color property).
    void ApplySolidVertexColor(Color32 color)
    {
        nameText.ForceMeshUpdate();
        TMP_TextInfo info = nameText.textInfo;
        int charCount = info.characterCount;

        for (int i = 0; i < charCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;
            SetCharacterColor(info, ch, color);
        }

        SafeUpdateVertexData();
    }

    // The FIRST time a freshly instantiated row gets populated (same frame
    // it's created/activated), TMP's internal CanvasRenderer/mesh setup
    // isn't always finished yet, and UpdateVertexData throws a
    // NullReferenceException deep inside TextMeshProUGUI itself. That
    // exception was uncaught, which aborted the REST of Set() too (rank,
    // name, rabbits never got applied) — this is why nothing showed up on
    // the very first open of the leaderboard/guilds list, and why closing
    // and reopening "fixed" it (TMP is already warmed up by then).
    // ApplyNicknameFrame/ApplyBackgroundFrame already re-run every frame
    // via Canvas.willRenderCanvases, so swallowing this one first-frame
    // failure just means the color paints one frame later instead of
    // crashing the whole row.
    void SafeUpdateVertexData()
    {
        try
        {
            nameText.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
        catch (System.NullReferenceException)
        {
            // Not ready yet — Canvas.willRenderCanvases will retry next frame.
        }
    }

    // Writes one character's 4 vertex colors directly into the TMP mesh.
    void SetCharacterColor(TMP_TextInfo info, TMP_CharacterInfo ch, Color32 color)
    {
        int matIndex = ch.materialReferenceIndex;
        int vertIndex = ch.vertexIndex;
        Color32[] colors = info.meshInfo[matIndex].colors32;

        colors[vertIndex + 0] = color;
        colors[vertIndex + 1] = color;
        colors[vertIndex + 2] = color;
        colors[vertIndex + 3] = color;
    }

    void SetRank(int rank)
    {
        if (rankText == null) return;
        int safeRank = Mathf.Max(rank, 1);

        if (safeRank > 99999)
        {
            rankText.text = "#100k+";
            rankText.fontSize = Rank5DigitSize; // same size as the longest normal case
            return;
        }

        rankText.text = "#" + safeRank;

        int digits = safeRank.ToString().Length;
        if (digits <= 3) rankText.fontSize = Rank1To3DigitSize;
        else if (digits == 4) rankText.fontSize = Rank4DigitSize;
        else rankText.fontSize = Rank5DigitSize; // 5+ digits
    }

    void SetCountry(Sprite countryFlag)
    {
        if (countryIcon == null) return;
        if (countryFlag != null) countryIcon.sprite = countryFlag;
        // else: leave the prefab's current placeholder sprite as-is.
    }

    void SetName(string playerName)
    {
        if (nameText == null) return;
        string safe = string.IsNullOrEmpty(playerName) ? "Farmer" : playerName;

        // UGS appends a "#XXXXX" discriminator to guarantee uniqueness
        // across players who pick the same base name — strip it for
        // display so the leaderboard just shows the nickname the player
        // actually typed. The real stored name (with discriminator) is
        // untouched server-side.
        int hashIndex = safe.IndexOf('#');
        if (hashIndex >= 0) safe = safe.Substring(0, hashIndex);

        if (safe.Length > MaxNameLength) safe = safe.Substring(0, MaxNameLength);
        nameText.text = safe;
    }

    void SetBigBangs(int count)
    {
        if (bigBangText == null) return;
        bigBangText.text = count.ToString();

        int digits = count.ToString().Length;
        if (digits <= 1) bigBangText.fontSize = BigBang1DigitSize;
        else if (digits == 2) bigBangText.fontSize = BigBang2DigitSize;
        else bigBangText.fontSize = BigBang3PlusDigitSize;
    }

    void SetRabbits(long lifetimeRabbits)
    {
        if (rabbitsText == null) return;
        string formatted = GameManager.FormatFullNumber(lifetimeRabbits); // includes thousands separators
        rabbitsText.text = formatted;
        if (formatted.Length < RabbitsLongThreshold) rabbitsText.fontSize = RabbitsShortSize;
        else if (formatted.Length < RabbitsXLThreshold) rabbitsText.fontSize = RabbitsLongSize;
        else rabbitsText.fontSize = RabbitsXLSize;
    }

    void SetOwnRow(bool isOwnRow)
    {
        if (rankText != null)
            rankText.color = isOwnRow ? ownRowColor : defaultRankColor;
        if (rowOutline != null)
            rowOutline.enabled = isOwnRow;
    }
}
