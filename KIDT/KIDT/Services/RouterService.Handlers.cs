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
            foreach (int docId in lastDocIds)
            {
                Document doc = await docDbService.GetDocumentByIdAsync(docId);
                if (doc != null && doc.Id > 0) analysisDocs.Add(doc);
            }
        }

        RouterResponse response = new RouterResponse();
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
        if (functionName == "search_documents") return await HandleSearchDocumentsAsync(resultText, docDbService, foundDocuments);
        if (functionName == "list_calendar_events") return await HandleListCalendarEventsAsync(resultText, calendarService);
        if (functionName == "create_calendar_event") return HandleCreateCalendarEvent(resultText);
        if (functionName == "delete_calendar_event") return HandleDeleteCalendarEvent(resultText);
        if (functionName == "update_calendar_event") return HandleUpdateCalendarEvent(resultText);
        if (functionName == "add_document_to_chat") return await HandleAddDocumentToChatAsync(resultText, docDbService, foundDocuments);
        return null;
    }

    private async Task<RouterResponse> HandleSearchDocumentsAsync( // Verarbeitet search_documents Ergebnis
        string resultText, DocumentDbService docDbService, List<Document> foundDocuments)
    {
        var toolResult = JsonSerializer.Deserialize<SearchResultJson>(resultText);

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
            Document doc = await docDbService.GetDocumentByIdAsync(docId);
            if (doc != null && doc.Id > 0) foundDocuments.Add(doc);
        }

        List<string> fileNames = new List<string>();
        List<int> docIds = new List<int>();
        foreach (Document d in foundDocuments)
        {
            string name = string.Empty;
            if (d.FileName != null) name = d.FileName;
            fileNames.Add(name);
            docIds.Add(d.Id);
        }

        string message = $"{toolResult.found} Dokument(e) gefunden: {string.Join(", ", fileNames)} [DocID: {string.Join(",", docIds)}]";

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
        var listResult = JsonSerializer.Deserialize<CalendarListResultJson>(resultText);
        List<CalendarEvent> foundEvents = new List<CalendarEvent>();
        string message = "Keine Termine gefunden.";

        if (listResult != null && listResult.found > 0 && listResult.events != null) // Termine gefunden?
        {
            foreach (var e in listResult.events) // Lade vollständige Events aus DB
            {
                var fullEvent = await calendarService.GetEventByIdAsync(e.id);
                if (fullEvent != null) foundEvents.Add(fullEvent);
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
        var result = JsonSerializer.Deserialize<CalendarCreateResultJson>(resultText);
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
            if (result != null && result.message != null) errorMsg = result.message;
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Erstellung fehlgeschlagen";
        }
        return response;
    }

    private RouterResponse HandleDeleteCalendarEvent(string resultText) // Verarbeitet delete_calendar_event Ergebnis
    {
        var result = JsonSerializer.Deserialize<CalendarDeleteResultJson>(resultText);
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        if (result != null && result.needsClarification && result.events != null) // Mehrere Treffer → Rückfrage
        {
            var lines = new List<string>();
            foreach (var e in result.events)
            {
                string timeInfo = string.Empty;
                if (e.time != "Ganztägig") timeInfo = $" ({e.time})";
                lines.Add($"• ID {e.id}: {e.title}" + timeInfo);
            }
            string msg = string.Empty;
            if (result.message != null) msg = result.message;
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
            if (result != null && result.message != null) errorMsg = result.message;
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Löschung fehlgeschlagen";
        }
        return response;
    }

    private RouterResponse HandleUpdateCalendarEvent(string resultText) // Verarbeitet update_calendar_event Ergebnis
    {
        var result = JsonSerializer.Deserialize<CalendarUpdateResultJson>(resultText);
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        if (result != null && result.needsClarification && result.events != null) // Mehrere Treffer → Rückfrage
        {
            var lines = new List<string>();
            foreach (var e in result.events)
            {
                string timeInfo = string.Empty;
                if (e.time != "Ganztägig") timeInfo = $" ({e.time})";
                lines.Add($"• ID {e.id}: {e.title}" + timeInfo);
            }
            string msg = string.Empty;
            if (result.message != null) msg = result.message;
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
            if (result != null && result.message != null) errorMsg = result.message;
            response.DirectResponse = errorMsg;
            response.Reason = "Termin-Update fehlgeschlagen";
        }
        return response;
    }

    private async Task<RouterResponse> HandleAddDocumentToChatAsync( // Verarbeitet add_document_to_chat Ergebnis
        string resultText, DocumentDbService docDbService, List<Document> foundDocuments)
    {
        var addResult = JsonSerializer.Deserialize<AddDocumentResultJson>(resultText);
        RouterResponse response = new RouterResponse();
        response.ToolWasUsed = true;

        bool alreadyLinked = addResult != null && addResult.message != null && addResult.message.Contains("bereits");
        bool isSuccess = addResult != null && (addResult.success || alreadyLinked); // Erfolg oder bereits verknüpft?

        if (isSuccess) // Dokument verfügbar?
        {
            Document docTemp = await docDbService.GetDocumentByIdAsync(addResult!.documentId);
            bool docFound = false;
            Document doc = new Document();
            if (docTemp != null && docTemp.Id > 0)
            {
                doc = docTemp;
                docFound = true;
            }
            if (docFound) foundDocuments.Add(doc);

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
            if (addResult!.fileName != null) fileName = addResult.fileName;
            response.DirectResponse = $"Dokument '{fileName}' wurde zum Chat hinzugefügt.";
            response.FoundDocuments = foundDocuments;
            response.Reason = "Dokument hinzugefügt";
        }
        else // Fehler beim Hinzufügen
        {
            string errorMsg = "Dokument konnte nicht hinzugefügt werden.";
            if (addResult != null && addResult.message != null) errorMsg = addResult.message;
            response.DirectResponse = errorMsg;
            response.Reason = "Dokument-Verknüpfung fehlgeschlagen";
        }
        return response;
    }
}
