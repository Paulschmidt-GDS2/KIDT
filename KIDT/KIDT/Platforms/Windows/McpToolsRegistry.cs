using Microsoft.SemanticKernel;
using System.Reflection;
using KIDT.Database;
using KIDT.Services.Logic;
using ModelContextProtocol.Server;

namespace KIDT.Services;

public static class McpToolsRegistry // Static Helper-Klasse: Registriert MCP-Tools automatisch per Reflection
{
    public static void RegisterTools(Kernel kernel, DocumentDbService docDbService, CalendarService calendarService, FolderDbService folderDbService, int currentConversationId) // Hauptmethode: Registriert alle Tools im Kernel
    {
        var assembly = Assembly.GetExecutingAssembly(); // Hole aktuelles Assembly
        var allTypes = assembly.GetTypes(); // Hole alle Typen im Assembly
        var toolTypes = new List<Type>();
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
                var folderConstructor = toolType.GetConstructor(new[] { typeof(FolderDbService) }); // Constructor für FolderTools

                if (docConstructor != null) // DocumentTools-Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, docDbService, currentConversationId); // Erstelle DocumentTools-Instanz
                }
                else if (calendarConstructor != null) // CalendarTools-Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, calendarService); // Erstelle CalendarTools-Instanz
                }
                else if (folderConstructor != null) // FolderTools-Constructor gefunden?
                {
                    toolInstance = Activator.CreateInstance(toolType, folderDbService); // Erstelle FolderTools-Instanz
                }
                else // Kein passender Constructor?
                {
                    toolInstance = Activator.CreateInstance(toolType); // Fallback: Parameterloser Constructor
                }

                if (toolInstance == null) continue; // Instanz-Erstellung fehlgeschlagen → Überspringe

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
                        functionName: ToolNameConverter.ToSnakeCase(method.Name) // Konvertiere Name zu snake_case (SearchDocuments → search_documents)
                    );

                    functions.Add(function);
                }

                if (functions.Count > 0) // Mindestens eine Funktion gefunden?
                {
                    kernel.ImportPluginFromFunctions( // Registriere Plugin im Kernel
                        toolType.Name.Replace("Tools", ""), // Plugin-Name ohne "Tools"-Suffix (DocumentTools → Document)
                        functions
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Registrieren von {toolType.Name}: {ex.Message}");
            }
        }
    }

}
