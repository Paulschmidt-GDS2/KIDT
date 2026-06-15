using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using KIDT.Database;
using KIDT.Models;
using KIDT.Services.Logic;

namespace KIDT.Services;

public partial class RouterService // Analysiert User-Nachrichten, ruft Tools auf und routet zu DataAnalysis
{
    private readonly IServiceProvider serviceProvider;
    private string apiKey = string.Empty;

    public RouterService(IServiceProvider serviceProvider) // Konstruktor: ServiceProvider für Scoped-Dienste (DB, Calendar etc.)
    {
        this.serviceProvider = serviceProvider;
    }

    public async Task InitializeAsync() // Lädt API-Key aus Datei
    {
        if (this.apiKey.Length > 0) return; // Bereits geladen → nichts tun
        string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenRouter-api-key.txt"); // Pfad zur Key-Datei
        this.apiKey = await File.ReadAllTextAsync(keyPath); // Key einlesen
    }

    public async Task<RouterResponse> ProcessAsync(string userMessage, bool hasFile, int conversationId, CancellationToken cancellationToken = default) // Hauptmethode: Verarbeitet Nachricht, führt Tools aus und gibt Routing-Ergebnis zurück
    {
        if (this.apiKey.Length == 0) await InitializeAsync(); // API-Key bei Bedarf laden

        using var scope = this.serviceProvider.CreateScope(); // Scoped DI-Container erstellen
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Dokument-DB-Service holen
        var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>(); // Kalender-Service holen
        var folderDbService = scope.ServiceProvider.GetRequiredService<FolderDbService>(); // Ordner-Service holen
        var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>(); // Chat-DB-Service holen

        System.Diagnostics.Debug.WriteLine($"[ROUTER] ProcessAsync → Modell: gemini-2.5-flash via OpenRouter, ConvId={conversationId}");

        try
        {
            // --- Kernel aufbauen ---
            var builder = Kernel.CreateBuilder(); // SK-Builder initialisieren
            builder.Services.AddOpenAIChatCompletion(
                modelId: "google/gemini-2.5-flash",
                apiKey: this.apiKey.Trim(),
                endpoint: new Uri("https://openrouter.ai/api/v1") // OpenRouter-Endpunkt (Gemini-Proxy)
            );
            var kernel = builder.Build(); // Fertigen Kernel erstellen
            McpToolsRegistry.RegisterTools(kernel, docDbService, calendarService, folderDbService, conversationId); // MCP-Tools registrieren
            kernel.ImportPluginFromObject(new AnalysisTools(), "Analysis"); // Analysis-Plugin registrieren

            var chatService = kernel.GetRequiredService<IChatCompletionService>(); // Chat-Service aus Kernel holen

            // --- System-Prompt mit aktuellem Datum ---
            DateTime now = DateTime.Now; // Aktuelles Datum/Uhrzeit für System-Prompt
            string systemPrompt = BuildSystemPrompt(
                now.ToString("dddd", new System.Globalization.CultureInfo("de-DE")), // Wochentag auf Deutsch
                now.ToString("dd.MM.yyyy"), now.ToString("HH:mm"), now.ToString("yyyy-MM-dd")); // Datum + Uhrzeit + ISO-Format

            // --- Chat-History aufbauen ---
            var chatHistory = new ChatHistory(); // Neuen Chat-Verlauf starten
            chatHistory.AddSystemMessage(systemPrompt); // System-Prompt als erste Nachricht

            var lastDocIds = new List<int>(); // Zuletzt referenzierte Dokument-IDs (für Follow-ups)

            if (conversationId > 0) // Letzte 10 Nachrichten aus DB laden
            {
                var allMessages = await dbService.LoadMessagesAsync(conversationId); // Alle Nachrichten dieser Conversation laden

                List<Message> recent;
                if (allMessages.Count > 10) // Nur letzte 10 Nachrichten für Kontext
                {
                    recent = new List<Message>();
                    int startIndex = allMessages.Count - 10; // Startindex für die letzten 10
                    for (int i = startIndex; i < allMessages.Count; i++)
                    {
                        recent.Add(allMessages[i]);
                    }
                }
                else
                {
                    recent = allMessages; // Weniger als 10 → alle verwenden
                }

                foreach (Message msg in recent) // Chat-History aufbauen
                {
                    if (msg.Text.StartsWith("[DocID:")) continue; // Interne Marker nicht in LLM-Kontext aufnehmen

                    if (msg.IsUser) // User-Nachricht?
                    {
                        chatHistory.AddUserMessage(msg.Text);
                    }
                    else // Assistant-Nachricht
                    {
                        chatHistory.AddAssistantMessage(msg.Text);
                    }
                }

                for (int i = 0; i < recent.Count; i++) // Alle recent messages nach DocIDs durchsuchen (kein 3er-Limit, übersteht auch Fehler-Nachrichten)
                {
                    Message msg = recent[i];
                    if (!msg.IsUser && !string.IsNullOrEmpty(msg.DocumentIdsJson)) // DocIDs aus JSON-Feld
                    {
                        try
                        {
                            var ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(msg.DocumentIdsJson); // JSON → ID-Liste
                            if (ids != null)
                            {
                                foreach (int id in ids)
                                {
                                    if (!lastDocIds.Contains(id)) lastDocIds.Add(id); // Duplikate vermeiden
                                }
                            }
                        }
                        catch { } // Parse-Fehler ignorieren
                    }
                }

                if (lastDocIds.Count > 0) // Kontext-DocIDs als System-Nachricht nach der Konversation (bewährter Ansatz)
                {
                    chatHistory.AddSystemMessage($"CONTEXT: Kürzlich gefundene DocIDs: {string.Join(", ", lastDocIds)}"); // DocIDs für analyze_document-Routing
                }
            }

            chatHistory.AddUserMessage(userMessage); // Aktuelle User-Nachricht hinzufügen

            // FunctionChoiceBehavior statt ToolCallBehavior: bessere Gemini-Kompatibilität,
            // korrektes Plugin-Naming, kein Parallel-Call-Crash (GitHub #12554)
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                    autoInvoke: false, // Manueller Dispatch im RouterService
                    options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = false } // Gemini-Crash bei Parallel-Calls verhindern
                ),
                Temperature = 0.1,
                MaxTokens = 2000
            };

            cancellationToken.ThrowIfCancellationRequested(); // Abbruch vor LLM-Aufruf prüfen

            System.Diagnostics.Debug.WriteLine($"[ROUTER] Gemini aufgerufen → Nachricht: \"{userMessage.Substring(0, Math.Min(60, userMessage.Length))}\"");

            // SK/Gemini-Kompatibilität: bei unbekanntem ChatFinishReason einmal retry
            ChatMessageContent response;
            try
            {
                response = await chatService.GetChatMessageContentAsync(chatHistory, executionSettings, kernel); // LLM aufrufen
            }
            catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason") || ex.Message.Contains("Unknown"))
            {
                System.Diagnostics.Debug.WriteLine($"[ROUTER] SK ChatFinishReason-Fehler, Retry nach 300ms... ({ex.Message})");
                await Task.Delay(300, cancellationToken); // Kurze Pause vor Retry
                try
                {
                    response = await chatService.GetChatMessageContentAsync(chatHistory, executionSettings, kernel); // Retry-Versuch
                }
                catch (ArgumentOutOfRangeException retryEx) when (retryEx.Message.Contains("ChatFinishReason") || retryEx.Message.Contains("Unknown"))
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Retry fehlgeschlagen → Klaerungs-Fallback ({retryEx.Message})");
                    try // Fallback: einfacher Call ohne Tools, Gemini kann nachfragen oder klaeren
                    {
                        var fallbackSettings = new OpenAIPromptExecutionSettings
                        {
                            FunctionChoiceBehavior = FunctionChoiceBehavior.None(), // Kein Tool-Aufruf erlaubt
                            Temperature = 0.3,
                            MaxTokens = 200
                        };
                        var fallbackResponse = await chatService.GetChatMessageContentAsync(chatHistory, fallbackSettings, kernel); // Fallback-LLM-Aufruf ohne Tools
                        if (!string.IsNullOrWhiteSpace(fallbackResponse.Content)) // Antwort vorhanden?
                        {
                            RouterResponse clarifyResponse = new RouterResponse();
                            clarifyResponse.DirectResponse = fallbackResponse.Content.Trim();
                            clarifyResponse.Reason = "Klaerungs-Fallback (kein Tool)";
                            return clarifyResponse;
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Klaerungs-Fallback fehlgeschlagen: {fallbackEx.Message}");
                    }
                    RouterResponse lastResort = new RouterResponse(); // Letzter Ausweg wenn auch Fallback scheitert
                    lastResort.DirectResponse = "Ich habe deine Anfrage nicht ganz verstanden. Kannst du sie anders formulieren?";
                    lastResort.Reason = "ChatFinishReason-Fehler (alle Fallbacks fehlgeschlagen)";
                    return lastResort;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Gemini antwortet → Content: \"{(response.Content ?? "").Substring(0, Math.Min(80, (response.Content ?? "").Length))}\"");

            string responseText = string.Empty;
            if (response.Content != null) responseText = response.Content.Trim(); // Content extrahieren (kann null sein)

            // --- Tool-Calls verarbeiten ---
            var foundDocuments = new List<Document>(); // Gefundene Dokumente für Antwort
            bool hasToolCalls = false; // Marker: LLM wollte Tool aufrufen

            if (response.Items != null) // Antwort enthält Items (Tool-Calls oder Text)?
            {
                foreach (var item in response.Items) // Alle Response-Items durchgehen
                {
                    if (item is Microsoft.SemanticKernel.FunctionCallContent functionCall) // Ist es ein Tool-Call?
                    {
                        hasToolCalls = true;

                        // --- Funktionsname normalisieren: Gemini hängt manchmal Plugin-Prefix an ---
                        // z.B. "Document_remove_document_from_folder" → "remove_document_from_folder"
                        string normalizedFunctionName = NormalizeFunctionName(functionCall.FunctionName);
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Call → {functionCall.PluginName}.{functionCall.FunctionName} (normalisiert: {normalizedFunctionName})");

                        if (normalizedFunctionName == "analyze_document") // Direkt zu DataAnalysis routen
                            return await HandleAnalyzeDocumentAsync(functionCall, docDbService, lastDocIds);

                        if (normalizedFunctionName == "generate_response") // Generative/aufwändige Aufgabe → lokales Modell übernimmt
                            return BuildLocalModelResponse();

                        try
                        {
                            // Plugin-übergreifende Funktionssuche (robust gegen falsche Plugin-Prefixe von Gemini)
                            KernelFunction? fn = FindFunctionInKernel(kernel, normalizedFunctionName);
                            if (fn == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ROUTER] Funktion '{normalizedFunctionName}' nicht gefunden → Fallback");
                                continue; // Nächsten Tool-Call versuchen
                            }

                            var result = await fn.InvokeAsync(kernel, functionCall.Arguments); // Tool ausführen

                            string resultStr = result.ToString() ?? string.Empty; // Ergebnis als String
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Ergebnis ({normalizedFunctionName}): {resultStr.Substring(0, Math.Min(100, resultStr.Length))}");

                            RouterResponse? toolResponse = await DispatchToolResultAsync(
                                normalizedFunctionName, resultStr, docDbService, calendarService, foundDocuments);

                            if (toolResponse != null) return toolResponse; // Handler hat Ergebnis produziert → sofort zurückgeben
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Fehler ({normalizedFunctionName}): {ex.GetType().Name}: {ex.Message}");
                            RouterResponse toolErrorResponse = new RouterResponse();
                            toolErrorResponse.DirectResponse = $"Fehler bei '{normalizedFunctionName}': {ex.Message}. Bitte erneut versuchen.";
                            toolErrorResponse.Reason = "Tool-Fehler";
                            return toolErrorResponse;
                        }
                    }
                }
            }

            // --- Kein Tool: JSON-Route oder direkte Konversations-Antwort ---
            if (responseText.TrimStart().StartsWith("{")) // Antwort beginnt mit '{' → mögliche JSON-Route
            {
                try
                {
                    var routingJson = JsonSerializer.Deserialize<SimpleRoutingJson>(responseText,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); // Case-insensitiv parsen

                    if (routingJson != null && routingJson.route == "dataAnalysis") // DataAnalysis Follow-up
                    {
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] → JSON-Route: dataAnalysis (Follow-up)");

                        var contextDocs = new List<Document>(); // Kontext-Dokumente aus Kontext-DocIDs laden
                        foreach (int docId in lastDocIds)
                        {
                            Document doc = await docDbService.GetDocumentByIdAsync(docId); // Dokument aus DB holen
                            if (doc != null && doc.Id > 0) contextDocs.Add(doc); // Nur gültige Dokumente
                        }

                        RouterResponse dataResponse = new RouterResponse();
                        dataResponse.ShouldRoute = true;
                        dataResponse.TargetService = "dataAnalysis";
                        dataResponse.MaxTokens = 2000;
                        dataResponse.FoundDocuments = contextDocs;
                        dataResponse.Reason = "DataAnalysis (Follow-up)";
                        return dataResponse;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] JSON-Parse-Fehler: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(responseText) && !hasToolCalls) // Direkte Gemini-Antwort
            {
                System.Diagnostics.Debug.WriteLine($"[ROUTER] → Direkte Konversations-Antwort von Gemini");
                RouterResponse convResponse = new RouterResponse();
                convResponse.DirectResponse = responseText;
                convResponse.Reason = "Konversation";
                return convResponse;
            }

            System.Diagnostics.Debug.WriteLine($"[ROUTER] → Fallback (kein Tool, keine Antwort)");
            RouterResponse fallback = new RouterResponse(); // Sicherheits-Fallback
            fallback.DirectResponse = "Wie kann ich dir helfen?";
            fallback.Reason = "Fallback";
            return fallback;
        }
        catch (OperationCanceledException)
        {
            throw; // Abbruch-Signal unverändert weiterleiten
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Kritischer Fehler: {ex.GetType().Name}: {ex.Message}");
            RouterResponse errorResponse = new RouterResponse(); // Nicht werfen — graceful zurückgeben
            errorResponse.DirectResponse = $"Fehler beim Verarbeiten der Anfrage ({ex.GetType().Name}: {ex.Message}). Bitte versuche es erneut.";
            errorResponse.Reason = "Router-Fehler (graceful)";
            return errorResponse;
        }
    }

    private string BuildSystemPrompt(string dayOfWeek, string dateGerman, string time, string dateIso) // Erstellt System-Prompt mit Datum/Uhrzeit
    {
        return
            $"Heute ist {dayOfWeek}, der {dateGerman}, {time} Uhr. ({dateIso})\n" +
            "Antworte immer auf Deutsch. Verwende 'du', nicht 'Sie'.\n\n" +
            "KRITISCH: Du darfst NIEMALS behaupten eine Aktion ausgeführt zu haben ohne vorher das passende Tool aufgerufen zu haben. " +
            "Aktionen wie Kopieren, Verschieben, Erstellen, Löschen IMMER zuerst das Tool aufrufen — danach kommt automatisch die Bestätigung. " +
            "Schreibe KEINE eigene Bestätigung wie 'Ich habe X kopiert' oder 'Erledigt' — nur der Tool-Aufruf zählt.\n\n" +
            "Du hast Zugriff auf verschiedene Tools. Nutze sie anhand ihrer Beschreibung je nach Nutzeranfrage.\n\n" +
            "AUFGABENTEILUNG: Kurze Fakten, Smalltalk und kurze Bestätigungen beantwortest du selbst direkt. " +
            "Für umfangreiche oder generative Aufgaben (Texte/Inhalte verfassen, ausführlich erklären, zusammenfassen, umformulieren, übersetzen, Code schreiben, brainstormen) rufe generate_response auf — das übernimmt dann das leistungsstarke lokale Modell. " +
            "Passt ein konkretes Tool (Kalender, Ordner, Dokumentsuche, Dokument-Analyse), nutze dieses statt generate_response.\n\n" +
            "KALENDER - Termin erstellen: Pflichtfelder = title + date + isAllDay.\n" +
            "isAllDay=true für ganztägig (kein startTime nötig). isAllDay=false für Uhrzeit (dann startTime angeben, z.B. '14:00').\n" +
            "Zeitspanne: isAllDay=false + startTime + endTime. Nur Startzeit: isAllDay=false + startTime ohne endTime.\n" +
            "Alle Pflichtfelder bekannt? → create_calendar_event SOFORT aufrufen.\n" +
            "Fehlende Pflichtfelder kurz erfragen (ein Satz):\n" +
            "  Alle fehlen:  \"Wie heißt der Termin, wann und ganztägig oder welche Uhrzeit?\"\n" +
            "  Titel fehlt:  \"Wie soll der Termin heißen?\"\n" +
            "  Datum fehlt:  \"Fuer welches Datum soll ich den Termin anlegen?\"\n" +
            "  Zeit fehlt:   \"Soll der Termin ganztaegig sein oder zu welcher Uhrzeit?\"\n" +
            "KALENDER - Termine anzeigen: list_calendar_events aufrufen — niemals Termine aus dem Gespraechsverlauf wiederholen ohne erneuten Tool-Aufruf.\n" +
            "KALENDER - Kontext: 'dieser Termin', 'den Termin', 'ihn' → zuletzt erwähnten Termin verwenden. Nur bei echtem Zweifel nachfragen.\n\n" +
            "Dokument-Analyse: Ist eine DocID im CONTEXT sichtbar (z.B. 'CONTEXT: Kürzlich gefundene DocIDs: 7') → analyze_document(docId) aufrufen. Sonst → add_document_to_chat aufrufen.\n\n" +
            "Kopieren zu Root/Hauptbereich: copy_document_to_folder mit folderName='hauptbereich'.\n" +
            "Entfernen aus Root/Hauptbereich: remove_document_from_folder mit folderName='hauptbereich'.\n" +
            "Alle Ordner anzeigen: list_all_folders aufrufen.\n" +
            "Dokumente im Hauptbereich/Root anzeigen: list_documents_in_folder mit folderName='hauptbereich'.\n\n" +
            "Dokument loeschen: remove_document_from_folder aufrufen — Standort (Ordnername oder 'hauptbereich') muss bekannt sein. Ist er unklar: zuerst find_documents aufrufen um zu sehen wo die Datei liegt, dann dem User die Standorte zeigen und fragen von wo er sie entfernen moechte.\n\n" +
            "Fehlende Informationen: Wenn eine Anfrage unklar ist oder Pflichtangaben fehlen, stelle EINE gezielte Rückfrage um die fehlende Information zu erhalten. Nicht raten, nicht ein Tool mit falschen Parametern aufrufen.\n\n" +
            "Konversation ohne Tool-Bezug: kurz und freundlich auf Deutsch antworten (max 2-3 Saetze).";
    }

    private string NormalizeFunctionName(string rawName) // Entfernt Plugin-Prefix den Gemini manchmal fälschlicherweise anhängt
    {
        return RouterFunctionName.Normalize(rawName); // Delegiert an testbare Logik-Klasse
    }

    private KernelFunction? FindFunctionInKernel(Kernel kernel, string functionName) // Sucht Funktion in allen Plugins (robust gegen Plugin-Zuordnungsfehler)
    {
        foreach (var plugin in kernel.Plugins) // Alle registrierten Plugins durchsuchen
        {
            foreach (var func in plugin) // Alle Funktionen im Plugin prüfen
            {
                if (string.Equals(func.Name, functionName, StringComparison.OrdinalIgnoreCase)) // Namen vergleichen (case-insensitiv)
                {
                    return func; // Funktion gefunden
                }
            }
        }
        return null; // Nicht gefunden
    }

}
