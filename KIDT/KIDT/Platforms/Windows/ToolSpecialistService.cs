using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics;
using System.Text;

namespace KIDT.Services;

/// <summary>
/// Spezialisierter Service für Dokumenten-Analyse und Tool-Nutzung.
/// Nutzt qwen2.5:7b für präzise Analysen mit MCP-Tools.
/// </summary>
public class ToolSpecialistService : IAsyncDisposable // Implementiert async Dispose-Pattern
{
    private Kernel? _kernel; // Nullable: Semantic Kernel-Instanz für KI
    private IChatCompletionService? _chatService; // Nullable: Chat-Service von Ollama
    private ChatHistory _chatHistory = new(); // Chat-History: Speichert Konversations-Verlauf
    private bool _isInitialized = false; // Flag: Verhindert mehrfache Initialisierung

    public async Task InitializeAsync() // Lädt qwen2.5, registriert MCP-Tools, lädt Instructions aus MD-Datei
    {
        if (_isInitialized) return; // Guard: Wenn schon initialisiert ? raus

        try
        {
            var builder = Kernel.CreateBuilder(); // Erstelle Kernel-Builder
            builder.Services.AddOpenAIChatCompletion( // Füge Chat-Completion hinzu
                modelId: "qwen2.5:7b",
                apiKey: null, // Kein API-Key nötig (Ollama lokal)
                endpoint: new Uri("http://localhost:11434/v1") // Ollama-Endpunkt (OpenAI-kompatibel)
            );
            _kernel = builder.Build(); // Baue Kernel aus Builder

            McpToolsRegistry.RegisterTools(_kernel); // Registriere alle MCP-Tools im Kernel
            
            Debug.WriteLine("[ToolSpecialist] MCP-Tools erfolgreich registriert."); // Log: Tools geladen

            _chatService = _kernel.GetRequiredService<IChatCompletionService>(); // Hole Chat-Service aus Kernel

            var instructionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Prompts", "tool-specialist-instructions.md"); // Erstelle Pfad zur MD-Datei
            string instructions;
            
            if (File.Exists(instructionsPath)) // Check: Existiert Instructions-Datei?
            {
                instructions = await File.ReadAllTextAsync(instructionsPath, Encoding.UTF8); // Ja ? Lese Datei asynchron mit UTF-8
                Debug.WriteLine("[ToolSpecialist] Custom Instructions geladen."); // Log: Erfolgreich geladen
            }
            else // Datei existiert nicht
            {
                instructions = "Du bist ein Dokumenten-Analyse-Spezialist. Nutze IMMER die verfügbaren Tools für präzise Analysen."; // Fallback-Prompt
                Debug.WriteLine("[ToolSpecialist] WARNUNG: Instructions-Datei nicht gefunden. Fallback verwendet."); // Log: Warnung + Fallback
            }

            _chatHistory.AddSystemMessage(instructions); // Füge Instructions als System-Message zur History hinzu

            _isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ToolSpecialistService: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet Nachricht an qwen2.5 mit Tool-Auto-Invoke
    {
        if (!_isInitialized) // Check: Ist Service initialisiert?
        {
            await InitializeAsync(); // Nein ? Initialisiere jetzt
        }

        if (_kernel == null || _chatService == null) // Check: Sind Kernel & Service verfügbar?
        {
            return "Fehler: Tool-Spezialist nicht initialisiert."; // Nein ? Fehler zurück
        }

        try
        {
            _chatHistory.AddUserMessage(userMessage); // Füge User-Nachricht zur History hinzu

            var settings = new OpenAIPromptExecutionSettings // Erstelle Settings-Objekt
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions, // Aktiviere automatische Tool-Aufrufe
                Temperature = 0.3 // Niedrige Temperatur = präzise, weniger Kreativität, deterministische Antworten
            };

            var response = await _chatService.GetChatMessageContentAsync( // Sende Anfrage an Ollama (async)
                _chatHistory, // Mit bisheriger Konversations-History
                executionSettings: settings, // Mit Tool-Settings
                kernel: _kernel // Mit registrierten MCP-Tools
            );

            var assistantMessage = response.Content ?? "Keine Antwort erhalten."; // Extrahiere Content, Fallback wenn null
            _chatHistory.AddMessage(response.Role, assistantMessage); // Füge Antwort zur History hinzu

            Debug.WriteLine($"[ToolSpecialist] Antwort generiert: {assistantMessage.Length} Zeichen"); // Log: Antwort-Länge
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
