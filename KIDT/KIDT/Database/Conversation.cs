using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace KIDT.Models;

public class Conversation // Klasse für einen Chat
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public DateTime CreatedAt { get; set; } // Wann wurde Chat erstellt?
    public string Title { get; set; } = string.Empty; // Chat-Titel (nie null)
    
    public List<Message> Messages { get; set; } = new(); // Alle Nachrichten in diesem Chat (nie null)
    
    [NotMapped] // WICHTIG: Verhindert dass EF Core eine direkte Beziehung zwischen Conversation und Document erstellt!
    public List<Document> LinkedDocuments { get; set; } = new(); // Verknüpfte Dokumente (manuell via ConversationDocuments geladen, nie null)
}