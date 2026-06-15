using KIDT.Database;
using KIDT.Models;
using KIDT.Services.Logic;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace KIDT.Services;

public class ChatCoordinator : IAsyncDisposable // Orchestrator: RouterService (Gemini) → optional DataAnalysisService (Ollama)
{
    private DataAnalysisService dataAnalysis;
    private FileService fileService;
    private RouterService router;
    private readonly IServiceProvider serviceProvider;
    private bool isInitialized = false;
    private string currentFileName = string.Empty;
    private string currentFileContent = string.Empty;

    public ChatCoordinator(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
        this.dataAnalysis = new DataAnalysisService();
        this.fileService = new FileService();
        this.router = new RouterService(serviceProvider);
    }

    public async Task InitializeAsync() // Initialisiert alle Services beim App-Start
    {
        if (this.isInitialized) return;

        try
        {
            this.dataAnalysis = new DataAnalysisService();
            this.fileService = new FileService();
            this.router = new RouterService(this.serviceProvider);
            await this.router.InitializeAsync(); // API-Key laden
            this.isInitialized = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei Initialisierung: {ex.Message}", ex);
        }
    }

    public async IAsyncEnumerable<ChatStreamChunk> SendStreamAsync( // Verarbeitet Nachricht mit Streaming-Ausgabe
        string userMessage, int conversationId, bool deepThink = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!this.isInitialized) await InitializeAsync();

        if (!UserInputValidator.IsValid(userMessage)) // Eingabe-Validierung (Gibberish, Wiederholungen)
        {
            yield return new ChatStreamChunk
            {
                TextChunk = "Deine Eingabe konnte nicht verarbeitet werden. Bitte formuliere deine Frage neu.",
                IsComplete = true
            };
            yield break;
        }

        System.Diagnostics.Debug.WriteLine($"[COORDINATOR] SendStreamAsync → ConvId={conversationId}, HasFile={!string.IsNullOrEmpty(this.currentFileName)}");

        using var scope = this.serviceProvider.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>();
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();

        cancellationToken.ThrowIfCancellationRequested(); // Abbruch vor Router-Aufruf prüfen

        bool hasFile = !string.IsNullOrEmpty(this.currentFileName);
        RouterResponse routerResponse = await this.router.ProcessAsync(userMessage, hasFile, conversationId, cancellationToken);

        System.Diagnostics.Debug.WriteLine($"[COORDINATOR] Router-Ergebnis: ShouldRoute={routerResponse.ShouldRoute}, Reason={routerResponse.Reason}, ToolWasUsed={routerResponse.ToolWasUsed}");

        if (!routerResponse.ShouldRoute) // Router gibt direkte Antwort (Tool-Ergebnis oder Konversation)
        {
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] → Direkte Antwort (kein DataAnalysis)");
            string responseText = "Dokument wurde verarbeitet.";
            if (routerResponse.DirectResponse != null) responseText = routerResponse.DirectResponse;

            yield return new ChatStreamChunk
            {
                TextChunk = responseText,
                IsComplete = true,
                FoundDocuments = routerResponse.FoundDocuments,
                FoundEvents = routerResponse.FoundEvents,
                ModelLabel = "gemini-2.5-flash via OpenRouter" // Router-Modell antwortet direkt
            };
            yield break;
        }

        // --- DataAnalysis: Dokument-Inhalt analysieren ---
        await this.dataAnalysis.InitializeAsync();

        string fullChatHistory = string.Empty;
        if (conversationId > 0)
            fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId);

        string enhancedMessage = userMessage;
        if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0)
            enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]";

        string fileContentToUse = this.currentFileContent; // Standard: direkt hochgeladene Datei
        string fileNameToUse = this.currentFileName;

        if (string.IsNullOrEmpty(fileContentToUse) && routerResponse.FoundDocuments.Count > 0) // Kein Upload → DB-Dokument verwenden
        {
            Document firstDoc = routerResponse.FoundDocuments[0];
            if (!string.IsNullOrEmpty(firstDoc.ExtractedText))
            {
                fileContentToUse = firstDoc.ExtractedText;
                fileNameToUse = firstDoc.FileName;
            }
        }

        if (fileContentToUse == null) fileContentToUse = string.Empty; // Null-Guard vor SendAsync
        System.Diagnostics.Debug.WriteLine($"[COORDINATOR] → DataAnalysis (qwen via Ollama) | Dokument: '{fileNameToUse}', TextLänge: {fileContentToUse.Length} Zeichen, MaxTokens: {routerResponse.MaxTokens}");

        string result = await this.dataAnalysis.SendAsync(
            enhancedMessage, fileContentToUse, fileNameToUse,
            routerResponse.MaxTokens, fullChatHistory, deepThink, cancellationToken);

        System.Diagnostics.Debug.WriteLine($"[COORDINATOR] DataAnalysis abgeschlossen → Antwort: {result.Length} Zeichen");

        yield return new ChatStreamChunk
        {
            TextChunk = result,
            IsComplete = true,
            FoundDocuments = new List<Document>(), // Bei DataAnalysis keine Cards (schon beim Suchen gezeigt)
            FoundEvents = routerResponse.FoundEvents,
            ModelLabel = "qwen3.5:9b via Ollama" // DataAnalysis-Modell antwortet
        };
    }

    public async Task<string> UploadFileAsync(string filePath) // Lädt Datei hoch und extrahiert Text
    {
        if (!this.isInitialized) await InitializeAsync();

        try
        {
            this.currentFileName = Path.GetFileName(filePath);
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] UploadFileAsync → Datei: '{this.currentFileName}'");
            this.currentFileContent = await this.fileService.ExtractTextAsync(filePath);
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] Text extrahiert: {this.currentFileContent.Length} Zeichen");

            if (this.currentFileContent.StartsWith("Fehler:")) // Extraktion fehlgeschlagen?
            {
                string errorMessage = this.currentFileContent;
                this.currentFileName = string.Empty;
                this.currentFileContent = string.Empty;
                return errorMessage;
            }

            return $"Datei '{this.currentFileName}' geladen. Stelle jetzt deine Frage!";
        }
        catch (Exception ex)
        {
            this.currentFileName = string.Empty;
            this.currentFileContent = string.Empty;
            return $"Fehler beim Hochladen: {ex.Message}";
        }
    }

    public void ClearFile() // Entfernt angehängte Datei
    {
        this.currentFileName = string.Empty;
        this.currentFileContent = string.Empty;
    }

    public string GetCurrentFileName() { return this.currentFileName; }

    public string GetCurrentFileContent() { return this.currentFileContent; }

    public async ValueTask DisposeAsync()
    {
        if (this.dataAnalysis != null) await this.dataAnalysis.DisposeAsync();
    }
}