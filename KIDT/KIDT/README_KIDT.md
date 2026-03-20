# KIDT - KI-gestützter Dokumenten- und Terminmanager

Ein intelligentes Desktop-Chat-System mit Multi-Model-Architektur, MCP-Integration und Dokument-Verwaltung.

---

## Inhaltsverzeichnis

- [Überblick] → Z. 20-62
- [Architektur] → Z. 64-98
- [File-Struktur] → Z. 100-230
- [Datenfluss & Ablauf] → Z. 232-470
- [MCP-Integration] → Z. 472-650
- [Modelle & Router] → Z. 652-720
- [Features] → Z. 722-800
- [Datenbank] → Z. 802-865
- [Wichtige Konzepte] → Z. 867-950
- [Verwendungsbeispiele] → Z. 952-1020
- [Entwickler-Notizen] → Z. 1022-Ende

---

## Überblick

KIDT ist ein .NET MAUI Desktop-Chat mit intelligenter Multi-Model-Architektur:
- **3 spezialisierte KI-Modelle** (Router, Conversation, Data Analysis)
- **MCP (Model Context Protocol)** für Tool-Funktionen (Dokumente + Kalender)
- **MySQL-Datenbank** für Chat-History, Dokumente & Kalender-Termine (Multi-User-fähig!)
- **PDF/Text-Upload** mit automatischer Extraktion
- **Dokument-Suche** via Function Calling
- **Kalender-Verwaltung** mit Monat/Woche/Tag-Ansichten
- **Erinnerungen** mit automatischen Notifications

```
+----------------------------------------------------------------+
|                      KIDT Desktop App                          |
+----------------------------------------------------------------+
|  Chat  |  Kalender  |  Dokumente  |  Chat-History  |  Upload   |
+--------+------------------------------------------------+-------+
         |                                                |
         v                                                v
+----------------------+              +---------------------+
|   ChatCoordinator    |<------------>|  FileService        |
+----------+-----------+              +---------------------+
           |
           v
    +-------------+
    |RouterService| (GPT-4) ---> Intent Detection + MCP Tools
    +------+------+
           |
      +----+----------------+-----------------+
      v                     v                 v
+------------+  +--------------+  +-------------------+
|Conversation|  |DataAnalysis  |  | CalendarService + |
|(phi3:mini) |  |(qwen2.5:7b)  |  | NotificationSvc   |
+------------+  +--------------+  +-------------------+
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
    +---> MCP Tools (search_documents, add_document_to_chat, list_calendar_events, create_calendar_event, delete_calendar_event)
    +---> ConversationService (phi3:mini)
    +---> DataAnalysisService (qwen2.5:7b)
    |
    v
Database (ChatDb / DocDb / CalendarEvents)
    |
    v
NotificationService (Background Timer für Erinnerungen)
```

### Modell-Spezialisierung

| Modell            | Engine              | Aufgabe                                        |
|-------------------|---------------------|------------------------------------------------|
| **Router**        | GPT-4 (Azure)       | Intent-Erkennung, Tool-Calls, Dokumenten-Suche |
| **Conversation**  | phi3:mini (Ollama)  | Schnelle, freundliche Gespräche                |
| **Data Analysis** | qwen2.5:7b (Ollama) | Präzise Daten-Analyse                          |

---

## File-Struktur

### UI (Components/Pages)

| File             | Verantwortung                                                                                      |
|------------------|-----------------------------------------------------------------------------------------------------|
| `Home.razor`     | Chat-Interface, Textarea, Upload-Badge, Typewriter-Effekt, Message-Rendering                       |
| `Daten.razor`    | Chat-History & Dokument-Explorer, Tab-Navigation, Lösch-Funktionen                                  |
| `Kalender.razor` | Kalender-Ansichten (Monat/Woche/Tag), Termin-Verwaltung, Add/Edit/Delete-Dialogs, Color-Picker     |

#### Home.razor - Wichtige Methoden:
- `OnInitializedAsync()` → Lade bestehenden Chat aus DB (parallel Messages + Documents)
- `SendMessage()` → User-Nachricht → Router → AI-Antwort → DB-Speicherung (parallel)
- `OnUploadClick()` → Datei-Upload → Extraktion → DB-Speicherung (Documents + ConversationDocuments)
- `TypewriterEffect()` → Zeichen-für-Zeichen Anzeige der AI-Antwort

#### Kalender.razor - Wichtige Methoden:
- `OnInitializedAsync()` → Lade alle Termine aus DB + Starte Notification-Service
- `LoadEventsAsync()` → Lade Termine aus CalendarService
- `GetEventsForDate(date)` → Filtert Termine für spezifisches Datum (für Monat/Woche/Tag-Ansicht)
- `OnDayClick(date)` → Öffnet Add-Dialog für gewähltes Datum
- `OnEventClick(event)` → Öffnet Edit-Dialog für bestehenden Termin
- `SaveEventAsync()` → Speichert neuen/bearbeiteten Termin (Create oder Update)
- `DeleteEventAsync(eventId)` → Löscht Termin aus DB + UI

#### Kalender.razor - Ansichten:
- **Monatsansicht**: 7x6 Grid (42 Zellen), max 3 Termine pro Tag sichtbar, "+X weitere" Indikator
- **Wochenansicht**: 7 Tagesspalten (Mo-So), alle Termine sichtbar, Timeline-Layout
- **Tagesansicht**: Fokus auf einzelnen Tag, Inline-Add-Input, alle Termine sichtbar

### UI (Components/UI)

| File                        | Verantwortung                                                             |
|-----------------------------|---------------------------------------------------------------------------|
| `NotificationComponent.razor` | Toast-Notifications, Startup-Overview (nächste 3 Termine), Event-Reminder |

#### NotificationComponent.razor - Features:
- **Startup-Notification**: Zeigt beim App-Start nächste 3 Termine
- **Event-Reminder**: Automatische Erinnerungen X Minuten vor Termin
- **Auto-Dismiss**: Toast verschwindet nach 10 Sekunden (oder manuell mit X)
- **Click-to-Calendar**: Klick auf Notification → Navigation zu Kalender-Seite

---

### Services

| Service                    | Zweck                                                                                                 | Modell     |
|----------------------------|-------------------------------------------------------------------------------------------------------|------------|
| `ChatCoordinator`          | **Hauptorchestrator**: Koordiniert Upload, Router, Services, DB-Speicherung                           | -          |
| `RouterService`            | **Intent-Detection**: Analysiert User-Nachricht → `conversation` / `dataAnalysis` / MCP Tools         | GPT-4      |
| `ConversationService`      | Schnelle Gespräche, Small Talk                                                                        | phi3:mini  |
| `DataAnalysisService`      | Daten-Analyse mit erhöhtem Token-Limit                                                                | qwen2.5:7b |
| `FileService`              | Text-Extraktion aus PDF/TXT/MD/JSON                                                                   | -          |
| `ThumbnailGenerator`       | PDF-Thumbnail-Generierung (erste Seite)                                                               | -          |
| `CalendarService`          | **Kalender-DB-Operationen**: CRUD für CalendarEvents, Datumsbereich-Queries                          | -          |
| `AppNotificationService`   | **Background-Timer**: Prüft alle 30s auf fällige Erinnerungen, Startup-Notification                  | -          |
| `ChatEventService`         | **Event-Broker**: Singleton für Chat-Reload-Events (zwischen Home.razor und Daten.razor)             | -          |

