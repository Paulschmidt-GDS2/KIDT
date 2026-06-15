using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using KIDT.Models;

namespace KIDT.Components.Pages;

public partial class Home // Kern: Chat-Zustand, Initialisierung, Nachrichtenversand
{
    [Parameter]
    public int ChatId { get; set; }

    private class ChatMessage // Repräsentiert eine einzelne Chat-Nachricht in der UI
    {
        public string Text { get; set; } = string.Empty;
        public bool IsUser { get; set; }
        public bool IsLoading { get; set; }
        public string DisplayText { get; set; } = string.Empty;
        public string LoadingText { get; set; } = string.Empty; // Konstantes Lade-Label (z.B. "KI denkt nach")
        public List<Document> FoundDocuments { get; set; } = new List<Document>();
        public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
        public string ModelLabel { get; set; } = string.Empty; // Debug: welches Modell hat geantwortet
    }

    private List<ChatMessage> Messages = new();
    private int currentConversationId;
    private bool isSaving = false;
    private ElementReference textareaRef;
    private ElementReference messagesRef;

    // --- Eingabe ---
    private const int MaxRows = 19; // Maximale Zeilen im Textarea
    private const int Cols = 60;
    private int rows = 1;

    private string inputText = string.Empty;
    private string InputText
    {
        get { return inputText; } // Aktuellen Text zurückgeben
        set
        {
            if (value == null) value = string.Empty; // Null-Wert auf leer normalisieren
            inputText = value;
            UpdateRows(); // Zeilenanzahl bei jeder Eingabe neu berechnen
        }
    }

    private CancellationTokenSource? cancelSource; // Token für Abbruch laufender AI-Anfragen
    private bool canAbort = false; // Abbrechen-Button nur sichtbar vor erstem Chunk

