using System;
using System.ComponentModel.DataAnnotations;

namespace KIDT.Models;

public class CalendarEvent // Klasse für einen Kalender-Termin
{
    [Key]
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)

    public DateTime Start { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public int ColorIndex { get; set; } = 0;

    public TimeSpan Time { get; set; } = TimeSpan.Zero; // Startzeit
    public TimeSpan EndTime { get; set; } = TimeSpan.Zero; // Endzeit (nur wenn HasEndTime = true)
    public bool HasTime { get; set; } = false;
    public bool HasEndTime { get; set; } = false; // Ob Endzeit gesetzt ist

    public bool IsAllDay { get; set; } = true;

    public int? ReminderMinutesBefore { get; set; }
    public bool ReminderShown { get; set; } = false;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}