#### ChatCoordinator - Workflow:

**SendAsync(userMessage, conversationId)**:  
1. Lade verknüpfte Dokumente (`LoadDocumentsForConversation`)  
2. Lade letzte 10 Nachrichten (`GetChatContext`)  
3. Router-Verarbeitung (`RouterService.ProcessAsync`)  
   - MCP Tools: Dokumente, Kalender  
   - Intent-Classification: conversation/dataAnalysis  
4. Weiterleitung an `ConversationService` oder `DataAnalysisService`  
5. Return `ChatResponse { Message, FoundDocuments }`  

→ Siehe: `Platforms/Windows/ChatCoordinator.cs`

#### CalendarService - Wichtige Methoden:
- `GetAllEventsAsync()` → Lade alle Termine aus DB, sortiert nach Start
- `GetEventsByDateRangeAsync(start, end)` → Lade Termine für Zeitraum
- `AddEventAsync(event)` → Erstelle neuen Termin, setze CreatedAt
- `UpdateEventAsync(event)` → Aktualisiere bestehenden Termin, setze UpdatedAt
- `DeleteEventAsync(eventId)` → Lösche Termin aus DB
- `EnsureDatabaseSchemaAsync()` → Migriert DB-Schema (fügt ReminderMinutesBefore, ReminderShown hinzu)

#### AppNotificationService - Workflow:

**Start()**:  
- Timer läuft alle 30 Sekunden  
- `CheckForDueRemindersAsync()`: Prüft fällige Erinnerungen  
- Berechnet ReminderTime = EventDateTime - ReminderMinutesBefore  
- Feuert `OnNotificationRequested` Event bei Fälligkeit  
- Setzt `ReminderShown = true` (verhindert Duplikate)  

**ShowStartupNotificationAsync()**:  
- Lädt nächste 3 bevorstehende Termine  
- Feuert Event mit `NotificationType.StartupOverview`  

→ Siehe: `Services/NotificationService.cs`

#### RouterService - Ablauf:
```
1. Kernel mit MCP-Tools erstellen (search_documents, add_document_to_chat, list_calendar_events, create_calendar_event, delete_calendar_event)
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

| Service             | Zweck                                                                                      |
|---------------------|--------------------------------------------------------------------------------------------|
| `ChatDbContext`     | EF Core Context (Conversations, Messages, Documents, ConversationDocuments, CalendarEvents)|
| `ChatDbService`     | CRUD für Conversations, Messages                                                            |
| `DocumentDbService` | CRUD für Documents (global), Verknüpfungen (ConversationDocuments), Suche                  |
| `CalendarService`   | CRUD für CalendarEvents, Datumsbereich-Queries, Schema-Migration                           |

#### Wichtige Methoden:

**ChatDbService**:
- `CreateConversationAsync()` → Neue Conversation
- `SaveMessageAsync(conversationId, isUser, text, documentIds?)` → Speichere Nachricht + optional Dokument-IDs als JSON
- `LoadMessagesAsync()` → Lade alle Messages für Chat (inkl. DocumentIdsJson)
- `LoadAllConversationsAsync()` → Lade alle Conversations mit verknüpften Documents (via ConversationDocuments)
- `DeleteConversationAsync()` → Lösche Conversation + Messages + ConversationDocuments-Verknüpfungen

**DocumentDbService**:
- `SaveDocumentAsync()` → Speichere Dokument (mit Hash für Duplikat-Erkennung)
- `SearchDocumentsAsync(searchTerm)` → Volltextsuche in FileName + ExtractedText
- `LinkDocumentToConversationAsync()` → Erstelle Verknüpfung in ConversationDocuments
- `GetDocumentsForConversationAsync()` → Lade alle verknüpften Dokumente für Chat

**CalendarService**:
- `GetAllEventsAsync()` → Lade alle Termine, sortiert nach Start
- `GetEventByIdAsync(eventId)` → Lade einzelnen Termin
- `GetEventsByDateRangeAsync(start, end)` → Lade Termine für Zeitraum
- `AddEventAsync(event)` → Erstelle neuen Termin, setze CreatedAt
- `UpdateEventAsync(event)` → Aktualisiere Termin, setze UpdatedAt
- `DeleteEventAsync(eventId)` → Lösche Termin
- `EnsureDatabaseSchemaAsync()` → Migriert DB-Schema (ALTER TABLE für neue Spalten)

---

## Datenfluss & Ablauf

### Chat-Nachricht senden

```
User gibt Text ein ---> Enter
  |
  v
Home.razor: SendMessage()
  +---> InputText leeren (sofort)
  +---> User-Nachricht zu UI hinzuf×gen
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
  +---> Assistent-Nachricht hinzuf×gen
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
     +---> DocDb.SaveDocumentAsync() // Global + Hash-Check
     +---> DocDb.LinkDocumentToConversationAsync() // Verkn×pfung erstellen
     +---> Db.SaveMessageAsync(assistant, text)
```

### Dokument suchen

```
User: "Hast du Dokumente ×ber Python?"
  |
  v
RouterService: search_documents Tool erkannt
  +---> System-Prompt instruiert: "Du MUSST Tools nutzen!"
  +---> GPT-4 Response: FunctionCallContent { FunctionName: "search_documents", Arguments: {query: "Python"} }
  |
  v
RouterService: Tool manuell ausFühren
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

### Termin erstellen (Kalender-UI)

```
User klickt auf Tag in Kalender
  |
  v
Kalender.razor: OnDayClick(date)
  +---> Öffne Add-Dialog
  +---> Zeige Datum, Input-Felder (Titel, Ganztägig-Checkbox, Zeit, Farbe, Erinnerung)
  |
  v
User füllt Formular aus + klickt "Hinzufügen"
  |
  v
SaveEventAsync()
  +---> Validierung: Titel nicht leer?
  +---> Erstelle CalendarEvent-Objekt
       +---> Start = gewähltes Datum
       +---> Title = User-Input
       +---> IsAllDay = Checkbox-State
       +---> Time = TimeSpan (falls nicht ganztägig)
       +---> ColorIndex = gewählte Farbe (0-7)
       +---> ReminderMinutesBefore = Dropdown-Auswahl (null, 15, 30, 60, 1440)
  |
  v
CalendarService.AddEventAsync()
  +---> Setze CreatedAt = DateTime.Now
  +---> _dbContext.CalendarEvents.Add(event)
  +---> SaveChangesAsync()
  |
  v
Kalender.razor:
  +---> Schließe Dialog
  +---> LoadEventsAsync() ---> UI refresh
```

