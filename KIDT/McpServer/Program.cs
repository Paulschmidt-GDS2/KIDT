using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

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
    .WithToolsFromAssembly(); // Lade alle Tools automatisch aus diesem Assembly

await builder.Build().RunAsync(); // Baue Host → Starte Server → Warte auf Beendigung (async)

// ==================================================================
// MCP Tools - Hier später neue Tools definieren
// ==================================================================

// Beispiel-Struktur für zukünftige Tools:
//
// [McpServerToolType]
// public static class DatenAnalyseTools
// {
//     [McpServerTool, Description("Analysiert strukturierte Daten aus CSV/Excel")]
//     public static string AnalysiereStrukturDaten(
//         [Description("Pfad zur Datendatei")] string filePath)
//     {
//         // Tool-Logik hier
//         return "Analyse-Ergebnis";
//     }
// }
//
// [McpServerToolType]
// public static class KalenderTools
// {
//     [McpServerTool, Description("Extrahiert Termine aus Text und fügt sie in Kalender ein")]
//     public static async Task<string> TerminExtrahieren(
//         [Description("Text mit Termininformationen")] string text)
//     {
//         // Tool-Logik hier
//         return "Termin erfolgreich hinzugefügt";
//     }
// }

// Aktuell keine Tools definiert - bereit für neue Tools