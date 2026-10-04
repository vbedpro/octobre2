using System.Text.RegularExpressions;

// Shared, whole-word-only blocklist check used by every player-typed name
// field (nickname, guild name, etc.) — NOT attached to any GameObject,
// just a plain static helper any script can call directly.
//
// Whole-word matching (via \b...\b) is deliberate: a plain substring check
// would false-positive constantly on words like "class", "pass", "glass",
// "assassin", or "overnight" (all of which contain "ass"/"nig" as a
// substring without being the blocked word itself). This only flags the
// blocked word when it appears on its own, not buried inside an unrelated
// word.
public static class ProfanityFilter
{
    // Add/remove words here — every call site (AuthenticationPopUp,
    // GuildCreationPanelUI, etc.) shares this single list.
    private static readonly string[] BlockedWords =
    {
        "nigger", "nigga", "nig",
        "cunt",
        "fag", "faggot",
        "chink",
        "bitch",
        "fuck", "fucking",
        "ass",
        "hoe", "whore",
        "nazi",
        "slut",
        "dick",
        "pussy",
    };

    private static readonly Regex[] CompiledPatterns = BuildPatterns();

    private static Regex[] BuildPatterns()
    {
        var patterns = new Regex[BlockedWords.Length];
        for (int i = 0; i < BlockedWords.Length; i++)
            patterns[i] = new Regex(@"\b" + Regex.Escape(BlockedWords[i]) + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return patterns;
    }

    // True if the text contains any blocked word as a standalone word
    // (case-insensitive). Safe to call with null/empty text.
    public static bool ContainsBlockedWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (Regex pattern in CompiledPatterns)
        {
            if (pattern.IsMatch(text))
                return true;
        }
        return false;
    }
}
