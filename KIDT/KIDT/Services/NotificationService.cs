using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Services;

public class AppNotificationService : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private System.Threading.Timer? _timer;
    private bool _isRunning = false;
    private bool _startupNotificationShown = false;

    public event Action<NotificationData>? OnNotificationRequested;

    public AppNotificationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _timer = new System.Threading.Timer(
            async _ => await CheckForDueRemindersAsync(),
            null,
            TimeSpan.FromSeconds(5),  // Start nach 5 Sekunden
            TimeSpan.FromSeconds(30)  // Wiederhole alle 30 Sekunden
        );

        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Timer gestartet - prüfe alle 30s auf fällige Erinnerungen");
    }

    public async Task ShowStartupNotificationAsync()
    {
        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] ShowStartupNotificationAsync aufgerufen - _startupNotificationShown={_startupNotificationShown}");

        if (_startupNotificationShown)
        {
            System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Startup-Notification bereits gezeigt - Abbruch");
            return;
        }

        _startupNotificationShown = true;

        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Lade Termine aus DB...");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var now = DateTime.Now;
        var upcomingEvents = await dbContext.CalendarEvents
            .Where(e => e.Start >= now.Date)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Time)
            .Take(3)
            .ToListAsync();

        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] {upcomingEvents.Count} Termine gefunden");

        var notification = new NotificationData
        {
            Type = NotificationType.StartupOverview,
            Events = upcomingEvents
        };

        System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Feuere OnNotificationRequested Event - Subscribers: {OnNotificationRequested?.GetInvocationList().Length ?? 0}");
        OnNotificationRequested?.Invoke(notification);
        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Event gefeuert");
    }

    private async Task CheckForDueRemindersAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var now = DateTime.Now;

            var dueEvents = await dbContext.CalendarEvents
                .Where(e => e.ReminderMinutesBefore != null && !e.ReminderShown)
                .ToListAsync();

            foreach (var ev in dueEvents)
            {
                DateTime reminderTime;

                if (ev.HasTime && !ev.IsAllDay)
                {
                    var eventDateTime = ev.Start.Date + ev.Time;
                    reminderTime = eventDateTime.AddMinutes(-ev.ReminderMinutesBefore!.Value);
                }
                else
                {
                    reminderTime = ev.Start.Date.AddMinutes(-ev.ReminderMinutesBefore!.Value);
                }

                if (now >= reminderTime && now < reminderTime.AddMinutes(5))
                {
                    System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Erinnerung fällig für: {ev.Title} (ID: {ev.Id})");

                    ev.ReminderShown = true;
                    dbContext.CalendarEvents.Update(ev);
                    await dbContext.SaveChangesAsync();

                    var notification = new NotificationData
                    {
                        Type = NotificationType.EventReminder,
                        Events = new List<CalendarEvent> { ev }
                    };

                    OnNotificationRequested?.Invoke(notification);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATION_SERVICE] Fehler beim Prüfen: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _isRunning = false;
        System.Diagnostics.Debug.WriteLine("[NOTIFICATION_SERVICE] Service beendet");
    }
}
