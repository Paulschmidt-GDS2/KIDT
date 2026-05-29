using Microsoft.Extensions.Logging;
using KIDT.Services;
using KIDT.Database;
using Microsoft.EntityFrameworkCore;

namespace KIDT
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            // DbContext als Transient (jede Operation bekommt eigene Instanz - wichtig für MAUI!)
            builder.Services.AddDbContext<ChatDbContext>(ServiceLifetime.Transient);

            // Services registrieren
            builder.Services.AddSingleton<ChatCoordinator>();
            builder.Services.AddTransient<ChatDbService>(); // Transient statt Scoped (MAUI hat keinen echten Scope!)
            builder.Services.AddTransient<DocumentDbService>(); // Transient statt Scoped (MAUI hat keinen echten Scope!)
            builder.Services.AddTransient<CalendarService>(); // Service für Kalender-Termine
            builder.Services.AddSingleton<ThumbnailGenerator>();
            builder.Services.AddSingleton<AppNotificationService>(); // Singleton für Termin-Benachrichtigungen
            builder.Services.AddSingleton<KIDT.Services.ThemeService>(); // Theme-System (visuell)
            builder.Services.AddSingleton<KIDT.Services.ITitleBarService, KIDT.Platforms.Windows.TitleBarService>(); // Titelleisten-Farbe pro Theme

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            var app = builder.Build();

            // Einmalige DB-Initialisierung beim App-Start
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                dbContext.Database.EnsureCreated(); // Erstelle Tabellen wenn nicht vorhanden

                // Migriere Datenbank-Schema (füge neue Spalten hinzu falls nötig)
                var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();
                Task.Run(async () => await calendarService.EnsureDatabaseSchemaAsync()).Wait(); // Synchron warten
            }

            return app;
        }
    }
}
