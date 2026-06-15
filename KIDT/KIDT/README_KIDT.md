# KIDT - Technische Projektdokumentation

.NET MAUI 10 / Blazor Hybrid Desktop-App (Windows)
Router-LLM: google/gemini-2.5-flash via OpenRouter
Analyse-LLM: qwen3.5:9b via Ollama (lokal)
Datenbank: MySQL via Entity Framework Core

---

## Inhaltsverzeichnis

1. Architektur und Modelle
2. Dependency Injection (wer kennt wen)
3. Services - technische Details
4. Pages und Code-Behind
5. MCP-Tools (15 gesamt)
6. Kernaablaeufe
7. Datenbankschema
8. Dateistruktur
9. Setup

---

## 1. Architektur und Modelle

### Zwei-Modell-Architektur

```
Nutzer-Eingabe
      |
      v
ChatCoordinator          (Services/ChatCoordinator.cs)
      |
      +---> RouterService (Gemini 2.5 Flash Lite via OpenRouter)
      |         |
      |         +--> Tool-Call erkannt?
      |         |        |
      |         |        +--> MCP-Tool ausfuehren (Dokument / Ordner / Kalender)
      |         |        +--> Direkte Antwort an UI
      |         |
      |         +--> analyze_document Tool erkannt?
      |                  |
      |                  v
      +---> DataAnalysisService (qwen3.5:9b via Ollama)
                         |
                         v
                  Antwort an UI
```

Gemini uebernimmt: Intent-Erkennung, alle Tool-Calls, direkte Konversation.
qwen3.5:9b uebernimmt: ausschliesslich Dokument-Analyse (wenn analyze_document aufgerufen).

### Modelle

| Modell                 | Engine         | Aufgabe                                        | Konfiguration                    |
|------------------------|----------------|------------------------------------------------|----------------------------------|
| gemini-2.5-flash       | OpenRouter API | Routing, Tool-Calls, Konversation              | Temperature 0.1, MaxTokens 2000  |
| qwen3.5:9b             | Ollama lokal   | Dokument-Analyse (PDF/Text-Inhalt)             | Temperature 0.3, MaxTokens 2000  |

---

## 2. Dependency Injection (wer kennt wen)

Registrierung in `MauiProgram.cs`:

```
Singleton:  ChatCoordinator      --> kennt IServiceProvider (erstellt Scopes intern)
Singleton:  AppNotificationService
Singleton:  ThumbnailGenerator
Singleton:  ThemeService
Singleton:  ITitleBarService / TitleBarService (Windows)

Transient:  ChatDbService
Transient:  DocumentDbService
Transient:  CalendarService
Transient:  FolderDbService

Transient:  ChatDbContext (EF Core, MAUI braucht Transient statt Scoped)
```

Transient-Services werden nicht direkt injiziert sondern per `IServiceProvider.CreateScope()` erzeugt.
Grund: Singleton-Services duerfen keinen Transient-Service direkt halten (Lifetime-Konflikt).

Ablauf in RouterService und ChatCoordinator:
```csharp
using var scope = this.serviceProvider.CreateScope();
var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();
```

Razor-Pages injizieren Services direkt per `@inject`:
```
Home.razor:    @inject ChatCoordinator Chat
               @inject ChatDbService Db
               @inject ThumbnailGenerator ThumbGen
               @inject IServiceProvider ServiceProvider

Daten.razor:   @inject ChatDbService ChatDb
               @inject DocumentDbService DocDb
               @inject FolderDbService FolderDb
               @inject ThumbnailGenerator ThumbGen

Kalender.razor: @inject CalendarService CalendarService
```

---

## 3. Services - technische Details

### ChatCoordinator  (Services/ChatCoordinator.cs)

Singleton. Zentraler Orchestrator. Einzige Klasse die Home.razor direkt kennt (via IAsyncEnumerable Streaming).

Felder:
- `RouterService router` - wird in InitializeAsync erstellt
- `DataAnalysisService dataAnalysis` - fuer Ollama-Analyse
- `FileService fileService` - fuer Datei-Text-Extraktion
- `string currentFileName / currentFileContent` - aktuell angehaengte Datei

Methoden:
- `InitializeAsync()` - laedt API-Key, erstellt Services, wird lazy beim ersten Aufruf ausgefuehrt
- `SendStreamAsync(userMessage, conversationId, deepThink, cancellationToken)` - Hauptmethode
  1. IsValidUserInput() pruefen (blockiert Gibberish/Wiederholungen)
  2. RouterService.ProcessAsync() aufrufen
  3. Wenn ShouldRoute=false: direkte Antwort als ChatStreamChunk zurueck
  4. Wenn ShouldRoute=true (dataAnalysis): DataAnalysisService.SendAsync() aufrufen
  5. yield return ChatStreamChunk mit Text, FoundDocuments, FoundEvents, ModelLabel
