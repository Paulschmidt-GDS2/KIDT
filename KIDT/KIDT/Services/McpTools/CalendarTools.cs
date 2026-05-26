using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using KIDT.Models;

namespace KIDT.Services.McpTools;

[McpServerToolType] // Markiert Klasse als MCP-Tool-Container (wird von MCP-Server registriert)
public class CalendarTools // MCP-Tools: list_calendar_events, create_calendar_event, delete_calendar_event (werden per Function Calling vom LLM aufgerufen)
{
    private readonly CalendarService calendarService;

    public CalendarTools(CalendarService calendarService) // Konstruktor: Wird bei RegisterTools aufgerufen (Dependency Injection)
    {
        this.calendarService = calendarService;
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Listet Kalender-Termine auf. 'date' für einen bestimmten Tag (z.B. heute). 'titleSearch' für Titelsuche. Mit startDate/endDate für Zeitraum. Ohne Parameter: alle Termine.")]
    public async Task<string> ListCalendarEvents( // Tool: Listet Termine aus Datenbank
        [Description("Suche nach Titel (optional, z.B. 'Meeting' findet 'Team Meeting')")] string titleSearch = "",
        [Description("Start-Datum im Format 'yyyy-MM-dd' (optional)")] string startDate = "",
        [Description("End-Datum im Format 'yyyy-MM-dd' (optional)")] string endDate = "",
        [Description("Exaktes Datum im Format 'yyyy-MM-dd' (optional, für alle Termine an einem bestimmten Tag, z.B. heute)")] string date = "")
    {
        // Einzelnes Datum als startDate/endDate setzen (falls angegeben)
        if (!string.IsNullOrEmpty(date) && string.IsNullOrEmpty(startDate) && string.IsNullOrEmpty(endDate)) // Nur date angegeben?
        {
            startDate = date; // Verwende date als Start
            endDate = date; // Verwende date als Ende (gleicher Tag)
        }

        List<CalendarEvent> events; // Liste für gefundene Termine

        // Lade Termine: Mit oder ohne Zeitraum-Filter
        if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate)) // Beide Parameter gesetzt?
        {
            DateTime start; // Variable für Start-Datum
            DateTime end; // Variable für End-Datum
            bool startParsed = DateTime.TryParse(startDate, out start); // Parse Start-Datum
            bool endParsed = DateTime.TryParse(endDate, out end); // Parse End-Datum
            if (startParsed && endParsed) // Beide Daten gültig?
            {
                events = await this.calendarService.GetEventsByDateRangeAsync(start, end); // Lade nur Termine im Zeitraum
            }
            else // Parsing fehlgeschlagen
            {
                events = await this.calendarService.GetAllEventsAsync(); // Fallback: alle Termine
            }
        }
        else // Kein Zeitraum? Lade alle Termine
        {
            events = await this.calendarService.GetAllEventsAsync(); // Lade alle Termine
        }

        // Wenn Titelsuche angegeben: Filtere Ergebnisse (Case-Insensitive)
        if (!string.IsNullOrEmpty(titleSearch)) // Titelsuche aktiv?
        {
            events = events.Where(e => e.Title.Contains(titleSearch, StringComparison.OrdinalIgnoreCase)).ToList(); // LINQ-Filter: Partial Match im Titel
        }

        if (events.Count == 0) // Keine Termine gefunden?
        {
            // Formuliere Fehlermeldung: mit/ohne Titelsuche
            string message = !string.IsNullOrEmpty(titleSearch) // Titelsuche war aktiv?
                ? $"Keine Termine mit '{titleSearch}' im Titel gefunden" // Ja: Mit Titel
                : "Keine Termine gefunden"; // Nein: Generisch

            return JsonSerializer.Serialize(new // Gib JSON zurück: 0 gefunden
            {
                found = 0,
                events = new List<object>(),
                message = message,
                searchedTitle = titleSearch
            });
        }

