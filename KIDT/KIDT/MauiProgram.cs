using Microsoft.Extensions.Logging;
using KIDT.Services;
using KIDT.Database;
using Microsoft.EntityFrameworkCore;

namespace KIDT
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp() // Erstellt und konfiguriert die MAUI-App inkl. DI und DB-Initialisierung
        {
            var builder = MauiApp.CreateBuilder(); // App-Builder initialisieren
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); // Standard-Schriftart registrieren
                });

            builder.Services.AddMauiBlazorWebView(); // Blazor-WebView für MAUI aktivieren

            // DbContext als Transient (jede Operation bekommt eigene Instanz - wichtig für MAUI!)
            builder.Services.AddDbContext<ChatDbContext>(ServiceLifetime.Transient);

            // Services registrieren
            builder.Services.AddSingleton<ChatCoordinator>(); // Singleton: koordiniert Chat-Anfragen und Streaming
            builder.Services.AddTransient<ChatDbService>(); // Transient statt Scoped (MAUI hat keinen echten Scope!)
            builder.Services.AddTransient<DocumentDbService>(); // Transient statt Scoped (MAUI hat keinen echten Scope!)
            builder.Services.AddTransient<CalendarService>(); // Service für Kalender-Termine
            builder.Services.AddTransient<FolderDbService>(); // Service für Ordner-Verwaltung
            builder.Services.AddSingleton<ThumbnailGenerator>(); // Singleton: erzeugt Vorschaubilder für Dokumente
            builder.Services.AddSingleton<AppNotificationService>(); // Singleton für Termin-Benachrichtigungen
            builder.Services.AddSingleton<KIDT.Services.ThemeService>(); // Theme-System (visuell)
            builder.Services.AddSingleton<KIDT.Services.ITitleBarService, KIDT.Platforms.Windows.TitleBarService>(); // Titelleisten-Farbe pro Theme

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools(); // Browser-DevTools in der WebView aktivieren
            builder.Logging.AddDebug(); // Debug-Logging aktivieren
#endif

            var app = builder.Build(); // App zusammenbauen

            // Einmalige DB-Initialisierung beim App-Start
            using (var scope = app.Services.CreateScope()) // Temporären Scope für Startup-Operationen erstellen
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>(); // DB-Context für Schema-Init holen
                dbContext.Database.EnsureCreated(); // Erstelle Tabellen wenn nicht vorhanden

                // Migriere Datenbank-Schema (füge neue Spalten hinzu falls nötig)
                var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();
                Task.Run(calendarService.EnsureDatabaseSchemaAsync).Wait(); // Task.Run auf Thread-Pool: verhindert UI-Thread-Deadlock in MAUI

                var folderService = scope.ServiceProvider.GetRequiredService<FolderDbService>();
                Task.Run(folderService.EnsureDatabaseSchemaAsync).Wait(); // Folders-Tabelle + FolderId-Spalte sicherstellen

                // Einmalige Bereinigung: [DocID:]-Marker aus alten gespeicherten Chat-Nachrichten entfernen
                var chatDbService = scope.ServiceProvider.GetRequiredService<ChatDbService>();
                Task.Run(chatDbService.CleanDocIdMarkersFromMessagesAsync).Wait(); // Standalone-Marker löschen, eingebettete Marker herausschneiden
            }

            return app; // Fertige App zurückgeben
        }
    }
}
