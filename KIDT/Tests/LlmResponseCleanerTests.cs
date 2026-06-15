using KIDT.Services.Logic;
using Xunit;

namespace KIDT.Tests;

public class LlmResponseCleanerTests // Tests für die Bereinigung lokaler LLM-Antworten
{
    [Fact]
    public void Clean_PlainText_PassesThrough() // Saubere Antwort bleibt erhalten
    {
        string result = LlmResponseCleaner.Clean("Das Dokument behandelt C# Strings.");
        Assert.Equal("Das Dokument behandelt C# Strings.", result);
    }

    [Fact]
    public void Clean_QuoteWrapped_StripsQuotes() // Umschließende Anführungszeichen entfernen
    {
        string result = LlmResponseCleaner.Clean("\"Eine Antwort\"");
        Assert.Equal("Eine Antwort", result);
    }

    [Fact]
    public void Clean_WithEmojis_RemovesThem() // Emojis werden entfernt
    {
        string result = LlmResponseCleaner.Clean("Fertig ✅ erledigt");
        Assert.DoesNotContain("✅", result);
        Assert.Contains("Fertig", result);
    }

    [Fact]
    public void Clean_EmptyInput_ReturnsInput() // Leere Eingabe bleibt leer
    {
        string result = LlmResponseCleaner.Clean("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Clean_DebugMarkerOnly_ReturnsFallback() // Nur Debug-Inhalt → freundlicher Fallback
    {
        string result = LlmResponseCleaner.Clean("=== Input:\n========");
        Assert.Equal("Wie kann ich dir helfen?", result);
    }
}
