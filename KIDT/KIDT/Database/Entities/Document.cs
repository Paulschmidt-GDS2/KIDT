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
    public int? FolderId { get; set; } // Legacy-Spalte (nicht mehr genutzt, bleibt für DB-Kompatibilität)
    public bool IsInRoot { get; set; } = true; // true = Datei liegt im Root-Bereich (Hauptbereich)

    public List<ConversationDocument> ConversationDocuments { get; set; } = new();
    public List<DocumentFolder> DocumentFolders { get; set; } = new(); // n:m Ordner-Zuordnungen
}