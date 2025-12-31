# KIDT - KI Chat mit Dokumenten-Analyse

**.NET MAUI Chat-Anwendung mit Ollama und intelligentiem Modell-Routing**

---

## Überblick

KIDT ist eine Windows-Desktop-Chat-App mit zwei spezialisierten KI-Modellen:

| Modell         | Zweck                   | Temperature      |
|----------------|-------------------------|------------------|
| **llama3.1**   | Allgemeine Konversation | 0.5 (ausgewogen) |
| **qwen2.5:7b** | Dokumenten-Analyse      | 0.3 (präzise)    |

Ein Router entscheidet automatisch anhand von Keywords, welches Modell genutzt wird.

---

## Architektur

```
??????????????????????????????????????????????????????????????
?                         Benutzer                           ?
?                    tippt Nachricht ein                     ?
??????????????????????????????????????????????????????????????
                       ?
                       ?
           ?????????????????????????
           ?    Home.razor (UI)    ?
           ?  - Chat-History       ?
           ?  - Input-Textfeld     ?
           ?  - Upload-Button      ?
           ?????????????????????????
                       ?
                       ?
           ?????????????????????????
           ?   ChatMcpService      ?
           ?      (Router)         ?
           ?  analysiert Keywords  ?
           ?????????????????????????
                       ?
        ???????????????????????????????
        ?                             ?
        ?                             ?
??????????????????          ????????????????????
? Conversation   ?          ? ToolSpecialist   ?
? Service        ?          ? Service          ?
?                ?          ?                  ?
? llama3.1       ?          ? qwen2.5:7b       ?
? - Small Talk   ?          ? - Analyse        ?
? - Temp: 0.5    ?          ? - Temp: 0.3      ?
? - MaxTokens:   ?          ? - MaxTokens:     ?
?   dynamisch    ?          ?   GPU-optimiert  ?
??????????????????          ????????????????????
                                     ?
                                     ?
                            ???????????????????
                            ? McpToolsRegistry?
                            ? (Tool-System)   ?
                            ? bereit für      ?
                            ? zukünftige      ?
                            ? Tools           ?
                            ???????????????????
```

---

## Routing-Logik

```
User-Nachricht
     ?
     ?
??????????????????????????????????
? Enthält Analyse-Keywords?      ?
? (analysier, datei, wörter,     ?
?  zeichen, länge, zähle, etc.)  ?
??????????????????????????????????
     ?                       ?
     ? Ja                    ? Nein
     ?                       ?
???????????????      ????????????????
? qwen2.5:7b  ?      ?  llama3.1    ?
? (Analyse)   ?      ? (Gespräch)   ?
???????????????      ????????????????
```

**Routing-Keywords:**
- analysier, analyse, wörter, zeichen, text
- länge, größe, zähle, anzahl
- dokument, datei, lies, öffne
- prüfe, überprüfe, check

**Bei angehängter Datei:** Immer qwen2.5:7b

---

## Projektstruktur

```
KIDT/
?
??? Components/
?   ??? Pages/
?       ??? Home.razor              UI (Chat, Loading, Typewriter)
?       ??? Home.razor.css          Styles (Bubbles, Animation)
?
??? Platforms/Windows/
?   ??? ChatMcpService.cs           Router (Keyword-Analyse)
?   ??? ConversationService.cs      llama3.1 (Chat)
?   ??? ToolSpecialistService.cs    qwen2.5 (Analyse)
?   ??? FileService.cs              Datei-Upload (PDF/TXT)
?   ??? McpToolsRegistry.cs         Tool-Registration (leer)
?
??? Services/
?   ??? ChatDbService.cs            PostgreSQL (Chat-History)
?
??? Data/
?   ??? ChatDbContext.cs            EF Core Context
?
??? Models/
?   ??? Conversation.cs             Chat-Sitzung
?   ??? Message.cs                  Einzelne Nachricht
?
??? Prompts/
?   ??? conversation-instructions.md    System-Prompt llama3.1
?   ??? tool-specialist-instructions.md System-Prompt qwen2.5
?
??? McpServer/
    ??? Program.cs                  MCP-Server (aktuell leer)
```

---

## Nachrichtenfluss

```
1. User tippt "Analysiere diesen Text"
          ?
          ?
2. Home.razor
   ?? Zeige User-Nachricht
   ?? Zeige Loading-Animation (blinkender Cursor)
   ?? Rufe Chat.SendAsync()
          ?
          ?
3. ChatMcpService (Router)
   ?? Prüfe Keywords ? "analysiere" gefunden
   ?? Leite zu ToolSpecialistService weiter
          ?
          ?
4. ToolSpecialistService
   ?? Zähle Wörter in Nachricht
   ?? Berechne MaxTokens (GPU-optimiert)
   ?? Sende an qwen2.5:7b
   ?? Erhalte Antwort
          ?
          ?
5. Home.razor
   ?? Entferne Loading-Animation
   ?? Zeige Antwort mit Typewriter-Effekt
   ?? Speichere in Datenbank
```

---

## Token-Limits (GPU-optimiert)

### ConversationService (llama3.1)

