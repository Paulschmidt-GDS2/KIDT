using Microsoft.EntityFrameworkCore;
using KIDT.Models;

namespace KIDT.Data;

public class ChatDbContext : DbContext // Datenbank-Context
{
    public DbSet<Conversation> Conversations { get; set; } // Tabelle: Conversations
    public DbSet<Message> Messages { get; set; } // Tabelle: Messages
    public DbSet<UploadedFile> UploadedFiles { get; set; } // Tabelle: UploadedFiles

    protected override void OnConfiguring(DbContextOptionsBuilder options) // Verbindung zu PostgreSQL
    {
        options.UseNpgsql("Host=localhost;Database=KIDT_Chats;Username=KIDT_App;Password=kidt123"); // Connection String
    }
}