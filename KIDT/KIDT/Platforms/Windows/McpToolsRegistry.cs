using Microsoft.SemanticKernel;

namespace KIDT.Services;

/// <summary>
/// Helper-Klasse zum Registrieren von MCP-Tools in Semantic Kernel.
/// Hier können später Tools für Daten-Analyse, Kalender-Integration, etc. hinzugefügt werden.
/// </summary>
public static class McpToolsRegistry
{
    /// <summary>
    /// Registriert alle MCP-Tools als Kernel-Funktionen.
    /// Aktuell leer - bereit für neue Tools.
    /// </summary>
    public static void RegisterTools(Kernel kernel) // Registriert Tools im Kernel
    {
        // ==================================================================
        // Hier später neue Tools hinzufügen
        // ==================================================================
        
        // Beispiel für zukünftige Tools:
        // - DatenAnalyseTool: Analysiert strukturierte Daten (CSV, Excel, etc.)
        // - KalenderTool: Extrahiert Termine aus Text und fügt sie in Kalender ein
        // - DokumentVergleichTool: Vergleicht zwei Dokumente auf Unterschiede
        // - ZusammenfassungsTool: Erstellt Zusammenfassungen von langen Texten
        
        // Aktuell keine Tools registriert
        // Tools können hier mit KernelFunctionFactory.CreateFromMethod() hinzugefügt werden
    }
}