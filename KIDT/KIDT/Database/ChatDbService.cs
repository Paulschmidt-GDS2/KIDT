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

    public async Task SaveUploadedFileAsync(int conversationId, string fileName, string extractedText, string thumbnailBase64) // Speichere hochgeladene Datei
    {
        var allFiles = await this.db.UploadedFiles.ToListAsync(); // Lade alle hochgeladenen Dateien
        
        bool fileExists = false; // Standardmäßig: Datei existiert nicht
        foreach (UploadedFile f in allFiles) // Gehe durch alle Dateien
        {
            if (f.ConversationId == conversationId && f.FileName == fileName) // Gleiche Conversation und gleicher Name?
            {
                fileExists = true; // Datei existiert bereits
                break;
            }
        }
        
        if (!fileExists) // Datei existiert noch nicht?
        {
            UploadedFile newFile = new UploadedFile(); // Erstelle neues UploadedFile-Objekt
            newFile.ConversationId = conversationId; // Setze Conversation-ID
            newFile.FileName = fileName; // Setze Dateiname
            newFile.ExtractedText = extractedText; // Setze extrahierten Text
            newFile.ThumbnailBase64 = thumbnailBase64; // Setze Thumbnail
            newFile.UploadedAt = DateTime.UtcNow; // Setze aktuelles Datum (UTC)
            
            this.db.UploadedFiles.Add(newFile); // Füge Datei zur Datenbank hinzu
            await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank
        }
    }

    public async Task<List<Conversation>> LoadAllConversationsAsync() // Lade alle Conversations
    {
        var query = this.db.Conversations.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allConversations = await query.ToListAsync(); // Lade alle Conversations aus Datenbank
        
        foreach (Conversation c in allConversations) // Gehe durch alle Conversations
        {
            var files = await this.db.UploadedFiles.Where(f => f.ConversationId == c.Id).ToListAsync(); // Lade Dateien für diese Conversation
            c.UploadedFiles = files; // Setze verknüpfte Dateien
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

    public async Task<List<UploadedFile>> LoadFilesForConversationAsync(int conversationId) // Lade Dateien für einen Chat
    {
        var query = this.db.UploadedFiles.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allFiles = await query.ToListAsync(); // Lade alle Dateien aus Datenbank
        
        var filtered = new List<UploadedFile>(); // Erstelle leere Liste für Ergebnis
        foreach (UploadedFile f in allFiles) // Gehe durch alle Dateien
        {
            if (f.ConversationId == conversationId) // Gehört Datei zu diesem Chat?
            {
                filtered.Add(f);
            }
        }
        
        return filtered;
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
        
        var allFiles = await this.db.UploadedFiles.ToListAsync(); // Lade alle Dateien aus Datenbank
        var files = new List<UploadedFile>(); // Erstelle leere Liste für zu löschende Dateien
        foreach (UploadedFile f in allFiles) // Gehe durch alle Dateien
        {
            if (f.ConversationId == conversationId) // Gehört Datei zu diesem Chat?
            {
                files.Add(f);
            }
        }
        
        this.db.UploadedFiles.RemoveRange(files); // Lösche alle Dateien
        
        Conversation? conv = await this.db.Conversations.FindAsync(conversationId); // Suche Conversation
        
        if (conv != null) // Conversation gefunden?
        {
            this.db.Conversations.Remove(conv);
        }
        
        await this.db.SaveChangesAsync(); // Speichere alle Änderungen in Datenbank
    }
}