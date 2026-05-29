using KIDT.Models;

namespace KIDT.Services;

public class RouterResponse // Routing-Ergebnis des RouterService (geht an ChatCoordinator)
{
    public bool ShouldRoute { get; set; }
    public string? DirectResponse { get; set; }
    public string TargetService { get; set; } = string.Empty;
    public int MaxTokens { get; set; }
    public List<Document> FoundDocuments { get; set; } = new List<Document>();
    public List<CalendarEvent> FoundEvents { get; set; } = new List<CalendarEvent>();
    public string Reason { get; set; } = string.Empty;
    public bool ToolWasUsed { get; set; }
}
