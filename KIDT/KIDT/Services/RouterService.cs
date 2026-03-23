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
    private readonly IServiceProvider serviceProvider;
    private string apiKey = string.Empty;

    public RouterService(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider; // Service Provider speichern für spätere Scope-Erstellung
    }

    public async Task InitializeAsync() // Lade API-Key aus Datei
    {
        if (this.apiKey.Length > 0) // Bereits initialisiert?
        {
            return;
        }

        string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openai-api-key.txt"); // Erstelle Pfad zur Key-Datei
        this.apiKey = await File.ReadAllTextAsync(keyPath); // Lade Key aus Datei
    }

    public async Task<RouterResponse> ProcessAsync(string userMessage, bool hasFile, int conversationId) // Hauptmethode: Analysiert User-Nachricht und routet zu passendem Service ODER handhabt Dokument-Suche
    {
        if (this.apiKey.Length == 0) // API-Key noch nicht geladen?
        {
            await InitializeAsync(); // Initialisiere API-Key
        }

        using var scope = this.serviceProvider.CreateScope(); // Erstelle neuen Service-Scope für DB-Zugriff
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Hole DocumentDbService für Dokument-Operationen
        var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>(); // Hole CalendarService für Kalender-Operationen
        var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>(); // Hole ChatDbService für Chat-History-Zugriff

        try
        {
            // SCHRITT 1: Erstelle Kernel mit MCP-Tools für Function Calling
            var builder = Kernel.CreateBuilder();
            builder.Services.AddAzureOpenAIChatCompletion( // Azure OpenAI hinzufügen
                deploymentName: "gpt-4.1-deployment",
                endpoint: "https://ts-openai-testing.openai.azure.com/",
                apiKey: this.apiKey.Trim()
            );

            var kernel = builder.Build(); // Kernel erstellen

            McpToolsRegistry.RegisterTools(kernel, docDbService, calendarService, conversationId); // Registriere MCP-Tools (search_documents, add_document_to_chat, list_calendar_events, create_calendar_event, delete_calendar_event)

            var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service für LLM-Anfragen
            var chatHistory = new ChatHistory(); // Chat-History erstellen (System + User)

            // Aktuelles Datum für relative Datumsangaben
            DateTime now = DateTime.Now;
            string currentDate = now.ToString("yyyy-MM-dd");
            string currentDateGerman = now.ToString("dd.MM.yyyy");
            int currentYear = now.Year;

            string systemPrompt =
                $"Router-Agent mit Tools. DATUM: {currentDate} ({currentDateGerman}), Jahr: {currentYear}\n\n" +
                "Du hast KEINE Infos zu Dokumenten/Terminen - nutze Tools!\n\n" +
                "TERMIN-ERSTELLUNG:\n" +
                "Pflicht: Titel, Datum, Zeit (ganztaegig/Uhrzeit)\n" +
                "Analysiere History: Was FEHLT?\n\n" +
                "Titel-Erkennung:\n" +
                "- 'Meeting' -> Titel DA\n" +
                "- 'name ist Test3' -> Titel DA (Test3)\n" +
                "- 'heißt Zahnarzt' -> Titel DA (Zahnarzt)\n" +
                "- 'der termin soll Test4 heissen' -> Titel DA (Test4)\n\n" +
                "Wenn Assistant fragt und User antwortet -> Info vorhanden!\n\n" +
                "ALLE 3 da? -> create_calendar_event\n\n" +
                "Infos fehlen? JSON:\n" +
                "ALLE 3: {\"needsRouting\": true, \"intent\": \"conversation\", \"conversationTask\": \"askForAll\"}\n" +
                "Nur TITEL: \"askForTitle\"\n" +
                "Nur DATUM: \"askForDate\"\n" +
                "Nur ZEIT: \"askForTime\"\n" +
                "TITEL+DATUM: \"askForTitleAndDate\"\n" +
                "TITEL+ZEIT: \"askForTitleAndTime\"\n" +
                "DATUM+ZEIT: \"askForDateAndTime\"\n\n" +
                "TOOLS: search_documents, add_document_to_chat, list_calendar_events, update_calendar_event, delete_calendar_event\n\n" +
                "DU BIST ROUTER! Nur Tool-Calls oder JSON!\n" +
                "KEINE eigenen Texte/Fragen/Erklärungen!";

            chatHistory.AddSystemMessage(systemPrompt); // Füge System-Prompt hinzu

            // WICHTIG: Lade vorherige Chat-History aus DB (Router braucht Kontext für Termin-Erstellung!)
            // LIMIT: Nur letzte 10 Nachrichten laden (Token-Limit beachten!)
            if (conversationId > 0) // Bestehender Chat?
            {
                var allMessages = await dbService.LoadMessagesAsync(conversationId); // Lade alle Nachrichten

                // Nehme nur die letzten 10 Nachrichten (um Token-Limit nicht zu überschreiten)
                var recentMessages = allMessages.Count > 10 
                    ? allMessages.Skip(allMessages.Count - 10).ToList() 
                    : allMessages;

                System.Diagnostics.Debug.WriteLine($"[ROUTER] Lade {recentMessages.Count} vorherige Nachrichten aus DB (von {allMessages.Count} gesamt)...");

                foreach (var msg in recentMessages) // Durchlaufe nur letzte 10 Nachrichten
                {
                    if (msg.IsUser) // User-Nachricht?
                    {
                        chatHistory.AddUserMessage(msg.Text); // Füge User-Message zur History hinzu
                    }
                    else // Assistant-Nachricht
                    {
                        chatHistory.AddAssistantMessage(msg.Text); // Füge Assistant-Message zur History hinzu
                    }
                }
            }

            chatHistory.AddUserMessage(userMessage); // Füge aktuelle User-Nachricht hinzu (NACH der History!)

            var executionSettings = new OpenAIPromptExecutionSettings // Execution-Settings für LLM-Anfrage
            {
                ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions, // Enable Function Calling (manuelles Handling!)
                Temperature = 0.1, // Niedrige Temperature für deterministisches Verhalten
                MaxTokens = 2000 // Erhöhtes Limit: Kurzer Prompt = mehr Tokens für Tool-Calls/Antworten
            };

            System.Diagnostics.Debug.WriteLine($"[ROUTER] Sende Anfrage mit Function Calling...");
            System.Diagnostics.Debug.WriteLine($"[ROUTER] User Message: {userMessage}");
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Has File: {hasFile}");
            System.Diagnostics.Debug.WriteLine($"[ROUTER] ConversationId: {conversationId}");
            System.Diagnostics.Debug.WriteLine($"[ROUTER] System Prompt Length: {systemPrompt.Length} chars");

            var response = await chatService.GetChatMessageContentAsync( // Sende Anfrage an LLM
                chatHistory, // Chat-History (System + User)
                executionSettings: executionSettings, // Execution-Settings
                kernel: kernel // Kernel mit registrierten Tools
            );

            string responseText = string.Empty; // Variable für Response-Text
            if (response.Content != null) // Response hat Content?
            {
                responseText = response.Content.Trim(); // Setze Response-Text und trimme Whitespace
            }

            System.Diagnostics.Debug.WriteLine($"[ROUTER] === GPT-4 RAW RESPONSE ===");
            System.Diagnostics.Debug.WriteLine(responseText);
            System.Diagnostics.Debug.WriteLine($"[ROUTER] === END RAW RESPONSE ===");

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
                                bool hasToolResult = false; // Flag für ToolResult vorhanden
                                bool hasDocIds = false; // Flag für DocumentIds vorhanden
                                if (toolResult != null) // ToolResult vorhanden?
                                {
                                    hasToolResult = true;
                                    if (toolResult.documentIds != null) // DocumentIds vorhanden?
                                    {
                                        hasDocIds = true;
                                    }
                                }
                                if (hasToolResult)
                                {
                                    int docIdsCount = 0; // Initialisiere Count
                                    int foundCount = 0; // Initialisiere found Count
                                    if (toolResult != null) // Prüfe ob toolResult vorhanden
                                    {
                                        foundCount = toolResult.found; // Hole found Count
                                        if (toolResult.documentIds != null) // Prüfe ob documentIds vorhanden
                                        {
                                            docIdsCount = toolResult.documentIds.Count; // Hole Count
                                        }
                                    }
                                    System.Diagnostics.Debug.WriteLine($"[ROUTER] JSON geparsed: found={foundCount}, documentIds={docIdsCount}");
                                }

                                if (hasToolResult && hasDocIds) // Erfolgreiche Suche?
                                {
                                    int docIdsCount = 0; // Initialisiere Count
                                    if (toolResult != null && toolResult.documentIds != null) // Prüfe ob documentIds vorhanden
                                    {
                                        docIdsCount = toolResult.documentIds.Count; // Hole Count
                                    }
                                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Lade {docIdsCount} Dokumente...");

                                    if (toolResult != null && toolResult.documentIds != null) // Nochmalige Prüfung für foreach
                                    {
                                        foreach (var docId in toolResult.documentIds) // Durchlaufe gefundene Dokument-IDs
                                        {
                                            Document docTemp = await docDbService.GetDocumentByIdAsync(docId); // Lade volles Dokument aus DB
                                            bool docFound = false; // Flag für Dokument gefunden
                                            Document doc = new Document(); // Initialisiere Dokument
                                            if (docTemp != null) // Prüfe ob Dokument vorhanden
                                            {
                                                doc = docTemp; // Übernehme Dokument
                                                if (doc.Id > 0) // Prüfe ob ID gültig (nicht -1 vom Dummy)
                                                {
                                                    docFound = true; // Setze Flag
                                                }
                                            }
                                            if (docFound) // Füge nur hinzu wenn gefunden
                                            {
                                                foundDocuments.Add(doc); // Füge zu Liste hinzu
                                            }
                                        }
                                    }

                                    string message; // Variable für Nachricht
                                    int toolResultFound = 0; // Initialisiere found Count
                                    if (toolResult != null) // Prüfe ob toolResult vorhanden
                                    {
                                        toolResultFound = toolResult.found; // Hole found Count
                                    }
                                    if (toolResultFound > 0) // Dokumente gefunden?
                                    {
                                        List<string> fileNames = new List<string>(); // Erstelle Liste für Dateinamen
                                        foreach (Document d in foundDocuments) // Gehe durch alle gefundenen Dokumente
                                        {
                                            string docFileName = string.Empty; // Initialisiere Dateiname
                                            if (d.FileName != null) // Dateiname vorhanden?
                                            {
                                                docFileName = d.FileName; // Übernehme Dateiname
                                            }
                                            fileNames.Add(docFileName); // Füge Dateiname zur Liste hinzu
                                        }
                                        string fileNamesList = string.Join(", ", fileNames); // Verbinde Dateinamen mit Komma
                                        message = $"{toolResultFound} Dokument(e) gefunden: {fileNamesList}";
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


                            // list_calendar_events Tool wurde aufgerufen
                            if (functionCall.FunctionName == "list_calendar_events") // War Tool list_calendar_events?
                            {
                                var listResult = JsonSerializer.Deserialize<CalendarListResultJson>(resultText); // Parse JSON-Result
                                bool hasListResult = false; // Flag für ListResult vorhanden
                                bool hasEvents = false; // Flag für Events vorhanden
                                if (listResult != null) // ListResult vorhanden?
                                {
                                    hasListResult = true;
                                    if (listResult.events != null) // Events vorhanden?
                                    {
                                        hasEvents = true;
                                    }
                                }

                                if (hasListResult) // Erfolgreich geparsed?
                                {
                                    string message; // Variable für Nachricht
                                    int foundCount = 0; // Initialisiere Count
                                    List<CalendarEvent> foundEvents = new List<CalendarEvent>(); // Liste für Events

                                    if (listResult != null) // Prüfe ob listResult vorhanden
                                    {
                                        foundCount = listResult.found; // Hole found Count
                                    }

                                    if (foundCount > 0 && hasEvents) // Termine gefunden und Events vorhanden?
                                    {
                                        if (listResult != null && listResult.events != null) // Prüfe ob events vorhanden
                                        {
                                            foreach (var e in listResult.events)
                                            {
                                                // Lade vollständiges Event aus DB
                                                var fullEvent = await calendarService.GetEventByIdAsync(e.id);
                                                if (fullEvent != null)
                                                {
                                                    foundEvents.Add(fullEvent);
                                                }
                                            }
                                        }
                                        message = $"{foundCount} Termin(e) gefunden"; // Kurze Meldung - Cards zeigen Details
                                    }
                                    else // Keine Termine gefunden
                                    {
                                        message = "Keine Termine gefunden.";
                                    }

                                    RouterResponse listResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    listResponse.ShouldRoute = false; // Kein Routing nötig
                                    listResponse.DirectResponse = message;
                                    listResponse.FoundDocuments = new List<Document>(); // Keine Dokumente
                                    listResponse.FoundEvents = foundEvents; // Setze gefundene Events
                                    listResponse.TargetService = string.Empty; // Kein Target-Service
                                    listResponse.MaxTokens = 0; // Keine Token-Limit
                                    listResponse.Reason = "Termine aufgelistet (Function Calling)";
                                    listResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return listResponse;
                                }
                            }

                            // create_calendar_event Tool wurde aufgerufen
                            if (functionCall.FunctionName == "create_calendar_event") // War Tool create_calendar_event?
                            {
                                var createResult = JsonSerializer.Deserialize<CalendarCreateResultJson>(resultText); // Parse JSON-Result
                                if (createResult != null && createResult.success) // Erfolgreich erstellt?
                                {
                                    string createMessage = string.Empty; // Initialisiere Nachricht
                                    if (createResult.message != null) // Nachricht vorhanden?
                                    {
                                        createMessage = createResult.message; // Übernehme Nachricht
                                    }

                                    RouterResponse createResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    createResponse.ShouldRoute = false; // Kein Routing nötig
                                    createResponse.DirectResponse = createMessage;
                                    createResponse.FoundDocuments = new List<Document>(); // Keine Dokumente
                                    createResponse.TargetService = string.Empty; // Kein Target-Service
                                    createResponse.MaxTokens = 0; // Keine Token-Limit
                                    createResponse.Reason = "Termin erstellt (Function Calling)";
                                    createResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return createResponse;
                                }
                                else // Fehler beim Erstellen
                                {
                                    string errorMessage = "Termin konnte nicht erstellt werden.";
                                    if (createResult != null && createResult.message != null) // Fehlermeldung vorhanden?
                                    {
                                        errorMessage = createResult.message; // Verwende spezifische Fehlermeldung
                                    }

                                    RouterResponse createErrorResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    createErrorResponse.ShouldRoute = false; // Kein Routing nötig
                                    createErrorResponse.DirectResponse = errorMessage;
                                    createErrorResponse.FoundDocuments = new List<Document>(); // Keine Dokumente
                                    createErrorResponse.TargetService = string.Empty; // Kein Target-Service
                                    createErrorResponse.MaxTokens = 0; // Keine Token-Limit
                                    createErrorResponse.Reason = "Termin-Erstellung fehlgeschlagen";
                                    createErrorResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return createErrorResponse;
                                }
                            }

                            // delete_calendar_event Tool wurde aufgerufen
                            if (functionCall.FunctionName == "delete_calendar_event") // War Tool delete_calendar_event?
                            {
                                var deleteResult = JsonSerializer.Deserialize<CalendarDeleteResultJson>(resultText); // Parse JSON-Result

                                // Mehrere Termine gefunden → Nachfragen
                                if (deleteResult != null && deleteResult.needsClarification && deleteResult.events != null)
                                {
                                    var eventList = new List<string>();
                                    foreach (var e in deleteResult.events)
                                    {
                                        string timeInfo = "";
                                        if (e.time != "Ganztägig")
                                        {
                                            timeInfo = $" ({e.time})";
                                        }
                                        eventList.Add($"• ID {e.id}: {e.title}" + timeInfo);
                                    }
                                    string deleteMsg = string.Empty; // Initialisiere Nachricht
                                    if (deleteResult.message != null) // Nachricht vorhanden?
                                    {
                                        deleteMsg = deleteResult.message; // Übernehme Nachricht
                                    }
                                    string message = deleteMsg + "\n" + string.Join("\n", eventList);

                                    RouterResponse clarificationResponse = new RouterResponse();
                                    clarificationResponse.ShouldRoute = false;
                                    clarificationResponse.DirectResponse = message;
                                    clarificationResponse.FoundDocuments = new List<Document>();
                                    clarificationResponse.TargetService = string.Empty;
                                    clarificationResponse.MaxTokens = 0;
                                    clarificationResponse.Reason = "Mehrere Termine gefunden (Nachfrage)";
                                    clarificationResponse.ToolWasUsed = true;
                                    return clarificationResponse;
                                }

                                if (deleteResult != null && deleteResult.success) // Erfolgreich gelöscht?
                                {
                                    string deleteMessage = string.Empty; // Initialisiere Nachricht
                                    if (deleteResult.message != null) // Nachricht vorhanden?
                                    {
                                        deleteMessage = deleteResult.message; // Übernehme Nachricht
                                    }

                                    RouterResponse deleteResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    deleteResponse.ShouldRoute = false; // Kein Routing nötig
                                    deleteResponse.DirectResponse = deleteMessage;
                                    deleteResponse.FoundDocuments = new List<Document>(); // Keine Dokumente
                                    deleteResponse.TargetService = string.Empty; // Kein Target-Service
                                    deleteResponse.MaxTokens = 0; // Keine Token-Limit
                                    deleteResponse.Reason = "Termin gelöscht (Function Calling)";
                                    deleteResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return deleteResponse;
                                }
                                else // Fehler beim Löschen
                                {
                                    string errorMessage = "Termin konnte nicht gelöscht werden.";
                                    if (deleteResult != null && deleteResult.message != null) // Fehlermeldung vorhanden?
                                    {
                                        errorMessage = deleteResult.message; // Verwende spezifische Fehlermeldung
                                    }

                                    RouterResponse deleteErrorResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    deleteErrorResponse.ShouldRoute = false; // Kein Routing nötig
                                    deleteErrorResponse.DirectResponse = errorMessage;
                                    deleteErrorResponse.FoundDocuments = new List<Document>(); // Keine Dokumente
                                    deleteErrorResponse.TargetService = string.Empty; // Kein Target-Service
                                    deleteErrorResponse.MaxTokens = 0; // Keine Token-Limit
                                    deleteErrorResponse.Reason = "Termin nicht gefunden";
                                    deleteErrorResponse.ToolWasUsed = true; // Tool wurde verwendet
                                    return deleteErrorResponse;
                                }
                            }

                            // update_calendar_event Tool wurde aufgerufen
                            if (functionCall.FunctionName == "update_calendar_event") // War Tool update_calendar_event?
                            {
                                var updateResult = JsonSerializer.Deserialize<CalendarUpdateResultJson>(resultText); // Parse JSON-Result

                                // Mehrere Termine gefunden → Nachfragen
                                if (updateResult != null && updateResult.needsClarification && updateResult.events != null)
                                {
                                    var eventList = new List<string>();
                                    foreach (var e in updateResult.events)
                                    {
                                        string timeInfo = "";
                                        if (e.time != "Ganztägig")
                                        {
                                            timeInfo = $" ({e.time})";
                                        }
                                        eventList.Add($"• ID {e.id}: {e.title}" + timeInfo);
                                    }
                                    string updateMsg = string.Empty; // Initialisiere Nachricht
                                    if (updateResult.message != null) // Nachricht vorhanden?
                                    {
                                        updateMsg = updateResult.message; // Übernehme Nachricht
                                    }
                                    string message = updateMsg + "\n" + string.Join("\n", eventList);

                                    RouterResponse clarificationResponse = new RouterResponse();
                                    clarificationResponse.ShouldRoute = false;
                                    clarificationResponse.DirectResponse = message;
                                    clarificationResponse.FoundDocuments = new List<Document>();
                                    clarificationResponse.TargetService = string.Empty;
                                    clarificationResponse.MaxTokens = 0;
                                    clarificationResponse.Reason = "Mehrere Termine gefunden (Nachfrage)";
                                    clarificationResponse.ToolWasUsed = true;
                                    return clarificationResponse;
                                }

                                if (updateResult != null && updateResult.success) // Erfolgreich aktualisiert?
                                {
                                    string updateMessage = string.Empty; // Initialisiere Nachricht
                                    if (updateResult.message != null) // Nachricht vorhanden?
                                    {
                                        updateMessage = updateResult.message; // Übernehme Nachricht
                                    }

                                    RouterResponse updateResponse = new RouterResponse();
                                    updateResponse.ShouldRoute = false;
                                    updateResponse.DirectResponse = updateMessage;
                                    updateResponse.FoundDocuments = new List<Document>();
                                    updateResponse.TargetService = string.Empty;
                                    updateResponse.MaxTokens = 0;
                                    updateResponse.Reason = "Termin aktualisiert (Function Calling)";
                                    updateResponse.ToolWasUsed = true;
                                    return updateResponse;
                                }
                                else // Fehler beim Aktualisieren
                                {
                                    string errorMessage = "Termin konnte nicht aktualisiert werden.";
                                    if (updateResult != null && updateResult.message != null)
                                    {
                                        errorMessage = updateResult.message;
                                    }

                                    RouterResponse updateErrorResponse = new RouterResponse();
                                    updateErrorResponse.ShouldRoute = false;
                                    updateErrorResponse.DirectResponse = errorMessage;
                                    updateErrorResponse.FoundDocuments = new List<Document>();
                                    updateErrorResponse.TargetService = string.Empty;
                                    updateErrorResponse.MaxTokens = 0;
                                    updateErrorResponse.Reason = "Termin-Update fehlgeschlagen";
                                    updateErrorResponse.ToolWasUsed = true;
                                    return updateErrorResponse;
                                }
                            }


                            // add_document_to_chat Tool wurde aufgerufen
                            if (functionCall.FunctionName == "add_document_to_chat") // War Tool add_document_to_chat?
                            {
                                var addResult = JsonSerializer.Deserialize<AddDocumentResultJson>(resultText); // Parse JSON-Result
                                if (addResult != null && addResult.success) // Erfolgreich hinzugefügt?
                                {
                                    Document docTemp = await docDbService.GetDocumentByIdAsync(addResult.documentId); // Lade Dokument
                                    bool docFound = false; // Flag für Dokument gefunden
                                    Document doc = new Document(); // Initialisiere Dokument
                                    if (docTemp != null) // Prüfe ob Dokument vorhanden
                                    {
                                        doc = docTemp; // Übernehme Dokument
                                        if (doc.Id > 0) // Prüfe ob ID gültig (nicht -1 vom Dummy)
                                        {
                                            docFound = true; // Setze Flag
                                        }
                                    }
                                    if (docFound) // Füge nur hinzu wenn gefunden
                                    {
                                        foundDocuments.Add(doc);
                                    }

                                    string fileName = "Unbekannt"; // Standard-Dateiname
                                    if (addResult.fileName != null) // Dateiname vorhanden?
                                    {
                                        fileName = addResult.fileName; // Übernehme Dateiname
                                    }

                                    RouterResponse addSuccessResponse = new RouterResponse(); // Erstelle neue RouterResponse
                                    addSuccessResponse.ShouldRoute = false; // Kein Routing nötig
                                    addSuccessResponse.DirectResponse = $"Dokument '{fileName}' wurde zum Chat hinzugefügt.";
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

                    if (routingJson != null && routingJson.needsRouting == true) // Routing nötig?
                    {
                        string targetService = "conversation"; // Standardmäßig Conversation
                        if (routingJson.intent == "dataAnalysis") // Intent ist DataAnalysis?
                        {
                            targetService = "dataAnalysis";
                        }

                        int maxTokens = 500; // Standard Token-Limit für Conversation (reicht für kurze UND lange Antworten)
                        if (targetService == "dataAnalysis") // DataAnalysis gewählt?
                        {
                            maxTokens = 2000; // Höheres Token-Limit für komplexe Analyse
                        }

                        string conversationTask = string.Empty; // Variable für Conversation-Task
                        if (routingJson.conversationTask != null) // Task vorhanden?
                        {
                            conversationTask = routingJson.conversationTask; // Setze Task
                        }

                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Routing zu: {targetService}");
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Conversation-Task: {conversationTask}");

                        RouterResponse intentResponse = new RouterResponse(); // Erstelle neue RouterResponse
                        intentResponse.ShouldRoute = true; // Routing nötig
                        intentResponse.DirectResponse = null;
                        intentResponse.TargetService = targetService;
                        intentResponse.ConversationTask = conversationTask; // NEU: Setze Task
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

            // SCHRITT 4: Letzter Fallback → Intent-Klassifizierung per GPT-4
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Fallback: Intent-Klassifizierung per GPT-4...");
            var fallbackIntent = await ClassifyIntentAsync(userMessage); // Klassifiziere Intent per GPT-4

            System.Diagnostics.Debug.WriteLine($"[ROUTER] Fallback Intent Result: {fallbackIntent.Intent}");

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
                        string docFileName = string.Empty; // Initialisiere Dateiname
                        if (d.FileName != null) // Dateiname vorhanden?
                        {
                            docFileName = d.FileName; // Übernehme Dateiname
                        }
                        fileNames.Add(docFileName); // Füge Dateiname zur Liste hinzu
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

            int finalMaxTokens = 500; // Standard Token-Limit für Conversation
            if (finalTargetService == "dataAnalysis") // DataAnalysis gewählt?
            {
                finalMaxTokens = 2000; // Höheres Token-Limit für komplexe Analyse
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
            apiKey: this.apiKey.Trim()
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
            jsonResponse = response.Content.Trim(); // Setze JSON-Response und trimme
        }

        System.Diagnostics.Debug.WriteLine($"[ROUTER] === ClassifyIntentAsync RAW RESPONSE ===");
        System.Diagnostics.Debug.WriteLine(jsonResponse);
        System.Diagnostics.Debug.WriteLine($"[ROUTER] === END CLASSIFY RESPONSE ===");

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

        if (intent != null)
        {
            result.SearchQuery = intent.searchQuery;
        }

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
        public bool needsRouting { get; set; }
        public string? intent { get; set; }
        public string? conversationTask { get; set; } // NEU: Task für Coordinator
    }

    private class SearchResultJson // JSON-Klasse: Ergebnis von search_documents Tool
    {
        public int found { get; set; }
        public List<int>? documentIds { get; set; }
        public string? message { get; set; }
    }

    private class AddDocumentResultJson // JSON-Klasse: Ergebnis von add_document_to_chat Tool
    {
        public bool success { get; set; }
        public int documentId { get; set; }
        public string? fileName { get; set; }
        public string? message { get; set; }
    }

    private class IntentJson // JSON-Klasse: Intent-Klassifizierung per GPT-4
    {
        public string? intent { get; set; }
        public string? searchQuery { get; set; }
        public int? documentId { get; set; }
    }

    private class IntentResult // Klasse: Ergebnis von ClassifyIntentAsync
    {
        public string Intent { get; set; } = "conversation";
        public string? SearchQuery { get; set; }
        public int DocumentId { get; set; }
    }

    private class CalendarListResultJson // JSON-Klasse: Ergebnis von list_calendar_events Tool
    {
        public int found { get; set; }
        public List<CalendarEventJson>? events { get; set; }
        public string? message { get; set; }
    }

    private class CalendarEventJson // JSON-Klasse: Einzelner Termin in list_calendar_events
    {
        public int id { get; set; }
        public string date { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string time { get; set; } = string.Empty;
        public int color { get; set; }
    }

    private class CalendarCreateResultJson // JSON-Klasse: Ergebnis von create_calendar_event Tool
    {
        public bool success { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
    }

    private class CalendarDeleteResultJson // JSON-Klasse: Ergebnis von delete_calendar_event Tool
    {
        public bool success { get; set; }
        public bool needsClarification { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
        public List<CalendarEventJson>? events { get; set; }
    }

    private class CalendarUpdateResultJson // JSON-Klasse: Ergebnis von update_calendar_event Tool
    {
        public bool success { get; set; }
        public bool needsClarification { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
        public List<CalendarEventJson>? events { get; set; }
    }
}

public class RouterResponse // Klasse: Response des RouterService (wird an ChatCoordinator zurückgegeben)
{
    public bool ShouldRoute { get; set; }
    public string? DirectResponse { get; set; }
    public string TargetService { get; set; } = string.Empty;
    public string ConversationTask { get; set; } = string.Empty; // NEU: Task für Coordinator ("askForTitle", "askForDate", "askForTime")
    public int MaxTokens { get; set; }
    public List<Document> FoundDocuments { get; set; } = new List<Document>();
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
    public string Reason { get; set; } = string.Empty;
    public bool ToolWasUsed { get; set; }
}