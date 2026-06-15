using KIDT.Services.Logic;
using Xunit;

namespace KIDT.Tests;

public class ToolNameConverterTests // Tests für PascalCase → snake_case
{
    [Theory]
    [InlineData("SearchDocuments", "search_documents")]
    [InlineData("CreateCalendarEvent", "create_calendar_event")]
    [InlineData("ListAllFolders", "list_all_folders")]
    [InlineData("Search", "search")]      // Einzelwort
    [InlineData("", "")]                   // Leer bleibt leer
    public void ToSnakeCase_ConvertsCorrectly(string input, string expected) // Namens-Konvertierung
    {
        string result = ToolNameConverter.ToSnakeCase(input);
        Assert.Equal(expected, result);
    }
}