    protected override async Task OnInitializedAsync() // Wird beim Laden der Seite aufgerufen
    {
        var backgroundTask = Task.Run(Chat.InitializeAsync); // KI-Modell im Hintergrund laden

        KIDT.Services.ChatEventService.OnNewChatRequested += HandleNewChatRequest; // Neuer-Chat-Event registrieren

        if (ChatId > 0) // Bestehenden Chat laden
        {
            currentConversationId = ChatId;

            await Task.Yield(); // UI-Thread freigeben

            List<KIDT.Models.Message> dbMessages;
            List<KIDT.Models.Document> files;

            using (var scope1 = ServiceProvider.CreateScope()) // Separate Scopes für parallele DB-Zugriffe
            using (var scope2 = ServiceProvider.CreateScope())
            {
                var dbService1 = scope1.ServiceProvider.GetRequiredService<KIDT.Database.ChatDbService>();
                var docDbService = scope2.ServiceProvider.GetRequiredService<KIDT.Database.DocumentDbService>();

                var messagesTask = dbService1.LoadMessagesAsync(currentConversationId); // Nachrichten laden
                var filesTask = docDbService.GetDocumentsForConversationAsync(currentConversationId); // Dokumente laden

                await Task.WhenAll(messagesTask, filesTask); // Parallel warten

                dbMessages = await messagesTask;
                files = await filesTask;
            }

            foreach (KIDT.Models.Message dbMsg in dbMessages) // Nachrichten in UI-Objekte umwandeln
            {
                if (dbMsg.Text.StartsWith("[DocID:")) continue; // Interne Marker nicht anzeigen

                var chatMsg = new ChatMessage { Text = dbMsg.Text, IsUser = dbMsg.IsUser };

                if (!string.IsNullOrEmpty(dbMsg.DocumentIdsJson)) // Verknüpfte Dokumente laden
                {
                    try
                    {
                        var docIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(dbMsg.DocumentIdsJson); // JSON → DocID-Liste deserialisieren
                        if (docIds != null && docIds.Count > 0) // IDs vorhanden?
                        {
                            using var docScope = ServiceProvider.CreateScope();
                            var docDbService = docScope.ServiceProvider.GetRequiredService<KIDT.Database.DocumentDbService>();

                            foreach (var docId in docIds)
                            {
                                var doc = await docDbService.GetDocumentByIdAsync(docId); // Dokument per ID laden
                                if (doc != null) chatMsg.FoundDocuments.Add(doc); // Gefundenes Dokument hinzufügen
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LOAD_DOCS] Fehler beim Laden von Dokumenten: {ex.Message}");
                    }
                }

                if (!string.IsNullOrEmpty(dbMsg.EventIdsJson)) // Verknüpfte Events laden
                {
                    try
                    {
                        var eventIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(dbMsg.EventIdsJson); // JSON → EventID-Liste deserialisieren
                        if (eventIds != null && eventIds.Count > 0) // IDs vorhanden?
                        {
                            using var calScope = ServiceProvider.CreateScope();
                            var calendarService = calScope.ServiceProvider.GetRequiredService<KIDT.Services.CalendarService>();

                            foreach (var eventId in eventIds)
                            {
                                var evt = await calendarService.GetEventByIdAsync(eventId); // Termin per ID laden
                                if (evt != null) chatMsg.FoundEvents.Add(evt); // Gefundenen Termin hinzufügen
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LOAD_EVENTS] Fehler beim Laden von Events: {ex.Message}");
                    }
                }

                chatMsg.DisplayText = StripDocIdMarkers(dbMsg.Text); // DocID-Marker aus Anzeigetext entfernen
                Messages.Add(chatMsg);
            }

            if (files.Count > 0) // Ersten Dateinamen im Badge anzeigen
            {
                UploadedFileName = files[0].FileName;
            }

            StateHasChanged();
        }
        else // Neuer Chat: keine ID aus URL
        {
            currentConversationId = 0;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender) // JS-Hooks registrieren und Auto-Scroll
    {
        if (firstRender) // Einmalig beim ersten Render
        {
            var dotnetRef = DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync("textareaHelper.setupEnterHandler", "chatTextarea", dotnetRef); // Enter-Handler
            await JSRuntime.InvokeVoidAsync("chatScrollHelper.init", messagesRef); // Auto-Scroll init
        }

        await JSRuntime.InvokeVoidAsync("chatScrollHelper.scrollToBottom"); // Nach jedem Render scrollen
    }

    [JSInvokable]
    public async Task OnEnterPressed() // Wird von JavaScript aufgerufen wenn Enter gedrückt wird (ohne Shift)
    {
        if (isSaving) return; // Kein Doppel-Aufruf während Antwort läuft
        string textToSend = InputText;
        InputText = string.Empty; // Leert sofort und ruft UpdateRows auf
        await SendMessage(textToSend);
    }

    private async void HandleNewChatRequest() // Event-Handler für Neuer-Chat-Button in der Navigation
    {
        if (isSaving) return; // Warten bis aktuelle Anfrage abgeschlossen

        bool hasContent = Messages.Count > 0 || !string.IsNullOrEmpty(UploadedFileName); // Prüfe ob Chat Inhalt hat
        if (hasContent) await ResetChat();
    }

    public void Dispose() // Event-Listener beim Entfernen der Komponente deregistrieren
    {
        KIDT.Services.ChatEventService.OnNewChatRequested -= HandleNewChatRequest;
    }

    private void UpdateRows() // Berechnet sichtbare Zeilen anhand Eingabetext und Cols-Breite
    {
        if (string.IsNullOrEmpty(inputText)) // Leer: eine Zeile anzeigen
        {
            rows = 1;
            return;
        }

        var lines = inputText.Split('\n'); // Splitte bei Newlines
        int total = 0;
        foreach (var line in lines)
        {
            int wraps = (int)Math.Ceiling(line.Length / (double)Cols); // Zeilenumbrüche durch Breite
            if (wraps < 1) wraps = 1; // Mindestens eine Zeile pro Abschnitt
            total += wraps;
        }
        rows = Math.Clamp(total, 1, MaxRows); // Auf Bereich [1, 19] beschränken
    }

    private async Task TypewriterEffect(ChatMessage message, string fullText) // Typewriter-Effekt mit adaptiver Geschwindigkeit
    {
        message.DisplayText = "";
        int displayedLength = 0;

        while (displayedLength < fullText.Length) // Solange noch Text übrig
        {
            int remaining = fullText.Length - displayedLength; // Noch nicht angezeigte Zeichen
            int step = remaining > 80 ? 3 : 2; // Adaptiv: bei viel Text schneller
            displayedLength = Math.Min(displayedLength + step, fullText.Length); // Nächste Position berechnen
            message.DisplayText = fullText[..displayedLength]; // Slice bis aktueller Position
            StateHasChanged();
            await Task.Delay(22); // ~45fps
        }

        message.Text = fullText;
        StateHasChanged();
    }

    private async Task AbortMessage() // Aktuelle AI-Anfrage abbrechen
    {
        canAbort = false;
        StateHasChanged();
        if (cancelSource != null) cancelSource.Cancel(); // Abbruch-Signal senden
    }

    private string GetSendClass() // CSS-Klasse für Send/Abort-Button
    {
        if (isSaving && canAbort) return "chat-send aborting"; // Rot-Hover beim Abbrechen
        return "chat-send";
    }

    private string GetSendTitle() // Tooltip für Send/Abort-Button
    {
        if (isSaving) return "Abbrechen";
        return "Senden";
    }

    private string GetActionsClass() // CSS-Klassen für chat-actions Container
    {
        string cls = string.Empty;
        if (!string.IsNullOrEmpty(InputText) || isSaving) cls = "active"; // Aktiv wenn Text oder Senden läuft
        if (isSaving && !canAbort)
        {
            if (cls.Length > 0) cls += " ";
            cls += "no-abort"; // Streaming läuft: Abbruch nicht möglich
        }
        return cls;
    }

    private async Task OnSendOrAbort() // Unified Handler für Send/Abbruch-Button
    {
        if (!isSaving) // Bereit: Nachricht senden
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;
            string textToSend = InputText;
            InputText = string.Empty;
            await SendMessage(textToSend);
        }
        else if (canAbort) // Wartet auf LLM: Abbruch möglich
        {
            await AbortMessage();
        }
        // isSaving && !canAbort: Streaming läuft — kein Abbruch
    }

    private async Task SendMessage(string text) // Hauptmethode: Nachricht senden und Antwort streamen
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();

        isSaving = true;
        cancelSource = new CancellationTokenSource();

        Messages.Add(new ChatMessage { Text = text, DisplayText = text, IsUser = true }); // User-Nachricht sofort anzeigen
        StateHasChanged();
        await JSRuntime.InvokeVoidAsync("chatScrollHelper.forceScrollToBottom");

        var loadingMessage = new ChatMessage { Text = string.Empty, IsUser = false, IsLoading = true, LoadingText = "KI denkt nach" }; // Loading-Indikator mit konstantem Label
        Messages.Add(loadingMessage);
        canAbort = true;
        StateHasChanged();

        int conversationIdForSave = currentConversationId;

        if (currentConversationId == 0) // Neue Conversation anlegen wenn noch keine existiert
        {
            currentConversationId = await Db.CreateConversationAsync("Neuer Chat");
            conversationIdForSave = currentConversationId;
        }

        var saveUserMessageTask = Task.Run(async () => // User-Message parallel in DB speichern
        {
            await Db.SaveMessageAsync(conversationIdForSave, true, text);
            await Db.UpdateConversationTitleAsync(conversationIdForSave);
        });

        var minDelayTask = Task.Delay(1600); // Loading bleibt mindestens 1.6s sichtbar

        var assistantMessage = new ChatMessage { Text = "", DisplayText = "", IsUser = false, IsLoading = false };

        string fullMessage = string.Empty;
        int displayedLength = 0;
        bool streamComplete = false;
        List<Document> foundDocuments = new List<Document>();
        List<CalendarEvent> foundEvents = new List<CalendarEvent>();
        bool firstChunkReceived = false;

        var drainTask = Task.Run(async () => // Typewriter-Drain: zeigt Puffer-Inhalt bei ~45fps an
        {
            while (!streamComplete || displayedLength < fullMessage.Length)
            {
                await InvokeAsync(() =>
                {
                    int total = fullMessage.Length;
                    if (displayedLength < total)
                    {
                        int lag = total - displayedLength;
                        int step = lag > 80 ? 3 : 2; // Adaptiv: bei viel Text schneller
                        displayedLength = Math.Min(displayedLength + step, total);
                        assistantMessage.DisplayText = fullMessage[..displayedLength];
                        StateHasChanged();
                    }
                });
                await Task.Delay(22);
            }
            await InvokeAsync(() => { assistantMessage.DisplayText = fullMessage; StateHasChanged(); }); // Sicherstellung: alles angezeigt
        });

        bool wasCancelled = false;
        bool hadError = false;

        try
        {
            await foreach (var chunk in Chat.SendStreamAsync(text, conversationIdForSave, false, cancelSource.Token).WithCancellation(cancelSource.Token))
            {
                if (!firstChunkReceived) // Erster Chunk: Loading-Nachricht durch Assistent-Nachricht ersetzen
                {
                    firstChunkReceived = true;
                    canAbort = false;
                    await minDelayTask; // MinDelay abwarten
                    Messages.Remove(loadingMessage);
                    Messages.Add(assistantMessage);
                    StateHasChanged();
                }

                if (!string.IsNullOrEmpty(chunk.TextChunk)) fullMessage += chunk.TextChunk; // In Puffer schreiben

                if (chunk.IsComplete) // Stream abgeschlossen
                {
                    foundDocuments = chunk.FoundDocuments;
                    foundEvents = chunk.FoundEvents;
                    assistantMessage.FoundDocuments = foundDocuments;
                    assistantMessage.FoundEvents = foundEvents;
                    assistantMessage.ModelLabel = chunk.ModelLabel;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        catch (Exception ex)
        {
            hadError = true;
            System.Diagnostics.Debug.WriteLine($"[CHAT] Fehler: {ex.Message}");
        }

        streamComplete = true; // Drain-Task beenden
        await Task.WhenAll(drainTask, saveUserMessageTask);

        if (wasCancelled) // Abbruch: UI aufräumen
        {
            Messages.Remove(loadingMessage);
            Messages.Remove(assistantMessage);
            cancelSource.Dispose();
            cancelSource = new CancellationTokenSource();
            isSaving = false;
            canAbort = false;
            StateHasChanged();
            return;
        }

        if (hadError) // Fehler: Fehlermeldung anzeigen
        {
            Messages.Remove(loadingMessage);
            Messages.Remove(assistantMessage);
            var errMsg = new ChatMessage();
            errMsg.Text = "Ein Fehler ist aufgetreten. Bitte erneut versuchen.";
            errMsg.DisplayText = errMsg.Text;
            errMsg.IsUser = false;
            Messages.Add(errMsg);
            isSaving = false;
            canAbort = false;
            StateHasChanged();
            return;
        }

        assistantMessage.Text = fullMessage;
        assistantMessage.DisplayText = StripDocIdMarkers(fullMessage); // DocID-Marker aus Anzeigetext entfernen
        StateHasChanged();

        await Task.Run(async () => // Assistent-Antwort mit DocIDs und EventIDs in DB speichern
        {
            var docIds = new List<int>();
            foreach (var d in foundDocuments) docIds.Add(d.Id);

            var eventIds = new List<int>();
            foreach (var e in foundEvents) eventIds.Add(e.Id);

            await Db.SaveMessageAsync(conversationIdForSave, false, fullMessage, docIds, eventIds);
        });

        cancelSource.Dispose();
        cancelSource = new CancellationTokenSource(); // Token für nächste Anfrage bereitstellen
        isSaving = false;
        StateHasChanged();
    }

    private static string StripDocIdMarkers(string text) // Entfernt [DocID: x,y]-Marker aus Anzeigetext (intern für RouterService benötigt, nicht für User sichtbar)
    {
        return Regex.Replace(text, @"\s*\[DocID:[^\]]*\]", "").TrimEnd(); // Muster: optionales Leerzeichen + [DocID:...] entfernen
    }

    private async Task ResetChat() // Setzt Chat vollständig zurück und navigiert zur Startseite
    {
        Messages.Clear();
        InputText = string.Empty;
        rows = 1;
        UploadedFileName = string.Empty;
        Chat.ClearFile();
        currentConversationId = 0;
        Navigation.NavigateTo("/", forceLoad: true);
        StateHasChanged();
        await Task.CompletedTask;
    }
}
