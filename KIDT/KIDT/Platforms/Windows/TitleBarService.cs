using Microsoft.UI;
using Microsoft.UI.Windowing;
using KIDT.Services;

namespace KIDT.Platforms.Windows;

public class TitleBarService : ITitleBarService
{
    private AppWindow? _appWindow;
    private Microsoft.UI.Xaml.Window? _nativeWindow;

    // --- Farb-Tabellen pro Theme ---
    private readonly Dictionary<string, (byte R, byte G, byte B)> _bgColors;    // --clr-surface      (Sidebar-Hintergrund)
    private readonly Dictionary<string, (byte R, byte G, byte B)> _hoverColors; // --clr-surface-dark (Hover-Farbe für Min/Max)
    private readonly Dictionary<string, (byte R, byte G, byte B)> _fgColors;    // --clr-text

    public TitleBarService()
    {
        _bgColors = new Dictionary<string, (byte R, byte G, byte B)>();
        _bgColors.Add("oliv",    (132, 136, 119)); // #848877
        _bgColors.Add("schwarz", ( 30,  30,  30)); // #1e1e1e
        _bgColors.Add("weiss",   (242, 241, 236)); // #f2f1ec
        _bgColors.Add("blau",    ( 54,  56,  84)); // #363854
        _bgColors.Add("rot",     ( 84,  24,  24)); // #541818

        _hoverColors = new Dictionary<string, (byte R, byte G, byte B)>();
        _hoverColors.Add("oliv",    (108, 112,  97)); // #6C7061
        _hoverColors.Add("schwarz", ( 17,  17,  17)); // #111111
        _hoverColors.Add("weiss",   (228, 227, 221)); // #e4e3dd
        _hoverColors.Add("blau",    ( 40,  42,  62)); // #282a3e
        _hoverColors.Add("rot",     ( 61,  16,  16)); // #3d1010

        _fgColors = new Dictionary<string, (byte R, byte G, byte B)>();
        _fgColors.Add("oliv",    ( 13,  14,  12)); // #0D0E0C
        _fgColors.Add("schwarz", (224, 224, 220)); // #e0e0dc
        _fgColors.Add("weiss",   ( 26,  26,  24)); // #1a1a18
        _fgColors.Add("blau",    (200, 200, 248)); // #c8c8f8
        _fgColors.Add("rot",     (240, 208, 184)); // #f0d0b8
    }

    private void EnsureWindow() // Native Window und AppWindow einmalig lazy initialisieren
    {
        if (_appWindow != null)
        {
            return;
        }

        if (!AppWindowTitleBar.IsCustomizationSupported()) // Nur Win11+ unterstützt Farbanpassung
        {
            return;
        }

        Microsoft.Maui.Controls.Application? mauiApp = Microsoft.Maui.Controls.Application.Current;
        if (mauiApp == null || mauiApp.Windows.Count == 0)
        {
            return;
        }

        Microsoft.Maui.Controls.Window mauiWindow = mauiApp.Windows[0];
        if (mauiWindow.Handler == null)
        {
            return;
        }

        Microsoft.UI.Xaml.Window? nativeWindow = mauiWindow.Handler.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow == null)
        {
            return;
        }

        _nativeWindow = nativeWindow;

        IntPtr handle     = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow); // Window-Handle holen
        WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);  // Handle → WindowId
        _appWindow        = AppWindow.GetFromWindowId(windowId);                       // AppWindow abrufen
    }

    public void ApplyTheme(string themeId) // Titelleiste vollständig in Theme-Farbe einfärben
    {
        EnsureWindow();

        if (_appWindow == null || _nativeWindow == null)
        {
            return;
        }

        (byte R, byte G, byte B) bg;
        if (_bgColors.ContainsKey(themeId))
        {
            bg = _bgColors[themeId];
        }
        else
        {
            bg = _bgColors["oliv"]; // Fallback
        }

        (byte R, byte G, byte B) fg;
        if (_fgColors.ContainsKey(themeId))
        {
            fg = _fgColors[themeId];
        }
        else
        {
            fg = _fgColors["oliv"]; // Fallback
        }

        (byte R, byte G, byte B) hover;
        if (_hoverColors.ContainsKey(themeId))
        {
            hover = _hoverColors[themeId];
        }
        else
        {
            hover = _hoverColors["oliv"]; // Fallback
        }

        global::Windows.UI.Color bgColor    = global::Windows.UI.Color.FromArgb(255, bg.R,    bg.G,    bg.B);
        global::Windows.UI.Color hoverColor = global::Windows.UI.Color.FromArgb(255, hover.R, hover.G, hover.B);
        global::Windows.UI.Color fgColor    = global::Windows.UI.Color.FromArgb(255, fg.R,    fg.G,    fg.B);

        // Linker Teil: Wurzelelement des WinUI-Fensters einfärben (scheint im Titelbalken durch)
        Microsoft.UI.Xaml.Controls.Panel? rootPanel = _nativeWindow.Content as Microsoft.UI.Xaml.Controls.Panel;
        if (rootPanel != null)
        {
            rootPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bgColor);
        }

        // Rechter Teil: Caption-Buttons — Ruhezustand, Hover (dunkler), Pressed, Inaktiv
        _appWindow.TitleBar.ButtonBackgroundColor         = bgColor;
        _appWindow.TitleBar.ButtonForegroundColor         = fgColor;
        _appWindow.TitleBar.ButtonHoverBackgroundColor    = hoverColor; // --clr-surface-dark beim Hover
        _appWindow.TitleBar.ButtonHoverForegroundColor    = fgColor;
        _appWindow.TitleBar.ButtonPressedBackgroundColor  = hoverColor; // gleicher Ton beim Drücken
        _appWindow.TitleBar.ButtonPressedForegroundColor  = fgColor;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = bgColor;
        _appWindow.TitleBar.ButtonInactiveForegroundColor = fgColor;
    }
}