### Termin erstellen (via Chat + MCP Tool)

```
User im Chat: "Erstelle einen Termin für morgen 14:00 - Meeting mit Team"
  |
  v
RouterService.ProcessAsync()
  +---> GPT-4 erkennt create_calendar_event Tool
  +---> FunctionCallContent { 
          FunctionName: "create_calendar_event", 
          Arguments: { 
            date: "2025-01-16", 
            title: "Meeting mit Team",
            isAllDay: false,
            time: "14:00",
            colorIndex: 0,
            reminderMinutes: 15
          }
        }
  |
  v
RouterService: Tool manuell ausführen
  +---> kernel.Plugins.GetFunction("Calendar", "create_calendar_event")
  +---> function.InvokeAsync(kernel, arguments)
  |
  v
CalendarTools.CreateCalendarEvent()
  +---> Validierung: Datum parsen, Titel nicht leer, ColorIndex 0-7
  +---> Zeit parsen (falls !isAllDay)
  +---> Erstelle CalendarEvent
  +---> CalendarService.AddEventAsync()
  +---> Return JSON: { success: true, message: "Termin erstellt: Meeting mit Team am 16.01.2025 14:00", eventId: 42 }
  |
  v
RouterService:
  +---> Parse JSON
  +---> Return RouterResponse { DirectResponse: "Termin erstellt: ..." }
  |
  v
Home.razor: Zeige Bestätigung als AI-Nachricht
```

### Erinnerung auslösen (Background-Service)

```
AppNotificationService Timer (alle 30s)
  |
  v
CheckForDueRemindersAsync()
  +---> Lade alle Events mit ReminderMinutesBefore != null && !ReminderShown
  +---> Für jeden Event:
       +---> Berechne ReminderTime = EventDateTime - ReminderMinutesBefore
       +---> Now >= ReminderTime && Now < ReminderTime+5min?
            +---> Setze ReminderShown = true
            +---> SaveChangesAsync()
            +---> Feuere OnNotificationRequested Event
  |
  v
MainLayout.razor: OnNotificationRequested Event empfangen
  +---> NotificationComponent: ShowNotification(data)
       +---> Zeige Toast mit Termin-Details
       +---> Auto-Dismiss nach 10 Sekunden
       +---> Click ---> Navigation zu /kalender
```

---

## MCP-Integration

### Was ist MCP?

**Model Context Protocol** = Tool-Funktionen für KI-Modelle (×hnlich OpenAI Function Calling)

### Implementierung

```
McpToolsRegistry (static Helper)
  +---> RegisterTools(kernel, docDbService, calendarService, conversationId)
       +---> Assembly.GetTypes() ---> Suche [McpServerToolType]
       +---> DocumentTools + CalendarTools gefunden
       +---> GetMethods() ---> Suche [McpServerTool]
       +---> DocumentTools: SearchDocuments, AddDocumentToChat
       +---> CalendarTools: ListCalendarEvents, CreateCalendarEvent, DeleteCalendarEvent
       +---> KernelFunctionFactory.CreateFromMethod()
            +---> kernel.ImportPluginFromFunctions("Document", [...])
            +---> kernel.ImportPluginFromFunctions("Calendar", [...])
```

### verfügbare Tools

#### Dokument-Tools

| Tool                   | Parameter         | Rückgabe                              | Zweck                       |
|------------------------|-------------------|---------------------------------------|-----------------------------|
| `search_documents`     | `query: string`   | `{ found: int, documentIds: int[] }`  | Sucht Dokumente in DB       |
| `add_document_to_chat` | `documentId: int` | `{ success: bool, fileName: string }` | Verknüpft Dokument mit Chat |

#### Kalender-Tools

| Tool                    | Parameter                                                                 | Rückgabe                                          | Zweck                          |
|-------------------------|---------------------------------------------------------------------------|---------------------------------------------------|--------------------------------|
| `list_calendar_events`  | `titleSearch: string`, `startDate: string`, `endDate: string` (optional) | `{ found: int, events: [{id, date, title, ...}]}` | Listet Termine aus DB          |
| `create_calendar_event` | `date: string`, `title: string`, `isAllDay: bool`, `time: string`, `colorIndex: int`, `reminderMinutes: int` | `{ success: bool, message: string, eventId: int }` | Erstellt neuen Termin          |
| `delete_calendar_event` | `eventId: int`, `date: string`, `title: string` (optional, flexibel)      | `{ success: bool, message: string }` oder `{ needsClarification: true, events: [...] }` | Löscht Termin (flexibel per ID oder Datum+Titel) |
| `update_calendar_event` | `eventId: int`, diverse neue Werte (newTitle, newDate, newTime, newColor, etc.) | `{ success: bool, message: string }` | Aktualisiert bestehenden Termin |

#### Tool-Implementierung:

**DocumentTools.SearchDocuments**:  
→ Sucht Dokumente via `DocumentDbService.SearchDocumentsAsync()`  
→ Gibt JSON mit `found`, `documentIds`, `message` zurück  
→ Siehe: `Services/McpTools/DocumentTools.cs`

**CalendarTools.CreateCalendarEvent**:  
→ Validiert Datum, Titel, Zeit, ColorIndex  
→ Erstellt `CalendarEvent`-Objekt mit allen Parametern  
→ Speichert via `CalendarService.AddEventAsync()`  
→ Gibt JSON mit `success`, `message`, `eventId` zurück  
→ Siehe: `Services/McpTools/CalendarTools.cs`

#### Tool-Verwendung Beispiele:

**list_calendar_events**:
```
User: "Welche Termine habe ich diese Woche?"
GPT-4: list_calendar_events(startDate="2025-01-13", endDate="2025-01-19")
Response: { 
  found: 3, 
  events: [
    {id: 1, date: "15.01.2025", title: "Team Meeting", time: "14:30", color: 0, reminderMinutes: 15},
    {id: 2, date: "17.01.2025", title: "Arzttermin", time: "10:00", color: 2, reminderMinutes: 60},
    {id: 3, date: "19.01.2025", title: "Geburtstag", time: "Ganztägig", color: 3, reminderMinutes: null}
  ],
  message: "3 Termin(e) gefunden"
}

User: "Zeig mir alle Meetings"
GPT-4: list_calendar_events(titleSearch="Meeting")
Response: { found: 5, events: [...], message: "5 Termin(e) mit 'Meeting' gefunden" }
```