- `UploadFileAsync(filePath)` - FileService.ExtractTextAsync(), setzt currentFileContent
- `ClearFile()` - leert currentFileName und currentFileContent
- `GetCurrentFileContent()` - liefert extrahierten Datei-Text fuer DB-Speicherung

### RouterService  (Services/RouterService.cs + Handlers.cs + Models.cs)

Partial class, drei Dateien. Erstellt und gehalten von ChatCoordinator.

RouterService.cs - Kernlogik:
- `InitializeAsync()` - liest OpenRouter-api-key.txt aus AppDomain.BaseDirectory
- `ProcessAsync(userMessage, hasFile, conversationId, cancellationToken)` - Hauptmethode:
  1. Kernel mit AddOpenAIChatCompletion (OpenRouter-Endpoint) aufbauen
  2. McpToolsRegistry.RegisterTools() - alle 15 Tools registrieren
  3. System-Prompt mit aktuellem Datum/Zeit erstellen (BuildSystemPrompt)
  4. Letzte 10 Nachrichten aus ChatDbService laden, als ChatHistory aufbauen
  5. DocIDs aus letzten 3 Nachrichten extrahieren, als CONTEXT-System-Message anhaengen
  6. FunctionChoiceBehavior.Auto (autoInvoke=false, AllowParallelCalls=false) - manueller Dispatch
  7. GetChatMessageContentAsync() aufrufen
  8. Bei ArgumentOutOfRangeException(ChatFinishReason): einmal Retry nach 300ms
  9. Response.Items nach FunctionCallContent durchsuchen
  10. Tool-Name normalisieren (NormalizeFunctionName: entfernt Gemini-Prefixe wie "Document_")
  11. Tool in Kernel suchen (FindFunctionInKernel: plugin-unabhaengig per Name)
  12. Tool ausfuehren: fn.InvokeAsync(kernel, arguments)
  13. DispatchToolResultAsync() fuer passenden Handler
  14. Kein Tool: JSON-Route pruefen oder direkte Konversationsantwort

RouterService.Handlers.cs - 14 Handler:
- HandleSearchDocumentsAsync - laedt volle Dokumente per ID, gibt Cards zurueck
- HandleListCalendarEventsAsync - laedt volle Events per ID, gibt Cards zurueck
- HandleCreateCalendarEvent / HandleDeleteCalendarEvent / HandleUpdateCalendarEvent
- HandleAddDocumentToChatAsync - bei Erfolg: ShouldRoute=true -> DataAnalysis
- HandleListDocumentsInFolderAsync - wie SearchDocuments, mit DocID-Marker
- HandleFolderOperationResult - universell fuer alle 6 Ordner-Operationen
- HandleListAllFoldersResult / HandleFindDocumentsResult
- HandleAnalyzeDocumentAsync - ShouldRoute=true, TargetService="dataAnalysis"

RouterService.Models.cs - interne JSON-Klassen:
- SearchResultJson, CalendarListResultJson, CalendarCreateResultJson
- CalendarDeleteResultJson, CalendarUpdateResultJson
- FolderResultJson, FolderListResultJson, AllFoldersResultJson
- FindDocumentsResultJson, AddDocumentResultJson, SimpleRoutingJson

### DataAnalysisService  (Services/DataAnalysisService.cs)

Gehalten von ChatCoordinator. Ollama HTTP-Client.
- Endpoint: `http://localhost:11434` (hardcodiert)
- Modell: qwen3.5:9b
- `SendAsync(userMessage, fileContent, fileName, maxTokens, chatHistory, deepThink, ct)`:
  System-Prompt + Chat-History + Datei-Inhalt + Frage -> Ollama -> Antwort als string

### CalendarService  (Database/CalendarService.cs)

Transient. In `Database/` angesiedelt da eng mit EF-Core-Context verzahnt. Direkt von RouterService-Scopes und Razor-Pages genutzt.
- `GetAllEventsAsync()` - alle Termine sortiert nach Start
- `GetEventByIdAsync(id)` - Einzel-Termin fuer Card-Anzeige
- `AddEventAsync(event)` / `UpdateEventAsync(event)` / `DeleteEventAsync(id)`
- `DeleteEventDirectAsync(event)` - fuer Kalender.razor (hat das Objekt bereits)
- `EnsureDatabaseSchemaAsync()` - Raw SQL ALTER TABLE fuer neue Spalten (ignoriert Fehler 1060)

### ChatDbService  (Database/ChatDbService.cs)

