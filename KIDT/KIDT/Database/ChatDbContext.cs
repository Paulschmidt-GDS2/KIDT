using Microsoft.EntityFrameworkCore;
using KIDT.Models;

namespace KIDT.Database;

public class ChatDbContext : DbContext
{
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<Document> Documents { get; set; }
    public DbSet<ConversationDocument> ConversationDocuments { get; set; }
    public DbSet<CalendarEvent> CalendarEvents { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // MySQL Connection String
        // WICHTIG: Für Multi-User einfach "localhost" durch Server-IP ersetzen!
        string connectionString =
            "Server=localhost;" +           // Teamkollege: Ändere zu Tailscale-IP (100.75.19.37)
            "Port=3306;" +
            "Database=kidt_chat;" +
            "User=root;" +                  // Teamkollege: Ändere zu "kidt_user"
            "Password=kidt123;" +
            "AllowUserVariables=true;";

        // 1. Start-Process "https://tailscale.com/download/windows"
        // 2. Einloggen mit Google/MS Account und Connect klicken
        // 3. Ich schalte dich frei
        // 4. IP ändern wie im Kommentar oben

        options.UseMySQL(connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) // Konfiguriere Models
    {
        // DateTime-Spalten als DATETIME(6) mit Default-Wert CURRENT_TIMESTAMP
        modelBuilder.Entity<Conversation>()
            .Property(c => c.CreatedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)"); // Default bei Insert

        modelBuilder.Entity<Message>()
            .Property(m => m.Timestamp)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");

        modelBuilder.Entity<Document>()
            .Property(d => d.UploadedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");

        modelBuilder.Entity<ConversationDocument>()
            .Property(cd => cd.AddedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");

        // CalendarEvent DateTime-Spalten
        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.Start)
            .HasColumnType("datetime(6)");

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.CreatedAt)
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");

        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.UpdatedAt)
            .HasColumnType("datetime(6)");

        // CalendarEvent: TimeSpan als BIGINT (Ticks) speichern und konvertieren
        modelBuilder.Entity<CalendarEvent>()
            .Property(ce => ce.Time)
            .HasColumnType("bigint")
            .HasConversion(
                v => v.Ticks,  // TimeSpan -> BIGINT (Ticks)
                v => TimeSpan.FromTicks(v)); // BIGINT (Ticks) -> TimeSpan

        // Relationships explizit konfigurieren
        modelBuilder.Entity<Message>()
            .HasOne(m => m.Conversation) // Message hat eine Conversation
            .WithMany(c => c.Messages) // Conversation hat viele Messages
            .HasForeignKey(m => m.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Messages löschen

        // ConversationDocument: Junction-Tabelle mit Composite Key
        modelBuilder.Entity<ConversationDocument>()
            .HasKey(cd => new { cd.ConversationId, cd.DocumentId }); // Composite Primary Key

        modelBuilder.Entity<ConversationDocument>()
            .HasOne(cd => cd.Conversation) // ConversationDocument hat eine Conversation
            .WithMany() // Conversation hat viele ConversationDocuments (keine Navigation Property nötig)
            .HasForeignKey(cd => cd.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Links löschen

        modelBuilder.Entity<ConversationDocument>()
            .HasOne(cd => cd.Document) // ConversationDocument hat ein Document
            .WithMany(d => d.ConversationDocuments) // Document hat viele ConversationDocuments
            .HasForeignKey(cd => cd.DocumentId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Document-Löschung auch Links löschen

        // Document: FileHash muss unique sein (verhindert Duplikate)
        modelBuilder.Entity<Document>()
            .HasIndex(d => d.FileHash)
            .IsUnique();
    }
}