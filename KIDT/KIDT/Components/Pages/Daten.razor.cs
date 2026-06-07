using KIDT.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace KIDT.Components.Pages;

public partial class Daten // Chats und Dokumente verwalten (Code-Behind)
{
    private string currentTab = "Chats";
    private List<Conversation> Chats = new List<Conversation>();
    private List<Document> Documents = new List<Document>(); // Dokumente der aktuellen Ansicht
    private List<Folder> Folders = new List<Folder>();
    private int? currentFolderId = null;
    private string currentFolderName = string.Empty;
    private int? moveDropdownDocId = null;
    private int? selectedDocId = null; // Ausgewähltes Dokument (Einfachklick)
    private int? clipboardDocId = null; // Zwischenablage (Strg+C)
    private string clipboardDocName = string.Empty;
    private int? clipboardSourceFolderId = null; // Ordner aus dem kopiert wurde (null = Root)
    private bool isDeleting = false;
    private bool isDeletingDocument = false;
    private bool isDeletingFolder = false;
    private bool isLoadingChats = true;
    private bool isLoadingDocuments = true;
    private bool isCreatingFolder = false;
    private bool isUploading = false;
    private string newFolderName = string.Empty;
    private string folderCreateError = string.Empty; // Fehlermeldung bei doppeltem Ordnernamen
    private string uploadError = string.Empty; // Fehlermeldung bei doppeltem Dateinamen
    private List<int> removingChatIds = new List<int>();
    private List<int> removingDocumentIds = new List<int>();
    private List<int> removingFolderIds = new List<int>();
    private DotNetObjectReference<Daten>? dotnetRef;

    protected override async Task OnInitializedAsync() // Wird bei jeder Navigation zur Seite aufgerufen → immer frische Daten
    {
        await Task.Yield();
        Chats = await ChatDb.LoadAllConversationsAsync();
        isLoadingChats = false;
        Documents = await FolderDb.GetRootDocumentsAsync();
        Folders = await FolderDb.GetAllFoldersAsync();
        isLoadingDocuments = false;
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender) // JS-Clipboard beim ersten Render registrieren
    {
        if (firstRender)
        {
            dotnetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("clipboardHelper.register", dotnetRef);
        }
    }

    public void Dispose() // JS-Listener und DotNetReference beim Entfernen freigeben
    {
        JS.InvokeVoidAsync("clipboardHelper.dispose");
        dotnetRef?.Dispose();
    }

    // --- Upload ---

