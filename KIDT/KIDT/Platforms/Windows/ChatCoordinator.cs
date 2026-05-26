using System.Diagnostics;
using KIDT.Database;
using KIDT.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KIDT.Services;

/// <summary>
/// ARCHITEKTUR-ÜBERSICHT:
/// 
/// User-Nachricht
///      ↓
/// [ROUTER (GPT-4 via Azure OpenAI + MCP-Tools)]
///      ↓
///      ├─ Function Call erkannt? (search_documents, list_calendar_events, create_calendar_event, etc.)
///      │   → Tool wird DIREKT ausgeführt (KEIN weiteres LLM!)
///      │   → Direkte Antwort an User (z.B. "Termin erstellt", "3 Dokumente gefunden")
///      │
///      └─ Kein Tool? → JSON-Routing-Entscheidung
///          ↓
///          ├─ Intent: "conversation"
///          │   → ConversationService (phi3:mini via Ollama)
///          │   → Schnelle, kurze Antworten (Small Talk, einfache Fragen)
///          │   → ROUTER-DIRECTED TASKS: Router analysiert Chat-History, gibt Task zurück (was fehlt?)
///          │   → ChatCoordinator fügt SYSTEM-Message hinzu → PHI3:MINI generiert natürliche Frage
///          │
///          └─ Intent: "dataAnalysis"
///              → DataAnalysisService (qwen2.5:7b via Ollama)
///              → Text-Analyse von Dateien/Dokumenten (KEINE Tools!)
/// 
/// WARUM SO?
/// - GPT-4: Bestes Function Calling (Tool-Orchestrierung) + Chat-History-Analyse für fehlende Infos
/// - phi3:mini: Schnell & klein für Small Talk + natürliche Frageformulierung (bekommt SYSTEM-Anweisungen)
/// - qwen2.5:7b: Präzise für Text-Analyse
/// 
/// TERMIN-ERSTELLUNG (MULTI-FIELD-TASKS):
/// - Router analysiert Chat-History: Was fehlt? (Titel, Datum, Zeit)
/// - Gibt kombinierte Tasks zurück: "askForAll" (alle 3), "askForDateAndTime" (2 fehlen), etc.
/// - ChatCoordinator mappt Task → SYSTEM-Message → PHI3:MINI fragt ALLE fehlenden Infos auf einmal
/// - Effizienter als sequenziell (3 separate Fragen)!
/// 
/// Tools werden NUR im Router gehandhabt, damit:
/// 1. Dokument-Suche VOR Analyse passiert
/// 2. Ein zentraler Punkt für alle Tool-Calls (leichter zu debuggen)
/// 3. Analyse-Modelle fokussieren sich NUR auf Text-Verarbeitung
/// 4. State-Tracking im Router (GPT-4), nicht in kleinen Modellen (PHI3:MINI)
/// </summary>
public class ChatCoordinator : IAsyncDisposable // Zentraler Orchestrator: Koordiniert Router, DataAnalysis und File-Upload (wird von Home.razor verwendet)
{
    private DataAnalysisService dataAnalysis;
    private FileService fileService;
    private RouterService router;
    private readonly IServiceProvider serviceProvider;
    private bool isInitialized = false;
    private string currentFileName = string.Empty;
    private string currentFileContent = string.Empty;

    public ChatCoordinator(IServiceProvider serviceProvider) // Konstruktor: Wird beim App-Start aufgerufen (Dependency Injection)
    {
        this.serviceProvider = serviceProvider;
        this.dataAnalysis = new DataAnalysisService();
        this.fileService = new FileService();
        this.router = new RouterService(serviceProvider);
    }

