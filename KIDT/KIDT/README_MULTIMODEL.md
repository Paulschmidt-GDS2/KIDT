# KIDT - KI-gestützter Dokumenten- und Terminmanger

.NET MAUI Desktop-App mit intelligenter KI-Modell-Auswahl fur Windows

---

## Projektubersicht

KIDT kombiniert zwei spezialisierte KI-Modelle:
- **llama3.1** - Naturliche Konversation (Temperature: 0.5)
- **qwen2.5:7b** - Prazise Dokumenten-Analyse (Temperature: 0.3)

Ein Router-System wahlt automatisch das optimale Modell.

---

## Projektstruktur

```
KIDT/
?
??? Components/Pages/
?   ??? Home.razor              Chat-Oberflache
?   ??? Home.razor.css          Styling
?
??? Platforms/Windows/
?   ??? ChatMcpService.cs       Router zwischen Modellen
?   ??? ConversationService.cs  llama3.1 Service
?   ??? ToolSpecialistService.cs qwen2.5 Service
?   ??? FileService.cs          Datei-Upload Handler
?   ??? McpToolsRegistry.cs     Tool-Registrierung
?
??? Services/
?   ??? ChatDbService.cs        Datenbank-Zugriff
?
??? Data/
?   ??? ChatDbContext.cs        EF Core Context
?
??? Models/
?   ??? Conversation.cs         Chat-Sitzung
?   ??? Message.cs              Einzelne Nachricht
?   ??? UploadedFile.cs         Hochgeladene Datei
?
??? Prompts/
?   ??? conversation-instructions.md      System-Prompt llama3.1
?   ??? tool-specialist-instructions.md   System-Prompt qwen2.5
?
??? McpServer/
    ??? Program.cs              MCP-Server (aktuell leer)
```

---

## Architektur

```
????????????????????
?   Benutzer-UI    ?
?   Home.razor     ?
????????????????????
         ?
         ?
????????????????????
? ChatMcpService   ?
?    (Router)      ?
????????????????????
         ?
         ???????????????????????
         ?                     ?
         ?                     ?
????????????????????  ????????????????????
? Conversation     ?  ? ToolSpecialist   ?
? Service          ?  ? Service          ?
?                  ?  ?                  ?
? llama3.1         ?  ? qwen2.5:7b       ?
? Temp: 0.5        ?  ? Temp: 0.3        ?
? Chat-Modus       ?  ? Analyse-Modus    ?
????????????????????  ????????????????????
                               ?
                               ?
                      ????????????????????
                      ? McpToolsRegistry ?
                      ?  (Tool-System)   ?
                      ????????????????????
```

---

## Routing-Logik

```
User-Nachricht
      ?
      ?
???????????????????
? Datei angehangt??
???????????????????
     ?        ?
    Ja       Nein
     ?        ?
     ?        ?
  ??????  ????????????????????
  ?qwen?  ? Keywords erkannt??
  ??????  ? (analysier, zahl,?
          ? wort, datei, etc)?
          ????????????????????
               ?        ?
              Ja       Nein
               ?        ?
               ?        ?
            ??????   ???????
            ?qwen?   ?llama?
            ??????   ???????
```

---

## Nachrichtenfluss

```
User gibt Nachricht ein
         ?
         ?
Home.razor
  ? Zeigt User-Nachricht
  ? Startet Loading-Animation
  ? Ruft ChatMcpService auf
         ?
         ?
ChatMcpService (Router)
  ? Analysiert Keywords
  ? Wahlt Modell
         ?
         ?
Service (llama3.1 oder qwen2.5)
  ? Berechnet Token-Limit
  ? Sendet Anfrage an Ollama
  ? Erhalt Antwort
         ?
         ?
Home.razor
  ? Stoppt Loading-Animation
  ? Zeigt Antwort mit Typewriter-Effekt
  ? Speichert in Datenbank
```

---

## Token-Limits

**ConversationService (llama3.1)**

| Worter   | Tokens | Verwendung   |
|----------|--------|--------------|
| bis 5    | 20     | Hallo, Danke |
| bis 15   | 80     | Kurze Fragen |
| bis 50   | 200    | Mittlere     |
| bis 500  | 400    | Lange Texte  |
| uber 500 | 800    | Dokumente    |

**ToolSpecialistService (qwen2.5:7b)**

| Worter    | Tokens | Verwendung      |
|-----------|--------|-----------------|
| bis 10    | 150    | Kurz-Analysen   |
| bis 30    | 350    | Mittlere        |
| bis 100   | 600    | Code-Analysen   |
| bis 500   | 1200   | Dokumente       |
| bis 2000  | 2000   | Lange PDFs      |
| uber 2000 | 3000   | Sehr grosse PDFs|

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
- Diese werden in `McpToolsRegistry.cs` manuell für Semantic Kernel registriert und vom `ToolSpecialistService` genutzt.


