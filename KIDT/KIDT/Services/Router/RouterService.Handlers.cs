using Microsoft.SemanticKernel;
using System.Text.Json;
using KIDT.Database;
using KIDT.Models;

namespace KIDT.Services;

public partial class RouterService
{
    private async Task<RouterResponse> HandleAnalyzeDocumentAsync( // Verarbeitet analyze_document Tool-Call → routet zu DataAnalysis
        FunctionCallContent functionCall, DocumentDbService docDbService, List<int> lastDocIds)
    {
        int requestedDocId = 0;
        if (functionCall.Arguments != null) // Argumente vorhanden?
        {
            object? docIdObj = null;
            functionCall.Arguments.TryGetValue("docId", out docIdObj); // DocID-Argument holen
            if (docIdObj != null) int.TryParse(docIdObj.ToString(), out requestedDocId); // Parse zu int
        }

        List<Document> analysisDocs = new List<Document>(); // Dokumente für DataAnalysis

        if (requestedDocId > 0) // Spezifische DocID vorhanden?
        {
            Document doc = await docDbService.GetDocumentByIdAsync(requestedDocId); // Dokument aus DB laden
            if (doc != null && doc.Id > 0) analysisDocs.Add(doc); // Nur gültige Dokumente
        }

        if (analysisDocs.Count == 0) // Kein Dokument per ID gefunden → Fallback auf Kontext-DocIDs
        {
            foreach (int docId in lastDocIds) // Kontext-DocIDs als Fallback durchgehen
            {
                Document doc = await docDbService.GetDocumentByIdAsync(docId); // Dokument aus DB laden
                if (doc != null && doc.Id > 0) analysisDocs.Add(doc); // Nur gültige Dokumente
            }
        }

        RouterResponse response = new RouterResponse(); // Routing-Antwort für DataAnalysis aufbauen
        response.ShouldRoute = true;
        response.TargetService = "dataAnalysis";
        response.MaxTokens = 2000;
        response.FoundDocuments = analysisDocs;
        response.Reason = "DataAnalysis (analyze_document)";
        response.ToolWasUsed = true;
        return response;
    }

    private async Task<RouterResponse?> DispatchToolResultAsync( // Leitet Tool-Ergebnis an passenden Handler weiter
        string functionName, string resultText,
        DocumentDbService docDbService, CalendarService calendarService,
        List<Document> foundDocuments)
    {
        if (functionName == "search_documents") return await HandleSearchDocumentsAsync(resultText, docDbService, foundDocuments); // Dokument-Suche
        if (functionName == "list_calendar_events") return await HandleListCalendarEventsAsync(resultText, calendarService); // Termine auflisten
        if (functionName == "create_calendar_event") return HandleCreateCalendarEvent(resultText); // Termin erstellen
        if (functionName == "delete_calendar_event") return HandleDeleteCalendarEvent(resultText); // Termin löschen
        if (functionName == "update_calendar_event") return HandleUpdateCalendarEvent(resultText); // Termin aktualisieren
        if (functionName == "add_document_to_chat") return await HandleAddDocumentToChatAsync(resultText, docDbService, foundDocuments); // Dokument verknüpfen
        if (functionName == "create_folder") return HandleFolderOperationResult(resultText); // Ordner erstellen
        if (functionName == "delete_folder") return HandleFolderOperationResult(resultText); // Ordner löschen
        if (functionName == "move_document_to_folder") return HandleFolderOperationResult(resultText); // Dokument verschieben
        if (functionName == "copy_document_to_folder") return HandleFolderOperationResult(resultText); // Dokument kopieren
        if (functionName == "remove_document_from_folder") return HandleFolderOperationResult(resultText); // Dokument aus Ordner entfernen
        if (functionName == "list_documents_in_folder") return await HandleListDocumentsInFolderAsync(resultText, docDbService, foundDocuments); // Ordner-Inhalt auflisten
        if (functionName == "find_documents") return HandleFindDocumentsResult(resultText); // Dokument-Standort suchen
        if (functionName == "list_all_folders") return HandleListAllFoldersResult(resultText); // Alle Ordner auflisten
        if (functionName == "rename_folder") return HandleFolderOperationResult(resultText); // Ordner umbenennen
        return null; // Unbekannte Funktion → kein Handler
    }

