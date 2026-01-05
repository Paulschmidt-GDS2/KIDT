# KIDT - KI-gestützter Dokumenten- und Terminmanger

.NET MAUI Desktop-App mit intelligenter KI-Modell-Auswahl für Windows

---

## Projektübersicht

KIDT kombiniert zwei spezialisierte KI-Modelle mit einem GPT-4o-mini Router-Agent:
- **phi3:mini** - Natürliche Konversation (Temperature: 0.5, 20-400 Tokens)
- **qwen2.5:7b** - Präzise Daten-Analyse (Temperature: 0.3, 1000-3000 Tokens)
- **gpt-4o-mini** - Router-Agent über OpenAI API (entscheidet bei jeder Nachricht)

---

## Projektstruktur

```
KIDT/
|
+-- Components/Pages/
|   +-- Home.razor              Chat-Oberfläche
|   +-- Home.razor.css          Styling
|
+-- Platforms/Windows/
|   +-- ChatCoordinator.cs      Koordiniert Router + Modelle + DB + Files
|   +-- RouterService.cs        GPT-4o-mini Router-Agent (OpenAI API)
|   +-- ConversationService.cs  phi3:mini Service
|   +-- DataAnalysisService.cs  qwen2.5 Service
|   +-- FileService.cs          Datei-Upload Handler
|   +-- McpToolsRegistry.cs     Tool-Registrierung
|
+-- Database/
|   +-- ChatDbContext.cs        EF Core Context
|   +-- ChatDbService.cs        Datenbank-Zugriff
|   +-- Conversation.cs         Chat-Sitzung
|   +-- Message.cs              Einzelne Nachricht
|   +-- UploadedFile.cs         Hochgeladene Datei
|
+-- Prompts/
|   +-- conversation-instructions.md      System-Prompt phi3:mini
|   +-- data-analysis-instructions.md     System-Prompt qwen2.5
|
+-- McpServer/
    +-- Program.cs              MCP-Server (aktuell leer)
```

---

## Architektur

```
+-------------------+
|   Benutzer-UI     |
|   Home.razor      |
+-------------------+
         |
         v
+-------------------+
| ChatCoordinator   |
|  (Orchestrator)   |
+-------------------+
         |
         v
+-------------------+
|  RouterService    |
|  (GPT-4o-mini)    |
|  OpenAI API       |
+-------------------+
         |
         +---------------+
         |               |
         v               v
+-------------------+  +-------------------+
| Conversation      |  | DataAnalysis      |
| Service           |  | Service           |
|                   |  |                   |
| phi3:mini         |  | qwen2.5:7b        |
| Temp: 0.5         |  | Temp: 0.3         |
| 20-400 Tokens     |  | 1000-3000 Tokens  |
+-------------------+  +-------------------+
         |                      |
         v                      v
  Keine History         +-------------------+
                        | Komplette Chat-   |
                        | History aus DB    |
                        +-------------------+
                                |
                                v
                       +-------------------+
                       | McpToolsRegistry  |
                       |  (Tool-System)    |
                       +-------------------+
```

---

## Routing-Logik (GPT-4o-mini)

```
User-Nachricht
      |
      v
+-------------------+
| ChatCoordinator   |
+-------------------+
      |
      v
+-------------------+
| RouterService     |
| (GPT-4o-mini API) |
+-------------------+
      |
      | Analysiert:
      | - User-Nachricht
      | - Datei angehängt?
      |
      v
+----------------------------------------+
| Router-Entscheidung                    |
| - Service: conversation/dataAnalysis   |
| - MaxTokens: 20-3000                   |
| - Reasoning: Begründung                |
+----------------------------------------+
      |
      +---------------+
      |               |
      v               v
  Conversation    DataAnalysis
  (phi3:mini)     (qwen2.5)
  20-400 Tokens   1000-3000 Tokens
```

**Router-Prinzip:**
- Bei **jeder** User-Nachricht fragt ChatCoordinator den Router-Agent
- Router bekommt: User-Nachricht + hasFile Flag
- Router gibt zurück: Service-Name + Token-Limit + Begründung
- Conversation bekommt: Nur aktuelle User-Nachricht
- DataAnalysis bekommt: Komplette Chat-History aus DB + Datei-Inhalt

---