**create_calendar_event**:
```
User: "Erstelle Termin morgen 14:00 - Team Meeting mit Erinnerung 30 Minuten vorher"
GPT-4: create_calendar_event(date="2025-01-16", title="Team Meeting", isAllDay=false, time="14:00", reminderMinutes=30)
Response: { success: true, message: "Termin 'Team Meeting' für 16.01.2025 erstellt", eventId: 42 }

User: "Termin am 20.01. - Geburtstag, ganztägig, gelb"
GPT-4: create_calendar_event(date="2025-01-20", title="Geburtstag", isAllDay=true, colorIndex=3)
Response: { success: true, message: "Termin 'Geburtstag' für 20.01.2025 erstellt", eventId: 43 }
```

**delete_calendar_event**:
```
User: "Lösche Termin mit ID 42"
GPT-4: delete_calendar_event(eventId=42)
Response: { success: true, message: "Termin 'Team Meeting' wurde gelöscht", eventId: 42 }

User: "Lösche den Meeting-Termin am 16.01"
GPT-4: delete_calendar_event(date="2025-01-16", title="Meeting")
→ Falls genau 1 Termin: { success: true, message: "Termin gelöscht" }
→ Falls mehrere: { success: false, needsClarification: true, message: "2 Termine gefunden...", events: [...] }
```

#### RouterService - Tool-Execution:

**Ablauf**:  
1. Erkenne `FunctionCallContent` in GPT-4 Response  
2. Lade Plugin-Funktion via `kernel.Plugins.GetFunction()`  
3. Führe manuell aus: `function.InvokeAsync(kernel, arguments)`  
4. Parse JSON-Result und verarbeite je nach Tool:  
   - `search_documents` → Lade volle Dokumente aus DB  
   - `create_calendar_event` → Return Bestätigung  
   - `list_calendar_events` → Return Termin-Liste  

→ Siehe: `Platforms/Windows/RouterService.cs`

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
   - User will Dokument hinzuf×gen? ---> `add_document_to_chat()`

3. **Direct Response**: Beantwortet selbst bei Tool-Nutzung
   - "2 Dokument(e) gefunden: python.pdf, tutorial.md"

#### System-Prompt:
```
Du bist ein Router-Agent mit Dokumenten- und Kalender-Tools.
KRITISCH WICHTIG: Du hast KEINE Informationen über Dokumente oder Kalender-Termine!
Du MUSST die Tools nutzen wenn User nach Dokumenten/Terminen fragt oder welche erstellen will!

verfügbare Tools:
Dokumente:
- search_documents(query): Sucht Dokumente in Datenbank
- add_document_to_chat(documentId): Fügt Dokument zum Chat hinzu

Kalender:
- list_calendar_events(titleSearch?, startDate?, endDate?): Listet Termine
- create_calendar_event(date, title, isAllDay?, time?, colorIndex?, reminderMinutes?): Erstellt Termin
- delete_calendar_event(eventId): Löscht Termin

REGELN:
1. User fragt nach Dokumenten? ---> search_documents() aufrufen!
2. User will Dokument hinzufügen? ---> add_document_to_chat(id) aufrufen!
3. User fragt nach Terminen? ---> list_calendar_events() aufrufen!
4. User will Termin erstellen? ---> create_calendar_event() aufrufen!
5. User will Termin löschen? ---> delete_calendar_event() aufrufen!
6. Normale Frage? ---> Antworte mit JSON: {"needsRouting": true, "intent": "..."}
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

- **Temperature**: 0.3 (Präzise)
- **MaxTokens**: 2000 (ausf×hrliche Analysen)
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
- Event-Broker für Chat-Reload (ChatEventService)

### Datei-Upload
- Unterstützte Formate: PDF, TXT, MD, JSON
- Max. 4 MB pro Datei
- Automatische Text-Extraktion (PdfPig für PDF)
- Thumbnail-Generierung für PDFs (erste Seite als PNG)
- Datei-Badge im Chat-Input
- Duplikat-Erkennung via SHA256-Hash

### Dokument-Suche
- Volltextsuche in Dateiname + Inhalt
- MCP Function Calling via GPT-4
- Dokument-Cards mit Thumbnail
- Click-to-Open in Standard-App
- "Zum Chat hinzufügen" Button

### Dokument-Verwaltung
- Globale Dokument-Bibliothek (wiederverwendbar über Chats hinweg)
- Pro-Chat Verknüpfungen (ConversationDocuments)
- Explorer-Ansicht (Daten.razor, Tab "Dokumente")
- Sortierung nach Upload-Datum (neueste zuerst)
- Lösch-Funktion (mit UI-Sofort + DB-Parallel)

### Kalender-System
- **3 Ansichten**: Monat (7x6 Grid), Woche (7 Spalten), Tag (fokussierte Ansicht)
- **Termin-Verwaltung**: Erstellen, Bearbeiten, Löschen (UI + via Chat)
- **Farbcodierung**: 8 vordefinierte Farben (0-7) für visuelle Kategorisierung
- **Ganztägig oder Uhrzeit**: Flexible Termin-Typen
- **Erinnerungen**: Konfigurierbar (15, 30, 60, 1440 Minuten vorher)
- **Navigation**: Prev/Next/Heute-Buttons für schnelle Zeitraum-Wechsel
- **Inline-Add**: Schnell-Erstellung in Tagesansicht

### Notifications & Erinnerungen
- **Startup-Notification**: Zeigt beim App-Start nächste 3 Termine
- **Event-Reminder**: Automatische Toast-Notification X Minuten vor Termin
- **Background-Timer**: Prüft alle 30 Sekunden auf fällige Erinnerungen
- **Auto-Dismiss**: Toast verschwindet nach 10 Sekunden (oder manuell mit X)
- **Click-to-Navigate**: Klick auf Notification → Navigation zu Kalender
- **ReminderShown-Flag**: Verhindert Doppel-Notifications

### Routing & Intent
- Automatische Intent-Erkennung (conversation/dataAnalysis)
- Tool-Call-Detection (Dokumente + Kalender)
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

Documents (Global)
+--- Id (Primary Key)
+--- FileName
+--- FileHash (SHA256, für Duplikat-Check)
+--- FileContent (Base64 für PDF, Text für TXT/MD/JSON)
+--- FileType (pdf/txt/md/json)
+--- ExtractedText
+--- ThumbnailBase64
+--- UploadedAt

ConversationDocuments (Verkn×pfung, Many-to-Many)
+--- ConversationId (Primary Key, Foreign Key)
+--- DocumentId (Primary Key, Foreign Key)
+--- AddedAt
```

### Beziehungen

```
Conversation 1:N Messages
Conversation N:M Documents (via ConversationDocuments)
Document N:M Conversations (via ConversationDocuments)
```

---

## Wichtige Konzepte

