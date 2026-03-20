using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using KIDT.Database;

namespace KIDT.Services;

/// <summary>
/// Spezialisierter Service für Daten-Analyse und Text-Verarbeitung.
/// Nutzt qwen2.5:7b für präzise Analysen von hochgeladenen Dateien und Chat-Dokumenten.
/// KEINE Tool-Integration - Tools werden vom RouterService gehandhabt.
/// </summary>
public class DataAnalysisService : IAsyncDisposable // Service für Text-Analyse mit asynchroner Aufräumung
{
    private Kernel kernel = null!; // Semantic Kernel-Instanz für KI (wird in InitializeAsync initialisiert)
    private IChatCompletionService chatService = null!; // Chat-Service von Ollama (wird in InitializeAsync initialisiert)
    private string systemInstructions = string.Empty; // System-Instructions aus data-analysis-instructions.md
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung
    private DocumentDbService docDbService = null!; // Service für Dokumenten-Zugriff (wird in InitializeAsync initialisiert)
    private int currentConversationId = 0; // Aktuelle Conversation-ID

    public async Task InitializeAsync(DocumentDbService documentDbService, int conversationId) // Initialisiere Service
    {
        if (this.isInitialized) // Bereits initialisiert?
        {
            return;
        }

        try
        {
            this.docDbService = documentDbService; // Speichere DocumentDbService
            this.currentConversationId = conversationId; // Speichere Conversation-ID
            
            var builder = Kernel.CreateBuilder(); // Erstelle Kernel-Builder
            builder.Services.AddOpenAIChatCompletion( // Füge Chat-Completion hinzu
                modelId: "qwen2.5:7b",
                apiKey: null,
                endpoint: new Uri("http://localhost:11434/v1")
            );
            this.kernel = builder.Build(); // Baue Kernel

            this.chatService = this.kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "data-analysis-instructions.md"); // Erstelle Pfad zur Instructions-Datei
            this.systemInstructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Instructions aus Datei

            this.isInitialized = true; // Markiere als initialisiert
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von DataAnalysisService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName, int maxTokens) // Sendet Nachricht mit optionalem Datei-Anhang und benutzerdefiniertem MaxTokens
    {
        return await SendAsync(userMessage, fileContent, fileName, maxTokens, string.Empty);
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName, int maxTokens, string recentContext) // Sende Nachricht an KI
    {
        if (this.kernel == null || this.chatService == null) // Service nicht initialisiert?
        {
            return "Fehler: Daten-Analyse-Service nicht initialisiert.";
        }
        
        try
        {
            var chatHistory = new ChatHistory(); // Erstelle neue Chat-History
            chatHistory.AddSystemMessage(this.systemInstructions); // Füge System-Instructions hinzu

            bool hasContext = false; // Variable für Context-Check
            if (!string.IsNullOrEmpty(recentContext)) // Kontext vorhanden?
            {
                hasContext = true;
            }
            
            if (hasContext) // Kontext vorhanden?
            {
                chatHistory.AddSystemMessage($"Letzter Gesprächsverlauf:\n{recentContext}");
            }
            
            string finalMessage = userMessage; // Variable für finale Nachricht
            
            bool hasFile = false; // Variable für Datei-Check
            if (!string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(fileName)) // Datei vorhanden?
            {
                hasFile = true;
            }
            
            if (hasFile) // Datei vorhanden?
            {
                string limitedContent = fileContent; // Variable für Datei-Inhalt
                int maxChars = 5000; // Maximum 5000 Zeichen
                
                if (fileContent.Length > maxChars) // Inhalt zu lang?
                {
                    limitedContent = fileContent.Substring(0, maxChars); // Kürze auf Maximum
                    limitedContent += "\n\n[... Datei gekürzt, nur erste 5000 Zeichen gezeigt ...]"; // Füge Warnung hinzu
                }
                
                finalMessage = $"[Datei: {fileName}]\n\n{limitedContent}\n\n---\n\n{userMessage}";
            }

            chatHistory.AddUserMessage(finalMessage); // Füge User-Nachricht zur History hinzu

            var settings = new OpenAIPromptExecutionSettings(); // Erstelle Settings-Objekt
            settings.Temperature = 0.3; // Setze Temperatur (niedrig = präzise)
            settings.MaxTokens = maxTokens; // Setze Token-Limit
            
            var response = await this.chatService.GetChatMessageContentAsync( // Sende Anfrage an KI
                chatHistory, // Mit Chat-History
                executionSettings: settings, // Mit Settings
                kernel: this.kernel // Mit Kernel
            );

            string assistantMessage; // Variable für Antwort
            if (response.Content != null) // Response hat Content?
            {
                assistantMessage = response.Content; // DIREKT verwenden (kein EnsureUtf8 mehr!)
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

        // Entferne Anführungszeichen am Anfang/Ende
        if (response.StartsWith("\"") && response.EndsWith("\"") && response.Length > 2)
        {
            response = response.Substring(1, response.Length - 2);
        }

        // Entferne gängige Emojis (einfach und schnell, kein Regex!)
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
        return string.IsNullOrWhiteSpace(cleaned) ? "Wie kann ich dir helfen?" : cleaned;
    }
}