| Wortanzahl | MaxTokens | Verwendung           |
|------------|-----------|----------------------|
| ? 5        | 20        | "Hallo", "Danke"     |
| ? 15       | 80        | Kurze Fragen         |
| ? 50       | 200       | Mittlere Fragen      |
| ? 500      | 400       | Lange Texte          |
| > 500      | 800       | Sehr lange Dokumente |

### ToolSpecialistService (qwen2.5:7b)

| Wortanzahl | MaxTokens | Verwendung           |
|------------|-----------|----------------------|
| ? 10       | 150       | "Analysiere: Test"   |
| ? 30       | 350       | Kurze Analysen       |
| ? 100      | 600       | Mittlere Analysen    |
| ? 500      | 1200      | Code-Dateien         |
| ? 2000     | 2000      | Lange PDFs           |
| > 2000     | 3000      | Sehr große Dokumente |


---

## Hybrid-MCP Ansatz

### Warum Hybrid?

**Problem mit klassischem MCP:**
- Client-API (v0.5.0) ist instabil und experimentell
- Client-Packages für .NET MAUI waren nicht möglich korrekt zu referenzieren
- stdio-Kommunikation mit Server ? 50ms Latenz pro Tool-Aufruf
- Komplexes Setup mit externem Server-Prozess

**Unsere Lösung:**
```
MCP-Standard (Tool-Definitionen)
          ?
          ?? McpServer/Program.cs        (MCP-Server, aktuell leer)
          ?                               [McpServerTool]-Attribute
          ?
          ?? McpToolsRegistry.cs          (Manuelle Registrierung)
                                           für Semantic Kernel
```

**Vorteile:**
- Tools laufen in-process ? <1ms Latenz (statt 50ms)
- Kein externes Server-Management nötig
- Direkter Code-Zugriff für Debugging
- Funktioniert ohne MCP-Client-API

**Nachteile:**
- Tool-Definitionen müssen synchron gehalten werden
- Externe MCP-Clients können unsere Tools nicht nutzen

**Zukunft:** Sobald stabile Client-Packages verfügbar sind, kann auf echtes MCP umgestellt werden.

---

## Features

### 1. Chat-UI
- Message-Bubbles (User links, Assistant rechts)
- Loading-Animation (blinkender Cursor)
- Typewriter-Effekt bei Antworten (50ms pro Zeichen)
- Auto-wachsendes Textarea (1-19 Zeilen)

### 2. Datei-Upload
- Unterstützte Formate: PDF, TXT, MD, JSON
- Badge-Anzeige bei angehängter Datei
- Text-Extraktion mit FileService
- Datei bleibt für Follow-up Fragen verfügbar

### 3. Datenbank
- PostgreSQL mit Entity Framework Core
- Chat-History persistent gespeichert
- Conversations und Messages

### 4. Performance
- Background Pre-warm: Modelle laden parallel zu UI-Start
- Modelle bleiben im VRAM nach Nutzung
- GPU-optimierte Token-Limits

---

## Setup

### 1. Ollama installieren und Modelle laden
```bash
ollama pull llama3.1
ollama pull qwen2.5:7b
ollama serve
```

### 2. Optional: Keep-Alive setzen (Modelle bleiben länger im RAM)
```powershell
$env:OLLAMA_KEEP_ALIVE="30m"
```

### 3. PostgreSQL konfigurieren
Connection String in `ChatDbContext.cs` anpassen:
```csharp
optionsBuilder.UseNpgsql("Host=localhost;Database=kidt;...");
```

### 4. Projekt bauen und starten
```bash
cd KIDT
dotnet build
dotnet run
```

---

## Tech-Stack

| Komponente      | Version               | Zweck                                   |
|-----------------|-----------------------|-----------------------------------------|
| .NET MAUI       | 10.0                  | Cross-Platform UI (nur Windows genutzt) |
| Semantic Kernel | 1.68.0                | KI-Orchestrierung                       |
| Ollama          | Latest                | Lokale LLM-Ausführung                   |
| PostgreSQL      | 10.0                  | Datenbank                               |
| PdfPig          | 0.1.9                 | PDF-Text-Extraktion                     |
| MCP             | 0.5.0                 | Tool-Standard (nur Definitionen)        |

---

## Performance-Zahlen

| Szenario            | Zeit    | VRAM   | Info               |
|---------------------|---------|--------|--------------------|
| App-Start           | <1 Sek  | 0 GB   | UI sofort sichtbar |
| Erste Nachricht     | ~10 Sek | 2-6 GB | Modell lädt        |
| Weitere Nachrichten | 2-3 Sek | 2-6 GB | Modell bereit      |
| Nach 30 Min Idle    | 2-3 Sek | 2-6 GB | Keep-Alive         |

---

## Zusammenfassung

KIDT kombiniert zwei spezialisierte LLMs in einer Desktop-App:

1. **Intelligentes Routing:** Keywords bestimmen automatisch das beste Modell
2. **Optimierte Performance:** GPU-Limits, Pre-warm, Keep-Alive
3. **Hybrid-MCP:** Nutzt MCP-Standard ohne instabile Client-API
4. **Einfache UI:** Chat-Bubbles, Typewriter, Datei-Upload
5. **Persistenz:** PostgreSQL für Chat-History

**Hauptvorteil:** Schnelle Konversation (llama3.1) + präzise Analyse (qwen2.5) in einer App.