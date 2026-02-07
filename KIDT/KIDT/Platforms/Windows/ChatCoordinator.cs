using System.Diagnostics;
using KIDT.Database;
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

    public async Task InitializeAsync() // Initialisiert alle Services (wird beim App-Start im Hintergrund aufgerufen von Home.razor)
    {
        if (this.isInitialized) return; // Bereits initialisiert? ? Abbruch (verhindert doppelte Initialisierung)

        try
        {
            this.dataAnalysis = new DataAnalysisService(); // Erstelle DataAnalysis-Service (für komplexe Anfragen)
            this.conversation = new ConversationService(); // Erstelle Conversation-Service (für normale Chats)
            this.fileService = new FileService(); // Erstelle File-Service (für PDF/TXT-Upload)
            this.router = new RouterService(this.serviceProvider); // Erstelle Router (bekommt ServiceProvider für DocumentDbService)
            
            await this.router.InitializeAsync(); // Router lädt API-Key aus Datei

            this.isInitialized = true; // Markiere als initialisiert
        }
        catch (Exception ex) // Initialisierung fehlgeschlagen?
        {
            throw new Exception($"Fehler bei Initialisierung: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Überladung: SendAsync ohne conversationId (für neuen Chat)
    {
        return await SendAsync(userMessage, 0); // Rufe Hauptmethode mit conversationId=0 auf
    }

    public async Task<string> SendAsync(string userMessage, int conversationId) // Hauptmethode: Verarbeitet User-Nachricht und gibt Antwort zurück (Ablauf: Router ? Conversation/DataAnalysis ? Antwort)
    {
        if (!this.isInitialized) // Services noch nicht initialisiert?
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        try
        {
            using var scope = this.serviceProvider.CreateScope(); // Erstelle neuen Service-Scope für DB-Zugriff (wird am Ende disposed)
            var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>(); // Hole ChatDbService für Chat-History
            var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Hole DocumentDbService für Dokument-Operationen (wird aktuell nicht direkt verwendet)

            bool hasFile = !string.IsNullOrEmpty(this.currentFileName); // Hat User eine Datei angehängt?
            
            // SCHRITT 1: Router verarbeitet Nachricht (entscheidet: Conversation/DataAnalysis ODER nutzt MCP-Tools für Dokument-Suche)
            RouterResponse routerResponse = await this.router.ProcessAsync(userMessage, hasFile, conversationId); // Router erstellt intern eigenen Scope für DocumentDbService

            // SCHRITT 2: Check ob Router direkt antworten soll (z.B. nach search_documents oder add_document_to_chat)
            if (!routerResponse.ShouldRoute) // Router gibt direkte Antwort zurück (kein Routing nötig)?
            {
                // Router hat Dokument-Suche durchgeführt oder Dokument hinzugefügt
                // routerResponse.FoundDocuments enthält gefundene Dokumente für UI-Anzeige
                // TODO: Diese müssen an die UI weitergegeben werden (später implementieren - aktuell nur Nachricht)
                return routerResponse.DirectResponse ?? "Dokument wurde verarbeitet.";
            }

            // SCHRITT 3: Router hat entschieden zu routen ? Sende an Conversation oder DataAnalysis
            // HINWEIS: routerResponse.FoundDocuments enthält Dokumente für UI-Anzeige
            // TODO: Diese müssen an die UI weitergegeben werden (später implementieren)

            string result; // Antwort vom Service (Conversation oder DataAnalysis)

            if (routerResponse.TargetService == "dataAnalysis") // Router hat DataAnalysis gewählt? (komplexe Anfragen)
            {
                await this.dataAnalysis.InitializeAsync(docDbService, conversationId); // Initialisiere DataAnalysis mit DocumentDbService und conversationId
                
                string fullChatHistory = string.Empty; // Chat-History (leer für neuen Chat)
                
                if (conversationId > 0) // Bestehender Chat? (conversationId > 0)
                {
                    fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId); // Lade Chat-History aus DB
                }
                
                // Bei Tool-Nutzung: Füge Info zu gefundenen Dokumenten hinzu (für Kontext)
                string enhancedMessage = userMessage; // Start mit Original-Nachricht
                if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0) // Wurden Tools verwendet UND Dokumente gefunden?
                {
                    enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]"; // Füge System-Info hinzu
                }
                
                result = await this.dataAnalysis.SendAsync( // Sende an DataAnalysis
                    enhancedMessage, // User-Nachricht (evtl. mit System-Info)
                    this.currentFileContent, // Angehängte Datei (Content)
                    this.currentFileName, // Angehängte Datei (Name)
                    routerResponse.MaxTokens, // Token-Limit (2000 für DataAnalysis)
                    fullChatHistory // Chat-History
                );
            }
            else // Router hat Conversation gewählt (normale Chats)
            {
                string fullChatHistory = string.Empty; // Chat-History (leer für neuen Chat)

                if (conversationId > 0) // Bestehender Chat? (conversationId > 0)
                {
                    fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId); // Lade Chat-History aus DB
                }
                
                // Bei Tool-Nutzung: Füge Info zu gefundenen Dokumenten hinzu (für Kontext)
                string enhancedMessage = userMessage; // Start mit Original-Nachricht
                if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0) // Wurden Tools verwendet UND Dokumente gefunden?
                {
                    enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]"; // Füge System-Info hinzu
                }
                
                result = await this.conversation.SendAsync( // Sende an Conversation
                    enhancedMessage, // User-Nachricht (evtl. mit System-Info)
                    routerResponse.MaxTokens, // Token-Limit (300 für Conversation)
                    fullChatHistory // Chat-History
                );
            }
            
            return result;
        }
        catch (Exception ex)
        {
            if (ex.Message.StartsWith("Router-Fehler:")) // Router-Fehler? (API-Key fehlt oder OpenAI nicht erreichbar)
            {
                return $"KI-Dienste nicht erreichbar. Versuche es später.\n\nDetails: {ex.Message}";
            }
            
            return $"Fehler: {ex.Message}";
        }
    }


    public async Task<string> UploadFileAsync(string filePath) // Lädt Datei (PDF/TXT/MD/JSON) und extrahiert Text (wird von Home.razor aufgerufen wenn User Upload-Button klickt)
    {
        if (!this.isInitialized) // Services noch nicht initialisiert?
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        try
        {
            this.currentFileName = Path.GetFileName(filePath); // Hole nur Dateinamen ohne Pfad (z.B. "Document.pdf")
            this.currentFileContent = await this.fileService.ExtractTextAsync(filePath); // Extrahiere Text mit FileService (PDF ? Text via PDFtoText)

            if (this.currentFileContent.StartsWith("Fehler:")) // Text-Extraktion fehlgeschlagen?
            {
                string errorMessage = this.currentFileContent; // Speichere Fehlermeldung
                this.currentFileName = string.Empty; // Reset: Dateiname löschen
                this.currentFileContent = string.Empty; // Reset: Content löschen
                return errorMessage; // Gib Fehlermeldung zurück
            }

            return $"Datei '{this.currentFileName}' geladen. Stelle jetzt deine Frage!";
        }
        catch (Exception ex)
        {
            this.currentFileName = string.Empty; // Reset: Dateiname löschen
            this.currentFileContent = string.Empty; // Reset: Content löschen
            return $"Fehler beim Hochladen: {ex.Message}";
        }
    }

    public void ClearFile() // Entfernt angehängte Datei (wird von Home.razor aufgerufen wenn User X-Button im Badge klickt)
    {
        this.currentFileName = string.Empty; // Dateiname löschen (Badge wird ausgeblendet)
        this.currentFileContent = string.Empty; // Datei-Content löschen (wird nicht mehr an Services übergeben)
    }

    public string GetCurrentFileName() // Gibt Dateinamen zurück (für UI-Anzeige im Badge)
    {
        return this.currentFileName; // Gib aktuellen Dateinamen zurück (oder leer wenn keine Datei)
    }

    public async ValueTask DisposeAsync() // Räumt alle Services auf (wird beim App-Shutdown aufgerufen)
    {
        if (this.dataAnalysis != null) // DataAnalysis-Service existiert?
        {
            await this.dataAnalysis.DisposeAsync(); // Räume auf (disposes AI-Model)
        }
        
        if (this.conversation != null) // Conversation-Service existiert?
        {
            await this.conversation.DisposeAsync(); // Räume auf (disposes AI-Model)
        }
    }
}
