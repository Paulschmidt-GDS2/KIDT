using System.Diagnostics;

namespace KIDT.Services;

/// <summary>
/// Router-Service: Entscheidet intelligent, welches spezialisierte Modell verwendet wird.
/// - ToolSpecialistService (qwen2.5) für Analysen & Dateien
/// - ConversationService (llama3.1) für allgemeine Konversation
/// </summary>
public class ChatMcpService : IAsyncDisposable // Router-Service mit automatischer asynchroner Aufräumung
{
    private ToolSpecialistService? toolSpecialist; // qwen2.5 Service-Instanz (wird später initialisiert)
    private ConversationService? conversation; // llama3.1 Service-Instanz (wird später initialisiert)
    private FileService? fileService; // Service für Datei-Extraktion (wird später initialisiert)
    private bool isInitialized = false; // Flag: Verhindert mehrfache Initialisierung
    private string currentFileName = string.Empty; // Aktuell angehängte Datei (leer = keine Datei)
    private string currentFileContent = string.Empty; // Extrahierter Text der Datei (leer = kein Inhalt)

    private readonly string[] analysisKeywords = new[] // Keywords für Tool-Spezialist-Routing
    {  
        "analysier", "analyse", "analysiere",  
        "wörter", "wort", "zeichen", "text", "buchstaben",       
        "länge", "lang", "wie lang", "wie gross", "wie groß", "größe", 
        "zähle", "zähl", "gezählt", "anzahl",
        "messe", "mess", "ermittle", "bestimme",
        "dokument", "datei", "lies", "lese", "liest", "öffne", "inhalt",
        "prüfe", "prüf", "überprüfe", "check"
    };

    public async Task InitializeAsync() // Initialisiert beide Services (qwen2.5 + llama3.1 + FileService)
    {
        if (this.isInitialized) return; // Wenn schon initialisiert -> raus

        try
        {
            Debug.WriteLine("[Router] Initialisiere spezialisierte Services...");

            this.toolSpecialist = new ToolSpecialistService(); // Erstelle qwen2.5 Service-Instanz
            this.conversation = new ConversationService(); // Erstelle llama3.1 Service-Instanz
            this.fileService = new FileService(); // Erstelle FileService-Instanz

            this.isInitialized = true; // Setze Flag auf true
            Debug.WriteLine("[Router] Router erfolgreich initialisiert.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ChatMcpService Router: {ex.Message}", ex);
        }

        await Task.CompletedTask; // Macht Methode async-kompatibel
    }