Transient. Chat-History und Konversationen.
- `CreateConversationAsync(title)` - neue Conversation
- `SaveMessageAsync(convId, isUser, text, docIds?, eventIds?)` - Nachricht + JSON-Arrays
- `LoadMessagesAsync(convId)` - letzte Nachrichten fuer Context und UI-Reload
- `LoadAllConversationsAsync()` - alle Chats mit LinkedDocuments (manuell via ConversationDocuments geladen)
- `DeleteConversationAsync(id)` - Conversation + Messages + ConversationDocuments
- `UpdateConversationTitleAsync(id)` - setzt Titel aus erster User-Nachricht (max 50 Zeichen)
- `GetFullChatHistoryAsync(convId)` - formatierter Text fuer DataAnalysis-Context

### DocumentDbService  (Database/DocumentDbService.cs)

Transient. Globale Dokument-Bibliothek.
- `SaveDocumentAsync(name, content, type, extractedText, thumbnail)` - prueft SHA256-Hash auf Duplikate
- `GetDocumentByIdAsync(id)` - Einzel-Dokument
- `SearchDocumentsAsync(query)` - LIKE-Suche in FileName + ExtractedText
- `GetDocumentsForConversationAsync(convId)` - alle verknuepften Dokumente
- `LinkDocumentToConversationAsync(docId, convId)` - Junction-Eintrag
- `UnlinkDocumentFromConversationAsync(docId, convId)` - Junction-Eintrag loeschen

### FolderDbService  (Database/FolderDbService.cs)

Transient. Ordner-Verwaltung mit n:m-Beziehung.
- `GetAllFoldersAsync()` / `CreateFolderAsync(name)` / `DeleteFolderAsync(id)`
- `GetRootDocumentsAsync()` - Dokumente mit IsInRoot=true
- `GetDocumentsInFolderAsync(folderId)` - Dokumente via DocumentFolders-Junction
- `CopyDocumentToFolderAsync(docId, folderId)` - neuer Junction-Eintrag
- `CopyDocumentToRootAsync(docId)` - setzt IsInRoot=true
- `MoveDocumentToFolderAsync(docId, folderId?)` - entfernt alle Junctions, setzt neuen Standort
- `SetDocumentInRootAsync(docId, inRoot)` - IsInRoot setzen
- `DeleteDocumentFromLocationAsync(docId, folderId?)` - entfernt nur von diesem Standort
- `EnsureDatabaseSchemaAsync()` - erstellt Folders + DocumentFolders, migriert altes FolderId-Feld

### AppNotificationService  (Services/NotificationService.cs)

Singleton. Background-Timer.
- Timer laeuft alle 30 Sekunden
- `CheckForDueRemindersAsync()`:
  Events mit ReminderMinutesBefore != null und ReminderShown=false laden
  ReminderTime = EventStart - ReminderMinutesBefore Minuten
  Wenn Now >= ReminderTime und Now < ReminderTime+5min:
  ReminderShown=true setzen, Event `OnNotificationRequested` ausloesen
- `ShowStartupNotificationAsync()` - naechste 3 Termine bei App-Start anzeigen
- `OnNotificationRequested` - event Action<NotificationData>, von MainLayout abonniert

### ThumbnailGenerator  (Services/ThumbnailGenerator.cs)

Singleton. Windows-only (Windows.Data.Pdf).
- `GenerateThumbnailAsync(filePath)` - nur bei .pdf, sonst string.Empty
- PDF-Rendering: erste Seite, 200px Breite, als PNG -> Base64-String
- Ergebnis wird in Document.ThumbnailBase64 gespeichert

### ThemeService  (Services/ThemeService.cs)

Singleton. Hell/Dunkel-Theme.
- Liest und schreibt Theme-Einstellung via MAUI Preferences API
- TitleBarService (Windows-spezifisch) passt Titelleisten-Farbe ans Theme an

### ChatEventService  (Services/ChatEventService.cs)

Static. Event-Broker fuer Neuer-Chat-Aktion.
- `OnNewChatRequested` - static event, von MainLayout ausgeloest, von Home.razor abonniert

### FileService  (Platforms/Windows/FileService.cs)

Erstellt von ChatCoordinator (nicht per DI).
- `ExtractTextAsync(filePath)`:
  .pdf -> PdfPig Textextraktion seitenweise
  .txt/.md/.json -> File.ReadAllText

---

## 4. Pages und Code-Behind

### Home (Chat-Seite)

Vier Dateien bilden eine partial class:

`Home.razor` - Template
- Rendert Nachrichten-Liste mit ChatMessage-Objekten
- Assistent-Nachrichten: Typewriter-Text + optionale Dokument-Cards + Event-Cards
- Dokument-Card: Thumbnail oder File-Icon, Klick oeffnet Datei, Button "Zum Chat hinzufuegen"
- Event-Card: Datum, Zeit, Titel, Loeschen-Button, Klick oeffnet Edit-Dialog
- Chat-Input: Textarea (dynamische Hoehe), Upload-Button, Send/Abort-Button
- Edit-Dialog: Termin-Bearbeitung mit Titel, Ganztaegig, Von/Bis, Farbe, Erinnerung

