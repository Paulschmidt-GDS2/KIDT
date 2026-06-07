namespace KIDT.Models;

public class ConversationDocument // Junction-Tabelle: Verbindet Conversations mit Documents
{
    public int ConversationId { get; set; }
    public int DocumentId { get; set; }
    public DateTime AddedAt { get; set; }

    public Conversation? Conversation { get; set; }
    public Document? Document { get; set; }
}