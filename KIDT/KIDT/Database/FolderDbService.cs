using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class FolderDbService // Service für Ordner-Operationen und Dokument-Standort-Verwaltung
{
    private readonly ChatDbContext db;

    public FolderDbService(ChatDbContext dbContext) // Konstruktor: DB-Context per Dependency Injection
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
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync(); // Alle Ordner ohne Tracking laden
        allFolders.Sort(CompareFoldersByNameAsc); // Sortiere alphabetisch aufsteigend
        return allFolders;
    }

    public async Task<Folder?> GetFolderByIdAsync(int id) // Ordner per ID
    {
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync(); // Alle Ordner laden
        foreach (Folder f in allFolders) // Gewünschten Ordner suchen
        {
            if (f.Id == id) return f; // Gefunden
        }
        return null; // Nicht gefunden
    }

    public async Task<Folder?> GetFolderByNameAsync(string name) // Ordner per Name (case-insensitive)
    {
        string lowerName = name.ToLower().Trim(); // Suchname normalisieren
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync(); // Alle Ordner laden
        foreach (Folder f in allFolders) // Case-insensitiver Vergleich
        {
            if (f.Name.ToLower() == lowerName) return f; // Gefunden
        }
        return null; // Nicht gefunden
    }

    public async Task<Folder> CreateFolderAsync(string name) // Neuen Ordner anlegen
    {
        Folder folder = new Folder();
        folder.Name = name.Trim(); // Name bereinigen
        folder.CreatedAt = DateTime.UtcNow;
        this.db.Folders.Add(folder); // Zur Datenbank hinzufügen
        await this.db.SaveChangesAsync(); // Speichern
        return folder; // Ordner mit generierter ID zurückgeben
    }

    public async Task DeleteFolderAsync(int id) // Ordner löschen; exklusive Docs aus DB, Kopien bleiben erhalten
    {
        List<Document> docsInFolder = await this.GetDocumentsInFolderAsync(id); // Dokumente im Ordner ermitteln

        foreach (Document doc in docsInFolder) // Jeden Dokument-Eintrag prüfen
        {
            bool hasOtherCopy = doc.IsInRoot; // Im Hauptbereich ist immer eine Kopie
            if (!hasOtherCopy) // Noch nicht sicher ob andere Kopie vorhanden
            {
                var otherLinks = await this.db.DocumentFolders
                    .AsNoTracking()
                    .Where(df => df.DocumentId == doc.Id && df.FolderId != id) // Links in anderen Ordnern suchen
                    .ToListAsync();
                if (otherLinks.Count > 0) hasOtherCopy = true; // Dokument existiert in anderem Ordner
            }

            if (!hasOtherCopy) // Letzte Kopie → Dokument komplett löschen
            {
                Document? tracked = await this.db.Documents.FindAsync(doc.Id); // Tracked Entity holen
                if (tracked != null) this.db.Documents.Remove(tracked); // Zum Löschen markieren
            }
        }

        await this.db.SaveChangesAsync(); // Dokument-Löschungen speichern

        Folder? toDelete = await this.db.Folders.FindAsync(id); // Ordner-Entity laden
        if (toDelete != null) // Ordner gefunden?
        {
            this.db.Folders.Remove(toDelete); // Ordner löschen (Junction-Einträge per CASCADE)
            await this.db.SaveChangesAsync(); // Speichern
        }
    }

    // --- Dokument-Standort-Abfragen ---

    public async Task<List<Document>> GetRootDocumentsAsync() // Dokumente im Hauptbereich (IsInRoot = true)
    {
        var result = await this.db.Documents
            .AsNoTracking()
            .Where(d => d.IsInRoot) // Nur Dokumente im Root-Bereich
            .ToListAsync();
        result.Sort(CompareDocumentsByUploadedAtDesc); // Sortiere nach Datum (neueste zuerst)
        return result;
    }

    public async Task<List<Document>> GetDocumentsInFolderAsync(int folderId) // Dokumente in einem Ordner
    {
        var docIds = await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.FolderId == folderId) // Nur Links dieses Ordners
            .Select(df => df.DocumentId) // Nur die Dokument-IDs extrahieren
            .ToListAsync();

        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync(); // Alle Dokumente laden
        var result = new List<Document>();
        foreach (Document d in allDocs) // Passende Dokumente herausfiltern
        {
            foreach (int id in docIds) // Gegen gefundene IDs prüfen
            {
                if (d.Id == id) { result.Add(d); break; } // Gefunden → hinzufügen
            }
        }
        result.Sort(CompareDocumentsByUploadedAtDesc); // Sortiere nach Datum (neueste zuerst)
        return result;
    }

    public async Task<List<int>> GetDocumentFolderIdsAsync(int documentId) // Ordner-IDs eines Dokuments
    {
        return await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.DocumentId == documentId) // Links dieses Dokuments
            .Select(df => df.FolderId) // Nur Ordner-IDs extrahieren
            .ToListAsync();
    }

    // --- Dokument-Standort-Operationen ---

    public async Task MoveDocumentToFolderAsync(int documentId, int? targetFolderId) // Verschieben: nur noch im Ziel
    {
        Document? doc = await this.db.Documents.FindAsync(documentId); // Dokument aus DB holen
        if (doc == null) return; // Nicht gefunden → abbrechen

        var existing = await this.db.DocumentFolders
            .Where(df => df.DocumentId == documentId) // Alle bisherigen Ordner-Links dieses Dokuments
            .ToListAsync();
        this.db.DocumentFolders.RemoveRange(existing); // Alle alten Links entfernen (Verschieben ≠ Kopieren)

        if (targetFolderId.HasValue) // Ziel-Ordner angegeben?
        {
            doc.IsInRoot = false; // Aus Root entfernen
            DocumentFolder link = new DocumentFolder();
            link.DocumentId = documentId;
            link.FolderId = targetFolderId.Value;
            this.db.DocumentFolders.Add(link); // Neuen Link zum Ziel-Ordner anlegen
        }
        else // Kein Ordner → in Root verschieben
        {
            doc.IsInRoot = true;
        }

        await this.db.SaveChangesAsync(); // Änderungen speichern
    }

    public async Task<bool> CopyDocumentToFolderAsync(int documentId, int targetFolderId) // Kopieren in Ordner
    {
        bool exists = await this.db.DocumentFolders
            .AsNoTracking()
            .AnyAsync(df => df.DocumentId == documentId && df.FolderId == targetFolderId); // Bereits im Ordner?

        if (exists) return false; // Bereits vorhanden → nichts tun

        DocumentFolder link = new DocumentFolder();
        link.DocumentId = documentId;
        link.FolderId = targetFolderId;
        this.db.DocumentFolders.Add(link); // Neuen Link anlegen
        await this.db.SaveChangesAsync(); // Speichern
        return true; // Erfolgreich kopiert
    }

    public async Task CopyDocumentToRootAsync(int documentId) // IsInRoot = true setzen
    {
        Document? doc = await this.db.Documents.FindAsync(documentId); // Dokument suchen
        if (doc == null) return; // Nicht gefunden → abbrechen
        doc.IsInRoot = true; // In Root markieren
        await this.db.SaveChangesAsync();
    }

    public async Task SetDocumentInRootAsync(int documentId, bool inRoot) // IsInRoot direkt setzen
    {
        Document? doc = await this.db.Documents.FindAsync(documentId); // Dokument suchen
        if (doc == null) return; // Nicht gefunden → abbrechen
        doc.IsInRoot = inRoot; // Root-Status setzen
        await this.db.SaveChangesAsync();
    }

    public async Task<bool> DeleteDocumentFromLocationAsync(int documentId, int? folderId) // Von Standort entfernen; löscht aus DB wenn letzte Kopie
    {
        Document? doc = await this.db.Documents.FindAsync(documentId); // Dokument suchen
        if (doc == null) return false; // Nicht gefunden → abbrechen

        if (folderId == null) // Aus Root entfernen?
        {
            doc.IsInRoot = false;
        }
        else // Aus Ordner entfernen
        {
            var link = await this.db.DocumentFolders
                .FirstOrDefaultAsync(df => df.DocumentId == documentId && df.FolderId == folderId.Value); // Link im Ordner suchen
            if (link != null) this.db.DocumentFolders.Remove(link); // Link entfernen
        }

        await this.db.SaveChangesAsync(); // Standort-Änderung speichern

        Document? reloaded = await this.db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId); // Aktuellen Zustand prüfen
        bool stillInRoot = false;
        if (reloaded != null) stillInRoot = reloaded.IsInRoot; // Root-Status auslesen

        int remainingLinks = await this.db.DocumentFolders
            .AsNoTracking()
            .CountAsync(df => df.DocumentId == documentId); // Verbleibende Ordner-Links zählen

        if (!stillInRoot && remainingLinks == 0) // Letzte Kopie gelöscht?
        {
            Document? toDelete = await this.db.Documents.FindAsync(documentId); // Tracked Entity holen
            if (toDelete != null) this.db.Documents.Remove(toDelete); // Dokument komplett löschen
            await this.db.SaveChangesAsync();
            return true; // Dokument wurde komplett gelöscht
        }

        return false; // Weitere Kopien vorhanden → nur Standort entfernt
    }

    public async Task<bool> RemoveDocumentFromFolderAsync(int documentId, int folderId) // Nur Link entfernen
    {
        var link = await this.db.DocumentFolders
            .FirstOrDefaultAsync(df => df.DocumentId == documentId && df.FolderId == folderId); // Link suchen
        if (link == null) return false; // Link nicht gefunden
        this.db.DocumentFolders.Remove(link); // Link entfernen
        await this.db.SaveChangesAsync();
        return true; // Erfolgreich entfernt
    }

    // --- Duplikat-Pruefung ---

    public async Task<bool> FolderNameExistsAsync(string name) // Prueft ob ein Ordnername bereits vergeben ist (global, case-insensitive)
    {
        string lower = name.ToLower().Trim(); // Suchname normalisieren
        var allFolders = await this.db.Folders.AsNoTracking().ToListAsync(); // Alle Ordner laden
        foreach (Folder f in allFolders) // Case-insensitiver Vergleich
        {
            if (f.Name.ToLower() == lower) return true; // Name bereits vergeben
        }
        return false; // Name frei
    }

    public async Task<bool> DocumentNameExistsInLocationAsync(string fileName, int? folderId) // Prueft ob exakter Dateiname an diesem Standort existiert
    {
        string lower = fileName.ToLower(); // Dateiname normalisieren

        if (folderId == null) // Root-Bereich: alle Dokumente mit IsInRoot=true pruefen
        {
            var rootDocs = await this.db.Documents.AsNoTracking().Where(d => d.IsInRoot).ToListAsync(); // Root-Dokumente laden
            foreach (Document d in rootDocs)
            {
                if (d.FileName.ToLower() == lower) return true; // Gleicher Dateiname im Root gefunden
            }
            return false; // Name frei im Root
        }

        // Ordner: Dokument-IDs im Ordner laden, dann Name pruefen
        var docIdsInFolder = await this.db.DocumentFolders
            .AsNoTracking()
            .Where(df => df.FolderId == folderId.Value) // Links dieses Ordners
            .Select(df => df.DocumentId) // Nur Dokument-IDs
            .ToListAsync();

        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync(); // Alle Dokumente laden
        foreach (Document d in allDocs)
        {
            if (d.FileName.ToLower() != lower) continue; // Anderer Name → überspringen
            foreach (int id in docIdsInFolder)
            {
                if (id == d.Id) return true; // Gleicher Name im selben Ordner gefunden
            }
        }
        return false; // Name frei in diesem Ordner
    }

    // --- Ordner umbenennen ---

    public async Task<bool> RenameFolderAsync(int folderId, string newName) // Ordner umbenennen per ID
    {
        Folder? folder = await this.db.Folders.FindAsync(folderId); // Ordner suchen
        if (folder == null) return false; // Nicht gefunden
        folder.Name = newName.Trim(); // Neuen Namen setzen (bereinigt)
        await this.db.SaveChangesAsync();
        return true; // Erfolgreich umbenannt
    }

    // --- Dokument komplett loeschen ---

    public async Task<bool> DeleteDocumentCompletelyAsync(int documentId) // Entfernt Dokument aus allen Standorten und aus der DB
    {
        var links = await this.db.DocumentFolders
            .Where(df => df.DocumentId == documentId) // Alle Links dieses Dokuments
            .ToListAsync();
        this.db.DocumentFolders.RemoveRange(links); // Alle Junction-Eintraege entfernen

        Document? doc = await this.db.Documents.FindAsync(documentId); // Dokument-Entity holen
        if (doc == null) return false; // Nicht gefunden

        this.db.Documents.Remove(doc); // Dokument aus DB entfernen
        await this.db.SaveChangesAsync();
        return true; // Erfolgreich gelöscht
    }

    // --- Suche ---

    public async Task<List<Document>> FindDocumentsByNameAsync(string searchName) // Alle Dokumente die den Suchbegriff im Namen enthalten
    {
        string lower = searchName.ToLower().Trim(); // Suchbegriff normalisieren
        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync(); // Alle Dokumente laden
        var result = new List<Document>();
        foreach (Document d in allDocs) // Case-insensitive Namens-Suche
        {
            if (d.FileName.ToLower().Contains(lower)) result.Add(d); // Treffer hinzufügen
        }
        return result;
    }

    public async Task<Document?> FindDocumentByNameAsync(string searchName) // Erstes passendes Dokument (oder null)
    {
        var found = await this.FindDocumentsByNameAsync(searchName); // Suche ausführen
        if (found.Count == 0) return null; // Kein Treffer
        return found[0]; // Ersten Treffer zurückgeben
    }

    public async Task<Document?> GetDocumentByIdAsync(int documentId) // Dokument per exakter ID laden
    {
        var allDocs = await this.db.Documents.AsNoTracking().ToListAsync(); // Alle Dokumente laden
        foreach (Document d in allDocs) // Passendes Dokument suchen
        {
            if (d.Id == documentId) return d; // Gefunden
        }
        return null; // Nicht gefunden
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