`Home.razor.cs` - Kern
- `ChatMessage` (private inner class): Text, IsUser, IsLoading, DisplayText, LoadingText, FoundDocuments, FoundEvents, ModelLabel
- `SendMessage(text)`:
  User-Nachricht sofort in UI, Loading-Spinner, DB-Speicherung parallel
  Chat.SendStreamAsync() -> IAsyncEnumerable<ChatStreamChunk>
  Typewriter-Drain-Task (~45fps, adaptiver Schritt: 2-3 Zeichen/Frame)
  Nach Stream: Assistent-Antwort mit DocIDs und EventIDs in DB speichern
- `TypewriterEffect(message, fullText)` - zeichenweise Anzeige, gleiche Logik wie Drain
- `OnInitializedAsync()` - bestehenden Chat laden: Messages + Documents parallel, DocIDs und EventIDs aus JSON wiederherstellen
- `ResetChat()` - Navigation.NavigateTo("/", forceLoad:true)

`Home.Upload.cs` - Datei-Operationen
- `OnUploadClick()`: FilePicker -> Chat.UploadFileAsync() -> ThumbnailGenerator + DocumentDbService parallel
- `RemoveFile()`: Badge leeren, DocumentDbService.UnlinkDocumentFromConversationAsync()
- `OpenDocument(id)`: Dokument aus DB laden, in %TEMP% schreiben, Process.Start()
- `AddDocumentToChat(id)`: DocumentDbService.LinkDocumentToConversationAsync()

`Home.EventDialog.cs` - Termin-Edit-Dialog
- `OpenEventEditDialog(eventId)`: Event laden, alle Dialog-Felder befuellen
- `SaveEventEdit()`: CalendarService.UpdateEventAsync(), FoundEvents in allen Messages aktualisieren
- `DeleteEventFromChat(eventId)`: CalendarService.DeleteEventAsync(), Event aus allen Messages entfernen
- Zeit-Handler: ClampHour/ClampMinute/FormatTimeStr fuer Stunden+Minuten-Eingabefelder

### Daten (Chats und Dokumente)

`Daten.razor` - Template: zwei Tabs (Chats / Dokumente), Ordner-Explorer mit Breadcrumb, Clipboard-Leiste, Dokument-Zeilen mit Copy/Move/Delete-Aktionen

`Daten.razor.cs` - Logik:
- Tab "Chats": LoadAllConversationsAsync(), OpenChat(id) -> Navigation, DeleteChat(id) mit Animation
- Tab "Dokumente": Root-Ansicht (Ordner + Dateien) und Ordner-Ansicht (nur Dateien)
- Ordner-Operationen: CreateFolderAsync, DeleteFolderAsync, OpenFolder, NavigateToRoot
- Dokument-Operationen: SelectDocument (Einfachklick), CopyToClipboard, PasteDocumentHere, MoveDocumentTo, DeleteDocument
- Strg+C/V via JS-Interop: clipboardHelper.register(dotnetRef), OnCtrlC/OnCtrlV [JSInvokable]
- Upload direkt auf Daten-Seite: ThumbGen.GenerateThumbnailAsync + DocDb.SaveDocumentAsync

### Kalender

`Kalender.razor` - Template: Toolbar (Prev/Today/Next, Monat/Woche/Tag), Kalender-Grid, Add-Dialog, Edit-Dialog

`Kalender.razor.cs` - Logik:
- Drei Ansichten: Monatsansicht (42 Zellen = 7x6), Wochenansicht (7 Spalten Mo-So), Tagesansicht (Inline-Form)
- `events` Dictionary<DateTime, List<CalendarEvent>> - Cache fuer schnelle Datum-Abfragen
- `BuildMonthCells()` / `BuildWeekDays()` - Zellen aufbauen, Montag-Offset berechnen
- `GetEventsForDate(date)` - aus Dictionary, leere Liste wenn kein Eintrag
- `OnParametersSetAsync()` - URL-Parameter ?eventId=x -> direkt Edit-Dialog oeffnen (fuer Notification-Klick)
- Zeit-Picker: getrennte Stunden/Minuten-Inputs, ClampHour/ClampMinute, FormatTimeStr

### MainLayout  (Components/Layout/MainLayout.razor)

- Navigation: Home, Kalender, Daten-Seite, Neuer-Chat-Button
- `OnInitializedAsync()`: AppNotificationService.Start() + ShowStartupNotificationAsync()
- Abonniert AppNotificationService.OnNotificationRequested -> NotificationComponent.ShowNotification()

### NotificationComponent  (Components/UI/NotificationComponent.razor)

- Toast-Anzeige: Titel, Datum, Zeit (oder "naechste 3 Termine" bei Startup)
- Auto-Dismiss nach 10 Sekunden (Timer in ShowNotification)
- Click -> Navigation.NavigateTo("/kalender?eventId=x")