    private async Task OnUploadClick() // Datei-Dialog öffnen und Datei hochladen
    {
        try
        {
            isUploading = true;
            StateHasChanged();

            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Datei auswählen",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".pdf", ".txt", ".md", ".json" } }
                })
            });

            if (result == null) return; // Abgebrochen

            bool nameConflict = await FolderDb.DocumentNameExistsInLocationAsync(result.FileName, currentFolderId); // Duplikat-Pruefung am aktuellen Standort
            if (nameConflict)
            {
                string location;
                if (currentFolderId == null) // Aktueller Standort bestimmen
                {
                    location = "Hauptbereich";
                }
                else
                {
                    location = "diesem Ordner";
                }
                uploadError = $"Im {location} existiert bereits eine Datei '{result.FileName}'."; // Inline-Fehler setzen
                StateHasChanged();
                _ = Task.Run(async () => // Fehlermeldung nach 3s ausblenden
                {
                    await Task.Delay(3000);
                    await InvokeAsync(() => { uploadError = string.Empty; StateHasChanged(); });
                });
                return;
            }

            uploadError = string.Empty;
            string thumbnail = await ThumbGen.GenerateThumbnailAsync(result.FullPath); // Thumbnail generieren

            string ext = Path.GetExtension(result.FileName).ToLower();
            string fileType = "unknown"; // Dateityp bestimmen
            if (ext == ".pdf") fileType = "pdf";
            else if (ext == ".txt") fileType = "txt";
            else if (ext == ".md") fileType = "md";
            else if (ext == ".json") fileType = "json";

            string fileContent;
            if (fileType == "pdf") // PDF → Base64
            {
                byte[] pdfBytes = await File.ReadAllBytesAsync(result.FullPath);
                fileContent = Convert.ToBase64String(pdfBytes);
            }
            else // Textdatei → direkt lesen
            {
                fileContent = await File.ReadAllTextAsync(result.FullPath);
            }

            int docId = await DocDb.SaveDocumentAsync(result.FileName, fileContent, fileType, string.Empty, thumbnail);

            if (currentFolderId != null && docId > 0) // Upload in Ordner: IsInRoot = false, nur im Ordner
            {
                await FolderDb.SetDocumentInRootAsync(docId, false);
                await FolderDb.CopyDocumentToFolderAsync(docId, currentFolderId.Value);
            }
            // Upload in Root: IsInRoot = true (bereits durch SaveDocumentAsync gesetzt)

            await RefreshDocuments();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UPLOAD] Fehler: {ex.Message}");
        }
        finally
        {
            isUploading = false;
            StateHasChanged();
        }
    }

    // --- Clipboard ---

    private void SelectDocument(int docId) // Einfachklick: Dokument auswählen (Highlight)
    {
        if (selectedDocId == docId)
            selectedDocId = null; // Nochmaliger Klick hebt Auswahl auf
        else
            selectedDocId = docId;
        moveDropdownDocId = null; // Offenes Dropdown schließen
        StateHasChanged();
    }

    private bool CanPaste // Paste nur anzeigen wenn sinnvoll: anderer Standort, kein Namenskonflikt
    {
        get
        {
            if (clipboardDocId == null) return false;
            if (clipboardSourceFolderId == currentFolderId) return false; // Quelle == Ziel
            foreach (Document d in Documents) // Datei mit gleichem Namen bereits im Ziel?
            {
                if (d.FileName == clipboardDocName) return false;
            }
            return true;
        }
    }

    private void CopyToClipboard(int docId, string docName) // Dokument in Zwischenablage legen
    {
        clipboardDocId = docId;
        clipboardDocName = docName;
        clipboardSourceFolderId = currentFolderId; // Quell-Standort merken
        selectedDocId = docId;
        StateHasChanged();
    }

    private void ClearClipboard() // Zwischenablage leeren
    {
        clipboardDocId = null;
        clipboardDocName = string.Empty;
        clipboardSourceFolderId = null;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnCtrlC() // Strg+C: Ausgewähltes Dokument kopieren
    {
        if (selectedDocId == null) return;

        foreach (Document d in Documents)
        {
            if (d.Id == selectedDocId)
            {
                clipboardDocId = d.Id;
                clipboardDocName = d.FileName;
                clipboardSourceFolderId = currentFolderId; // Quell-Standort merken
                break;
            }
        }
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnCtrlV() // Strg+V: In aktuellen Bereich einfügen
    {
        if (clipboardDocId == null) return;
        await PasteDocumentHere();
    }

    private async Task PasteDocumentHere() // Kopiert Zwischenablage-Dokument in aktuellen Bereich
    {
        if (clipboardDocId == null) return;

        if (currentFolderId == null) // Einfügen in Root
        {
            await FolderDb.CopyDocumentToRootAsync(clipboardDocId.Value);
        }
        else // Einfügen in Ordner
        {
            await FolderDb.CopyDocumentToFolderAsync(clipboardDocId.Value, currentFolderId.Value);
        }

        ClearClipboard();
        await RefreshDocuments();
        StateHasChanged();
    }

    // --- Navigation ---

    private async Task OpenFolder(int folderId, string folderName) // In Ordner navigieren
    {
        currentFolderId = folderId;
        currentFolderName = folderName;
        isCreatingFolder = false;
        moveDropdownDocId = null;
        selectedDocId = null;
        Documents = await FolderDb.GetDocumentsInFolderAsync(folderId);
        StateHasChanged();
    }

    private async Task NavigateToRoot() // Zurück zur Root-Ansicht
    {
        currentFolderId = null;
        currentFolderName = string.Empty;
        isCreatingFolder = false;
        moveDropdownDocId = null;
        selectedDocId = null;
        await RefreshDocuments();
        Folders = await FolderDb.GetAllFoldersAsync();
        StateHasChanged();
    }

    private async Task RefreshDocuments() // Lädt Dokumente der aktuellen Ansicht neu
    {
        if (currentFolderId == null)
            Documents = await FolderDb.GetRootDocumentsAsync();
        else
            Documents = await FolderDb.GetDocumentsInFolderAsync(currentFolderId.Value);
    }

    // --- Verschieben ---

    private void ToggleMoveDropdown(int docId) // Öffnet/schließt Move-Dropdown für ein Dokument
    {
        if (moveDropdownDocId == docId)
            moveDropdownDocId = null;
        else
            moveDropdownDocId = docId;
    }

    private async Task MoveDocumentTo(int docId, int? targetFolderId) // Verschiebt Dokument an neuen Standort
    {
        moveDropdownDocId = null;
        await FolderDb.MoveDocumentToFolderAsync(docId, targetFolderId);
        await RefreshDocuments();
        StateHasChanged();
    }

    // --- Ordner-Verwaltung ---

    private void StartCreateFolder() // Neue Ordner-Erstellungszeile einblenden
    {
        isCreatingFolder = true;
        newFolderName = string.Empty;
        moveDropdownDocId = null;
    }

    private void CancelCreateFolder() // Ordner-Erstellung abbrechen
    {
        isCreatingFolder = false;
        newFolderName = string.Empty;
    }

    private void OnFolderNameInput(ChangeEventArgs e) // Ordner-Name-Eingabe verarbeiten
    {
        if (e.Value == null) // Kein Wert übergeben?
        {
            newFolderName = string.Empty;
            return;
        }
        string? converted = e.Value.ToString(); // ToString() kann null zurückgeben
        if (converted != null) // ToString-Ergebnis prüfen
        {
            newFolderName = converted;
        }
        else
        {
            newFolderName = string.Empty;
        }
    }

    private async Task OnNewFolderKeyDown(KeyboardEventArgs e) // Enter/Escape im Ordner-Eingabefeld
    {
        if (e.Key == "Enter") await ConfirmCreateFolder();
        else if (e.Key == "Escape") CancelCreateFolder();
    }

    private async Task ConfirmCreateFolder() // Ordner anlegen und Liste aktualisieren
    {
        if (string.IsNullOrWhiteSpace(newFolderName)) return;

        string nameToCreate = newFolderName.Trim();

        bool exists = await FolderDb.FolderNameExistsAsync(nameToCreate); // Duplikat-Pruefung
        if (exists)
        {
            folderCreateError = $"Ein Ordner '{nameToCreate}' existiert bereits."; // Inline-Fehler anzeigen
            StateHasChanged();
            _ = Task.Run(async () => // Fehlermeldung nach 3s automatisch ausblenden
            {
                await Task.Delay(3000);
                await InvokeAsync(() => { folderCreateError = string.Empty; StateHasChanged(); });
            });
            return;
        }

        folderCreateError = string.Empty;
        isCreatingFolder = false;
        newFolderName = string.Empty;

        await FolderDb.CreateFolderAsync(nameToCreate);
        Folders = await FolderDb.GetAllFoldersAsync();
        StateHasChanged();
    }

    private async Task DeleteFolder(int folderId) // Ordner löschen mit Entferne-Animation
    {
        if (isDeletingFolder) return;
        isDeletingFolder = true;

        removingFolderIds.Add(folderId);
        StateHasChanged();
        await Task.Delay(300); // Animations-Dauer

        List<Folder> updated = new List<Folder>(); // Ordner aus UI-Liste entfernen
        foreach (Folder f in Folders)
        {
            if (f.Id != folderId) updated.Add(f);
        }
        Folders = updated;
        removingFolderIds.Remove(folderId);
        StateHasChanged();

        try
        {
            await Task.Run(async () => await FolderDb.DeleteFolderAsync(folderId));
            await RefreshDocuments();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Fehler beim Löschen des Ordners: {ex.Message}");
        }
        finally
        {
            isDeletingFolder = false;
        }
    }

    // --- Chat-Operationen ---

    private void OpenChat(int chatId) // Chat öffnen
    {
        Navigation.NavigateTo("/chat/" + chatId);
    }

    private async Task DeleteChat(int chatId) // Chat löschen mit Entferne-Animation
    {
        if (isDeleting) return;
        isDeleting = true;

        removingChatIds.Add(chatId);
        StateHasChanged();
        await Task.Delay(340); // Animations-Dauer

        List<Conversation> updatedChats = new List<Conversation>(); // Chat aus UI-Liste entfernen
        foreach (Conversation c in Chats)
        {
            if (c.Id != chatId) updatedChats.Add(c);
        }
        Chats = updatedChats;
        removingChatIds.Remove(chatId);
        StateHasChanged();

        try { await Task.Run(async () => await ChatDb.DeleteConversationAsync(chatId)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Fehler beim Löschen: {ex.Message}"); }
        finally { isDeleting = false; }
    }

    // --- Dokument-Operationen ---

    private async Task DeleteDocument(int documentId) // Dokument von aktuellem Standort entfernen
    {
        if (isDeletingDocument) return;
        isDeletingDocument = true;

        if (moveDropdownDocId == documentId) moveDropdownDocId = null;
        if (selectedDocId == documentId) selectedDocId = null;
        if (clipboardDocId == documentId) ClearClipboard();

        removingDocumentIds.Add(documentId);
        StateHasChanged();
        await Task.Delay(300); // Animations-Dauer

        List<Document> updatedDocs = new List<Document>(); // Dokument aus UI-Liste entfernen
        foreach (Document d in Documents)
        {
            if (d.Id != documentId) updatedDocs.Add(d);
        }
        Documents = updatedDocs;
        removingDocumentIds.Remove(documentId);
        StateHasChanged();

        try
        {
            int? locationFolderId = currentFolderId; // Standort merken vor async-Aufruf
            await Task.Run(async () =>
                await FolderDb.DeleteDocumentFromLocationAsync(documentId, locationFolderId)
            );
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Fehler beim Löschen: {ex.Message}"); }
        finally { isDeletingDocument = false; }
    }

    private string GetFormattedDate(DateTime createdAt) // Datum UTC → lokale Zeit formatieren
    {
        if (createdAt.Year < 2000) return "Unbekanntes Datum";

        DateTime localTime;
        if (createdAt.Kind == DateTimeKind.Unspecified) // Unbekannte Zeitzone → als UTC behandeln
        {
            localTime = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc).ToLocalTime();
        }
        else
        {
            localTime = createdAt.ToLocalTime();
        }

        return localTime.ToString("dd.MM.yyyy HH:mm");
    }

    private async Task OpenDocument(int documentId) // Doppelklick: Dokument in Standard-App öffnen
    {
        try
        {
            var doc = await DocDb.GetDocumentByIdAsync(documentId);
            if (doc == null || doc.Id < 0) return;

            string tempPath = Path.Combine(Path.GetTempPath(), doc.FileName);

            if (doc.FileType == "pdf") // PDF von Base64 konvertieren
            {
                try { await File.WriteAllBytesAsync(tempPath, Convert.FromBase64String(doc.FileContent)); }
                catch (FormatException) { await File.WriteAllTextAsync(tempPath, doc.FileContent); }
            }
            else // Textdatei direkt schreiben
            {
                await File.WriteAllTextAsync(tempPath, doc.FileContent);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempPath) { UseShellExecute = true });
        }
        catch { } // Fehler beim Öffnen ignorieren (externe App)
    }
}
