using UnityEngine;
using TMPro;

// Applies the same "Golden Shine" effect used by the Golden Nickname
// cosmetic (base gold color + a bright band sweeping left to right, on a
// loop) — but PERMANENTLY, to any TMP text, regardless of cosmetic equip
// state. Just drop this on a text component and it always shows gold.
//
// Same requirements as the nickname cosmetics: the font material's vertex
// color needs to be white for the color writes to actually show (see
// LeaderboardRowUI for the same note) — if this text renders solid black/
// white instead of gold, that's the fix.
public class PermanentGoldenShineText : MonoBehaviour
{
    public TMP_Text text;

    [Header("Golden Shine")]
    public Color baseColor = new Color(1f, 0.875f, 0f); // #FFDF00
    public Color shineColor = new Color(1f, 1f, 0.85f);
    [Tooltip("Seconds between each shine sweep.")]
    public float shineIntervalSeconds = 3f;
    [Tooltip("Seconds the sweep itself takes to cross the whole text.")]
    public float shineDurationSeconds = 0.6f;
    [Tooltip("Width of the bright band, in CHARACTERS (not a fraction of the text). Keeping this in character units — rather than normalized 0..1 text position — is what keeps the sweep looking like a smooth moving highlight instead of individual letters flashing on/off on short texts (2-4 characters), where the old normalized-width band was narrower than the gap between letters.")]
    public float shineBandWidthChars = 1.25f;

    void Reset()
    {
        text = GetComponent<TMP_Text>();
    }

    void OnEnable()
    {
        Canvas.willRenderCanvases += ApplyFrame;
    }

    void OnDisable()
    {
        Canvas.willRenderCanvases -= ApplyFrame;
    }

    void ApplyFrame()
    {
        if (text == null) return;

        text.ForceMeshUpdate();
        TMP_TextInfo info = text.textInfo;
        int charCount = info.characterCount;
        if (charCount == 0) return;

        float cyclePos = Mathf.Repeat(Time.time, Mathf.Max(shineIntervalSeconds, 0.01f));
        float sweepT = cyclePos <= shineDurationSeconds
            ? cyclePos / Mathf.Max(shineDurationSeconds, 0.01f)
            : -1f;

        for (int i = 0; i < charCount; i++)
        {
            TMP_CharacterInfo ch = info.characterInfo[i];
            if (!ch.isVisible) continue;

            Color32 col = baseColor;
            if (sweepT >= 0f)
            {
                // Work in CHARACTER units, not normalized 0..1 text position.
                // On a short string (2-4 characters), the old normalized
                // band (width 0.15 out of 0..1) was narrower than the gap
                // between adjacent characters' normalized positions, so only
                // one character was ever lit at a time — it looked like a
                // one-by-one flash instead of a sweep. Measuring distance in
                // characters keeps the band the same physical width
                // regardless of how many characters there are, so it always
                // covers (and blends across) more than one letter at once.
                float sweepPosChars = sweepT * Mathf.Max(charCount - 1, 0);
                float distChars = Mathf.Abs(i - sweepPosChars);
                float brightness = Mathf.Clamp01(1f - distChars / Mathf.Max(shineBandWidthChars, 0.01f));
                col = Color32.Lerp(baseColor, shineColor, brightness);
            }

            int vertIndex = ch.vertexIndex;
            Color32[] colors = info.meshInfo[ch.materialReferenceIndex].colors32;
            colors[vertIndex + 0] = col;
            colors[vertIndex + 1] = col;
            colors[vertIndex + 2] = col;
            colors[vertIndex + 3] = col;
        }

        text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }
}
