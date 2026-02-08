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

    public async Task InitializeAsync() // Lade API-Key aus Datei
    {
        if (this.apiKey != null) // Bereits initialisiert?
        {
            return;
        }

        string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openai-api-key.txt"); // Erstelle Pfad zur Key-Datei
        this.apiKey = await File.ReadAllTextAsync(keyPath); // Lade Key aus Datei
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
                "- add_document_to_chat(documentId): Fügt Dokument zum Chat hinzu\n\n" +
                "REGELN:\n" +
                "1. User fragt nach Dokumenten? ? search_documents() aufrufen!\n" +
                "2. User will Dokument hinzufügen? ? add_document_to_chat(id) aufrufen!\n" +
                "   Beispiele: 'füge das hinzu', 'nimm Dokument 5', 'add die zum Chat'\n" +
                "3. Normale Frage? ? Antworte mit JSON: {\"needsRouting\": true, \"intent\": \"...\"}\n\n" +
                "Beispiele:\n" +
                "User: 'Hast du Dokumente über Python?' ? search_documents('Python')\n" +
                "User: 'Füge Dokument 5 zum Chat hinzu' ? add_document_to_chat(5)\n" +
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

            string responseText = string.Empty; // Variable für Response-Text
            if (response.Content != null) // Response hat Content?
            {
                responseText = response.Content; // Setze Response-Text
            }
            
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Response: {responseText}");
            
            int itemCount = 0; // Variable für Item-Count
            if (response.Items != null) // Response hat Items?
            {
                itemCount = response.Items.Count; // Setze Item-Count
            }
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Response Items: {itemCount}");
            
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
                                    
                                    string message; // Variable für Nachricht
                                    if (toolResult.found > 0) // Dokumente gefunden?
                                    {
                                        List<string> fileNames = new List<string>(); // Erstelle Liste für Dateinamen
                                        foreach (Document d in foundDocuments) // Gehe durch alle gefundenen Dokumente
                                        {
                                            fileNames.Add(d.FileName); // Füge Dateiname zur Liste hinzu
                                        }
                                        string fileNamesList = string.Join(", ", fileNames); // Verbinde Dateinamen mit Komma
                                        message = $"{toolResult.found} Dokument(e) gefunden: {fileNamesList}";
                                    }
                                    else // Keine Dokumente gefunden
                                    {
                                        message = "Keine Dokumente gefunden.";
                                    }
                                    
                                    
                                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Returning: {message}");
                                    
                                    RouterResponse searchResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    searchResponse.ShouldRoute = false; // Kein Routing nötig
                                    searchResponse.DirectResponse = message;
                                    searchResponse.FoundDocuments = foundDocuments;
                                    searchResponse.TargetService = string.Empty; // Kein Target-Service
                                    searchResponse.MaxTokens = 0; // Keine Token-Limit
                                    searchResponse.Reason = "Dokumentensuche durchgeführt (Function Calling)";
                                    searchResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return searchResponse;
                                }
                            }
                            
                            
                            // add_document_to_chat Tool wurde aufgerufen
                            if (functionCall.FunctionName == "add_document_to_chat") // War Tool add_document_to_chat?
                            {
                                var addResult = JsonSerializer.Deserialize<AddDocumentResultJson>(resultText); // Parse JSON-Result
                                if (addResult != null && addResult.success) // Erfolgreich hinzugefügt?
                                {
                                    var doc = await docDbService.GetDocumentByIdAsync(addResult.documentId); // Lade Dokument
                                    if (doc != null) // Dokument gefunden?
                                    {
                                        foundDocuments.Add(doc);
                                    }
                                    
                                    RouterResponse addSuccessResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    addSuccessResponse.ShouldRoute = false; // Kein Routing nötig
                                    addSuccessResponse.DirectResponse = $"? Dokument '{addResult.fileName}' wurde zum Chat hinzugefügt.";
                                    addSuccessResponse.FoundDocuments = foundDocuments;
                                    addSuccessResponse.TargetService = string.Empty; // Kein Target-Service
                                    addSuccessResponse.MaxTokens = 0; // Keine Token-Limit
                                    addSuccessResponse.Reason = "Dokument hinzugefügt (Function Calling)";
                                    addSuccessResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return addSuccessResponse;
                                }
                                else // Fehler beim Hinzufügen
                                {
                                    string errorMessage = "Dokument konnte nicht hinzugefügt werden.";
                                    if (addResult != null && addResult.message != null) // Fehlermeldung vorhanden?
                                    {
                                        errorMessage = addResult.message; // Verwende spezifische Fehlermeldung
                                    }
                                    
                                    RouterResponse addErrorResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    addErrorResponse.ShouldRoute = false; // Kein Routing nötig
                                    addErrorResponse.DirectResponse = errorMessage;
                                    addErrorResponse.FoundDocuments = new List<Document>(); // Leere Dokument-Liste
                                    addErrorResponse.TargetService = string.Empty; // Kein Target-Service
                                    addErrorResponse.MaxTokens = 0; // Keine Token-Limit
                                    addErrorResponse.Reason = "Dokument bereits verknüpft";
                                    addErrorResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return addErrorResponse;
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
                        string targetService = "conversation"; // Standardmäßig Conversation
                        if (routingJson.intent == "dataAnalysis") // Intent ist DataAnalysis?
                        {
                            targetService = "dataAnalysis";
                        }
                        
                        int maxTokens = 300; // Standard Token-Limit
                        if (targetService == "dataAnalysis") // DataAnalysis gewählt?
                        {
                            maxTokens = 2000; // Setze höheres Token-Limit
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Routing zu: {targetService}");
                        
                        RouterResponse intentResponse = new RouterResponse(); // Erstelle neue RouterResponse
                        intentResponse.ShouldRoute = true; // Routing nötig
                        intentResponse.DirectResponse = null;
                        intentResponse.TargetService = targetService;
                        intentResponse.MaxTokens = maxTokens;
                        intentResponse.FoundDocuments = new List<Document>(); // Leere Dokument-Liste
                        intentResponse.Reason = $"Intent-Routing: {routingJson.intent}";
                        intentResponse.ToolWasUsed = false; // Keine Tools verwendet
                        return intentResponse;
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
                
                string searchQuery = string.Empty; // Variable für Such-Query
                if (fallbackIntent.SearchQuery != null) // Such-Query vorhanden?
                {
                    searchQuery = fallbackIntent.SearchQuery; // Setze Such-Query
                }
                
                var documents = await docDbService.SearchDocumentsAsync(searchQuery); // Suche in Datenbank
                
                string message; // Variable für Nachricht
                if (documents.Count > 0) // Dokumente gefunden?
                {
                    List<string> fileNames = new List<string>(); // Erstelle Liste für Dateinamen
                    foreach (Document d in documents) // Gehe durch alle gefundenen Dokumente
                    {
                        fileNames.Add(d.FileName);
                    }
                    string fileNamesList = string.Join(", ", fileNames); // Verbinde Dateinamen mit Komma
                    message = $"{documents.Count} Dokument(e) gefunden: {fileNamesList}";
                }
                else // Keine Dokumente gefunden
                {
                    message = "Keine Dokumente gefunden.";
                }
                
                RouterResponse fallbackSearchResponse = new RouterResponse(); // Erstelle neue RouterResponse
                fallbackSearchResponse.ShouldRoute = false; // Kein Routing nötig
                fallbackSearchResponse.DirectResponse = message;
                fallbackSearchResponse.FoundDocuments = documents;
                fallbackSearchResponse.TargetService = string.Empty; // Kein Target-Service
                fallbackSearchResponse.MaxTokens = 0; // Keine Token-Limit
                fallbackSearchResponse.Reason = "Dokumentensuche durchgeführt (Fallback)";
                fallbackSearchResponse.ToolWasUsed = true; // Tool-ähnlich (DB-Suche)
                return fallbackSearchResponse;
            }
            
            // Normales Intent-Routing (Conversation oder DataAnalysis)
            string finalTargetService = "conversation"; // Standardmäßig Conversation
            if (fallbackIntent.Intent == "dataAnalysis") // Intent ist DataAnalysis?
            {
                finalTargetService = "dataAnalysis";
            }
            
            int finalMaxTokens = 300; // Standard Token-Limit
            if (finalTargetService == "dataAnalysis") // DataAnalysis gewählt?
            {
                finalMaxTokens = 2000; // Setze höheres Token-Limit
            }
            
            RouterResponse finalResponse = new RouterResponse(); // Erstelle neue RouterResponse
            finalResponse.ShouldRoute = true; // Routing nötig
            finalResponse.DirectResponse = null; 
            finalResponse.TargetService = finalTargetService; 
            finalResponse.MaxTokens = finalMaxTokens; 
            finalResponse.FoundDocuments = new List<Document>(); // Leere Dokument-Liste
            finalResponse.Reason = $"Intent-Routing (Fallback): {fallbackIntent.Intent}";
            finalResponse.ToolWasUsed = false; // Keine Tools
            return finalResponse;
        }
        catch (Exception ex)
        {
            throw new Exception($"Router-Fehler: {ex.Message}");
        }
    }

    private async Task<IntentResult> ClassifyIntentAsync(string userMessage) // Hilfsmethode: Klassifiziert Intent per GPT-4 (Fallback wenn Function Calling versagt)
    {
        var builder = Kernel.CreateBuilder(); // Erstelle neuen Kernel
        builder.Services.AddAzureOpenAIChatCompletion( //Füge Azure OpenAI hinzu
            deploymentName: "gpt-4.1-deployment",
            endpoint: "https://ts-openai-testing.openai.azure.com/",
            apiKey: this.apiKey!.Trim()
        );
        
        var kernel = builder.Build(); // Kernel erstellen
        var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service
        
        var chatHistory = new ChatHistory();
        string systemPrompt = 
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
            Temperature = 0.1, // Niedrige Temperature für deterministisches Verhalten
            MaxTokens = 200 // Maximale Antwort-Länge
        };
        
        var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel); // Sende Anfrage an LLM
        
        string jsonResponse = "{}"; // Standard: Leeres JSON
        if (response.Content != null) // Response hat Content?
        {
            jsonResponse = response.Content; // Setze JSON-Response
        }
        
        System.Diagnostics.Debug.WriteLine($"[ROUTER] Intent-Response: {jsonResponse}");
        
        var intent = JsonSerializer.Deserialize<IntentJson>(jsonResponse, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); // Parse JSON
        
        IntentResult result = new IntentResult(); // Erstelle neues IntentResult
        
        if (intent != null && intent.intent != null) // Intent vorhanden?
        {
            result.Intent = intent.intent;
        }
        else // Kein Intent gefunden
        {
            result.Intent = "conversation";
        }
        
        result.SearchQuery = intent?.searchQuery;
        
        if (intent != null && intent.documentId != null) // Dokument-ID vorhanden?
        {
            result.DocumentId = intent.documentId.Value;
        }
        else // Keine Dokument-ID
        {
            result.DocumentId = 0;
        }
        
        return result;
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