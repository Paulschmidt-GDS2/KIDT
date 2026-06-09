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

    public FolderTools(FolderDbService folderDbService) // Konstruktor: FolderDbService per DI injizieren
    {
        this.folderDbService = folderDbService;
    }

    [McpServerTool]
    [Description("Erstellt einen neuen Ordner für Dokumente.")]
    public async Task<string> CreateFolder( // Tool: Legt neuen Ordner in DB an, prüft Duplikat
        [Description("Name des neuen Ordners")] string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) // Leerer Name?
            {
                return JsonSerializer.Serialize(new { success = false, message = "Ordnername darf nicht leer sein." });
            }

            bool exists = await this.folderDbService.FolderNameExistsAsync(name); // Duplikat-Prüfung
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
    [Description("Löscht einen Ordner inklusive aller darin enthaltenen Dokumente. Dokumente die auch an anderen Standorten liegen bleiben dort erhalten.")]
    public async Task<string> DeleteFolder( // Tool: Löscht Ordner und alle exklusiven Dokumente aus DB
        [Description("Name des zu löschenden Ordners")] string name)
    {
        try
        {
            Folder? folder = await this.folderDbService.GetFolderByNameAsync(name); // Ordner per Name suchen
            if (folder == null) // Ordner nicht gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{name}' nicht gefunden." });
            }

            List<Document> docsInFolder = await this.folderDbService.GetDocumentsInFolderAsync(folder.Id); // Dokumentanzahl für Rückmeldung
            int docCount = docsInFolder.Count;

            await this.folderDbService.DeleteFolderAsync(folder.Id); // Ordner und Inhalt aus DB entfernen
            return JsonSerializer.Serialize(new { success = true, message = $"Ordner '{name}' und {docCount} Dokument(e) wurden gelöscht." });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] DeleteFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Verschiebt ein Dokument zu einem Standort — danach ist es NUR noch dort, alle bisherigen Standorte werden entfernt. folderName='hauptbereich' = in den Root-Bereich ohne Ordner. Verwende documentId (int) wenn die ID aus einem früheren Suchergebnis bekannt ist, sonst documentName.")]
    public async Task<string> MoveDocumentToFolder( // Tool: Entfernt Dokument von allen Standorten und setzt es ans Ziel
        [Description("Ziel-Ordnername oder 'hauptbereich' für Root")] string folderName,
        [Description("Dateiname oder Teil des Dateinamens — nur wenn documentId nicht bekannt")] string documentName = "",
        [Description("Dokument-ID (int) aus einem Suchergebnis — hat Vorrang vor documentName")] int documentId = -1)
    {
        try
        {
            Document? doc = null;
            if (documentId >= 0) // ID bekannt? → direkt laden (präziser als Namenssuche)
            {
                doc = await this.folderDbService.GetDocumentByIdAsync(documentId);
            }
            if (doc == null && !string.IsNullOrEmpty(documentName)) // Fallback: per Name suchen
            {
                doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            }
            if (doc == null) // Dokument nicht gefunden?
            {
                string searchInfo = documentId >= 0 ? $"ID {documentId}" : $"'{documentName}'"; // Fehlermeldung kontextuell
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit {searchInfo} gefunden." });
            }

            int? targetFolderId = null;
            string lower = folderName.ToLower().Trim(); // Normalisieren für Alias-Vergleich

            if (lower != "root" && lower != "hauptbereich") // Kein Root-Alias → Ordner suchen
            {
                Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName); // Zielordner aus DB laden
                if (folder == null) // Ordner nicht gefunden?
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
                }
                targetFolderId = folder.Id;
            }

            bool nameConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, targetFolderId); // Duplikat am Ziel prüfen
            if (nameConflict) // Namenskonflikt?
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
    [Description("Legt ein Dokument zusätzlich an einem weiteren Standort ab — es bleibt überall wo es bereits liegt (kein Entfernen bestehender Standorte). folderName='hauptbereich' = auch in Root sichtbar. Verwende documentId (int) wenn die ID aus einem früheren Suchergebnis bekannt ist, sonst documentName.")]
    public async Task<string> CopyDocumentToFolder( // Tool: Fügt Dokument an Zielstandort hinzu, ohne es woanders zu entfernen
        [Description("Ziel-Ordnername oder 'hauptbereich' für Root")] string folderName,
        [Description("Dateiname oder Teil des Dateinamens — nur wenn documentId nicht bekannt")] string documentName = "",
        [Description("Dokument-ID (int) aus einem Suchergebnis — hat Vorrang vor documentName")] int documentId = -1)
    {
        try
        {
            Document? doc = null;
            if (documentId >= 0) // ID bekannt? → direkt laden
            {
                doc = await this.folderDbService.GetDocumentByIdAsync(documentId);
            }
            if (doc == null && !string.IsNullOrEmpty(documentName)) // Fallback: per Name suchen
            {
                doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            }
            if (doc == null) // Dokument nicht gefunden?
            {
                string searchInfo = documentId >= 0 ? $"ID {documentId}" : $"'{documentName}'";
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit {searchInfo} gefunden." });
            }

            string lower = folderName.ToLower().Trim(); // Normalisieren für Alias-Vergleich

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner") // Root-Alias erkannt?
            {
                if (doc.IsInRoot) // Bereits im Hauptbereich?
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"'{doc.FileName}' ist bereits im Hauptbereich." });
                }
                bool rootConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, null); // Duplikat im Hauptbereich prüfen
                if (rootConflict)
                {
                    return JsonSerializer.Serialize(new { success = false, message = $"Im Hauptbereich existiert bereits eine Datei mit dem Namen '{doc.FileName}'." });
                }
                await this.folderDbService.CopyDocumentToRootAsync(doc.Id); // Dokument in Root kopieren
                return JsonSerializer.Serialize(new { success = true, message = $"'{doc.FileName}' ist jetzt auch im Hauptbereich sichtbar.", documentId = doc.Id });
            }

            Folder? targetFolder = await this.folderDbService.GetFolderByNameAsync(folderName); // Zielordner aus DB laden
            if (targetFolder == null) // Ordner nicht gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool folderConflict = await this.folderDbService.DocumentNameExistsInLocationAsync(doc.FileName, targetFolder.Id); // Duplikat im Zielordner prüfen
            if (folderConflict)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"In Ordner '{folderName}' existiert bereits eine Datei mit dem Namen '{doc.FileName}'." });
            }

            bool copied = await this.folderDbService.CopyDocumentToFolderAsync(doc.Id, targetFolder.Id); // Dokument in Zielordner kopieren
            if (!copied) // Kopieren fehlgeschlagen (bereits vorhanden)?
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
    [Description("Entfernt ein Dokument von einem bestimmten Standort. Ist es an keinem anderen Standort mehr vorhanden, wird es vollständig aus der Bibliothek gelöscht. folderName='hauptbereich' = aus Root entfernen. Verwende documentId (int) wenn die ID bekannt ist, sonst documentName.")]
    public async Task<string> RemoveDocumentFromFolder( // Tool: Entfernt Dokument nur von angegebenem Standort; löscht aus DB wenn letzte Kopie
        [Description("Ordnername ODER 'hauptbereich' um aus Root zu entfernen")] string folderName,
        [Description("Dateiname oder Teil des Dateinamens — nur wenn documentId nicht bekannt")] string documentName = "",
        [Description("Dokument-ID (int) aus einem Suchergebnis — hat Vorrang vor documentName")] int documentId = -1)
    {
        try
        {
            Document? doc = null;
            if (documentId >= 0) // ID bekannt? → direkt laden
            {
                doc = await this.folderDbService.GetDocumentByIdAsync(documentId);
            }
            if (doc == null && !string.IsNullOrEmpty(documentName)) // Fallback: per Name suchen
            {
                doc = await this.folderDbService.FindDocumentByNameAsync(documentName);
            }
            if (doc == null) // Dokument nicht gefunden?
            {
                string searchInfo = documentId >= 0 ? $"ID {documentId}" : $"'{documentName}'";
                return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit {searchInfo} gefunden." });
            }

            string lower = folderName.ToLower().Trim(); // Normalisieren für Alias-Vergleich

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner") // Root-Alias erkannt?
            {
                bool wasDeleted = await this.folderDbService.DeleteDocumentFromLocationAsync(doc.Id, null); // Aus Root entfernen
                string msg;
                if (wasDeleted) // Letzte Kopie → vollständig gelöscht
                {
                    msg = $"'{doc.FileName}' wurde vollständig gelöscht (war nur im Hauptbereich).";
                }
                else // Noch in anderen Ordnern vorhanden
                {
                    msg = $"'{doc.FileName}' wurde aus dem Hauptbereich entfernt und ist noch in anderen Ordnern vorhanden.";
                }
                return JsonSerializer.Serialize(new { success = true, message = msg, documentId = doc.Id });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName); // Zielordner aus DB laden
            if (folder == null) // Ordner nicht gefunden?
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool removed = await this.folderDbService.DeleteDocumentFromLocationAsync(doc.Id, folder.Id); // Aus Ordner entfernen
            string resultMsg;
            if (removed) // Letzte Kopie → vollständig gelöscht
            {
                resultMsg = $"'{doc.FileName}' wurde vollständig gelöscht (war nur in '{folderName}').";
            }
            else // Noch an anderen Standorten vorhanden
            {
                resultMsg = $"'{doc.FileName}' wurde aus '{folderName}' entfernt und ist noch an anderen Standorten vorhanden.";
            }
            return JsonSerializer.Serialize(new { success = true, message = resultMsg, documentId = doc.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FOLDER_TOOLS] RemoveDocumentFromFolder Fehler: {ex.Message}");
            return JsonSerializer.Serialize(new { success = false, message = $"Fehler: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Zeigt an welchen Standorten (Ordner, Hauptbereich) ein Dokument liegt. Verwende documentId (int) wenn bekannt, sonst documentName.")]
    public async Task<string> FindDocuments( // Tool: Gibt alle Standorte (Root + Ordner) eines Dokuments zurück
        [Description("Dateiname oder Teil des Dateinamens (wenn documentId nicht bekannt)")] string documentName = "",
        [Description("Dokument-ID (int) aus einem Suchergebnis — hat Vorrang vor documentName")] int documentId = -1)
    {
        List<Document> docs = new List<Document>();

        if (documentId >= 0) // ID bekannt? → direkt laden
        {
            Document? byId = await this.folderDbService.GetDocumentByIdAsync(documentId);
            if (byId != null) docs.Add(byId); // Gefunden → zur Liste hinzufügen
        }
        if (docs.Count == 0 && !string.IsNullOrEmpty(documentName)) // Fallback: per Name suchen
        {
            docs = await this.folderDbService.FindDocumentsByNameAsync(documentName);
        }

        if (docs.Count == 0) // Kein Treffer?
        {
            string searchInfo = documentId >= 0 ? $"ID {documentId}" : $"'{documentName}'";
            return JsonSerializer.Serialize(new { success = false, message = $"Kein Dokument mit {searchInfo} gefunden." });
        }

        List<object> docList = new List<object>();
        List<Folder> allFolders = await this.folderDbService.GetAllFoldersAsync(); // Alle Ordner für Namensauflösung laden

        foreach (Document d in docs) // Für jedes gefundene Dokument: Standorte ermitteln
        {
            List<int> folderIds = await this.folderDbService.GetDocumentFolderIdsAsync(d.Id); // Ordner-IDs des Dokuments
            List<string> folderNames = new List<string>();

            if (d.IsInRoot) folderNames.Add("Hauptbereich"); // Dokument liegt in Root?

            foreach (int fid in folderIds) // Ordner-IDs zu Namen auflösen
            {
                foreach (Folder f in allFolders)
                {
                    if (f.Id == fid) { folderNames.Add(f.Name); break; } // Ordnername gefunden
                }
            }

            if (folderNames.Count == 0) folderNames.Add("Hauptbereich"); // Fallback: Root annehmen

            docList.Add(new { id = d.Id, fileName = d.FileName, fileType = d.FileType, inFolders = folderNames });
        }

        return JsonSerializer.Serialize(new { success = true, found = docs.Count, documents = docList });
    }

    [McpServerTool]
    [Description("Zeigt alle Dokumente in einem bestimmten Ordner. 'hauptbereich' oder 'root' = Dokumente im Hauptbereich (ohne Ordner).")]
    public async Task<string> ListDocumentsInFolder( // Tool: Gibt alle Dokumente eines Ordners oder des Hauptbereichs zurück
        [Description("Name des Ordners ODER 'hauptbereich'/'root' für Dokumente ohne Ordner")] string folderName)
    {
        try
        {
            string lower = folderName.ToLower().Trim(); // Normalisieren für Alias-Vergleich

            if (lower == "hauptbereich" || lower == "root" || lower == "hauptordner") // Root-Bereich
            {
                List<Document> rootDocs = await this.folderDbService.GetRootDocumentsAsync(); // Root-Dokumente laden

                if (rootDocs.Count == 0)
                {
                    return JsonSerializer.Serialize(new { success = true, found = 0, message = "Keine Dokumente im Hauptbereich.", documents = new List<object>() });
                }

                List<object> rootList = new List<object>();
                foreach (Document d in rootDocs) // Durchlaufe Root-Dokumente
                {
                    rootList.Add(new { id = d.Id, fileName = d.FileName, fileType = d.FileType });
                }

                return JsonSerializer.Serialize(new { success = true, found = rootDocs.Count, message = $"{rootDocs.Count} Dokument(e) im Hauptbereich.", documents = rootList });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName); // Ordner aus DB laden
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            List<Document> docs = await this.folderDbService.GetDocumentsInFolderAsync(folder.Id); // Dokumente im Ordner laden

            if (docs.Count == 0)
            {
                return JsonSerializer.Serialize(new { success = true, found = 0, message = $"Keine Dokumente in Ordner '{folderName}'.", documents = new List<object>() });
            }

            List<object> docList = new List<object>();
            foreach (Document d in docs) // Durchlaufe Ordner-Dokumente
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
    [Description("Benennt einen bestehenden Ordner um.")]
    public async Task<string> RenameFolder( // Tool: Ändert den Namen eines Ordners, prüft Duplikat
        [Description("Aktueller Name des Ordners")] string folderName,
        [Description("Neuer Name des Ordners")] string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                return JsonSerializer.Serialize(new { success = false, message = "Neuer Ordnername darf nicht leer sein." });
            }

            Folder? folder = await this.folderDbService.GetFolderByNameAsync(folderName); // Name → ID auflösen
            if (folder == null)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ordner '{folderName}' nicht gefunden." });
            }

            bool nameExists = await this.folderDbService.FolderNameExistsAsync(newName); // Duplikat-Prüfung
            if (nameExists)
            {
                return JsonSerializer.Serialize(new { success = false, message = $"Ein Ordner mit dem Namen '{newName}' existiert bereits." });
            }

            bool success = await this.folderDbService.RenameFolderAsync(folder.Id, newName); // Umbenennen per ID in DB
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
    [Description("Listet alle vorhandenen Ordner mit ihrer Dokumentenanzahl auf.")]
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