---

## 5. MCP-Tools (15 gesamt)

Registriert von `McpToolsRegistry.cs` per Reflection: alle Klassen mit `[McpServerToolType]`, alle Methoden mit `[McpServerTool]`.

RouterService erstellt den Kernel, `McpToolsRegistry.RegisterTools()` wird mit `docDbService`, `calendarService`, `folderDbService` und `conversationId` aufgerufen. Services werden in den Tool-Instanzen gebunden.

### DocumentTools  (Services/McpTools/DocumentTools.cs)

| Tool                 | Parameter               | JSON-Rueckgabe                              |
|----------------------|-------------------------|---------------------------------------------|
| search_documents     | query: string           | found, documentIds[], message               |
| add_document_to_chat | documentId: int         | success, documentId, fileName, message      |

add_document_to_chat erstellt ConversationDocuments-Eintrag und setzt `[DocID: x]`-Marker.

### FolderTools  (Services/McpTools/FolderTools.cs)

| Tool                       | Parameter                     | JSON-Rueckgabe           |
|----------------------------|-------------------------------|--------------------------|
| create_folder              | name: string                  | success, message         |
| delete_folder              | folderName: string            | success, message         |
| move_document_to_folder    | documentName, folderName      | success, message         |
| copy_document_to_folder    | documentName, folderName      | success, message         |
| remove_document_from_folder| documentName, folderName      | success, message         |
| list_documents_in_folder   | folderName: string            | success, found, docs[]   |
| list_all_folders           | (keine)                       | success, found, folders[]|
| find_documents             | fileName: string              | success, found, docs[]   |

folderName='hauptbereich' = Root (IsInRoot=true, kein Ordner-Eintrag).

### CalendarTools  (Services/McpTools/CalendarTools.cs)

| Tool                  | Parameter                                             | JSON-Rueckgabe                              |
|-----------------------|-------------------------------------------------------|---------------------------------------------|
| list_calendar_events  | titleSearch?, startDate?, endDate?                    | found, events[], message                    |
| create_calendar_event | date, title, isAllDay, time?, endTime?, colorIndex?, reminderMinutes? | success, message, eventId   |
| delete_calendar_event | eventId?, date?, title?                               | success/needsClarification, message         |
| update_calendar_event | eventId?, date+title?, newTitle?, newDate?, usw.      | success/needsClarification, message         |

Bei delete/update mit mehrdeutigem Ergebnis: needsClarification=true + events-Liste.

### AnalysisTools  (Services/AnalysisTools.cs)

| Tool             | Parameter    | Besonderheit                                       |
|------------------|--------------|----------------------------------------------------|
| analyze_document | docId: int   | Dummy-Return (docId als String), echter Dispatch   |

RouterService prueft in Tool-Dispatch-Schleife: wenn normalizedFunctionName == "analyze_document", direkt HandleAnalyzeDocumentAsync aufrufen, ohne das Tool wirklich auszufuehren. Weiterleitung zu DataAnalysisService.

---

## 6. Kernaablaeufe

### Chat-Nachricht mit Tool-Call

```
Home.razor: SendMessage(text)
  --> Messages.Add(userMessage)           sofort in UI
  --> Messages.Add(loadingMessage)        Spinner
  --> Db.SaveMessageAsync(user)           parallel im Hintergrund
  --> Chat.SendStreamAsync()
        --> RouterService.ProcessAsync()
              --> Gemini: FunctionCallContent erkannt
              --> NormalizeFunctionName()
              --> FindFunctionInKernel()
              --> fn.InvokeAsync() -> JSON-String
              --> DispatchToolResultAsync() -> passender Handler
              --> RouterResponse { DirectResponse, FoundDocuments/Events }
        --> yield ChatStreamChunk { Text, FoundDocuments, FoundEvents, ModelLabel }
  --> Drain-Task: Typewriter-Ausgabe
  --> Db.SaveMessageAsync(assistant, docIds, eventIds)
```

### Dokument-Upload

```
Home.Upload.cs: OnUploadClick()
  --> FilePicker.PickAsync()
  --> Chat.UploadFileAsync(path)
        --> FileService.ExtractTextAsync()   PdfPig oder File.ReadAllText
        --> currentFileContent setzen
  --> Parallel: Task.Run()
        --> ThumbGen.GenerateThumbnailAsync()   PDF Seite 1 als PNG Base64
        --> DocDb.SaveDocumentAsync()           SHA256 Duplikat-Check
        --> DocDb.LinkDocumentToConversationAsync()
        --> Db.SaveMessageAsync("[DocID: x]")   Marker fuer RouterService-Context
  --> TypewriterEffect("Datei geladen...")
```

### Termin-Karte aus Chat loeschen

