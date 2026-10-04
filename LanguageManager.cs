using UnityEngine;
using System.Collections.Generic;

// FOUNDATION ONLY — this handles selecting/persisting a language and
// notifying the rest of the game when it changes. It does NOT translate
// any actual UI text — that's a much larger separate task (every string
// in the game needs a translated version, typically via Unity's
// Localization package or a custom string-table system, with each UI
// Text/TextMeshPro component reading from that table instead of having
// hardcoded English text). Wiring the DROPDOWN and PERSISTING the choice
// is the easy 10% of "adding language support" — translating the actual
// game is the other 90%, and needs your own translated text before any
// code can use it.
public class LanguageManager : MonoBehaviour
{
    public static LanguageManager Instance { get; private set; }

    public enum Language { English, French, Spanish, German, Portuguese }

    public Language CurrentLanguage { get; private set; } = Language.English;

    // Fired whenever the language changes — any script displaying
    // translatable text should subscribe to this and refresh itself.
    public static event System.Action<Language> OnLanguageChanged;

    private const string LanguageKey = "RabbitUniverse_Language";

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        DontDestroyOnLoad(gameObject);

        int saved = PlayerPrefs.GetInt(LanguageKey, (int)Language.English);
        CurrentLanguage = (Language)saved;
    }

    public void SetLanguage(Language language)
    {
        CurrentLanguage = language;
        PlayerPrefs.SetInt(LanguageKey, (int)language);
        PlayerPrefs.Save();
        OnLanguageChanged?.Invoke(language);
    }

    public List<string> GetLanguageDisplayNames()
    {
        return new List<string> { "English", "Français", "Español", "Deutsch", "Português" };
    }
}