    private async Task<RouterResponse> HandleSearchDocumentsAsync( // Verarbeitet search_documents Ergebnis
        string resultText, DocumentDbService docDbService, List<Document> foundDocuments)
    {
        var toolResult = JsonSerializer.Deserialize<SearchResultJson>(resultText); // JSON-Ergebnis deserialisieren

        if (toolResult == null || toolResult.documentIds == null || toolResult.found == 0) // Keine Treffer?
        {
            RouterResponse emptyResponse = new RouterResponse();
            emptyResponse.DirectResponse = "Keine Dokumente gefunden.";
            emptyResponse.Reason = "Dokumentensuche ohne Ergebnis";
            emptyResponse.ToolWasUsed = true;
            return emptyResponse;
        }

        foreach (int docId in toolResult.documentIds) // Lade vollständige Dokumente aus DB
        {
            Document doc = await docDbService.GetDocumentByIdAsync(docId); // Vollständiges Dokument laden
            if (doc != null && doc.Id > 0) foundDocuments.Add(doc); // Nur gültige Dokumente
        }

        List<string> fileNames = new List<string>(); // Dateinamen für Anzeige-Nachricht
        foreach (Document d in foundDocuments)
        {
            string name = string.Empty;
            if (d.FileName != null) name = d.FileName; // Dateiname sicher auslesen
            fileNames.Add(name);
        }

        string message = $"{toolResult.found} Dokument(e) gefunden: {string.Join(", ", fileNames)}"; // Kein DocID-Marker: wird über DocumentIdsJson gespeichert

        RouterResponse response = new RouterResponse();
        response.DirectResponse = message;
        response.FoundDocuments = foundDocuments;
        response.Reason = "Dokumentensuche";
        response.ToolWasUsed = true;
        return response;
    }

    private async Task<RouterResponse> HandleListCalendarEventsAsync( // Verarbeitet list_calendar_events Ergebnis
        string resultText, CalendarService calendarService)
    {
        var listResult = JsonSerializer.Deserialize<CalendarListResultJson>(resultText); // JSON-Ergebnis deserialisieren
        List<CalendarEvent> foundEvents = new List<CalendarEvent>(); // Geladene CalendarEvent-Objekte
        string message = "Keine Termine gefunden.";

        if (listResult != null && listResult.found > 0 && listResult.events != null) // Termine gefunden?
        {
            foreach (var e in listResult.events) // Lade vollständige Events aus DB
            {
                var fullEvent = await calendarService.GetEventByIdAsync(e.id); // Vollständiges Event per ID holen
                if (fullEvent != null) foundEvents.Add(fullEvent); // Nur gefundene Events hinzufügen
            }
            message = $"{listResult.found} Termin(e) gefunden";
        }

        RouterResponse response = new RouterResponse();
        response.DirectResponse = message;
        response.FoundEvents = foundEvents;
        response.Reason = "Termine aufgelistet";
        response.ToolWasUsed = true;
        return response;
    }

    private RouterResponse HandleCreateCalendarEvent(string resultText) // Verarbeitet create_calendar_event Ergebnis
    {
        var result = JsonSerializer.Deserialize<CalendarCreateResultJson>(resultText); // JSON-Ergebnis deserialisieren
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        if (result != null && result.success) // Termin erfolgreich erstellt?
        {
            response.DirectResponse = result.message;
            response.Reason = "Termin erstellt";
        }
        else // Fehler beim Erstellen
        {
            string errorMsg = "Termin konnte nicht erstellt werden.";
            if (result != null && result.message != null) errorMsg = result.message; // Tool-Fehlermeldung übernehmen
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Erstellung fehlgeschlagen";
        }
        return response;
    }

