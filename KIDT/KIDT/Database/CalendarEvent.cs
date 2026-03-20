using System;
using System.ComponentModel.DataAnnotations;

namespace KIDT.Models;

public class CalendarEvent // Klasse für einen Kalender-Termin
{
    [Key]
    public int Id { get; set; } // Primärschlüssel (wird automatisch hochgezählt)
    
    public DateTime Start { get; set; } // Startdatum des Termins
    
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty; // Titel des Termins (nie null)
    
    public int ColorIndex { get; set; } = 0; // Farbindex (0-7)

    public TimeSpan Time { get; set; } = TimeSpan.Zero; // Uhrzeit (Zero = nicht gesetzt)
    public bool HasTime { get; set; } = false; // Hat eine Uhrzeit?

    public bool IsAllDay { get; set; } = true; // Ist der Termin ganztägig?

    public int? ReminderMinutesBefore { get; set; } // Erinnerung X Minuten vorher (null = keine Erinnerung)
    public bool ReminderShown { get; set; } = false; // Wurde die Erinnerung bereits angezeigt?

    public DateTime CreatedAt { get; set; } // Wann wurde der Termin erstellt?

    public DateTime? UpdatedAt { get; set; } // Wann wurde der Termin zuletzt bearbeitet? (nullable)
}
