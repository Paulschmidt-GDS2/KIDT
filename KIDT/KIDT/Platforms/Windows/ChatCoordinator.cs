using System.Diagnostics;
using KIDT.Database;
using KIDT.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KIDT.Services;

public class ChatCoordinator : IAsyncDisposable // Zentraler Orchestrator: Koordiniert Router, Conversation, DataAnalysis und File-Upload (wird von Home.razor verwendet)
{
    private DataAnalysisService dataAnalysis; // Service für Datenanalyse (komplexe Anfragen mit mehr Token)
    private ConversationService conversation; // Service für Konversation (normale Chat-Anfragen)
    private FileService fileService; // Service für File-Upload und Text-Extraktion (PDF, TXT etc.)
    private RouterService router; // Router: Entscheidet ob Conversation oder DataAnalysis + handhabt MCP-Tools
    private readonly IServiceProvider serviceProvider; // Service Provider für DB-Services (Dependency Injection)
    private bool isInitialized = false; // Flag: Wurden Services initialisiert? (verhindert doppelte Initialisierung)
    private string currentFileName = string.Empty; // Aktuell angehängte Datei (Dateiname)
    private string currentFileContent = string.Empty; // Aktuell angehängte Datei (extrahierter Text-Content)

    public ChatCoordinator(IServiceProvider serviceProvider) // Konstruktor: Wird beim App-Start aufgerufen (Dependency Injection)
    {
        this.serviceProvider = serviceProvider; // Service Provider speichern für DB-Zugriff
        this.dataAnalysis = null!; // Wird in InitializeAsync erstellt (null! = compiler-beruhigend)
        this.conversation = null!; // Wird in InitializeAsync erstellt
        this.fileService = null!; // Wird in InitializeAsync erstellt
        this.router = null!; // Wird in InitializeAsync erstellt
    }

    public async Task InitializeAsync() // Initialisiere alle Services
    {
        if (this.isInitialized) return; // Bereits initialisiert? ? Abbruch (verhindert doppelte Initialisierung)

        try
        {
            this.dataAnalysis = new DataAnalysisService(); // Erstelle DataAnalysis-Service
            this.conversation = new ConversationService(); // Erstelle Conversation-Service
            this.fileService = new FileService(); // Erstelle File-Service
            this.router = new RouterService(this.serviceProvider); // Erstelle Router-Service
            
            await this.router.InitializeAsync(); // Router lädt API-Key aus Datei

            this.isInitialized = true; // Markiere als erfolgreich initialisiert
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei Initialisierung: {ex.Message}", ex);
        }
    }

    public async Task<ChatResponse> SendAsync(string userMessage, int conversationId) // Verarbeite User-Nachricht und gib Antwort zurück
    {
        if (!this.isInitialized) // Services noch nicht initialisiert?
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        try
        {
            using var scope = this.serviceProvider.CreateScope(); // Erstelle neuen Service-Scope für Datenbank-Zugriff
            var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>(); // Hole ChatDbService
            var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Hole DocumentDbService

            bool hasFile = false; // Standardmäßig: Keine Datei angehängt
            if (!string.IsNullOrEmpty(this.currentFileName)) // Dateiname vorhanden?
            {
                hasFile = true;
            }
            
            RouterResponse routerResponse = await this.router.ProcessAsync(userMessage, hasFile, conversationId); // Router verarbeitet Nachricht

            if (!routerResponse.ShouldRoute) // Router gibt direkte Antwort?
            {
                ChatResponse response = new ChatResponse(); // Erstelle neue ChatResponse
                response.Message = routerResponse.DirectResponse ?? "Dokument wurde verarbeitet."; // Setze Nachricht
                response.FoundDocuments = routerResponse.FoundDocuments; // Setze gefundene Dokumente
                return response;
            }

            string result; // Variable für Antwort vom Service

            if (routerResponse.TargetService == "dataAnalysis") // Router hat DataAnalysis gewählt?
            {
                await this.dataAnalysis.InitializeAsync(docDbService, conversationId); // Initialisiere DataAnalysis
                
                string fullChatHistory = string.Empty;
                
                if (conversationId > 0) // Bestehender Chat?
                {
                    fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId); // Lade Chat-History aus Datenbank
                }
                
                string enhancedMessage = userMessage; // Start mit Original-Nachricht
                if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0) // Wurden Tools verwendet und Dokumente gefunden?
                {
                    enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]"; // Füge System-Info hinzu
                }
                
