using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;

namespace KIDT.Services;

/// <summary>
/// Spezialisierter Service für natürliche Konversation.
/// Nutzt phi3:mini für schnelle, freundliche Gespräche.
/// </summary>
public class ConversationService : IAsyncDisposable // Konversations-Service mit asynchroner Aufräumung
{
    private Kernel kernel = null!;
    private IChatCompletionService chatService = null!;
    private bool isInitialized = false;
    private string systemInstructions = string.Empty;

    public async Task InitializeAsync() // Lädt phi3:mini, lädt Instructions aus MD-Datei
    {
        if (this.isInitialized) return; // Wenn schon initialisiert -> raus

        try
        {
            var builder = Kernel.CreateBuilder(); // Erstelle Kernel-Builder
            builder.Services.AddOpenAIChatCompletion( // Füge Chat-Completion hinzu
                modelId: "phi3:mini",
                apiKey: null,
                endpoint: new Uri("http://localhost:11434/v1")
            );
            this.kernel = builder.Build(); // Baue Kernel aus Builder

            this.chatService = this.kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "conversation-instructions.md"); // Erstelle Pfad zur Instructions-Datei
            this.systemInstructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Instructions aus MD-Datei (UTF-8)

            this.isInitialized = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ConversationService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage, int maxTokens, string recentContext) // Sendet Nachricht mit MaxTokens und Verlauf (Non-Streaming, Fallback)
    {
        if (!this.isInitialized) // Wenn Service noch nicht initialisiert ist
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        if (this.kernel == null || this.chatService == null) // Wenn Kernel oder Chat-Service null sind und nicht initialisiert wurden
        {
            return "Fehler: Konversations-Service nicht initialisiert.";
        }

        try
        {
            var chatHistory = new ChatHistory(); // Erstelle frische Chat-History für jeden Call
            chatHistory.AddSystemMessage(this.systemInstructions); // Füge System-Instructions hinzu

            if (!string.IsNullOrEmpty(recentContext)) // Wenn Verlauf vorhanden ist
            {
                chatHistory.AddSystemMessage($"Bisheriger Gesprächsverlauf:\n{recentContext}");
            }

            chatHistory.AddUserMessage(userMessage);

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                Temperature = 0.5, // Mittlere Temperatur = ausgewogen zwischen Präzision und Kreativität
                MaxTokens = maxTokens // Dynamisch: Kurze Fragen = kurze Antworten
            };

            var response = await this.chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                chatHistory, // Mit frischer History (kein alter Kontext)
                executionSettings: settings, // Mit dynamischen Settings
                kernel: this.kernel // Ohne MCP-Tools (nur Konversation)
            );

            string assistantMessage;
            if (response.Content != null) // Wenn Response einen Content hat
            {
                assistantMessage = response.Content;
                assistantMessage = CleanLlmResponse(assistantMessage); // Bereinige Antwort von Debug-Informationen
            }
            else // Kein Content
            {
                assistantMessage = "Keine Antwort erhalten.";
            }

