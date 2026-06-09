using Microsoft.EntityFrameworkCore;
using KIDT.Models;

namespace KIDT.Database;

public class ChatDbContext : DbContext // EF Core DbContext: zentrale Datenbankschnittstelle
{
    public DbSet<Conversation> Conversations { get; set; } // Tabelle: Chats
    public DbSet<Message> Messages { get; set; } // Tabelle: Nachrichten
    public DbSet<Document> Documents { get; set; } // Tabelle: Dokumente
    public DbSet<ConversationDocument> ConversationDocuments { get; set; } // Tabelle: Chat↔Dokument-Verknüpfungen
    public DbSet<CalendarEvent> CalendarEvents { get; set; } // Tabelle: Kalender-Termine
    public DbSet<Folder> Folders { get; set; } // Tabelle: Ordner
    public DbSet<DocumentFolder> DocumentFolders { get; set; } // Junction: Dokument↔Ordner (n:m)

    protected override void OnConfiguring(DbContextOptionsBuilder options) // Konfiguriert MySQL-Verbindung
    {
        string connectionString = // WICHTIG: Für Multi-User einfach "localhost" durch Server-IP ersetzen!
            "Server=localhost;" +           // Teamkollege: Ändere zu Tailscale-IP (100.75.19.37)
            "Port=3306;" +
            "Database=kidt_chat;" +
            "User=root;" +                  // Teamkollege: Ändere zu "kidt_user"
            "Password=kidt123;" +
            "AllowUserVariables=true;"; // 1. Tailscale: https://tailscale.com/download/windows | 2. Einloggen + Connect | 3. Ich schalte dich frei | 4. IP oben ändern

        options.UseMySQL(connectionString); // MySQL-Provider aktivieren
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) // Konfiguriere EF-Modelle, Spaltentypen und Beziehungen
    {
        // --- DateTime-Spalten ---
        modelBuilder.Entity<Conversation>()
            .Property(c => c.CreatedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // Default bei Insert

        modelBuilder.Entity<Message>()
            .Property(m => m.Timestamp)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // Default bei Insert

        modelBuilder.Entity<Document>()
            .Property(d => d.UploadedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // Default bei Insert

        modelBuilder.Entity<ConversationDocument>()
            .Property(cd => cd.AddedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // Default bei Insert

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.Start)
            .HasColumnType("datetime(6)"); // CalendarEvent: Start-Datum

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.CreatedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // CalendarEvent: Erstellungszeitpunkt

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.UpdatedAt)
            .HasColumnType("datetime(6)"); // CalendarEvent: letztes Update

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.Time)
            .HasColumnType("bigint")
            .HasConversion(
                v => v.Ticks,               // TimeSpan -> BIGINT (Ticks)
                v => TimeSpan.FromTicks(v)); // BIGINT (Ticks) -> TimeSpan

        // --- Relationships ---
        modelBuilder.Entity<Message>()
            .HasOne(m => m.Conversation)    // Message hat eine Conversation
            .WithMany(c => c.Messages)      // Conversation hat viele Messages
            .HasForeignKey(m => m.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Messages löschen

        modelBuilder.Entity<ConversationDocument>()
            .HasKey(cd => new { cd.ConversationId, cd.DocumentId }); // Composite Primary Key

        modelBuilder.Entity<ConversationDocument>()
            .HasOne(cd => cd.Conversation)  // ConversationDocument hat eine Conversation
            .WithMany()                     // Keine Navigation Property nötig
            .HasForeignKey(cd => cd.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Links löschen

        modelBuilder.Entity<ConversationDocument>()
            .HasOne(cd => cd.Document)      // ConversationDocument hat ein Document
            .WithMany(d => d.ConversationDocuments) // Document hat viele ConversationDocuments
            .HasForeignKey(cd => cd.DocumentId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Document-Löschung auch Links löschen

        modelBuilder.Entity<Document>()
            .HasIndex(d => d.FileHash)
            .IsUnique(); // FileHash muss unique sein (verhindert Duplikate)

        modelBuilder.Entity<DocumentFolder>()
            .HasKey(df => new { df.DocumentId, df.FolderId }); // Composite Primary Key (DocumentId + FolderId)

        modelBuilder.Entity<DocumentFolder>()
            .HasOne(df => df.Document)      // DocumentFolder hat ein Document
            .WithMany(d => d.DocumentFolders) // Document hat viele DocumentFolders
            .HasForeignKey(df => df.DocumentId)
            .OnDelete(DeleteBehavior.Cascade); // Dokument gelöscht → alle Ordner-Links weg

        modelBuilder.Entity<DocumentFolder>()
            .HasOne(df => df.Folder)        // DocumentFolder hat einen Folder
            .WithMany()                     // Folder braucht keine Navigation
            .HasForeignKey(df => df.FolderId)
            .OnDelete(DeleteBehavior.Cascade); // Ordner gelöscht → alle Links weg
    }
}