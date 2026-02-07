using ModelContextProtocol.Server;
using System.ComponentModel;
using KIDT.Database;
using System.Text.Json;

namespace KIDT.Services.McpTools;

[McpServerToolType] // Markiert Klasse als MCP-Tool-Container (wird von MCP-Server registriert)
public class DocumentTools // MCP-Tools: search_documents und add_document_to_chat (werden per Function Calling vom LLM aufgerufen)
{
    private readonly DocumentDbService docDbService; // Service für Dokument-DB-Operationen
    private readonly int conversationId; // Aktuelle Conversation-ID (für add_document_to_chat)

    public DocumentTools(DocumentDbService documentDbService, int currentConversationId) // Konstruktor: Wird bei RegisterTools aufgerufen (Dependency Injection)
    {
        this.docDbService = documentDbService; // DocumentDbService speichern
        this.conversationId = currentConversationId; // Conversation-ID speichern
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Durchsucht alle gespeicherten Dokumente nach einem Suchbegriff. Gibt JSON mit IDs und Details zurück.")] // Tool-Beschreibung für LLM
    public async Task<string> SearchDocuments( // Tool: Sucht Dokumente nach Suchbegriff (Dateiname ODER Textinhalt)
        [Description("Der Suchbegriff (wird in Dateiname und Textinhalt gesucht)")] string searchQuery) // Parameter-Beschreibung für LLM
    {
        var documents = await this.docDbService.SearchDocumentsAsync(searchQuery); // Suche in DB (case-insensitive)
        
        if (documents.Count == 0) // Keine Dokumente gefunden?
        {
            return JsonSerializer.Serialize(new  // Gib JSON zurück: 0 gefunden
            { 
                found = 0, // Anzahl: 0
                documentIds = new List<int>(), // Leere Liste
                message = $"Keine Dokumente gefunden für '{searchQuery}'" // Nachricht
            });
        }
        
        var result = new // Erstelle JSON-Result mit Dokument-Infos
        {
            found = documents.Count, // Anzahl gefundener Dokumente
            documentIds = documents.Select(d => d.Id).ToList(), // Liste der IDs
            documents = documents.Select(d => new // Liste mit Details
            {
                id = d.Id, // Dokument-ID
                fileName = d.FileName, // Dateiname
                fileType = d.FileType, // Typ (z.B. "pdf")
                uploadedAt = d.UploadedAt.ToString("dd.MM.yyyy HH:mm"), // Upload-Datum formatiert
                hasThumbnail = !string.IsNullOrEmpty(d.ThumbnailBase64) // Hat Thumbnail?
            }).ToList()
        };
        
        return JsonSerializer.Serialize(result); // Gib JSON zurück
    }

    [McpServerTool] // Markiert Methode als MCP-Tool (wird vom LLM per Function Calling aufgerufen)
    [Description("Fügt ein Dokument zum aktuellen Chat hinzu. Gibt JSON mit Erfolg und Details zurück.")] // Tool-Beschreibung für LLM
    public async Task<string> AddDocumentToChat( // Tool: Fügt Dokument zum aktuellen Chat hinzu (erstellt Link in ConversationDocuments-Tabelle)
        [Description("Die ID des hinzuzufügenden Dokuments (aus search_documents)")] int documentId) // Parameter-Beschreibung für LLM
    {
        bool alreadyLinked = await this.docDbService.IsDocumentLinkedAsync(documentId, this.conversationId); // Prüfe ob bereits hinzugefügt
        
        if (alreadyLinked) // Bereits hinzugefügt?
        {
            return JsonSerializer.Serialize(new  // Gib JSON zurück: Bereits hinzugefügt
            { 
                success = false, // Nicht erfolgreich (bereits vorhanden)
                message = "Dokument ist bereits hinzugefügt", // Nachricht
                documentId // Dokument-ID
            });
        }
        
        bool success = await this.docDbService.LinkDocumentToConversationAsync(documentId, this.conversationId); // Erstelle Link in DB
        
        if (!success) // Hinzufügen fehlgeschlagen? (sollte nicht passieren)
        {
            return JsonSerializer.Serialize(new  // Gib JSON zurück: Fehler
            { 
                success = false, // Nicht erfolgreich
                message = "Hinzufügen fehlgeschlagen", // Nachricht
                documentId // Dokument-ID
            });
        }
        
        var document = await this.docDbService.GetDocumentByIdAsync(documentId); // Lade volles Dokument aus DB
        
        if (document == null) // Dokument nicht gefunden? (sollte nicht passieren)
        {
            return JsonSerializer.Serialize(new  // Gib JSON zurück: Nicht gefunden
            { 
                success = false, // Nicht erfolgreich
                message = "Dokument nicht gefunden", // Nachricht
                documentId // Dokument-ID
            });
        }
        
        var result = new // Erstelle JSON-Result mit Erfolg
        {
            success = true, // Erfolgreich
            message = $"Dokument '{document.FileName}' wurde hinzugefügt", // Bestätigungs-Nachricht
            documentId, // Dokument-ID
            fileName = document.FileName, // Dateiname
            fileType = document.FileType, // Dateityp
            extractedTextLength = document.ExtractedText?.Length ?? 0 // Textlänge (oder 0 wenn null)
        };
        
        return JsonSerializer.Serialize(result); // Gib JSON zurück
    }
}