    public async Task<string> SendAsync(string userMessage) // Routet Nachricht an qwen2.5 oder llama3.1
    {
        if (!this.isInitialized) // Ist Router initialisiert?
        {
            await InitializeAsync(); // Nein -> Initialisiere jetzt
        }

        try
        {
            var useToolSpecialist = ShouldUseToolSpecialist(userMessage); // Routing-Entscheidung treffen

            string result; // Variable für Ergebnis

            if (useToolSpecialist) // Wird Tool-Spezialist verwendet?
            {
                int maxChars = Math.Min(50, userMessage.Length); // Maximal 50 Zeichen für Log
                Debug.WriteLine($"[Router] Tool-Spezialist (qwen2.5) für: {userMessage.Substring(0, maxChars)}...");
                result = await this.toolSpecialist!.SendAsync(userMessage, this.currentFileContent, this.currentFileName); // Leite an qwen2.5 weiter (mit Datei)
            }
            else // Nein -> Konversation verwenden
            {
                int maxChars = Math.Min(50, userMessage.Length); // Maximal 50 Zeichen für Log
                Debug.WriteLine($"[Router] Konversation (llama3.1) für: {userMessage.Substring(0, maxChars)}...");
                result = await this.conversation!.SendAsync(userMessage, this.currentFileContent, this.currentFileName); // Leite an llama3.1 weiter (mit Datei)
            }
            
            return result; // Gib Ergebnis zurück
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Router] Fehler: {ex.Message}");
            return $"Fehler beim Routing: {ex.Message}";
        }
    }

    /// <summary>
    /// Lädt eine Datei und extrahiert den Text für nachfolgende Nachrichten.
    /// </summary>
    public async Task<string> UploadFileAsync(string filePath) // Lädt Datei und extrahiert Text
    {
        if (!this.isInitialized) // Ist Router initialisiert?
        {
            await InitializeAsync(); // Nein -> Initialisiere jetzt
        }

        try
        {
            Debug.WriteLine($"[Router] Lade Datei: {filePath}");
            
            this.currentFileName = Path.GetFileName(filePath); // Hole nur Dateinamen (ohne Pfad)
            this.currentFileContent = await this.fileService!.ExtractTextAsync(filePath); // Extrahiere Text mit FileService

            if (this.currentFileContent.StartsWith("Fehler:")) // War Extraktion erfolgreich?
            {
                string errorMessage = this.currentFileContent; // Speichere Fehlermeldung
                this.currentFileName = string.Empty; // Setze zurück bei Fehler
                this.currentFileContent = string.Empty; // Setze zurück bei Fehler
                Debug.WriteLine("[Router] Upload fehlgeschlagen");
                return errorMessage;
            }

            Debug.WriteLine($"[Router] Datei geladen: {this.currentFileName} ({this.currentFileContent.Length} Zeichen)");
            return $"Datei '{this.currentFileName}' erfolgreich geladen. Stelle jetzt deine Frage dazu!";
        }
        catch (Exception ex)
        {
            this.currentFileName = string.Empty; // Setze zurück bei Fehler
            this.currentFileContent = string.Empty; // Setze zurück bei Fehler
            Debug.WriteLine($"[Router] Upload-Fehler: {ex.Message}");
            return $"Fehler beim Hochladen: {ex.Message}";
        }
    }

    /// <summary>
    /// Entfernt die aktuell angehängte Datei.
    /// </summary>
    public void ClearFile() // Entfernt angehängte Datei
    {
        this.currentFileName = string.Empty; // Dateiname löschen
        this.currentFileContent = string.Empty; // Datei-Inhalt löschen
        Debug.WriteLine("[Router] Datei entfernt");
    }

    /// <summary>
    /// Gibt den Namen der aktuell angehängten Datei zurück (oder leer).
    /// </summary>
    public string GetCurrentFileName() // Gibt Dateinamen zurück (oder leer)
    {
        return this.currentFileName; // Gib aktuellen Dateinamen zurück
    }

    private bool ShouldUseToolSpecialist(string userMessage) // Entscheidet ob Tool-Spezialist oder Konversation
    {
        if (!string.IsNullOrEmpty(this.currentFileName)) // Ist eine Datei angehängt?
        {
            Debug.WriteLine("[Router] Datei angehängt ? Tool-Spezialist");
            return true; // Ja -> immer Tool-Spezialist
        }

        var messageLower = userMessage.ToLowerInvariant(); // Konvertiere zu Lowercase für Case-insensitive Vergleich
        
        foreach (var keyword in this.analysisKeywords) // Durchlaufe alle Keywords
        {
            if (messageLower.Contains(keyword)) // Check: Enthält Nachricht dieses Keyword?
            {
                Debug.WriteLine($"[Router] Schlüsselwort '{keyword}' erkannt ? Tool-Spezialist");
                return true; // Ja -> Tool-Spezialist verwenden
            }
        }

        Debug.WriteLine("[Router] Keine Analyse-Indikatoren ? Konversation");
        return false; // Standard: Konversations-Modell
    }

    public async ValueTask DisposeAsync() // Räumt beide Services auf
    {
        if (this.toolSpecialist != null) // Wenn Tool-Spezialist existiert?
        {
            await this.toolSpecialist.DisposeAsync(); // Ja -> Räume auf
        }
        
        if (this.conversation != null) // Wenn Konversation existiert?
        {
            await this.conversation.DisposeAsync(); // Ja -> Räume auf
        }
    }
}