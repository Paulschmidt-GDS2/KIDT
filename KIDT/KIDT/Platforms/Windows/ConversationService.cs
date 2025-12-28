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
public class ConversationService : IAsyncDisposable // Implementiert async Dispose-Pattern
{
    private Kernel? _kernel; // Nullable: Semantic Kernel-Instanz für KI
    private IChatCompletionService? _chatService; // Nullable: Chat-Service von Ollama
    private ChatHistory _chatHistory = new(); // Chat-History: Speichert Konversations-Verlauf
    private bool _isInitialized = false; // Flag: Verhindert mehrfache Initialisierung

    public async Task InitializeAsync() // Lädt phi3:mini, lädt Instructions aus MD-Datei (ohne Tools)
    {
        if (_isInitialized) return; // Guard: Wenn schon initialisiert ? raus

        try
        {
            var builder = Kernel.CreateBuilder(); // Erstelle Kernel-Builder
            builder.Services.AddOpenAIChatCompletion( // Füge Chat-Completion hinzu
                modelId: "phi3:mini",
                apiKey: null, // Kein API-Key nötig (Ollama lokal)
                endpoint: new Uri("http://localhost:11434/v1") // Ollama-Endpunkt (OpenAI-kompatibel)
            );
            _kernel = builder.Build(); // Baue Kernel aus Builder

            Debug.WriteLine("[Conversation] Kernel erfolgreich erstellt."); // Log: Kernel erstellt

            _chatService = _kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "conversation-instructions.md"); // Erstelle Pfad zur MD-Datei
            string instructions; // Variable für Instructions
            
            if (File.Exists(instructionsPath)) // Check: Existiert Instructions-Datei?
            {
                instructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Ja ? Lese Datei asynchron mit UTF-8
                Debug.WriteLine("[Conversation] Custom Instructions geladen."); // Log: Erfolgreich geladen
            }
            else // Datei existiert nicht
            {
                instructions = "Du bist ein freundlicher Chat-Assistent. Sei kurz und natürlich."; // Fallback-Prompt
                Debug.WriteLine("[Conversation] WARNUNG: Instructions-Datei nicht gefunden. Fallback verwendet."); // Log: Warnung + Fallback
            }

            _chatHistory.AddSystemMessage(instructions); // Füge Instructions als System-Message zur History hinzu

            _isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ConversationService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet Nachricht an phi3:mini mit dynamischem MaxTokens (Temperature 0.5)
    {
        if (!_isInitialized) // Check: Ist Service initialisiert?
        {
            await InitializeAsync(); // Nein ? Initialisiere jetzt
        }

        if (_kernel == null || _chatService == null) // Check: Sind Kernel & Service verfügbar?
        {
            return "Fehler: Konversations-Service nicht initialisiert."; // Nein ? Fehler zurück
        }

        try
        {
            _chatHistory.AddUserMessage(userMessage); // Füge User-Nachricht zur History hinzu

            // Dynamisches MaxTokens: Kurze Fragen ? kurze Antworten
            var wordCount = userMessage.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length; // Zähle Wörter in User-Nachricht
            var maxTokens = wordCount < 10 ? 150 : 500; // Wenn < 10 Wörter ? 150 Tokens, sonst 500

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                Temperature = 0.5, // Mittlere Temperatur = ausgewogen zwischen Präzision und Kreativität
                MaxTokens = maxTokens // Dynamisch: Kurze Fragen = kurze Antworten
            };

            var response = await _chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                _chatHistory, // Mit bisheriger Konversations-History
                executionSettings: settings, // Mit dynamischen Settings
                kernel: _kernel // Ohne MCP-Tools (nur Konversation)
            );

            var assistantMessage = response.Content ?? "Keine Antwort erhalten."; // Extrahiere Content, Fallback wenn null
            _chatHistory.AddMessage(response.Role, assistantMessage); // Füge Antwort zur History hinzu

            Debug.WriteLine($"[Conversation] Antwort generiert: {assistantMessage.Length} Zeichen (MaxTokens: {maxTokens})"); // Log: Antwort-Länge + MaxTokens
            return assistantMessage; // Gib Antwort zurück
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
}
