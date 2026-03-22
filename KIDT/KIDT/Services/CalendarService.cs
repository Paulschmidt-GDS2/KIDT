using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Services;

public class CalendarService // Service für Kalender-Datenbankoperationen
{
    private readonly ChatDbContext _dbContext;

    public CalendarService(ChatDbContext dbContext) // Konstruktor: Dependency Injection
    {
        _dbContext = dbContext; // Speichere DB-Context
    }

    // Lädt alle Termine aus der Datenbank
    public async Task<List<CalendarEvent>> GetAllEventsAsync() // Gibt alle Termine sortiert zurück
    {
        return await _dbContext.CalendarEvents // Query auf CalendarEvents-Tabelle
            .OrderBy(e => e.Start) // Sortiere nach Start-Datum
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    // Lädt einen Termin per ID
    public async Task<CalendarEvent?> GetEventByIdAsync(int eventId) // Gibt Event oder null zurück
    {
        return await _dbContext.CalendarEvents // Query auf CalendarEvents-Tabelle
            .FirstOrDefaultAsync(e => e.Id == eventId); // Finde ersten mit passender ID (oder null)
    }

    // Lädt Termine für einen bestimmten Datumsbereich
    public async Task<List<CalendarEvent>> GetEventsByDateRangeAsync(DateTime start, DateTime end) // Gibt Events im Zeitraum zurück
    {
        return await _dbContext.CalendarEvents // Query auf CalendarEvents-Tabelle
            .Where(e => e.Start >= start && e.Start <= end) // Filter: Zwischen start und end
            .OrderBy(e => e.Start) // Sortiere nach Datum
            .ThenBy(e => e.Time) // Bei gleichem Datum: Nach Uhrzeit
            .ToListAsync(); // Führe Query aus
    }

    // Fügt einen neuen Termin hinzu
    public async Task<CalendarEvent> AddEventAsync(CalendarEvent calendarEvent) // Speichert Event in DB und gibt es zurück
    {
        calendarEvent.CreatedAt = DateTime.Now; // Setze Erstellungs-Zeitstempel
        _dbContext.CalendarEvents.Add(calendarEvent); // Füge zur DB hinzu
        await _dbContext.SaveChangesAsync(); // Speichere Änderungen
        return calendarEvent; // Gib Event zurück (mit generierter ID)
    }

    // Aktualisiert einen bestehenden Termin
    public async Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent) // Speichert Änderungen in DB
    {
        calendarEvent.UpdatedAt = DateTime.Now; // Setze Update-Zeitstempel
        _dbContext.CalendarEvents.Update(calendarEvent); // Markiere als geändert
        await _dbContext.SaveChangesAsync(); // Speichere Änderungen
        return calendarEvent; // Gib aktualisiertes Event zurück
    }

    // Löscht einen Termin
    public async Task DeleteEventAsync(int eventId) // Löscht Event per ID
    {
        var calendarEvent = await _dbContext.CalendarEvents.FindAsync(eventId); // Suche Event in DB
        if (calendarEvent != null) // Event gefunden?
        {
            _dbContext.CalendarEvents.Remove(calendarEvent); // Markiere zum Löschen
            await _dbContext.SaveChangesAsync(); // Speichere Änderungen (löscht aus DB)
        }
    }

    // Löscht einen Termin direkt (ohne ID-Lookup)
    public async Task DeleteEventDirectAsync(CalendarEvent calendarEvent) // Löscht übergebenes Event-Objekt
    {
        _dbContext.CalendarEvents.Remove(calendarEvent); // Markiere zum Löschen
        await _dbContext.SaveChangesAsync(); // Speichere Änderungen
    }

    // Migriert Datenbank-Schema (fügt fehlende Spalten hinzu)
    public async Task EnsureDatabaseSchemaAsync() // Führt ALTER TABLE aus (idempotent)
    {
        try // Äußerer Try: Fängt alle unerwarteten Fehler
        {
            // Versuche einfach die Spalten hinzuzufügen - wenn sie existieren, gibt's einen Fehler den wir ignorieren
            try // Innerer Try: ReminderMinutesBefore + ReminderShown
            {
                await _dbContext.Database.ExecuteSqlRawAsync(@" // Führe SQL direkt aus
                    ALTER TABLE CalendarEvents 
                    ADD COLUMN ReminderMinutesBefore INT NULL; // Spalte: Erinnerung in Minuten (NULL = keine)
                ");

                await _dbContext.Database.ExecuteSqlRawAsync(@" // Führe SQL direkt aus
                    ALTER TABLE CalendarEvents 
                    ADD COLUMN ReminderShown TINYINT(1) NOT NULL DEFAULT 0; // Spalte: Erinnerung bereits gezeigt? (0/1)
                ");

                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Datenbank-Schema erfolgreich aktualisiert (ReminderMinutesBefore, ReminderShown hinzugefügt)");
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060) // Error 1060: Duplicate column name
            {
                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Spalten existieren bereits - kein Update nötig"); // Ignoriere Fehler (idempotent)
            }

            // Füge EventIdsJson zu Messages hinzu
            try // Innerer Try: EventIdsJson in Messages-Tabelle
            {
                await _dbContext.Database.ExecuteSqlRawAsync(@" // Führe SQL direkt aus
                    ALTER TABLE Messages 
                    ADD COLUMN EventIdsJson TEXT NULL; // Spalte: JSON-Array mit Event-IDs
                ");

                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] Messages-Tabelle erweitert (EventIdsJson hinzugefügt)");
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060) // Spalte existiert bereits?
            {
                System.Diagnostics.Debug.WriteLine("[CALENDAR_SERVICE] EventIdsJson existiert bereits"); // Ignoriere Fehler
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CALENDAR_SERVICE] Fehler bei Schema-Migration: {ex.Message}");
        }
    }
}