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
    private Kernel? kernel; // Semantic Kernel-Instanz für KI (wird später initialisiert)
    private IChatCompletionService? chatService; // Chat-Service von Ollama (wird später initialisiert)
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung
    private string systemInstructions = string.Empty; // System-Instructions (werden bei jedem Call neu verwendet)

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

    public async Task<string> SendAsync(string userMessage, int maxTokens, string recentContext) // Sendet Nachricht mit MaxTokens und Verlauf
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
                assistantMessage = EnsureUtf8(response.Content);
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

    private string EnsureUtf8(string text) // Konvertiert Text zu UTF-8 (falls nötig)
    {
        if (string.IsNullOrEmpty(text)) return text; // Leerer Text -> direkt zurück
        
        try
        {
            var bytes = Encoding.Default.GetBytes(text); // Text -> Bytes (System-Encoding)
            return Encoding.UTF8.GetString(bytes); // Bytes -> UTF-8 String
        }
        catch
        {
            return text;
        }
    }

    public ValueTask DisposeAsync() // Räumt Ressourcen auf (aktuell leer)
    {
        return ValueTask.CompletedTask; // Aktuell nichts zu tun
    }
}