    private RouterResponse HandleDeleteCalendarEvent(string resultText) // Verarbeitet delete_calendar_event Ergebnis
    {
        var result = JsonSerializer.Deserialize<CalendarDeleteResultJson>(resultText); // JSON-Ergebnis deserialisieren
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        if (result != null && result.needsClarification && result.events != null) // Mehrere Treffer → Rückfrage
        {
            var lines = new List<string>(); // Termin-Optionen als Liste aufbauen
            foreach (var e in result.events) // Jeden Treffer als Auswahloption formatieren
            {
                string timeInfo = string.Empty;
                if (e.time != "Ganztägig") timeInfo = $" ({e.time})"; // Uhrzeit nur wenn nicht ganztägig
                lines.Add($"• ID {e.id}: {e.title}" + timeInfo);
            }
            string msg = string.Empty;
            if (result.message != null) msg = result.message; // Tool-Nachricht (z.B. "Welchen Termin meinst du?")
            response.DirectResponse = msg + "\n" + string.Join("\n", lines);
            response.Reason = "Mehrdeutiger Termin (Rückfrage)";
            return response;
        }

        if (result != null && result.success) // Termin erfolgreich gelöscht?
        {
            response.DirectResponse = result.message;
            response.Reason = "Termin gelöscht";
        }
        else // Fehler beim Löschen
        {
            string errorMsg = "Termin konnte nicht gelöscht werden.";
            if (result != null && result.message != null) errorMsg = result.message; // Tool-Fehlermeldung übernehmen
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Löschung fehlgeschlagen";
        }
        return response;
    }

    private RouterResponse HandleUpdateCalendarEvent(string resultText) // Verarbeitet update_calendar_event Ergebnis
    {
        var result = JsonSerializer.Deserialize<CalendarUpdateResultJson>(resultText); // JSON-Ergebnis deserialisieren
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        if (result != null && result.needsClarification && result.events != null) // Mehrere Treffer → Rückfrage
        {
            var lines = new List<string>(); // Termin-Optionen als Liste aufbauen
            foreach (var e in result.events) // Jeden Treffer als Auswahloption formatieren
            {
                string timeInfo = string.Empty;
                if (e.time != "Ganztägig") timeInfo = $" ({e.time})"; // Uhrzeit nur wenn nicht ganztägig
                lines.Add($"• ID {e.id}: {e.title}" + timeInfo);
            }
            string msg = string.Empty;
            if (result.message != null) msg = result.message; // Tool-Nachricht übernehmen
            response.DirectResponse = msg + "\n" + string.Join("\n", lines);
            response.Reason = "Mehrdeutiger Termin (Rückfrage)";
            return response;
        }

        if (result != null && result.success) // Termin erfolgreich aktualisiert?
        {
            response.DirectResponse = result.message;
            response.Reason = "Termin aktualisiert";
        }
        else // Fehler beim Aktualisieren
        {
            string errorMsg = "Termin konnte nicht aktualisiert werden.";
            if (result != null && result.message != null) errorMsg = result.message; // Tool-Fehlermeldung übernehmen
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Update fehlgeschlagen";
        }
        return response;
    }

    private RouterResponse HandleFolderOperationResult(string resultText) // Verarbeitet Ergebnis aller Ordner-Tools (create, delete, move, copy, remove)
    {
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        try
        {
            var result = JsonSerializer.Deserialize<FolderResultJson>(resultText); // JSON-Ergebnis deserialisieren

            if (result != null && result.success) // Tool wurde aufgerufen UND hat erfolgreich abgeschlossen
            {
                response.DirectResponse = result.message ?? "Operation ausgeführt.";
                response.Reason = "Ordner-Operation erfolgreich";
            }
            else // Tool wurde aufgerufen, aber die Operation ist fehlgeschlagen
            {
                string errorMsg = "Die Operation konnte nicht ausgeführt werden.";
                if (result != null && result.message != null) errorMsg = result.message; // Tool-Fehlermeldung übernehmen
                response.DirectResponse = errorMsg;
                response.Reason = "Ordner-Operation fehlgeschlagen";
            }
        }
        catch // JSON-Parse-Fehler: Fallback-Antwort
        {
            response.DirectResponse = "Fehler beim Verarbeiten der Antwort.";
            response.Reason = "Ordner-Operation Fehler";
        }
        return response;
    }

