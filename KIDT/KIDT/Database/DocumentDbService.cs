using System.Security.Cryptography;
using System.Text;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class DocumentDbService // Service: Dokumenten-Operationen (CRUD + Search + Linking zu Conversations)
{
    private readonly ChatDbContext db;

    public DocumentDbService(ChatDbContext dbContext)
    {
        this.db = dbContext;
    }

    public async Task<int> SaveDocumentAsync(string fileName, string fileContent, string fileType, string extractedText, string thumbnailBase64) // Speichere Dokument in Datenbank
    {
        string fileHash = ComputeFileHash(fileContent); // Berechne Hash vom Datei-Inhalt

        var allDocuments = await this.db.Documents.ToListAsync(); // Lade alle Dokumente aus Datenbank

        Document existingDoc = new Document();
        bool docExists = false;
        foreach (Document d in allDocuments) // Gehe durch alle Dokumente
        {
            if (d.FileHash == fileHash) // Gleicher Hash gefunden?
            {
                existingDoc = d;
                docExists = true;
                break;
            }
        }

        if (docExists) // Dokument existiert bereits?
        {
            return existingDoc.Id; // Gib existierende ID zurück (verhindert Duplikate)
        }

        Document newDoc = new Document();
        newDoc.FileName = fileName;
        newDoc.FileHash = fileHash;
        newDoc.FileContent = fileContent;
        newDoc.FileType = fileType;
        newDoc.ExtractedText = extractedText;
        newDoc.ThumbnailBase64 = thumbnailBase64;
        newDoc.UploadedAt = DateTime.UtcNow;

        this.db.Documents.Add(newDoc); // Füge Dokument zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank

        return newDoc.Id;
    }

    public async Task<List<Document>> GetAllDocumentsAsync() // Lade alle Dokumente
    {
        var query = this.db.Documents.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allDocuments = await query.ToListAsync(); // Lade alle Dokumente aus Datenbank

        allDocuments.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt)); // Sortiere nach Datum (neueste zuerst)
        return allDocuments;
    }

    public async Task<Document> GetDocumentByIdAsync(int documentId) // Lade einzelnes Dokument
    {
        var query = this.db.Documents.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allDocuments = await query.ToListAsync(); // Lade alle Dokumente aus Datenbank

        foreach (Document d in allDocuments) // Gehe durch alle Dokumente
        {
            if (d.Id == documentId) // Ist das die gesuchte ID?
            {
                return d;
            }
        }

        Document notFoundDoc = new Document();
        notFoundDoc.Id = -1; // -1 = nicht gefunden (Sentinel-Wert)
        return notFoundDoc;
    }

    public async Task<List<Document>> SearchDocumentsAsync(string searchTerm) // Suche Dokumente nach Suchbegriff
    {
        string lowerSearchTerm = searchTerm.ToLower(); // Konvertiere Suchbegriff zu Kleinbuchstaben

        var query = this.db.Documents.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allDocuments = await query.ToListAsync(); // Lade alle Dokumente aus Datenbank

        var filtered = new List<Document>();
        foreach (Document d in allDocuments) // Gehe durch alle Dokumente
        {
            string lowerFileName = d.FileName.ToLower(); // Dateiname in Kleinbuchstaben
            string lowerText = d.ExtractedText.ToLower(); // Text in Kleinbuchstaben

            if (lowerFileName.Contains(lowerSearchTerm) || lowerText.Contains(lowerSearchTerm)) // Suchbegriff gefunden?
            {
                filtered.Add(d);
            }
        }

        filtered.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt)); // Sortiere nach Datum (neueste zuerst)
        return filtered;
    }

    public async Task<bool> LinkDocumentToConversationAsync(int documentId, int conversationId) // Verknüpfe Dokument mit Chat
    {
        // Prüfe ob Link bereits existiert (ohne Navigation Properties zu laden)
        var existingLink = await this.db.ConversationDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(cd => cd.ConversationId == conversationId && cd.DocumentId == documentId);

        if (existingLink != null) // Link existiert bereits?
        {
            return false; // Bereits verknüpft
        }

        // WICHTIG: Prüfe ob Conversation und Document existieren (Foreign Key Constraints!)
        var conversationExists = await this.db.Conversations.AnyAsync(c => c.Id == conversationId);
        var documentExists = await this.db.Documents.AnyAsync(d => d.Id == documentId);

        System.Diagnostics.Debug.WriteLine($"[DocumentDbService] ConversationExists={conversationExists}, DocumentExists={documentExists}");

        if (!conversationExists || !documentExists) // Einer der beiden existiert nicht?
        {
            System.Diagnostics.Debug.WriteLine($"[DocumentDbService] Abbruch: Conversation oder Document existiert nicht");
            return false; // Kann nicht verknüpfen
        }

        // Erstelle neue Verknüpfung (OHNE Navigation Properties!)
        ConversationDocument link = new ConversationDocument
        {
            ConversationId = conversationId,
            DocumentId = documentId,
            AddedAt = DateTime.UtcNow,
            Conversation = null, // Explizit auf null setzen
            Document = null // Explizit auf null setzen
        };

        System.Diagnostics.Debug.WriteLine($"[DocumentDbService] Link erstellen: ConvId={conversationId}, DocId={documentId}");
        this.db.ConversationDocuments.Add(link); // Füge Verknüpfung zur Datenbank hinzu

        try
        {
            await this.db.SaveChangesAsync(); // Speichere Änderungen
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DocumentDbService] Fehler beim Verknüpfen: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"[DocumentDbService] Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[DocumentDbService] Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DocumentDbService] InnerException: {ex.InnerException.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[DocumentDbService] InnerMessage: {ex.InnerException.Message}");
            }
            return false; // Fehler beim Speichern
        }
    }

    public async Task<bool> UnlinkDocumentFromConversationAsync(int documentId, int conversationId) // Entferne Verknüpfung zwischen Dokument und Chat
    {
        var allLinks = await this.db.ConversationDocuments.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank

        ConversationDocument link = new ConversationDocument();
        bool linkFound = false;
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId && cd.DocumentId == documentId) // Gleiche Conversation und gleiches Dokument?
            {
                link = cd;
                linkFound = true;
                break;
            }
        }

        if (linkFound == false) // Keine Verknüpfung gefunden?
        {
            return false;
        }

        this.db.ConversationDocuments.Remove(link); // Entferne Verknüpfung aus Datenbank
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank

        return true;
    }

    public async Task<bool> IsDocumentLinkedAsync(int documentId, int conversationId) // Prüfe ob Dokument mit Chat verknüpft ist
    {
        var allLinks = await this.db.ConversationDocuments.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank

        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId && cd.DocumentId == documentId) // Gleiche Conversation und gleiches Dokument?
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<Document>> GetDocumentsForConversationAsync(int conversationId) // Lade alle Dokumente für einen Chat
    {
        var query = this.db.ConversationDocuments.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allLinks = await query.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank

        var documentIds = new List<int>();
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId) // Gehört zu diesem Chat?
            {
                documentIds.Add(cd.DocumentId);
            }
        }

        var allDocuments = await this.db.Documents.ToListAsync(); // Lade alle Dokumente aus Datenbank
        var result = new List<Document>();

        foreach (int docId in documentIds) // Gehe durch alle gefundenen Dokument-IDs
        {
            foreach (Document d in allDocuments) // Gehe durch alle Dokumente
            {
                if (d.Id == docId) // Ist das die gesuchte ID?
                {
                    result.Add(d);
                    break;
                }
            }
        }

        result.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt)); // Sortiere nach Datum (neueste zuerst)
        return result;
    }

    public async Task DeleteDocumentAsync(int documentId) // Lösche Dokument aus Datenbank
    {
        var allLinks = await this.db.ConversationDocuments.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank
        var links = new List<ConversationDocument>();

        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.DocumentId == documentId) // Gehört zu diesem Dokument?
            {
                links.Add(cd);
            }
        }

        this.db.ConversationDocuments.RemoveRange(links); // Entferne alle Verknüpfungen aus Datenbank

        var allDocuments = await this.db.Documents.ToListAsync();
        Document doc = new Document();
        bool docFound = false;
        foreach (Document d in allDocuments)
        {
            if (d.Id == documentId)
            {
                doc = d;
                docFound = true;
                break;
            }
        }

        if (docFound) // Dokument gefunden?
        {
            this.db.Documents.Remove(doc);
        }

        await this.db.SaveChangesAsync(); // Speichere alle Änderungen in Datenbank
    }

    private string ComputeFileHash(string fileContent) // Hilfsmethode: Berechnet SHA256-Hash von File-Content (für Duplikat-Erkennung)
    {
        using (SHA256 sha256 = SHA256.Create()) // Erstelle SHA256-Hasher (wird automatisch disposed)
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(fileContent); // Konvertiere String zu UTF8-Bytes
            byte[] hashBytes = sha256.ComputeHash(contentBytes); // Berechne SHA256-Hash
            return Convert.ToBase64String(hashBytes); // Konvertiere Hash zu Base64-String (für DB-Speicherung)
        }
    }
}