### Parallele DB-Operationen
- Separate DbContext-Scopes für parallele Queries (verhindert Concurrency-Konflikt)
- UI-Update sofort, DB-Speicherung parallel (Fire-and-Forget)
- Beispiel: `Task.Run(() => SaveMessageAsync())` während Typewriter-Effekt läuft

### Typewriter-Effekt
- 20ms Delay pro Zeichen
- StateHasChanged() nach jedem Zeichen
- Parallel: DB-Speicherung der Nachricht
- Abbruch bei Component-Dispose

### File-Badge
- Zeigt angehängte Datei im Chat-Input
- Persistent bis Chat-Wechsel oder Entfernen
- `currentDocumentId` für späteres Löschen der Verknüpfung
- X-Button zum Entfernen

### Dokument-Verknüpfung
- **Documents**: Global (für alle Chats wiederverwendbar)
- **ConversationDocuments**: Many-to-Many Junction-Tabelle (Composite Key: ConversationId + DocumentId)
- **Conversation.LinkedDocuments**: `[NotMapped]` Property - wird zur Laufzeit manuell gefüllt

### Kalender-Farben
- **8 vordefinierte Farben** (0-7): Rot, Blau, Grün, Gelb, Lila, Orange, Pink, Türkis
- Gespeichert als `ColorIndex` (int) in DB
- CSS-Klasse: `.event-color-{index}` für visuelle Darstellung
- Color-Picker im Add/Edit-Dialog

### Erinnerungs-System
- **Reminder-Zeitpunkt**: `EventDateTime - ReminderMinutesBefore`
- **Background-Timer**: Prüft alle 30 Sekunden auf fällige Erinnerungen
- **5-Minuten-Fenster**: Reminder nur einmal innerhalb 5min nach Fälligkeit
- **ReminderShown-Flag**: Verhindert Doppel-Notifications (persistent in DB)
- **Notification-Types**: StartupOverview (beim Start), EventReminder (fällige Erinnerung)

### Notification-Flow
```
MainLayout.razor: OnInitializedAsync()
  +---> AppNotificationService.Start() // Starte Timer
  +---> AppNotificationService.ShowStartupNotificationAsync() // Zeige Startup-Notification
  +---> Subscribe: OnNotificationRequested Event
       +---> NotificationComponent.ShowNotification(data)
            +---> Zeige Toast (Type: StartupOverview oder EventReminder)
            +---> Auto-Dismiss nach 10s
```

### Router-Logic
- 3-stufiger Fallback: Function Calling → JSON → ClassifyIntent
- Debug-Logging für jeden Schritt
- Manual Tool-Execution (kein automatisches Callback)
- Tool-Priorität: Dokumente & Kalender vor Intent-Routing

### Kalender-Ansichten
- **Monatsansicht**: 
  - 7x6 Grid (42 Zellen, inkl. Vor-/Nachmonat)
  - Max 3 Termine pro Zelle sichtbar
  - "+X weitere" Indikator für mehr Termine
  - Grau für Vor-/Nachmonat, Weiß für aktuellen Monat

- **Wochenansicht**:
  - 7 Tagesspalten (Mo-So)
  - Alle Termine sichtbar (kein Limit)
  - Horizontal-Scroll für viele Termine

- **Tagesansicht**:
  - Fokus auf einzelnen Tag
  - Alle Termine als große Cards
  - Inline-Add-Input für schnelle Erstellung

### Datenbank-Migration (UploadedFiles → Documents)
**✓ Durchgeführt:** Die ursprüngliche `uploadedfiles`-Tabelle wurde entfernt und durch das `Documents`-System ersetzt:

**Vorher:**
- `UploadedFiles` (Pro Chat) - Redundante Speicherung
- `Documents` (Global) - Eigentliche Dokumente
- Doppelte Datenhaltung

**Jetzt:**
- `Documents` (Global, wiederverwendbar) - Einzige Dokumenten-Quelle
- `ConversationDocuments` (Junction-Tabelle) - Many-to-Many Verknüpfung
- `Conversation.LinkedDocuments` (`[NotMapped]`) - Zur Laufzeit gefüllt

**Wichtig:**
- `LoadAllConversationsAsync()` lädt `LinkedDocuments` manuell via `ConversationDocuments`
- `[NotMapped]` verhindert, dass EF Core eine direkte Beziehung erstellt
- Badge-Funktion verwendet jetzt `currentDocumentId` (aus `Documents`)

```

---

## Verwendungsbeispiele

### Chat mit Dokument-Kontext

```
1. Datei hochladen:
   - Klicke auf Upload-Button (📎)
   - Wähle PDF/TXT/MD/JSON (max 4 MB)
   - Badge wird im Input angezeigt

2. Frage stellen:
   User: "Was steht in diesem Dokument über Machine Learning?"
   → AI analysiert hochgeladenes Dokument
   → Antwort basiert auf ExtractedText

3. Weitere Dokumente suchen:
   User: "Hast du noch mehr Dokumente über ML?"
   → GPT-4 ruft search_documents("ML") auf
   → Zeigt gefundene Dokumente als Cards
   → "Zum Chat hinzufügen" Button verfügbar
```

### Kalender-Verwaltung via Chat

```
Termine erstellen:
User: "Erstelle einen Termin morgen 14:00 - Team Meeting"
→ GPT-4: create_calendar_event(date="2025-01-16", title="Team Meeting", isAllDay=false, time="14:00")
→ AI: "Termin 'Team Meeting' für 16.01.2025 14:00 erstellt"

Termine abfragen:
User: "Welche Termine habe ich diese Woche?"
→ GPT-4: list_calendar_events(startDate="2025-01-13", endDate="2025-01-19")
→ AI: "Du hast 3 Termine: 1. Team Meeting (15.01. 14:30), 2. Arzttermin (17.01. 10:00), 3. Geburtstag (19.01. ganztägig)"

Termine löschen:
User: "Lösche das Meeting am 16.01"
→ GPT-4: delete_calendar_event(date="2025-01-16", title="Meeting")
→ Falls mehrere: AI zeigt Liste zum Nachfragen
→ Falls eindeutig: "Termin 'Team Meeting' wurde gelöscht"
```

### Kalender-UI Interaktion

```
Monatsansicht:
- Klick auf Tag → Öffnet Add-Dialog
- Klick auf Event → Öffnet Edit-Dialog
- Prev/Next → Navigiere Monate
- "Heute" → Springe zu aktuellem Monat

Wochenansicht:
- 7 Tagesspalten (Mo-So)
- Alle Events sichtbar
- Klick auf Tag → Add-Dialog

Tagesansicht:
- Fokus auf einzelnen Tag
- Inline-Input: "Neuer Termin" + Enter
- Volle Event-Cards mit Details
```

### Erinnerungen nutzen

```
1. Termin mit Erinnerung erstellen:
   - Im Add-Dialog: Dropdown "Erinnerung" auswählen
   - Optionen: Keine, 15 Min., 30 Min., 1 Std., 1 Tag

