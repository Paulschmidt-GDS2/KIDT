namespace KIDT.Services;

public partial class RouterService
{
    private class SimpleRoutingJson // JSON: DataAnalysis-Route-Signal von Gemini
    {
        public string? route { get; set; }
    }

    private class SearchResultJson // JSON: Ergebnis von search_documents
    {
        public int found { get; set; }
        public List<int>? documentIds { get; set; }
        public string? message { get; set; }
    }

    private class AddDocumentResultJson // JSON: Ergebnis von add_document_to_chat
    {
        public bool success { get; set; }
        public int documentId { get; set; }
        public string? fileName { get; set; }
        public string? message { get; set; }
    }

    private class CalendarListResultJson // JSON: Ergebnis von list_calendar_events
    {
        public int found { get; set; }
        public List<CalendarEventJson>? events { get; set; }
        public string? message { get; set; }
    }

    private class CalendarEventJson // JSON: Einzelner Termin in Kalender-Ergebnissen
    {
        public int id { get; set; }
        public string date { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string time { get; set; } = string.Empty;
        public int color { get; set; }
    }

    private class CalendarCreateResultJson // JSON: Ergebnis von create_calendar_event
    {
        public bool success { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
    }

    private class CalendarDeleteResultJson // JSON: Ergebnis von delete_calendar_event
    {
        public bool success { get; set; }
        public bool needsClarification { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
        public List<CalendarEventJson>? events { get; set; }
    }

    private class CalendarUpdateResultJson // JSON: Ergebnis von update_calendar_event
    {
        public bool success { get; set; }
        public bool needsClarification { get; set; }
        public string? message { get; set; }
        public int eventId { get; set; }
        public List<CalendarEventJson>? events { get; set; }
    }
}
