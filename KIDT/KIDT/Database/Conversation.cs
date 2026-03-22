using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace KIDT.Models;

public class Conversation // Klasse für einen Chat
{
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    
    public List<Message> Messages { get; set; } = new(); 
    
    [NotMapped] // WICHTIG: Verhindert dass EF Core eine direkte Beziehung zwischen Conversation und Document erstellt!
    public List<Document> LinkedDocuments { get; set; } = new();
}