```
MCP-Standard                    Tool-Definitionen
     ?                                  ?
     ?                                  ?
McpServer/Program.cs            McpToolsRegistry.cs
(MCP-Tool-Definitionen)         (Tools-Registrierung für Semantic Kernel)

     ????????????????????????????????????
        Muss synchron gehalten werden
                       ?
                       ?
           ToolSpecialistService.cs
           (Nutzt registrierte Tools)
```

**Vorteile:** Tools laufen in-process (unter 1ms statt 50ms), kein Server-Management, direkter Code-Zugriff, funktioniert ohne instabile Client-API

**Nachteil:** Externe MCP-Clients konnen Tools nicht nutzen und beide Code-Teile mussen synchron gehalten werden.

---

## Wichtige Code-Dateien

**ChatMcpService.cs** - Router zwischen llama3.1 und qwen2.5
- `InitializeAsync()` - Initialisiert beide Modelle
- `SendAsync()` - Routet Nachricht zum passenden Modell
- `UploadFileAsync()` - Ladt Datei und extrahiert Text
- `ClearFile()` - Entfernt angehangte Datei

**ConversationService.cs** - Service fur naturliche Konversation (llama3.1, Temp: 0.5, keine Tools)

**ToolSpecialistService.cs** - Service fur Dokumenten-Analyse (qwen2.5:7b, Temp: 0.3, MCP-Tools aktiv)

**FileService.cs** - Extrahiert Text aus PDF/TXT/MD/JSON (Max. 4 MB, Warnung bei uber 3000 Wortern)

**Home.razor** - Chat-UI mit Message-Bubbles, Loading-Animation, Typewriter-Effekt, Auto-wachsendes Textarea

**ChatDbService.cs** - Datenbank-Service (PostgreSQL, EF Core)

---

## Setup

**1. Ollama installieren**
```bash
ollama pull llama3.1
ollama pull qwen2.5:7b
ollama serve
```

**2. Optional: Keep-Alive**
```powershell
$env:OLLAMA_KEEP_ALIVE="30m"
ollama serve
```

**3. PostgreSQL konfigurieren**
```csharp
options.UseNpgsql("Host=localhost;Database=KIDT_Chats;Username=KIDT_App;Password=kidt123");
```

**4. Projekt starten**
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
| Ollama          | Latest  | Lokale LLM-Ausfuhrung |
| PostgreSQL      | 10.0    | Datenbank             |
| PdfPig          | 0.1.9   | PDF-Text-Extraktion   |
| EF Core         | 10.0    | ORM                   |
| MCP Standard    | 0.5.0   | Tool-Definitionen     |

---

## Performance

| Szenario            | Zeit     | VRAM   |
|---------------------|----------|--------|
| App-Start           | unter 1s | 0 GB   |
| Erste Nachricht     | ca. 10s  | 2-6 GB |
| Weitere Nachrichten | 2-3s     | 2-6 GB |
| Nach 30 Min Idle    | 2-3s     | 2-6 GB |

---

## Features

**Chat-Oberflache:** Message-Bubbles, blinkender Cursor (800ms), Typewriter-Effekt, auto-wachsendes Eingabefeld, Enter = senden

**Datei-Upload:** PDF/TXT/MD/JSON (max. 4 MB), Badge-Anzeige, Text-Extraktion, bleibt fur Follow-up verfugbar

**Intelligentes Routing:** Automatische Modell-Auswahl via Keywords, bei Datei immer Analyse-Modell, dynamische Token-Limits

**Datenbank:** PostgreSQL mit EF Core, persistente Chat-History, getrennte Conversations/Messages

**Performance:** Background Pre-warm, Keep-Alive, GPU-optimierte Token-Limits, In-Process Tools (unter 1ms Latenz)

---

## Zusammenfassung

KIDT kombiniert zwei KI-Modelle in einer Desktop-App:

1. **Intelligentes Routing** - Keywords bestimmen automatisch das beste Modell
2. **Hybrid-MCP** - Nutzt MCP-Standard ohne instabile Client-API
3. **Optimierte Performance** - GPU-Limits, Pre-warm, Keep-Alive, In-Process Tools
4. **Benutzerfreundliche UI** - Chat-Bubbles, Typewriter, Datei-Upload
5. **Persistente Speicherung** - PostgreSQL fur Chat-History

**Hauptvorteil:** Schnelle Konversation (llama3.1) + prazise Analyse (qwen2.5) ohne komplexes MCP-Setup.