                result = await this.dataAnalysis.SendAsync( // Sende an DataAnalysis-Service
                    enhancedMessage, // User-Nachricht
                    this.currentFileContent, // Datei-Inhalt
                    this.currentFileName, // Dateiname
                    routerResponse.MaxTokens, // Token-Limit
                    fullChatHistory // Chat-History
                );
            }
            else // Router hat Conversation gewählt
            {
                string fullChatHistory = string.Empty; // Variable für Chat-History

                if (conversationId > 0) // Bestehender Chat?
                {
                    fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId); // Lade Chat-History aus Datenbank
                }
                
                string enhancedMessage = userMessage; // Start mit Original-Nachricht
                if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0) // Wurden Tools verwendet und Dokumente gefunden?
                {
                    enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]"; // Füge System-Info hinzu
                }
                
                result = await this.conversation.SendAsync( // Sende an Conversation-Service
                    enhancedMessage, // User-Nachricht
                    routerResponse.MaxTokens, // Token-Limit
                    fullChatHistory // Chat-History
                );
            }
            
            ChatResponse finalResponse = new ChatResponse(); // Erstelle neue ChatResponse
            finalResponse.Message = result; // Setze Antwort vom Service
            finalResponse.FoundDocuments = routerResponse.FoundDocuments; // Setze gefundene Dokumente
            return finalResponse;
        }
        catch (Exception ex)
        {
            if (ex.Message.StartsWith("Router-Fehler:")) // Router-Fehler?
            {
                ChatResponse errorResponse = new ChatResponse(); // Erstelle Fehler-Response
                errorResponse.Message = $"KI-Dienste nicht erreichbar. Versuche es später.\n\nDetails: {ex.Message}";
                return errorResponse;
            }
            
            ChatResponse generalErrorResponse = new ChatResponse(); // Erstelle allgemeine Fehler-Response
            generalErrorResponse.Message = $"Fehler: {ex.Message}";
            return generalErrorResponse;
        }
    }


    public async Task<string> UploadFileAsync(string filePath) // Lade Datei hoch und extrahiere Text
    {
        if (!this.isInitialized) // Services noch nicht initialisiert?
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        try
        {
            this.currentFileName = Path.GetFileName(filePath); // Hole nur Dateinamen ohne Pfad
            this.currentFileContent = await this.fileService.ExtractTextAsync(filePath); // Extrahiere Text aus Datei

            if (this.currentFileContent.StartsWith("Fehler:")) // Text-Extraktion fehlgeschlagen?
            {
                string errorMessage = this.currentFileContent; // Speichere Fehlermeldung
                this.currentFileName = string.Empty; // Lösche Dateiname
                this.currentFileContent = string.Empty; // Lösche Content
                return errorMessage;
            }

            return $"Datei '{this.currentFileName}' geladen. Stelle jetzt deine Frage!";
        }
        catch (Exception ex)
        {
            this.currentFileName = string.Empty; // Lösche Dateiname
            this.currentFileContent = string.Empty; // Lösche Content
            return $"Fehler beim Hochladen: {ex.Message}";
        }
    }

    public void ClearFile() // Entferne angehängte Datei
    {
        this.currentFileName = string.Empty; // Lösche Dateiname
        this.currentFileContent = string.Empty; // Lösche Datei-Inhalt
    }

    public string GetCurrentFileName() // Gib aktuellen Dateinamen zurück
    {
        return this.currentFileName; // Gib Dateiname zurück
    }

    public async ValueTask DisposeAsync() // Räume alle Services auf
    {
        if (this.dataAnalysis != null) // DataAnalysis-Service existiert?
        {
            await this.dataAnalysis.DisposeAsync(); // Räume DataAnalysis-Service auf
        }
        
        if (this.conversation != null) // Conversation-Service existiert?
        {
            await this.conversation.DisposeAsync(); // Räume Conversation-Service auf
        }
    }
}