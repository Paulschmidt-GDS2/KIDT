namespace KIDT.Services;

// Stub für Plattformen, die SemanticKernel/MCP nicht unterstützen (Android, iOS, MacCatalyst)
public class ChatMcpService : IAsyncDisposable
{
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task<string> SendAsync(string userMessage)
    {
        return Task.FromResult("MCP-Chat ist nur auf Windows verfügbar.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