        // Baue Event-Liste für JSON-Output (mit formatierter Zeitangabe)
        List<object> eventList = new List<object>(); // Initialisiere Event-Liste für JSON
        foreach (CalendarEvent e in events) // Durchlaufe alle gefundenen Events
        {
            // Formatiere Zeitangabe: Ganztägig vs. Zeitspanne vs. Startzeit vs. Keine Zeit
            string timeString; // Variable für formatierte Zeit
            if (e.IsAllDay) // Ganztägiger Termin?
            {
                timeString = "Ganztägig"; // Setze Text
            }
            else if (e.HasTime && e.HasEndTime) // Zeitspanne (Start + Ende)?
            {
                timeString = $"{e.Time:hh\\:mm} - {e.EndTime:hh\\:mm}"; // Format: "14:00 - 16:00"
            }
            else if (e.HasTime) // Nur Startzeit?
            {
                timeString = e.Time.ToString(@"hh\:mm"); // Format: "14:00"
            }
            else // Keine Uhrzeit
            {
                timeString = "Keine Zeit"; // Fallback-Text
            }

            eventList.Add(new // Füge Event zur Liste hinzu
            {
                id = e.Id, // Event-ID
                date = e.Start.ToString("dd.MM.yyyy"), // Formatiertes Datum
                title = e.Title, // Titel
                time = timeString, // Formatierte Zeit (ggf. Zeitspanne)
                color = e.ColorIndex, // Farb-Index (0-7)
                reminderMinutes = e.ReminderMinutesBefore // Erinnerung in Minuten (null = keine)
            });
        }

        string resultMessage = !string.IsNullOrEmpty(titleSearch) // Titelsuche war aktiv?
            ? $"{events.Count} Termin(e) mit '{titleSearch}' gefunden" // Ja: Mit Titel
            : $"{events.Count} Termin(e) gefunden"; // Nein: Generisch

        var result = new // Erstelle JSON-Result-Objekt
        {
            found = events.Count, // Anzahl gefundener Termine
            events = eventList, // Event-Liste mit Details
            message = resultMessage, // Human-readable Message
            searchedTitle = titleSearch // Suchbegriff (für Client)
        };

        return JsonSerializer.Serialize(result);
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Erstellt einen neuen Kalender-Termin. PFLICHTFELDER: date (yyyy-MM-dd), title, isAllDay (MUSS immer angegeben werden: true=ganztägig, false=mit Uhrzeit). Optional: startTime (HH:mm), endTime (HH:mm), colorIndex (0-7), reminderMinutes. Wenn startTime+endTime: Zeitspanne. Bei Überschneidung: Fehler.")]
    public async Task<string> CreateCalendarEvent( // Tool: Erstellt neuen Termin in Datenbank
        [Description("Datum im Format 'yyyy-MM-dd' (z.B. '2025-01-15')")] string date,
        [Description("Titel des Termins (z.B. 'Meeting mit Team')")] string title,
        [Description("PFLICHTFELD: Ganztägig? true = ganztägig (kein startTime/endTime nötig), false = mit Uhrzeit (dann startTime angeben)")] bool isAllDay,
        [Description("Startzeit im Format 'HH:mm' (z.B. '14:00'), nur wenn isAllDay=false")] string startTime = "",
        [Description("Endzeit im Format 'HH:mm' (z.B. '15:30'), nur wenn isAllDay=false und Zeitspanne gewünscht")] string endTime = "",
        [Description("Farbindex 0-7 (Standard: 0)")] int colorIndex = 0,
        [Description("Erinnerung X Minuten vorher (z.B. 15, 30, 60, 1440). 0 = keine Erinnerung")] int reminderMinutes = 0)
    {
        // Validierung: Datum parsen
        DateTime parsedDate; // Variable für geparsten Datum
        bool parseDateSuccess = DateTime.TryParse(date, out parsedDate); // Versuche Datum zu parsen
        if (!parseDateSuccess) // Datum ungültig?
        {
            return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd'." });
        }

        if (string.IsNullOrWhiteSpace(title)) // Titel leer?
        {
            return JsonSerializer.Serialize(new { success = false, message = "Titel darf nicht leer sein." });
        }

        if (colorIndex < 0 || colorIndex > 7) // ColorIndex außerhalb Bereich?
        {
            colorIndex = 0; // Setze auf Standard
        }

        // Wenn startTime angegeben: kann kein ganztägiger Termin sein (Gemini vergisst isAllDay oft)
        if (!string.IsNullOrEmpty(startTime)) // Startzeit übergeben?
        {
            isAllDay = false; // Überschreibe Default: nicht ganztägig
        }

        // Startzeit parsen
        TimeSpan parsedStart = TimeSpan.Zero; // Initialisiere Startzeit
        bool hasTime = false; // Flag: Startzeit gesetzt?
        if (!isAllDay && !string.IsNullOrEmpty(startTime)) // Nicht ganztägig und Startzeit angegeben?
        {
            TimeSpan tempStart; // Temporäre Variable
            bool parseOk = TimeSpan.TryParse(startTime, out tempStart); // Parse Startzeit
            if (!parseOk) // Parsing fehlgeschlagen?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Startzeit-Format. Nutze 'HH:mm'." });
            }
            parsedStart = tempStart; // Übernehme Startzeit
            hasTime = true; // Startzeit gesetzt
        }

