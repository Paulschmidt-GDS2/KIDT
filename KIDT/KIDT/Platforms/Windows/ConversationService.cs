using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics;
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
                apiKey: null, // Kein API-Key nötig (Ollama lokal)
                endpoint: new Uri("http://localhost:11434/v1") // Ollama-Endpunkt (OpenAI-kompatibel)
            );
            this.kernel = builder.Build(); // Baue Kernel aus Builder

            Debug.WriteLine("[Conversation] Kernel erfolgreich erstellt.");

            this.chatService = this.kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "conversation-instructions.md"); // Erstelle Pfad zur MD-Datei
            
            if (File.Exists(instructionsPath)) // Existiert Instructions-Datei?
            {
                this.systemInstructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Datei asynchron mit UTF-8
                Debug.WriteLine("[Conversation] Custom Instructions geladen.");
            }
            else // Datei existiert nicht
            {
                this.systemInstructions = "Du bist ein freundlicher Chat-Assistent. Sei kurz und natürlich."; // Fallback-Prompt
                Debug.WriteLine("[Conversation] WARNUNG: Instructions-Datei nicht gefunden. Fallback verwendet.");
            }

            this.isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ConversationService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet Nachricht ohne Datei
    {
        return await SendAsync(userMessage, string.Empty, string.Empty, 150); // Aufruf mit leeren Datei-Parametern
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName) // Sendet Nachricht mit optionalem Datei-Anhang
    {
        return await SendAsync(userMessage, fileContent, fileName, 150);
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName, int maxTokens) // Sendet Nachricht mit optionalem Datei-Anhang und MaxTokens
    {
        if (!this.isInitialized) // Ist Service initialisiert?
        {
            await InitializeAsync(); // Nein -> Initialisiere jetzt
        }

        if (this.kernel == null || this.chatService == null) // Sind Kernel & Service verfügbar?
        {
            return "Fehler: Konversations-Service nicht initialisiert.";
        }

        try
        {
            var chatHistory = new ChatHistory(); // Erstelle frische Chat-History für jeden Call
            chatHistory.AddSystemMessage(this.systemInstructions); // Füge System-Instructions hinzu
            chatHistory.AddUserMessage(userMessage); // Füge User-Nachricht hinzu

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
            if (response.Content != null) // Hat Response einen Content?
            {
                assistantMessage = EnsureUtf8(response.Content);
            }
            else // Kein Content
            {
                assistantMessage = "Keine Antwort erhalten."; // Fallback-Nachricht
            }

            Debug.WriteLine($"[Conversation] Antwort generiert: {assistantMessage.Length} Zeichen (MaxTokens: {maxTokens})");
            return assistantMessage; // Gibt Antwort zurück
        }
        catch (Exception ex)
        {
            return $"Fehler: {ex.Message}";
        }
    }

    private string EnsureUtf8(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        try
        {
            var bytes = Encoding.Default.GetBytes(text);
            return Encoding.UTF8.GetString(bytes);
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