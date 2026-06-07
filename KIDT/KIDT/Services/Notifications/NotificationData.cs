using KIDT.Models;

namespace KIDT.Models;

public enum NotificationType
{
    EventReminder,    // Einzelner Termin-Reminder (rechts unten)
    StartupOverview   // Startup-Overview mit nächsten 3 Terminen (mittig oben)
}

public class NotificationData
{
    public NotificationType Type { get; set; }
    public List<CalendarEvent> Events { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
