using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Services;

public class AppNotificationService : IDisposable // Service für App-weite Benachrichtigungen (Startup-Overview + fällige Erinnerungen)
{
    private readonly IServiceProvider _serviceProvider;
    private System.Threading.Timer? _timer;
    private bool _isRunning = false;
    private bool _startupNotificationShown = false;

    public event Action<NotificationData>? OnNotificationRequested;

    public AppNotificationService(IServiceProvider serviceProvider) // Konstruktor: Dependency Injection
    {
        _serviceProvider = serviceProvider; // Speichere Service Provider für spätere Scope-Erstellung
    }

    public void Start() // Startet Timer für Erinnerungs-Prüfung
    {
        if (_isRunning) return; // Bereits gestartet? Abbruch

        _isRunning = true; // Setze Flag
        _timer = new System.Threading.Timer( // Erstelle Timer
            async _ => await CheckForDueRemindersAsync(), // Callback: Prüfe auf fällige Erinnerungen
            null, // Kein State
            TimeSpan.FromSeconds(5),  // Start nach 5 Sekunden (UI-Initialisierung abwarten)
            TimeSpan.FromSeconds(30)  // Wiederhole alle 30 Sekunden
        );

        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Timer gestartet - prüfe alle 30s auf fällige Erinnerungen");
    }

    public async Task ShowStartupNotificationAsync() // Zeigt Startup-Benachrichtigung mit nächsten 3 Terminen
    {
        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] ShowStartupNotificationAsync aufgerufen - _startupNotificationShown={_startupNotificationShown}");

        if (_startupNotificationShown) // Bereits gezeigt?
        {
            System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Startup-Notification bereits gezeigt - Abbruch");
            return; // Abbruch: Nur 1x pro App-Start zeigen
        }

        _startupNotificationShown = true; // Setze Flag (verhindert doppelte Anzeige)

        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Lade Termine aus DB...");

        using var scope = _serviceProvider.CreateScope(); // Erstelle DB-Scope
        var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>(); // Hole DB-Context

        var now = DateTime.Now; // Aktueller Zeitpunkt
        var upcomingEvents = await dbContext.CalendarEvents // Lade nächste 3 Termine
            .Where(e => e.Start >= now.Date) // Filter: Heute oder später
            .OrderBy(e => e.Start) // Sortiere nach Datum
            .ThenBy(e => e.Time) // Bei gleichem Datum: Nach Uhrzeit
            .Take(3) // Nimm nur die ersten 3
            .ToListAsync(); // Führe Query aus

        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] {upcomingEvents.Count} Termine gefunden");

        var notification = new NotificationData // Erstelle Notification-Objekt
        {
            Type = NotificationType.StartupOverview, // Typ: Startup-Overview
            Events = upcomingEvents // Liste der nächsten 3 Termine
        };

        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Feuere OnNotificationRequested Event - Subscribers: {OnNotificationRequested?.GetInvocationList().Length ?? 0}");
        OnNotificationRequested?.Invoke(notification); // Feuere Event (MainLayout empfängt und zeigt Notification-Component)
        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Event gefeuert");
    }

    private async Task CheckForDueRemindersAsync() // Prüft alle 30s ob Erinnerungen fällig sind
    {
        try
        {
            using var scope = _serviceProvider.CreateScope(); // Erstelle DB-Scope
            var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>(); // Hole DB-Context

            var now = DateTime.Now; // Aktueller Zeitpunkt

            var dueEvents = await dbContext.CalendarEvents // Lade Termine mit noch nicht gezeigten Erinnerungen
                .Where(e => e.ReminderMinutesBefore != null && !e.ReminderShown) // Filter: Hat Erinnerung UND noch nicht gezeigt
                .ToListAsync(); // Führe Query aus

            foreach (var ev in dueEvents) // Durchlaufe alle Kandidaten
            {
                DateTime reminderTime; // Variable für berechneten Erinnerungszeitpunkt

                if (ev.HasTime && !ev.IsAllDay) // Termin mit Uhrzeit?
                {
                    var eventDateTime = ev.Start.Date + ev.Time; // Kombiniere Datum + Uhrzeit
                    reminderTime = eventDateTime.AddMinutes(-ev.ReminderMinutesBefore!.Value); // Subtrahiere Erinnerungszeit
                }
                else // Ganztägiger Termin oder ohne Uhrzeit
                {
                    reminderTime = ev.Start.Date.AddMinutes(-ev.ReminderMinutesBefore!.Value); // Erinnerung ab Mitternacht
                }

                if (now >= reminderTime && now < reminderTime.AddMinutes(5)) // Erinnerung fällig? (5-Minuten-Fenster)
                {
                    System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Erinnerung fällig für: {ev.Title} (ID: {ev.Id})");

                    ev.ReminderShown = true; // Markiere Erinnerung als gezeigt
                    dbContext.CalendarEvents.Update(ev); // Update in DB
                    await dbContext.SaveChangesAsync(); // Speichere Änderung

                    var notification = new NotificationData // Erstelle Notification-Objekt
                    {
                        Type = NotificationType.EventReminder, // Typ: Event-Erinnerung
                        Events = new List<CalendarEvent> { ev } // Einzelnes Event in Liste
                    };

                    OnNotificationRequested?.Invoke(notification); // Feuere Event (MainLayout zeigt Notification)
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Fehler beim Prüfen: {ex.Message}");
        }
    }

    public void Dispose() // Räumt Service auf beim Beenden
    {
        _timer?.Dispose(); // Timer stoppen und freigeben
        _isRunning = false; // Setze Flag zurück
        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Service beendet");
    }
}