using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Services;

public partial class RouterService // Analysiert User-Nachrichten, ruft Tools auf und routet zu DataAnalysis
{
    private readonly IServiceProvider serviceProvider;
    private string apiKey = string.Empty;

    public RouterService(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public async Task InitializeAsync() // Lädt API-Key aus Datei
    {
        if (this.apiKey.Length > 0) return;
        string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openai-api-key.txt");
        this.apiKey = await File.ReadAllTextAsync(keyPath);
    }

    public async Task<RouterResponse> ProcessAsync(string userMessage, bool hasFile, int conversationId, CancellationToken cancellationToken = default) // Hauptmethode: Verarbeitet Nachricht, führt Tools aus und gibt Routing-Ergebnis zurück
    {
        if (this.apiKey.Length == 0) await InitializeAsync();

        using var scope = this.serviceProvider.CreateScope();
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();
        var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();
        var folderDbService = scope.ServiceProvider.GetRequiredService<FolderDbService>();
        var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>();

        System.Diagnostics.Debug.WriteLine($"[ROUTER] ProcessAsync → Modell: gemini-2.5-flash-lite via OpenRouter, ConvId={conversationId}");

        try
        {
            // --- Kernel aufbauen ---
            var builder = Kernel.CreateBuilder();
            builder.Services.AddOpenAIChatCompletion(
                modelId: "google/gemini-2.5-flash-lite",
                apiKey: this.apiKey.Trim(),
                endpoint: new Uri("https://openrouter.ai/api/v1")
            );
            var kernel = builder.Build();
            McpToolsRegistry.RegisterTools(kernel, docDbService, calendarService, folderDbService, conversationId);
            kernel.ImportPluginFromObject(new AnalysisTools(), "Analysis");

            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            // --- System-Prompt mit aktuellem Datum ---
            DateTime now = DateTime.Now;
            string systemPrompt = BuildSystemPrompt(
                now.ToString("dddd", new System.Globalization.CultureInfo("de-DE")),
                now.ToString("dd.MM.yyyy"), now.ToString("HH:mm"), now.ToString("yyyy-MM-dd"));

            // --- Chat-History aufbauen ---
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(systemPrompt);

            var lastDocIds = new List<int>();

            if (conversationId > 0) // Letzte 10 Nachrichten aus DB laden
            {
                var allMessages = await dbService.LoadMessagesAsync(conversationId);
                var recent = allMessages.Count > 10
                    ? allMessages.Skip(allMessages.Count - 10).ToList()
                    : allMessages;

                foreach (var msg in recent)
                {
                    if (msg.IsUser) chatHistory.AddUserMessage(msg.Text);
                    else chatHistory.AddAssistantMessage(msg.Text);
                }

                foreach (var msg in recent.TakeLast(3)) // DocIDs aus letzten Nachrichten extrahieren
                {
                    if (!msg.IsUser && msg.Text.Contains("[DocID:"))
                        ExtractDocIds(msg.Text, lastDocIds);
                }

                if (lastDocIds.Count > 0)
                    chatHistory.AddSystemMessage($"CONTEXT: Kürzlich gefundene DocIDs: {string.Join(", ", lastDocIds)}");
            }

            chatHistory.AddUserMessage(userMessage);

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
                response = await chatService.GetChatMessageContentAsync(chatHistory, executionSettings, kernel);
            }
            catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason") || ex.Message.Contains("Unknown"))
            {
                System.Diagnostics.Debug.WriteLine($"[ROUTER] SK ChatFinishReason-Fehler, Retry nach 300ms... ({ex.Message})");
                await Task.Delay(300, cancellationToken); // Kurze Pause vor Retry
                try
                {
                    response = await chatService.GetChatMessageContentAsync(chatHistory, executionSettings, kernel);
                }
                catch (ArgumentOutOfRangeException retryEx) when (retryEx.Message.Contains("ChatFinishReason") || retryEx.Message.Contains("Unknown"))
                {
                    System.Diagnostics.Debug.WriteLine($"[ROUTER] Retry fehlgeschlagen → Fallback ({retryEx.Message})");
                    RouterResponse retryFallback = new RouterResponse(); // Freundliche Rückmeldung statt rohem Fehler
                    retryFallback.DirectResponse = "Das Modell ist gerade überlastet. Bitte versuche es in einem Moment erneut.";
                    retryFallback.Reason = "ChatFinishReason-Fehler (nach Retry)";
                    return retryFallback;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            System.Diagnostics.Debug.WriteLine($"[ROUTER] Gemini antwortet → Content: \"{(response.Content ?? "").Substring(0, Math.Min(80, (response.Content ?? "").Length))}\"");

            string responseText = string.Empty;
            if (response.Content != null) responseText = response.Content.Trim();

            // --- Tool-Calls verarbeiten ---
            var foundDocuments = new List<Document>();
            bool hasToolCalls = false;

            if (response.Items != null)
            {
                foreach (var item in response.Items)
                {
                    if (item is Microsoft.SemanticKernel.FunctionCallContent functionCall)
                    {
                        hasToolCalls = true;

                        // --- Funktionsname normalisieren: Gemini hängt manchmal Plugin-Prefix an ---
                        // z.B. "Document_remove_document_from_folder" → "remove_document_from_folder"
                        string normalizedFunctionName = NormalizeFunctionName(functionCall.FunctionName);
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Call → {functionCall.PluginName}.{functionCall.FunctionName} (normalisiert: {normalizedFunctionName})");

                        if (normalizedFunctionName == "analyze_document") // Direkt zu DataAnalysis routen
                            return await HandleAnalyzeDocumentAsync(functionCall, docDbService, lastDocIds);

                        try
                        {
                            // Plugin-übergreifende Funktionssuche (robust gegen falsche Plugin-Prefixe von Gemini)
                            KernelFunction? fn = FindFunctionInKernel(kernel, normalizedFunctionName);
                            if (fn == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ROUTER] Funktion '{normalizedFunctionName}' nicht gefunden → Fallback");
                                continue; // Nächsten Tool-Call versuchen
                            }

                            var result = await fn.InvokeAsync(kernel, functionCall.Arguments);

                            string resultStr = result.ToString() ?? string.Empty;
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Ergebnis ({normalizedFunctionName}): {resultStr.Substring(0, Math.Min(100, resultStr.Length))}");

                            RouterResponse? toolResponse = await DispatchToolResultAsync(
                                normalizedFunctionName, resultStr, docDbService, calendarService, foundDocuments);

                            if (toolResponse != null) return toolResponse;
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
            if (responseText.TrimStart().StartsWith("{"))
            {
                try
                {
                    var routingJson = JsonSerializer.Deserialize<SimpleRoutingJson>(responseText,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (routingJson != null && routingJson.route == "dataAnalysis") // DataAnalysis Follow-up
                    {
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] → JSON-Route: dataAnalysis (Follow-up)");

                        var contextDocs = new List<Document>();
                        foreach (int docId in lastDocIds)
                        {
                            Document doc = await docDbService.GetDocumentByIdAsync(docId);
                            if (doc != null && doc.Id > 0) contextDocs.Add(doc);
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
            "KRITISCH: Du darfst NIEMALS behaupten eine Aktion ausgefuehrt zu haben ohne vorher das passende Tool aufgerufen zu haben. " +
            "Aktionen wie Kopieren, Verschieben, Erstellen, Loeschen IMMER zuerst das Tool aufrufen — danach kommt automatisch die Bestaetigung. " +
            "Schreibe KEINE eigene Bestaetigung wie 'Ich habe X kopiert' oder 'Erledigt' — nur der Tool-Aufruf zaehlt.\n\n" +
            "Du hast Zugriff auf verschiedene Tools. Nutze sie anhand ihrer Beschreibung je nach Nutzeranfrage.\n\n" +
            "Kalender: Wenn Titel, Datum und Zeit bekannt sind, erstelle den Termin sofort ohne Rückfrage. Fehlende Pflichtfelder (Titel, Datum, Zeit) kurz erfragen.\n\n" +
            "Dokument-Analyse: Ist eine DocID im CONTEXT sichtbar (z.B. 'CONTEXT: Kürzlich gefundene DocIDs: 7') → analyze_document(docId) aufrufen. Sonst → add_document_to_chat aufrufen.\n\n" +
            "Kopieren zu Root/Hauptbereich: copy_document_to_folder mit folderName='hauptbereich'.\n" +
            "Entfernen aus Root/Hauptbereich: remove_document_from_folder mit folderName='hauptbereich'.\n" +
            "Alle Ordner anzeigen: list_all_folders aufrufen.\n" +
            "Dokumente im Hauptbereich/Root anzeigen: list_documents_in_folder mit folderName='hauptbereich'.\n\n" +
            "Konversation ohne Tool-Bezug: kurz und freundlich auf Deutsch antworten (max 2-3 Saetze).";
    }

    private string NormalizeFunctionName(string rawName) // Entfernt Plugin-Prefix den Gemini manchmal fälschlicherweise anhängt
    {
        // Gemini gibt manchmal "Document_remove_document_from_folder" statt "remove_document_from_folder"
        string[] knownPrefixes = { "Document_", "Folder_", "Calendar_", "Analysis_" };
        foreach (string prefix in knownPrefixes)
        {
            if (rawName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return rawName.Substring(prefix.Length); // Prefix entfernen
            }
        }
        return rawName; // Kein Prefix → unverändert
    }

    private KernelFunction? FindFunctionInKernel(Kernel kernel, string functionName) // Sucht Funktion in allen Plugins (robust gegen Plugin-Zuordnungsfehler)
    {
        foreach (var plugin in kernel.Plugins) // Alle registrierten Plugins durchsuchen
        {
            foreach (var func in plugin)
            {
                if (string.Equals(func.Name, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    return func; // Funktion gefunden
                }
            }
        }
        return null; // Nicht gefunden
    }

    private void ExtractDocIds(string text, List<int> docIds) // Extrahiert DocIDs aus '[DocID: x,y]'-Markern
    {
        int startIdx = text.IndexOf("[DocID:") + 7;
        int endIdx = text.IndexOf("]", startIdx);
        if (startIdx > 6 && endIdx > startIdx)
        {
            foreach (var idStr in text.Substring(startIdx, endIdx - startIdx).Trim().Split(','))
            {
                if (int.TryParse(idStr.Trim(), out int docId)) docIds.Add(docId);
            }
        }
    }
}
