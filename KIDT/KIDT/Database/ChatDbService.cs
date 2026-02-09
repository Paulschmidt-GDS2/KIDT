using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class ChatDbService // Service für Datenbank-Zugriff
{
    private readonly ChatDbContext db;

    public ChatDbService(ChatDbContext dbContext) // Konstruktor: Wird beim Erstellen der Klasse aufgerufen
    {
        this.db = dbContext; // Context per Dependency Injection erhalten
    }

    public async Task<int> CreateConversationAsync(string title) // Neuen Chat erstellen
    {
        Conversation conversation = new Conversation(); // Erstelle neues Conversation-Objekt
        conversation.Title = title;
        conversation.CreatedAt = DateTime.UtcNow;

        this.db.Conversations.Add(conversation); // Füge Conversation zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank

        return conversation.Id;
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text) // Nachricht speichern
    {
        await SaveMessageAsync(conversationId, isUser, text, null); // Rufe Überladung ohne DocumentIds auf
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text, List<int>? documentIds) // Nachricht speichern (mit optionalen Dokument-IDs)
    {
        Message message = new Message(); // Neue Nachricht
        message.ConversationId = conversationId; // Zu welchem Chat?
        message.IsUser = isUser; // User oder Assistant?
        message.Text = text; // Nachrichtentext
        message.Timestamp = DateTime.UtcNow; // Aktueller Zeitstempel
        
        // Nur setzen wenn Spalte existiert (Fallback für fehlende Migration)
        try
        {
            if (documentIds != null && documentIds.Count > 0) // Dokument-IDs vorhanden?
            {
                message.DocumentIdsJson = System.Text.Json.JsonSerializer.Serialize(documentIds); // Serialisiere zu JSON
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB] DocumentIdsJson nicht verfügbar (Migration fehlt?): {ex.Message}");
        }

        this.db.Messages.Add(message); // Füge zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere in Datenbank
    }

    public async Task<List<Message>> LoadMessagesAsync(int conversationId) // Nachrichten laden
    {
        var query = this.db.Messages.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        
        var filtered = new List<Message>(); // Erstelle leere Liste für Ergebnis
        var allMessages = await query.ToListAsync(); // Lade alle Nachrichten aus Datenbank
        
        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId) // Gehört Nachricht zu diesem Chat?
            {
                filtered.Add(m); // Füge zur gefilterten Liste hinzu
            }
        }
        
        filtered.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp)); // Sortiere nach Zeitstempel
        return filtered;
    }

    public async Task<string> GetFullChatHistoryAsync(int conversationId) // Hole kompletten Chat-Verlauf als Text
    {
        List<Message> allMessages = await LoadMessagesAsync(conversationId); // Lade alle Nachrichten

        if (allMessages.Count == 0) // Keine Nachrichten vorhanden?
        {
            return string.Empty;
        }

        List<string> contextLines = new List<string>(); // Erstelle Liste für formatierte Zeilen

        foreach (Message msg in allMessages) // Gehe durch alle Nachrichten
        {
            string role = "Assistant";
            if (msg.IsUser) // Ist es eine User-Nachricht?
            {
                role = "User";
            }

            contextLines.Add($"{role}: {msg.Text}"); // Füge formatierte Zeile zur Liste hinzu
        }

        return string.Join("\n", contextLines); // Verbinde alle Zeilen mit Zeilenumbruch
    }

    public async Task<List<Conversation>> LoadAllConversationsAsync() // Lade alle Conversations
    {
        var allConversations = await this.db.Conversations
            .AsNoTracking()
            .ToListAsync(); // Lade alle Conversations aus Datenbank (OHNE Include!)
        
        foreach (Conversation c in allConversations) // Gehe durch alle Conversations
        {
            // Lade verknüpfte Dokumente via ConversationDocuments (getrennte Query!)
            var conversationDocs = await this.db.ConversationDocuments
                .AsNoTracking()
                .Where(cd => cd.ConversationId == c.Id)
                .ToListAsync(); // Lade alle Verknüpfungen für diese Conversation
            
            c.LinkedDocuments = new List<Document>(); // Initialisiere Liste
            
            foreach (var cd in conversationDocs) // Gehe durch alle Verknüpfungen
            {
                var doc = await this.db.Documents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == cd.DocumentId); // Lade Dokument per expliziter Query
                    
                if (doc != null) // Dokument gefunden?
                {
                    c.LinkedDocuments.Add(doc); // Füge zur Liste hinzu
                }
            }
        }
        
        allConversations.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt)); // Sortiere nach Datum (neueste zuerst)
        return allConversations; // Gib sortierte Liste zurück
    }

    public async Task UpdateConversationTitleAsync(int conversationId) // Aktualisiere Chat-Titel
    {
        var allMessages = await this.db.Messages.ToListAsync(); // Lade alle Nachrichten aus Datenbank
        
        var userMessages = new List<Message>(); // Erstelle leere Liste für User-Nachrichten
        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId && m.IsUser) // Gehört zu diesem Chat und ist User-Nachricht?
            {
                userMessages.Add(m);
            }
        }
        
        userMessages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp)); // Sortiere nach Zeitstempel
        
        Message? firstUserMessage = null; // Variable für erste Nachricht
        if (userMessages.Count > 0) // Gibt es User-Nachrichten?
        {
            firstUserMessage = userMessages[0]; // Nimm erste Nachricht
        }
        
        if (firstUserMessage != null) // User-Nachricht gefunden?
        {
            string title = firstUserMessage.Text; // Nimm Nachricht als Titel
            
            if (title.Length > 50) // Titel zu lang?
            {
                title = title.Substring(0, 47) + "..."; // Kürze auf 50 Zeichen
            }
            
            Conversation? conv = await this.db.Conversations.FindAsync(conversationId); // Suche Conversation
            
            if (conv != null) // Conversation gefunden?
            {
                conv.Title = title;
                await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank
            }
        }
    }

    public async Task DeleteConversationAsync(int conversationId) // Lösche Conversation mit allen Daten
    {
        var allMessages = await this.db.Messages.ToListAsync(); // Lade alle Nachrichten aus Datenbank
        var messages = new List<Message>(); // Erstelle leere Liste für zu löschende Nachrichten
        foreach (Message m in allMessages) // Gehe durch alle Nachrichten
        {
            if (m.ConversationId == conversationId) // Gehört Nachricht zu diesem Chat?
            {
                messages.Add(m);
            }
        }
        
        this.db.Messages.RemoveRange(messages); // Lösche alle Nachrichten
        
        // Lösche ConversationDocuments-Verknüpfungen (Documents selbst bleiben erhalten!)
        var conversationDocs = await this.db.ConversationDocuments
            .Where(cd => cd.ConversationId == conversationId)
            .ToListAsync(); // Lade alle Verknüpfungen
        
        this.db.ConversationDocuments.RemoveRange(conversationDocs); // Lösche Verknüpfungen
        
        Conversation? conv = await this.db.Conversations.FindAsync(conversationId); // Suche Conversation
        
        if (conv != null) // Conversation gefunden?
        {
            this.db.Conversations.Remove(conv);
        }
        
        await this.db.SaveChangesAsync(); // Speichere alle Änderungen in Datenbank
    }
}