using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class FolderDbService // Service für Ordner-Operationen und Dokument-Standort-Verwaltung
{
    private readonly ChatDbContext db;

    public FolderDbService(ChatDbContext dbContext)
    {
        this.db = dbContext;
    }

    public async Task EnsureDatabaseSchemaAsync() // Erstellt Tabellen und migriert Schema (idempotent)
    {
        try
        {
            await this.db.Database.ExecuteSqlRawAsync( // Folders-Tabelle
                "CREATE TABLE IF NOT EXISTS `Folders` (" +
                "`Id` INT NOT NULL AUTO_INCREMENT, " +
                "`Name` VARCHAR(255) NOT NULL, " +
                "`CreatedAt` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6), " +
                "PRIMARY KEY (`Id`));"
            );

            try // FolderId-Spalte (Legacy)
            {
                await this.db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `Documents` ADD COLUMN `FolderId` INT NULL;"
                );
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060) { }

            try // IsInRoot-Spalte
            {
                await this.db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `Documents` ADD COLUMN `IsInRoot` TINYINT(1) NOT NULL DEFAULT 1;"
                );
            }
            catch (MySql.Data.MySqlClient.MySqlException ex) when (ex.Number == 1060) { }

            await this.db.Database.ExecuteSqlRawAsync( // Junction-Tabelle Dokument↔Ordner
                "CREATE TABLE IF NOT EXISTS `DocumentFolders` (" +
                "`DocumentId` INT NOT NULL, " +
                "`FolderId` INT NOT NULL, " +
                "PRIMARY KEY (`DocumentId`, `FolderId`), " +
                "FOREIGN KEY (`DocumentId`) REFERENCES `Documents`(`Id`) ON DELETE CASCADE, " +
                "FOREIGN KEY (`FolderId`) REFERENCES `Folders`(`Id`) ON DELETE CASCADE);"
            );

            await this.db.Database.ExecuteSqlRawAsync( // Bestehende FolderId-Daten in Junction migrieren
                "INSERT IGNORE INTO `DocumentFolders` (`DocumentId`, `FolderId`) " +
                "SELECT `Id`, `FolderId` FROM `Documents` WHERE `FolderId` IS NOT NULL;"
            );

            System.Diagnostics.Debug.WriteLine("[FOLDER_SERVICE] Schema sichergestellt");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_SERVICE] Schema-Fehler: {ex.Message}");
        }
    }

    // --- Ordner-Operationen ---

    public async Task<List<Folder>> GetAllFoldersAsync() // Alle Ordner alphabetisch
    {
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync();
        allFolders.Sort(CompareFoldersByNameAsc); // Sortiere alphabetisch aufsteigend
        return allFolders;
    }

    public async Task<Folder?> GetFolderByIdAsync(int id) // Ordner per ID
    {
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync();
        foreach (Folder f in allFolders)
        {
            if (f.Id == id) return f;
        }
        return null;
    }

    public async Task<Folder?> GetFolderByNameAsync(string name) // Ordner per Name (case-insensitive)
    {
        string lowerName = name.ToLower().Trim();
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync();
        foreach (Folder f in allFolders)
        {
            if (f.Name.ToLower() == lowerName) return f;
        }
        return null;
    }

    public async Task<Folder> CreateFolderAsync(string name) // Neuen Ordner anlegen
    {
        Folder folder = new Folder();
        folder.Name = name.Trim();
        folder.CreatedAt = DateTime.UtcNow;
        this.db.Folders.Add(folder);
        await this.db.SaveChangesAsync();
        return folder;
    }

    public async Task DeleteFolderAsync(int id) // Ordner löschen; exklusive Docs aus DB, Kopien bleiben erhalten
    {
        List<Document> docsInFolder = await this.GetDocumentsInFolderAsync(id);

        foreach (Document doc in docsInFolder)
        {
            bool hasOtherCopy = doc.IsInRoot;
            if (!hasOtherCopy)
            {
                var otherLinks = await this.db.DocumentFolders
                    .AsNoTracking()
                    .Where(df => df.DocumentId == doc.Id && df.FolderId != id)
                    .ToListAsync();
                if (otherLinks.Count > 0) hasOtherCopy = true;
            }

            if (!hasOtherCopy)
            {
                Document? tracked = await this.db.Documents.FindAsync(doc.Id);
                if (tracked != null) this.db.Documents.Remove(tracked);
            }
        }

        await this.db.SaveChangesAsync();

        Folder? toDelete = await this.db.Folders.FindAsync(id);
        if (toDelete != null)
        {
            this.db.Folders.Remove(toDelete);
            await this.db.SaveChangesAsync();
        }
    }

    // --- Dokument-Standort-Abfragen ---

    public async Task<List<Document>> GetRootDocumentsAsync() // Dokumente im Hauptbereich (IsInRoot = true)
    {
        var result = await this.db.Documents
            .AsNoTracking()
            .Where(d => d.IsInRoot)
            .ToListAsync();
        result.Sort(CompareDocumentsByUploadedAtDesc); // Sortiere nach Datum (neueste zuerst)
        return result;
    }

    public async Task<List<Document>> GetDocumentsInFolderAsync(int folderId) // Dokumente in einem Ordner
    {
        var docIds = await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.FolderId == folderId)
            .Select(df => df.DocumentId)
            .ToListAsync();

        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync();
        var result = new List<Document>();
        foreach (Document d in allDocs)
        {
            foreach (int id in docIds)
            {
                if (d.Id == id) { result.Add(d); break; }
            }
        }
        result.Sort(CompareDocumentsByUploadedAtDesc); // Sortiere nach Datum (neueste zuerst)
        return result;
    }

    public async Task<List<int>> GetDocumentFolderIdsAsync(int documentId) // Ordner-IDs eines Dokuments
    {
        return await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.DocumentId == documentId)
            .Select(df => df.FolderId)
            .ToListAsync();
    }

    // --- Dokument-Standort-Operationen ---

    public async Task MoveDocumentToFolderAsync(int documentId, int? targetFolderId) // Verschieben: nur noch im Ziel
    {
        Document? doc = await this.db.Documents.FindAsync(documentId);
        if (doc == null) return;

        var existing = await this.db.DocumentFolders
            .Where(df => df.DocumentId == documentId)
            .ToListAsync();
        this.db.DocumentFolders.RemoveRange(existing);

        if (targetFolderId.HasValue)
        {
            doc.IsInRoot = false;
            DocumentFolder link = new DocumentFolder();
            link.DocumentId = documentId;
            link.FolderId = targetFolderId.Value;
            this.db.DocumentFolders.Add(link);
        }
        else
        {
            doc.IsInRoot = true;
        }

        await this.db.SaveChangesAsync();
    }

    public async Task<bool> CopyDocumentToFolderAsync(int documentId, int targetFolderId) // Kopieren in Ordner
    {
        bool exists = await this.db.DocumentFolders
            .AsNoTracking()
            .AnyAsync(df => df.DocumentId == documentId && df.FolderId == targetFolderId);

        if (exists) return false;

        DocumentFolder link = new DocumentFolder();
        link.DocumentId = documentId;
        link.FolderId = targetFolderId;
        this.db.DocumentFolders.Add(link);
        await this.db.SaveChangesAsync();
        return true;
    }

    public async Task CopyDocumentToRootAsync(int documentId) // IsInRoot = true setzen
    {
        Document? doc = await this.db.Documents.FindAsync(documentId);
        if (doc == null) return;
        doc.IsInRoot = true;
        await this.db.SaveChangesAsync();
    }

    public async Task SetDocumentInRootAsync(int documentId, bool inRoot) // IsInRoot direkt setzen
    {
        Document? doc = await this.db.Documents.FindAsync(documentId);
        if (doc == null) return;
        doc.IsInRoot = inRoot;
        await this.db.SaveChangesAsync();
    }

    public async Task<bool> DeleteDocumentFromLocationAsync(int documentId, int? folderId) // Von Standort entfernen; löscht aus DB wenn letzte Kopie
    {
        Document? doc = await this.db.Documents.FindAsync(documentId);
        if (doc == null) return false;

        if (folderId == null)
        {
            doc.IsInRoot = false;
        }
        else
        {
            var link = await this.db.DocumentFolders
                .FirstOrDefaultAsync(df => df.DocumentId == documentId && df.FolderId == folderId.Value);
            if (link != null) this.db.DocumentFolders.Remove(link);
        }

        await this.db.SaveChangesAsync();

        Document? reloaded = await this.db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId);
        bool stillInRoot = false;
        if (reloaded != null) stillInRoot = reloaded.IsInRoot;

        int remainingLinks = await this.db.DocumentFolders
            .AsNoTracking()
            .CountAsync(df => df.DocumentId == documentId);

        if (!stillInRoot && remainingLinks == 0)
        {
            Document? toDelete = await this.db.Documents.FindAsync(documentId);
            if (toDelete != null) this.db.Documents.Remove(toDelete);
            await this.db.SaveChangesAsync();
            return true;
        }

        return false;
    }

    public async Task<bool> RemoveDocumentFromFolderAsync(int documentId, int folderId) // Nur Link entfernen
    {
        var link = await this.db.DocumentFolders
            .FirstOrDefaultAsync(df => df.DocumentId == documentId && df.FolderId == folderId);
        if (link == null) return false;
        this.db.DocumentFolders.Remove(link);
        await this.db.SaveChangesAsync();
        return true;
    }

    // --- Duplikat-Pruefung ---

    public async Task<bool> FolderNameExistsAsync(string name) // Prueft ob ein Ordnername bereits vergeben ist (global, case-insensitive)
    {
        string lower = name.ToLower().Trim();
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync();
        foreach (Folder f in allFolders)
        {
            if (f.Name.ToLower() == lower) return true; // Name bereits vergeben
        }
        return false;
    }

    public async Task<bool> DocumentNameExistsInLocationAsync(string fileName, int? folderId) // Prueft ob exakter Dateiname an diesem Standort existiert
    {
        string lower = fileName.ToLower();

        if (folderId == null) // Root-Bereich: alle Dokumente mit IsInRoot=true pruefen
        {
            var rootDocs = await this.db.Documents.AsNoTracking().Where(d => d.IsInRoot).ToListAsync();
            foreach (Document d in rootDocs)
            {
                if (d.FileName.ToLower() == lower) return true;
            }
            return false;
        }

        // Ordner: Dokument-IDs im Ordner laden, dann Name pruefen
        var docIdsInFolder = await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.FolderId == folderId.Value)
            .Select(df => df.DocumentId)
            .ToListAsync();

        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync();
        foreach (Document d in allDocs)
        {
            if (d.FileName.ToLower() != lower) continue;
            foreach (int id in docIdsInFolder)
            {
                if (id == d.Id) return true; // Gleicher Name im selben Ordner gefunden
            }
        }
        return false;
    }

    // --- Ordner umbenennen ---

    public async Task<bool> RenameFolderAsync(int folderId, string newName) // Ordner umbenennen per ID
    {
        Folder? folder = await this.db.Folders.FindAsync(folderId);
        if (folder == null) return false;
        folder.Name = newName.Trim();
        await this.db.SaveChangesAsync();
        return true;
    }

    // --- Dokument komplett loeschen ---

    public async Task<bool> DeleteDocumentCompletelyAsync(int documentId) // Entfernt Dokument aus allen Standorten und aus der DB
    {
        var links = await this.db.DocumentFolders
            .Where(df => df.DocumentId == documentId)
            .ToListAsync();
        this.db.DocumentFolders.RemoveRange(links); // Alle Junction-Eintraege entfernen

        Document? doc = await this.db.Documents.FindAsync(documentId);
        if (doc == null) return false;

        this.db.Documents.Remove(doc);
        await this.db.SaveChangesAsync();
        return true;
    }

    // --- Suche ---

    public async Task<List<Document>> FindDocumentsByNameAsync(string searchName)
    {
        string lower = searchName.ToLower().Trim();
        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync();
        var result = new List<Document>();
        foreach (Document d in allDocs)
        {
            if (d.FileName.ToLower().Contains(lower)) result.Add(d);
        }
        return result;
    }

    public async Task<Document?> FindDocumentByNameAsync(string searchName)
    {
        var found = await this.FindDocumentsByNameAsync(searchName);
        if (found.Count == 0) return null;
        return found[0];
    }

    private static int CompareFoldersByNameAsc(Folder a, Folder b) // Vergleich: alphabetisch aufsteigend
    {
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareDocumentsByUploadedAtDesc(Document a, Document b) // Vergleich: neuestes Dokument zuerst
    {
        return b.UploadedAt.CompareTo(a.UploadedAt);
    }
}