2. Background-Service prüft automatisch:
   - Alle 30 Sekunden Scan nach fälligen Reminders
   - ReminderTime = EventDateTime - ReminderMinutesBefore

3. Toast-Notification erscheint:
   - X Minuten vor Termin
   - Zeigt Titel + Datum + Zeit
   - Klick → Navigation zu Kalender
   - Auto-Dismiss nach 10s

4. Startup-Notification:
   - Beim App-Start automatisch
   - Zeigt nächste 3 bevorstehende Termine
```

---

## Entwickler-Notizen

### Wichtige Dateien zum Verstehen:
1. `ChatCoordinator.cs` ---> Zentrale Orchestrierung
2. `RouterService.cs` ---> Intent-Detection + MCP
3. `Home.razor` ---> Chat-UI + Workflow
4. `Kalender.razor` ---> Kalender-UI + 3 Ansichten
5. `DocumentTools.cs` ---> MCP-Tool-Implementierung (Dokumente)
6. `CalendarTools.cs` ---> MCP-Tool-Implementierung (Kalender)
7. `ChatDbService.cs` ---> DB-Operationen (Chats + Messages)
8. `DocumentDbService.cs` ---> DB-Operationen (Dokumente + Verknüpfungen)
9. `CalendarService.cs` ---> DB-Operationen (Kalender-Termine)
10. `NotificationService.cs` ---> Background-Timer + Erinnerungen

### Setup-Voraussetzungen:
- **.NET 10 SDK** installiert
- **MySQL Server** läuft (localhost oder remote)
- **Ollama** installiert mit Modellen: `phi3:mini`, `qwen2.5:7b`
- **Azure OpenAI** API-Key in `openai-api-key.txt`
- **Connection-String** in `ChatDbContext.cs` anpassen (Server, User, Password, Database)
- **(Optional) Tailscale** für Multi-User-Setup (gemeinsamer MySQL-Server)

### Erste Schritte:

#### 1. MySQL-Datenbank einrichten
```sql
CREATE DATABASE kidt_chat;
CREATE USER 'kidt_user'@'%' IDENTIFIED BY 'kidt123';
GRANT ALL PRIVILEGES ON kidt_chat.* TO 'kidt_user'@'%';
FLUSH PRIVILEGES;
```

#### 2. Connection-String anpassen
In `Database/ChatDbContext.cs`:
```csharp
// Lokal:
Server=localhost;Port=3306;Database=kidt_chat;User=root;Password=kidt123;

