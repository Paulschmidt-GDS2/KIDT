using Microsoft.Extensions.DependencyInjection;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Components.Pages;

public partial class Home // Upload, Dokument-Öffnen, Dokument-zum-Chat-Hinzufügen
{
    private string UploadedFileName = string.Empty;
    private int currentDocumentId = 0;

    private async Task OnUploadClick() // Upload-Button: Öffnet Datei-Dialog und lädt Datei hoch
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions // Öffne Datei-Auswahl-Dialog
            {
                PickerTitle = "Wähle eine Datei aus",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".pdf", ".txt", ".md", ".json" } }
                })
            });

            if (result != null) // Check: Hat User eine Datei ausgewählt?
            {
                System.Diagnostics.Debug.WriteLine($"[UPLOAD] START - Datei ausgewählt: {result.FileName}");

                using (var checkScope = ServiceProvider.CreateScope()) // Duplikat-Pruefung im Hauptbereich (Chat-Upload geht immer nach Root)
                {
                    var folderDb = checkScope.ServiceProvider.GetRequiredService<KIDT.Database.FolderDbService>();
                    bool nameConflict = await folderDb.DocumentNameExistsInLocationAsync(result.FileName, null);
                    if (nameConflict)
                    {
                        var errMsg = new ChatMessage { Text = string.Empty, IsUser = false, IsLoading = false };
                        Messages.Add(errMsg);
                        await TypewriterEffect(errMsg, $"Im Hauptbereich existiert bereits eine Datei '{result.FileName}'. Bitte zuerst die bestehende Datei entfernen oder einen anderen Namen verwenden.");
                        StateHasChanged();
                        return;
                    }
                }

                UploadedFileName = result.FileName; // Badge sofort anzeigen
                StateHasChanged();

                Messages.Add(new ChatMessage { Text = "Datei wird geladen...", IsUser = false, IsLoading = true }); // Loading-Nachricht
                StateHasChanged();

                string uploadResult = await Chat.UploadFileAsync(result.FullPath); // Text extrahieren

                Messages.RemoveAt(Messages.Count - 1); // Loading entfernen

                var uploadMessage = new ChatMessage { Text = string.Empty, IsUser = false, IsLoading = false };
                Messages.Add(uploadMessage);
                StateHasChanged();

                if (uploadResult.StartsWith("Fehler")) // Fehler beim Upload?
                {
                    UploadedFileName = string.Empty;
                    await TypewriterEffect(uploadMessage, uploadResult);
                    StateHasChanged();
                }
                else // Upload erfolgreich
                {
                    int conversationIdForSave = currentConversationId;

                    if (currentConversationId == 0) // Conversation erstellen falls nötig
                    {
                        currentConversationId = await Db.CreateConversationAsync("Neuer Chat");
                        conversationIdForSave = currentConversationId;
                    }

                    string extractedFileText = Chat.GetCurrentFileContent(); // Datei-Text für DB

                    string fileExtension = Path.GetExtension(result.FileName).ToLower(); // Datei-Endung bestimmen
                    string fileType = "unknown";
                    if (fileExtension == ".pdf") fileType = "pdf";
                    else if (fileExtension == ".txt") fileType = "txt";
                    else if (fileExtension == ".md") fileType = "md";
                    else if (fileExtension == ".json") fileType = "json";

                    var saveFileTask = Task.Run(async () =>
                    {
                        string thumbnail = await ThumbGen.GenerateThumbnailAsync(result.FullPath); // PDF-Vorschau generieren

                        int documentId = 0;
                        using (var scope = ServiceProvider.CreateScope())
                        {
                            var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();

                            string fileContent;
                            if (fileType == "pdf") // PDF als Base64 speichern
                            {
                                byte[] pdfBytes = await File.ReadAllBytesAsync(result.FullPath);
                                fileContent = Convert.ToBase64String(pdfBytes);
                            }
                            else // Text direkt lesen
                            {
                                fileContent = await File.ReadAllTextAsync(result.FullPath);
                            }

                            documentId = await docDbService.SaveDocumentAsync(result.FileName, fileContent, fileType, extractedFileText, thumbnail);
                            await docDbService.LinkDocumentToConversationAsync(documentId, conversationIdForSave);
                        }

                        await Db.SaveMessageAsync(conversationIdForSave, false, uploadResult);

                        if (documentId > 0) // DocID-Marker für RouterService analyze_document
                        {
                            await Db.SaveMessageAsync(conversationIdForSave, false, $"[DocID: {documentId}]");
                        }

                        await Db.UpdateConversationTitleAsync(conversationIdForSave);

                        return documentId;
                    });

                    await TypewriterEffect(uploadMessage, uploadResult);

                    int uploadedDocId = await saveFileTask;
                    currentDocumentId = uploadedDocId;
                    System.Diagnostics.Debug.WriteLine($"[UPLOAD] currentDocumentId gesetzt: {currentDocumentId}");
                }
            }
        }
        catch (Exception ex)
        {
            UploadedFileName = string.Empty;
            var errorMessage = new ChatMessage { Text = string.Empty, IsUser = false, IsLoading = false };
            Messages.Add(errorMessage);
            await TypewriterEffect(errorMessage, $"Fehler beim Datei-Upload: {ex.Message}");
            StateHasChanged();
        }
    }

    private async Task RemoveFile() // Entfernt angehängte Datei und deren Konversations-Verknüpfung
    {
        UploadedFileName = string.Empty;
        Chat.ClearFile();

        if (currentDocumentId > 0 && currentConversationId > 0) // Verknüpfung in DB entfernen
        {
            try
            {
                using var scope = ServiceProvider.CreateScope();
                var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();
                bool removed = await docDbService.UnlinkDocumentFromConversationAsync(currentDocumentId, currentConversationId);
                System.Diagnostics.Debug.WriteLine($"[REMOVE] Verknüpfung entfernt: {removed}, DocId: {currentDocumentId}, ConvId: {currentConversationId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[REMOVE] Fehler beim Entfernen der Verknüpfung: {ex.Message}");
            }
        }

        currentDocumentId = 0;

        var confirmMessage = new ChatMessage { Text = string.Empty, IsUser = false, IsLoading = false };
        Messages.Add(confirmMessage);
        await TypewriterEffect(confirmMessage, "Datei wurde entfernt.");
        StateHasChanged();
    }

    private async Task OpenDocument(int documentId) // Öffnet Dokument in Standard-Anwendung
    {
        try
        {
            using var scope = ServiceProvider.CreateScope();
            var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();
            var doc = await docDbService.GetDocumentByIdAsync(documentId);
            if (doc == null) return;

            string tempPath = Path.Combine(Path.GetTempPath(), doc.FileName); // Temporärer Pfad

            if (doc.FileType == "pdf") // PDF von Base64 konvertieren
            {
                try
                {
                    await File.WriteAllBytesAsync(tempPath, Convert.FromBase64String(doc.FileContent));
                }
                catch (FormatException) // Fallback: kein Base64
                {
                    await File.WriteAllTextAsync(tempPath, doc.FileContent);
                }
            }
            else // Textdatei direkt schreiben
            {
                await File.WriteAllTextAsync(tempPath, doc.FileContent);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempPath) { UseShellExecute = true }); // Öffne in Standard-App
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage { Text = $"Fehler: {ex.Message}", DisplayText = $"Fehler: {ex.Message}", IsUser = false });
            StateHasChanged();
        }
    }

    private async Task AddDocumentToChat(int documentId) // Verknüpft Dokument aus der Daten-Seite mit aktivem Chat
    {
        try
        {
            if (currentConversationId == 0) // Conversation erstellen falls nötig
            {
                currentConversationId = await Db.CreateConversationAsync("Neuer Chat");
            }

            using var scope = ServiceProvider.CreateScope();
            var docDbService = scope.ServiceProvider.GetRequiredService<DocumentDbService>();

            var doc = await docDbService.GetDocumentByIdAsync(documentId);

            if (doc != null) // Dokument gefunden: Badge anzeigen
            {
                UploadedFileName = doc.FileName;
                currentDocumentId = doc.Id;
                StateHasChanged();
            }

            bool success = await docDbService.LinkDocumentToConversationAsync(documentId, currentConversationId);

            var confirmMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(confirmMsg);
            StateHasChanged();

            string fileName = "Unbekannt"; // Dateiname für Bestätigung
            if (doc != null) fileName = doc.FileName;

            if (success) // Erfolgreich verknüpft
            {
                await TypewriterEffect(confirmMsg, $"Dokument '{fileName}' wurde zum Chat hinzugefügt.");
            }
            else // Bereits verknüpft
            {
                await TypewriterEffect(confirmMsg, $"Dokument '{fileName}' ist bereits im Chat verknüpft.");
            }
        }
        catch (Exception ex)
        {
            var errorMsg = new ChatMessage { Text = string.Empty, DisplayText = string.Empty, IsUser = false };
            Messages.Add(errorMsg);
            StateHasChanged();
            await TypewriterEffect(errorMsg, $"Fehler: {ex.Message}");
        }
    }
}
