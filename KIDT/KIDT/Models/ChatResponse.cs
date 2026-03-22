namespace KIDT.Models;

public class ChatResponse // Klasse: Response von ChatCoordinator (Message + gefundene Dokumente/Termine für UI-Anzeige)
{
    public string Message { get; set; } = string.Empty;
    public List<Document> FoundDocuments { get; set; } = new List<Document>();
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
}