        // Endzeit parsen
        TimeSpan parsedEnd = TimeSpan.Zero; // Initialisiere Endzeit
        bool hasEndTime = false; // Flag: Endzeit gesetzt?
        if (!isAllDay && !string.IsNullOrEmpty(endTime)) // Nicht ganztägig und Endzeit angegeben?
        {
            TimeSpan tempEnd; // Temporäre Variable
            bool parseOk = TimeSpan.TryParse(endTime, out tempEnd); // Parse Endzeit
            if (!parseOk) // Parsing fehlgeschlagen?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Endzeit-Format. Nutze 'HH:mm'." });
            }
            if (hasTime && tempEnd <= parsedStart) // Endzeit vor oder gleich Startzeit?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Endzeit muss nach der Startzeit liegen." });
            }
            parsedEnd = tempEnd; // Übernehme Endzeit
            hasEndTime = true; // Endzeit gesetzt
        }

        // Überlappungsprüfung: Nur wenn Zeitspanne vorhanden (Start + Ende)
        if (!isAllDay && hasTime && hasEndTime) // Zeitspanne angegeben?
        {
            var eventsOnDay = await this.calendarService.GetEventsByDateRangeAsync(parsedDate.Date, parsedDate.Date); // Lade Events des Tages
            foreach (CalendarEvent existing in eventsOnDay) // Prüfe alle bestehenden Events
            {
                if (existing.IsAllDay || !existing.HasTime || !existing.HasEndTime) // Ganztägig oder ohne Zeitspanne?
                {
                    continue; // Kein Konflikt möglich
                }
                bool overlaps = parsedStart < existing.EndTime && parsedEnd > existing.Time; // Überlappungsformel
                if (overlaps) // Überlappung gefunden?
                {
                    string existingTime = $"{existing.Time:hh\\:mm} - {existing.EndTime:hh\\:mm}"; // Formatiere bestehende Zeit
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        message = $"Zeitüberschneidung mit '{existing.Title}' ({existingTime}). Wähle eine andere Uhrzeit."
                    });
                }
            }
        }

        // Erstelle neues Event-Objekt
        var newEvent = new CalendarEvent(); // Initialisiere neues Event
        newEvent.Start = parsedDate.Date; // Setze Datum
        newEvent.Title = title.Trim(); // Setze Titel
        newEvent.ColorIndex = colorIndex; // Setze Farbe
        newEvent.IsAllDay = isAllDay; // Setze Ganztägig-Flag
        newEvent.Time = parsedStart; // Startzeit (Zero wenn ganztägig)
        newEvent.HasTime = hasTime; // Flag Startzeit
        newEvent.EndTime = parsedEnd; // Endzeit (Zero wenn nicht gesetzt)
        newEvent.HasEndTime = hasEndTime; // Flag Endzeit

        if (reminderMinutes > 0) // Erinnerung gewünscht?
        {
            newEvent.ReminderMinutesBefore = reminderMinutes; // Setze Erinnerung
        }

        await this.calendarService.AddEventAsync(newEvent); // Speichere in Datenbank

        // Zeitstring für Response
        string timeString; // Variable für formatierte Zeit
        if (isAllDay) // Ganztägig?
        {
            timeString = "Ganztägig"; // Text
        }
        else if (hasTime && hasEndTime) // Zeitspanne?
        {
            timeString = $"{parsedStart:hh\\:mm} - {parsedEnd:hh\\:mm}"; // Format: "14:00 - 16:00"
        }
        else if (hasTime) // Nur Startzeit?
        {
            timeString = parsedStart.ToString(@"hh\:mm"); // Format: "14:00"
        }
        else
        {
            timeString = "Keine Zeit"; // Fallback
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Termin '{title}' für {parsedDate:dd.MM.yyyy} ({timeString}) erstellt",
            eventId = newEvent.Id,
            date = parsedDate.ToString("dd.MM.yyyy"),
            title = title,
            time = timeString
        });
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Löscht einen Kalender-Termin. Nutze ID ODER Datum+Titel. Wenn mehrere Termine am Datum gefunden: gib Liste zurück zum Nachfragen.")]
    public async Task<string> DeleteCalendarEvent( // Tool: Löscht Termin aus Datenbank (flexibel per ID oder Datum+Titel)
        [Description("Die ID des Termins (optional, wenn date gesetzt)")] int eventId = -1,
        [Description("Datum im Format 'yyyy-MM-dd' oder 'dd.MM' (optional, wenn eventId gesetzt)")] string date = "",
        [Description("Titel des Termins (optional, hilft bei Mehrdeutigkeit)")] string title = "")
    {
        var allEvents = await this.calendarService.GetAllEventsAsync(); // Lade alle Termine

        // FALL 1: Lösche per ID (direkter Zugriff)
        if (eventId >= 0) // ID angegeben?
        {
            CalendarEvent foundEvent = new CalendarEvent(); // Initialisiere Event-Variable
            bool eventFound = false; // Flag: Event gefunden?
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Id == eventId) // ID stimmt überein?
                {
                    foundEvent = e; // Übernehme Event
                    eventFound = true; // Setze Flag
                    break; // Beende Schleife
                }
            }

            if (eventFound == false) // Termin nicht gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Termin mit ID {eventId} nicht gefunden." });
            }

            await this.calendarService.DeleteEventAsync(eventId); // Lösche aus Datenbank
            return JsonSerializer.Serialize(new { success = true, message = $"Termin '{foundEvent.Title}' wurde gelöscht", eventId = eventId, title = foundEvent.Title });
        }

        // FALL 2: Lösche per Datum (+ optional Titel) - flexibles Datum-Format
        if (!string.IsNullOrEmpty(date)) // Datum angegeben?
        {
            DateTime parsedDate; // Variable für geparsten Datum

            // Parse flexibles Datum: Unterstützt "yyyy-MM-dd" UND "dd.MM" (ohne Jahr = aktuelles Jahr)
            if (date.Contains("-")) // Format "yyyy-MM-dd"?
            {
                if (!DateTime.TryParse(date, out parsedDate)) // Parse fehlgeschlagen?
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'." });
                }
            }
            else if (date.Contains(".")) // Format "dd.MM" ohne Jahr?
            {
                var parts = date.Split('.'); // Splitte nach Punkt
                if (parts.Length == 2) // Genau 2 Teile? (Tag.Monat)
                {
                    int day; // Variable für Tag
                    int month; // Variable für Monat
                    bool dayParsed = int.TryParse(parts[0], out day); // Parse Tag
                    bool monthParsed = int.TryParse(parts[1], out month); // Parse Monat
                    if (dayParsed && monthParsed) // Beide Parts gültig?
                    {
                        parsedDate = new DateTime(DateTime.Now.Year, month, day); // Nehme aktuelles Jahr (weil nicht angegeben)
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'dd.MM' (z.B. '19.03')." });
                    }
                }
                else
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'dd.MM' (z.B. '19.03')." });
                }
            }
            else
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'." });
            }

            // Filtere: Finde alle Termine am angegebenen Tag
            var eventsOnDate = new List<CalendarEvent>(); // Initialisiere Liste
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Start.Date == parsedDate.Date) // Datum stimmt überein?
                {
                    eventsOnDate.Add(e); // Füge zur Liste hinzu
                }
            }

            if (eventsOnDate.Count == 0) // Keine Termine gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Keine Termine am {parsedDate:dd.MM.yyyy} gefunden." });
            }

            // Optionaler zweiter Filter: Nach Titel (Case-Insensitive, Partial Match)
            if (!string.IsNullOrWhiteSpace(title)) // Titel angegeben?
            {
                var filteredEvents = new List<CalendarEvent>(); // Neue Liste für gefilterte Events
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe Termine am Tag
                {
                    if (e.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) // Titel-Match?
                    {
                        filteredEvents.Add(e); // Füge hinzu
                    }
                }
                eventsOnDate = filteredEvents; // Überschreibe mit gefilterter Liste

                if (eventsOnDate.Count == 0) // Nach Titel-Filter nichts mehr übrig?
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Kein Termin mit Titel '{title}' am {parsedDate:dd.MM.yyyy} gefunden." }); // Gib Fehler zurück
                }
            }

            // Bei Mehrdeutigkeit: Gib Liste zurück damit User per ID wählen kann
            if (eventsOnDate.Count > 1) // Mehrere Termine am Tag?
            {
                var eventList = new List<object>(); // Initialisiere Liste für JSON
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe alle Termine
                {
                    string timeString; // Variable für formatierte Zeit
                    if (e.IsAllDay) // Ganztägiger Termin?
                    {
                        timeString = "Ganztägig"; // Setze Text
                    }
                    else // Termin mit Uhrzeit
                    {
                        if (e.HasTime) // Uhrzeit vorhanden?
                        {
                            timeString = e.Time.ToString(@"hh\:mm"); // Formatiere als HH:mm
                        }
                        else // Keine Uhrzeit gesetzt
                        {
                            timeString = "Keine Zeit"; // Fallback-Text
                        }
                    }

                    eventList.Add(new { id = e.Id, title = e.Title, time = timeString }); // Füge zur Liste hinzu
                }

                return JsonSerializer.Serialize(new { success = false, needsClarification = true, message = $"{eventsOnDate.Count} Termine am {parsedDate:dd.MM.yyyy} gefunden. Bitte wähle:", events = eventList });
            }

            // Genau 1 Termin gefunden: Lösche ihn
            var singleEventToDelete = eventsOnDate[0]; // Hole ersten (einzigen) Termin
            await this.calendarService.DeleteEventAsync(singleEventToDelete.Id); // Lösche aus DB
            return JsonSerializer.Serialize(new { success = true, message = $"Termin '{singleEventToDelete.Title}' wurde gelöscht", eventId = singleEventToDelete.Id, title = singleEventToDelete.Title }); // Gib Erfolg zurück
        }

        // FALL 3: Weder ID noch Datum angegeben
        return JsonSerializer.Serialize(new { success = false, message = "Bitte gib entweder eine ID oder ein Datum an." });
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Aktualisiert einen Kalender-Termin. Finde Termin per ID oder Datum+Titel. Änderbare Felder: title, date, isAllDay, newStartTime (HH:mm), newEndTime (HH:mm, Endzeit der Zeitspanne), color, reminder.")]
    public async Task<string> UpdateCalendarEvent( // Tool: Aktualisiert bestehenden Termin
        [Description("ID des Termins (wenn bekannt)")] int eventId = -1,
        [Description("Aktuelles Datum zum Finden des Termins (Format 'yyyy-MM-dd' oder 'dd.MM')")] string currentDate = "",
        [Description("Aktueller Titel zum Finden des Termins")] string currentTitle = "",
        [Description("Neuer Titel (optional)")] string newTitle = "",
        [Description("Neues Datum (Format 'yyyy-MM-dd', optional)")] string newDate = "",
        [Description("Neuer Ganztägig-Status als String: 'true' für ganztägig, 'false' für mit Uhrzeit (optional)")] string newIsAllDay = "",
        [Description("Neue Startzeit (Format 'HH:mm', optional)")] string newStartTime = "",
        [Description("Neue Endzeit (Format 'HH:mm', optional, für Zeitspanne)")] string newEndTime = "",
        [Description("Neue Farbe als Text: 'grau/oliv', 'blau', 'lachs/rosa', 'grün', 'gelb', 'lila/violett', 'pink', 'mint' (optional)")] string newColor = "",
        [Description("Neue Erinnerung X Minuten vorher (z.B. 15, 30, 60, 1440). -1 = Erinnerung entfernen (optional)")] int newReminderMinutes = -999)
    {
        var allEvents = await this.calendarService.GetAllEventsAsync(); // Lade alle Termine aus DB
        CalendarEvent eventToUpdate = new CalendarEvent(); // Initialisiere Event-Variable
        bool updateEventFound = false; // Flag: Event gefunden?

        // SCHRITT 1: Finde zu aktualisierenden Termin (per ID oder Datum+Titel)
        if (eventId >= 0) // ID angegeben? (schnellster Weg)
        {
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Id == eventId) // ID stimmt überein?
                {
                    eventToUpdate = e; // Übernehme Event
                    updateEventFound = true; // Setze Flag
                    break; // Beende Schleife
                }
            }
        }
        else if (!string.IsNullOrEmpty(currentDate)) // Keine ID? Suche per Datum
        {
            // Parse Datum flexibel: "yyyy-MM-dd" oder "dd.MM"
            DateTime parsedDate; // Variable für geparsten Datum
            if (currentDate.Contains("-")) // Format "yyyy-MM-dd"?
            {
                bool parseSuccess = DateTime.TryParse(currentDate, out parsedDate); // Versuche zu parsen
                if (!parseSuccess) // Parsing fehlgeschlagen?
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
            }
            else if (currentDate.Contains(".")) // Format "dd.MM"?
            {
                var parts = currentDate.Split('.'); // Splitte nach Punkt
                if (parts.Length == 2) // Genau 2 Teile?
                {
                    int day; // Variable für Tag
                    int month; // Variable für Monat
                    bool dayParsed = int.TryParse(parts[0], out day); // Parse Tag
                    bool monthParsed = int.TryParse(parts[1], out month); // Parse Monat
                    if (dayParsed && monthParsed) // Beide erfolgreich geparst?
                    {
                        parsedDate = new DateTime(DateTime.Now.Year, month, day); // Ergänze aktuelles Jahr
                    }
                    else // Parsing fehlgeschlagen
                    {
                        return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
                    }
                }
                else
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
                }
            }
            else
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
            }

            // Finde alle Termine am angegebenen Tag
            var eventsOnDate = new List<CalendarEvent>(); // Initialisiere Liste
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Start.Date == parsedDate.Date) // Datum stimmt überein?
                {
                    eventsOnDate.Add(e); // Füge hinzu
                }
            }

            // Optionaler Filter: Nach aktuellem Titel (falls mehrere Termine am Tag)
            if (!string.IsNullOrWhiteSpace(currentTitle)) // Titel angegeben?
            {
                var filteredEvents = new List<CalendarEvent>(); // Neue Liste für gefilterte Events
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe Termine am Tag
                {
                    if (e.Title.Contains(currentTitle, StringComparison.OrdinalIgnoreCase)) // Titel-Match?
                    {
                        filteredEvents.Add(e); // Füge hinzu
                    }
                }
                eventsOnDate = filteredEvents; // Überschreibe mit gefilterter Liste
            }

            if (eventsOnDate.Count == 0) // Keine Termine gefunden?
            {
                string titlePart = ""; // Initialisiere Titel-Teil für Fehlermeldung
                if (!string.IsNullOrWhiteSpace(currentTitle)) // Titel war angegeben?
                {
                    titlePart = $" mit Titel '{currentTitle}'"; // Ergänze Titel in Fehlermeldung
                }
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Termin am {parsedDate:dd.MM.yyyy}" + titlePart + " gefunden." }); // Gib Fehler zurück
            }

            // Mehrdeutigkeit: Gib Liste zurück zur Auswahl per ID
            if (eventsOnDate.Count > 1) // Mehrere Termine gefunden?
            {
                var eventList = new List<object>(); // Initialisiere Liste für JSON
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe alle Termine
                {
                    string eventTimeString; // Variable für formatierte Zeit
                    if (e.IsAllDay) // Ganztägiger Termin?
                    {
                        eventTimeString = "Ganztägig"; // Text für ganztägig
                    }
                    else // Termin mit Uhrzeit
                    {
                        if (e.HasTime) // Uhrzeit vorhanden?
                        {
                            eventTimeString = e.Time.ToString(@"hh\:mm"); // Formatiere als HH:mm
                        }
                        else // Keine Uhrzeit gesetzt
                        {
                            eventTimeString = "Keine Zeit"; // Fallback-Text
                        }
                    }

                    eventList.Add(new { id = e.Id, title = e.Title, time = eventTimeString }); // Füge zur Liste hinzu
                }

                return JsonSerializer.Serialize(new { success = false, needsClarification = true, message = $"{eventsOnDate.Count} Termine gefunden. Bitte wähle per ID:", events = eventList }); // Gib Liste zurück
            }

            eventToUpdate = eventsOnDate[0]; // Genau 1 Event gefunden: Übernehme es
            updateEventFound = true; // Setze Flag
        }
        else // Weder ID noch Datum angegeben
        {
            return JsonSerializer.Serialize(new { success = false, message = "Bitte gib entweder eine ID oder ein Datum an." }); // Gib Fehler zurück
        }

        if (updateEventFound == false) // Event nicht gefunden?
        {
            return JsonSerializer.Serialize(new { success = false, message = "Termin nicht gefunden." });
        }

        // SCHRITT 2: Aktualisiere angegebene Felder (nur nicht-leere Werte werden übernommen)
        bool wasUpdated = false; // Track ob überhaupt Änderungen gemacht wurden

        if (!string.IsNullOrWhiteSpace(newTitle)) // Neuer Titel angegeben?
        {
            eventToUpdate.Title = newTitle.Trim(); // Übernehme neuen Titel (getrimmt)
            wasUpdated = true; // Markiere als geändert
        }

        if (!string.IsNullOrEmpty(newDate)) // Neues Datum angegeben?
        {
            DateTime parsedNewDate; // Variable für geparsten Datum
            bool parseDateSuccess = DateTime.TryParse(newDate, out parsedNewDate); // Versuche zu parsen
            if (parseDateSuccess) // Datum valide?
            {
                eventToUpdate.Start = parsedNewDate.Date; // Übernehme neues Datum
                wasUpdated = true; // Markiere als geändert
            }
        }

        if (!string.IsNullOrEmpty(newIsAllDay)) // Ganztägig-Status ändern?
        {
            if (newIsAllDay.ToLower() == "true") // Zu ganztägig wechseln
            {
                eventToUpdate.IsAllDay = true; // Setze Flag
                eventToUpdate.Time = TimeSpan.Zero; // Uhrzeit entfernen
                eventToUpdate.HasTime = false; // Markiere: Keine Uhrzeit
                wasUpdated = true; // Markiere als geändert
            }
            else if (newIsAllDay.ToLower() == "false") // Zu Termin mit Uhrzeit wechseln
            {
                eventToUpdate.IsAllDay = false; // Entferne Ganztägig-Flag
                wasUpdated = true; // Markiere als geändert
            }
        }

        if (!string.IsNullOrEmpty(newStartTime)) // Neue Startzeit angegeben?
        {
            TimeSpan parsedNewStart; // Variable für geparste Startzeit
            bool parseSuccess = TimeSpan.TryParse(newStartTime, out parsedNewStart); // Versuche zu parsen
            if (parseSuccess) // Zeit valide?
            {
                eventToUpdate.Time = parsedNewStart; // Übernehme neue Startzeit
                eventToUpdate.HasTime = true; // Markiere: Hat Startzeit
                eventToUpdate.IsAllDay = false; // Startzeit gesetzt = nicht ganztägig
                wasUpdated = true; // Markiere als geändert
            }
        }

        if (!string.IsNullOrEmpty(newEndTime)) // Neue Endzeit angegeben?
        {
            TimeSpan parsedNewEnd; // Variable für geparste Endzeit
            bool parseOk = TimeSpan.TryParse(newEndTime, out parsedNewEnd); // Versuche zu parsen
            if (parseOk) // Zeit valide?
            {
                if (eventToUpdate.HasTime && parsedNewEnd <= eventToUpdate.Time) // Endzeit vor Startzeit?
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Endzeit muss nach der Startzeit liegen." });
                }
                eventToUpdate.EndTime = parsedNewEnd; // Übernehme neue Endzeit
                eventToUpdate.HasEndTime = true; // Markiere: Hat Endzeit
                wasUpdated = true; // Markiere als geändert
            }
        }

        if (!string.IsNullOrWhiteSpace(newColor)) // Neue Farbe angegeben?
        {
            // Mappe Text-Farbnamen zu ColorIndex (0-7) - erlaubt natürlichsprachliche Eingabe
            var colorMapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) // Case-Insensitive Mapping
            {
                { "grau", 0 }, { "oliv", 0 }, { "standard", 0 },
                { "blau", 1 }, { "hellblau", 1 },
                { "lachs", 2 }, { "rosa", 2 }, { "orange", 2 },
                { "grün", 3 }, { "hellgrün", 3 },
                { "gruen", 3 }, // Alternative Schreibweise
                { "gelb", 4 }, { "gold", 4 },
                { "lila", 5 }, { "violett", 5 }, { "purple", 5 },
                { "pink", 6 }, { "magenta", 6 },
                { "mint", 7 }, { "türkis", 7 }, { "tuerkis", 7 } // Mit/ohne Umlaut
            };

            int colorIndexValue; // Variable für ColorIndex
            bool colorFound = colorMapping.TryGetValue(newColor.Trim(), out colorIndexValue); // Suche Farbe im Dictionary
            if (colorFound) // Farbe erkannt?
            {
                eventToUpdate.ColorIndex = colorIndexValue; // Setze neue Farbe
                wasUpdated = true; // Markiere als geändert
            }
            else // Unbekannter Farbname
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Unbekannte Farbe '{newColor}'. Nutze: grau, blau, lachs, grün, gelb, lila, pink, mint." });
            }
        }

        if (newReminderMinutes != -999) // Reminder-Update gewünscht? (-999 = nicht angegeben)
        {
            if (newReminderMinutes == -1) // -1 = Erinnerung entfernen
            {
                eventToUpdate.ReminderMinutesBefore = null; // Entferne Erinnerung
                eventToUpdate.ReminderShown = false; // Reset Flag (wichtig für Konsistenz)
                wasUpdated = true; // Markiere als geändert
            }
            else if (newReminderMinutes >= 0) // Positive Zahl = Neue Erinnerung setzen
            {
                eventToUpdate.ReminderMinutesBefore = newReminderMinutes; // Setze neue Erinnerung in Minuten
                eventToUpdate.ReminderShown = false; // Reset, damit Erinnerung erneut angezeigt wird
                wasUpdated = true; // Markiere als geändert
            }
        }

        if (!wasUpdated) // Kein einziges Feld wurde geändert?
            return JsonSerializer.Serialize(new { success = false, message = "Keine Änderungen angegeben." });

        // SCHRITT 3: Speichere alle Änderungen in Datenbank
        await this.calendarService.UpdateEventAsync(eventToUpdate);

        // Formatiere Zeitangabe für JSON-Response
        string finalTimeString; // Variable für formatierte Zeit
        if (eventToUpdate.IsAllDay) // Ganztägig?
        {
            finalTimeString = "Ganztägig"; // Text für ganztägig
        }
        else // Mit Uhrzeit
        {
            if (eventToUpdate.HasTime) // Uhrzeit vorhanden?
            {
                finalTimeString = eventToUpdate.Time.ToString(@"hh\:mm"); // Formatiere als HH:mm
            }
            else // Keine Uhrzeit
            {
                finalTimeString = "Keine Zeit"; // Fallback-Text
            }
        }

        return JsonSerializer.Serialize(new // Erstelle Erfolgs-Response
        {
            success = true, // Erfolg-Flag
            message = $"Termin '{eventToUpdate.Title}' wurde aktualisiert", // Human-readable Message
            eventId = eventToUpdate.Id, // Event-ID
            title = eventToUpdate.Title, // Aktualisierter Titel
            date = eventToUpdate.Start.ToString("dd.MM.yyyy"), // Aktualisiertes Datum
            time = finalTimeString // Aktualisierte Zeit (formatiert)
        });
    }
}