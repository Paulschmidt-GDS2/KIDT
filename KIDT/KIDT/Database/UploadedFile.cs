using System;

namespace KIDT.Models;

public class UploadedFile // Klasse für hochgeladene Datei
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public int ConversationId { get; set; } // Foreign Key: Zu welchem Chat gehört diese Datei?
    public string FileName { get; set; } = string.Empty; // Dateiname (nie null)
    public string ExtractedText { get; set; } = string.Empty; // Extrahierter Text (nie null)
    public string ThumbnailBase64 { get; set; } = string.Empty; // Vorschaubild Base64 (nie null)
    public DateTime UploadedAt { get; set; } // Wann wurde Datei hochgeladen?
    
    public Conversation? Conversation { get; set; } // Navigation: Zugehöriger Chat (nullable!)
}