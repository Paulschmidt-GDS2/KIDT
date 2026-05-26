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
    public static void RegisterTools(Kernel kernel, DocumentDbService docDbService, CalendarService calendarService, int currentConversationId) // Hauptmethode: Registriert alle Tools im Kernel
    {
        var assembly = Assembly.GetExecutingAssembly(); // Hole aktuelles Assembly
        var allTypes = assembly.GetTypes(); // Hole alle Typen im Assembly
        var toolTypes = new List<Type>(); // Liste für Tool-Typen
        foreach (var t in allTypes) // Durchlaufe alle Typen
        {
            if (t.GetCustomAttribute<McpServerToolTypeAttribute>() != null) // Hat [McpServerToolType]-Attribut?
            {
                toolTypes.Add(t); // Füge zur Liste hinzu
            }
        }

        foreach (var toolType in toolTypes) // Durchlaufe alle Tool-Klassen (z.B. DocumentTools, CalendarTools)
        {
            try
            {
                object? toolInstance = null;

                // Versuche verschiedene Constructor-Signaturen (DocumentTools, CalendarTools haben unterschiedliche Parameter)
                var docConstructor = toolType.GetConstructor(new[] { typeof(DocumentDbService), typeof(int) }); // Constructor für DocumentTools
                var calendarConstructor = toolType.GetConstructor(new[] { typeof(CalendarService) }); // Constructor für CalendarTools

                if (docConstructor != null) // DocumentTools-Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, docDbService, currentConversationId); // Erstelle DocumentTools-Instanz
                }
                else if (calendarConstructor != null) // CalendarTools-Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, calendarService); // Erstelle CalendarTools-Instanz
                }
                else // Kein passender Constructor?
                {
                    toolInstance = Activator.CreateInstance(toolType); // Fallback: Parameterloser Constructor
                }

                if (toolInstance == null) continue; // Instanz-Erstellung fehlgeschlagen? ? Überspringe

                var allMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance); // Finde alle public Instance-Methoden
                var toolMethods = new List<MethodInfo>();
                foreach (var m in allMethods) // Durchlaufe alle Methoden
                {
                    if (m.GetCustomAttribute<McpServerToolAttribute>() != null) // Hat [McpServerTool]-Attribut?
                    {
                        toolMethods.Add(m); // Füge zur Liste hinzu
                    }
                }

                var functions = new List<KernelFunction>();

                foreach (var method in toolMethods) // Durchlaufe alle Tool-Methoden (z.B. SearchDocuments, AddDocumentToChat)
                {
                    var function = KernelFunctionFactory.CreateFromMethod( // Erstelle Kernel-Funktion aus Methode
                        method,
                        toolInstance,
                        functionName: ConvertToPythonCase(method.Name) // Konvertiere Name zu snake_case (SearchDocuments ? search_documents)
                    );

                    functions.Add(function);
                }

                if (functions.Count > 0) // Mindestens eine Funktion gefunden?
                {
                    kernel.ImportPluginFromFunctions( // Registriere Plugin im Kernel
                        toolType.Name.Replace("Tools", ""), // Plugin-Name ohne "Tools"-Suffix (DocumentTools ? Document)
                        functions // Liste der Kernel-Funktionen
                    );
                }
            }
            catch (Exception ex)
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