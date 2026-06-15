namespace KIDT.Services.Logic;

public static class RouterFunctionName // Normalisiert von Gemini gelieferte Funktionsnamen
{
    private static readonly string[] KnownPrefixes = { "Document_", "Folder_", "Calendar_", "Analysis_" }; // Plugin-Präfixe die Gemini fälschlich anhängt

    public static string Normalize(string rawName) // Entfernt fälschlich angehängtes Plugin-Präfix
    {
        if (string.IsNullOrEmpty(rawName)) return rawName; // Leerer Name unverändert

        foreach (string prefix in KnownPrefixes) // Jeden bekannten Präfix prüfen
        {
            if (rawName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) // Präfix vorhanden?
            {
                return rawName.Substring(prefix.Length); // Präfix abschneiden
            }
        }
        return rawName; // Kein Präfix → unverändert
    }
}
