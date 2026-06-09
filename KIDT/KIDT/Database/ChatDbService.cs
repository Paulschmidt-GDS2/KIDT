using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class ChatDbService // Service für Datenbank-Zugriff
{
    private readonly ChatDbContext db;

    public ChatDbService(ChatDbContext dbContext) // Konstruktor: DB-Context per Dependency Injection
    {
        this.db = dbContext; // Context per Dependency Injection erhalten
    }

    public async Task<int> CreateConversationAsync(string title) // Neuen Chat erstellen
    {
        Conversation conversation = new Conversation();
        string safeTitle = string.Empty;
        if (title != null) // Prüfe ob Title vorhanden
        {
            safeTitle = title;
        }
        conversation.Title = safeTitle;
        conversation.CreatedAt = DateTime.UtcNow;

        this.db.Conversations.Add(conversation); // Füge Conversation zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank

        return conversation.Id;
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text) // Nachricht speichern
    {
        List<int> emptyList = new List<int>();
        await SaveMessageAsync(conversationId, isUser, text, emptyList); // Rufe Überladung ohne DocumentIds auf
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text, List<int> documentIds) // Nachricht speichern (mit optionalen Dokument-IDs)
    {
        List<int> emptyEventIds = new List<int>();
        await SaveMessageAsync(conversationId, isUser, text, documentIds, emptyEventIds); // Rufe Überladung mit leeren Event-IDs auf
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text, List<int> documentIds, List<int> eventIds) // Nachricht speichern (mit Dokument-IDs und Event-IDs)
    {
        Message message = new Message();
        message.ConversationId = conversationId; // Zu welchem Chat?
        message.IsUser = isUser; // User oder Assistant?
        string safeText = string.Empty;
        if (text != null) // Prüfe ob Text vorhanden
        {
            safeText = text;
        }
        message.Text = safeText; // Nachrichtentext
        message.Timestamp = DateTime.UtcNow; // Aktueller Zeitstempel

        try // Fallback: Spalten optional setzen (falls Migration fehlt)
        {
            if (documentIds != null && documentIds.Count > 0) // Dokument-IDs vorhanden?
            {
                message.DocumentIdsJson = System.Text.Json.JsonSerializer.Serialize(documentIds); // Serialisiere zu JSON
            }

            if (eventIds != null && eventIds.Count > 0) // Event-IDs vorhanden?
            {
                message.EventIdsJson = System.Text.Json.JsonSerializer.Serialize(eventIds); // Serialisiere zu JSON
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB] DocumentIdsJson/EventIdsJson nicht verfügbar (Migration fehlt?): {ex.Message}");
        }

        this.db.Messages.Add(message); // Füge zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere in Datenbank
    }

    public async Task<List<Message>> LoadMessagesAsync(int conversationId) // Nachrichten laden
    {
        var query = this.db.Messages.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung

        var filtered = new List<Message>();
        var allMessages = await query.ToListAsync(); // Lade alle Nachrichten aus Datenbank

        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId) // Gehört Nachricht zu diesem Chat?
            {
                filtered.Add(m); // Füge zur gefilterten Liste hinzu
            }
        }

        filtered.Sort(CompareMessagesByTimestampAsc); // Sortiere nach Zeitstempel aufsteigend
        return filtered;
    }

    public async Task<string> GetFullChatHistoryAsync(int conversationId) // Hole kompletten Chat-Verlauf als Text
    {
        List<Message> allMessages = await LoadMessagesAsync(conversationId); // Lade alle Nachrichten

        if (allMessages.Count == 0) // Keine Nachrichten vorhanden?
        {
            return string.Empty;
        }

        List<string> contextLines = new List<string>();

        foreach (Message msg in allMessages) // Gehe durch alle Nachrichten
        {
            string role = "Assistant";
            if (msg.IsUser) // Ist es eine User-Nachricht?
            {
                role = "User";
            }

            string msgText = string.Empty;
            if (msg.Text != null) // Prüfe ob Text vorhanden
            {
                msgText = msg.Text;
            }

            contextLines.Add($"{role}: {msgText}"); // Füge formatierte Zeile zur Liste hinzu
        }

        return string.Join("\n", contextLines); // Verbinde alle Zeilen mit Zeilenumbruch
    }

    public async Task<List<Conversation>> LoadAllConversationsAsync() // Lade alle Conversations
    {
        var allConversations = await this.db.Conversations
            .AsNoTracking()
            .ToListAsync(); // Lade alle Conversations aus Datenbank (OHNE Include!)

        var allConversationDocs = await this.db.ConversationDocuments
            .AsNoTracking()
            .ToListAsync(); // Einmalig laden (verhindert N+1-Abfragen)

        var allDocuments = await this.db.Documents
            .AsNoTracking()
            .ToListAsync(); // Einmalig laden (verhindert N+1-Abfragen)

        foreach (Conversation c in allConversations) // Gehe durch alle Conversations
        {
            c.LinkedDocuments = new List<Document>();

            foreach (ConversationDocument cd in allConversationDocs) // Gehe durch alle ConversationDocuments
            {
                if (cd.ConversationId == c.Id) // Gehört zu dieser Conversation?
                {
                    foreach (Document d in allDocuments) // Suche passendes Dokument im Memory
                    {
                        if (d.Id == cd.DocumentId) // ID stimmt überein?
                        {
                            c.LinkedDocuments.Add(d); // Füge zur Liste hinzu
                            break;
                        }
                    }
                }
            }
        }

        allConversations.Sort(CompareConversationsByCreatedAtDesc); // Sortiere nach Datum (neueste zuerst)
        return allConversations;
    }

    public async Task UpdateConversationTitleAsync(int conversationId) // Aktualisiere Chat-Titel
    {
        var allMessages = await this.db.Messages.ToListAsync(); // Lade alle Nachrichten aus Datenbank

        var userMessages = new List<Message>();
        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId && m.IsUser) // Gehört zu diesem Chat und ist User-Nachricht?
            {
                userMessages.Add(m);
            }
        }

        userMessages.Sort(CompareMessagesByTimestampAsc); // Sortiere nach Zeitstempel aufsteigend

        Message firstUserMessage = new Message();
        bool hasFirstMessage = false;
        if (userMessages.Count > 0) // Gibt es User-Nachrichten?
        {
            firstUserMessage = userMessages[0]; // Nimm erste Nachricht
            hasFirstMessage = true;
        }

        if (hasFirstMessage) // User-Nachricht gefunden?
        {
            string title = string.Empty;
            if (firstUserMessage.Text != null) // Prüfe ob Text vorhanden
            {
                title = firstUserMessage.Text; // Nimm Nachricht als Titel
            }

            if (title.Length > 50) // Titel zu lang?
            {
                title = title.Substring(0, 47) + "..."; // Kürze auf 50 Zeichen
            }

            var allConversations = await this.db.Conversations.ToListAsync(); // Alle Conversations laden
            Conversation conv = new Conversation();
            bool convFound = false;
            foreach (Conversation c in allConversations) // Gesuchte Conversation finden
            {
                if (c.Id == conversationId)
                {
                    conv = c;
                    convFound = true;
                    break;
                }
            }

            if (convFound) // Conversation gefunden?
            {
                conv.Title = title;
                await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank
            }
        }
    }

    public async Task DeleteConversationAsync(int conversationId) // Lösche Conversation mit allen Daten
    {
        var allMessages = await this.db.Messages.ToListAsync(); // Lade alle Nachrichten aus Datenbank
        var messages = new List<Message>();
        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId) // Gehört Nachricht zu diesem Chat?
            {
                messages.Add(m);
            }
        }

        this.db.Messages.RemoveRange(messages); // Lösche alle Nachrichten

        var allConversationDocs = await this.db.ConversationDocuments.ToListAsync(); // ConversationDocuments laden (Documents selbst bleiben erhalten!)
        var conversationDocs = new List<ConversationDocument>();
        foreach (var cd in allConversationDocs) // Gehe durch alle ConversationDocuments
        {
            if (cd.ConversationId == conversationId) // Gehört zu diesem Chat?
            {
                conversationDocs.Add(cd); // Füge hinzu
            }
        }

        this.db.ConversationDocuments.RemoveRange(conversationDocs); // Lösche Verknüpfungen

        var allConversations = await this.db.Conversations.ToListAsync(); // Alle Conversations laden
        Conversation conv = new Conversation();
        bool convFound = false;
        foreach (Conversation c in allConversations) // Gesuchte Conversation finden
        {
            if (c.Id == conversationId)
            {
                conv = c;
                convFound = true;
                break;
            }
        }

        if (convFound) // Conversation gefunden?
        {
            this.db.Conversations.Remove(conv);
        }

        await this.db.SaveChangesAsync(); // Speichere alle Änderungen in Datenbank
    }

    public async Task CleanDocIdMarkersFromMessagesAsync() // Bereinigt alle [DocID:]-Marker aus bestehenden DB-Nachrichten
    {
        var allMessages = await this.db.Messages.ToListAsync(); // Alle Nachrichten laden
        bool hasChanges = false; // Marker: ob überhaupt etwas geändert wurde

        foreach (Message msg in allMessages) // Jede Nachricht prüfen
        {
            if (msg.Text == null) continue; // Leere Nachricht überspringen

            if (msg.Text.TrimStart().StartsWith("[DocID:")) // Standalone-Marker: Nachricht vollständig löschen
            {
                this.db.Messages.Remove(msg); // Internen Marker-Eintrag entfernen
                hasChanges = true;
                continue;
            }

            if (msg.Text.Contains("[DocID:")) // Eingebetteter Marker: aus Nachrichtentext herausschneiden
            {
                string cleaned = System.Text.RegularExpressions.Regex.Replace(msg.Text, @"\s*\[DocID:[^\]]*\]", "").TrimEnd(); // Marker-Muster entfernen
                if (cleaned != msg.Text) // Nur speichern wenn sich Text geändert hat
                {
                    msg.Text = cleaned;
                    hasChanges = true;
                }
            }
        }

        if (hasChanges) // Nur speichern wenn etwas geändert wurde
        {
            await this.db.SaveChangesAsync(); // Alle Änderungen in einem Durchgang speichern
        }
    }

    private static int CompareMessagesByTimestampAsc(Message a, Message b) // Vergleich: älteste Nachricht zuerst
    {
        return a.Timestamp.CompareTo(b.Timestamp);
    }

    private static int CompareConversationsByCreatedAtDesc(Conversation a, Conversation b) // Vergleich: neueste Conversation zuerst
    {
        return b.CreatedAt.CompareTo(a.CreatedAt);
    }
}
