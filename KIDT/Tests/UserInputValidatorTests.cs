using KIDT.Services.Logic;
using Xunit;

namespace KIDT.Tests;

public class UserInputValidatorTests // Tests für die Eingabe-Validierung
{
    [Theory]
    [InlineData("Erstelle einen Termin morgen", true)] // Normale Eingabe
    [InlineData("Hi", true)]                            // Kurz, aber gültig
    [InlineData("", false)]                             // Leer
    [InlineData(" ", false)]                            // Nur Whitespace
    [InlineData("a", false)]                            // Zu kurz
    public void IsValid_HandlesBasicCases(string input, bool expected) // Grundfälle
    {
        bool result = UserInputValidator.IsValid(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValid_RepetitiveGibberish_IsRejected() // Lange Wiederholung ohne Leerzeichen
    {
        string gibberish = "abcdeabcdeabcdeabcdeabcdeabcdeabcdeabcdeabcde"; // Muster "abcde" vielfach
        bool result = UserInputValidator.IsValid(gibberish);
        Assert.False(result);
    }

    [Fact]
    public void IsValid_LongTextWithSpaces_IsAccepted() // Langer echter Satz bleibt gültig
    {
        string sentence = "Bitte fasse mir das hochgeladene Dokument in wenigen Sätzen verständlich zusammen";
        bool result = UserInputValidator.IsValid(sentence);
        Assert.True(result);
    }
}
