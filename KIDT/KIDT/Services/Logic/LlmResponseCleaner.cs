using System.Text;

namespace KIDT.Services.Logic;

public static class LlmResponseCleaner // Bereinigt lokale LLM-Antworten (Anführungszeichen, Emojis, Debug-Marker)
{
    private static readonly string[] DebugMarkers = { "=== Input:", "=== Output:", "DENSE_CATEGORIES", "### QUERY REPORT", "As a solution", "--- Begin of code" }; // Typische Debug-Artefakte lokaler Modelle

    public static string Clean(string response) // Entfernt Wrapping-Quotes, Emojis und Debug-Zeilen
    {
        if (string.IsNullOrWhiteSpace(response)) return response; // Leere Antwort unverändert

        response = response.Trim();

        if (response.StartsWith("\"") && response.EndsWith("\"") && response.Length > 2) // Komplett in Anführungszeichen?
        {
            response = response.Substring(1, response.Length - 2); // Umschließende Quotes entfernen
        }

        response = response.Replace("✅", "").Replace("❌", "").Replace("🎉", "") // Häufige Emojis entfernen
                           .Replace("⚠️", "").Replace("✓", "").Replace("→", "");

        bool hasDebugContent = ContainsAnyMarker(response); // Enthält Antwort überhaupt Debug-Artefakte?
        if (!hasDebugContent) return response.Trim(); // Sauber → direkt zurück

        return RemoveDebugLines(response); // Zeilenweise bereinigen
    }

    private static bool ContainsAnyMarker(string text) // Prüft ob ein Debug-Marker enthalten ist
    {
        foreach (string marker in DebugMarkers) // Alle Marker durchgehen
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true; // Marker gefunden
        }
        return false;
    }

    private static string RemoveDebugLines(string response) // Filtert Debug-Zeilen heraus
    {
        string[] lines = response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries); // In Zeilen zerlegen
        StringBuilder cleaned = new StringBuilder();
        bool skipMode = false;

        foreach (string line in lines) // Jede Zeile prüfen
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || IsAllChar(trimmed, '=')) continue; // Leer- oder Trennzeilen überspringen

            bool isDebugLine = LineHasMarker(trimmed); // Debug-Marker in dieser Zeile?
            if (isDebugLine) skipMode = true; // Ab hier überspringen

            if (!isDebugLine && !skipMode) // Normale Zeile vor jedem Debug-Block
            {
                cleaned.AppendLine(line);
            }
            else if (!isDebugLine && skipMode && trimmed.Length > 10) // Längere Zeile nach Debug-Block → echter Inhalt
            {
                skipMode = false; // Skip beenden
                cleaned.AppendLine(line);
            }
        }

        string result = cleaned.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result)) return "Wie kann ich dir helfen?"; // Nichts übrig → Fallback
        return result;
    }

    private static bool LineHasMarker(string trimmed) // Prüft ob Zeile einen Debug-Marker enthält
    {
        foreach (string marker in DebugMarkers) // Alle Marker durchgehen
        {
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true; // Marker gefunden
        }
        return false;
    }

    private static bool IsAllChar(string text, char c) // Prüft ob Zeile nur aus dem angegebenen Zeichen besteht
    {
        if (text.Length == 0) return false; // Leere Zeile zählt nicht
        for (int i = 0; i < text.Length; i++) // Jedes Zeichen prüfen
        {
            if (text[i] != c) return false; // Abweichung gefunden
        }
        return true;
    }
}