// Multi-User (Tailscale):
Server=100.75.19.37;Port=3306;Database=kidt_chat;User=kidt_user;Password=kidt123;
```

#### 3. Azure OpenAI API-Key
Erstelle `KIDT/openai-api-key.txt` mit deinem Key:
```
sk-proj-...
```

#### 4. Ollama-Modelle installieren
```bash
ollama pull phi3:mini
ollama pull qwen2.5:7b
```

#### 5. App starten
- Visual Studio: F5 (Debug) oder Ctrl+F5 (Release)
- EF Core erstellt automatisch alle Tabellen
- Kalender-Schema-Migration läuft automatisch bei erstem Start (falls nötig)
- Startup-Notification zeigt nächste 3 Termine

### Multi-User Setup (Tailscale)

#### Server-Seite (Host):
1. Installiere Tailscale: https://tailscale.com/download/windows
2. Login & Connect
3. Notiere deine Tailscale-IP (z.B. `100.75.19.37`)
4. MySQL Server auf `0.0.0.0` binden (alle Interfaces):
   - In `my.ini` / `my.cnf`: `bind-address = 0.0.0.0`
   - Service neustarten
5. Firewall: Port 3306 öffnen
6. User erstellen: `CREATE USER 'kidt_user'@'%' ...`

#### Client-Seite (Teamkollege):
1. Installiere Tailscale
2. Login & Connect (selbes Netzwerk wie Host)
### Event-System:
- **ChatEventService**: Static Event-Broker für "Neuer Chat"
  - MainLayout → TriggerNewChat()
  - Home.razor → Subscribe OnNewChatRequested
- **AppNotificationService**: Instance Event-Broker für Notifications
  - AppNotificationService → OnNotificationRequested?.Invoke(data)
  - MainLayout → Subscribe + Forward zu NotificationComponent

### Performance-Optimierungen:
- **AsNoTracking()**: Für Read-Only Queries (verhindert Change-Tracking Overhead)
- **Parallel DB-Operations**: Separate Scopes für gleichzeitige Queries
- **Typewriter-Effect**: 20ms Delay (smooth, nicht zu langsam)
- **Lazy-Loading**: Dokumente nur laden wenn Chat geöffnet
- **Batch-Delete**: RemoveRange() statt einzelner Remove()-Calls

---

## Troubleshooting

### MySQL-Verbindung fehlgeschlagen
```
Problem: "Unable to connect to any of the specified MySQL hosts"
Lösung:
1. Prüfe ob MySQL-Server läuft: Services → MySQL → Running?
2. Prüfe Connection-String in ChatDbContext.cs
3. Prüfe User-Credentials: mysql -u root -p
4. Multi-User? Prüfe Firewall (Port 3306) und bind-address
```

### Ollama-Modelle nicht verfügbar
```
Problem: "Model phi3:mini not found"
Lösung:
1. Prüfe ob Ollama läuft: ollama list
2. Installiere Modelle: ollama pull phi3:mini && ollama pull qwen2.5:7b
3. Prüfe Ollama-URL in ConversationService.cs / DataAnalysisService.cs
```

### Azure OpenAI 401 Unauthorized
```
Problem: "Unauthorized - Invalid API Key"
Lösung:
1. Prüfe ob openai-api-key.txt existiert
2. Prüfe Key-Format: sk-proj-... (ohne Leerzeichen/Zeilenumbrüche)
3. Prüfe Endpoint-URL in RouterService.cs
```

### Erinnerungen werden nicht angezeigt
```
Problem: "Notification erscheint nicht trotz fälligem Termin"
Lösung:
1. Prüfe Debug-Log: [NOTIFICATION_SERVICE] Timer gestartet
2. Prüfe ReminderMinutesBefore != null in DB
3. Prüfe ReminderShown = false (wird nach Anzeige auf true gesetzt)
4. Prüfe 5-Minuten-Fenster: Reminder erscheint nur innerhalb 5min nach Fälligkeit
5. Restart App → Startup-Notification sollte erscheinen
```

### Dokumente werden nicht gefunden
```
Problem: "search_documents findet keine Dokumente"
Lösung:
1. Prüfe ob Dokumente in DB: SELECT * FROM Documents;
2. Prüfe ExtractedText: Ist Text vorhanden?
3. Suche ist Case-Insensitive: "Python" findet auch "python"
4. Suche durchsucht FileName + ExtractedText
```

### Typewriter-Effekt stockt
```
Problem: "AI-Antwort erscheint ruckartig"
Lösung:
1. Reduziere Delay in TypewriterEffect() (Standard: 20ms)
2. Prüfe CPU-Last (Ollama-Modelle können System belasten)
3. Deaktiviere während Debugging: Setze delay auf 0
```

---

## Bekannte Limitierungen

- **Dokument-Größe**: Max 4 MB (größere Dateien werden abgelehnt)
- **PDF-Extraktion**: Nur Text-basierte PDFs (keine OCR für Scans)
- **Kalender-Erinnerungen**: Nur während App läuft (kein System-Service)
- **Notification-Window**: 5 Minuten (danach keine erneute Anzeige)
- **Context-Limit**: Max 10 letzte Nachrichten im Chat-Context
- **Concurrency**: DbContext als Transient (keine parallelen Updates auf selber Entity)
- **Thumbnail**: Nur für PDFs (andere Formate zeigen File-Icon)

---

## Projekt-Statistik

### Zeilen Code (geschätzt):
- **Razor Components**: ~2.500 Zeilen (Home, Kalender, Daten, NotificationComponent)
- **Services**: ~1.800 Zeilen (ChatCoordinator, Router, Conversation, DataAnalysis, Calendar, Notification)
- **Database**: ~800 Zeilen (DbContext, ChatDbService, DocumentDbService, CalendarService)
- **MCP Tools**: ~600 Zeilen (DocumentTools, CalendarTools)
- **Models**: ~300 Zeilen (ChatResponse, Document, Conversation, Message, CalendarEvent, NotificationData)
- **CSS**: ~1.200 Zeilen (app.css, component-styles, calendar-styles)
- **Gesamt**: **~7.200 Zeilen**

### Technologien:
- **.NET MAUI 10** (Desktop-Framework)
- **Blazor Hybrid** (UI-Framework)
- **Entity Framework Core** (ORM)
- **MySQL** (Datenbank)
- **Microsoft Semantic Kernel** (AI-Orchestrierung)
- **Azure OpenAI SDK** (GPT-4)
- **Ollama** (lokale Modelle)
- **PdfPig** (PDF-Text-Extraktion)
- **SkiaSharp** (PDF-Thumbnail-Generierung)
- **Radzen Blazor** (UI-Komponenten: Dialog, Notification)
- **Model Context Protocol** (Tool-Calling Framework)

### Projektstruktur:
```
KIDT/
  +--- Components/
  |     +--- Pages/
  |     |     +--- Home.razor              (Chat-Interface)
  |     |     +--- Daten.razor             (Chat-History & Dokument-Explorer)
  |     |     +--- Kalender.razor          (Kalender mit 3 Ansichten)
  |     +--- Layout/
  |     |     +--- MainLayout.razor        (App-Layout mit Navigation)
  |     +--- UI/
  |           +--- NotificationComponent.razor (Toast-Notifications)
  |
  +--- Database/
  |     +--- ChatDbContext.cs            (EF Core Context)
  |     +--- ChatDbService.cs            (Chat-DB-Operationen)
  |     +--- DocumentDbService.cs        (Dokument-DB-Operationen)
  |     +--- Conversation.cs             (Model: Chat)
  |     +--- Message.cs                  (Model: Nachricht)
  |     +--- Document.cs                 (Model: Dokument)
  |     +--- ConversationDocument.cs     (Model: Verknüpfung)
  |     +--- CalendarEvent.cs            (Model: Kalender-Termin)
  |
  +--- Services/
  |     +--- CalendarService.cs          (Kalender-DB-Operationen)
  |     +--- NotificationService.cs      (Background-Timer für Erinnerungen)
  |     +--- ChatEventService.cs         (Event-Broker für Chat-Reload)
  |     +--- ThumbnailGenerator.cs       (PDF-Thumbnail-Generierung)
  |     +--- McpTools/
  |           +--- DocumentTools.cs      (MCP: Dokument-Tools)
  |           +--- CalendarTools.cs      (MCP: Kalender-Tools)
  |
  +--- Platforms/Windows/
  |     +--- ChatCoordinator.cs          (Zentrale Orchestrierung)
  |     +--- RouterService.cs            (Intent-Detection + MCP)
  |     +--- ConversationService.cs      (phi3:mini Ollama)
  |     +--- DataAnalysisService.cs      (qwen2.5:7b Ollama)
  |     +--- FileService.cs              (Text-Extraktion)
  |     +--- McpToolsRegistry.cs         (MCP-Tool-Registrierung)
  |
  +--- Models/
  |     +--- ChatResponse.cs             (Response-Model für AI)
  |     +--- NotificationData.cs         (Model: Notification-Payload)
  |
  +--- Prompts/
  |     +--- conversation-instructions.md (System-Prompt für phi3:mini)
  |     +--- data-analysis-instructions.md (System-Prompt für qwen2.5:7b)
  |
  +--- wwwroot/
  |     +--- css/
  |     |     +--- app.css                 (Basis-Styles)
  |     |     +--- calendar-components.css (Kalender-Styles)
  |     +--- images/                       (Icons & Assets)
  |
  +--- openai-api-key.txt                  (Azure OpenAI API-Key)
