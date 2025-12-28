using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;

var builder = Host.CreateApplicationBuilder(args); // Erstelle Host-Builder mit Command-Line-Args

// Logging konfigurieren (Console-Output für Debug-Zwecke)
builder.Logging.AddConsole(consoleLogOptions => // Konfiguriert Console-Logging für alle Trace-Levels
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace; // Setze Threshold: Alle Logs (auch Trace) werden ausgegeben
});

// MCP Server registrieren und konfigurieren
builder.Services // Zugriff auf Service-Collection
    .AddMcpServer() // Registriere MCP-Server-Services
    .WithStdioServerTransport() // Konfiguriere stdio-Transport (Standard Input/Output für Kommunikation)
    .WithToolsFromAssembly(); // Lade alle Tools automatisch aus diesem Assembly (via Reflection)

await builder.Build().RunAsync(); // Baue Host → Starte Server → Warte auf Beendigung (async)

// ==================================================================
// CommunicationTools - Tools für Kommunikations-Tests
// ==================================================================
[McpServerToolType] // Attribut: Markiert Klasse als MCP-Tool-Container
public static class CommunicationTools
{
    // Tool 1: Einfaches Echo-Tool zum Testen der Kommunikation
    [McpServerTool, Description("Gibt die empfangene Nachricht mit [ECHO] Präfix zurück. Nützlich zum Testen der Kommunikation.")] // Attribut: Markiert als Tool + Beschreibung
    public static string EchoMessage( // Gibt "[ECHO] {message}" zurück
        [Description("Die Nachricht, die zurückgegeben werden soll")] string message) // Parameter mit Beschreibung
    {
        return $"[ECHO] {message}"; // String-Interpolation: Füge Prefix hinzu
    }

    // Tool 2: Nachricht analysieren (Wortanzahl, Zeichenanzahl)
    [McpServerTool, Description("Analysiert eine Nachricht und gibt Statistiken wie Wortanzahl und Zeichenanzahl zurück.")] // Attribut: Markiert als Tool + Beschreibung
    public static string AnalyzeMessage( // Splittet Text, zählt Wörter und Zeichen (mit/ohne Spaces)
        [Description("Die zu analysierende Nachricht")] string message) // Parameter mit Beschreibung
    {
        if (string.IsNullOrWhiteSpace(message)) // Check: Ist Nachricht null, leer oder nur Whitespace?
        {
            return "Fehler: Nachricht ist leer."; // Ja → Gib Fehler zurück
        }

        var words = message.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries); // Split: Teile bei Whitespace-Zeichen, entferne leere Einträge
        var wordCount = words.Length; // Array-Länge = Anzahl Wörter
        var charCount = message.Length; // String-Länge = Anzahl Zeichen (mit Leerzeichen)
        var charCountNoSpaces = message.Replace(" ", "").Length; // Replace: Entferne alle Spaces → String-Länge = Zeichen ohne Leerzeichen

        return $"Nachrichten-Analyse:\n" + // Multi-line String für formatierte Ausgabe
               $"- Wortanzahl: {wordCount}\n" + // Interpolation: Füge Wortanzahl ein
               $"- Zeichenanzahl (mit Leerzeichen): {charCount}\n" + // Interpolation: Zeichenanzahl mit
               $"- Zeichenanzahl (ohne Leerzeichen): {charCountNoSpaces}"; // Interpolation: Zeichenanzahl ohne
    }
}

// ==================================================================
// DocumentTools - Tools für Dokumenten-Analyse
// ==================================================================
[McpServerToolType] // Attribut: Markiert Klasse als MCP-Tool-Container
public static class DocumentTools // Static: Keine Instanzen, nur statische Methoden
{
    // Tool 3: Datei-Inhalt lesen (für Dokumenten-Analyse)
    [McpServerTool, Description("Liest und gibt den Inhalt einer lokalen Datei zurück. Nützlich für Dokumenten-Analyse.")] // Attribut: Markiert als Tool + Beschreibung
    public static async Task<string> GetFileContent( // Liest Datei asynchron wenn vorhanden, sonst Fehler
        [Description("Vollständiger Pfad zur Datei")] string filePath) // Parameter mit Beschreibung
    {
        if (!File.Exists(filePath)) // Check: Existiert Datei unter diesem Pfad?
        {
            return $"Fehler: Datei nicht gefunden unter {filePath}"; // Nein → Gib Fehler mit Pfad zurück
        }

        try // Try-Block für I/O-Fehlerbehandlung
        {
            string fileContent = await File.ReadAllTextAsync(filePath); // Lese Datei asynchron → speichere in Variable
            return fileContent; // Gib Datei-Inhalt zurück
        }
        catch (Exception ex) // Fange alle I/O-Exceptions (z.B. Zugriffsfehler, Encoding-Probleme)
        {
            return $"Fehler beim Lesen der Datei: {ex.Message}"; // Gib Fehler mit Exception-Message zurück
        }
    }

    // Später: Weitere Tools für Dokumenten-Analyse hinzufügen
    // z.B. ExtractKeywords, SummarizeDocument, FindInDocument, etc.
}