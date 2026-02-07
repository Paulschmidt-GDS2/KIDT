using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Database;

public class ChatDbService // Service für Datenbank-Zugriff
{
    private ChatDbContext db; // Datenbank-Context

    public ChatDbService() // Konstruktor: Wird beim Erstellen der Klasse aufgerufen
    {
        this.db = new ChatDbContext(); // Erstelle neuen DB-Context
        this.db.Database.EnsureCreated(); // Erstelle Tabellen wenn nicht vorhanden (automatisch!)
    }

    public async Task<int> CreateConversationAsync(string title) // Neuen Chat erstellen
    {
        Conversation conversation = new Conversation(); // Neuer Chat
        conversation.Title = title; // Setze Titel
        conversation.CreatedAt = DateTime.UtcNow; // Setze Erstellungsdatum

        this.db.Conversations.Add(conversation); // Füge zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere in Datenbank

        return conversation.Id; // Gib ID zurück (wurde automatisch von DB gesetzt)
    }

    public async Task SaveMessageAsync(int conversationId, bool isUser, string text) // Nachricht speichern
    {
        Message message = new Message(); // Neue Nachricht
        message.ConversationId = conversationId; // Zu welchem Chat?
        message.IsUser = isUser; // User oder Assistant?
        message.Text = text; // Nachrichtentext
        message.Timestamp = DateTime.UtcNow; // Aktueller Zeitstempel

        this.db.Messages.Add(message); // Füge zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere in Datenbank
    }

    public Task<List<Message>> LoadMessagesAsync(int conversationId) // Nachrichten laden
    {
        List<Message> allMessages = this.db.Messages.ToList(); // Hole alle Messages aus DB
        List<Message> filteredMessages = new List<Message>(); // Leere Liste für gefilterte Messages

        foreach (Message msg in allMessages) // Durchlaufe alle Messages
        {
            if (msg.ConversationId == conversationId) // Gehört Message zu diesem Chat?
            {
                filteredMessages.Add(msg); // Ja -> Füge hinzu
            }
        }

        return Task.FromResult(filteredMessages); // Gib gefilterte Liste zurück
    }

    public async Task<string> GetFullChatHistoryAsync(int conversationId) // Hole GESAMTEN aktuellen Chat-Verlauf als String
    {
        List<Message> allMessages = await LoadMessagesAsync(conversationId); // Lade alle Nachrichten für diesen Chat

        if (allMessages.Count == 0) // Keine Nachrichten?
        {
            return string.Empty; // Gib leer zurück
        }

        List<string> contextLines = new List<string>(); // Liste für formatierte Zeilen

        foreach (Message msg in allMessages) // Durchlaufe alle Nachrichten
        {
            string role = "Assistant"; // Standard: Assistant
            if (msg.IsUser) // Ist User-Nachricht?
            {
                role = "User"; // Ja -> User
            }

            contextLines.Add($"{role}: {msg.Text}"); // Füge formatierte Zeile hinzu
        }

        return string.Join("\n", contextLines); // Gib kompletten Chat-Verlauf zurück
    }

    public async Task SaveUploadedFileAsync(int conversationId, string fileName, string extractedText, string thumbnailBase64) // Datei speichern (nur wenn neu)
    {
        List<UploadedFile> allFiles = this.db.UploadedFiles.ToList(); // Hole alle Files aus DB
        bool fileExists = false; // Flag ob Datei schon existiert
        
        foreach (UploadedFile file in allFiles) // Durchlaufe alle Files
        {
            if (file.ConversationId == conversationId && file.FileName == fileName) // Gleiche Conversation und gleicher Name?
            {
                fileExists = true; // Ja -> Datei existiert bereits
                break; // Schleife abbrechen
            }
        }
        
        if (!fileExists) // Nur speichern wenn neu
        {
            UploadedFile newFile = new UploadedFile(); // Neue Datei erstellen
            newFile.ConversationId = conversationId; // Zu welchem Chat?
            newFile.FileName = fileName; // Dateiname setzen
            newFile.ExtractedText = extractedText; // Extrahierten Text setzen
            newFile.ThumbnailBase64 = thumbnailBase64; // Thumbnail setzen
            newFile.UploadedAt = DateTime.UtcNow; // Aktueller Zeitstempel
            
            this.db.UploadedFiles.Add(newFile); // Füge zur Datenbank hinzu
            await this.db.SaveChangesAsync(); // Speichere in Datenbank
        }
    }