```
Home.razor: Klick auf "Loeschen" in Event-Card
  --> DeleteEventFromChat(eventId)
        --> CalendarService.DeleteEventAsync(eventId)
        --> foreach msg in Messages:
              foreach ev in msg.FoundEvents:
                  if ev.Id == eventId -> eventToRemove merken
              if eventToRemove != null -> FoundEvents.Remove()
        --> TypewriterEffect("Termin geloescht")
```

### Erinnerungs-Ablauf

```
AppNotificationService: Timer alle 30s
  --> CheckForDueRemindersAsync()
        --> CalendarService: Events mit Reminder und ReminderShown=false
        --> ReminderTime = Start.Date + Time - ReminderMinutesBefore Minuten
        --> Now in [ReminderTime, ReminderTime+5min]?
              --> event.ReminderShown = true
              --> CalendarService.UpdateEventAsync()
              --> OnNotificationRequested?.Invoke(NotificationData)
MainLayout: empfaengt Event
  --> NotificationComponent.ShowNotification(data)
        --> Toast anzeigen, Timer 10s, Click -> /kalender?eventId=x
```

---

## 7. Datenbankschema

Tabellen werden per `EnsureCreated()` in MauiProgram.cs angelegt.
Neue Spalten werden per Raw SQL in `EnsureDatabaseSchemaAsync()` nachgezogen (CalendarService + FolderDbService).

```
Conversations
  Id           int PK auto_increment
  Title        varchar
  CreatedAt    datetime

Messages
  Id               int PK auto_increment
  ConversationId   int FK -> Conversations.Id
  IsUser           bool
  Text             longtext
  Timestamp        datetime
  DocumentIdsJson  text       (List<int> als JSON, fuer Card-Reload beim Chat-Laden)
  EventIdsJson     text       (List<int> als JSON, fuer Card-Reload beim Chat-Laden)

Documents
  Id               int PK auto_increment
  FileName         varchar
  FileHash         varchar    (SHA256, Duplikat-Sperre)
  FileContent      longtext   (Base64 bei PDF, Klartext bei TXT/MD/JSON)
  FileType         varchar    (pdf/txt/md/json)
  ExtractedText    longtext   (fuer Volltextsuche und Dokument-Analyse)
  ThumbnailBase64  longtext   (PNG der ersten PDF-Seite, 200px)
  UploadedAt       datetime
  IsInRoot         bool       (true = im Hauptbereich sichtbar)

ConversationDocuments           Many-to-Many: Conversation <-> Document
  ConversationId   int PK FK
  DocumentId       int PK FK
  AddedAt          datetime

Folders
  Id           int PK auto_increment
  Name         varchar
  CreatedAt    datetime

DocumentFolders                 Many-to-Many: Document <-> Folder
  DocumentId   int PK FK
  FolderId     int PK FK

CalendarEvents
  Id                    int PK auto_increment
  Title                 varchar
  Start                 datetime   (nur Datum, Zeit separat)
  IsAllDay              bool
  Time                  time       (Startzeit als TimeSpan)
  HasTime               bool
  EndTime               time
  HasEndTime            bool
  ColorIndex            int        (0-7, CSS-Klasse event-color-{index})
  ReminderMinutesBefore int?       (null = keine Erinnerung)
  ReminderShown         bool       (verhindert Doppel-Notification)
  CreatedAt             datetime
  UpdatedAt             datetime
```

---

## 8. Dateistruktur

```
KIDT/
  +-- Components/
  |     +-- Pages/          (Seiten als partial class: .razor + .razor.cs [+ weitere .cs])
  |     +-- Layout/         (App-Rahmen und Navigation)
  |     +-- UI/             (wiederverwendbare UI-Komponenten)
  |
  +-- Database/
  |     +-- Entities/       (EF-Core-Modelle, Namespace KIDT.Models)
  |     CalendarService.cs  (CRUD Kalender-Termine)
  |     ChatDbContext.cs
  |     ChatDbService.cs
  |     DocumentDbService.cs
  |     FolderDbService.cs
  |
  +-- Services/
  |     +-- AI/             (LLM-Services und Analyse)
  |     +-- McpTools/       (die 15 MCP-Tool-Implementierungen)
  |     +-- Notifications/  (Erinnerungs-Service und DTOs)
  |     +-- Router/         (Routing-Logik, Handlers, Models)
  |     +-- UI/             (Theme, TitleBar, ChatEvent, Thumbnail)
  |
  +-- Platforms/Windows/    (Windows-spezifischer Code)
  +-- wwwroot/              (CSS, JS, Icons)
  +-- MauiProgram.cs        (DI-Setup + App-Start)
  +-- OpenRouter-api-key.txt
```

---

### Components/Pages/  -  Praesentationsschicht

Home ist eine partial class aufgeteilt auf vier Dateien:

