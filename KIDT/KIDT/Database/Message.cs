using System;

namespace KIDT.Models;

public class Message // Klasse für eine Nachricht
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public int ConversationId { get; set; } // Foreign Key: Zu welchem Chat gehört diese Nachricht?
    public bool IsUser { get; set; } // true = User, false = Assistant
    public string Text { get; set; } = string.Empty; // Nachrichtentext (nie null)
    public DateTime Timestamp { get; set; } // Wann wurde Nachricht gesendet?
    
    public Conversation? Conversation { get; set; } // Navigation: Zugehöriger Chat (nullable!)
}