```

---

**Version**: 2.0  
**Framework**: .NET MAUI 10 / C# 14.0  
**Datenbank**: MySQL mit EF Core  
**KI-Modelle**: Azure OpenAI (GPT-4), Ollama (phi3:mini, qwen2.5:7b)  
**GitHub**: https://github.com/Paulschmidt-GDS2/KIDT

### Performance-Optimierungen:
- **AsNoTracking()**: Für Read-Only Queries (verhindert Change-Tracking Overhead)
- **Parallel DB-Operations**: Separate Scopes für gleichzeitige Queries
- **Typewriter-Effect**: 20ms Delay (smooth, nicht zu langsam)
- **Lazy-Loading**: Dokumente nur laden wenn Chat geöffnet
- **Batch-Delete**: RemoveRange() statt einzelner Remove()-Calls

---

## Troubleshooting

### MySQL-Verbindung fehlgeschlagen
```
Problem: "Unable to connect to any of the specified MySQL hosts"
Lösung:
1. Prüfe ob MySQL-Server läuft: Services → MySQL → Running?
2. Prüfe Connection-String in ChatDbContext.cs
3. Prüfe User-Credentials: mysql -u root -p
4. Multi-User? Prüfe Firewall (Port 3306) und bind-address
```

### Ollama-Modelle nicht verfügbar
```
Problem: "Model phi3:mini not found"
Lösung:
1. Prüfe ob Ollama läuft: ollama list
2. Installiere Modelle: ollama pull phi3:mini && ollama pull qwen2.5:7b
3. Prüfe Ollama-URL in ConversationService.cs / DataAnalysisService.cs
```

### Azure OpenAI 401 Unauthorized
```
Problem: "Unauthorized - Invalid API Key"
Lösung:
1. Prüfe ob openai-api-key.txt existiert
2. Prüfe Key-Format: sk-proj-... (ohne Leerzeichen/Zeilenumbrüche)
3. Prüfe Endpoint-URL in RouterService.cs
```

### Erinnerungen werden nicht angezeigt
```
Problem: "Notification erscheint nicht trotz fälligem Termin"
Lösung:
1. Prüfe Debug-Log: [NOTIFICATION_SERVICE] Timer gestartet
2. Prüfe ReminderMinutesBefore != null in DB
3. Prüfe ReminderShown = false (wird nach Anzeige auf true gesetzt)
4. Prüfe 5-Minuten-Fenster: Reminder erscheint nur innerhalb 5min nach Fälligkeit
5. Restart App → Startup-Notification sollte erscheinen
```

### Dokumente werden nicht gefunden
```
Problem: "search_documents findet keine Dokumente"
Lösung:
1. Prüfe ob Dokumente in DB: SELECT * FROM Documents;
2. Prüfe ExtractedText: Ist Text vorhanden?
3. Suche ist Case-Insensitive: "Python" findet auch "python"
4. Suche durchsucht FileName + ExtractedText
```

### Typewriter-Effekt stockt
```
Problem: "AI-Antwort erscheint ruckartig"
Lösung:
1. Reduziere Delay in TypewriterEffect() (Standard: 20ms)
2. Prüfe CPU-Last (Ollama-Modelle können System belasten)
3. Deaktiviere während Debugging: Setze delay auf 0
```

---

## Bekannte Limitierungen

- **Dokument-Größe**: Max 4 MB (größere Dateien werden abgelehnt)
- **PDF-Extraktion**: Nur Text-basierte PDFs (keine OCR für Scans)
- **Kalender-Erinnerungen**: Nur während App läuft (kein System-Service)
- **Notification-Window**: 5 Minuten (danach keine erneute Anzeige)
- **Context-Limit**: Max 10 letzte Nachrichten im Chat-Context
- **Concurrency**: DbContext als Transient (keine parallelen Updates auf selber Entity)
- **Thumbnail**: Nur für PDFs (andere Formate zeigen File-Icon)

---

## Projekt-Statistik

### Zeilen Code (geschätzt):
- **Razor Components**: ~2.500 Zeilen (Home, Kalender, Daten, NotificationComponent)
- **Services**: ~1.800 Zeilen (ChatCoordinator, Router, Conversation, DataAnalysis, Calendar, Notification)
- **Database**: ~800 Zeilen (DbContext, ChatDbService, DocumentDbService, CalendarService)
- **MCP Tools**: ~600 Zeilen (DocumentTools, CalendarTools)
- **Models**: ~300 Zeilen (ChatResponse, Document, Conversation, Message, CalendarEvent, NotificationData)
- **CSS**: ~1.200 Zeilen (app.css, component-styles, calendar-styles)
- **Gesamt**: **~7.200 Zeilen**

### Technologien:
- **.NET MAUI 10** (Desktop-Framework)
- **Blazor Hybrid** (UI-Framework)
- **Entity Framework Core** (ORM)
- **MySQL** (Datenbank)
- **Microsoft Semantic Kernel** (AI-Orchestrierung)
- **Azure OpenAI SDK** (GPT-4)
- **Ollama** (lokale Modelle)
- **PdfPig** (PDF-Text-Extraktion)
- **SkiaSharp** (PDF-Thumbnail-Generierung)
- **Radzen Blazor** (UI-Komponenten: Dialog, Notification)
- **Model Context Protocol** (Tool-Calling Framework)

### Projektstruktur:
```
KIDT/
  +--- Components/
  |     +--- Pages/
  |     |     +--- Home.razor              (Chat-Interface)
  |     |     +--- Daten.razor             (Chat-History & Dokument-Explorer)
  |     |     +--- Kalender.razor          (Kalender mit 3 Ansichten)
  |     +--- Layout/
  |     |     +--- MainLayout.razor        (App-Layout mit Navigation)
  |     +--- UI/
  |           +--- NotificationComponent.razor (Toast-Notifications)
  |
  +--- Database/
  |     +--- ChatDbContext.cs            (EF Core Context)
  |     +--- ChatDbService.cs            (Chat-DB-Operationen)
  |     +--- DocumentDbService.cs        (Dokument-DB-Operationen)
  |     +--- Conversation.cs             (Model: Chat)
  |     +--- Message.cs                  (Model: Nachricht)
  |     +--- Document.cs                 (Model: Dokument)
  |     +--- ConversationDocument.cs     (Model: Verknüpfung)
  |     +--- CalendarEvent.cs            (Model: Kalender-Termin)
  |
  +--- Services/
  |     +--- CalendarService.cs          (Kalender-DB-Operationen)
  |     +--- NotificationService.cs      (Background-Timer für Erinnerungen)
  |     +--- ChatEventService.cs         (Event-Broker für Chat-Reload)
  |     +--- ThumbnailGenerator.cs       (PDF-Thumbnail-Generierung)
  |     +--- McpTools/
  |           +--- DocumentTools.cs      (MCP: Dokument-Tools)
  |           +--- CalendarTools.cs      (MCP: Kalender-Tools)
  |
  +--- Platforms/Windows/
  |     +--- ChatCoordinator.cs          (Zentrale Orchestrierung)
  |     +--- RouterService.cs            (Intent-Detection + MCP)
  |     +--- ConversationService.cs      (phi3:mini Ollama)
  |     +--- DataAnalysisService.cs      (qwen2.5:7b Ollama)
  |     +--- FileService.cs              (Text-Extraktion)
  |     +--- McpToolsRegistry.cs         (MCP-Tool-Registrierung)
  |
  +--- Models/
  |     +--- ChatResponse.cs             (Response-Model für AI)
  |     +--- NotificationData.cs         (Model: Notification-Payload)
  |
  +--- Prompts/
  |     +--- conversation-instructions.md (System-Prompt für phi3:mini)
  |     +--- data-analysis-instructions.md (System-Prompt für qwen2.5:7b)
  |
  +--- wwwroot/
  |     +--- css/
  |     |     +--- app.css                 (Basis-Styles)
  |     |     +--- calendar-components.css (Kalender-Styles)
  |     +--- images/                       (Icons & Assets)
  |
  +--- openai-api-key.txt                  (Azure OpenAI API-Key)
```

---

## Technologien
**Framework**: .NET MAUI 10 / C# 14.0  
**Datenbank**: MySQL mit EF Core (Multi-User-fähig!)  
**KI-Modelle**: Azure OpenAI (GPT-4), Ollama (phi3:mini, qwen2.5:7b)  

---