using System;

namespace KIDT.Models;

public class Message // Klasse für eine Nachricht
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public int ConversationId { get; set; }
    public bool IsUser { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? DocumentIdsJson { get; set; }
    public string? EventIdsJson { get; set; }
    public Conversation? Conversation { get; set; }
}