using System;
using System.Collections.Generic;

namespace KIDT.Models;

public class Conversation // Klasse für einen Chat
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public DateTime CreatedAt { get; set; } // Wann wurde Chat erstellt?
    public string Title { get; set; } // Chat-Titel
    
    public List<Message> Messages { get; set; } // Alle Nachrichten in diesem Chat
    public List<UploadedFile> UploadedFiles { get; set; } // Alle hochgeladenen Dateien in diesem Chat

    public Conversation() // Konstruktor: Wird beim Erstellen aufgerufen
    {
        Title = "Neuer Chat"; // Standard-Titel setzen
        Messages = new List<Message>(); // Leere Liste erstellen
        UploadedFiles = new List<UploadedFile>(); // Leere Liste erstellen
    }
}