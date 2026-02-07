using Microsoft.EntityFrameworkCore;
using KIDT.Models;

namespace KIDT.Database;

public class ChatDbContext : DbContext
{
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<Message> Messages { get; set; }
    public DbSet<UploadedFile> UploadedFiles { get; set; }
    public DbSet<Document> Documents { get; set; }
    public DbSet<ConversationDocument> ConversationDocuments { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // MySQL LOKAL statt Cloud
        string connectionString =
            "Server=localhost;" +           // Lokal auf diesem PC
            "Port=3306;" +                  // Standard MySQL Port
            "Database=kidt_chat;" +         // Deine Datenbank-Name
            "User=root;" +                  // MySQL User (oder kidt_user)
            "Password=kidt123;" +           // Dein Root-Passwort
            "AllowUserVariables=true;";     // Für EF Core
        
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
            
        modelBuilder.Entity<UploadedFile>()
            .Property(u => u.UploadedAt)
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
            
        // Relationships explizit konfigurieren
        modelBuilder.Entity<Message>()
            .HasOne(m => m.Conversation) // Message hat eine Conversation
            .WithMany(c => c.Messages) // Conversation hat viele Messages
            .HasForeignKey(m => m.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Messages löschen
            
        modelBuilder.Entity<UploadedFile>()
            .HasOne(f => f.Conversation) // UploadedFile hat eine Conversation
            .WithMany(c => c.UploadedFiles) // Conversation hat viele UploadedFiles
            .HasForeignKey(f => f.ConversationId) // Foreign Key
            .OnDelete(DeleteBehavior.Cascade); // Bei Conversation-Löschung auch Files löschen
            
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
