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
            string jsonResponse = response.Content[0].Text!; // JSON-Antwort aus Response extrahieren (nicht null deshalb !)

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

**SERVICES:**
- ""conversation"": Für Small Talk, Fragen, Danke/Bitte, allgemeine Konversation, Erklärungen
- ""dataAnalysis"": Für Analysen, Berechnungen, Datei-Verarbeitung, technische Aufgaben

**MAXTOKEN-RICHTLINIEN:**

Für CONVERSATION:
- 40-80: Sehr kurze Antworten (Hallo, Danke, Ja/Nein)
- 120-200: Kurze Erklärungen (1-2 Sätze, max 40 Wörter)
- 250-500: Ausführlichere Antworten (nur wenn User ""ausführlich"" oder ""genauer"" sagt)

Für DATAANALYSIS:
- 1000-1500: Standard-Analysen
- 2000-3000: Detaillierte Analysen und komplexe Berechnungen

**KONTEXT:**
Nachricht: ""{message}""
Datei angehängt: {fileInfo}

**WICHTIG:**
- Auch wenn Datei angehängt ist: Nutze ""conversation"" für Small Talk wie Danke/Hallo/Tschüss
- Conversation-Antworten sollen kurz sein (max 40 Wörter)
- DataAnalysis braucht mehr Tokens für gründliche Analysen
- Gib phi3:mini genug Tokens für vollständige Sätze (mindestens 40-80)

Antworte NUR mit JSON:
{{
  ""service"": ""conversation"" oder ""dataAnalysis"",
  ""maxTokens"": 40-3000,
  ""reasoning"": ""1-2 Sätze""
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

            RoutingJson decision = JsonSerializer.Deserialize<RoutingJson>(cleanJson, options)!; // JSON zu Objekt deserialisieren (nicht null deshalb !)
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

public record RoutingDecision(string Service, int MaxTokens, string Reasoning); // Record Methode für Routing-Entscheidung mit Service, MaxTokens und Reasoning
