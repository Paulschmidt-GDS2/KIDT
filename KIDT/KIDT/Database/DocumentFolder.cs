using KIDT.Models;

namespace KIDT.Models;

public class DocumentFolder // Junction: Dokument↔Ordner-Zuordnung (n:m)
{
    public int DocumentId { get; set; }
    public int FolderId { get; set; }

    public Document Document { get; set; } = null!;
    public Folder Folder { get; set; } = null!;
}
