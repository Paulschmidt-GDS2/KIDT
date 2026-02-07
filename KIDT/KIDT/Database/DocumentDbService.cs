using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KIDT.Database;
using KIDT.Models;
using Microsoft.EntityFrameworkCore;

namespace KIDT.Database;

public class DocumentDbService // Service: Dokumenten-Operationen (CRUD + Search + Linking zu Conversations)
{
    private readonly ChatDbContext db; // EF Core Datenbank-Context (Dependency Injection)

    public DocumentDbService(ChatDbContext dbContext) // Konstruktor: Wird beim App-Start aufgerufen (Dependency Injection)
    {
        this.db = dbContext; // Context speichern für alle DB-Operationen
    }

    public async Task<int> SaveDocumentAsync(string fileName, string fileContent, string fileType, string extractedText, string thumbnailBase64) // Speichert Dokument in DB (mit Duplikat-Check via Hash - Dokument wird NICHT doppelt gespeichert!)
    {
        string fileHash = ComputeFileHash(fileContent); // Berechne SHA256-Hash vom File-Content (für Duplikat-Erkennung)

        Document? existingDoc = await this.db.Documents // Suche in DB nach existierendem Dokument
            .FirstOrDefaultAsync(d => d.FileHash == fileHash); // Gleicher Hash? ? Datei ist identisch

        if (existingDoc != null) // Dokument existiert bereits in DB?
        {
            return existingDoc.Id; // Gib existierende ID zurück (speichere NICHT neu - verhindert Duplikate!)
        }

        Document newDoc = new Document // Erstelle neues Dokument-Objekt
        {
            FileName = fileName, // Dateiname (z.B. "Document.pdf")
            FileHash = fileHash, // SHA256-Hash (für Duplikat-Check)
            FileContent = fileContent, // Roher File-Content (Base64 bei PDF)
            FileType = fileType, // Dateityp (z.B. "pdf", "txt")
            ExtractedText = extractedText, // Extrahierter Text (von PDFtoText etc.)
            ThumbnailBase64 = thumbnailBase64, // Thumbnail als Base64-String
            UploadedAt = DateTime.UtcNow // UTC-Timestamp (wird später zu Local konvertiert)
        };

        this.db.Documents.Add(newDoc); // Füge Dokument zur DB hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen in DB (generiert ID)

        return newDoc.Id; // Gib neue Dokument-ID zurück
    }

    public async Task<List<Document>> GetAllDocumentsAsync() // Lädt alle Dokumente aus DB (für Dokumente-Seite)
    {
        return await this.db.Documents // Query: Alle Dokumente
            .AsNoTracking() // Keine Change-Tracking (Performance-Optimierung - read-only)
            .OrderByDescending(d => d.UploadedAt) // Sortiere nach Upload-Datum (neueste zuerst)
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    public async Task<Document?> GetDocumentByIdAsync(int documentId) // Lädt einzelnes Dokument anhand ID
    {
        return await this.db.Documents // Query: Dokument mit ID
            .AsNoTracking() // Keine Change-Tracking (Performance-Optimierung)
            .FirstOrDefaultAsync(d => d.Id == documentId); // Finde anhand ID (oder null wenn nicht gefunden)
    }

    public async Task<List<Document>> SearchDocumentsAsync(string searchTerm) // Sucht Dokumente nach Suchbegriff (case-insensitive in Filename ODER ExtractedText)
    {
        string lowerSearchTerm = searchTerm.ToLower(); // Konvertiere Suchbegriff zu Kleinbuchstaben (case-insensitive)

        return await this.db.Documents // Query: Alle Dokumente
            .AsNoTracking() // Keine Change-Tracking (Performance-Optimierung)
            .Where(d => d.FileName.ToLower().Contains(lowerSearchTerm) || d.ExtractedText.ToLower().Contains(lowerSearchTerm)) // Suche in Filename ODER ExtractedText
            .OrderByDescending(d => d.UploadedAt) // Sortiere nach Upload-Datum (neueste zuerst)
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    public async Task<bool> LinkDocumentToConversationAsync(int documentId, int conversationId) // Verknüpft Dokument mit Conversation (erstellt Eintrag in ConversationDocuments-Junction-Tabelle)
    {
        bool alreadyLinked = await this.db.ConversationDocuments // Prüfe ob Link bereits existiert
            .AnyAsync(cd => cd.ConversationId == conversationId && cd.DocumentId == documentId); // Gleiche Conversation UND gleiches Document?


        if (alreadyLinked) // Link existiert bereits?
        {
            return false; // Gib false zurück (nichts getan - Duplikat-Schutz)
        }

        ConversationDocument link = new ConversationDocument // Erstelle neue Verknüpfung (Junction-Tabelle: Many-to-Many)
        {
            ConversationId = conversationId, // Chat-ID setzen
            DocumentId = documentId, // Dokument-ID setzen
            AddedAt = DateTime.UtcNow // UTC-Timestamp
        };

        this.db.ConversationDocuments.Add(link); // Füge Verknüpfung zur DB hinzu
        await this.db.SaveChangesAsync(); // Speichere Änderungen

        return true; // Gib true zurück (erfolgreich hinzugefügt)
    }

    public async Task<bool> IsDocumentLinkedAsync(int documentId, int conversationId) // Prüft ob Dokument bereits zum Chat hinzugefügt wurde (wird von add_document_to_chat-Tool verwendet)
    {
        return await this.db.ConversationDocuments // Query: ConversationDocuments-Junction-Tabelle
            .AnyAsync(cd => cd.ConversationId == conversationId && cd.DocumentId == documentId); // Existiert Link?
    }

    public async Task<List<Document>> GetDocumentsForConversationAsync(int conversationId) // Lädt alle Dokumente die zu einem Chat hinzugefügt wurden (über Junction-Tabelle)
    {
        return await this.db.ConversationDocuments // Starte mit Junction-Tabelle
            .AsNoTracking() // Keine Change-Tracking (Performance-Optimierung)
            .Where(cd => cd.ConversationId == conversationId) // Filtere nach Chat-ID
            .Include(cd => cd.Document) // EF Core: Lade verknüpfte Document-Objekte mit (JOIN)
            .Select(cd => cd.Document!) // Extrahiere nur Document-Objekte (! = garantiert nicht null)
            .OrderByDescending(d => d.UploadedAt) // Sortiere nach Upload-Datum (neueste zuerst)
            .ToListAsync(); // Führe Query aus und gib Liste zurück
    }

    public async Task DeleteDocumentAsync(int documentId) // Löscht Dokument aus DB (inkl. aller Verknüpfungen zu Conversations)
    {
        // SCHRITT 1: Lösche alle Verknüpfungen zu Conversations (verhindert Foreign-Key-Fehler)
        var links = await this.db.ConversationDocuments // Query: Alle Verknüpfungen zu diesem Dokument
            .Where(cd => cd.DocumentId == documentId) // Filtere nach Dokument-ID
            .ToListAsync(); // Führe Query aus
        
        this.db.ConversationDocuments.RemoveRange(links); // Entferne alle Verknüpfungen aus DB
        
        // SCHRITT 2: Lösche das Dokument selbst
        Document? doc = await this.db.Documents.FindAsync(documentId); // Finde Dokument anhand ID
        if (doc != null) // Dokument existiert?
        {
            this.db.Documents.Remove(doc); // Entferne Dokument aus DB
        }
        
        await this.db.SaveChangesAsync(); // Speichere Änderungen (beide Löschungen atomar)
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
