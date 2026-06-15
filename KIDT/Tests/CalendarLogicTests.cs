using System;
using KIDT.Services.Logic;
using Xunit;

namespace KIDT.Tests;

public class CalendarLogicTests // Tests für die reine Kalender-Logik
{
    // --- ParseFlexibleDate ---

    [Fact]
    public void ParseFlexibleDate_IsoFormat_ParsesAndReturnsNull() // ISO-Datum wird korrekt geparst
    {
        DateTime result;
        string? error = CalendarLogic.ParseFlexibleDate("2026-03-19", out result); // Gültiges ISO-Datum

        Assert.Null(error); // Kein Fehler
        Assert.Equal(new DateTime(2026, 3, 19), result.Date);
    }

    [Fact]
    public void ParseFlexibleDate_DayMonthFormat_UsesCurrentYear() // "dd.MM" nimmt aktuelles Jahr an
    {
        DateTime result;
        string? error = CalendarLogic.ParseFlexibleDate("19.03", out result); // Tag.Monat ohne Jahr

        Assert.Null(error); // Kein Fehler
        Assert.Equal(DateTime.Now.Year, result.Year);
        Assert.Equal(3, result.Month);
        Assert.Equal(19, result.Day);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein datum")]
    [InlineData("32.01")]
    [InlineData("10.13")]
    public void ParseFlexibleDate_Invalid_ReturnsError(string input) // Ungültige Eingaben liefern Fehlertext
    {
        DateTime result;
        string? error = CalendarLogic.ParseFlexibleDate(input, out result); // Ungültige Eingabe

        Assert.NotNull(error); // Fehlertext erwartet
    }

    // --- EventsOverlap ---

    [Theory]
    [InlineData(10, 12, 11, 13, true)]   // Überlappung
    [InlineData(10, 12, 12, 14, false)]  // Berührung am Rand → keine Überlappung
    [InlineData(10, 12, 13, 15, false)]  // Komplett getrennt
    [InlineData(9, 17, 12, 13, true)]    // Voll umschlossen
    public void EventsOverlap_DetectsCorrectly(int newStart, int newEnd, int exStart, int exEnd, bool expected) // Überschneidungsprüfung
    {
        bool overlaps = CalendarLogic.EventsOverlap(
            TimeSpan.FromHours(newStart), TimeSpan.FromHours(newEnd),
            TimeSpan.FromHours(exStart), TimeSpan.FromHours(exEnd));

        Assert.Equal(expected, overlaps);
    }

    // --- MapColorNameToIndex ---

    [Theory]
    [InlineData("blau", true, 1)]
    [InlineData("BLAU", true, 1)]    // Case-insensitiv
    [InlineData("grün", true, 3)]
    [InlineData("gruen", true, 3)]   // Schreibweise ohne Umlaut
    [InlineData("tuerkis", true, 7)]
    [InlineData("regenbogen", false, 0)] // Unbekannte Farbe
    public void MapColorNameToIndex_MapsKnownColors(string name, bool expectedFound, int expectedIndex) // Farbnamen-Mapping
    {
        int index;
        bool found = CalendarLogic.MapColorNameToIndex(name, out index);

        Assert.Equal(expectedFound, found);
        if (expectedFound) Assert.Equal(expectedIndex, index); // Index nur bei Treffer prüfen
    }

    // --- FormatEventTime ---

    [Fact]
    public void FormatEventTime_AllDay_ReturnsGanztaegig() // Ganztägiger Termin
    {
        string text = CalendarLogic.FormatEventTime(true, false, TimeSpan.Zero, false, TimeSpan.Zero);
        Assert.Equal("Ganztägig", text);
    }

    [Fact]
    public void FormatEventTime_TimeSpan_ReturnsRange() // Zeitspanne Start + Ende
    {
        string text = CalendarLogic.FormatEventTime(false, true, TimeSpan.FromHours(14), true, TimeSpan.FromHours(16));
        Assert.Equal("14:00 - 16:00", text);
    }

    [Fact]
    public void FormatEventTime_StartOnly_ReturnsStart() // Nur Startzeit
    {
        string text = CalendarLogic.FormatEventTime(false, true, new TimeSpan(9, 30, 0), false, TimeSpan.Zero);
        Assert.Equal("09:30", text);
    }

    [Fact]
    public void FormatEventTime_NoTime_ReturnsKeineZeit() // Keine Uhrzeit
    {
        string text = CalendarLogic.FormatEventTime(false, false, TimeSpan.Zero, false, TimeSpan.Zero);
        Assert.Equal("Keine Zeit", text);
    }
}
