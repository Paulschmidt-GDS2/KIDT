using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace KIDT.Services;

internal class AnalysisTools // Dummy-SK-Plugin: stellt analyze_document als KernelFunction bereit (Routing passiert im RouterService)
{
    [KernelFunction("analyze_document")]
    [Description("Analysiert oder fasst den Inhalt eines Dokuments aus dem Chat zusammen. Aufrufen wenn der User Dokument-Inhalt verstehen, zusammenfassen, erklären oder analysieren möchte und ein Dokument im Chat-Kontext vorhanden ist.")]
    public string AnalyzeDocument( // Dummy-Return — eigentliche Verarbeitung erfolgt im RouterService-Handler
        [Description("DocID des Dokuments aus dem Chat-Kontext (steht in [DocID: X] Nachrichten)")]
        int docId)
    {
        return docId.ToString();
    }

    [KernelFunction("generate_response")]
    [Description("Delegiert die Beantwortung an das leistungsstarke lokale Modell. Aufrufen, wenn die Antwort umfangreich, kreativ oder analytisch ist: Texte oder Inhalte verfassen/generieren, ausführlich erklären, zusammenfassen, umformulieren, übersetzen, Code schreiben oder brainstormen. NICHT aufrufen für kurze Fakten, Smalltalk, Bestätigungen oder wenn ein anderes Tool (Kalender, Ordner, Dokumentsuche, Dokument-Analyse) besser passt.")]
    public string GenerateResponse( // Dummy-Return — Routing zum lokalen Modell erfolgt im RouterService-Handler
        [Description("Worum es geht — die Aufgabe in einem kurzen Satz")]
        string task)
    {
        return task;
    }
}
