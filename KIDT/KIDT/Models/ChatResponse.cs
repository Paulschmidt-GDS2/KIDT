namespace KIDT.Models;

public class ChatResponse // Klasse: Response von ChatCoordinator (Message + gefundene Dokumente/Termine für UI-Anzeige)
{
    public string Message { get; set; } = string.Empty; // Text-Antwort für User
    public List<Document> FoundDocuments { get; set; } = new List<Document>(); // Gefundene Dokumente (bei search_documents)
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>(); // Gefundene Termine (bei list_calendar_events)
}
