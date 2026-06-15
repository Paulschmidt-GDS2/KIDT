using ModelContextProtocol.Server;
using System.ComponentModel;
using KIDT.Database;
using KIDT.Models;
using System.Text.Json;

namespace KIDT.Services.McpTools;

[McpServerToolType] // Markiert Klasse als MCP-Tool-Container (wird von MCP-Server registriert)
public class DocumentTools // MCP-Tools: search_documents und add_document_to_chat (werden per Function Calling vom LLM aufgerufen)
{
    private readonly DocumentDbService docDbService;
    private readonly int conversationId;

    public DocumentTools(DocumentDbService documentDbService, int currentConversationId) // Konstruktor: Wird bei RegisterTools aufgerufen (Dependency Injection)
    {
        this.docDbService = documentDbService;
        this.conversationId = currentConversationId;
    }

    [McpServerTool]
    [Description("Sucht Dokumente in der Bibliothek anhand von Dateiname oder Stichwort und zeigt die Treffer als Card an. Verwenden, wenn der Nutzer ein Dokument finden, sehen oder öffnen möchte oder wissen will, ob es existiert. Liefert die Dokumente selbst — nicht deren Ordner-Standort (dafür find_documents).")]
    public async Task<string> SearchDocuments( // Tool: Sucht Dokumente nach Suchbegriff
        [Description("Der Suchbegriff (Dateiname oder Stichwort)")] string searchQuery)
    {
        var documents = await this.docDbService.SearchDocumentsAsync(searchQuery); // Suche in DB (case-insensitive, Dateiname + ExtractedText)

        if (documents.Count == 0) // Keine Dokumente gefunden?
        {
            return JsonSerializer.Serialize(new
            {
                found = 0,
                documentIds = new List<int>(),
                message = $"Keine Dokumente gefunden für '{searchQuery}'"
            });
        }

        var result = new // Erstelle JSON-Result mit Dokument-Infos
        {
            found = documents.Count,
            documentIds = new List<int>() // Liste der IDs (für add_document_to_chat)
        };

        foreach (Document d in documents) // Durchlaufe alle gefundenen Dokumente
        {
            result.documentIds.Add(d.Id); // Füge ID zur Liste hinzu
        }

        var documentList = new List<object>();
        foreach (Document d in documents) // Durchlaufe alle gefundenen Dokumente
        {
            bool hasThumbnail = false;
            if (!string.IsNullOrEmpty(d.ThumbnailBase64)) // Thumbnail vorhanden?
            {
                hasThumbnail = true;
            }

            documentList.Add(new
            {
                id = d.Id,
                fileName = d.FileName,
                fileType = d.FileType,
                uploadedAt = d.UploadedAt.ToString("dd.MM.yyyy HH:mm"), // Formatierter Upload-Zeitstempel
                hasThumbnail = hasThumbnail
            });
        }

        var finalResult = new
        {
            found = documents.Count,
            documentIds = result.documentIds,
            documents = documentList
        };

        return JsonSerializer.Serialize(finalResult);
    }

    [McpServerTool]
    [Description("Fügt ein Dokument zum aktuellen Chat hinzu. Gibt JSON mit Erfolg und Details zurück.")]
    public async Task<string> AddDocumentToChat( // Tool: Fügt Dokument zum aktuellen Chat hinzu (erstellt Link in ConversationDocuments-Tabelle)
        [Description("Die ID des hinzuzufügenden Dokuments (aus search_documents)")] int documentId)
    {
        bool alreadyLinked = await this.docDbService.IsDocumentLinkedAsync(documentId, this.conversationId); // Prüfe ob bereits verknüpft

        if (alreadyLinked) // Bereits hinzugefügt?
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Dokument ist bereits hinzugefügt",
                documentId
            });
        }

        bool success = await this.docDbService.LinkDocumentToConversationAsync(documentId, this.conversationId); // Erstelle Link in ConversationDocuments-Tabelle

        if (!success) // Hinzufügen fehlgeschlagen?
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Hinzufügen fehlgeschlagen",
                documentId
            });
        }

        var document = await this.docDbService.GetDocumentByIdAsync(documentId); // Lade volles Dokument aus DB (für Details)

        if (document == null) // Dokument nicht gefunden?
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                message = "Dokument nicht gefunden",
                documentId
            });
        }

        var result = new
        {
            success = true,
            message = $"Dokument '{document.FileName}' wurde hinzugefügt",
            documentId = documentId,
            fileName = document.FileName,
            fileType = document.FileType,
            extractedTextLength = 0 // Länge des extrahierten Texts (Initial: 0)
        };

        if (document.ExtractedText != null) // Extrahierter Text vorhanden?
        {
            result = new
            {
                success = true,
                message = $"Dokument '{document.FileName}' wurde hinzugefügt",
                documentId = documentId,
                fileName = document.FileName,
                fileType = document.FileType,
                extractedTextLength = document.ExtractedText.Length // Länge des extrahierten Texts
            };
        }

        return JsonSerializer.Serialize(result);
    }
}
