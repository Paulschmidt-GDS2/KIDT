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
    private ChatHistory chatHistory = new(); // Chat-History: Speichert Konversations-Verlauf
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung

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
            string instructions;
            
            if (File.Exists(instructionsPath)) // Existiert Instructions-Datei?
            {
                instructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Datei asynchron mit UTF-8
                Debug.WriteLine("[Conversation] Custom Instructions geladen.");
            }
            else // Datei existiert nicht
            {
                instructions = "Du bist ein freundlicher Chat-Assistent. Sei kurz und natürlich."; // Fallback-Prompt
                Debug.WriteLine("[Conversation] WARNUNG: Instructions-Datei nicht gefunden. Fallback verwendet.");
            }

            this.chatHistory.AddSystemMessage(instructions); // Füge Instructions als System-Message zur History hinzu

            this.isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ConversationService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet Nachricht ohne Datei
    {
        return await SendAsync(userMessage, string.Empty, string.Empty); // Aufruf mit leeren Datei-Parametern
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName) // Sendet Nachricht mit optionalem Datei-Anhang
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
            string finalMessage = userMessage; // Baue finale User-Nachricht (Standard: ohne Datei)
            
            if (!string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(fileName)) // Ist Datei angehängt?
            {
                finalMessage = $"[Datei: {fileName}]\n\n{fileContent}\n\n---\n\n{userMessage}"; // Füge Datei-Kontext vor User-Nachricht hinzu
                Debug.WriteLine($"[Conversation] Datei angehängt: {fileName} ({fileContent.Length} Zeichen)");
            }

            this.chatHistory.AddUserMessage(finalMessage); // Füge User-Nachricht zur History hinzu

            var words = finalMessage.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries); // Splitte bei Leerzeichen/Tabs/Newlines
            int wordCount = words.Length; // Zähle Wörter in finaler Nachricht

            int maxTokens; // Variable für MaxTokens-Limit
            
            if (wordCount <= 5) // Sehr kurze Nachrichten (Hallo, Hi, Danke)
            {
                maxTokens = 20; // Sehr kurze Antworten: ~5-10 Wörter
            }
            else if (wordCount <= 15) // Kurze Fragen
            {
                maxTokens = 80; // Kurze Antworten: ~15-20 Wörter (1-2 Sätze)
            }
            else if (wordCount <= 50) // Mittlere Fragen
            {
                maxTokens = 200; // Mittlere Antworten: ~40-60 Wörter (3-5 Sätze)
            }
            else if (wordCount <= 500) // Mittellange Dokumente
            {
                maxTokens = 400; // Ausführliche Antworten: ~80-120 Wörter
            }
            else // Sehr lange Dokumente (PDFs)
            {
                maxTokens = 800; // Mehr Platz für Zusammenfassungen
            }

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                Temperature = 0.5, // Mittlere Temperatur = ausgewogen zwischen Präzision und Kreativität
                MaxTokens = maxTokens // Dynamisch: Kurze Fragen = kurze Antworten
            };

            var response = await this.chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                this.chatHistory, // Mit bisheriger Konversations-History
                executionSettings: settings, // Mit dynamischen Settings
                kernel: this.kernel // Ohne MCP-Tools (nur Konversation)
            );

            string assistantMessage;
            if (response.Content != null) // Hat Response einen Content?
            {
                assistantMessage = response.Content; // Nehme Response direkt (Ollama liefert bereits UTF-8)
            }
            else // Kein Content
            {
                assistantMessage = "Keine Antwort erhalten."; // Fallback-Nachricht
            }
            
            this.chatHistory.AddMessage(response.Role, assistantMessage); // Füge Antwort zur History hinzu

            Debug.WriteLine($"[Conversation] Antwort generiert: {assistantMessage.Length} Zeichen (MaxTokens: {maxTokens}, Wörter: {wordCount})");
            return assistantMessage; // Gibt Antwort zurück
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