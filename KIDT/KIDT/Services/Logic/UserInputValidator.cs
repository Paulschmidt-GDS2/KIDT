namespace KIDT.Services.Logic;

public static class UserInputValidator // Prüft Nutzereingaben auf Leere/Gibberish-Wiederholungen
{
    public static bool IsValid(string text) // Blockiert leere oder stark repetitive Eingaben
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false; // Leer oder zu kurz

        if (text.Length > 40 && !text.Contains(' ')) // Sehr lange Eingabe ohne Leerzeichen → Wiederholungsverdacht
        {
            string pattern = text.Substring(0, Math.Min(5, text.Length)); // Anfangsmuster als Vergleichsbasis
            int count = 0;
            for (int i = 0; i <= text.Length - pattern.Length; i += pattern.Length) // In Blöcken der Musterlänge prüfen
            {
                if (text.Substring(i, pattern.Length).Equals(pattern, StringComparison.OrdinalIgnoreCase)) // Block gleich dem Muster?
                {
                    count++; // Wiederholung zählen
                }
            }
            if (count >= 4) return false; // 4+ Wiederholungen → ungültig
        }
        return true; // Eingabe ok
    }
}
