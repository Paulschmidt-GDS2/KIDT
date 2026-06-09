namespace KIDT.Services;

public class ThemeService // Verwaltet das aktive App-Theme (persistiert via MAUI Preferences)
{
    private const string PreferenceKey = "app_theme";

    public string CurrentTheme { get; private set; } // Aktuell aktives Theme-ID

    public event Action? ThemeChanged; // Event: wird ausgelöst wenn Theme gewechselt wird

    public ThemeService() // Konstruktor: Lädt gespeichertes Theme aus Preferences
    {
        CurrentTheme = Microsoft.Maui.Storage.Preferences.Get(PreferenceKey, "oliv"); // Gespeichertes Theme laden (Fallback: oliv)
    }

    public void SetTheme(string theme) // Setzt neues Theme und persistiert es
    {
        CurrentTheme = theme; // Neues Theme setzen
        Microsoft.Maui.Storage.Preferences.Set(PreferenceKey, theme); // In Preferences speichern
        ThemeChanged?.Invoke(); // Alle Subscriber über Theme-Wechsel informieren
    }
}
