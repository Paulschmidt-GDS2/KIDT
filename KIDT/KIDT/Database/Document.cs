using System;
using System.Collections.Generic;

namespace KIDT.Models;

public class Document // Klasse für global gespeicherte Dokumente
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public string FileName { get; set; } = string.Empty; // Dateiname (nie null)
    public string FileHash { get; set; } = string.Empty; // SHA256-Hash für Duplikat-Check (nie null)
    public string FileContent { get; set; } = string.Empty; // Kompletter File-Content als Base64 (nie null)
    public string FileType { get; set; } = string.Empty; // Dateityp z.B. "pdf", "txt" (nie null)
    public string ExtractedText { get; set; } = string.Empty; // Extrahierter Text (nie null)
    public string ThumbnailBase64 { get; set; } = string.Empty; // Vorschaubild Base64 (nie null)
    public DateTime UploadedAt { get; set; } // Wann wurde Dokument hochgeladen?
    
    public List<ConversationDocument> ConversationDocuments { get; set; } = new(); // Junction: Welche Chats haben dieses Dokument? (nie null)
}
