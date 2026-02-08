# KIDT - KI Desktop Tool

Ein intelligentes Desktop-Chat-System mit Multi-Model-Architektur, MCP-Integration und Dokument-Verwaltung.

---

## Inhaltsverzeichnis

- [Überblick](#überblick)
- [Architektur](#architektur)
- [File-Struktur](#file-struktur)
- [Datenfluss & Ablauf](#datenfluss--ablauf)
- [MCP-Integration](#mcp-integration)
- [Modelle & Router](#modelle--router)
- [Features](#features)
- [Datenbank](#datenbank)

---

## Überblick

KIDT ist ein .NET MAUI Desktop-Chat mit intelligenter Multi-Model-Architektur:
- **3 spezialisierte KI-Modelle** (Router, Conversation, Data Analysis)
- **MCP (Model Context Protocol)** für Tool-Funktionen
- **MySQL-Datenbank** für Chat-History & Dokumente (Multi-User-fähig!)
- **PDF/Text-Upload** mit automatischer Extraktion
- **Dokument-Suche** via Function Calling

```
+---------------------------------------------------------+
|                    KIDT Desktop App                     |
+---------------------------------------------------------+
|  Chat UI  |  Dokumente  |  Chat-History  |  Upload    |
+--------+----------------------------------------+-------+
         |                                        |
         v                                        v
+----------------------+              +--------------------+
|   ChatCoordinator    |<------------>|  FileService       |
+----------+-----------+              +--------------------+
           |
           v
    +-------------+
    |RouterService| (GPT-4) ---> Intent Detection + MCP Tools
    +------+------+
           |
      +----+-----+
      v          v
+----------+  +--------------+
|Conversation| |DataAnalysis  |
|(phi3:mini) | |(qwen2.5:7b)  |
+----------+  +--------------+
```

---

## Architektur

### Service-Hierarchie

```
User Input
    |
    v
ChatCoordinator (Orchestrator)
    |
    +---> FileService (Upload & Extract)
    |
    v
RouterService (GPT-4)
    |
    +---> MCP Tools (search_documents, add_document_to_chat)
    +---> ConversationService (phi3:mini)
    +---> DataAnalysisService (qwen2.5:7b)
    |
    v
Database (ChatDb / DocDb)
```

### Modell-Spezialisierung

| Modell | Engine | Aufgabe |
|--------|--------|---------|
| **Router** | GPT-4 (Azure) | Intent-Erkennung, Tool-Calls, Dokumenten-Suche |
| **Conversation** | phi3:mini (Ollama) | Schnelle, freundliche Gespräche |
| **Data Analysis** | qwen2.5:7b (Ollama) | Präzise Daten-Analyse |

---

## File-Struktur

### UI (Components/Pages)

| File | Verantwortung |
|------|---------------|
| `Home.razor` | Chat-Interface, Textarea, Upload-Badge, Typewriter-Effekt, Message-Rendering |
| `Daten.razor` | Chat-History & Dokument-Explorer, Tab-Navigation, Lösch-Funktionen |

#### Home.razor - Wichtige Methoden:
- `OnInitializedAsync()` ? Lade bestehenden Chat aus DB (parallel Messages + Files)
- `SendMessage()` ? User-Nachricht ? Router ? AI-Antwort ? DB-Speicherung (parallel)
- `OnUploadClick()` ? Datei-Upload ? Extraktion ? DB-Speicherung (Documents + UploadedFiles)
- `TypewriterEffect()` ? Zeichen-für-Zeichen Anzeige der AI-Antwort

---

### Services

| Service | Zweck | Modell |
|---------|-------|--------|
| `ChatCoordinator` | **Hauptorchestrator**: Koordiniert Upload, Router, Services, DB-Speicherung | - |
| `RouterService` | **Intent-Detection**: Analysiert User-Nachricht ? `conversation` / `dataAnalysis` / `document_search` | GPT-4 |
| `ConversationService` | Schnelle Gespräche, Small Talk | phi3:mini |
| `DataAnalysisService` | Daten-Analyse mit erhöhtem Token-Limit | qwen2.5:7b |
| `FileService` | Text-Extraktion aus PDF/TXT/MD/JSON | - |
| `ThumbnailGenerator` | PDF-Thumbnail-Generierung (erste Seite) | - |

#### ChatCoordinator - Workflow:
```csharp
SendAsync(userMessage, conversationId)
  +---> LoadDocumentsForConversation() // Lade verknüpfte Dokumente
  +---> GetChatContext() // Lade letzte 10 Nachrichten
  |
  +---> RouterService.ProcessAsync()
  |     +---> MCP Tools (search_documents, add_document_to_chat)
  |     +---> Intent-Classification (conversation/dataAnalysis)
  |
  +---> ConversationService.SendAsync() // oder
  +---> DataAnalysisService.SendAsync()
       +---> Return ChatResponse { Message, FoundDocuments }
```

#### RouterService - Ablauf:
```
1. Kernel mit MCP-Tools erstellen (search_documents, add_document_to_chat)
2. System-Prompt mit Tool-Instruktionen
3. GetChatMessageContentAsync() ---> GPT-4 entscheidet
4. Prüfe Response:
   +---> FunctionCallContent? ---> Führe Tool manuell aus
   +---> JSON {needsRouting:true}? ---> Route zu Service
   +---> Fallback ---> ClassifyIntentAsync()
5. Return RouterResponse { ShouldRoute, TargetService, FoundDocuments }
```

---

### Database

| Service | Zweck |
|---------|-------|
| `ChatDbContext` | EF Core Context (Conversations, Messages, UploadedFiles, Documents, ConversationDocuments) |
| `ChatDbService` | CRUD für Conversations, Messages, UploadedFiles (pro Chat) |
| `DocumentDbService` | CRUD für Documents (global), Verknüpfungen (ConversationDocuments), Suche |

#### Wichtige Methoden:

**ChatDbService**:
- `CreateConversationAsync()` ? Neue Conversation
- `SaveMessageAsync(conversationId, isUser, text, documentIds?)` ? Speichere Nachricht + optional Dokument-IDs als JSON
- `LoadMessagesAsync()` ? Lade alle Messages für Chat (inkl. DocumentIdsJson)
- `DeleteConversationAsync()` ? Lösche Conversation + Messages + UploadedFiles

**DocumentDbService**:
- `SaveDocumentAsync()` ? Speichere Dokument (mit Hash für Duplikat-Erkennung)
- `SearchDocumentsAsync(searchTerm)` ? Volltextsuche in FileName + ExtractedText
- `LinkDocumentToConversationAsync()` ? Erstelle Verknüpfung in ConversationDocuments
- `GetDocumentsForConversationAsync()` ? Lade alle verknüpften Dokumente für Chat

---

## Datenfluss & Ablauf

### Chat-Nachricht senden

```
User gibt Text ein ---> Enter
  |
  v
Home.razor: SendMessage()
  +---> InputText leeren (sofort)
  +---> User-Nachricht zu UI hinzufügen
  +---> Loading-Nachricht anzeigen
  |
  +---> Parallel:
  |    +---> DB: SaveMessageAsync(user) + UpdateTitle
  |    +---> Chat.SendAsync() ---> Router
  |
  v
RouterService.ProcessAsync()
  +---> System-Prompt + User-Message
  +---> GPT-4 ---> Tool-Call oder Intent?
  |
  +---> TOOL: search_documents?
  |    +---> DocumentDbService.SearchDocumentsAsync()
  |         +---> Return RouterResponse { DirectResponse, FoundDocuments }
  |
  +---> INTENT: conversation/dataAnalysis?
       +---> Return RouterResponse { ShouldRoute: true, TargetService }
  |
  v
ChatCoordinator:
  +---> RouterResponse.ToolWasUsed? ---> Return sofort
  +---> RouterResponse.ShouldRoute?
       +---> ConversationService.SendAsync() oder
       +---> DataAnalysisService.SendAsync()
            +---> Return ChatResponse { Message }
  |
  v
Home.razor:
  +---> Loading-Nachricht entfernen
  +---> Assistent-Nachricht hinzufügen
  +---> TypewriterEffect() starten
  +---> Parallel: SaveMessageAsync(assistant, documentIds)
```

### Datei hochladen

```
User klickt Upload-Button
  |
  v
Home.razor: OnUploadClick()
  +---> FilePicker.PickAsync() ---> Windows Explorer
  +---> Badge anzeigen (sofort)
  +---> Loading-Nachricht
  |
  v
Chat.UploadFileAsync()
  +---> FileService.ExtractTextAsync()
       +---> PDF? ---> PdfPig.ExtractText()
       +---> TXT/MD/JSON? ---> File.ReadAllText()
  |
  v
Home.razor:
  +---> TypewriterEffect() für Ergebnis
  +---> Parallel: Task.Run()
       +---> ThumbnailGenerator.GenerateThumbnailAsync()
       +---> Db.SaveUploadedFileAsync() // Pro Chat
       +---> DocDb.SaveDocumentAsync() // Global + Hash-Check
       +---> DocDb.LinkDocumentToConversationAsync()
       +---> Db.SaveMessageAsync(assistant, text)
```

### Dokument suchen

```
User: "Hast du Dokumente über Python?"
  |
  v
RouterService: search_documents Tool erkannt
  +---> System-Prompt instruiert: "Du MUSST Tools nutzen!"
  +---> GPT-4 Response: FunctionCallContent { FunctionName: "search_documents", Arguments: {query: "Python"} }
  |
  v
RouterService: Tool manuell ausführen
  +---> kernel.Plugins.GetFunction("Document", "search_documents")
  +---> function.InvokeAsync(kernel, arguments)
  |
  v
DocumentTools.SearchDocuments(query: "Python")
  +---> DocumentDbService.SearchDocumentsAsync("Python")
  +---> Return JSON: { found: 2, documentIds: [1, 5] }
  |
  v
RouterService:
  +---> Parse JSON
  +---> Lade volle Dokumente: GetDocumentByIdAsync(1), GetDocumentByIdAsync(5)
  +---> Return RouterResponse { DirectResponse: "2 Dokument(e) gefunden", FoundDocuments: [doc1, doc5] }
  |
  v
Home.razor: Zeige Dokumente als Cards mit Thumbnail
```

---

## MCP-Integration

### Was ist MCP?

**Model Context Protocol** = Tool-Funktionen für KI-Modelle (ähnlich OpenAI Function Calling)

### Implementierung

```
McpToolsRegistry (static Helper)
  +---> RegisterTools(kernel, docDbService, conversationId)
       +---> Assembly.GetTypes() ---> Suche [McpServerToolType]
       +---> DocumentTools gefunden
       +---> GetMethods() ---> Suche [McpServerTool]
       +---> SearchDocuments + AddDocumentToChat gefunden
       +---> KernelFunctionFactory.CreateFromMethod()
            +---> kernel.ImportPluginFromFunctions("Document", [search_documents, add_document_to_chat])
```

### Verfügbare Tools

| Tool | Parameter | Rückgabe | Zweck |
|------|-----------|----------|-------|
| `search_documents` | `query: string` | `{ found: int, documentIds: int[] }` | Sucht Dokumente in DB |
| `add_document_to_chat` | `documentId: int` | `{ success: bool, fileName: string }` | Verknüpft Dokument mit Chat |

#### DocumentTools.SearchDocuments - Ablauf:
```csharp
[McpServerTool(Description = "Sucht Dokumente...")]
public async Task<string> SearchDocuments(string query)
{
    var documents = await docDbService.SearchDocumentsAsync(query);
    var documentIds = documents.Select(d => d.Id).ToList();
    
    return JsonSerializer.Serialize(new {
        found = documents.Count,
        documentIds = documentIds,
        message = $"{documents.Count} Dokument(e) gefunden"
    });
}
```

#### RouterService - Tool-Execution:
```csharp
if (item is FunctionCallContent functionCall)
{
    var function = kernel.Plugins.GetFunction(pluginName, functionName);
    var result = await function.InvokeAsync(kernel, arguments); // Manuell ausführen!
    
    if (functionName == "search_documents")
    {
        var toolResult = JsonSerializer.Deserialize<SearchResultJson>(result);
        // Lade volle Dokumente + Return RouterResponse
    }
}
```

---

## Modelle & Router

### Router-Modell (GPT-4)

**Aufgabe**: "Traffic Controller" für alle User-Anfragen

#### Funktionen:
1. **Intent-Detection**: Klassifiziert User-Anfrage
   - `conversation` ---> phi3:mini (Small Talk)
   - `dataAnalysis` ---> qwen2.5:7b (Analyse)
   
2. **Tool-Calling**: Entscheidet wann Tools genutzt werden
   - User fragt nach Dokumenten? ---> `search_documents()`
   - User will Dokument hinzufügen? ---> `add_document_to_chat()`

3. **Direct Response**: Beantwortet selbst bei Tool-Nutzung
   - "2 Dokument(e) gefunden: python.pdf, tutorial.md"

#### System-Prompt:
```
Du bist ein Router-Agent mit Dokumenten-Tools.
KRITISCH WICHTIG: Du hast KEINE Informationen über Dokumente!
Du MUSST die Tools nutzen wenn User nach Dokumenten fragt!

Verfügbare Tools:
- search_documents(query): Sucht Dokumente in Datenbank
- add_document_to_chat(documentId): Fügt Dokument zum Chat hinzu

REGELN:
1. User fragt nach Dokumenten? ---> search_documents() aufrufen!
2. User will Dokument hinzufügen? ---> add_document_to_chat(id) aufrufen!
3. Normale Frage? ---> Antworte mit JSON: {"needsRouting": true, "intent": "..."}
```

#### Fallback-Mechanismus:
```
1. Versuch: Function Calling (ToolCallBehavior.EnableKernelFunctions)
2. Versuch: JSON-Parsing aus Response-Text
3. Versuch: ClassifyIntentAsync() mit separatem GPT-4 Call
```

### Conversation-Modell (phi3:mini)

**Aufgabe**: Schnelle, freundliche Gespräche

- **Temperature**: 0.5 (ausgewogen)
- **MaxTokens**: 300 (kurze Antworten)
- **Context**: Letzte 10 Nachrichten
- **Ideal für**: Small Talk, einfache Fragen

### Data Analysis-Modell (qwen2.5:7b)

**Aufgabe**: Präzise Daten-Analyse

- **Temperature**: 0.3 (präzise)
- **MaxTokens**: 2000 (ausführliche Analysen)
- **Context**: Letzte 10 Nachrichten + verknüpfte Dokumente
- **Ideal für**: Dokument-Analyse, Daten-Verarbeitung

---

## Features

### Chat-System
- Multi-Conversation-Support (jeder Chat hat eigene ID)
- Typewriter-Effekt für AI-Antworten
- Loading-Animation während AI antwortet
- Auto-Scroll zu neuester Nachricht
- Chat-History persistent in DB
- Auto-Titel-Generierung (erste User-Nachricht, max 50 Zeichen)

### Datei-Upload
- Unterstützte Formate: PDF, TXT, MD, JSON
- Max. 4 MB pro Datei
- Automatische Text-Extraktion
- Thumbnail-Generierung für PDFs
- Datei-Badge im Chat-Input
- Duplikat-Erkennung via SHA256-Hash

### Dokument-Suche
- Volltextsuche in Dateiname + Inhalt
- MCP Function Calling via GPT-4
- Dokument-Cards mit Thumbnail
- Click-to-Open in Standard-App
- "Zum Chat hinzufügen" Button

### Dokument-Verwaltung
- Globale Dokument-Bibliothek
- Pro-Chat Verknüpfungen (ConversationDocuments)
- Explorer-Ansicht (Daten.razor)
- Sortierung nach Upload-Datum
- Lösch-Funktion (mit UI-Sofort + DB-Parallel)

### Routing & Intent
- Automatische Intent-Erkennung
- Tool-Call-Detection
- Fallback-Mechanismen (3-stufig)
- Debug-Logging für Entwicklung

---

## Datenbank

### Schema

```
Conversations (Chats)
+--- Id (Primary Key)
+--- Title
+--- CreatedAt

Messages (Nachrichten)
+--- Id (Primary Key)
+--- ConversationId (Foreign Key)
+--- IsUser (bool)
+--- Text
+--- Timestamp
+--- DocumentIdsJson (string, List<int> als JSON)

UploadedFiles (Pro-Chat, Legacy)
+--- Id (Primary Key)
+--- ConversationId (Foreign Key)
+--- FileName
+--- ExtractedText
+--- ThumbnailBase64
+--- UploadedAt

Documents (Global)
+--- Id (Primary Key)
+--- FileName
+--- FileHash (SHA256, für Duplikat-Check)
+--- FileContent (Base64 für PDF, Text für TXT/MD/JSON)
+--- FileType (pdf/txt/md/json)
+--- ExtractedText
+--- ThumbnailBase64
+--- UploadedAt

ConversationDocuments (Verknüpfung)
+--- Id (Primary Key)
+--- ConversationId (Foreign Key)
+--- DocumentId (Foreign Key)
+--- AddedAt
```

### Beziehungen

```
Conversation 1:N Messages
Conversation 1:N UploadedFiles (Legacy)
Conversation N:M Documents (via ConversationDocuments)
```

---

## Wichtige Konzepte

### Parallele DB-Operationen
- Separate DbContext-Scopes für parallele Queries (verhindert Concurrency-Konflikt)
- UI-Update sofort, DB-Speicherung parallel (Fire-and-Forget)

### Typewriter-Effekt
- 20ms Delay pro Zeichen
- StateHasChanged() nach jedem Zeichen
- Parallel: DB-Speicherung der Nachricht

### File-Badge
- Zeigt angehängte Datei im Chat-Input
- Persistent bis Chat-Wechsel oder Entfernen
- `currentDocumentId` für späteres Löschen der Verknüpfung

### Dokument-Verknüpfung
- **UploadedFiles**: Pro Chat (Legacy, wird noch verwendet für Badge)
- **Documents**: Global (für Dokumente-Seite)
- **ConversationDocuments**: Many-to-Many Verknüpfung

### Router-Logic
- 3-stufiger Fallback: Function Calling ---> JSON ---> ClassifyIntent
- Debug-Logging für jeden Schritt
- Manual Tool-Execution (kein automatisches Callback)

---

## Entwickler-Notizen

### Code-Stil
- Alle komplexen Operatoren (`?.`, `??`, `=>`) durch explizite if-else ersetzt
- Inline returns vermieden
- Ausführliche Kommentare für Anfänger
- Object Initializers in separate Zeilen umgewandelt

### Wichtige Dateien zum Verstehen:
1. `ChatCoordinator.cs` ---> Zentrale Orchestrierung
2. `RouterService.cs` ---> Intent-Detection + MCP
3. `Home.razor` ---> Chat-UI + Workflow
4. `DocumentTools.cs` ---> MCP-Tool-Implementierung
5. `ChatDbService.cs` ---> DB-Operationen

---

**Version**: 1.0  
**Framework**: .NET MAUI 10 / C# 14.0  
**Datenbank**: MySQL mit EF Core (Multi-User-fähig!)  
**KI-Modelle**: Azure OpenAI (GPT-4), Ollama (phi3:mini, qwen2.5:7b)

---

## Multi-User Setup

Siehe **[MYSQL_SETUP.md](MYSQL_SETUP.md)** für die 3-Schritte-Anleitung.

**TL;DR für Teamkollege:**
1. Klone Repo
2. Ändere in `ChatDbContext.cs` Zeile 18: `Server=192.168.1.100` (IP vom Server-PC)
3. Ändere in `ChatDbContext.cs` Zeile 21: `User=kidt_user` (statt root)
4. `dotnet run` ? Fertig!
