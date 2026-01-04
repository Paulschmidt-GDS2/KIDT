using OpenAI;
using OpenAI.Chat;
using System.Text.Json;

namespace KIDT.Services;

public class RouterService // Klasse für Routing-Entscheidungen
{
    private OpenAIClient client; // OpenAI Client Instanz
    private ChatClient router; // Chat Client für GPT-4o-mini
    private bool isInitialized = false; // Flag ob Service initialisiert ist

    public RouterService() // Konstruktor
    {
        this.client = null!; // Client auf null setzen (wird später initialisiert)
        this.router = null!; // Router auf null setzen (wird später initialisiert)
    }

    public async Task InitializeAsync() // Initialisierung des Services
    {
        if (this.isInitialized) // Wenn bereits initialisiert
        {
            return; // Dann abbrechen
        }

        try
        {
            string keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openai-api-key.txt"); // Pfad zur API-Key-Datei
            string apiKey = await File.ReadAllTextAsync(keyPath); // API-Key aus Datei lesen

            this.client = new OpenAIClient(apiKey.Trim()); // OpenAI Client mit API Key erstellen
            this.router = this.client.GetChatClient("gpt-4o-mini"); // Chat Client für gpt-4o-mini Modell holen
            this.isInitialized = true; // Flag auf true setzen - Initialisierung abgeschlossen
        }
        catch (Exception ex)
        {
            throw new Exception($"Router-Fehler: {ex.Message}");
        }

        await Task.CompletedTask; // Async-Methode abschließen
    }

    public async Task<RoutingDecision> RouteAsync(string userMessage, bool hasFile) // Hauptmethode für Routing
    {
        if (!this.isInitialized) // Wenn noch nicht initialisiert
        {
            await InitializeAsync(); // Dann erst initialisieren
        }

        return await RouteWithApiAsync(userMessage, hasFile); // API-Call ausführen und Ergebnis zurückgeben
    }

    private async Task<RoutingDecision> RouteWithApiAsync(string userMessage, bool hasFile) // API-Call durchführen
    {
        try
        {
            string prompt = BuildRoutingPrompt(userMessage, hasFile); // Prompt für Router erstellen

            ChatMessage[] messages = new ChatMessage[] // Chat-Nachrichten Array erstellen
            {
                new SystemChatMessage("Du bist ein Router-Agent. Antworte NUR mit JSON."), // System-Anweisung
                new UserChatMessage(prompt) // User-Nachricht mit Prompt
            };

            ChatCompletion response = await this.router.CompleteChatAsync(messages); // API-Call an OpenAI durchführen
            string jsonResponse = response.Content[0].Text!; // JSON-Antwort aus Response extrahieren (nicht null)

            return ParseRoutingDecision(jsonResponse); // JSON parsen und RoutingDecision zurückgeben
        }
        catch (Exception ex)
        {
            throw new Exception($"Router-Fehler: {ex.Message}");
        }
    }

    private string BuildRoutingPrompt(string message, bool hasFile) // Prompt für Router-Agent erstellen
    {
        string fileInfo = "Nein"; // Standard: Keine Datei
        if (hasFile) // Wenn Datei angehängt ist
        {
            fileInfo = "Ja"; // Datei-Info auf "Ja" setzen
        }

        return $@"Analysiere diese User-Nachricht und entscheide: // Prompt-Text mit Anweisungen

**SERVICES:** // Welche Services verfügbar sind
- ""conversation"": Für Small Talk, Fragen, Danke/Bitte, allgemeine Konversation, Erklärungen // Conversation Service
- ""toolSpecialist"": Für Analysen, Berechnungen, Datei-Verarbeitung, technische Aufgaben // Tool Specialist Service

**MAXTOKEN-RICHTLINIEN:** // Token-Limits für Antworten

Für CONVERSATION:
- 20-50: Sehr kurze Antworten (Hallo, Danke, Ja/Nein) // Sehr kurze Antworten
- 80-150: Kurze Erklärungen (1-2 Sätze, max 40 Wörter) // Kurze Erklärungen
- 200-400: Ausführlichere Antworten (nur wenn User ""ausführlich"" oder ""genauer"" sagt) // Ausführlichere Antworten

Für TOOLSPECIALIST:
- 1000-1500: Standard-Analysen
- 2000-3000: Detaillierte Analysen und komplexe Berechnungen

**KONTEXT:** // Kontext-Informationen
Nachricht: ""{message}"" // User-Nachricht einsetzen
Datei angehängt: {fileInfo} // Datei-Status einsetzen

**WICHTIG:**  // Wichtige Hinweise
- Auch wenn Datei angehängt ist: Nutze ""conversation"" für Small Talk wie Danke/Hallo/Tschüss // Small Talk immer zu Conversation
- Conversation-Antworten sollen kurz sein (max 40 Wörter) // Standard = kurz
- ToolSpecialist braucht mehr Tokens für gründliche Analysen // Nur bei explizitem Wunsch mehr Tokens

Antworte NUR mit JSON: // JSON-Format vorgeben
{{
  ""service"": ""conversation"" oder ""toolSpecialist"", // Service-Wahl
  ""maxTokens"": 20-3000, // Token-Limit
  ""reasoning"": ""1-2 Sätze"" // Begründung
}}";
    }

    private RoutingDecision ParseRoutingDecision(string jsonResponse) // JSON-Antwort parsen
    {
        try
        {
            string cleanJson = jsonResponse.Trim(); // Whitespace entfernen
            
            if (cleanJson.StartsWith("```")) // Wenn JSON in Code-Block eingepackt ist
            {
                string[] lines = cleanJson.Split('\n'); // In Zeilen aufteilen
                cleanJson = string.Join("\n", lines.Skip(1).Take(lines.Length - 2)); // Erste und letzte Zeile entfernen
            }
            
            cleanJson = cleanJson.Trim(); // Nochmal Whitespace entfernen

            JsonSerializerOptions options = new JsonSerializerOptions // JSON Serializer Optionen erstellen
            {
                PropertyNameCaseInsensitive = true // Groß-/Kleinschreibung ignorieren
            };

            RoutingJson decision = JsonSerializer.Deserialize<RoutingJson>(cleanJson, options)!; // JSON zu Objekt deserialisieren (nicht null)
            return new RoutingDecision(decision.service, decision.maxTokens, decision.reasoning); // RoutingDecision Record erstellen und zurückgeben
        }
        catch (Exception ex)
        {
            throw new Exception($"Router-Parsing-Fehler: {ex.Message}");
        }
    }

    private class RoutingJson // Interne Klasse für JSON-Deserialisierung
    {
        public string service { get; set; } = string.Empty; // Service-Name aus JSON
        public int maxTokens { get; set; } // Token-Limit aus JSON
        public string reasoning { get; set; } = string.Empty; // Begründung aus JSON
    }
}

public record RoutingDecision(string Service, int MaxTokens, string Reasoning); // Record für Routing-Entscheidung mit Service, MaxTokens und Reasoning
