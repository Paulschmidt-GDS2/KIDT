using System.Diagnostics;

namespace KIDT.Services;

/// <summary>
/// ChatCoordinator: Koordiniert Router, Modelle (qwen2.5 + phi3:mini), Datei-Service und Datenbank.
/// Router-Modell entscheidet bei jeder User-Nachricht welches Modell verwendet wird.
/// </summary>
public class ChatCoordinator : IAsyncDisposable // Koordinator mit asynchroner Aufräumung
{
    private ToolSpecialistService toolSpecialist; // qwen2.5 für Analysen (wird später initialisiert)
    private ConversationService conversation; // phi3:mini für Small Talk (wird später initialisiert)
    private FileService fileService; // Service für Datei-Extraktion (wird später initialisiert)
    private RouterService router; // GPT-4o-mini Router-Agent (wird später initialisiert)
    private ChatDbService dbService; // Datenbank-Service (wird später initialisiert)
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung
    private string currentFileName = string.Empty; // Aktuell angehängte Datei (leer = keine Datei)
    private string currentFileContent = string.Empty; // Extrahierter Text der Datei (leer = kein Inhalt)

    public ChatCoordinator() // Konstruktor
    {
        this.toolSpecialist = null!; // Setze auf null (wird später initialisiert)
        this.conversation = null!; // "
        this.fileService = null!; // "
        this.router = null!; // "
        this.dbService = null!; // "
    }

    public async Task InitializeAsync() // Initialisiert alle Services
    {
        if (this.isInitialized) return; // Wenn schon initialisiert -> raus

        try
        {
            this.toolSpecialist = new ToolSpecialistService(); // Erstelle ToolSpecialist-Instanz
            this.conversation = new ConversationService(); // Erstelle Conversation-Instanz
            this.fileService = new FileService(); // Erstelle FileService-Instanz
            this.router = new RouterService(); // Erstelle RouterService-Instanz
            this.dbService = new ChatDbService(); // Erstelle ChatDbService-Instanz
            
            await this.router.InitializeAsync(); // Initialisiere Router mit OpenAI API

            this.isInitialized = true; // Setze Flag auf true
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei Initialisierung: {ex.Message}", ex);
        }
    }

    public async Task<string> SendAsync(string userMessage) // Sendet User-Nachricht ohne Conversation-ID
    {
        return await SendAsync(userMessage, 0); // Rufe Überladung mit conversationId = 0
    }

    public async Task<string> SendAsync(string userMessage, int conversationId) // Sendet User-Nachricht mit Conversation-ID
    {
        if (!this.isInitialized) // Ist Coordinator initialisiert?
        {
            await InitializeAsync(); // Nein -> Initialisiere jetzt
        }

        try
        {
            bool hasFile = !string.IsNullOrEmpty(this.currentFileName); // Ist Datei angehängt?
            
            RoutingDecision decision = await this.router.RouteAsync(userMessage, hasFile); // Hole Routing-Entscheidung vom Router

            string result; // Variable für Ergebnis

            if (decision.Service == "toolSpecialist") // Wird ToolSpecialist verwendet?
            {
                string fullChatHistory = string.Empty; // Chat-Verlauf für Analyse (Standard: leer)
                
                if (conversationId > 0) // Ist eine gültige Conversation-ID vorhanden?
                {
                    fullChatHistory = await this.dbService.GetFullChatHistoryAsync(conversationId); // Hole komplette Chat-History aus DB
                }
                
                result = await this.toolSpecialist.SendAsync( // Leite an qwen2.5 weiter
                    userMessage, // Aktuelle User-Nachricht
                    this.currentFileContent, // Datei-Inhalt (leer wenn keine Datei)
                    this.currentFileName, // Datei-Name (leer wenn keine Datei)
                    decision.MaxTokens, // Token-Limit vom Router (1000-3000)
                    fullChatHistory // Komplette Chat-History (leer beim ersten Mal)
                );
            }
            else // Nein -> Conversation verwenden
            {
                result = await this.conversation.SendAsync( // Leite an phi3:mini weiter
                    userMessage, // Aktuelle User-Nachricht
                    string.Empty, // KEINE Datei für Conversation
                    string.Empty, // KEINE Datei für Conversation
                    decision.MaxTokens // Token-Limit vom Router (20-400)
                );
            }
            
            return result; // Gib Ergebnis zurück
        }
        catch (Exception ex)
        {
            if (ex.Message.StartsWith("Router-Fehler:")) // Ist es ein Router-API-Fehler?
            {
                return $"KI-Dienste nicht erreichbar. Versuche es später.\n\nDetails: {ex.Message}"; // Benutzerfreundliche Fehlermeldung
            }
            
            return $"Fehler: {ex.Message}"; // Allgemeine Fehlermeldung
        }
    }

    public async Task<string> UploadFileAsync(string filePath) // Lädt Datei und extrahiert Text
    {
        if (!this.isInitialized) // Ist Coordinator initialisiert?
        {
            await InitializeAsync(); // Nein -> Initialisiere jetzt
        }

        try // Fehlerbehandlung starten
        {
            this.currentFileName = Path.GetFileName(filePath); // Hole nur Dateinamen (ohne Pfad)
            this.currentFileContent = await this.fileService.ExtractTextAsync(filePath); // Extrahiere Text mit FileService

            if (this.currentFileContent.StartsWith("Fehler:")) // War Extraktion erfolgreich?
            {
                string errorMessage = this.currentFileContent; // Speichere Fehlermeldung
                this.currentFileName = string.Empty; // Setze zurück bei Fehler
                this.currentFileContent = string.Empty; // Setze zurück bei Fehler
                return errorMessage; // Gib Fehlermeldung zurück
            }

            return $"Datei '{this.currentFileName}' geladen. Stelle jetzt deine Frage!"; // Erfolgsmeldung
        }
        catch (Exception ex) // Wenn Fehler auftritt
        {
            this.currentFileName = string.Empty; // Setze zurück bei Fehler
            this.currentFileContent = string.Empty; // Setze zurück bei Fehler
            return $"Fehler beim Hochladen: {ex.Message}"; // Fehlermeldung zurückgeben
        }
    }

    public void ClearFile() // Entfernt angehängte Datei
    {
        this.currentFileName = string.Empty; // Dateiname löschen
        this.currentFileContent = string.Empty; // Datei-Inhalt löschen
    }

    public string GetCurrentFileName() // Gibt Dateinamen zurück
    {
        return this.currentFileName; // Gib aktuellen Dateinamen zurück (oder leer)
    }

    public async ValueTask DisposeAsync() // Räumt beide Services auf
    {
        if (this.toolSpecialist != null) // Wenn ToolSpecialist existiert?
        {
            await this.toolSpecialist.DisposeAsync(); // Ja -> Räume auf
        }
        
        if (this.conversation != null) // Wenn Conversation existiert?
        {
            await this.conversation.DisposeAsync(); // Ja -> Räume auf
        }
    }
}
