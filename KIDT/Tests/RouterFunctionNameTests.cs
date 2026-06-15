using KIDT.Services.Logic;
using Xunit;

namespace KIDT.Tests;

public class RouterFunctionNameTests // Tests für Normalisierung der Gemini-Funktionsnamen
{
    [Theory]
    [InlineData("Document_remove_document_from_folder", "remove_document_from_folder")]
    [InlineData("Calendar_create_calendar_event", "create_calendar_event")]
    [InlineData("Folder_list_all_folders", "list_all_folders")]
    [InlineData("Analysis_analyze_document", "analyze_document")]
    [InlineData("create_calendar_event", "create_calendar_event")] // Ohne Präfix unverändert
    [InlineData("DOCUMENT_search_documents", "search_documents")]    // Case-insensitiv
    public void Normalize_StripsKnownPrefix(string input, string expected) // Präfix-Entfernung
    {
        string result = RouterFunctionName.Normalize(input);
        Assert.Equal(expected, result);
    }
}
