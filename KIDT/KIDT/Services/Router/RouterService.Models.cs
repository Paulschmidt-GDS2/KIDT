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

    private class FolderResultJson // JSON: Ergebnis von Ordner-Tools (create, delete, move)
    {
        public bool success { get; set; }
        public string? message { get; set; }
        public int? folderId { get; set; }
        public int? documentId { get; set; }
    }

    private class FolderListResultJson // JSON: Ergebnis von list_documents_in_folder
    {
        public bool success { get; set; }
        public int found { get; set; }
        public string? message { get; set; }
        public List<FolderDocumentJson>? documents { get; set; }
    }

    private class FolderDocumentJson // JSON: Einzelnes Dokument im Ordner-Listing
    {
        public int id { get; set; }
        public string fileName { get; set; } = string.Empty;
        public string fileType { get; set; } = string.Empty;
    }

    private class FindDocumentsResultJson // JSON: Ergebnis von find_documents (mit Ordner-Zugehörigkeiten)
    {
        public bool success { get; set; }
        public int found { get; set; }
        public string? message { get; set; }
        public List<FindDocumentItemJson>? documents { get; set; }
    }

    private class FindDocumentItemJson // JSON: Einzelne Datei mit Ordner-Zugehörigkeiten
    {
        public int id { get; set; }
        public string fileName { get; set; } = string.Empty;
        public string fileType { get; set; } = string.Empty;
        public List<string>? inFolders { get; set; }
    }

    private class AllFoldersResultJson // JSON: Ergebnis von list_all_folders
    {
        public bool success { get; set; }
        public int found { get; set; }
        public string? message { get; set; }
        public List<FolderItemJson>? folders { get; set; }
    }

    private class FolderItemJson // JSON: Einzelner Ordner in der Ordnerliste
    {
        public int id { get; set; }
        public string name { get; set; } = string.Empty;
        public int documentCount { get; set; }
    }
}