## Nachrichtenfluss

```
User gibt Nachricht ein
         |
         v
Home.razor
  - Zeigt User-Nachricht
  - Startet Loading-Animation
  - Ruft ChatCoordinator auf
         |
         v
ChatCoordinator
  - Ruft RouterService auf
  - Holt Chat-History (nur für DataAnalysis)
  - Leitet an gewähltes Modell weiter
         |
         v
RouterService (GPT-4o-mini)
  - Analysiert User-Nachricht
  - Entscheidet: conversation oder dataAnalysis
  - Bestimmt MaxTokens: 20-3000
         |
         v
Service (phi3:mini oder qwen2.5)
  - phi3:mini: Nur aktuelle Nachricht
  - qwen2.5: Komplette History + Datei
  - Sendet Anfrage an Ollama
  - Erhält Antwort
         |
         v
Home.razor
  - Stoppt Loading-Animation
  - Zeigt Antwort mit Typewriter-Effekt
  - Speichert in Datenbank
```

---

## Token-Limits (Router-gesteuert)

**ConversationService (phi3:mini)**

| Token-Range | Verwendung                                                  |
|-------------|-------------------------------------------------------------|
| 20-50       | Sehr kurze Antworten (Hallo, Danke, Ja/Nein)                |
| 80-150      | Kurze Erklärungen (1-2 Sätze, max 40 Wörter)                |
| 200-400     | Ausführlichere Antworten (nur bei "genauer", "ausführlich") |

**DataAnalysisService (qwen2.5:7b)**

| Token-Range | Verwendung                                      |
|-------------|-------------------------------------------------|
| 1000-1500   | Standard-Analysen                               |
| 2000-3000   | Detaillierte Analysen und komplexe Berechnungen |

**Router entscheidet dynamisch** basierend auf User-Nachricht!

---

## Hybrid-MCP Ansatz

**Problem mit klassischem MCP:**
- MCP Client-API (v0.5.0) ist experimentell und instabil
- .NET MAUI Package-Referenzen konnten nicht korrekt integriert werden
- stdio-Kommunikation hatte 50ms Latenz pro Tool-Aufruf
- Komplexes Setup mit Prozess-Management

**Unsere Lösung:**
- Statt MCP-Client verwenden wir direkte Tool-Registrierung in Semantic Kernel. 
- Tools laufen im gleichen Prozess wie die App, wodurch die stdio-Kommunikation entfällt.
- In `McpServer/Program.cs` liegen die originalen MCP-Tool-Definitionen mit `[McpServerTool]`-Attributen. 
- Diese werden in `McpToolsRegistry.cs` manuell für Semantic Kernel registriert und vom `DataAnalysisService` genutzt.


```
MCP-Standard                    Tool-Definitionen
     |                                  |
     v                                  v
McpServer/Program.cs            McpToolsRegistry.cs
(MCP-Tool-Definitionen)         (Tools-Registrierung für Semantic Kernel)
     |                                  |
     +----------------------------------+
        Muss synchron gehalten werden
                       |
                       v
           DataAnalysisService.cs
           (Nutzt registrierte Tools)
```

**Vorteile:** Tools laufen in-process (unter 1ms statt 50ms), kein Server-Management, direkter Code-Zugriff, funktioniert ohne instabile Client-API

**Nachteil:** Externe MCP-Clients können Tools nicht nutzen und beide Code-Teile müssen synchron gehalten werden.

---

## Wichtige Code-Dateien

**ChatCoordinator.cs** - Orchestrator für Router, Modelle, DB und Files
- `InitializeAsync()` - Initialisiert alle Services
- `SendAsync()` - Koordiniert Router-Call und Modell-Auswahl
- `UploadFileAsync()` - Lädt Datei und extrahiert Text
- `ClearFile()` - Entfernt angehängte Datei

**RouterService.cs** - GPT-4o-mini Router-Agent über OpenAI API
- Analysiert User-Nachricht bei jedem Call
- Entscheidet: conversation oder dataAnalysis
- Bestimmt dynamisches Token-Limit (20-3000)

**ConversationService.cs** - Service für natürliche Konversation (phi3:mini, Temp: 0.5, keine Tools, keine History)

