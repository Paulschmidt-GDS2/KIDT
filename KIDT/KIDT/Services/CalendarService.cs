using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Services;

public class CalendarService // Service für Kalender-Datenbankoperationen
{
    private readonly ChatDbContext _dbContext;

    public CalendarService(ChatDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Lädt alle Termine aus der Datenbank
    public async Task<List<CalendarEvent>> GetAllEventsAsync()
    {
        return await _dbContext.CalendarEvents
            .OrderBy(e => e.Start)
            .ToListAsync();
    }

    // Lädt einen Termin per ID
    public async Task<CalendarEvent?> GetEventByIdAsync(int eventId)
    {
        return await _dbContext.CalendarEvents
            .FirstOrDefaultAsync(e => e.Id == eventId);
    }

    // Lädt Termine für einen bestimmten Datumsbereich
    public async Task<List<CalendarEvent>> GetEventsByDateRangeAsync(DateTime start, DateTime end)
    {
        return await _dbContext.CalendarEvents
            .Where(e => e.Start >= start && e.Start <= end)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Time)
            .ToListAsync();
    }

    // Fügt einen neuen Termin hinzu
    public async Task<CalendarEvent> AddEventAsync(CalendarEvent calendarEvent)
    {
        calendarEvent.CreatedAt = DateTime.Now;
        _dbContext.CalendarEvents.Add(calendarEvent);
        await _dbContext.SaveChangesAsync();
        return calendarEvent;
    }

    // Aktualisiert einen bestehenden Termin
    public async Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent)
    {
        calendarEvent.UpdatedAt = DateTime.Now;
        _dbContext.CalendarEvents.Update(calendarEvent);
        await _dbContext.SaveChangesAsync();
        return calendarEvent;
    }

    // Löscht einen Termin
    public async Task DeleteEventAsync(int eventId)
    {
        var calendarEvent = await _dbContext.CalendarEvents.FindAsync(eventId);
        if (calendarEvent != null)
        {
            _dbContext.CalendarEvents.Remove(calendarEvent);
            await _dbContext.SaveChangesAsync();
        }
    }

    // Löscht einen Termin direkt (ohne ID-Lookup)
    public async Task DeleteEventDirectAsync(CalendarEvent calendarEvent)
    {
        _dbContext.CalendarEvents.Remove(calendarEvent);
        await _dbContext.SaveChangesAsync();
    }

    // Migriert Datenbank-Schema (fügt fehlende Spalten hinzu)
    public async Task EnsureDatabaseSchemaAsync()
    {
        try
        {
            // Versuche einfach die Spalten hinzuzufügen - wenn sie existieren, gibt's einen Fehler den wir ignorieren
            try
            {
                await _dbContext.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE CalendarEvents 
                    ADD COLUMN ReminderMinutesBefore INT NULL;
                ");

                await _dbContext.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE CalendarEvents 
                    ADD COLUMN ReminderShown TINYINT(1) NOT NULL DEFAULT 0;
                ");

                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Datenbank-Schema erfolgreich aktualisiert (ReminderMinutesBefore, ReminderShown hinzugefügt)");
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060) // Error 1060: Duplicate column name
            {
                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Spalten existieren bereits - kein Update nötig");
            }

            // Füge EventIdsJson zu Messages hinzu
            try
            {
                await _dbContext.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE Messages 
                    ADD COLUMN EventIdsJson TEXT NULL;
                ");

                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Messages-Tabelle erweitert (EventIdsJson hinzugefügt)");
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060)
            {
                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] EventIdsJson existiert bereits");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CALENDAR_SERVICE] Fehler bei Schema-Migration: {ex.Message}");
        }
    }
}
