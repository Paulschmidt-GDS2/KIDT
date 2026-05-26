using System.Collections.Concurrent;

namespace KIDT.Services;

/// <summary>
/// TypewriterStreamingService: Implementiert character-by-character Streaming mit konfigurierbarem Delay.
/// Buffered Tokens vom Modell und gibt sie mit natürlichem Typewriter-Effekt aus.
/// Verhindert Stauung: Tokens kommen schneller rein als sie ausgegeben werden (gewollt).
/// </summary>
public interface ITypewriterStreamingService
{
    IAsyncEnumerable<TypewriterChunk> ProcessStreamAsync(IAsyncEnumerable<string> tokenStream);
    void SetDelay(int delayMs, int varianceMs = 0);
}

public class TypewriterStreamingService : ITypewriterStreamingService
{
    private int baseDelayMs; // Basis-Delay zwischen Zeichen
    private int varianceMs; // Zufalls-Varianz für natürlichen Effekt
    private readonly Random random; // Random-Generator für Varianz

    public TypewriterStreamingService() // Konstruktor: Standard-Settings
    {
        this.baseDelayMs = 18; // 18ms Standard-Delay
        this.varianceMs = 5; // ±5ms Varianz für natürlichen Effekt
        this.random = new Random();
    }

    public void SetDelay(int delayMs, int varianceMs = 0) // Methode: Delay-Settings anpassen
    {
        this.baseDelayMs = Math.Max(1, delayMs); // Minimum 1ms
        this.varianceMs = Math.Max(0, varianceMs); // Varianz nicht negativ
    }

    public async IAsyncEnumerable<TypewriterChunk> ProcessStreamAsync(IAsyncEnumerable<string> tokenStream) // Hauptmethode: Verarbeitet Token-Stream zu Typewriter-Chunks
    {
        var charQueue = new ConcurrentQueue<char>(); // Buffer für Zeichen (Thread-Safe)
        bool isStreamComplete = false; // Flag: Ist Input-Stream fertig?
        bool isFirstChar = true; // Flag: Erstes Zeichen (sofort ausgeben)

        // --- Token-Producer (Background-Task) ---
        var tokenTask = Task.Run(async () => // Background-Task: Sammelt Tokens und fügt Zeichen zu Queue hinzu
        {
            try
            {
                await foreach (var token in tokenStream) // Durchlaufe alle Tokens vom Modell
                {
                    if (!string.IsNullOrEmpty(token)) // Token hat Content?
                    {
                        foreach (char c in token) // Jedes Zeichen einzeln in Queue
                        {
                            charQueue.Enqueue(c); // Thread-Safe Queue-Operation
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TYPEWRITER] Token-Producer-Fehler: {ex.Message}");
            }
            finally
            {
                isStreamComplete = true; // Input-Stream ist fertig
                System.Diagnostics.Debug.WriteLine("[TYPEWRITER] Token-Stream abgeschlossen");
            }
        });

        // --- Char-Consumer (Main-Loop) ---
        while (!isStreamComplete || !charQueue.IsEmpty) // Solange Stream läuft oder Queue nicht leer
        {
            if (charQueue.TryDequeue(out char currentChar)) // Zeichen aus Queue holen
            {
                if (isFirstChar) // Erstes Zeichen?
                {
                    yield return new TypewriterChunk // Sofort ausgeben (gefühlte Sofort-Reaktion)
                    {
                        Character = currentChar,
                        IsComplete = false,
                        DelayApplied = 0
                    };

                    isFirstChar = false; // Flag zurücksetzen
                    System.Diagnostics.Debug.WriteLine($"[TYPEWRITER] Erstes Zeichen sofort: '{currentChar}'");
                }
                else // Normale Zeichen mit Delay
                {
                    int actualDelay = CalculateDelay(); // Berechne Delay mit Varianz

                    await Task.Delay(actualDelay); // Typewriter-Delay anwenden

                    yield return new TypewriterChunk // Zeichen mit Delay ausgeben
                    {
                        Character = currentChar,
                        IsComplete = false,
                        DelayApplied = actualDelay
                    };
                }
            }
            else // Queue leer, aber Stream noch aktiv
            {
                await Task.Delay(5); // Kurz warten auf neue Tokens (Non-Blocking)
            }
        }

        await tokenTask; // Warte auf Token-Producer-Completion

        yield return new TypewriterChunk // Finaler Chunk: Stream komplett
        {
            Character = '\0', // Null-Character als End-Marker
            IsComplete = true,
            DelayApplied = 0
        };

        System.Diagnostics.Debug.WriteLine("[TYPEWRITER] ✅ Typewriter-Stream abgeschlossen");
    }

    private int CalculateDelay() // Hilfsmethode: Berechnet Delay mit natürlicher Varianz
    {
        if (this.varianceMs <= 0) // Keine Varianz?
        {
            return this.baseDelayMs; // Konstanter Delay
        }

        int variance = this.random.Next(-this.varianceMs, this.varianceMs + 1); // Zufalls-Varianz: ±varianceMs
        int delay = this.baseDelayMs + variance; // Basis + Varianz

        return Math.Max(1, delay); // Minimum 1ms garantieren
    }

    // --- Zusätzliche Utility-Methoden ---

    public static async IAsyncEnumerable<string> ConvertToTextStream(IAsyncEnumerable<TypewriterChunk> typewriterStream) // Utility: Konvertiert Typewriter-Chunks zurück zu Text-Stream
    {
        var textBuffer = new System.Text.StringBuilder(); // Buffer für Text

        await foreach (var chunk in typewriterStream) // Durchlaufe alle Chunks
        {
            if (chunk.IsComplete) // Stream komplett?
            {
                if (textBuffer.Length > 0) // Noch Text im Buffer?
                {
                    yield return textBuffer.ToString(); // Letzter Text
                    textBuffer.Clear();
                }
                break; // Stream beenden
            }

            if (chunk.Character != '\0') // Normales Zeichen (nicht End-Marker)?
            {
                textBuffer.Append(chunk.Character); // Zu Buffer hinzufügen

                if (textBuffer.Length >= 10 || chunk.Character == ' ' || chunk.Character == '\n') // Buffer voll oder Wort-Ende?
                {
                    yield return textBuffer.ToString(); // Text ausgeben
                    textBuffer.Clear(); // Buffer leeren
                }
            }
        }
    }
}

// === RESULT-KLASSEN ===

public class TypewriterChunk // Einzelner Typewriter-Chunk (ein Zeichen + Metadata)
{
    public char Character { get; set; } = '\0'; // Einzelnes Zeichen
    public bool IsComplete { get; set; } = false; // Ist der gesamte Stream komplett?
    public int DelayApplied { get; set; } = 0; // Angewendeter Delay in ms (Debug-Info)
}