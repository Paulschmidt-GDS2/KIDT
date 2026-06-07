using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Services.McpTools;

[McpServerToolType]
public class FolderTools // MCP-Tools für Ordner-Verwaltung (create, delete, move, copy, list)
{
    private readonly FolderDbService folderDbService;

    public FolderTools(FolderDbService folderDbService)
    {
        this.folderDbService = folderDbService;
    }

    [McpServerTool]
    [Description("Erstellt einen neuen Ordner für Dokumente. NUR aufrufen wenn User explizit einen Ordner erstellen möchte.")]
    public async Task<string> CreateFolder(
        [Description("Name des neuen Ordners")] string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ordnername darf nicht leer sein." });
            }

            bool exists = await this.folderDbService.FolderNameExistsAsync(name); // Duplikat-Pruefung
            if (exists)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ein Ordner mit dem Namen '{name}' existiert bereits." });
            }

            Folder folder = await this.folderDbService.CreateFolderAsync(name);
            return JsonSerializer.Serialize(new { success = true, message = $"Ordner '{folder.Name}' wurde erstellt.", folderId = folder.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] CreateFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Löscht einen Ordner inklusive aller darin enthaltenen Dokumente. Dokumente die auch woanders liegen bleiben erhalten.")]
    public async Task<string> DeleteFolder(
        [Description("Name des zu löschenden Ordners")] string name)
    {
        try
        {
            Folder? folder = await this.folderDbService.GetFolderByNameAsync(name);
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{name}' nicht gefunden." });
            }

            List<Document> docsInFolder = await this.folderDbService.GetDocumentsInFolderAsync(folder.Id);
            int docCount = docsInFolder.Count;

            await this.folderDbService.DeleteFolderAsync(folder.Id);
            return JsonSerializer.Serialize(new { success = true, message = $"Ordner '{name}' und {docCount} Dokument(e) wurden gelöscht." });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] DeleteFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Verschiebt ein Dokument an einen Standort — es ist danach NUR noch dort. folderName='hauptbereich' = nur in Root.")]
    public async Task<string> MoveDocumentToFolder(
        [Description("Dateiname oder Teil des Dateinamens")] string documentName,
        [Description("Ziel-Ordnername oder 'hauptbereich' für Root")] string folderName)
    {
        try
        {
            Document? doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            if (doc == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit '{documentName}' gefunden." });
            }

            int? targetFolderId = null;
            string lower = folderName.ToLower().Trim();

            if (lower != "root" && lower != "hauptbereich")
            {
                Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName);
                if (folder == null)
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
                }
                targetFolderId = folder.Id;
            }

            bool nameConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, targetFolderId); // Duplikat am Ziel pruefen
            if (nameConflict)
            {
                string targetLabel = targetFolderId.HasValue ? $"Ordner '{folderName}'" : "Hauptbereich";
                return JsonSerializer.Serialize(new { success = false, message = $"Im {targetLabel} existiert bereits eine Datei mit dem Namen '{doc.FileName}'." });
            }

            await this.folderDbService.MoveDocumentToFolderAsync(doc.Id, targetFolderId);
            string target = targetFolderId.HasValue ? $"Ordner '{folderName}'" : "Hauptbereich";
            return JsonSerializer.Serialize(new { success = true, message = $"'{doc.FileName}' wurde in {target} verschoben.", documentId = doc.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] MoveDocumentToFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Kopiert ein Dokument an einen Standort. Bleibt überall wo es bereits liegt. folderName='hauptbereich' = auch in Root sichtbar.")]
    public async Task<string> CopyDocumentToFolder(
        [Description("Dateiname oder Teil des Dateinamens")] string documentName,
        [Description("Ziel-Ordnername oder 'hauptbereich' für Root")] string folderName)
    {
        try
        {
            Document? doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            if (doc == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit '{documentName}' gefunden." });
            }

            string lower = folderName.ToLower().Trim();

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner")
            {
                if (doc.IsInRoot)
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"'{doc.FileName}' ist bereits im Hauptbereich." });
                }
                bool rootConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, null); // Duplikat im Hauptbereich pruefen
                if (rootConflict)
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Im Hauptbereich existiert bereits eine Datei mit dem Namen '{doc.FileName}'." });
                }
                await this.folderDbService.CopyDocumentToRootAsync(doc.Id);
                return JsonSerializer.Serialize(new { success = true, message = $"'{doc.FileName}' ist jetzt auch im Hauptbereich sichtbar.", documentId = doc.Id });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName);
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool folderConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, folder.Id); // Duplikat im Zielordner pruefen
            if (folderConflict)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"In Ordner '{folderName}' existiert bereits eine Datei mit dem Namen '{doc.FileName}'." });
            }

            bool copied = await this.folderDbService.CopyDocumentToFolderAsync(doc.Id, folder.Id);
            if (!copied)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"'{doc.FileName}' ist bereits in Ordner '{folderName}'." });
            }
            return JsonSerializer.Serialize(new { success = true, message = $"'{doc.FileName}' ist jetzt auch in Ordner '{folderName}' sichtbar.", documentId = doc.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] CopyDocumentToFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Entfernt ein Dokument von einem Standort. folderName='hauptbereich' = aus Root entfernen. Letzte Kopie → wird aus DB gelöscht.")]
    public async Task<string> RemoveDocumentFromFolder(
        [Description("Dateiname oder Teil des Dateinamens")] string documentName,
        [Description("Ordnername ODER 'hauptbereich' um aus Root zu entfernen")] string folderName)
    {
        try
        {
            Document? doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            if (doc == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit '{documentName}' gefunden." });
            }

            string lower = folderName.ToLower().Trim();

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner")
            {
                bool wasDeleted = await this.folderDbService.DeleteDocumentFromLocationAsync(doc.Id, null);
                string msg = wasDeleted
                    ? $"'{doc.FileName}' wurde vollständig gelöscht (war nur im Hauptbereich)."
                    : $"'{doc.FileName}' wurde aus dem Hauptbereich entfernt und ist noch in anderen Ordnern vorhanden.";
                return JsonSerializer.Serialize(new { success = true, message = msg, documentId = doc.Id });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName);
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool removed = await this.folderDbService.DeleteDocumentFromLocationAsync(doc.Id, folder.Id);
            string resultMsg = removed
                ? $"'{doc.FileName}' wurde vollständig gelöscht (war nur in '{folderName}')."
                : $"'{doc.FileName}' wurde aus '{folderName}' entfernt und ist noch an anderen Standorten vorhanden.";
            return JsonSerializer.Serialize(new { success = true, message = resultMsg, documentId = doc.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] RemoveDocumentFromFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Zeigt in welchen Ordnern eine bekannte Datei liegt. Aufrufen wenn User nach dem Speicherort fragt ('Wo liegt', 'In welchem Ordner') — NICHT fuer die Suche ob eine Datei existiert.")]
    public async Task<string> FindDocuments(
        [Description("Dateiname oder Teil des Dateinamens")] string documentName)
    {
        List<Document> docs = await this.folderDbService.FindDocumentsByNameAsync(documentName);

        if (docs.Count == 0)
        {
            return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit '{documentName}' gefunden." });
        }

        List<object> docList = new List<object>();
        List<Folder> allFolders = await this.folderDbService.GetAllFoldersAsync();

        foreach (Document d in docs)
        {
            List<int> folderIds = await this.folderDbService.GetDocumentFolderIdsAsync(d.Id);
            List<string> folderNames = new List<string>();

            if (d.IsInRoot) folderNames.Add("Hauptbereich");

            foreach (int fid in folderIds)
            {
                foreach (Folder f in allFolders)
                {
                    if (f.Id == fid) { folderNames.Add(f.Name); break; }
                }
            }

            if (folderNames.Count == 0) folderNames.Add("Hauptbereich");

            docList.Add(new { id = d.Id, fileName = d.FileName, fileType = d.FileType, inFolders = folderNames });
        }

        return JsonSerializer.Serialize(new { success = true, found = docs.Count, documents = docList });
    }

    [McpServerTool]
    [Description("Zeigt alle Dokumente in einem bestimmten Ordner an. 'hauptbereich' oder 'root' = Dokumente im Hauptbereich (ohne Ordner).")]
    public async Task<string> ListDocumentsInFolder(
        [Description("Name des Ordners ODER 'hauptbereich'/'root' für Dokumente ohne Ordner")] string folderName)
    {
        try
        {
            string lower = folderName.ToLower().Trim();

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner") // Root-Bereich: alle Dokumente mit IsInRoot=true
            {
                List<Document> rootDocs = await this.folderDbService.GetRootDocumentsAsync();

                if (rootDocs.Count == 0)
                {
                    return JsonSerializer.Serialize(new { success = true, found = 0, message = "Keine Dokumente im Hauptbereich.", documents = new List<object>() });
                }

                List<object> rootList = new List<object>();
                foreach (Document d in rootDocs)
                {
                    rootList.Add(new { id = d.Id, fileName = d.FileName, fileType = d.FileType });
                }

                return JsonSerializer.Serialize(new { success = true, found = rootDocs.Count, message = $"{rootDocs.Count} Dokument(e) im Hauptbereich.", documents = rootList });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName);
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            List<Document> docs = await this.folderDbService.GetDocumentsInFolderAsync(folder.Id);

            if (docs.Count == 0)
            {
                return JsonSerializer.Serialize(new { success = true, found = 0, message = $"Keine Dokumente in Ordner '{folderName}'.", documents = new List<object>() });
            }

            List<object> docList = new List<object>();
            foreach (Document d in docs)
            {
                docList.Add(new { id = d.Id, fileName = d.FileName, fileType = d.FileType });
            }

            return JsonSerializer.Serialize(new { success = true, found = docs.Count, message = $"{docs.Count} Dokument(e) in Ordner '{folderName}'.", documents = docList });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] ListDocumentsInFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Benennt einen bestehenden Ordner um. Aufrufen wenn User einen Ordner umbenennen möchte.")]
    public async Task<string> RenameFolder(
        [Description("Aktueller Name des Ordners")] string folderName,
        [Description("Neuer Name des Ordners")] string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                return JsonSerializer.Serialize(new { success = false, message = "Neuer Ordnername darf nicht leer sein." });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName); // Name -> ID auflösen
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool nameExists = await this.folderDbService.FolderNameExistsAsync(newName); // Neuer Name bereits vergeben?
            if (nameExists)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ein Ordner mit dem Namen '{newName}' existiert bereits." });
            }

            bool success = await this.folderDbService.RenameFolderAsync(folder.Id, newName); // Umbenennen per ID
            if (!success)
            {
                return JsonSerializer.Serialize(new { success = false, message = "Umbenennen fehlgeschlagen." });
            }

            return JsonSerializer.Serialize(new { success = true, message = $"Ordner '{folderName}' wurde in '{newName}' umbenannt." });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] RenameFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Listet alle vorhandenen Ordner auf. Aufrufen wenn User fragt 'Welche Ordner gibt es', 'Zeige mir die Ordner', 'Was für Ordner habe ich'.")]
    public async Task<string> ListAllFolders() // Tool: Gibt Liste aller Ordner mit Dokumentenanzahl zurück
    {
        try
        {
            List<Folder> folders = await this.folderDbService.GetAllFoldersAsync();

            List<object> folderList = new List<object>();
            foreach (Folder f in folders) // Durchlaufe alle Ordner und lade Dokumentenanzahl
            {
                List<Document> docs = await this.folderDbService.GetDocumentsInFolderAsync(f.Id);
                folderList.Add(new { id = f.Id, name = f.Name, documentCount = docs.Count });
            }

            string msg = folders.Count == 0 ? "Keine Ordner vorhanden." : $"{folders.Count} Ordner gefunden.";
            return JsonSerializer.Serialize(new { success = true, found = folders.Count, message = msg, folders = folderList });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] ListAllFolders Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }
}
