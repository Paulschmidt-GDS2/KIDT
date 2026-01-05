using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;

namespace KIDT.Services;

/// <summary>
/// Spezialisierter Service für Daten-Analyse und Tool-Nutzung.
/// Nutzt qwen2.5:7b für präzise Analysen mit MCP-Tools.
/// </summary>
public class DataAnalysisService : IAsyncDisposable // Service für Tool-Nutzung mit asynchroner Aufräumung
{
    private Kernel? kernel; // Semantic Kernel-Instanz für KI (wird später initialisiert)
    private IChatCompletionService? chatService; // Chat-Service von Ollama (wird später initialisiert)
    private string systemInstructions = "Du bist ein Daten-Analyse-Spezialist. Nutze IMMER die verfügbaren Tools für präzise Analysen."; // Standard-Systemanweisung
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung

    public async Task InitializeAsync() // Lädt qwen2.5, registriert MCP-Tools, lädt Instructions aus MD-Datei
    {
        if (this.isInitialized) return; // Wenn schon initialisiert -> raus

        try
        {
            var builder = Kernel.CreateBuilder(); // Erstelle Kernel-Builder
            builder.Services.AddOpenAIChatCompletion( // Füge Chat-Completion hinzu
                modelId: "qwen2.5:7b",
                apiKey: null, // Kein API-Key nötig (Ollama lokal)
                endpoint: new Uri("http://localhost:11434/v1") // Ollama-Endpunkt (OpenAI-kompatibel)
            );
            this.kernel = builder.Build(); // Baue Kernel aus Builder

            McpToolsRegistry.RegisterTools(this.kernel); // Registriere alle MCP-Tools im Kernel

            this.chatService = this.kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "data-analysis-instructions.md"); // Erstelle Pfad zur Instructions-Datei
            this.systemInstructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Instructions aus MD-Datei (UTF-8)

            this.isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von DataAnalysisService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet Nachricht ohne Datei
    {
        return await SendAsync(userMessage, string.Empty, string.Empty, 3000); // Aufruf mit leeren Datei-Parametern und Standard-MaxTokens
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName) // Sendet Nachricht mit optionalem Datei-Anhang
    {
        return await SendAsync(userMessage, fileContent, fileName, 3000); // Aufruf mit Standard-MaxTokens
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName, int maxTokens) // Sendet Nachricht mit optionalem Datei-Anhang und benutzerdefiniertem MaxTokens
    {
        return await SendAsync(userMessage, fileContent, fileName, maxTokens, string.Empty);
    }

    public async Task<string> SendAsync(string userMessage, string fileContent, string fileName, int maxTokens, string recentContext) // Sendet Nachricht mit optionalem Datei-Anhang, benutzerdefiniertem MaxTokens und letztem Gesprächsverlauf
    {
        if (!this.isInitialized) // Wenn Service noch nicht initialisiert ist
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        if (this.kernel == null || this.chatService == null) // Wenn Kernel oder Chat-Service null sind und nicht initialisiert wurden
        {
            return "Fehler: Daten-Analyse-Service nicht initialisiert.";
        }
        
        try
        {
            var chatHistory = new ChatHistory(); // Erstelle neue Chat-History
            chatHistory.AddSystemMessage(this.systemInstructions); // Füge System-Instructions hinzu

            if (!string.IsNullOrEmpty(recentContext)) // Wenn Gesprächsverlauf vorhanden ist
            {
                chatHistory.AddSystemMessage($"Letzter Gesprächsverlauf:\n{recentContext}"); // Füge Kontext als System-Message hinzu
            }
            
            string finalMessage = userMessage; // Baue finale User-Nachricht (Standard: ohne Datei)
            
            if (!string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(fileName)) // Wenn Datei-Inhalt und Name vorhanden sind
            {
                string limitedContent = fileContent; // Standard: Kompletter Inhalt
                int maxChars = 5000; // Maximum 5000 Zeichen (ca. 1000 Wörter)
                
                if (fileContent.Length > maxChars) // Wenn Inhalt zu lang ist
                {
                    limitedContent = fileContent.Substring(0, maxChars); // Schneide ab
                    limitedContent += "\n\n[... Datei gekürzt, nur erste 5000 Zeichen gezeigt ...]"; // Warnung hinzufügen
                }
                
                finalMessage = $"[Datei: {fileName}]\n\n{limitedContent}\n\n---\n\n{userMessage}"; // Füge Datei-Kontext vor User-Nachricht hinzu
            }

            chatHistory.AddUserMessage(finalMessage); // Füge User-Nachricht zur History hinzu

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions, // Aktiviere automatische Tool-Aufrufe
                Temperature = 0.3, // Niedrige Temperatur = präzise, weniger Kreativität
                MaxTokens = maxTokens // Setze dynamisches MaxTokens-Limit
            };
            
            var response = await this.chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                chatHistory, // Mit bisheriger Konversations-History
                executionSettings: settings, // Mit dynamischen Settings
                kernel: this.kernel // Mit MCP-Tools für Analyse
            );

            string assistantMessage;
            if (response.Content != null) // Wenn Response einen Content hat
            {
                assistantMessage = EnsureUtf8(response.Content);
            }
            else // Kein Content
            {
                assistantMessage = "Keine Antwort erhalten."; // Fallback-Nachricht
            }
            
            return assistantMessage; // Gibt Antwort zurück
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
            return text; // Bei Fehler: Original-Text zurückgeben
        }
    }

    public ValueTask DisposeAsync() // Räumt Ressourcen auf (aktuell leer)
    {
        return ValueTask.CompletedTask; // Aktuell nichts zu tun
    }
}