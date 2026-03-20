using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using KIDT.Models;

namespace KIDT.Services.McpTools;

[McpServerToolType] // Markiert Klasse als MCP-Tool-Container (wird von MCP-Server registriert)
public class CalendarTools // MCP-Tools: list_calendar_events, create_calendar_event, delete_calendar_event (werden per Function Calling vom LLM aufgerufen)
{
    private readonly CalendarService calendarService; // Service für Kalender-DB-Operationen

    public CalendarTools(CalendarService calendarService) // Konstruktor: Wird bei RegisterTools aufgerufen (Dependency Injection)
    {
        this.calendarService = calendarService;
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Listet Kalender-Termine auf. Nutze 'titleSearch' um nach spezifischem Titel zu suchen. Mit startDate/endDate kannst du Zeitraum einschränken. Ohne Parameter: alle Termine.")]
    public async Task<string> ListCalendarEvents( // Tool: Listet Termine aus Datenbank
        [Description("Suche nach Titel (optional, z.B. 'Meeting' findet 'Team Meeting')")] string titleSearch = "",
        [Description("Start-Datum im Format 'yyyy-MM-dd' (optional)")] string startDate = "",
        [Description("End-Datum im Format 'yyyy-MM-dd' (optional)")] string endDate = "")
    {
        List<CalendarEvent> events; // Liste für gefundene Termine

        // Prüfe ob Datumsbereich angegeben wurde
        if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate))
        {
            DateTime start;
            DateTime end;
            bool startParsed = DateTime.TryParse(startDate, out start);
            bool endParsed = DateTime.TryParse(endDate, out end);
            if (startParsed && endParsed)
            {
                events = await this.calendarService.GetEventsByDateRangeAsync(start, end);
            }
            else
            {
                events = await this.calendarService.GetAllEventsAsync();
            }
        }
        else // Kein Zeitraum? Lade alle Termine
        {
            events = await this.calendarService.GetAllEventsAsync(); // Lade alle Termine
        }

