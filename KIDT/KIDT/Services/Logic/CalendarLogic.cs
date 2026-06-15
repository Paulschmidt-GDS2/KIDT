using System.Globalization;

namespace KIDT.Services.Logic;

public static class CalendarLogic // Reine, DB-freie Kalender-Logik (testbar ohne MAUI/MySQL)
{
    public static string? ParseFlexibleDate(string input, out DateTime result) // Parst "yyyy-MM-dd" oder "dd.MM" (aktuelles Jahr); gibt Fehlertext zurück oder null bei Erfolg
    {
        result = DateTime.MinValue;

        if (input == null) return "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'."; // Null-Guard

        if (input.Contains("-")) // Format "yyyy-MM-dd"
        {
            bool ok = DateTime.TryParse(input, out result); // ISO-Datum parsen
            if (!ok) return "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'.";
            return null; // Erfolg
        }

        if (input.Contains(".")) // Format "dd.MM" ohne Jahr
        {
            string[] parts = input.Split('.'); // In Tag/Monat zerlegen
            if (parts.Length == 2)
            {
                bool dayOk = int.TryParse(parts[0], out int day); // Tag parsen
                bool monthOk = int.TryParse(parts[1], out int month); // Monat parsen
                if (dayOk && monthOk && IsValidDayMonth(day, month)) // Gültige Tag/Monat-Kombination?
                {
                    result = new DateTime(DateTime.Now.Year, month, day); // Aktuelles Jahr annehmen
                    return null; // Erfolg
                }
            }
            return "Ungültiges Datum-Format. Nutze 'dd.MM' (z.B. '19.03').";
        }

        return "Ungültiges Datum-Format. Nutze 'yyyy-MM-dd' oder 'dd.MM'.";
    }

    private static bool IsValidDayMonth(int day, int month) // Prüft groben Wertebereich für Tag/Monat
    {
        if (month < 1 || month > 12) return false; // Monat außerhalb 1-12
        if (day < 1 || day > 31) return false; // Tag außerhalb 1-31
        return true;
    }

    public static bool EventsOverlap(TimeSpan newStart, TimeSpan newEnd, TimeSpan existingStart, TimeSpan existingEnd) // Prüft Zeitüberschneidung zweier Zeitspannen
    {
        bool overlaps = newStart < existingEnd && newEnd > existingStart; // Standard-Überlappungsformel
        return overlaps;
    }

    public static bool MapColorNameToIndex(string colorName, out int index) // Mappt deutschen Farbnamen auf ColorIndex (0-7); false wenn unbekannt
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(colorName)) return false; // Leerer Name

        Dictionary<string, int> colorMapping = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) // Case-insensitive Farb-Mapping
        {
            { "grau", 0 }, { "oliv", 0 }, { "standard", 0 },
            { "blau", 1 }, { "hellblau", 1 },
            { "lachs", 2 }, { "rosa", 2 }, { "orange", 2 },
            { "grün", 3 }, { "hellgrün", 3 }, { "gruen", 3 },
            { "gelb", 4 }, { "gold", 4 },
            { "lila", 5 }, { "violett", 5 }, { "purple", 5 },
            { "pink", 6 }, { "magenta", 6 },
            { "mint", 7 }, { "türkis", 7 }, { "tuerkis", 7 }
        };

        bool found = colorMapping.TryGetValue(colorName.Trim(), out index); // Farbe nachschlagen
        return found;
    }

    public static string FormatEventTime(bool isAllDay, bool hasTime, TimeSpan time, bool hasEndTime, TimeSpan endTime) // Formatiert Zeitangabe einheitlich für Anzeige
    {
        if (isAllDay) return "Ganztägig"; // Ganztägiger Termin
        if (hasTime && hasEndTime) return $"{time:hh\\:mm} - {endTime:hh\\:mm}"; // Zeitspanne "14:00 - 16:00"
        if (hasTime) return time.ToString(@"hh\:mm", CultureInfo.InvariantCulture); // Nur Startzeit "14:00"
        return "Keine Zeit"; // Keine Uhrzeit gesetzt
    }
}