    public async Task InitializeAsync() // Initialisiere alle Services
    {
        if (this.isInitialized) return; // Bereits initialisiert? ? Abbruch (verhindert doppelte Initialisierung)

        try
        {
            this.dataAnalysis = new DataAnalysisService(); // Erstelle DataAnalysis-Service
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

    public async Task<ChatResponse> SendAsync(string userMessage, int conversationId, bool deepThink = false) // Verarbeite User-Nachricht und gib Antwort zurück
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

            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] === ROUTER RESPONSE ===");
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] ShouldRoute: {routerResponse.ShouldRoute}");
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] TargetService: {routerResponse.TargetService}");
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] ToolWasUsed: {routerResponse.ToolWasUsed}");
            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] Reason: {routerResponse.Reason}");
            if (routerResponse.DirectResponse != null)
            {
                System.Diagnostics.Debug.WriteLine($"[COORDINATOR] DirectResponse: {routerResponse.DirectResponse}");
            }

            if (!routerResponse.ShouldRoute) // Router gibt direkte Antwort?
            {
                ChatResponse response = new ChatResponse(); // Erstelle neue ChatResponse
                string responseMessage = "Dokument wurde verarbeitet.";
                if (routerResponse.DirectResponse != null)
                {
                    responseMessage = routerResponse.DirectResponse;
                }
                response.Message = responseMessage; // Setze Nachricht
                response.FoundDocuments = routerResponse.FoundDocuments; // Setze gefundene Dokumente
                response.FoundEvents = routerResponse.FoundEvents; // Setze gefundene Termine
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

                // --- Dokument-Inhalt bestimmen ---
                string fileContentToUse = this.currentFileContent; // Standard: direkt hochgeladene Datei
                string fileNameToUse = this.currentFileName; // Standard: direkt hochgeladener Dateiname

                if (string.IsNullOrEmpty(fileContentToUse) && routerResponse.FoundDocuments.Count > 0) // Kein Upload, aber DB-Dokument vorhanden?
                {
                    Document firstDoc = routerResponse.FoundDocuments[0]; // Erstes gefundenes DB-Dokument verwenden
                    if (!string.IsNullOrEmpty(firstDoc.ExtractedText)) // Extrahierter Text vorhanden?
                    {
                        fileContentToUse = firstDoc.ExtractedText; // Dokument-Text aus DB übergeben
                        fileNameToUse = firstDoc.FileName; // Dateiname aus DB übergeben
                    }
                }

                result = await this.dataAnalysis.SendAsync( // Sende an DataAnalysis-Service
                    enhancedMessage, // User-Nachricht
                    fileContentToUse, // Datei-Inhalt (Upload oder DB-Dokument)
                    fileNameToUse, // Dateiname (Upload oder DB-Dokument)
                    routerResponse.MaxTokens, // Token-Limit
                    fullChatHistory, // Chat-History
                    deepThink // Deep Think Toggle
                );
            }
            else // Fallback: sollte nicht vorkommen (Gemini antwortet direkt via DirectResponse)
            {
                result = "Wie kann ich dir helfen?"; // Sicherheits-Fallback
            }

            ChatResponse finalResponse = new ChatResponse(); // Erstelle neue ChatResponse
            finalResponse.Message = result; // Setze Antwort vom Service

            // NUR bei Conversation oder direkt nach Suche: Dokumente als Cards anzeigen
            // NICHT bei DataAnalysis (Dokumente wurden schon beim Suchen angezeigt!)
            if (routerResponse.TargetService != "dataAnalysis")
            {
                finalResponse.FoundDocuments = routerResponse.FoundDocuments; // Setze gefundene Dokumente
            }
            else
            {
                finalResponse.FoundDocuments = new List<Document>(); // Leere Liste bei DataAnalysis
            }

            finalResponse.FoundEvents = routerResponse.FoundEvents; // Setze gefundene Termine
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


    public async IAsyncEnumerable<ChatStreamChunk> SendStreamAsync(string userMessage, int conversationId, bool deepThink = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) // Verarbeite User-Nachricht mit STREAMING
    {
        if (!this.isInitialized) // Services noch nicht initialisiert?
        {
            await InitializeAsync(); // Initialisiere jetzt
        }

        // VALIDIERUNG 1: Prüfe User-Input BEVOR Router aufgerufen wird
        if (!IsValidUserInput(userMessage)) // Ungültiger User-Input? (Gibberish, Wiederholungen, etc.)
        {
            yield return new ChatStreamChunk // Sende Fehler-Nachricht zurück
            {
                TextChunk = "Deine Eingabe konnte nicht verarbeitet werden. Bitte formuliere deine Frage neu.", // Fehler-Text
                IsComplete = true, // Stream ist komplett
                FoundDocuments = new List<Document>(), // Keine Dokumente
                FoundEvents = new List<CalendarEvent>() // Keine Events
            };
            yield break; // Stream beenden
        }

        using var scope = this.serviceProvider.CreateScope(); // Erstelle neuen Service-Scope für Datenbank-Zugriff
        var dbService = scope.ServiceProvider.GetRequiredService<ChatDbService>(); // Hole ChatDbService
        var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>(); // Hole DocumentDbService

        bool hasFile = false; // Standardmäßig: Keine Datei angehängt
        if (!string.IsNullOrEmpty(this.currentFileName)) // Dateiname vorhanden?
        {
            hasFile = true;
        }

        RouterResponse routerResponse = await this.router.ProcessAsync(userMessage, hasFile, conversationId); // Router verarbeitet Nachricht

        if (!routerResponse.ShouldRoute) // Router gibt direkte Antwort? (z.B. Dokument-Suche)
        {
            string responseText = "Dokument wurde verarbeitet.";
            if (routerResponse.DirectResponse != null)
            {
                responseText = routerResponse.DirectResponse;
            }

            yield return new ChatStreamChunk // Sende finale Antwort als Stream-Chunk
            {
                TextChunk = responseText, // Antwort-Text (z.B. "Termin erstellt")
                IsComplete = true, // Stream ist komplett
                FoundDocuments = routerResponse.FoundDocuments, // Gefundene Dokumente mitgeben
                FoundEvents = routerResponse.FoundEvents // Gefundene Events mitgeben
            };
            yield break; // Stream beenden
        }

        // DATAANALYSIS-SERVICE: qwen3.5:9b analysiert Dokument-Text (einziger verbleibender Route-Pfad)
        {
            await this.dataAnalysis.InitializeAsync(docDbService, conversationId); // Initialisiere DataAnalysis mit Dokument-Zugriff

            string fullChatHistory = string.Empty; // Variable für Chat-History
            if (conversationId > 0) // Bestehender Chat?
            {
                fullChatHistory = await dbService.GetFullChatHistoryAsync(conversationId); // Lade Chat-History aus Datenbank
            }

            string enhancedMessage = userMessage; // Start mit Original-Nachricht
            if (routerResponse.ToolWasUsed && routerResponse.FoundDocuments.Count > 0) // Wurden Dokumente gesucht und gefunden?
            {
                enhancedMessage += $"\n\n[SYSTEM: {routerResponse.FoundDocuments.Count} Dokument(e) gefunden]"; // Füge System-Info hinzu
            }

            // --- Dokument-Inhalt bestimmen ---
            string fileContentToUse = this.currentFileContent; // Standard: direkt hochgeladene Datei
            string fileNameToUse = this.currentFileName; // Standard: direkt hochgeladener Dateiname

            if (string.IsNullOrEmpty(fileContentToUse) && routerResponse.FoundDocuments.Count > 0) // Kein Upload, aber DB-Dokument vorhanden?
            {
                Document firstDoc = routerResponse.FoundDocuments[0]; // Erstes gefundenes DB-Dokument verwenden
                if (!string.IsNullOrEmpty(firstDoc.ExtractedText)) // Extrahierter Text vorhanden?
                {
                    fileContentToUse = firstDoc.ExtractedText; // Dokument-Text aus DB übergeben
                    fileNameToUse = firstDoc.FileName; // Dateiname aus DB übergeben
                }
            }

            System.Diagnostics.Debug.WriteLine($"[COORDINATOR] DataAnalysis aufgerufen - DeepThink: {deepThink}, File: '{fileNameToUse}', ContentLength: {fileContentToUse.Length} Zeichen");

            string result = await this.dataAnalysis.SendAsync( // Sende an DataAnalysis-Service (qwen3.5:9b)
                enhancedMessage, // User-Nachricht (evtl. mit System-Info)
                fileContentToUse, // Datei-Inhalt (Upload oder DB-Dokument)
                fileNameToUse, // Dateiname (Upload oder DB-Dokument)
                routerResponse.MaxTokens, // Token-Limit
                fullChatHistory, // Chat-History (für Kontext)
                deepThink, // Deep Think Toggle (reasoning_effort: none/high)
                cancellationToken // Weiterleiten: ermöglicht Abort-Button den qwen3-Request zu stoppen
            );

            yield return new ChatStreamChunk // Sende komplette Antwort als einzelner Chunk (kein echtes Streaming)
            {
                TextChunk = result, // Komplette Antwort von qwen2.5:7b
                IsComplete = true, // Stream ist direkt fertig (Non-Streaming)
                FoundDocuments = new List<Document>(), // LEER bei DataAnalysis (Dokumente wurden schon beim Suchen angezeigt!)
                FoundEvents = routerResponse.FoundEvents // Gefundene Events mitgeben
            };
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

    public string GetCurrentFileContent() // Gib extrahierten Datei-Inhalt zurück (für DB-Speicherung)
    {
        return this.currentFileContent; // Gib Inhalt zurück
    }

    private bool IsValidUserInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2) return false;

        // Sehr lange Wiederholungen blockieren (z.B. "hallo" 20x)
        if (text.Length > 40 && !text.Contains(' '))
        {
            string pattern = text.Substring(0, Math.Min(5, text.Length));
            int count = 0;
            for (int i = 0; i < text.Length - pattern.Length + 1; i += pattern.Length)
            {
                if (text.Substring(i, pattern.Length).Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            if (count >= 4) return false;
        }

        return true;
    }

    private bool IsValidLLMOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 3) return false;

        string trimmed = text.Trim();

        // Blockiere JSON/Code-Output
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || text.Contains("```"))
            return false;

        return true;
    }

    public async ValueTask DisposeAsync() // Räume alle Services auf
    {
        if (this.dataAnalysis != null) // DataAnalysis-Service existiert?
        {
            await this.dataAnalysis.DisposeAsync(); // Räume DataAnalysis-Service auf
        }
    }
}

public class ChatStreamChunk // Klasse für Streaming-Chunks (enthält Text-Teil + Status ob komplett)
{
    public string TextChunk { get; set; } = string.Empty;
    public bool IsComplete { get; set; } = false;
    public List<Document> FoundDocuments { get; set; } = new List<Document>();
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
}