| Datei                  | Inhalt und Aufgabe                                                          |
|------------------------|-----------------------------------------------------------------------------|
| Home.razor             | Template: Nachrichtenliste, Dokument-Cards, Event-Cards, Chat-Input, Edit-Dialog |
| Home.razor.cs          | Kern: ChatMessage-Klasse, Felder, OnInitializedAsync, SendMessage, TypewriterEffect, ResetChat |
| Home.Upload.cs         | OnUploadClick, RemoveFile, OpenDocument, AddDocumentToChat                  |
| Home.EventDialog.cs    | Dialog-Felder, OpenEventEditDialog, SaveEventEdit, DeleteEventFromChat      |

Daten und Kalender je zwei Dateien:

| Datei                  | Inhalt und Aufgabe                                                          |
|------------------------|-----------------------------------------------------------------------------|
| Daten.razor            | Template: Chat-Karten (Tab), Ordner-Explorer mit Breadcrumb (Tab)           |
| Daten.razor.cs         | Tabs, Ordner-Operationen, Clipboard (Strg+C/V via JS), Upload, Chat-Loeschen |
| Kalender.razor         | Template: Monat/Woche/Tag-Ansicht, Add-Dialog, Edit-Dialog                  |
| Kalender.razor.cs      | events-Dictionary, BuildMonthCells, BuildWeekDays, GetEventsForDate, Dialoge |

Layout und UI:

| Datei                        | Inhalt und Aufgabe                                                    |
|------------------------------|-----------------------------------------------------------------------|
| MainLayout.razor             | Navigation, Notification-Init (AppNotificationService.Start), Theme  |
| NotificationComponent.razor  | Toast: Erinnerung oder Startup-Uebersicht, Auto-Dismiss 10s, Click-Nav |

---

### Services/AI/  -  LLM-Orchestrierung und Analyse

| Datei                  | Inhalt und Aufgabe                                                           |
|------------------------|------------------------------------------------------------------------------|
| ChatCoordinator.cs     | Singleton-Orchestrator: SendStreamAsync, UploadFileAsync, haelt Router + DataAnalysis |
| DataAnalysisService.cs | Ollama HTTP-Client fuer qwen3.5:9b, bekommt Dokument-Text + Frage           |
| AnalysisTools.cs       | Dummy-SK-Plugin: analyze_document gibt nur docId zurueck, Dispatch in RouterService |
| ChatStreamChunk.cs     | Streaming-Einheit: TextChunk, IsComplete, FoundDocuments, FoundEvents, ModelLabel |

### Services/Router/  -  Routing-Logik

| Datei                     | Inhalt und Aufgabe                                                           |
|---------------------------|------------------------------------------------------------------------------|
| RouterService.cs          | Gemini-Aufruf (OpenRouter), System-Prompt, Tool-Dispatch, Chat-History-Aufbau |
| RouterService.Handlers.cs | 14 Handler-Methoden fuer alle Tool-Ergebnis-Typen                            |
| RouterService.Models.cs   | Interne JSON-Deserialisierungsklassen fuer Tool-Rueckgaben                   |
| RouterResponse.cs         | Rueckgabe von RouterService: ShouldRoute, DirectResponse, FoundDocuments, FoundEvents |

### Services/Notifications/  -  Erinnerungs-System

| Datei                  | Inhalt und Aufgabe                                                           |
|------------------------|------------------------------------------------------------------------------|
| NotificationService.cs | Singleton, 30s-Timer, CheckForDueRemindersAsync, ShowStartupNotificationAsync |
| NotificationData.cs    | Enum NotificationType (StartupOverview, EventReminder) + Termin-Felder       |

### Services/UI/  -  UI-Hilfsdienste

| Datei               | Inhalt und Aufgabe                                                             |
|---------------------|--------------------------------------------------------------------------------|
| ThemeService.cs     | Singleton, Hell/Dunkel via MAUI Preferences API                                |
| ThumbnailGenerator.cs | Singleton, Windows.Data.Pdf: erste PDF-Seite als PNG Base64 (200px)          |
| ChatEventService.cs | Static event OnNewChatRequested (MainLayout -> Home.razor)                     |
| ITitleBarService.cs | Interface fuer TitleBarService (Windows-Impl. in Platforms/Windows/)           |

### Services/McpTools/  -  MCP-Tool-Implementierungen

| Datei              | Enthaelt                                                              |
|--------------------|-----------------------------------------------------------------------|
| DocumentTools.cs   | search_documents, add_document_to_chat                                |
| FolderTools.cs     | create_folder, delete_folder, move/copy/remove, list, list_all, find |
| CalendarTools.cs   | list_calendar_events, create/delete/update_calendar_event             |

---

### Database/  -  Datenzugriff und EF-Core-Modelle

DB-Services:

