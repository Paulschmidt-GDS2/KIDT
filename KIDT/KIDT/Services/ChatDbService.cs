using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KIDT.Data;
using KIDT.Models;

namespace KIDT.Services;

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

    public async Task<List<Message>> LoadMessagesAsync(int conversationId) // Nachrichten laden
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
        return filteredMessages; // Gib gefilterte Liste zurück
    }
}