            return assistantMessage;
        }
        catch (Exception ex)
        {
            return $"Fehler: {ex.Message}";
        }
    }

    public async IAsyncEnumerable<string> SendStreamAsync(string userMessage, int maxTokens, string recentContext) // Sendet Nachricht mit STREAMING (Token für Token!)
    {
        if (!this.isInitialized) // Wenn Service noch nicht initialisiert ist
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        if (this.kernel == null || this.chatService == null) // Wenn Kernel oder Chat-Service null sind
        {
            yield return "Fehler: Konversations-Service nicht initialisiert."; // Fehler als Stream zurückgeben
            yield break; // Stream beenden
        }

        var chatHistory = new ChatHistory(); // Erstelle frische Chat-History

        chatHistory.AddSystemMessage(this.systemInstructions); // Füge System-Instructions hinzu (OHNE gefährliche Marker!)

        if (!string.IsNullOrEmpty(recentContext)) // Wenn Verlauf vorhanden ist
        {
            chatHistory.AddSystemMessage($"Bisheriger Gesprächsverlauf:\n{recentContext}");
        }

        chatHistory.AddUserMessage(userMessage);

        var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
        {
            Temperature = 0.5, // Mittlere Temperatur
            MaxTokens = maxTokens // Token-Limit
        };

        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] === SENDING TO PHI3:MINI ===");
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] User Message: {userMessage}");
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] Max Tokens: {maxTokens}");
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] Has Context: {!string.IsNullOrEmpty(recentContext)}");
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] System Instructions Length: {this.systemInstructions.Length} chars");

        // STREAMING: Token für Token, aber sammle für Post-Processing
        StringBuilder fullResponse = new StringBuilder();
        bool hasStartedOutput = false;

        await foreach (var chunk in this.chatService.GetStreamingChatMessageContentsAsync(
            chatHistory,
            executionSettings: settings,
            kernel: this.kernel))
        {
            if (chunk.Content != null)
            {
                fullResponse.Append(chunk.Content);

                // Wenn noch nicht ausgegeben: Prüfe ob wir echten Content haben (keine Quotes am Anfang)
                if (!hasStartedOutput)
                {
                    string currentText = fullResponse.ToString().TrimStart();

                    // Überspringe führende Quotes
                    if (currentText.StartsWith("\""))
                    {
                        continue; // Sammle weiter
                    }

                    hasStartedOutput = true;
                }

                // Gib Chunk DIREKT aus (echtes Streaming!)
                if (hasStartedOutput)
                {
                    yield return chunk.Content;
                }
            }
        }

        string rawResponse = fullResponse.ToString();
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] === RAW RESPONSE FROM PHI3:MINI ===");
        System.Diagnostics.Debug.WriteLine(rawResponse);
        System.Diagnostics.Debug.WriteLine($"[CONVERSATION] === END RAW RESPONSE ===");
    }

    public ValueTask DisposeAsync() // Räumt Ressourcen auf (aktuell leer)
    {
        return ValueTask.CompletedTask; // Aktuell nichts zu tun
    }

    private static string CleanLlmResponse(string response) // Bereinigt LLM-Antworten von Debug-Informationen
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return response;
        }

        response = response.Trim();

        // Entferne Anführungszeichen am Anfang/Ende (einfach und schnell)
        if (response.StartsWith("\"") && response.EndsWith("\"") && response.Length > 2)
        {
            response = response.Substring(1, response.Length - 2);
        }

        // Entferne Emojis (nur gängigste Unicode-Bereiche, VEREINFACHT)
        // Verzichte auf komplexen Regex - zu langsam!
        response = response
            .Replace("✅", "")
            .Replace("❌", "")
            .Replace("🎉", "")
            .Replace("⚠️", "")
            .Replace("✓", "")
            .Replace("→", "");

        // Entferne bekannte Debug-Marker
        string[] debugMarkers = new[]
        {
            "=== Input:",
            "=== Output:",
            "DENSE_CATEGORIES",
            "### QUERY REPORT",
            "As a solution",
            "--- Begin of code"
        };

        bool hasDebugContent = false;
        foreach (var marker in debugMarkers)
        {
            if (response.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                hasDebugContent = true;
                break;
            }
        }

        if (!hasDebugContent)
        {
            return response.Trim();
        }

        // Suche nach echtem Text nach Debug-Bereich
        string[] lines = response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        StringBuilder cleanedResponse = new StringBuilder();
        bool skipMode = false;

        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.All(c => c == '='))
            {
                continue;
            }

            bool isDebugLine = false;
            foreach (var marker in debugMarkers)
            {
                if (trimmedLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    isDebugLine = true;
                    skipMode = true;
                    break;
                }
            }

            if (!isDebugLine && !skipMode)
            {
                cleanedResponse.AppendLine(line);
            }
            else if (!isDebugLine && skipMode && trimmedLine.Length > 10)
            {
                skipMode = false;
                cleanedResponse.AppendLine(line);
            }
        }

        string cleaned = cleanedResponse.ToString().Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Hallo! Wie kann ich dir helfen?" : cleaned;
    }
}