using KIDT.Models;

namespace KIDT.Services;

public class ChatStreamChunk // Einzelner Streaming-Chunk (Text + Status + gefundene Elemente)
{
    public string TextChunk { get; set; } = string.Empty;
    public bool IsComplete { get; set; } = false;
    public List<Document> FoundDocuments { get; set; } = new List<Document>();
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
}
