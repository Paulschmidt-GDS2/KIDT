using System;

namespace KIDT.Models;

public class ConversationDocument // Junction-Tabelle: Verbindet Conversations mit Documents
{
    public int ConversationId { get; set; } // Foreign Key: Welcher Chat?
    public int DocumentId { get; set; } // Foreign Key: Welches Dokument?
    public DateTime AddedAt { get; set; } // Wann wurde Dokument zum Chat hinzugefügt?
    
    public Conversation? Conversation { get; set; } // Navigation: Zugeh?riger Chat (nullable!)
    public Document? Document { get; set; } // Navigation: Zugeh?riges Dokument (nullable!)
}
