using System.Globalization;
using KIDT.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace KIDT.Components.Pages;

public partial class Kalender // Kalender-Logik: Ansichten, Termine laden/speichern/löschen (Code-Behind)
{
    private string currentView = "Month";
    private DateTime selectedDate = DateTime.Today;
    private DateTime DisplayMonth { get { return new DateTime(selectedDate.Year, selectedDate.Month, 1); } }
    private DateTime DisplayDay { get { return selectedDate.Date; } }
    private List<DateTime> MonthCells = new();
    private List<DateTime> WeekDays = new();
    private string[] DayNames = new[] { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
    private CultureInfo germanCulture = new CultureInfo("de-DE");

    private Dictionary<DateTime, List<CalendarEvent>> events = new();
    private string[] availableColors = new[] { "#848877", "#A8C5DD", "#F4C2B8", "#C3E5A4", "#F7DC9F", "#D4B5E8", "#FFB6C1", "#B0D9B0" };

    private bool showAddDialog = false;
    private bool showEditDialog = false;
    private DateTime dialogDate = DateTime.Today;
    private string newEventText = string.Empty;
    private int selectedColorIndex = 0;
    private CalendarEvent editingEvent = new CalendarEvent();
    private bool isAllDay = true;
    private string selectedTime = "09:00";
    private string selectedEndTime = "10:00";
    private int? newEventReminderMinutes = null;
    private int selectedTimeHour = 9;
    private int selectedTimeMinute = 0;
    private int selectedEndTimeHour = 10;
    private int selectedEndTimeMinute = 0;
    private int editStartHour = 9;
    private int editStartMinute = 0;
    private int editEndHour = 10;
    private int editEndMinute = 0;

    protected override async Task OnInitializedAsync() // Kalender beim Laden initialisieren
    {
        BuildMonthCells();
        BuildWeekDays();
        await LoadEventsAsync();
    }

    protected override async Task OnParametersSetAsync() // URL-Parameter prüfen (z.B. ?eventId=5 für direktes Öffnen)
    {
        var uri = Navigation.Uri;
        var queryIndex = uri.IndexOf("?eventId=");

        if (queryIndex > -1)
        {
            var queryPart = uri.Substring(queryIndex + 9);
            var ampIndex = queryPart.IndexOf("&");
            if (ampIndex > -1) queryPart = queryPart.Substring(0, ampIndex);

            if (int.TryParse(queryPart, out int eventId))
            {
                var ev = await CalendarService.GetEventByIdAsync(eventId);
                if (ev != null)
                {
                    editingEvent = ev;
                    InitEditTimeFields();
                    showEditDialog = true;
                    Navigation.NavigateTo("/Kalender", replace: true);
                }
            }
        }
    }

    private async Task LoadEventsAsync() // Alle Termine aus DB laden und nach Datum gruppieren
    {
        var allEvents = await CalendarService.GetAllEventsAsync();
        events.Clear();

        foreach (var ev in allEvents)
        {
            var date = ev.Start.Date; // Nur Datum ohne Uhrzeit als Key
            if (!events.ContainsKey(date)) events[date] = new List<CalendarEvent>();
            events[date].Add(ev);
        }
    }

    private void BuildMonthCells() // Baut 42 Zellen für Monats-Grid (startet montags)
    {
        MonthCells.Clear();
        var first = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7; // Offset berechnen (Montag = 0)
        var start = first.AddDays(-offset);
        for (int i = 0; i < 42; i++) MonthCells.Add(start.AddDays(i));
    }

    private void BuildWeekDays() // Baut 7 Tage für Wochenansicht (Montag–Sonntag)
    {
        WeekDays.Clear();
        int offset = ((int)selectedDate.DayOfWeek + 6) % 7; // Offset zum Montag
        var start = selectedDate.AddDays(-offset).Date;
        for (int i = 0; i < 7; i++) WeekDays.Add(start.AddDays(i));
    }

    private void Prev() // Zur vorherigen Periode navigieren
    {
        if (currentView == "Month") selectedDate = selectedDate.AddMonths(-1);
        else if (currentView == "Week") selectedDate = selectedDate.AddDays(-7);
        else selectedDate = selectedDate.AddDays(-1);
        BuildMonthCells();
        BuildWeekDays();
    }

    private void Next() // Zur nächsten Periode navigieren
    {
        if (currentView == "Month") selectedDate = selectedDate.AddMonths(1);
        else if (currentView == "Week") selectedDate = selectedDate.AddDays(7);
        else selectedDate = selectedDate.AddDays(1);
        BuildMonthCells();
        BuildWeekDays();
    }

    private void Today() // Zu heutiger Ansicht springen
    {
        selectedDate = DateTime.Today;
        BuildMonthCells();
        BuildWeekDays();
    }

    private void OnDayClick(DateTime day) // Klick auf Tag: Add-Dialog öffnen
    {
        dialogDate = day.Date;
        newEventText = string.Empty;
        selectedColorIndex = 0;
        isAllDay = true;
        selectedTime = "09:00";
        selectedEndTime = "10:00";
        selectedTimeHour = 9;
        selectedTimeMinute = 0;
        selectedEndTimeHour = 10;
        selectedEndTimeMinute = 0;
        newEventReminderMinutes = null;
        showAddDialog = true;
    }

    private void OnEventClick(CalendarEvent ev, MouseEventArgs e) // Klick auf Termin: Edit-Dialog öffnen
    {
        editingEvent = ev;
        InitEditTimeFields();
        showEditDialog = true;
    }

    private void CloseEditDialog() // Schließt Edit-Dialog
    {
        showEditDialog = false;
        editingEvent = new CalendarEvent(); // Zurücksetzen auf leeres Objekt
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

    private void InitEditTimeFields() // Lädt Zeitfelder aus editingEvent in Edit-Dialog
    {
        if (editingEvent == null) return;
        if (editingEvent.HasTime)
        {
            editStartHour = editingEvent.Time.Hours;
            editStartMinute = editingEvent.Time.Minutes;
        }
        else
        {
            editStartHour = 9;
            editStartMinute = 0;
        }
        if (editingEvent.HasEndTime)
        {
            editEndHour = editingEvent.EndTime.Hours;
            editEndMinute = editingEvent.EndTime.Minutes;
        }
        else
        {
            editEndHour = 10;
            editEndMinute = 0;
        }
    }

    private void OnAddStartHourChanged(ChangeEventArgs e) // Handler: Stunden der Startzeit (Add-Dialog)
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        selectedTimeHour = h;
        selectedTime = FormatTimeStr(selectedTimeHour, selectedTimeMinute);
    }

    private void OnAddStartMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Startzeit (Add-Dialog)
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        selectedTimeMinute = m;
        selectedTime = FormatTimeStr(selectedTimeHour, selectedTimeMinute);
    }