**DataAnalysisService.cs** - Service für Daten-Analyse (qwen2.5:7b, Temp: 0.3, MCP-Tools aktiv, komplette Chat-History)

**FileService.cs** - Extrahiert Text aus PDF/TXT/MD/JSON (Max. 4 MB, Warnung bei über 3000 Wörtern)

**Home.razor** - Chat-UI mit Message-Bubbles, Loading-Animation, Typewriter-Effekt, Auto-wachsendes Textarea

**ChatDbService.cs** - Datenbank-Service (PostgreSQL, EF Core) - CRUD-Operationen

**ChatDbContext.cs** - EF Core Context für Datenbank-Verbindung

---

## Setup

**1. Ollama installieren**
```bash
ollama pull phi3:mini
ollama pull qwen2.5:7b
ollama serve
```

**2. OpenAI API Key setzen**
```bash
# In RouterService.cs den API-Key eintragen
string apiKey = "sk-proj-...";
```

**3. Optional: Keep-Alive**
```powershell
$env:OLLAMA_KEEP_ALIVE="30m"
ollama serve
```

**4. PostgreSQL konfigurieren**
```csharp
options.UseNpgsql("Host=localhost;Database=KIDT_Chats;Username=KIDT_App;Password=kidt123");
```

**5. Projekt starten**
```bash
cd KIDT
dotnet build
dotnet run
```

---

## Tech-Stack

| Komponente      | Version | Zweck                 |
|-----------------|---------|-----------------------|
| .NET MAUI       | 10.0    | UI-Framework          |
| Semantic Kernel | 1.68.0  | KI-Orchestrierung     |
| OpenAI SDK      | Latest  | Router-Agent API      |
| Ollama          | Latest  | Lokale LLM-Ausführung |
| PostgreSQL      | 10.0    | Datenbank             |
| PdfPig          | 0.1.9   | PDF-Text-Extraktion   |
| EF Core         | 10.0    | ORM                   |
| MCP Standard    | 0.5.0   | Tool-Definitionen     |

---

## Performance

| Szenario            | Zeit     | VRAM   |
|---------------------|----------|--------|
| App-Start           | unter 1s | 0 GB   |
| Router-Call         | 0.5-1s   | 0 GB   |
| Erste Nachricht     | ca. 10s  | 2-6 GB |
| Weitere Nachrichten | 2-3s     | 2-6 GB |
| Nach 30 Min Idle    | 2-3s     | 2-6 GB |

---

## Features

**Chat-Oberfläche:** Message-Bubbles, blinkender Cursor (800ms), Typewriter-Effekt, auto-wachsendes Eingabefeld, Enter = senden

**Datei-Upload:** PDF/TXT/MD/JSON (max. 4 MB), Badge-Anzeige, Text-Extraktion, bleibt für Follow-up verfügbar

**Intelligentes Routing:** GPT-4o-mini Router-Agent entscheidet bei jeder Nachricht, dynamische Token-Limits (20-3000), Begründung für Routing-Entscheidung

**Datenbank:** PostgreSQL mit EF Core, persistente Chat-History, getrennte Conversations/Messages

**Performance:** Background Pre-warm, Keep-Alive, GPU-optimierte Token-Limits, In-Process Tools (unter 1ms Latenz)

---

## Zusammenfassung

KIDT kombiniert zwei KI-Modelle mit einem intelligenten Router-Agent:

1. **GPT-4o-mini Router** - Entscheidet bei jeder Nachricht über OpenAI API
2. **Zwei spezialisierte Modelle** - phi3:mini (Conversation) + qwen2.5 (Daten-Analyse)
3. **Unterschiedliche Kontexte** - Conversation: nur aktuelle Nachricht, Analyse: komplette History
4. **Hybrid-MCP** - Nutzt MCP-Standard ohne instabile Client-API
5. **Optimierte Performance** - GPU-Limits, Pre-warm, Keep-Alive, In-Process Tools
6. **Benutzerfreundliche UI** - Chat-Bubbles, Typewriter, Datei-Upload
7. **Persistente Speicherung** - PostgreSQL für Chat-History

**Hauptvorteil:** Intelligente Modell-Auswahl durch GPT-4o-mini Router bei jeder Nachricht, schnelle Konversation (phi3:mini) + präzise Daten-Analyse (qwen2.5) mit kompletter History.