    public Task<List<Conversation>> LoadAllConversationsAsync() // Alle Conversations mit Files laden
    {
        List<Conversation> allConversations = this.db.Conversations.ToList(); // Hole alle Conversations aus DB
        
        foreach (Conversation conv in allConversations) // Für jede Conversation die Files laden
        {
            List<UploadedFile> files = new List<UploadedFile>(); // Leere Liste für Files
            
            foreach (UploadedFile file in this.db.UploadedFiles.ToList()) // Durchlaufe alle Files
            {
                if (file.ConversationId == conv.Id) // Gehört File zu diesem Chat?
                {
                    files.Add(file); // Ja -> Füge hinzu
                }
            }
            
            conv.UploadedFiles = files; // Setze Files für diese Conversation
        }
        
        return Task.FromResult(allConversations); // Gib alle Conversations zurück
    }

    public async Task UpdateConversationTitleAsync(int conversationId) // Chat-Titel aus erster Nachricht generieren
    {
        List<Message> messages = await LoadMessagesAsync(conversationId); // Lade alle Nachrichten
        
        if (messages.Count > 0) // Gibt es Nachrichten?
        {
            Message firstUserMessage = null; // Variable für erste User-Nachricht
            
            foreach (Message msg in messages) // Durchlaufe alle Nachrichten
            {
                if (msg.IsUser) // Ist es eine User-Nachricht?
                {
                    firstUserMessage = msg; // Ja -> Speichere diese
                    break; // Schleife abbrechen
                }
            }
            
            if (firstUserMessage != null) // Wurde User-Nachricht gefunden?
            {
                string title = firstUserMessage.Text; // Nimm Text als Titel
                
                if (title.Length > 50) // Ist Titel zu lang?
                {
                    title = title.Substring(0, 47) + "..."; // Kürze auf 50 Zeichen mit ...
                }
                
                Conversation conv = this.db.Conversations.Find(conversationId); // Finde Conversation in DB
                
                if (conv != null) // Wurde Conversation gefunden?
                {
                    conv.Title = title; // Setze neuen Titel
                    await this.db.SaveChangesAsync(); // Speichere in Datenbank
                }
            }
        }
    }

    public Task<List<UploadedFile>> LoadFilesForConversationAsync(int conversationId) // Files für einen Chat laden
    {
        List<UploadedFile> allFiles = this.db.UploadedFiles.ToList(); // Hole alle Files aus DB
        List<UploadedFile> filteredFiles = new List<UploadedFile>(); // Leere Liste für gefilterte Files
        
        foreach (UploadedFile file in allFiles) // Durchlaufe alle Files
        {
            if (file.ConversationId == conversationId) // Gehört File zu diesem Chat?
            {
                filteredFiles.Add(file); // Ja -> Füge hinzu
            }
        }
        
        return Task.FromResult(filteredFiles); // Gib gefilterte Liste zurück
    }

    public async Task DeleteConversationAsync(int conversationId) // Conversation mit allen Messages und Files löschen
    {
        List<Message> messages = await LoadMessagesAsync(conversationId); // Lade alle Nachrichten
        
        foreach (Message msg in messages) // Durchlaufe alle Nachrichten
        {
            this.db.Messages.Remove(msg); // Lösche jede Nachricht
        }
        
        List<UploadedFile> files = await LoadFilesForConversationAsync(conversationId); // Lade alle Files
        
        foreach (UploadedFile file in files) // Durchlaufe alle Files
        {
            this.db.UploadedFiles.Remove(file); // Lösche jedes File
        }
        
        Conversation conv = this.db.Conversations.Find(conversationId); // Finde Conversation
        
        if (conv != null) // Wurde Conversation gefunden?
        {
            this.db.Conversations.Remove(conv); // Ja -> Lösche Conversation
        }
        
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank
    }
}