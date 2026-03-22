using System;

namespace KIDT.Models;

public class Document // Klasse für global gespeicherte Dokumente
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string FileContent { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty; 
    public string ExtractedText { get; set; } = string.Empty;
    public string ThumbnailBase64 { get; set; } = string.Empty; 
    public DateTime UploadedAt { get; set; }
    
    public List<ConversationDocument> ConversationDocuments { get; set; } = new();
}