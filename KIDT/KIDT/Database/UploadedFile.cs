using System;

namespace KIDT.Models;

public class UploadedFile // Klasse für hochgeladene Datei
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public int ConversationId { get; set; } // Foreign Key: Zu welchem Chat gehört diese Datei?
    public string FileName { get; set; } // Dateiname (z.B. "Rechnung.pdf")
    public string ExtractedText { get; set; } // Extrahierter Text aus PDF/TXT/etc.
    public string ThumbnailBase64 { get; set; } // Vorschaubild als Base64-String (für PDFs: erste Seite)
    public DateTime UploadedAt { get; set; } // Wann wurde Datei hochgeladen?
    
    public Conversation Conversation { get; set; } // Navigation: Zugehöriger Chat

    public UploadedFile() // Konstruktor: Wird beim Erstellen aufgerufen
    {
        FileName = string.Empty; // Leerer String als Standard
        ExtractedText = string.Empty; // Leerer String als Standard
        ThumbnailBase64 = string.Empty; // Leerer String als Standard
        Conversation = new Conversation(); // Initialisiere mit leerem Conversation-Objekt
    }
}