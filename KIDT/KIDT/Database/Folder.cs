namespace KIDT.Models;

public class Folder // Model für Dokumenten-Ordner
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
