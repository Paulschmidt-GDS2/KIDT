using System.Security.Cryptography;
using System.Text;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class DocumentDbService // Service: Dokumenten-Operationen (CRUD + Search + Linking zu Conversations)
{
    private readonly ChatDbContext db; // EF Core Datenbank-Context (Dependency Injection)

    public DocumentDbService(ChatDbContext dbContext)
    {
        this.db = dbContext;
    }

    public async Task<int> SaveDocumentAsync(string fileName, string fileContent, string fileType, string extractedText, string thumbnailBase64) // Speichere Dokument in Datenbank
    {
        string fileHash = ComputeFileHash(fileContent); // Berechne Hash vom Datei-Inhalt

        var allDocuments = await this.db.Documents.ToListAsync(); // Lade alle Dokumente aus Datenbank
        
        Document? existingDoc = null; // Variable für existierendes Dokument
        foreach (Document d in allDocuments) // Gehe durch alle Dokumente
        {
            if (d.FileHash == fileHash) // Gleicher Hash gefunden?
            {
                existingDoc = d; // Dokument existiert bereits
                break;
            }
        }

        if (existingDoc != null) // Dokument existiert bereits?
        {
            return existingDoc.Id; // Gib existierende ID zurück (verhindert Duplikate)
        }

        Document newDoc = new Document(); // Erstelle neues Dokument-Objekt
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
        return allDocuments; // Gib sortierte Liste zurück
    }

    public async Task<Document?> GetDocumentByIdAsync(int documentId) // Lade einzelnes Dokument
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
        
        return null;
    }

    public async Task<List<Document>> SearchDocumentsAsync(string searchTerm) // Suche Dokumente nach Suchbegriff
    {
        string lowerSearchTerm = searchTerm.ToLower(); // Konvertiere Suchbegriff zu Kleinbuchstaben

        var query = this.db.Documents.AsNoTracking(); // Starte Query ohne Änderungs-Verfolgung
        var allDocuments = await query.ToListAsync(); // Lade alle Dokumente aus Datenbank
        
        var filtered = new List<Document>(); // Erstelle leere Liste für Ergebnis
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
        var allLinks = await this.db.ConversationDocuments.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank
        
        bool alreadyLinked = false; // Standardmäßig: Noch nicht verknüpft
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId && cd.DocumentId == documentId) // Gleiche Conversation und gleiches Dokument?
            {
                alreadyLinked = true; // Link existiert bereits
                break;
            }
        }

        if (alreadyLinked) // Link existiert bereits?
        {
            return false;
        }

        ConversationDocument link = new ConversationDocument(); // Erstelle neue Verknüpfung
        link.ConversationId = conversationId; // Setze Chat-ID
        link.DocumentId = documentId; // Setze Dokument-ID
        link.AddedAt = DateTime.UtcNow; // Setze aktuelles Datum (UTC)

        this.db.ConversationDocuments.Add(link); // Füge Verknüpfung zur Datenbank hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen in Datenbank

        return true;
    }

    public async Task<bool> UnlinkDocumentFromConversationAsync(int documentId, int conversationId) // Entferne Verknüpfung zwischen Dokument und Chat
    {
        var allLinks = await this.db.ConversationDocuments.ToListAsync(); // Lade alle Verknüpfungen aus Datenbank
        
        ConversationDocument? link = null; // Variable für gefundene Verknüpfung
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId && cd.DocumentId == documentId) // Gleiche Conversation und gleiches Dokument?
            {
                link = cd; // Verknüpfung gefunden
                break;
            }
        }

        if (link == null) // Keine Verknüpfung gefunden?
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
        
        var documentIds = new List<int>(); // Erstelle leere Liste für Dokument-IDs
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.ConversationId == conversationId) // Gehört zu diesem Chat?
            {
                documentIds.Add(cd.DocumentId);
            }
        }
        
        var allDocuments = await this.db.Documents.ToListAsync(); // Lade alle Dokumente aus Datenbank
        var result = new List<Document>(); // Erstelle leere Liste für Ergebnis
        
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
        var links = new List<ConversationDocument>(); // Erstelle leere Liste für zu löschende Verknüpfungen
        
        foreach (ConversationDocument cd in allLinks) // Gehe durch alle Verknüpfungen
        {
            if (cd.DocumentId == documentId) // Gehört zu diesem Dokument?
            {
                links.Add(cd);
            }
        }
        
        this.db.ConversationDocuments.RemoveRange(links); // Entferne alle Verknüpfungen aus Datenbank
        
        Document? doc = await this.db.Documents.FindAsync(documentId); // Suche Dokument
        if (doc != null) // Dokument gefunden?
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
            byte[] hashBytes = sha256.ComputeHash(contentBytes); // Berechne SHA256-Hash (32 Bytes)
            return Convert.ToBase64String(hashBytes); // Konvertiere Hash zu Base64-String (für DB-Speicherung)
        }
    }
}