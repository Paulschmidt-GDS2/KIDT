using System.Text;
using UglyToad.PdfPig;

namespace KIDT.Services;

public class FileService // Klasse für Datei-Operationen
{
    public async Task<string> ExtractTextAsync(string filePath) // Hauptmethode: Lädt Datei und gibt Text zurück
    {
        try
        {
            if (!File.Exists(filePath)) // Existiert eine Datei?
            {
                return "Fehler: Datei nicht gefunden.";
            }

            var fileInfo = new FileInfo(filePath); // Datei-Infos holen (Größe, etc.)
            long maxSize = 4 * 1024 * 1024; // 4 MB Limit in Bytes
            if (fileInfo.Length > maxSize) // Ist Datei zu groß?
            {
                long sizeMB = fileInfo.Length / (1024 * 1024); // Größe in MB berechnen
                return $"Fehler: Datei ist zu groß ({sizeMB} MB). Maximum: 4 MB.";
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant(); // Dateierweiterung holen (z.B. ".pdf") und zu lowercase
            string result;

            switch (extension)
            {
                case ".pdf": // PDF-Datei erkannt
                    result = await ExtractFromPdfAsync(filePath); // Extrahiere mit PdfPig
                    break;

                case ".txt": // Text-Datei erkannt
                    result = await File.ReadAllTextAsync(filePath, Encoding.UTF8); // Lese direkt als UTF-8
                    break;

                case ".md": // Markdown-Datei erkannt
                    result = await File.ReadAllTextAsync(filePath, Encoding.UTF8); // Lese direkt als UTF-8
                    break;

                case ".json": // JSON-Datei erkannt
                    result = await File.ReadAllTextAsync(filePath, Encoding.UTF8); // Lese direkt als UTF-8
                    break;

                default: // Unbekannter Dateityp
                    return "Fehler: Dateityp nicht unterstützt. Unterstützt: PDF, TXT, MD, JSON.";
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Fehler beim Lesen der Datei: {ex.Message}";
        }
    }

    private async Task<string> ExtractFromPdfAsync(string filePath) // PDF-Extraktion: Öffnet PDF und holt Text aller Seiten
    {
        return await Task.Run(() => // Läuft in eigenem Thread (UI nicht blockieren)
        {
            try
            {
                using var document = PdfDocument.Open(filePath); // Öffne PDF mit PdfPig
                var textBuilder = new StringBuilder();

                foreach (var page in document.GetPages()) // Durchlaufe alle Seiten
                {
                    textBuilder.AppendLine(page.Text); // Text der Seite hinzufügen (OHNE Seiten-Marker - spart Tokens!)
                    textBuilder.AppendLine(); // Leerzeile nach jeder Seite
                }

                var extractedText = textBuilder.ToString();

                string[] separators = new string[] { " ", "\t", "\n" };
                var words = extractedText.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                int wordCount = words.Length;

                if (wordCount > 3000) // Check: PDF zu lang für Token-Limit?
                {
                    string warning = $"[WARNUNG: Diese PDF ist sehr lang ({wordCount} Wörter). Das Modell kann möglicherweise nicht alles verarbeiten.]\n\n";
                    extractedText = warning + extractedText; // Warnung vor Text setzen
                }

                return extractedText;
            }
            catch (Exception ex)
            {
                return $"Fehler beim Lesen der PDF: {ex.Message}";
            }
        });
    }
}
