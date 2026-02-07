using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Services;

/// <summary>
/// /// RouterService: Analysiert User-Nachrichten und entscheidet, ob sie an Conversation-Agent, DataAnalysis-Agent oder Dokumenten-Suche weitergeleitet werden.
/// </summary>

public class RouterService 
{
    private readonly IServiceProvider serviceProvider; // Service Provider für Dependency Injection (DocDbService)
    private string? apiKey; // Azure OpenAI API Key (wird bei InitializeAsync geladen)

    public RouterService(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider; // Service Provider speichern für spätere Scope-Erstellung
    }

    public async Task InitializeAsync() // Lädt API-Key aus Datei (wird beim ersten ProcessAsync aufgerufen)
    {
        if (this.apiKey != null) return; // Bereits initialisiert? ? Abbruch

        string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openai-api-key.txt");
        this.apiKey = await File.ReadAllTextAsync(keyPath);
    }

    public async Task<RouterResponse> ProcessAsync(string userMessage, bool hasFile, int conversationId) // Hauptmethode: Analysiert User-Nachricht und routet zu passendem Service ODER handhabt Dokument-Suche
    {
        if (this.apiKey == null) // API-Key noch nicht geladen?
        {
            await InitializeAsync(); // Initialisiere API-Key
        }

        using var scope = this.serviceProvider.CreateScope(); // Erstelle neuen Service-Scope für DB-Zugriff
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Hole DocumentDbService für Dokument-Operationen

        try
        {
            // SCHRITT 1: Erstelle Kernel mit MCP-Tools für Function Calling
            var builder = Kernel.CreateBuilder();
            builder.Services.AddAzureOpenAIChatCompletion( // Azure OpenAI hinzufügen
                deploymentName: "gpt-4.1-deployment", 
                endpoint: "https://ts-openai-testing.openai.azure.com/",
                apiKey: this.apiKey!.Trim()
            );

            var kernel = builder.Build(); // Kernel erstellen
            
            McpToolsRegistry.RegisterTools(kernel, docDbService, conversationId); // Registriere MCP-Tools (search_documents, add_document_to_chat)
            
            var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service für LLM-Anfragen
            var chatHistory = new ChatHistory(); // Chat-History erstellen (System + User)
            
            string systemPrompt =
                "Du bist ein Router-Agent mit Dokumenten-Tools.\n\n" +
                "KRITISCH WICHTIG: Du hast KEINE Informationen über Dokumente!\n" +
                "Du MUSST die Tools nutzen wenn User nach Dokumenten fragt!\n\n" +
                "Verfügbare Tools:\n" +
                "- search_documents(query): Sucht Dokumente in Datenbank\n" +
                "- add_document_to_chat(id): Fügt Dokument zum Chat hinzu\n\n" +
                "REGEL: User fragt nach Dokumenten?\n" +
                "? Du MUSST search_documents() aufrufen (du weißt es NICHT!)\n" +
                "? FALSCH: \"Es sind keine Dokumente...\" ?\n" +
                "? RICHTIG: Rufe search_documents() auf! ?\n\n" +
                "Wenn User KEINE Dokumenten-Frage stellt:\n" +
                "? Antworte mit JSON: {\"needsRouting\": true, \"intent\": \"conversation\" oder \"dataAnalysis\"}\n\n" +
                "Beispiele:\n" +
                "User: 'Hast du Dokumente über Python?' ? search_documents('Python')\n" +
                "User: 'Hallo' ? {\"needsRouting\": true, \"intent\": \"conversation\"}\n" +
                "User: 'Analysiere X' ? {\"needsRouting\": true, \"intent\": \"dataAnalysis\"}";

            chatHistory.AddSystemMessage(systemPrompt); // Füge System-Prompt hinzu
            chatHistory.AddUserMessage(userMessage); // Füge User-Nachricht hinzu

            var executionSettings = new OpenAIPromptExecutionSettings // Execution-Settings für LLM-Anfrage
            {
                ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions, // Enable Function Calling (manuelles Handling!)
                Temperature = 0.1, // Niedrige Temperature für deterministisches Verhalten
                MaxTokens = 1000 // Maximale Antwort-Länge
            };
            
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Sende Anfrage mit Function Calling...");

            var response = await chatService.GetChatMessageContentAsync( // Sende Anfrage an LLM
                chatHistory, // Chat-History (System + User)
                executionSettings: executionSettings, // Execution-Settings
                kernel: kernel // Kernel mit registrierten Tools
            );

            string responseText = response.Content ?? string.Empty; // Response-Text (oder leer wenn null)
            
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Response: {responseText}");
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Response Items: {response.Items?.Count ?? 0}");
            
            // SCHRITT 2: Prüfe ob LLM Tool-Calls ausführen möchte
            List<Document> foundDocuments = new List<Document>(); // Liste für gefundene Dokumente
            bool hasToolCalls = false; // Flag: Wurde Tool aufgerufen?
            
            
            if (response.Items != null) // LLM hat Items zurückgegeben?
            {
                foreach (var item in response.Items) // Durchlaufe alle Items
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Item-Typ: {item.GetType().Name}");
                    
                    if (item is Microsoft.SemanticKernel.FunctionCallContent functionCall) // Ist Item ein Function-Call?
                    {
                        hasToolCalls = true; // Flag: Tool wurde aufgerufen
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] ? Tool-Call erkannt: {functionCall.FunctionName}");
                        
                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Plugin: {functionCall.PluginName}, Function: {functionCall.FunctionName}");
                            
                            var function = kernel.Plugins.GetFunction(functionCall.PluginName, functionCall.FunctionName); // Hole registrierte Function
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Function gefunden, führe aus...");
                            
                            var result = await function.InvokeAsync(kernel, functionCall.Arguments); // Führe Tool manuell aus mit Arguments
                            
                            var resultText = result.ToString(); // Tool-Result zu String konvertieren
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool Result: {resultText}");
                            
                            
                            // search_documents Tool wurde aufgerufen
                            if (functionCall.FunctionName == "search_documents") // War Tool search_documents?
                            {
                                System.Diagnostics.Debug.WriteLine($"[ROUTER] Parsing JSON...");
                                var toolResult = JsonSerializer.Deserialize<SearchResultJson>(resultText); // Parse JSON-Result
                                System.Diagnostics.Debug.WriteLine($"[ROUTER] JSON geparsed: found={toolResult?.found}, documentIds={toolResult?.documentIds?.Count}");
                                
                                if (toolResult != null && toolResult.documentIds != null) // Erfolgreiche Suche?
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Lade {toolResult.documentIds.Count} Dokumente...");
                                    
                                    foreach (var docId in toolResult.documentIds) // Durchlaufe gefundene Dokument-IDs
                                    {
                                        var doc = await docDbService.GetDocumentByIdAsync(docId); // Lade volles Dokument aus DB
                                        if (doc != null) foundDocuments.Add(doc); // Füge zu Liste hinzu
                                    }
                                    
                                    string message = toolResult.found > 0 // Dokumente gefunden?
                                        ? $"{toolResult.found} Dokument(e) gefunden: {string.Join(", ", foundDocuments.Select(d => d.FileName))}" // Ja: Liste Dateinamen
                                        : "Keine Dokumente gefunden."; // Nein: Keine Dokumente
                                    
                                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Returning: {message}");
                                    
                                    return new RouterResponse // Gib Dokument-Suche-Response zurück
                                    {
                                        ShouldRoute = false, // Kein Routing nötig (direkt beantwortet)
                                        DirectResponse = message, // Nachricht an User
                                        FoundDocuments = foundDocuments, // Gefundene Dokumente
                                        TargetService = string.Empty, // Kein Target-Service
                                        MaxTokens = 0, // Keine Token-Limit
                                        Reason = "Dokumentensuche durchgeführt (Function Calling)", // Grund
                                        ToolWasUsed = true // Tool wurde verwendet
                                    };
                                }
                            }
                            
                            
                            // add_document_to_chat Tool wurde aufgerufen
                            if (functionCall.FunctionName == "add_document_to_chat") // War Tool add_document_to_chat?
                            {
                                var addResult = JsonSerializer.Deserialize<AddDocumentResultJson>(resultText); // Parse JSON-Result
                                if (addResult != null && addResult.success) // Erfolgreich hinzugefügt?
                                {
                                    var doc = await docDbService.GetDocumentByIdAsync(addResult.documentId); // Lade volles Dokument aus DB
                                    if (doc != null) foundDocuments.Add(doc); // Füge zu Liste hinzu
                                    
                                    return new RouterResponse // Gib Add-Document-Response zurück
                                    {
                                        ShouldRoute = false, // Kein Routing nötig (direkt beantwortet)
                                        DirectResponse = $"Dokument '{addResult.fileName}' wurde hinzugefügt.", // Bestätigungs-Nachricht
                                        FoundDocuments = foundDocuments, // Hinzugefügtes Dokument
                                        TargetService = string.Empty, // Kein Target-Service
                                        MaxTokens = 0, // Keine Token-Limit
                                        Reason = "Dokument hinzugefügt (Function Calling)", // Grund
                                        ToolWasUsed = true // Tool wurde verwendet
                                    };
                                }
                            }
                        }
                        catch (Exception ex) // Tool-Execution fehlgeschlagen?
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Execution-Fehler: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Stack: {ex.StackTrace}");
                        }
                    }
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tools aufgerufen: {hasToolCalls}");
            
            // SCHRITT 3: Keine Tools aufgerufen ? Fallback auf JSON-basiertes Intent-Routing
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Keine Tools genutzt, prüfe Intent-JSON...");
            
            if (responseText.TrimStart().StartsWith("{")) // Response ist JSON?
            {
                try
                {
                    var routingJson = JsonSerializer.Deserialize<RoutingResponseJson>(responseText, // Parse JSON-Response
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); // Case-insensitive
                    
                    if (routingJson?.needsRouting == true) // Routing nötig?
                    {
                        string targetService = routingJson.intent == "dataAnalysis" ? "dataAnalysis" : "conversation"; // Bestimme Target-Service
                        int maxTokens = targetService == "dataAnalysis" ? 2000 : 300; // Token-Limit je nach Service
                        
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Routing zu: {targetService}");
                        
                        return new RouterResponse // Gib Routing-Response zurück
                        {
                            ShouldRoute = true, // Routing nötig
                            DirectResponse = null, // Keine direkte Antwort
                            TargetService = targetService, // Ziel-Service
                            MaxTokens = maxTokens, // Token-Limit
                            FoundDocuments = new List<Document>(), // Keine Dokumente
                            Reason = $"Intent-Routing: {routingJson.intent}", // Grund
                            ToolWasUsed = false // Keine Tools verwendet
                        };
                    }
                }
                catch (Exception ex) // JSON-Parsing fehlgeschlagen?
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] JSON-Parse-Fehler: {ex.Message}");
                }
            }
            
            
            // SCHRITT 4: Letzter Fallback ? Intent-Klassifizierung per GPT-4
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Fallback: Intent-Klassifizierung...");
            var fallbackIntent = await ClassifyIntentAsync(userMessage); // Klassifiziere Intent per GPT-4
            
            if (fallbackIntent.Intent == "document_search") // Intent ist Dokumenten-Suche?
            {
                System.Diagnostics.Debug.WriteLine($"[ROUTER] Fallback-Suche: '{fallbackIntent.SearchQuery}'");
                
                var documents = await docDbService.SearchDocumentsAsync(fallbackIntent.SearchQuery ?? string.Empty); // Suche in DB
                
                string message = documents.Count > 0 // Dokumente gefunden?
                    ? $"{documents.Count} Dokument(e) gefunden: {string.Join(", ", documents.Select(d => d.FileName))}" // Ja: Liste Dateinamen
                    : "Keine Dokumente gefunden."; // Nein: Keine Dokumente
                
                return new RouterResponse // Gib Dokument-Suche-Response zurück (Fallback)
                {
                    ShouldRoute = false, // Kein Routing nötig
                    DirectResponse = message, // Nachricht an User
                    FoundDocuments = documents, // Gefundene Dokumente
                    TargetService = string.Empty, // Kein Target-Service
                    MaxTokens = 0, // Keine Token-Limit
                    Reason = "Dokumentensuche durchgeführt (Fallback)", // Grund
                    ToolWasUsed = true // Tool-ähnlich (DB-Suche)
                };
            }
            
            // Normales Intent-Routing (Conversation oder DataAnalysis)
            string finalTargetService = fallbackIntent.Intent == "dataAnalysis" ? "dataAnalysis" : "conversation"; // Bestimme Service
            int finalMaxTokens = finalTargetService == "dataAnalysis" ? 2000 : 300; // Token-Limit
            
            return new RouterResponse // Gib Routing-Response zurück
            {
                ShouldRoute = true, // Routing nötig
                DirectResponse = null, // Keine direkte Antwort
                TargetService = finalTargetService, // Ziel-Service
                MaxTokens = finalMaxTokens, // Token-Limit
                FoundDocuments = new List<Document>(), // Keine Dokumente
                Reason = $"Intent-Routing (Fallback): {fallbackIntent.Intent}", // Grund
                ToolWasUsed = false // Keine Tools
            };
        }
        catch (Exception ex) // Router-Fehler?
        {
            throw new Exception($"Router-Fehler: {ex.Message}"); // Werfe Exception weiter
        }
    }

    private async Task<IntentResult> ClassifyIntentAsync(string userMessage) // Hilfsmethode: Klassifiziert Intent per GPT-4 (Fallback wenn Function Calling versagt)
    {
        var builder = Kernel.CreateBuilder(); // Erstelle neuen Kernel
        builder.Services.AddAzureOpenAIChatCompletion( // F\u00fcge Azure OpenAI hinzu
            deploymentName: "gpt-4.1-deployment", // Deployment-Name
            endpoint: "https://ts-openai-testing.openai.azure.com/", // Azure-Endpoint
            apiKey: this.apiKey!.Trim() // API-Key (! = garantiert nicht null)
        );
        
        var kernel = builder.Build(); // Kernel erstellen
        var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service
        
        var chatHistory = new ChatHistory(); // Chat-History erstellen
        string systemPrompt =  // System-Prompt: Instruiert Intent-Klassifizierer
            "Du bist ein Intent-Klassifizierer. Analysiere die User-Nachricht und gib NUR JSON zur\u00fcck:\n\n" +
            "Intents:\n" +
            "- 'document_search': User fragt nach Dokumenten (z.B. 'Hast du Dokumente \u00fcber X?')\n" +
            "  \u2192 Extrahiere searchQuery (Suchbegriff)\n" +
            "- 'document_add': User will Dokument hinzuf\u00fcgen (z.B. 'F\u00fcge Dokument 5 hinzu')\n" +
            "  \u2192 Extrahiere documentId (Zahl)\n" +
            "- 'conversation': Small Talk, einfache Fragen\n" +
            "- 'dataAnalysis': Analyse-Anfragen\n\n" +
            "Beispiele:\n" +
            "{\"intent\": \"document_search\", \"searchQuery\": \"Python\"}\n" +
            "{\"intent\": \"document_add\", \"documentId\": 5}\n" +
            "{\"intent\": \"conversation\"}";
        
        chatHistory.AddSystemMessage(systemPrompt); // F\u00fcge System-Prompt hinzu
        chatHistory.AddUserMessage(userMessage); // F\u00fcge User-Nachricht hinzu
        
        var settings = new OpenAIPromptExecutionSettings // Execution-Settings
        {
            Temperature = 0.1, // Niedrige Temperature f\u00fcr deterministisches Verhalten
            MaxTokens = 200 // Maximale Antwort-L\u00e4nge
        };
        
        var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel); // Sende Anfrage an LLM
        string jsonResponse = response.Content ?? "{}"; // Response-Text (oder leeres JSON wenn null)
        
        System.Diagnostics.Debug.WriteLine($"[ROUTER] Intent-Response: {jsonResponse}");
        
        var intent = JsonSerializer.Deserialize<IntentJson>(jsonResponse, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); // Parse JSON
        
        return new IntentResult // Gib Intent-Result zur\u00fcck
        {
            Intent = intent?.intent ?? "conversation", // Intent (oder "conversation" wenn null)
            SearchQuery = intent?.searchQuery, // Such-Query (oder null)
            DocumentId = intent?.documentId ?? 0 // Dokument-ID (oder 0 wenn null)
        };
    }

    // === HELPER-KLASSEN FÜR JSON-SERIALISIERUNG ===

    private class RoutingResponseJson // JSON-Klasse: Intent-basiertes Routing-Response
    {
        public bool needsRouting { get; set; } // Routing nötig?
        public string? intent { get; set; } // Intent (z.B. "conversation", "dataAnalysis")
    }

    private class SearchResultJson // JSON-Klasse: Ergebnis von search_documents Tool
    {
        public int found { get; set; } // Anzahl gefundener Dokumente
        public List<int>? documentIds { get; set; } // Liste der Dokument-IDs
        public string? message { get; set; } // Optionale Nachricht
        public List<DocumentInfo>? documents { get; set; } // Optionale Liste mit Dokument-Infos
    }
    
    private class DocumentInfo // JSON-Klasse: Dokument-Info im search_documents Result
    {
        public int id { get; set; } // Dokument-ID
        public string? fileName { get; set; } // Dateiname
        public string? fileType { get; set; } // Dateityp (z.B. "pdf")
        public string? uploadedAt { get; set; } // Upload-Datum
        public bool hasThumbnail { get; set; } // Hat Thumbnail?
    }

    private class AddDocumentResultJson // JSON-Klasse: Ergebnis von add_document_to_chat Tool
    {
        public bool success { get; set; } // Erfolgreich hinzugefügt?
        public int documentId { get; set; } // Dokument-ID
        public string? fileName { get; set; } // Dateiname
        public string? message { get; set; } // Optionale Nachricht
    }

    private class IntentJson // JSON-Klasse: Intent-Klassifizierung per GPT-4
    {
        public string? intent { get; set; } // Intent (z.B. "document_search", "conversation")
        public string? searchQuery { get; set; } // Such-Query (bei document_search)
        public int? documentId { get; set; } // Dokument-ID (bei document_add)
    }

    private class IntentResult // Klasse: Ergebnis von ClassifyIntentAsync
    {
        public string Intent { get; set; } = "conversation"; // Intent (Standard: "conversation")
        public string? SearchQuery { get; set; } // Such-Query (oder null)
        public int DocumentId { get; set; } // Dokument-ID (Standard: 0)
    }
}

public class RouterResponse // Klasse: Response des RouterService (wird an ChatCoordinator zurückgegeben)
{
    public bool ShouldRoute { get; set; } // Soll zu anderem Service geroutet werden?
    public string? DirectResponse { get; set; } // Direkte Antwort an User (wenn kein Routing)
    public string TargetService { get; set; } = string.Empty; // Ziel-Service ("conversation" oder "dataAnalysis")
    public int MaxTokens { get; set; } // Token-Limit für Ziel-Service
    public List<Document> FoundDocuments { get; set; } = new List<Document>(); // Gefundene Dokumente (bei Dokument-Suche)
    public string Reason { get; set; } = string.Empty; // Grund für Routing/Direktantwort
    public bool ToolWasUsed { get; set; } // Wurde Tool verwendet?
}
