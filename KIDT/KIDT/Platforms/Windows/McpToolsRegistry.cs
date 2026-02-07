using Microsoft.SemanticKernel;
using System.Reflection;
using KIDT.Database;
using ModelContextProtocol.Server;

namespace KIDT.Services;

/// <summary>
/// Helper-Klasse zum Registrieren von MCP-Tools in Semantic Kernel.
/// Lädt automatisch alle Klassen mit [McpServerToolType] Attribut und registriert deren Methoden als Kernel-Funktionen.
/// Wird vom RouterService aufgerufen um Document-Tools zu registrieren (search_documents, add_document_to_chat).
/// </summary>
public static class McpToolsRegistry // Static Helper-Klasse: Registriert MCP-Tools automatisch per Reflection
{
    /// <summary>
    /// Registriert alle MCP-Tools als Kernel-Funktionen (wird von RouterService.ProcessAsync aufgerufen).
    /// Sucht automatisch nach Klassen mit [McpServerToolType] Attribut und registriert deren [McpServerTool]-Methoden.
    /// </summary>
    public static void RegisterTools(Kernel kernel, DocumentDbService docDbService, int currentConversationId) // Hauptmethode: Registriert alle Tools im Kernel
    {
        var assembly = Assembly.GetExecutingAssembly(); // Hole aktuelles Assembly
        var toolTypes = assembly.GetTypes() // Finde alle Typen im Assembly
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null); // Filtere nach [McpServerToolType]-Attribut

        foreach (var toolType in toolTypes) // Durchlaufe alle Tool-Klassen (z.B. DocumentTools)
        {
            try
            {
                object? toolInstance = null; // Instanz der Tool-Klasse
                
                var constructor = toolType.GetConstructor(new[] { typeof(DocumentDbService), typeof(int) }); // Suche Constructor mit (DocumentDbService, int)
                if (constructor != null) // Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, docDbService, currentConversationId); // Erstelle Instanz mit DI
                }
                else // Kein passender Constructor?
                {
                    toolInstance = Activator.CreateInstance(toolType); // Fallback: Parameterloser Constructor
                }

                if (toolInstance == null) continue; // Instanz-Erstellung fehlgeschlagen? ? Überspringe

                var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance) // Finde alle public Instance-Methoden
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null); // Filtere nach [McpServerTool]-Attribut

                var functions = new List<KernelFunction>(); // Liste für Kernel-Funktionen
                
                foreach (var method in toolMethods) // Durchlaufe alle Tool-Methoden (z.B. SearchDocuments, AddDocumentToChat)
                {
                    var function = KernelFunctionFactory.CreateFromMethod( // Erstelle Kernel-Funktion aus Methode
                        method, // Methode (z.B. SearchDocuments)
                        toolInstance, // Instanz der Tool-Klasse
                        functionName: ConvertToPythonCase(method.Name) // Konvertiere Name zu snake_case (SearchDocuments ? search_documents)
                    );
                    
                    functions.Add(function); // Füge zu Liste hinzu
                }

                if (functions.Count > 0) // Mindestens eine Funktion gefunden?
                {
                    kernel.ImportPluginFromFunctions( // Registriere Plugin im Kernel
                        toolType.Name.Replace("Tools", ""), // Plugin-Name ohne "Tools"-Suffix (DocumentTools ? Document)
                        functions // Liste der Kernel-Funktionen
                    );
                }
            }
            catch (Exception ex) // Registrierung fehlgeschlagen?
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Registrieren von {toolType.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Konvertiert PascalCase zu snake_case (z.B. SearchDocuments ? search_documents).
    /// Wird verwendet um C#-Methoden-Namen zu Python-Style-Function-Namen zu konvertieren.
    /// </summary>
    private static string ConvertToPythonCase(string name) // Hilfsmethode: PascalCase ? snake_case
    {
        if (string.IsNullOrEmpty(name)) return name; // Leer? ? Gib zurück
        
        var result = new System.Text.StringBuilder(); // StringBuilder für Ergebnis
        result.Append(char.ToLower(name[0])); // Erstes Zeichen lowercase
        
        for (int i = 1; i < name.Length; i++) // Durchlaufe restliche Zeichen
        {
            if (char.IsUpper(name[i])) // Uppercase-Zeichen?
            {
                result.Append('_'); // Füge Underscore hinzu
                result.Append(char.ToLower(name[i])); // Füge Zeichen lowercase hinzu
            }
            else
            {
                result.Append(name[i]); // Füge Zeichen unverändert hinzu
            }
        }
        
        return result.ToString(); // Gib snake_case-String zurück
    }
}