    private async Task<RouterResponse> HandleListDocumentsInFolderAsync( // Verarbeitet list_documents_in_folder → gibt Dokument-Cards zurück wie search_documents
        string resultText, DocumentDbService docDbService, List<Document> foundDocuments)
    {
        var listResult = JsonSerializer.Deserialize<FolderListResultJson>(resultText); // JSON-Ergebnis deserialisieren

        if (listResult == null || !listResult.success || listResult.documents == null || listResult.found == 0) // Keine Dokumente?
        {
            RouterResponse emptyResponse = new RouterResponse();
            string msg = "Keine Dokumente im Ordner gefunden.";
            if (listResult != null && listResult.message != null) msg = listResult.message; // Tool-Nachricht übernehmen
            emptyResponse.DirectResponse = msg;
            emptyResponse.Reason = "Ordner-Inhalt leer";
            emptyResponse.ToolWasUsed = true;
            return emptyResponse;
        }

        foreach (var docInfo in listResult.documents) // Vollständige Dokumente aus DB laden (für Thumbnails etc.)
        {
            Document doc = await docDbService.GetDocumentByIdAsync(docInfo.id); // Vollständiges Dokument per ID laden
            if (doc != null && doc.Id > 0) foundDocuments.Add(doc); // Nur gültige Dokumente
        }

        string baseMsg;
        if (listResult.message != null) // Spezifische Nachricht vorhanden?
        {
            baseMsg = listResult.message;
        }
        else
        {
            baseMsg = $"{listResult.found} Dokument(e) gefunden";
        }
        string message = baseMsg; // Kein DocID-Marker: DocIDs werden über FoundDocuments.DocumentIdsJson gespeichert

        RouterResponse response = new RouterResponse();
        response.DirectResponse = message;
        response.FoundDocuments = foundDocuments;
        response.Reason = "Ordner-Inhalt";
        response.ToolWasUsed = true;
        return response;
    }

    private RouterResponse HandleListAllFoldersResult(string resultText) // Verarbeitet list_all_folders → natürlichsprachliche Ordnerliste
    {
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        try
        {
            var result = JsonSerializer.Deserialize<AllFoldersResultJson>(resultText); // JSON-Ergebnis deserialisieren

            if (result == null || !result.success || result.folders == null || result.found == 0) // Keine Ordner?
            {
                string noFolders = "Es gibt noch keine Ordner.";
                if (result != null && result.message != null) noFolders = result.message; // Tool-Nachricht übernehmen
                response.DirectResponse = noFolders;
                response.Reason = "Ordnerliste leer";
                return response;
            }

            var lines = new List<string>(); // Ordner als Liste aufbauen
            foreach (FolderItemJson f in result.folders) // Jeden Ordner als Listeneintrag formatieren
            {
                string docLabel;
                if (f.documentCount == 1) // Singular oder Plural?
                {
                    docLabel = "1 Dokument";
                }
                else
                {
                    docLabel = $"{f.documentCount} Dokumente";
                }
                lines.Add($"• {f.name} ({docLabel})"); // Ordner-Eintrag mit Dokumentanzahl
            }

            response.DirectResponse = $"{result.found} Ordner vorhanden:\n{string.Join("\n", lines)}"; // Gesamtergebnis zusammenbauen
            response.Reason = "Ordner aufgelistet";
        }
        catch // JSON-Parse-Fehler
        {
            response.DirectResponse = "Fehler beim Laden der Ordnerliste.";
            response.Reason = "list_all_folders Fehler";
        }

        return response;
    }