        // Filter nach Titel (Case-Insensitive)
        if (!string.IsNullOrEmpty(titleSearch))
        {
            events = events.Where(e => e.Title.Contains(titleSearch, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (events.Count == 0) // Keine Termine gefunden?
        {
            string message = !string.IsNullOrEmpty(titleSearch) 
                ? $"Keine Termine mit '{titleSearch}' im Titel gefunden" 
                : "Keine Termine gefunden";

            return JsonSerializer.Serialize(new // Gib JSON zurück: 0 gefunden
            {
                found = 0,
                events = new List<object>(),
                message = message,
                searchedTitle = titleSearch
            });
        }

        List<object> eventList = new List<object>();
        foreach (CalendarEvent e in events)
        {
            string timeString;
            if (e.IsAllDay)
            {
                timeString = "Ganztägig";
            }
            else
            {
                if (e.HasTime)
                {
                    timeString = e.Time.ToString(@"hh\:mm");
                }
                else
                {
                    timeString = "Keine Zeit";
                }
            }

            eventList.Add(new
            {
                id = e.Id,
                date = e.Start.ToString("dd.MM.yyyy"),
                title = e.Title,
                time = timeString,
                color = e.ColorIndex,
                reminderMinutes = e.ReminderMinutesBefore
            });
        }

        string resultMessage = !string.IsNullOrEmpty(titleSearch)
            ? $"{events.Count} Termin(e) mit '{titleSearch}' gefunden"
            : $"{events.Count} Termin(e) gefunden";

        var result = new
        {
            found = events.Count,
            events = eventList,
            message = resultMessage,
            searchedTitle = titleSearch
        };

        return JsonSerializer.Serialize(result);
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Erstellt einen neuen Kalender-Termin. Benötigt: date (yyyy-MM-dd), title. Optional: isAllDay (true/false), time (HH:mm), colorIndex (0-7), reminderMinutes (Erinnerung X Minuten vorher, z.B. 15, 30, 60, 1440 für 1 Tag).")]
    public async Task<string> CreateCalendarEvent( // Tool: Erstellt neuen Termin in Datenbank
        [Description("Datum im Format 'yyyy-MM-dd' (z.B. '2025-01-15')")] string date,
        [Description("Titel des Termins (z.B. 'Meeting mit Team')")] string title,
        [Description("Ganztägig? true oder false (Standard: true)")] bool isAllDay = true,
        [Description("Uhrzeit im Format 'HH:mm' (z.B. '14:30'), nur wenn isAllDay=false")] string time = "",
        [Description("Farbindex 0-7 (Standard: 0)")] int colorIndex = 0,
        [Description("Erinnerung X Minuten vorher (z.B. 15, 30, 60, 1440). 0 oder nicht angegeben = keine Erinnerung")] int reminderMinutes = 0)
    {
        // Validierung: Datum parsen
        DateTime parsedDate;
        bool parseDateSuccess = DateTime.TryParse(date, out parsedDate);
        if (!parseDateSuccess) // Datum ungültig?
        {
            return JsonSerializer.Serialize(new // Gib JSON zurück: Fehler
            {
                success = false,
                message = "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd'."
            });
        }

        // Validierung: Titel darf nicht leer sein
        if (string.IsNullOrWhiteSpace(title)) // Titel leer?
        {
            return JsonSerializer.Serialize(new // Gib JSON zurück: Fehler
            {
                success = false,
                message = "Titel darf nicht leer sein."
            });
        }

        // Validierung: ColorIndex muss 0-7 sein
        if (colorIndex < 0 || colorIndex > 7) // ColorIndex außerhalb Bereich?
        {
            colorIndex = 0; // Setze auf Standard
        }

        TimeSpan parsedTime = TimeSpan.Zero;
        bool hasTime = false;
        if (!isAllDay && !string.IsNullOrEmpty(time)) // Nicht ganztägig und Zeit angegeben?
        {
            TimeSpan tempTime;
            bool parseSuccess = TimeSpan.TryParse(time, out tempTime);
            if (!parseSuccess) // Zeit parsen fehlgeschlagen?
            {
                return JsonSerializer.Serialize(new // Gib JSON zurück: Fehler
                {
                    success = false,
                    message = "Ungültiges Zeit-Format. Nutze 'HH:mm' (z.B. '14:30')."
                });
            }
            parsedTime = tempTime; // Uhrzeit setzen
            hasTime = true;
        }

        // Erstelle neuen Termin
        var newEvent = new CalendarEvent();
        newEvent.Start = parsedDate.Date;
        newEvent.Title = title.Trim();
        newEvent.ColorIndex = colorIndex;
        newEvent.IsAllDay = isAllDay;
        if (hasTime)
        {
            newEvent.Time = parsedTime;
            newEvent.HasTime = true;
        }
        else
        {
            newEvent.Time = TimeSpan.Zero;
            newEvent.HasTime = false;
        }

        if (reminderMinutes > 0) // Erinnerung gewünscht?
        {
            newEvent.ReminderMinutesBefore = reminderMinutes;
        }

        await this.calendarService.AddEventAsync(newEvent); // Speichere in Datenbank

        string timeString;
        if (isAllDay)
        {
            timeString = "Ganztägig";
        }
        else
        {
            if (hasTime)
            {
                timeString = parsedTime.ToString(@"hh\:mm");
            }
            else
            {
                timeString = "Keine Zeit";
            }
        }

        var result = new // Erstelle JSON-Result mit Erfolg
        {
            success = true,
            message = $"Termin '{title}' für {parsedDate:dd.MM.yyyy} erstellt",
            eventId = newEvent.Id,
            date = parsedDate.ToString("dd.MM.yyyy"),
            title = title,
            time = timeString
        };

        return JsonSerializer.Serialize(result); // Gib JSON zurück
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Löscht einen Kalender-Termin. Nutze ID ODER Datum+Titel. Wenn mehrere Termine am Datum gefunden: gib Liste zurück zum Nachfragen.")]
    public async Task<string> DeleteCalendarEvent( // Tool: Löscht Termin aus Datenbank (flexibel per ID oder Datum+Titel)
        [Description("Die ID des Termins (optional, wenn date gesetzt)")] int eventId = -1,
        [Description("Datum im Format 'yyyy-MM-dd' oder 'dd.MM' (optional, wenn eventId gesetzt)")] string date = "",
        [Description("Titel des Termins (optional, hilft bei Mehrdeutigkeit)")] string title = "")
    {
        var allEvents = await this.calendarService.GetAllEventsAsync(); // Lade alle Termine

        // FALL 1: Lösche per ID
        if (eventId >= 0) // ID angegeben?
        {
            CalendarEvent foundEvent = new CalendarEvent();
            bool eventFound = false;
            foreach (CalendarEvent e in allEvents)
            {
                if (e.Id == eventId)
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

        // FALL 2: Lösche per Datum (+ optional Titel)
        if (!string.IsNullOrEmpty(date)) // Datum angegeben?
        {
            DateTime parsedDate;

            // Parse flexibles Datum (z.B. "19.03" oder "2025-03-19")
            if (date.Contains("-")) // Format "yyyy-MM-dd"?
            {
                if (!DateTime.TryParse(date, out parsedDate))
                {
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'." });
                }
            }
            else if (date.Contains(".")) // Format "dd.MM" ohne Jahr?
            {
                var parts = date.Split('.');
                if (parts.Length == 2)
                {
                    int day;
                    int month;
                    bool dayParsed = int.TryParse(parts[0], out day);
                    bool monthParsed = int.TryParse(parts[1], out month);
                    if (dayParsed && monthParsed)
                    {
                        parsedDate = new DateTime(DateTime.Now.Year, month, day); // Nehme aktuelles Jahr
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

            // Suche alle Termine an diesem Tag
            var eventsOnDate = new List<CalendarEvent>();
            foreach (CalendarEvent e in allEvents)
            {
                if (e.Start.Date == parsedDate.Date)
                {
                    eventsOnDate.Add(e);
                }
            }

            if (eventsOnDate.Count == 0) // Keine Termine gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Keine Termine am {parsedDate:dd.MM.yyyy} gefunden." });
            }

            // Wenn Titel angegeben: Filtere danach
            if (!string.IsNullOrWhiteSpace(title))
            {
                var filteredEvents = new List<CalendarEvent>();
                foreach (CalendarEvent e in eventsOnDate)
                {
                    if (e.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        filteredEvents.Add(e);
                    }
                }
                eventsOnDate = filteredEvents;

                if (eventsOnDate.Count == 0)
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Kein Termin mit Titel '{title}' am {parsedDate:dd.MM.yyyy} gefunden." });
                }
            }

            // Wenn mehrere Termine gefunden: Gib Liste zurück zum Nachfragen
            if (eventsOnDate.Count > 1)
            {
                var eventList = new List<object>();
                foreach (CalendarEvent e in eventsOnDate)
                {
                    string timeString;
                    if (e.IsAllDay)
                    {
                        timeString = "Ganztägig";
                    }
                    else
                    {
                        if (e.HasTime)
                        {
                            timeString = e.Time.ToString(@"hh\:mm");
                        }
                        else
                        {
                            timeString = "Keine Zeit";
                        }
                    }

                    eventList.Add(new { id = e.Id, title = e.Title, time = timeString });
                }

                return JsonSerializer.Serialize(new { success = false, needsClarification = true, message = $"{eventsOnDate.Count} Termine am {parsedDate:dd.MM.yyyy} gefunden. Bitte wähle:", events = eventList });
            }

            // Genau 1 Termin gefunden: Lösche ihn
            var singleEventToDelete = eventsOnDate[0];
            await this.calendarService.DeleteEventAsync(singleEventToDelete.Id);
            return JsonSerializer.Serialize(new { success = true, message = $"Termin '{singleEventToDelete.Title}' wurde gelöscht", eventId = singleEventToDelete.Id, title = singleEventToDelete.Title });
        }

        // FALL 3: Weder ID noch Datum angegeben
        return JsonSerializer.Serialize(new { success = false, message = "Bitte gib entweder eine ID oder ein Datum an." });
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Aktualisiert einen Kalender-Termin. Finde Termin per ID oder Datum+Titel. Änderbare Felder: title, date, isAllDay, time, color (Farbe als Text z.B. 'gelb', 'blau'), reminder (Erinnerung in Minuten, z.B. 15, 30, 60, 1440).")]
    public async Task<string> UpdateCalendarEvent( // Tool: Aktualisiert bestehenden Termin
        [Description("ID des Termins (wenn bekannt)")] int eventId = -1,
        [Description("Aktuelles Datum zum Finden des Termins (Format 'yyyy-MM-dd' oder 'dd.MM')")] string currentDate = "",
        [Description("Aktueller Titel zum Finden des Termins")] string currentTitle = "",
        [Description("Neuer Titel (optional)")] string newTitle = "",
        [Description("Neues Datum (Format 'yyyy-MM-dd', optional)")] string newDate = "",
        [Description("Neuer Ganztägig-Status als String: 'true' für ganztägig, 'false' für mit Uhrzeit (optional)")] string newIsAllDay = "",
        [Description("Neue Uhrzeit (Format 'HH:mm', optional)")] string newTime = "",
        [Description("Neue Farbe als Text: 'grau/oliv', 'blau', 'lachs/rosa', 'grün', 'gelb', 'lila/violett', 'pink', 'mint' (optional)")] string newColor = "",
        [Description("Neue Erinnerung X Minuten vorher (z.B. 15, 30, 60, 1440). -1 = Erinnerung entfernen (optional)")] int newReminderMinutes = -999)
    {
        var allEvents = await this.calendarService.GetAllEventsAsync();
        CalendarEvent eventToUpdate = new CalendarEvent();
        bool updateEventFound = false;

        // SCHRITT 1: Finde Termin (per ID oder Datum+Titel)
        if (eventId >= 0)
        {
            foreach (CalendarEvent e in allEvents)
            {
                if (e.Id == eventId)
                {
                    eventToUpdate = e;
                    updateEventFound = true;
                    break;
                }
            }
        }
        else if (!string.IsNullOrEmpty(currentDate))
        {
            // Parse Datum flexibel
            DateTime parsedDate;
            if (currentDate.Contains("-"))
            {
                bool parseSuccess = DateTime.TryParse(currentDate, out parsedDate);
                if (!parseSuccess)
                    return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
            }
            else if (currentDate.Contains("."))
            {
                var parts = currentDate.Split('.');
                if (parts.Length == 2)
                {
                    int day;
                    int month;
                    bool dayParsed = int.TryParse(parts[0], out day);
                    bool monthParsed = int.TryParse(parts[1], out month);
                    if (dayParsed && monthParsed)
                    {
                        parsedDate = new DateTime(DateTime.Now.Year, month, day);
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
            }
            else
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ungültiges Datum-Format." });
            }

            var eventsOnDate = new List<CalendarEvent>();
            foreach (CalendarEvent e in allEvents)
            {
                if (e.Start.Date == parsedDate.Date)
                {
                    eventsOnDate.Add(e);
                }
            }

            if (!string.IsNullOrWhiteSpace(currentTitle))
            {
                var filteredEvents = new List<CalendarEvent>();
                foreach (CalendarEvent e in eventsOnDate)
                {
                    if (e.Title.Contains(currentTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        filteredEvents.Add(e);
                    }
                }
                eventsOnDate = filteredEvents;
            }

            if (eventsOnDate.Count == 0)
            {
                string titlePart = "";
                if (!string.IsNullOrWhiteSpace(currentTitle))
                {
                    titlePart = $" mit Titel '{currentTitle}'";
                }
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Termin am {parsedDate:dd.MM.yyyy}" + titlePart + " gefunden." });
            }

            if (eventsOnDate.Count > 1)
            {
                var eventList = new List<object>();
                foreach (CalendarEvent e in eventsOnDate)
                {
                    string eventTimeString;
                    if (e.IsAllDay)
                    {
                        eventTimeString = "Ganztägig";
                    }
                    else
                    {
                        if (e.HasTime)
                        {
                            eventTimeString = e.Time.ToString(@"hh\:mm");
                        }
                        else
                        {
                            eventTimeString = "Keine Zeit";
                        }
                    }

                    eventList.Add(new { id = e.Id, title = e.Title, time = eventTimeString });
                }

                return JsonSerializer.Serialize(new { success = false, needsClarification = true, message = $"{eventsOnDate.Count} Termine gefunden. Bitte wähle per ID:", events = eventList });
            }

            eventToUpdate = eventsOnDate[0];
            updateEventFound = true;
        }
        else
        {
            return JsonSerializer.Serialize(new { success = false, message = "Bitte gib entweder eine ID oder ein Datum an." });
        }

        if (updateEventFound == false)
        {
            return JsonSerializer.Serialize(new { success = false, message = "Termin nicht gefunden." });
        }

        // SCHRITT 2: Aktualisiere Felder
        bool wasUpdated = false;

        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            eventToUpdate.Title = newTitle.Trim();
            wasUpdated = true;
        }

        if (!string.IsNullOrEmpty(newDate))
        {
            DateTime parsedNewDate;
            bool parseDateSuccess = DateTime.TryParse(newDate, out parsedNewDate);
            if (parseDateSuccess)
            {
                eventToUpdate.Start = parsedNewDate.Date;
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrEmpty(newIsAllDay))
        {
            if (newIsAllDay.ToLower() == "true")
            {
                eventToUpdate.IsAllDay = true;
                eventToUpdate.Time = TimeSpan.Zero;
                eventToUpdate.HasTime = false;
                wasUpdated = true;
            }
            else if (newIsAllDay.ToLower() == "false")
            {
                eventToUpdate.IsAllDay = false;
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrEmpty(newTime))
        {
            TimeSpan parsedNewTime;
            bool parseSuccess = TimeSpan.TryParse(newTime, out parsedNewTime);
            if (parseSuccess)
            {
                eventToUpdate.Time = parsedNewTime;
                eventToUpdate.HasTime = true;
                eventToUpdate.IsAllDay = false; // Wenn Zeit gesetzt → nicht ganztägig
                wasUpdated = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(newColor))
        {
            // Mappe Farbnamen zu ColorIndex (0-7)
            var colorMapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
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
            bool colorFound = colorMapping.TryGetValue(newColor.Trim(), out colorIndexValue);
            if (colorFound)
            {
                eventToUpdate.ColorIndex = colorIndexValue;
                wasUpdated = true;
            }
            else
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Unbekannte Farbe '{newColor}'. Nutze: grau, blau, lachs, grün, gelb, lila, pink, mint." });
            }
        }

        if (newReminderMinutes != -999) // Reminder-Update gewünscht?
        {
            if (newReminderMinutes == -1) // Erinnerung entfernen?
            {
                eventToUpdate.ReminderMinutesBefore = null;
                eventToUpdate.ReminderShown = false;
                wasUpdated = true;
            }
            else if (newReminderMinutes >= 0) // Neue Erinnerung setzen
            {
                eventToUpdate.ReminderMinutesBefore = newReminderMinutes;
                eventToUpdate.ReminderShown = false; // Reset, falls bereits angezeigt
                wasUpdated = true;
            }
        }

        if (!wasUpdated)
            return JsonSerializer.Serialize(new { success = false, message = "Keine Änderungen angegeben." });

        // SCHRITT 3: Speichere Änderungen
        await this.calendarService.UpdateEventAsync(eventToUpdate);

        string finalTimeString;
        if (eventToUpdate.IsAllDay)
        {
            finalTimeString = "Ganztägig";
        }
        else
        {
            if (eventToUpdate.HasTime)
            {
                finalTimeString = eventToUpdate.Time.ToString(@"hh\:mm");
            }
            else
            {
                finalTimeString = "Keine Zeit";
            }
        }

        return JsonSerializer.Serialize(new 
        { 
            success = true, 
            message = $"Termin '{eventToUpdate.Title}' wurde aktualisiert",
            eventId = eventToUpdate.Id,
            title = eventToUpdate.Title,
            date = eventToUpdate.Start.ToString("dd.MM.yyyy"),
            time = finalTimeString
        });
    }
}
