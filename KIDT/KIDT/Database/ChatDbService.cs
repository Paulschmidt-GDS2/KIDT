using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class ChatDbService // Service für Datenbank-Zugriff
{
    private readonly ChatDbContext db; // Datenbank-Context

    public ChatDbService(ChatDbContext dbContext) // Konstruktor: Wird beim Erstellen der Klasse aufgerufen
    {
        this.db = dbContext; // Context per Dependency Injection erhalten
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

    public async Task<List<Message>> LoadMessagesAsync(int conversationId) // Nachrichten laden
    {
        return await this.db.Messages // Hole nur Messages für diesen Chat (Filter in DB, nicht clientseitig)
            .AsNoTracking() // Keine Change-Tracking (schneller + verhindert Concurrency-Probleme)
            .Where(m => m.ConversationId == conversationId) // Filtere nach ConversationId
            .OrderBy(m => m.Timestamp) // Sortiere nach Zeitstempel
            .ToListAsync(); // Führe Query aus und gib Liste zurück
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
        bool fileExists = await this.db.UploadedFiles // Prüfe ob File bereits existiert (in DB, nicht clientseitig)
            .AnyAsync(f => f.ConversationId == conversationId && f.FileName == fileName); // Gleiche Conversation und gleicher Name?
        
        if (!fileExists) // Nur speichern wenn neu
        {
            UploadedFile newFile = new UploadedFile // Neue Datei erstellen
            {
                ConversationId = conversationId, // Zu welchem Chat?
                FileName = fileName, // Dateiname setzen
                ExtractedText = extractedText, // Extrahierten Text setzen
                ThumbnailBase64 = thumbnailBase64, // Thumbnail setzen
                UploadedAt = DateTime.UtcNow // Aktueller Zeitstempel
            };
            
            this.db.UploadedFiles.Add(newFile); // Füge zur Datenbank hinzu
            await this.db.SaveChangesAsync(); // Speichere in Datenbank
        }
    }

    public async Task<List<Conversation>> LoadAllConversationsAsync() // Alle Conversations mit Thumbnails laden
    {
        return await this.db.Conversations // Lade alle Conversations
            .AsNoTracking() // Keine Change-Tracking (schneller)
            .Include(c => c.UploadedFiles) // Lade Files mit Thumbnails
            .OrderByDescending(c => c.CreatedAt) // Sortiere nach Erstellungsdatum (neueste zuerst)
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    public async Task UpdateConversationTitleAsync(int conversationId) // Chat-Titel aus erster Nachricht generieren
    {
        Message firstUserMessage = await this.db.Messages // Finde erste User-Nachricht (direkt in DB)
            .Where(m => m.ConversationId == conversationId && m.IsUser) // Nur User-Nachrichten in diesem Chat
            .OrderBy(m => m.Timestamp) // Sortiere nach Zeitstempel
            .FirstOrDefaultAsync(); // Hole erste oder null
        
        if (firstUserMessage != null) // Wurde User-Nachricht gefunden?
        {
            string title = firstUserMessage.Text; // Nimm Text als Titel
            
            if (title.Length > 50) // Ist Titel zu lang?
            {
                title = title.Substring(0, 47) + "..."; // Kürze auf 50 Zeichen mit ...
            }
            
            Conversation conv = await this.db.Conversations.FindAsync(conversationId); // Finde Conversation in DB
            
            if (conv != null) // Wurde Conversation gefunden?
            {
                conv.Title = title; // Setze neuen Titel
                await this.db.SaveChangesAsync(); // Speichere in Datenbank
            }
        }
    }

    public async Task<List<UploadedFile>> LoadFilesForConversationAsync(int conversationId) // Files für einen Chat laden
    {
        return await this.db.UploadedFiles // Hole nur Files für diesen Chat (Filter in DB, nicht clientseitig)
            .AsNoTracking() // Keine Change-Tracking (schneller + verhindert Concurrency-Probleme)
            .Where(f => f.ConversationId == conversationId) // Filtere nach ConversationId
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    public async Task DeleteConversationAsync(int conversationId) // Conversation mit allen Messages und Files löschen
    {
        var messages = await this.db.Messages // Lade alle Nachrichten für diesen Chat
            .Where(m => m.ConversationId == conversationId) // Filtere nach ConversationId
            .ToListAsync(); // Führe Query aus
        
        this.db.Messages.RemoveRange(messages); // Lösche alle Nachrichten auf einmal
        
        var files = await this.db.UploadedFiles // Lade alle Files für diesen Chat
            .Where(f => f.ConversationId == conversationId) // Filtere nach ConversationId
            .ToListAsync(); // Führe Query aus
        
        this.db.UploadedFiles.RemoveRange(files); // Lösche alle Files auf einmal
        
        Conversation conv = await this.db.Conversations.FindAsync(conversationId); // Finde Conversation
        
        if (conv != null) // Wurde Conversation gefunden?
        {
            this.db.Conversations.Remove(conv); // Ja -> Lösche Conversation
        }
        
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank
    }
}
