using System;
using System.IO;
using System.Threading.Tasks;

#if WINDOWS
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
#endif

namespace KIDT.Services;

public class ThumbnailGenerator // Service zum Generieren von Datei-Thumbnails
{
    public async Task<string> GenerateThumbnailAsync(string filePath) // Generiert Thumbnail für Datei
    {
        string extension = Path.GetExtension(filePath).ToLower(); // Hole Datei-Endung (klein geschrieben)
        
        if (extension == ".pdf") // Check: Ist es eine PDF?
        {
            return await GeneratePdfThumbnailAsync(filePath); // Ja -> Generiere PDF-Thumbnail
        }
        else // Andere Dateitypen (TXT, MD, JSON)
        {
            return string.Empty; // Kein Thumbnail -> Icon wird in UI verwendet
        }
    }
    
    private async Task<string> GeneratePdfThumbnailAsync(string filePath) // Generiert Thumbnail für PDF (erste Seite)
    {
#if WINDOWS
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath); // Lade PDF-Datei
            PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file); // Öffne PDF
            
            if (pdfDoc.PageCount == 0) // Check: Hat PDF keine Seiten?
            {
                return string.Empty; // Gib leer zurück
            }
            
            PdfPage page = pdfDoc.GetPage(0); // Hole erste Seite
            
            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream(); // Erstelle Memory-Stream
            
            PdfPageRenderOptions options = new PdfPageRenderOptions(); // Render-Optionen
            options.DestinationWidth = 200; // Thumbnail-Breite 200px
            
            await page.RenderToStreamAsync(stream, options); // Rendere Seite zu Stream
            
            stream.Seek(0); // Stream zurücksetzen
            
            byte[] bytes = new byte[stream.Size]; // Byte-Array für Bild
            IBuffer buffer = await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None); // Lese Stream
            
            string base64 = Convert.ToBase64String(bytes); // Konvertiere zu Base64
            
            return base64; // Gib Base64-String zurück
        }
        catch (Exception ex)
        {
            throw new Exception("Fehler beim Generieren des PDF-Thumbnails: " + ex.Message);
        }
#else
        await Task.Delay(1); // Placeholder für nicht-Windows-Plattformen
        return string.Empty;
#endif
    }
}


