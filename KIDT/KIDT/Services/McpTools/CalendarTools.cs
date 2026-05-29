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

    [McpServerTool]
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

        List<CalendarEvent> events;

        // Lade Termine: Mit oder ohne Zeitraum-Filter
        if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate)) // Beide Parameter gesetzt?
        {
            DateTime start;
            DateTime end;
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
                ? $"Keine Termine mit '{titleSearch}' im Titel gefunden"
                : "Keine Termine gefunden";

            return JsonSerializer.Serialize(new
            {
                found = 0,
                events = new List<object>(),
                message = message,
                searchedTitle = titleSearch
            });
        }

        // Baue Event-Liste für JSON-Output (mit formatierter Zeitangabe)
        List<object> eventList = new List<object>();
        foreach (CalendarEvent e in events) // Durchlaufe alle gefundenen Events
        {
            // Formatiere Zeitangabe: Ganztägig vs. Zeitspanne vs. Startzeit vs. Keine Zeit
            string timeString;
            if (e.IsAllDay) // Ganztägiger Termin?
            {
                timeString = "Ganztägig";
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
                timeString = "Keine Zeit";
            }

            eventList.Add(new // Füge Event zur Liste hinzu
            {
                id = e.Id,
                date = e.Start.ToString("dd.MM.yyyy"), // Formatiertes Datum
                title = e.Title,
                time = timeString, // Formatierte Zeit (ggf. Zeitspanne)
                color = e.ColorIndex, // Farb-Index (0-7)
                reminderMinutes = e.ReminderMinutesBefore // Erinnerung in Minuten (null = keine)
            });
        }

        string resultMessage = !string.IsNullOrEmpty(titleSearch) // Titelsuche war aktiv?
            ? $"{events.Count} Termin(e) mit '{titleSearch}' gefunden"
            : $"{events.Count} Termin(e) gefunden";

        return JsonSerializer.Serialize(new
        {
            found = events.Count,
            events = eventList,
            message = resultMessage,
            searchedTitle = titleSearch
        });
    }

    [McpServerTool]
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
        DateTime parsedDate;
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
        TimeSpan parsedStart = TimeSpan.Zero;
        bool hasTime = false;
        if (!isAllDay && !string.IsNullOrEmpty(startTime)) // Nicht ganztägig und Startzeit angegeben?
        {
            TimeSpan tempStart;
            bool parseOk = TimeSpan.TryParse(startTime, out tempStart); // Parse Startzeit
            if (!parseOk) // Parsing fehlgeschlagen?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Startzeit-Format. Nutze 'HH:mm'." });
            }
            parsedStart = tempStart;
            hasTime = true;
        }

        // Endzeit parsen
        TimeSpan parsedEnd = TimeSpan.Zero;
        bool hasEndTime = false;
        if (!isAllDay && !string.IsNullOrEmpty(endTime)) // Nicht ganztägig und Endzeit angegeben?
        {
            TimeSpan tempEnd;
            bool parseOk = TimeSpan.TryParse(endTime, out tempEnd); // Parse Endzeit
            if (!parseOk) // Parsing fehlgeschlagen?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Endzeit-Format. Nutze 'HH:mm'." });
            }
            if (hasTime && tempEnd <= parsedStart) // Endzeit vor oder gleich Startzeit?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Endzeit muss nach der Startzeit liegen." });
            }
            parsedEnd = tempEnd;
            hasEndTime = true;
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
        var newEvent = new CalendarEvent();
        newEvent.Start = parsedDate.Date;
        newEvent.Title = title.Trim();
        newEvent.ColorIndex = colorIndex;
        newEvent.IsAllDay = isAllDay;
        newEvent.Time = parsedStart; // Startzeit (Zero wenn ganztägig)
        newEvent.HasTime = hasTime;
        newEvent.EndTime = parsedEnd; // Endzeit (Zero wenn nicht gesetzt)
        newEvent.HasEndTime = hasEndTime;

        if (reminderMinutes > 0) // Erinnerung gewünscht?
        {
            newEvent.ReminderMinutesBefore = reminderMinutes;
        }

        await this.calendarService.AddEventAsync(newEvent); // Speichere in Datenbank

        // Zeitstring für Response
        string timeString;
        if (isAllDay) // Ganztägig?
        {
            timeString = "Ganztägig";
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
            timeString = "Keine Zeit";
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

    [McpServerTool]
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
            CalendarEvent foundEvent = new CalendarEvent();
            bool eventFound = false;
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Id == eventId) // ID stimmt überein?
                {
                    foundEvent = e;
                    eventFound = true;
                    break;
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
            DateTime parsedDate;
            string? parseError = ParseFlexibleDate(date, out parsedDate); // Datum flexibel parsen ("yyyy-MM-dd" oder "dd.MM")
            if (parseError != null)
                return JsonSerializer.Serialize(new { success = false, message = parseError });

            // Filtere: Finde alle Termine am angegebenen Tag
            var eventsOnDate = new List<CalendarEvent>();
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
                var filteredEvents = new List<CalendarEvent>();
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
                var eventList = new List<object>();
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe alle Termine
                {
                    string timeString;
                    if (e.IsAllDay) // Ganztägiger Termin?
                    {
                        timeString = "Ganztägig";
                    }
                    else // Termin mit Uhrzeit
                    {
                        if (e.HasTime) // Uhrzeit vorhanden?
                        {
                            timeString = e.Time.ToString(@"hh\:mm"); // Format: "14:00"
                        }
                        else // Keine Uhrzeit gesetzt
                        {
                            timeString = "Keine Zeit";
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

    [McpServerTool]
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
        CalendarEvent eventToUpdate = new CalendarEvent();
        bool updateEventFound = false;

        // SCHRITT 1: Finde zu aktualisierenden Termin (per ID oder Datum+Titel)
        if (eventId >= 0) // ID angegeben? (schnellster Weg)
        {
            foreach (CalendarEvent e in allEvents) // Durchlaufe alle Events
            {
                if (e.Id == eventId) // ID stimmt überein?
                {
                    eventToUpdate = e;
                    updateEventFound = true;
                    break;
                }
            }
        }
        else if (!string.IsNullOrEmpty(currentDate)) // Keine ID? Suche per Datum
        {
            DateTime parsedDate;
            string? parseError = ParseFlexibleDate(currentDate, out parsedDate); // Datum flexibel parsen
            if (parseError != null)
                return JsonSerializer.Serialize(new { success = false, message = parseError });

            // Finde alle Termine am angegebenen Tag
            var eventsOnDate = new List<CalendarEvent>();
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
                var filteredEvents = new List<CalendarEvent>();
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
                var eventList = new List<object>();
                foreach (CalendarEvent e in eventsOnDate) // Durchlaufe alle Termine
                {
                    string eventTimeString;
                    if (e.IsAllDay) // Ganztägiger Termin?
                    {
                        eventTimeString = "Ganztägig";
                    }
                    else // Termin mit Uhrzeit
                    {
                        if (e.HasTime) // Uhrzeit vorhanden?
                        {
                            eventTimeString = e.Time.ToString(@"hh\:mm"); // Format: "14:00"
                        }
                        else // Keine Uhrzeit gesetzt
                        {
                            eventTimeString = "Keine Zeit";
                        }
                    }

                    eventList.Add(new { id = e.Id, title = e.Title, time = eventTimeString }); // Füge zur Liste hinzu
                }

                return JsonSerializer.Serialize(new { success = false, needsClarification = true, message = $"{eventsOnDate.Count} Termine gefunden. Bitte wähle per ID:", events = eventList }); // Gib Liste zurück
            }

            eventToUpdate = eventsOnDate[0]; // Genau 1 Event gefunden: Übernehme es
            updateEventFound = true;
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
            wasUpdated = true;
        }

        if (!string.IsNullOrEmpty(newDate)) // Neues Datum angegeben?
        {
            DateTime parsedNewDate;
            bool parseDateSuccess = DateTime.TryParse(newDate, out parsedNewDate); // Versuche zu parsen
            if (parseDateSuccess) // Datum valide?
            {
                eventToUpdate.Start = parsedNewDate.Date;
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrEmpty(newIsAllDay)) // Ganztägig-Status ändern?
        {
            if (newIsAllDay.ToLower() == "true") // Zu ganztägig wechseln
            {
                eventToUpdate.IsAllDay = true;
                eventToUpdate.Time = TimeSpan.Zero; // Uhrzeit entfernen
                eventToUpdate.HasTime = false;
                wasUpdated = true;
            }
            else if (newIsAllDay.ToLower() == "false") // Zu Termin mit Uhrzeit wechseln
            {
                eventToUpdate.IsAllDay = false;
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrEmpty(newStartTime)) // Neue Startzeit angegeben?
        {
            TimeSpan parsedNewStart;
            bool parseSuccess = TimeSpan.TryParse(newStartTime, out parsedNewStart); // Versuche zu parsen
            if (parseSuccess) // Zeit valide?
            {
                eventToUpdate.Time = parsedNewStart;
                eventToUpdate.HasTime = true;
                eventToUpdate.IsAllDay = false; // Startzeit gesetzt = nicht ganztägig
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrEmpty(newEndTime)) // Neue Endzeit angegeben?
        {
            TimeSpan parsedNewEnd;
            bool parseOk = TimeSpan.TryParse(newEndTime, out parsedNewEnd); // Versuche zu parsen
            if (parseOk) // Zeit valide?
            {
                if (eventToUpdate.HasTime && parsedNewEnd <= eventToUpdate.Time) // Endzeit vor Startzeit?
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Endzeit muss nach der Startzeit liegen." });
                }
                eventToUpdate.EndTime = parsedNewEnd;
                eventToUpdate.HasEndTime = true;
                wasUpdated = true;
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

            int colorIndexValue;
            bool colorFound = colorMapping.TryGetValue(newColor.Trim(), out colorIndexValue); // Suche Farbe im Dictionary
            if (colorFound) // Farbe erkannt?
            {
                eventToUpdate.ColorIndex = colorIndexValue;
                wasUpdated = true;
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
                wasUpdated = true;
            }
            else if (newReminderMinutes >= 0) // Positive Zahl = Neue Erinnerung setzen
            {
                eventToUpdate.ReminderMinutesBefore = newReminderMinutes;
                eventToUpdate.ReminderShown = false; // Reset, damit Erinnerung erneut angezeigt wird
                wasUpdated = true;
            }
        }

        if (!wasUpdated) // Kein einziges Feld wurde geändert?
            return JsonSerializer.Serialize(new { success = false, message = "Keine Änderungen angegeben." });

        // SCHRITT 3: Speichere alle Änderungen in Datenbank
        await this.calendarService.UpdateEventAsync(eventToUpdate);

        // Formatiere Zeitangabe für JSON-Response
        string finalTimeString;
        if (eventToUpdate.IsAllDay) // Ganztägig?
        {
            finalTimeString = "Ganztägig";
        }
        else // Mit Uhrzeit
        {
            if (eventToUpdate.HasTime) // Uhrzeit vorhanden?
            {
                finalTimeString = eventToUpdate.Time.ToString(@"hh\:mm"); // Format: "14:00"
            }
            else // Keine Uhrzeit
            {
                finalTimeString = "Keine Zeit";
            }
        }

        return JsonSerializer.Serialize(new // Erstelle Erfolgs-Response
        {
            success = true,
            message = $"Termin '{eventToUpdate.Title}' wurde aktualisiert",
            eventId = eventToUpdate.Id,
            title = eventToUpdate.Title,
            date = eventToUpdate.Start.ToString("dd.MM.yyyy"),
            time = finalTimeString
        });
    }

    private static string? ParseFlexibleDate(string input, out DateTime result) // Parst "yyyy-MM-dd" oder "dd.MM" (aktuelles Jahr); gibt Fehlermeldung zurück oder null bei Erfolg
    {
        result = DateTime.MinValue;

        if (input.Contains("-")) // Format "yyyy-MM-dd"
        {
            if (!DateTime.TryParse(input, out result))
                return "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'.";
            return null;
        }

        if (input.Contains(".")) // Format "dd.MM" ohne Jahr
        {
            var parts = input.Split('.');
            if (parts.Length == 2)
            {
                bool dayOk = int.TryParse(parts[0], out int day);
                bool monthOk = int.TryParse(parts[1], out int month);
                if (dayOk && monthOk)
                {
                    result = new DateTime(DateTime.Now.Year, month, day);
                    return null;
                }
            }
            return "Ungültiges Datum-Format. Nutze 'dd.MM' (z.B. '19.03').";
        }

        return "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'.";
    }
}
