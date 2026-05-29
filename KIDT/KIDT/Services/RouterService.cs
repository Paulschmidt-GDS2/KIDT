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

    public async Task<RouterResponse> ProcessAsync(string userMessage, bool hasFile, int conversationId) // Hauptmethode: Verarbeitet Nachricht, führt Tools aus und gibt Routing-Ergebnis zurück
    {
        if (this.apiKey.Length == 0) await InitializeAsync();

        using var scope = this.serviceProvider.CreateScope();
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();
        var calendarService = scope.ServiceProvider.GetRequiredService<CalendarService>();
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
            McpToolsRegistry.RegisterTools(kernel, docDbService, calendarService, conversationId);
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

            var executionSettings = new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions,
                Temperature = 0.1,
                MaxTokens = 2000
            };

            System.Diagnostics.Debug.WriteLine($"[ROUTER] Gemini aufgerufen → Nachricht: \"{userMessage.Substring(0, Math.Min(60, userMessage.Length))}\"");
            var response = await chatService.GetChatMessageContentAsync(chatHistory, executionSettings, kernel);
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
                        System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Call → {functionCall.PluginName}.{functionCall.FunctionName}");

                        if (functionCall.FunctionName == "analyze_document") // Direkt zu DataAnalysis routen
                            return await HandleAnalyzeDocumentAsync(functionCall, docDbService, lastDocIds);

                        try
                        {
                            var fn = kernel.Plugins.GetFunction(functionCall.PluginName, functionCall.FunctionName);
                            var result = await fn.InvokeAsync(kernel, functionCall.Arguments);
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Ergebnis ({functionCall.FunctionName}): {result.ToString().Substring(0, Math.Min(100, result.ToString().Length))}");

                            RouterResponse? toolResponse = await DispatchToolResultAsync(
                                functionCall.FunctionName, result.ToString(), docDbService, calendarService, foundDocuments);

                            if (toolResponse != null) return toolResponse;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROUTER] Tool-Fehler: {ex.Message}");
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
        catch (Exception ex)
        {
            throw new Exception($"Router-Fehler: {ex.Message}");
        }
    }

    private string BuildSystemPrompt(string dayOfWeek, string dateGerman, string time, string dateIso) // Erstellt System-Prompt mit Datum/Uhrzeit
    {
        return
            $"Heute ist {dayOfWeek}, der {dateGerman}, {time} Uhr. ({dateIso})\n" +
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
