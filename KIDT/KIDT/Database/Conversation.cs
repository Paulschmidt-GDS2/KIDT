using System;
using System.Collections.Generic;

namespace KIDT.Models;

public class Conversation // Klasse für einen Chat
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public DateTime CreatedAt { get; set; } // Wann wurde Chat erstellt?
    public string Title { get; set; } = string.Empty; // Chat-Titel (nie null)
    
    public List<Message> Messages { get; set; } = new(); // Alle Nachrichten in diesem Chat (nie null)
    public List<UploadedFile> UploadedFiles { get; set; } = new(); // Alle hochgeladenen Dateien in diesem Chat (nie null)
}