    private RouterResponse HandleFindDocumentsResult(string resultText) // Verarbeitet find_documents → natürlichsprachliche Standort-Aussage
    {
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        try
        {
            var result = JsonSerializer.Deserialize<FindDocumentsResultJson>(resultText); // JSON-Ergebnis deserialisieren

            if (result == null || !result.success || result.documents == null || result.found == 0) // Keine Treffer?
            {
                string noFound = "Kein Dokument mit diesem Namen gefunden.";
                if (result != null && result.message != null) noFound = result.message; // Tool-Nachricht übernehmen
                response.DirectResponse = noFound;
                response.Reason = "Dokument-Suche ohne Ergebnis";
                return response;
            }

            var sentences = new List<string>(); // Sätze pro Dokument aufbauen

            foreach (var doc in result.documents) // Jeden gefundenen Dokument-Eintrag verarbeiten
            {
                List<string> locations = new List<string>(); // Standorte dieses Dokuments
                if (doc.inFolders != null)
                {
                    foreach (string loc in doc.inFolders) locations.Add(loc); // Ordner-Zugehörigkeiten übernehmen
                }
                if (locations.Count == 0) locations.Add("Hauptbereich"); // Kein Ordner → Hauptbereich als Fallback

                // --- Natürlichsprachliche Formulierung ---
                string sentence = string.Empty;

                if (locations.Count == 1 && locations[0] == "Hauptbereich") // Nur im Hauptbereich
                {
                    sentence = $"Die Datei {doc.fileName} liegt nur im Hauptbereich (kein Ordner zugewiesen).";
                }
                else if (locations.Count == 1) // Genau ein Ordner
                {
                    sentence = $"Die Datei {doc.fileName} liegt im Ordner {locations[0]}.";
                }
                else // Mehrere Standorte
                {
                    var ordnerListe = new List<string>(); // Echte Ordner (ohne Hauptbereich)
                    bool inHauptbereich = false;

                    foreach (string loc in locations) // Standorte klassifizieren
                    {
                        if (loc == "Hauptbereich") inHauptbereich = true; // Hauptbereich-Flag setzen
                        else ordnerListe.Add(loc); // Echte Ordner sammeln
                    }

                    string ordnerText = string.Join(" und ", ordnerListe); // Ordnernamen verbinden

                    if (inHauptbereich && ordnerListe.Count == 0) // Nur Hauptbereich (mehrfach gelistet)
                        sentence = $"Die Datei {doc.fileName} liegt nur im Hauptbereich.";
                    else if (inHauptbereich && ordnerListe.Count == 1) // Hauptbereich + 1 Ordner
                        sentence = $"Die Datei {doc.fileName} liegt im Ordner {ordnerText} und im Hauptbereich.";
                    else if (inHauptbereich) // Hauptbereich + mehrere Ordner
                        sentence = $"Die Datei {doc.fileName} liegt in den Ordnern {ordnerText} und im Hauptbereich.";
                    else if (ordnerListe.Count == 1) // Genau 1 Ordner (kein Hauptbereich)
                        sentence = $"Die Datei {doc.fileName} liegt im Ordner {ordnerText}.";
                    else // Mehrere Ordner (kein Hauptbereich)
                        sentence = $"Die Datei {doc.fileName} liegt in den Ordnern {ordnerText}.";
                }

                sentences.Add(sentence); // Formulierten Satz zur Liste
            }

            response.DirectResponse = string.Join(" ", sentences); // Alle Sätze zu einem Text zusammenführen
            response.Reason = "Dokument-Standort";
        }
        catch // JSON-Parse-Fehler
        {
            response.DirectResponse = "Fehler beim Auslesen der Ordner-Zugehörigkeit.";
            response.Reason = "find_documents Fehler";
        }

        return response;
    }

    private async Task<RouterResponse> HandleAddDocumentToChatAsync( // Verarbeitet add_document_to_chat Ergebnis
        string resultText, DocumentDbService docDbService, List<Document> foundDocuments)
    {
        var addResult = JsonSerializer.Deserialize<AddDocumentResultJson>(resultText); // JSON-Ergebnis deserialisieren
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        bool alreadyLinked = addResult != null && addResult.message != null && addResult.message.Contains("bereits"); // Dokument bereits verknüpft?
        bool isSuccess = addResult != null && (addResult.success || alreadyLinked); // Erfolg oder bereits verknüpft?

        if (isSuccess) // Dokument verfügbar?
        {
            Document docTemp = await docDbService.GetDocumentByIdAsync(addResult!.documentId); // Vollständiges Dokument aus DB laden
            bool docFound = false;
            Document doc = new Document();
            if (docTemp != null && docTemp.Id > 0) // Gültiges Dokument?
            {
                doc = docTemp;
                docFound = true;
            }
            if (docFound) foundDocuments.Add(doc); // Dokument zur Ergebnis-Liste

            if (docFound) // Dokument gefunden → zu DataAnalysis routen
            {
                response.ShouldRoute = true;
                response.TargetService = "dataAnalysis";
                response.FoundDocuments = foundDocuments;
                response.MaxTokens = 2000;
                response.Reason = "Dokument verknüpft → DataAnalysis";
                return response;
            }

            string fileName = "Unbekannt";
            if (addResult!.fileName != null) fileName = addResult.fileName; // Dateiname sicher auslesen
            response.DirectResponse = $"Dokument '{fileName}' wurde zum Chat hinzugefügt.";
            response.FoundDocuments = foundDocuments;
            response.Reason = "Dokument hinzugefügt";
        }
        else // Fehler beim Hinzufügen
        {
            string errorMsg = "Dokument konnte nicht hinzugefügt werden.";
            if (addResult != null && addResult.message != null) errorMsg = addResult.message; // Tool-Fehlermeldung übernehmen
            response.DirectResponse = errorMsg;
            response.Reason = "Dokument-Verknüpfung fehlgeschlagen";
        }
        return response;
    }
}