    private void OnAddEndHourChanged(ChangeEventArgs e) // Handler: Stunden der Endzeit (Add-Dialog)
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        selectedEndTimeHour = h;
        selectedEndTime = FormatTimeStr(selectedEndTimeHour, selectedEndTimeMinute);
    }

    private void OnAddEndMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Endzeit (Add-Dialog)
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        selectedEndTimeMinute = m;
        selectedEndTime = FormatTimeStr(selectedEndTimeHour, selectedEndTimeMinute);
    }

    private void OnEditStartHourChanged(ChangeEventArgs e) // Handler: Stunden der Startzeit (Edit-Dialog)
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        editStartHour = h;
        if (editingEvent != null)
        {
            editingEvent.Time = new TimeSpan(editStartHour, editStartMinute, 0);
            editingEvent.HasTime = true;
        }
    }

    private void OnEditStartMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Startzeit (Edit-Dialog)
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        editStartMinute = m;
        if (editingEvent != null)
        {
            editingEvent.Time = new TimeSpan(editStartHour, editStartMinute, 0);
            editingEvent.HasTime = true;
        }
    }

    private void OnEditEndHourChanged(ChangeEventArgs e) // Handler: Stunden der Endzeit (Edit-Dialog)
    {
        int h = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out h);
            h = ClampHour(h);
        }
        editEndHour = h;
        if (editingEvent != null)
        {
            editingEvent.EndTime = new TimeSpan(editEndHour, editEndMinute, 0);
            editingEvent.HasEndTime = true;
        }
    }

    private void OnEditEndMinuteChanged(ChangeEventArgs e) // Handler: Minuten der Endzeit (Edit-Dialog)
    {
        int m = 0;
        if (e.Value != null)
        {
            int.TryParse($"{e.Value}", out m);
            m = ClampMinute(m);
        }
        editEndMinute = m;
        if (editingEvent != null)
        {
            editingEvent.EndTime = new TimeSpan(editEndHour, editEndMinute, 0);
            editingEvent.HasEndTime = true;
        }
    }

    private async Task SaveEditedEvent() // Bearbeiteten Termin in DB speichern
    {
        if (editingEvent != null && !string.IsNullOrWhiteSpace(editingEvent.Title))
        {
            if (!editingEvent.IsAllDay && !editingEvent.HasTime) // Kein Ganztägig, keine Zeit: Default setzen
            {
                editingEvent.Time = new TimeSpan(9, 0, 0);
                editingEvent.HasTime = true;
            }
            else if (editingEvent.IsAllDay) // Ganztägig: Zeiten entfernen
            {
                editingEvent.Time = TimeSpan.Zero;
                editingEvent.HasTime = false;
                editingEvent.EndTime = TimeSpan.Zero;
                editingEvent.HasEndTime = false;
            }

            editingEvent.ReminderShown = false; // Reminder bei Bearbeitung zurücksetzen

            await CalendarService.UpdateEventAsync(editingEvent);
            await LoadEventsAsync();
        }
        CloseEditDialog();
    }

    private async Task DeleteEvent() // Termin aus DB löschen
    {
        if (editingEvent != null)
        {
            await CalendarService.DeleteEventDirectAsync(editingEvent);
            await LoadEventsAsync();
            CloseEditDialog();
        }
    }

    private void CloseDialog() // Schließt Add-Dialog
    {
        showAddDialog = false;
    }

    private async Task ConfirmAdd() // Neuen Termin speichern (aus Dialog)
    {
        await AddEventForDate(dialogDate);
        showAddDialog = false;
    }

    private async Task SaveDayViewEvent() // Termin direkt aus Tagesansicht speichern
    {
        await AddEventForDate(DisplayDay);
    }

    private async Task AddEventForDate(DateTime date) // Termin für Datum anlegen und in DB speichern
    {
        if (string.IsNullOrWhiteSpace(newEventText)) return;
        var d = date.Date;

        TimeSpan time = TimeSpan.Zero;
        bool hasTime = false;
        TimeSpan endTime = TimeSpan.Zero;
        bool hasEndTime = false;

        if (!isAllDay) // Nicht ganztägig: Zeiten parsen
        {
            TimeSpan parsedTime;
            bool parseSuccess = TimeSpan.TryParse(selectedTime, out parsedTime);
            if (parseSuccess)
            {
                time = parsedTime;
                hasTime = true;
            }

            TimeSpan parsedEnd;
            bool parseEndSuccess = TimeSpan.TryParse(selectedEndTime, out parsedEnd);
            if (parseEndSuccess && hasTime && parsedEnd > time) // Endzeit nach Startzeit?
            {
                endTime = parsedEnd;
                hasEndTime = true;
            }
        }

        var newEvent = new CalendarEvent();
        newEvent.Start = d;
        newEvent.Title = newEventText.Trim();
        newEvent.ColorIndex = selectedColorIndex;
        newEvent.IsAllDay = isAllDay;
        newEvent.Time = time;
        newEvent.HasTime = hasTime;
        newEvent.EndTime = endTime;
        newEvent.HasEndTime = hasEndTime;
        newEvent.ReminderMinutesBefore = newEventReminderMinutes;

        await CalendarService.AddEventAsync(newEvent);
        await LoadEventsAsync();

        // Eingabefelder zurücksetzen
        newEventText = string.Empty;
        selectedColorIndex = 0;
        isAllDay = true;
        selectedTime = "09:00";
        selectedEndTime = "10:00";
        selectedTimeHour = 9;
        selectedTimeMinute = 0;
        selectedEndTimeHour = 10;
        selectedEndTimeMinute = 0;
        newEventReminderMinutes = null;
    }

    private List<CalendarEvent> GetEventsForDate(DateTime date) // Liefert alle Termine für ein Datum
    {
        var d = date.Date;
        List<CalendarEvent>? list; // Variable für gefundene Events
        if (events.TryGetValue(d, out list)) return list; // Datum gefunden: Events zurückgeben
        return new List<CalendarEvent>(); // Nicht gefunden: leere Liste
    }

    private string FormatEventTime(CalendarEvent ev) // Formatiert Zeit als "HH:MM–HH:MM" oder "HH:MM"
    {
        if (ev.HasTime && ev.HasEndTime) return $"{ev.Time:hh\\:mm}–{ev.EndTime:hh\\:mm}"; // Zeitspanne
        return ev.Time.ToString(@"hh\:mm"); // Nur Startzeit
    }

    private string ViewTitle // Titel-Text je nach aktueller Ansicht
    {
        get
        {
            if (currentView == "Month") return DisplayMonth.ToString("MMMM yyyy", germanCulture);
            if (currentView == "Week") return $"Woche von {WeekDays[0].ToString("dd.MM.yyyy", germanCulture)}"; // Montag der Woche
            return DisplayDay.ToString("dddd, dd.MM.yyyy", germanCulture);
        }
    }
}
