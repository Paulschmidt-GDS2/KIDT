using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using KIDT.Services;
using KIDT.Models;

namespace KIDT.Components.Pages;

public partial class Home // Termin-Edit-Dialog: Öffnen, Speichern, Löschen
{
    private bool showEventEditDialog = false;
    private CalendarEvent? editingEvent = null;
    private string editEventTitle = string.Empty;
    private bool editIsAllDay = true;
    private string editTime = "12:00";
    private string editEndTime = "13:00";
    private int editTimeHour = 12;
    private int editTimeMinute = 0;
    private int editEndTimeHour = 13;
    private int editEndTimeMinute = 0;
    private int editColorIndex = 0;
    private int? editReminderMinutes = null;
    private string[] editAvailableColors = { "#848877", "#A8C5DD", "#F4C2B8", "#C3E5A4", "#F7DC9F", "#D4B5E8", "#FFB6C1", "#B0D9B0" };

    private async Task DeleteEventFromChat(int eventId) // Löscht Termin direkt aus Event-Card
    {
        try
        {
            using var scope = ServiceProvider.CreateScope();
            var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();

            var calendarEvent = await calendarService.GetEventByIdAsync(eventId);
            string eventTitle = "Unbekannt"; // Titel für Bestätigung
            if (calendarEvent != null) eventTitle = calendarEvent.Title;

            await calendarService.DeleteEventAsync(eventId); // Aus DB löschen

            foreach (var msg in Messages) // Event aus allen Chat-Nachrichten entfernen
            {
                if (msg.FoundEvents.Count > 0)
                {
                    CalendarEvent? eventToRemove = null; // Suche Event mit passender ID
                    foreach (var ev in msg.FoundEvents)
                    {
                        if (ev.Id == eventId)
                        {
                            eventToRemove = ev;
                            break;
                        }
                    }
                    if (eventToRemove != null) msg.FoundEvents.Remove(eventToRemove); // Aus Liste entfernen
                }
            }

            var confirmMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(confirmMsg);
            StateHasChanged();
            await TypewriterEffect(confirmMsg, $"Termin '{eventTitle}' wurde gelöscht.");
        }
        catch (Exception ex)
        {
            var errorMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(errorMsg);
            StateHasChanged();
            await TypewriterEffect(errorMsg, $"Fehler: {ex.Message}");
        }
    }

