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

    public TimeSpan Time { get; set; } = TimeSpan.Zero;
    public bool HasTime { get; set; } = false;

    public bool IsAllDay { get; set; } = true;

    public int? ReminderMinutesBefore { get; set; }
    public bool ReminderShown { get; set; } = false;

    public DateTime CreatedAt { get; set; } 

    public DateTime? UpdatedAt { get; set; }
}