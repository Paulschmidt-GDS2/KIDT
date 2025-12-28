using System.Diagnostics;

namespace KIDT.Services;

/// <summary>
/// Router-Service: Entscheidet intelligent, welches spezialisierte Modell verwendet wird.
/// - ToolSpecialistService (qwen2.5) für Analysen & Dateien
/// - ConversationService (llama3.1) für allgemeine Konversation
/// </summary>
public class ChatMcpService : IAsyncDisposable
{
    private ToolSpecialistService? _toolSpecialist; // Nullable: qwen2.5 für Dokumenten-Analyse
    private ConversationService? _conversation; // Nullable: llama3.1 für Konversation
    private bool _isInitialized = false; // Flag: Verhindert mehrfache Initialisierung
    private bool _hasUploadedFile = false; // Flag: Markiert ob Datei hochgeladen wurde

    // Schlüsselwörter-Array: Wenn enthalten ? Tool-Spezialist verwenden (erweitert für bessere Erkennung)
    private readonly string[] _analysisKeywords = new[]
    {
        // Analyse-Varianten
        "analysier", "analyse", "analysiere",
        
        // Text-Metriken
        "wörter", "wort", "zeichen", "text", "buchstaben",
        
        // Länge/Größe
        "länge", "lang", "wie lang", "wie gross", "wie groß", "größe",
        
        // Zählen
        "zähle", "zähl", "gezählt", "anzahl",
        
        // Messen/Bestimmen
        "messe", "mess", "ermittle", "bestimme",
        
        // Dokumente/Dateien
        "dokument", "datei", "lies", "lese", "liest", "öffne", "inhalt",
        
        // Prüfen/Überprüfen
        "prüfe", "prüf", "überprüfe", "check"
    };

    public async Task InitializeAsync() // Erstellt beide Service-Instanzen wenn noch nicht vorhanden
    {
        if (_isInitialized) return; // Guard: Wenn schon initialisiert ? raus

        try
        {
            Debug.WriteLine("[Router] Initialisiere spezialisierte Services..."); // Log-Ausgabe

            _toolSpecialist = new ToolSpecialistService(); // Erstelle qwen2.5 Service-Instanz
            _conversation = new ConversationService(); // Erstelle llama3.1 Service-Instanz

            _isInitialized = true; // Setze Flag auf true
            Debug.WriteLine("[Router] Router erfolgreich initialisiert."); // Log-Ausgabe: Erfolg
        }
        catch (Exception ex)
        {
            throw new Exception($"Fehler bei der Initialisierung von ChatMcpService Router: {ex.Message}", ex);
        }

        await Task.CompletedTask; // Async-Pattern: Dummy-Await
    }

    public async Task<string> SendAsync(string userMessage) // Routet Nachricht an qwen2.5 oder llama3.1 basierend auf Keyword-Match
    {
        if (!_isInitialized) // Check: Ist Router initialisiert?
        {
            await InitializeAsync(); // Nein ? Initialisiere jetzt
        }

        try
        {
            var useToolSpecialist = ShouldUseToolSpecialist(userMessage); // Routing-Entscheidung treffen

            if (useToolSpecialist) // Check: Tool-Spezialist verwenden?
            {
                // Log: Zeige erste 50 Zeichen der Nachricht
                Debug.WriteLine($"[Router] ? Tool-Spezialist (qwen2.5) für: {userMessage.Substring(0, Math.Min(50, userMessage.Length))}...");
                return await _toolSpecialist!.SendAsync(userMessage); // Leite an qwen2.5 weiter, ! = null-forgiving
            }
            else // Nein ? Konversation verwenden
            {
                // Log: Zeige erste 50 Zeichen der Nachricht
                Debug.WriteLine($"[Router] ? Konversation (llama3.1) für: {userMessage.Substring(0, Math.Min(50, userMessage.Length))}...");
                return await _conversation!.SendAsync(userMessage); // Leite an llama3.1 weiter, ! = null-forgiving
            }
        }
        catch (Exception ex)
        {
            return $"Fehler beim Routing: {ex.Message}";
        }
    }

    private bool ShouldUseToolSpecialist(string userMessage) // Gibt true wenn Keyword oder Datei-Upload, sonst false
    {
        if (_hasUploadedFile) // Check: Wurde Datei hochgeladen?
        {
            Debug.WriteLine("[Router] Datei-Upload erkannt ? Tool-Spezialist"); // Log-Ausgabe
            return true; // Ja ? immer Tool-Spezialist
        }

        var messageLower = userMessage.ToLowerInvariant(); // Konvertiere zu Lowercase für Case-insensitive Vergleich
        
        foreach (var keyword in _analysisKeywords) // Iteriere über alle Keywords
        {
            if (messageLower.Contains(keyword)) // Check: Enthält Nachricht dieses Keyword?
            {
                Debug.WriteLine($"[Router] Schlüsselwort '{keyword}' erkannt ? Tool-Spezialist"); // Log: Welches Keyword gefunden
                return true; // Ja ? Tool-Spezialist verwenden
            }
        }

        Debug.WriteLine("[Router] Keine Analyse-Indikatoren ? Konversation"); // Log: Kein Keyword gefunden
        return false; // Standard: Konversations-Modell
    }

    public void SetFileUploaded(bool hasFile) // Setzt Datei-Upload Flag für UI-Integration
    {
        _hasUploadedFile = hasFile; // Setze Flag
        Debug.WriteLine($"[Router] Datei-Upload Status: {hasFile}"); // Log: Neuer Status
    }

    public async ValueTask DisposeAsync() // Räumt beide Services auf wenn vorhanden
    {
        if (_toolSpecialist != null) // Check: Tool-Spezialist existiert?
        {
            await _toolSpecialist.DisposeAsync(); // Ja ? Dispose aufrufen
        }
        
        if (_conversation != null) // Check: Konversation-Service existiert?
        {
            await _conversation.DisposeAsync(); // Ja ? Dispose aufrufen
        }
    }
}
