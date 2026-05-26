namespace KIDT.Services;

public class ThemeService
{
    private const string PreferenceKey = "app_theme";

    public string CurrentTheme { get; private set; }

    public event Action? ThemeChanged;

    public ThemeService()
    {
        CurrentTheme = Microsoft.Maui.Storage.Preferences.Get(PreferenceKey, "oliv");
    }

    public void SetTheme(string theme)
    {
        CurrentTheme = theme;
        Microsoft.Maui.Storage.Preferences.Set(PreferenceKey, theme);
        ThemeChanged?.Invoke();
    }
}
