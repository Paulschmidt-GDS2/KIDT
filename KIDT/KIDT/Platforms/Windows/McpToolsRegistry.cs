using Microsoft.SemanticKernel;

namespace KIDT.Services;

/// <summary>
/// Helper-Klasse zum Synchronisieren der MCP-Server Tools mit Semantic Kernel.
/// Die Tools sind definiert in: McpServer/Program.cs
/// Diese Klasse stellt sicher, dass sie im Client verfügbar sind.
/// </summary>
public static class McpToolsRegistry
{
    /// <summary>
    /// Registriert alle MCP-Tools vom Server als Kernel-Funktionen.
    /// WICHTIG: Diese Methode muss synchron mit McpServer/Program.cs gehalten werden!
    /// </summary>
    public static void RegisterTools(Kernel kernel) // Fügt EchoMessage, AnalyzeMessage, GetFileContent als Kernel-Functions hinzu
    {
        // ==================================================================
        // CommunicationTools (aus McpServer/Program.cs)
        // ==================================================================

        // Tool 1: EchoMessage - Test-Tool für Kommunikation
        var echoMessage = KernelFunctionFactory.CreateFromMethod( // Tool: Gibt "[ECHO] {message}" zurück
            (string message) => $"[ECHO] {message}", // Lambda: Nimmt String, gibt String mit Prefix zurück
            "EchoMessage", // Tool-Name (muss exakt mit McpServer übereinstimmen)
            "Gibt die empfangene Nachricht mit [ECHO] Präfix zurück. Nützlich zum Testen der Kommunikation.", // Tool-Beschreibung für KI
            new[] { new KernelParameterMetadata("message") { Description = "Die Nachricht, die zurückgegeben werden soll" } } // Parameter-Metadaten für KI
        );

        // Tool 2: AnalyzeMessage - Textanalyse (Wortanzahl, Zeichenanzahl)
        var analyzeMessage = KernelFunctionFactory.CreateFromMethod( // Tool: Zählt Wörter und Zeichen (mit/ohne Spaces)
            (string message) => // Lambda mit komplexerer Logik
            {
                if (string.IsNullOrWhiteSpace(message)) // Check: Ist Nachricht leer oder nur Whitespace?
                    return "Fehler: Nachricht ist leer."; // Ja ? Fehler zurück

                var words = message.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries); // Split: Teile bei Whitespace, entferne leere Einträge
                var wordCount = words.Length; // Zähle Array-Elemente = Wortanzahl
                var charCount = message.Length; // String-Länge = Zeichenanzahl mit Leerzeichen
                var charCountNoSpaces = message.Replace(" ", "").Length; // Replace alle Spaces ? String-Länge = Zeichen ohne Leerzeichen

                return $"Nachrichten-Analyse:\n" + // Multi-line String für formatierte Ausgabe
                       $"- Wortanzahl: {wordCount}\n" + // Interpolation: Füge Wortanzahl ein
                       $"- Zeichenanzahl (mit Leerzeichen): {charCount}\n" + // Interpolation: Zeichenanzahl mit
                       $"- Zeichenanzahl (ohne Leerzeichen): {charCountNoSpaces}"; // Interpolation: Zeichenanzahl ohne
            },
            "AnalyzeMessage", // Tool-Name (muss exakt mit McpServer übereinstimmen)
            "Analysiert eine Nachricht und gibt Statistiken wie Wortanzahl und Zeichenanzahl zurück.", // Tool-Beschreibung für KI
            new[] { new KernelParameterMetadata("message") { Description = "Die zu analysierende Nachricht" } } // Parameter-Metadaten für KI
        );

        // ==================================================================
        // DocumentTools (aus McpServer/Program.cs)
        // ==================================================================

        // Tool 3: GetFileContent - Datei-Inhalt lesen für Dokumenten-Analyse
        var getFileContent = KernelFunctionFactory.CreateFromMethod( // Tool: Liest Datei asynchron, gibt Inhalt oder Fehler zurück
            async (string filePath) => // Async Lambda für I/O-Operation
            {
                if (!File.Exists(filePath)) // Check: Existiert Datei unter diesem Pfad?
                    return $"Fehler: Datei nicht gefunden unter {filePath}"; // Nein ? Fehler mit Pfad zurück

                try // Try-Block für I/O-Fehlerbehandlung
                {
                    return await File.ReadAllTextAsync(filePath); // Lese Datei asynchron, gib Inhalt zurück
                }
                catch (Exception ex) // Fange alle I/O-Exceptions (z.B. Zugriffsfehler)
                {
                    return $"Fehler beim Lesen der Datei: {ex.Message}"; // Gib Fehler mit Exception-Message zurück
                }
            },
            "GetFileContent", // Tool-Name (muss exakt mit McpServer übereinstimmen)
            "Liest und gibt den Inhalt einer lokalen Datei zurück. Nützlich für Dokumenten-Analyse.", // Tool-Beschreibung für KI
            new[] { new KernelParameterMetadata("filePath") { Description = "Vollständiger Pfad zur Datei" } } // Parameter-Metadaten für KI
        );

        // Alle Tools als Plugin zum Kernel hinzufügen
        kernel.Plugins.AddFromFunctions( // Registriert alle 3 Tools als "McpTools" Plugin im Kernel
            "McpTools", // Plugin-Name (Namespace für Tools)
            new[] // Array von Funktionen
            {
                echoMessage, // Tool 1: Echo
                analyzeMessage, // Tool 2: Analyse
                getFileContent // Tool 3: Datei lesen
            }
        );
    }

    // TODO für später: Automatische Tool-Discovery über Reflection
    // Dies würde die manuelle Synchronisation überflüssig machen
}