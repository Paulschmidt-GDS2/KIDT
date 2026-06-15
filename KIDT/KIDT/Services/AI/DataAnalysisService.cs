using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using KIDT.Services.Logic;

namespace KIDT.Services;

public class DataAnalysisService : IAsyncDisposable // Analyse-Service: nutzt qwen3.5:9b für Dokument-Analyse via Ollama
{
    private Kernel kernel = null!;
    private IChatCompletionService chatService = null!;
    private string systemInstructions = string.Empty;
    private bool isInitialized = false;

    public async Task InitializeAsync() // Lädt qwen3.5:9b und Instructions-Datei
    {
        if (this.isInitialized) return;

        try
        {
            var builder = Kernel.CreateBuilder();
            builder.Services.AddOpenAIChatCompletion(
                modelId: "qwen3.5:9b",
                apiKey: "ollama",
                endpoint: new Uri("http://localhost:11434/v1")
            );
            this.kernel = builder.Build();
            this.chatService = this.kernel.GetRequiredService<IChatCompletionService>();

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "data-analysis-instructions.md");
            this.systemInstructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8);

            this.isInitialized = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von DataAnalysisService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync( // Sendet Analyse-Anfrage mit optionalem Datei-Inhalt an qwen3.5:9b
        string userMessage, string fileContent, string fileName,
        int maxTokens, string recentContext, bool deepThink = false,
        CancellationToken cancellationToken = default)
    {
        if (this.kernel == null || this.chatService == null)
            return "Fehler: Daten-Analyse-Service nicht initialisiert.";

        try
        {
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(this.systemInstructions);

            if (!string.IsNullOrEmpty(recentContext)) // Kontext vorhanden?
                chatHistory.AddSystemMessage($"Letzter Gesprächsverlauf:\n{recentContext}");

            string finalMessage = userMessage; // Nachricht aufbauen

            if (!string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(fileName)) // Datei-Inhalt vorhanden?
            {
                string limitedContent = fileContent;
                if (fileContent.Length > 5000) // Auf 5000 Zeichen begrenzen
                {
                    limitedContent = fileContent.Substring(0, 5000);
                    limitedContent += "\n\n[... Datei gekürzt, nur erste 5000 Zeichen gezeigt ...]";
                }
                finalMessage = $"[Datei: {fileName}]\n\n{limitedContent}\n\n---\n\n{userMessage}";
            }

            chatHistory.AddUserMessage(finalMessage);

            var settings = new OpenAIPromptExecutionSettings();
            settings.Temperature = 0.3;
            settings.MaxTokens = maxTokens;
            settings.ExtensionData = new System.Collections.Generic.Dictionary<string, object>();

            if (deepThink) // Deep Think: Volles Reasoning einschalten
                settings.ExtensionData["reasoning_effort"] = "high";
            else // Normal-Modus: Thinking ausschalten
                settings.ExtensionData["reasoning_effort"] = "none";

            var response = await this.chatService.GetChatMessageContentAsync(
                chatHistory, executionSettings: settings, kernel: this.kernel, cancellationToken: cancellationToken);

            string assistantMessage;
            if (response.Content != null)
            {
                assistantMessage = LlmResponseCleaner.Clean(response.Content); // Bereinigt lokale LLM-Antwort
            }
            else
            {
                assistantMessage = "Keine Antwort erhalten.";
            }

            return assistantMessage;
        }
        catch (OperationCanceledException)
        {
            throw; // Abbruch durch Abort-Button weiterleiten
        }
        catch (Exception ex)
        {
            return $"Fehler: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() // Ressourcen aufräumen (aktuell leer)
    {
        return ValueTask.CompletedTask;
    }
}