    private async Task OpenEventEditDialog(int eventId) // Öffnet Edit-Dialog und befüllt Felder
    {
        try
        {
            using var scope = ServiceProvider.CreateScope();
            var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();

            editingEvent = await calendarService.GetEventByIdAsync(eventId);
            if (editingEvent == null) return;

            editEventTitle = editingEvent.Title;
            editIsAllDay = editingEvent.IsAllDay;
            editColorIndex = editingEvent.ColorIndex;
            editReminderMinutes = editingEvent.ReminderMinutesBefore;

            if (editingEvent.HasTime) // Startzeit laden
            {
                editTime = editingEvent.Time.ToString(@"hh\:mm");
            }
            else
            {
                editTime = "12:00";
            }
            if (editingEvent.HasEndTime) // Endzeit laden
            {
                editEndTime = editingEvent.EndTime.ToString(@"hh\:mm");
            }
            else
            {
                editEndTime = "13:00";
            }

            if (editingEvent.HasTime) // Stunden-/Minuten-Felder aus vorhandener Zeit
            {
                editTimeHour = editingEvent.Time.Hours;
                editTimeMinute = editingEvent.Time.Minutes;
            }
            else
            {
                editTimeHour = 12;
                editTimeMinute = 0;
            }

            if (editingEvent.HasEndTime)
            {
                editEndTimeHour = editingEvent.EndTime.Hours;
                editEndTimeMinute = editingEvent.EndTime.Minutes;
            }
            else
            {
                editEndTimeHour = 13;
                editEndTimeMinute = 0;
            }

            showEventEditDialog = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EDIT] Fehler beim Öffnen: {ex.Message}");
        }
    }

    private int ClampHour(int h) // Begrenzt Stunden auf 0-23
    {
        if (h < 0) return 0;
        if (h > 23) return 23;
        return h;
    }

    private int ClampMinute(int m) // Begrenzt Minuten auf 0-59
    {
        if (m < 0) return 0;
        if (m > 59) return 59;
        return m;
    }

    private string FormatTimeStr(int h, int m) // Erstellt Zeit-String "HH:MM"
    {
        return $"{h:D2}:{m:D2}";
    }

    private void OnEditStartHourChanged(ChangeEventArgs e) // Handler: Stunden der Startzeit
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        editTimeHour = h;
        editTime = FormatTimeStr(editTimeHour, editTimeMinute);
    }

    private void OnEditStartMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Startzeit
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        editTimeMinute = m;
        editTime = FormatTimeStr(editTimeHour, editTimeMinute);
    }

    private void OnEditEndHourChanged(ChangeEventArgs e) // Handler: Stunden der Endzeit
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        editEndTimeHour = h;
        editEndTime = FormatTimeStr(editEndTimeHour, editEndTimeMinute);
    }

    private void OnEditEndMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Endzeit
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        editEndTimeMinute = m;
        editEndTime = FormatTimeStr(editEndTimeHour, editEndTimeMinute);
    }

    private async Task SaveEventEdit() // Speichert Änderungen am Termin aus dem Dialog
    {
        if (editingEvent == null) return;

        try
        {
            using var scope = ServiceProvider.CreateScope();
            var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();

            editingEvent.Title = editEventTitle.Trim();
            editingEvent.IsAllDay = editIsAllDay;
            editingEvent.ColorIndex = editColorIndex;

            if (editingEvent.ReminderMinutesBefore != editReminderMinutes) // Reminder geändert → zurücksetzen
            {
                editingEvent.ReminderMinutesBefore = editReminderMinutes;
                editingEvent.ReminderShown = false;
            }

            if (!editIsAllDay && TimeSpan.TryParse(editTime, out TimeSpan parsedTime)) // Startzeit gültig?
            {
                editingEvent.Time = parsedTime;
                editingEvent.HasTime = true;

                TimeSpan parsedEnd;
                bool endParsed = TimeSpan.TryParse(editEndTime, out parsedEnd);
                if (endParsed && parsedEnd > parsedTime) // Endzeit nach Startzeit?
                {
                    editingEvent.EndTime = parsedEnd;
                    editingEvent.HasEndTime = true;
                }
                else // Ungültige Endzeit: zurücksetzen
                {
                    editingEvent.EndTime = TimeSpan.Zero;
                    editingEvent.HasEndTime = false;
                }
            }
            else // Ganztägig: Zeiten zurücksetzen
            {
                editingEvent.Time = TimeSpan.Zero;
                editingEvent.HasTime = false;
                editingEvent.EndTime = TimeSpan.Zero;
                editingEvent.HasEndTime = false;
            }

            await calendarService.UpdateEventAsync(editingEvent);

            foreach (var msg in Messages) // Aktualisierte Daten in alle Chat-Nachrichten übernehmen
            {
                foreach (var evt in msg.FoundEvents)
                {
                    if (evt.Id == editingEvent.Id)
                    {
                        evt.Title = editingEvent.Title;
                        evt.IsAllDay = editingEvent.IsAllDay;
                        evt.Time = editingEvent.Time;
                        evt.HasTime = editingEvent.HasTime;
                        evt.EndTime = editingEvent.EndTime;
                        evt.HasEndTime = editingEvent.HasEndTime;
                        evt.ColorIndex = editingEvent.ColorIndex;
                        evt.ReminderMinutesBefore = editingEvent.ReminderMinutesBefore;
                        evt.ReminderShown = editingEvent.ReminderShown;
                    }
                }
            }

            showEventEditDialog = false;
            StateHasChanged();

            var confirmMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(confirmMsg);
            StateHasChanged();
            await TypewriterEffect(confirmMsg, $"Termin '{editingEvent.Title}' wurde aktualisiert.");
        }
        catch (Exception ex)
        {
            var errorMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(errorMsg);
            StateHasChanged();
            await TypewriterEffect(errorMsg, $"Fehler: {ex.Message}");
        }
    }

    private async Task DeleteEventFromDialog() // Löscht Termin aus dem Edit-Dialog heraus
    {
        if (editingEvent == null) return;

        await DeleteEventFromChat(editingEvent.Id);
        showEventEditDialog = false;
        StateHasChanged();
    }

    private void CloseEventEditDialog() // Schließt Edit-Dialog ohne Speichern
    {
        showEventEditDialog = false;
        editingEvent = null;
        StateHasChanged();
    }
}
