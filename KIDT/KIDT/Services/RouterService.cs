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
            builder.Services.AddOpenAIChatCompletion( // OpenRouter hinzufügen (gemini-2.5-flash-lite)
                modelId: "google/gemini-2.5-flash-lite",
                apiKey: this.apiKey.Trim(),
                endpoint: new Uri("https://openrouter.ai/api/v1")
            );

            var kernel = builder.Build(); // Kernel erstellen

            McpToolsRegistry.RegisterTools(kernel, docDbService, calendarService, conversationId); // Registriere MCP-Tools (search_documents, add_document_to_chat, list_calendar_events, create_calendar_event, delete_calendar_event)
            kernel.ImportPluginFromObject(new AnalysisTools(), "Analysis"); // Registriere analyze_document Tool für Intent-basiertes Analyse-Routing

            var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service für LLM-Anfragen
            var chatHistory = new ChatHistory(); // Chat-History erstellen (System + User)

            // Aktuelles Datum und Uhrzeit für relative Abfragen
            DateTime now = DateTime.Now;
            string currentDate = now.ToString("yyyy-MM-dd");
            string currentDateGerman = now.ToString("dd.MM.yyyy");
            string currentTime = now.ToString("HH:mm");

            string dayOfWeek = now.ToString("dddd", new System.Globalization.CultureInfo("de-DE")); // Wochentag auf Deutsch
            string systemPrompt =
                $"Heute ist {dayOfWeek}, der {currentDateGerman}, {currentTime} Uhr. ({currentDate})\n" +
                "Antworte immer auf Deutsch. Verwende 'du', nicht 'Sie'.\n\n" +
                "KALENDER - Termine erstellen (benötigt: Titel + Datum + Zeit):\n" +
                "Titel-Erkennung: 'Termin [Titel] für/am [Datum]...' → das Wort/die Phrase zwischen 'Termin' und 'für'/'am'/'von' ist der Titel.\n" +
                "Beispiele: 'Termin Mittagessen für heute von 12:00-13:00' → Titel=Mittagessen, Datum=heute, Zeit=12:00-13:00\n" +
                "           'Termin Arzt morgen 14:00' → Titel=Arzt, Datum=morgen, Zeit=14:00\n" +
                "           'erstelle Termin Team-Meeting am Freitag ganztägig' → Titel=Team-Meeting, Datum=Freitag, ganztägig\n" +
                "Zeit = entweder 'ganztägig' ODER Zeitspanne (Startzeit + Endzeit, z.B. 14:00-16:00) ODER nur Startzeit.\n" +
                "Alle Infos vorhanden? → create_calendar_event SOFORT aufrufen (startTime + endTime wenn Zeitspanne).\n" +
                "KRITISCH: Antworte NIEMALS mit Text der besagt du hast einen Termin erstellt/gelöscht/aktualisiert ohne vorher das entsprechende Tool aufgerufen zu haben!\n" +
                "Bei Zeitüberschneidung meldet das Tool Fehler → informiere User und frage nach anderer Zeit.\n" +
                "Infos fehlen? → Frage direkt danach (max 15 Wörter, ganzer Satz):\n" +
                "  Alle fehlen:   \"Wie heißt der Termin, wann und ganztägig oder welche Zeitspanne?\"\n" +
                "  Titel fehlt:   \"Wie soll der Termin heißen?\"\n" +
                "  Datum fehlt:   \"Für welches Datum soll ich den Termin anlegen?\"\n" +
                "  Zeit fehlt:    \"Soll der Termin ganztägig sein oder welche Zeitspanne (z.B. 14:00-16:00)?\"\n" +
                "  Titel+Datum:   \"Wie heißt der Termin und für welches Datum?\"\n" +
                "  Titel+Zeit:    \"Wie soll der Termin heißen und ganztägig oder welche Zeitspanne?\"\n" +
                "  Datum+Zeit:    \"Für welches Datum und ganztägig oder welche Zeitspanne?\"\n\n" +
                "DOKUMENTE:\n" +
                "User sucht ein Dokument → search_documents aufrufen\n" +
                "User will Dokument-Inhalt analysieren/zusammenfassen/erklären → add_document_to_chat aufrufen (wenn Dokument noch nicht im Chat)\n\n" +
                "DATENANALYSE (Dokument-Inhalt analysieren, zusammenfassen, erklären):\n" +
                "  PRÜFE ZUERST: Ist eine DocID im CONTEXT sichtbar (z.B. 'CONTEXT: Kürzlich gefundene DocIDs: 7')?\n" +
                "    JA → analyze_document(docId) aufrufen – NICHT add_document_to_chat!\n" +
                "    NEIN → add_document_to_chat aufrufen (Dokument noch nicht im Chat)\n" +
                "  STRIKT VERBOTEN: Selbst antworten oder eigene Zusammenfassung schreiben!\n\n" +
                "SONSTIGE TOOLS: list_calendar_events, update_calendar_event, delete_calendar_event\n\n" +
                "KONVERSATION (Smalltalk, allgemeine Fragen, Begrüßungen):\n" +
                "Antworte freundlich und kurz auf Deutsch (max 2-3 Sätze).";

            chatHistory.AddSystemMessage(systemPrompt); // Füge System-Prompt hinzu

            var lastDocIds = new List<int>(); // DocIDs aus Chat-Kontext (für DataAnalysis Follow-up zugänglich)

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

                // Extrahiere DocIDs aus letzten Nachrichten für einfacheren Zugriff
                foreach (var msg in recentMessages.TakeLast(3)) // Nur letzte 3 Nachrichten prüfen
                {
                    if (!msg.IsUser && msg.Text.Contains("[DocID:")) // Assistant-Message mit DocID?
                    {
                        var startIdx = msg.Text.IndexOf("[DocID:") + 7; // Start nach "[DocID:"
                        var endIdx = msg.Text.IndexOf("]", startIdx); // Ende bei "]"
                        if (startIdx > 6 && endIdx > startIdx) // Valid indices?
                        {
                            var idsText = msg.Text.Substring(startIdx, endIdx - startIdx).Trim(); // Extrahiere IDs
                            var idStrings = idsText.Split(','); // Splitte bei Komma
                            foreach (var idStr in idStrings)
                            {
                                if (int.TryParse(idStr.Trim(), out int docId)) // Parse zu int
                                {
                                    lastDocIds.Add(docId); // Füge zur Liste hinzu
                                }
                            }
                        }
                    }
                }

                // Füge DocIDs als System-Context hinzu (falls vorhanden)
                if (lastDocIds.Count > 0)
                {
                    string docIdsContext = string.Join(", ", lastDocIds);
                    chatHistory.AddSystemMessage($"CONTEXT: Kürzlich gefundene DocIDs: {docIdsContext}");
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] DocIDs im Context: {docIdsContext}");
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

            System.Diagnostics.Debug.WriteLine($"[ROUTER] === GEMINI RAW RESPONSE ===");
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

                        // analyze_document Tool aufgerufen → Dokument aus DB laden und zu DataAnalysis routen
                        if (functionCall.FunctionName == "analyze_document")
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] analyze_document aufgerufen, extrahiere docId...");

                            int requestedDocId = 0; // DocID aus Tool-Argument
                            if (functionCall.Arguments != null) // Argumente vorhanden?
                            {
                                object? docIdObj = null;
                                functionCall.Arguments.TryGetValue("docId", out docIdObj); // DocID-Argument holen
                                if (docIdObj != null) // Argument vorhanden?
                                {
                                    int.TryParse(docIdObj.ToString(), out requestedDocId); // Parse zu int
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[ROUTER] analyze_document docId={requestedDocId}");

                            List<Document> analysisDocs = new List<Document>(); // Dokumente für DataAnalysis

                            if (requestedDocId > 0) // Spezifische DocID vorhanden?
                            {
                                Document requestedDoc = await docDbService.GetDocumentByIdAsync(requestedDocId); // Dokument aus DB laden
                                if (requestedDoc != null && requestedDoc.Id > 0) // Gültiges Dokument?
                                {
                                    analysisDocs.Add(requestedDoc); // Zur Liste hinzufügen
                                }
                            }

                            if (analysisDocs.Count == 0) // Kein Dokument gefunden → Fallback auf Kontext-DocIDs
                            {
                                foreach (int docId in lastDocIds) // Bekannte DocIDs aus Kontext
                                {
                                    Document doc = await docDbService.GetDocumentByIdAsync(docId); // Dokument aus DB laden
                                    if (doc != null && doc.Id > 0) // Gültiges Dokument?
                                    {
                                        analysisDocs.Add(doc); // Zur Liste hinzufügen
                                    }
                                }
                            }

                            RouterResponse analyzeResponse = new RouterResponse(); // Routing-Response erstellen
                            analyzeResponse.ShouldRoute = true; // Weiterleitung zu DataAnalysis
                            analyzeResponse.DirectResponse = null;
                            analyzeResponse.TargetService = "dataAnalysis";
                            analyzeResponse.MaxTokens = 2000;
                            analyzeResponse.FoundDocuments = analysisDocs;
                            analyzeResponse.Reason = "DataAnalysis-Routing (analyze_document Tool)";
                            analyzeResponse.ToolWasUsed = true;
                            return analyzeResponse;
                        }

                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Plugin: {functionCall.PluginName}, Function: {functionCall.FunctionName}");
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Arguments: {JsonSerializer.Serialize(functionCall.Arguments)}");

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
                                        List<int> docIds = new List<int>(); // Erstelle Liste für Dokument-IDs
                                        foreach (Document d in foundDocuments) // Gehe durch alle gefundenen Dokumente
                                        {
                                            string docFileName = string.Empty; // Initialisiere Dateiname
                                            if (d.FileName != null) // Dateiname vorhanden?
                                            {
                                                docFileName = d.FileName; // Übernehme Dateiname
                                            }
                                            fileNames.Add(docFileName); // Füge Dateiname zur Liste hinzu
                                            docIds.Add(d.Id); // Füge Dokument-ID zur Liste hinzu
                                        }
                                        string fileNamesList = string.Join(", ", fileNames); // Verbinde Dateinamen mit Komma
                                        string docIdsList = string.Join(",", docIds); // Verbinde IDs mit Komma
                                        message = $"{toolResultFound} Dokument(e) gefunden: {fileNamesList} [DocID: {docIdsList}]"; // Inkludiere IDs für GPT-4 Context
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

                                if (addResult != null && (addResult.success || addResult.message?.Contains("bereits") == true)) // Erfolgreich ODER bereits hinzugefügt?
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

                                    // add_document_to_chat wird laut System-Prompt nur bei Analyse-Intent aufgerufen → immer zu DataAnalysis
                                    if (docFound)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Dokument verknüpft, route zu DataAnalysis");

                                        RouterResponse analysisResponse = new RouterResponse();
                                        analysisResponse.ShouldRoute = true; // Route zu DataAnalysis
                                        analysisResponse.DirectResponse = null;
                                        analysisResponse.TargetService = "dataAnalysis";
                                        analysisResponse.FoundDocuments = foundDocuments;
                                        analysisResponse.MaxTokens = 2000;
                                        analysisResponse.Reason = "Dokument verknüpft -> DataAnalysis";
                                        analysisResponse.ToolWasUsed = true;
                                        return analysisResponse;
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
                                    addErrorResponse.Reason = "Dokument-Verknüpfung fehlgeschlagen";
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

            // SCHRITT 3: Kein Tool aufgerufen - prüfe ob dataAnalysis-JSON oder direkte Konversation
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Analysiere Gemini-Response...");

            if (responseText.TrimStart().StartsWith("{")) // Response ist JSON?
            {
                try
                {
                    var routingJson = JsonSerializer.Deserialize<SimpleRoutingJson>(responseText, // Parse JSON-Response
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); // Case-insensitive

                    if (routingJson != null && routingJson.route == "dataAnalysis") // DataAnalysis-Route erkannt?
                    {
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] DataAnalysis-Routing erkannt (Follow-up, DocIDs: {lastDocIds.Count})");

                        // Lade Dokumente aus DB anhand der DocIDs aus dem Chat-Kontext
                        List<Document> contextDocs = new List<Document>();
                        foreach (int docId in lastDocIds) // Durchlaufe bekannte DocIDs
                        {
                            Document doc = await docDbService.GetDocumentByIdAsync(docId); // Lade Dokument aus DB
                            if (doc != null && doc.Id > 0) // Gültiges Dokument?
                            {
                                contextDocs.Add(doc); // Zur Liste hinzufügen
                            }
                        }

                        RouterResponse dataResponse = new RouterResponse(); // Erstelle neue RouterResponse
                        dataResponse.ShouldRoute = true; // Weiterleitung nötig
                        dataResponse.DirectResponse = null;
                        dataResponse.TargetService = "dataAnalysis"; // Ziel: DataAnalysis-Service
                        dataResponse.MaxTokens = 2000; // Höheres Token-Limit für Analyse
                        dataResponse.FoundDocuments = contextDocs; // Dokumente aus Kontext mitgeben
                        dataResponse.Reason = "DataAnalysis-Routing (Follow-up)";
                        dataResponse.ToolWasUsed = false;
                        return dataResponse;
                    }
                }
                catch (Exception ex) // JSON-Parsing fehlgeschlagen?
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] JSON-Parse-Fehler: {ex.Message}");
                }
            }

            // SCHRITT 4: Direkte Konversations-Antwort von Gemini verwenden
            if (!string.IsNullOrWhiteSpace(responseText) && !hasToolCalls) // Antwort vorhanden und kein Tool?
            {
                System.Diagnostics.Debug.WriteLine($"[ROUTER] Direkte Konversations-Antwort von Gemini");

                RouterResponse conversationResponse = new RouterResponse(); // Erstelle neue RouterResponse
                conversationResponse.ShouldRoute = false; // Kein Routing - direkte Antwort
                conversationResponse.DirectResponse = responseText; // Gemini-Antwort direkt verwenden
                conversationResponse.FoundDocuments = new List<Document>(); // Leere Dokument-Liste
                conversationResponse.Reason = "Konversation (Gemini direkt)";
                conversationResponse.ToolWasUsed = false;
                return conversationResponse;
            }

            // SCHRITT 5: Fallback wenn keine verwertbare Antwort
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Fallback: Keine verwertbare Antwort");

            RouterResponse fallbackResponse = new RouterResponse(); // Erstelle Fallback-Response
            fallbackResponse.ShouldRoute = false; // Kein Routing
            fallbackResponse.DirectResponse = "Wie kann ich dir helfen?"; // Standard-Antwort
            fallbackResponse.FoundDocuments = new List<Document>(); // Leere Dokument-Liste
            fallbackResponse.Reason = "Fallback (leere Antwort)";
            fallbackResponse.ToolWasUsed = false;
            return fallbackResponse;
        }
        catch (Exception ex)
        {
            throw new Exception($"Router-Fehler: {ex.Message}");
        }
    }

    private async Task<IntentResult> ClassifyIntentAsync(string userMessage) // Hilfsmethode: Klassifiziert Intent (Fallback, selten aufgerufen)
    {
        var builder = Kernel.CreateBuilder(); // Erstelle neuen Kernel
        builder.Services.AddOpenAIChatCompletion( // OpenRouter als Fallback
            modelId: "google/gemini-2.5-flash-lite",
            apiKey: this.apiKey.Trim(),
            endpoint: new Uri("https://openrouter.ai/api/v1")
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

    private class SimpleRoutingJson // JSON-Klasse: DataAnalysis-Routing-Signal von Gemini
    {
        public string? route { get; set; } // "dataAnalysis" wenn Analyse nötig
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

internal class AnalysisTools // Dummy-Klasse: stellt analyze_document als SK-Tool bereit (echter Handler läuft im RouterService)
{
    [KernelFunction("analyze_document")]
    [System.ComponentModel.Description("Analysiert oder fasst den Inhalt eines Dokuments aus dem Chat zusammen. Aufrufen wenn der User Dokument-Inhalt verstehen, zusammenfassen, erklären oder analysieren möchte und ein Dokument im Chat-Kontext vorhanden ist.")]
    public string AnalyzeDocument( // Dummy-Methode: wird nie wirklich ausgeführt, Routing passiert im RouterService-Handler
        [System.ComponentModel.Description("DocID des Dokuments aus dem Chat-Kontext (steht in [DocID: X] Nachrichten)")]
        int docId) // DocID des zu analysierenden Dokuments
    {
        return docId.ToString(); // Dummy-Return — Routing passiert im RouterService-Handler
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