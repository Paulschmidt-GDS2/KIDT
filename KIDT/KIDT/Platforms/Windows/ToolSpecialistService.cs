using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;

namespace KIDT.Services;

/// <summary>
/// Spezialisierter Service für Dokumenten-Analyse und Tool-Nutzung.
/// Nutzt qwen2.5:7b für präzise Analysen mit MCP-Tools.
/// </summary>
public class ToolSpecialistService : IAsyncDisposable // Service für Tool-Nutzung mit asynchroner Aufräumung
{
    private Kernel? kernel; // Semantic Kernel-Instanz für KI (wird später initialisiert)
    private IChatCompletionService? chatService; // Chat-Service von Ollama (wird später initialisiert)
    private ChatHistory chatHistory = new(); // Chat-History: Speichert Konversations-Verlauf
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

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "tool-specialist-instructions.md"); // Erstelle Pfad zur MD-Datei
            string instructions;
            
            if (File.Exists(instructionsPath)) // Existiert Instructions-Datei?
            {
                instructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Lese Datei asynchron mit UTF-8
            }
            else // Datei existiert nicht
            {
                instructions = "Du bist ein Dokumenten-Analyse-Spezialist. Nutze IMMER die verfügbaren Tools für präzise Analysen."; // Fallback-Prompt
            }

            this.chatHistory.AddSystemMessage(instructions); // Füge Instructions als System-Message zur History hinzu
            this.isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ToolSpecialistService: {ex.Message}", ex);
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
            return "Fehler: Tool-Spezialist nicht initialisiert.";
        }
        
        try
        {
            string finalMessage = userMessage; // Baue finale User-Nachricht (Standard: ohne Datei)
            
            if (!string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(fileName)) // Ist Datei angehängt?
            {
                string limitedContent = fileContent; // Standard: Kompletter Inhalt
                int maxChars = 5000; // Maximum 5000 Zeichen (ca. 1000 Wörter)
                
                if (fileContent.Length > maxChars) // Ist Datei zu lang?
                {
                    limitedContent = fileContent.Substring(0, maxChars); // Schneide ab
                    limitedContent += "\n\n[... Datei gekürzt, nur erste 5000 Zeichen gezeigt ...]"; // Warnung hinzufügen
                }
                
                finalMessage = $"[Datei: {fileName}]\n\n{limitedContent}\n\n---\n\n{userMessage}"; // Füge Datei-Kontext vor User-Nachricht hinzu
            }

            this.chatHistory.AddUserMessage(finalMessage); // Füge User-Nachricht zur History hinzu

            var words = finalMessage.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries); // Splitte bei Leerzeichen/Tabs/Newlines
            int wordCount = words.Length; // Zähle Wörter in finaler Nachricht

            int maxTokens; // Variable für MaxTokens-Limit
            
            if (wordCount <= 10) // Sehr kurze Analyse-Anfragen (z.B. "Analysiere: Test")
            {
                maxTokens = 150; // Präzise Kurzanalyse mit Tool-Daten
            }
            else if (wordCount <= 30) // Kurze bis mittlere Anfragen
            {
                maxTokens = 350; // Strukturierte Analyse mit Details
            }
            else if (wordCount <= 100) // Mittlere Anfragen
            {
                maxTokens = 600; // Vollständige Struktur mit ausführlicher Analyse
            }
            else if (wordCount <= 500) // Mittellange Dokumente
            {
                maxTokens = 1200; // Detaillierte Code-Analysen und Zusammenfassungen
            }
            else if (wordCount <= 2000) // Lange Dokumente (Code-PDFs, große TXT-Dateien)
            {
                maxTokens = 2000; // Umfangreiche Analysen mit mehreren Aspekten
            }
            else // Sehr lange Dokumente (große PDFs)
            {
                maxTokens = 3000; // Maximum für tiefgehende Analysen komplexer Dokumente
            }

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions, // Aktiviere automatische Tool-Aufrufe
                Temperature = 0.3, // Niedrige Temperatur = präzise, weniger Kreativität
                MaxTokens = maxTokens // Setze dynamisches MaxTokens-Limit
            };
            
            var response = await this.chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                this.chatHistory, // Mit bisheriger Konversations-History
                executionSettings: settings, // Mit dynamischen Settings
                kernel: this.kernel // Mit MCP-Tools für Analyse
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