| Datei                | Inhalt und Aufgabe                                                          |
|----------------------|-----------------------------------------------------------------------------|
| ChatDbContext.cs     | EF Core DbContext, Connection-String, alle DbSets, OnModelCreating           |
| ChatDbService.cs     | CRUD Conversations + Messages, LoadAllConversationsAsync laedt LinkedDocuments manuell |
| DocumentDbService.cs | CRUD Documents (global), SHA256-Duplikat-Check, ConversationDocuments-Verknuepfungen |
| FolderDbService.cs   | CRUD Folders + DocumentFolders (n:m), IsInRoot, EnsureDatabaseSchemaAsync   |
| CalendarService.cs   | CRUD CalendarEvents, EnsureDatabaseSchemaAsync (ALTER TABLE Raw SQL)        |

EF-Core-Entities (Database/Entities/, Namespace KIDT.Models):

| Datei                    | Model und Besonderheit                                                  |
|--------------------------|-------------------------------------------------------------------------|
| Conversation.cs          | Chat-Kopf, [NotMapped] LinkedDocuments wird zur Laufzeit befuellt       |
| Message.cs               | Nachricht, DocumentIdsJson + EventIdsJson als JSON-String in DB         |
| Document.cs              | Globales Dokument, ThumbnailBase64, FileHash, IsInRoot                  |
| ConversationDocument.cs  | Junction: Conversation <-> Document (Composite PK)                     |
| Folder.cs                | Ordner-Entity                                                           |
| DocumentFolder.cs        | Junction: Document <-> Folder (Composite PK, n:m)                      |
| CalendarEvent.cs         | Termin: Start, Time, EndTime, HasTime, HasEndTime, ColorIndex, Reminder |

---

### Platforms/Windows/  -  Windows-spezifische Implementierungen

| Datei               | Inhalt und Aufgabe                                                             |
|---------------------|--------------------------------------------------------------------------------|
| McpToolsRegistry.cs | RegisterTools(): Reflection ueber [McpServerToolType]-Klassen -> SK-Plugins    |
| FileService.cs      | ExtractTextAsync(): PdfPig fuer PDF, File.ReadAllText fuer TXT/MD/JSON         |
| TitleBarService.cs  | Implementiert ITitleBarService, passt Windows-Titelleiste ans Theme an         |
| App.xaml.cs         | MAUI Windows-App-Einstiegspunkt                                                |

---

### Root

| Datei                  | Inhalt und Aufgabe                                                      |
|------------------------|-------------------------------------------------------------------------|
| MauiProgram.cs         | DI-Registrierungen, EnsureCreated, EnsureDatabaseSchemaAsync beim Start |
| OpenRouter-api-key.txt | API-Key, wird per .csproj ins Output kopiert, in .gitignore eingetragen |

---

## 9. Setup

### Voraussetzungen

- .NET 10 SDK
- MySQL Server (lokal)
- Ollama mit qwen3.5:9b: `ollama pull qwen3.5:9b`
- OpenRouter-Account (openrouter.ai)

### Schritte

1. MySQL-Datenbank anlegen:
```sql
CREATE DATABASE kidt_chat;
GRANT ALL PRIVILEGES ON kidt_chat.* TO 'root'@'localhost';
```

2. Connection-String in `Database/ChatDbContext.cs` anpassen:
```
Server=localhost;Port=3306;Database=kidt_chat;User=root;Password=...;
```

3. Datei `KIDT/OpenRouter-api-key.txt` anlegen (sk-or-v1-...):
   Wird beim Build nach Output kopiert. Nicht committen (.gitignore).

4. App starten (F5 in Visual Studio):
   - EF Core erstellt alle Tabellen automatisch
   - CalendarService und FolderDbService fuehren Schema-Migration aus
   - Startup-Notification zeigt naechste 3 Termine

### Router-Modell wechseln

In `RouterService.cs`:
```csharp
modelId: "google/gemini-2.5-flash",
endpoint: new Uri("https://openrouter.ai/api/v1")
```
Jedes OpenRouter-kompatible Modell kann eingesetzt werden.

### Analyse-Modell wechseln

In `DataAnalysisService.cs`: Modellname und Ollama-Endpoint anpassen.

---

## Bekannte Grenzen

- PDF-Extraktion nur bei Text-PDFs (keine OCR fuer gescannte Dokumente)
- Kalender-Erinnerungen laufen nur waehrend die App offen ist (kein System-Service)
- Chat-Kontext: maximal 10 letzte Nachrichten ans LLM
- "Zeige mir diesen Termin" via Chat kann den Router ueberfordern (kein Detail-Tool vorhanden)
- Ollama muss lokal auf Port 11434 laufen, kein Cloud-Fallback

---

Version 3.1 | .NET MAUI 10 | Gemini 2.5 Flash Lite + qwen3.5:9b | 2026-06-06
