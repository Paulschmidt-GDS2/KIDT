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
}
