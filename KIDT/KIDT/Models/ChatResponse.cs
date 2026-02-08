namespace KIDT.Models;

public class ChatResponse // Klasse: Response von ChatCoordinator (Message + gefundene Dokumente für UI-Anzeige)
{
    public string Message { get; set; } = string.Empty; // Text-Antwort für User
    public List<Document> FoundDocuments { get; set; } = new List<Document>(); // Gefundene Dokumente (bei search_documents)
}
