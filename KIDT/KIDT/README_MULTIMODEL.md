# KIDT - KI-gestützer Dokumenten- und Terminmanger

## Was ist das?

KIDT ist eine .NET MAUI Chat-Anwendung mit **zwei spezialisierten Ollama-Modellen**:
- **qwen2.5:7b**: Für präzise Dokumenten-Analyse (mit Tools)
- **phi3:mini**: Für schnelle, natürliche Konversation

Ein intelligenter Router entscheidet automatisch, welches Modell verwendet wird.

---

## Architektur

```
Nutzer-Nachricht
       ?
ChatMcpService (Router)
       ?
    ???????????????????
    ?                 ?
qwen2.5:7b       phi3:mini
(Analyse)      (Konversation)
    ?                 
Tools werden         
ausgeführt          
```

### Routing-Logik

Der Router analysiert die Nachricht und entscheidet:

**? qwen2.5 (Tool-Spezialist):**
- Keywords gefunden: "analysier", "wörter", "zeichen", "datei", "lies", etc.
- Datei wurde hochgeladen

**? phi3:mini (Konversation):**
- Keine Keywords gefunden
- Allgemeine Fragen, Small Talk

---

## Warum Hybrid? (MCP-inspiriert, nicht MCP-nativ)

### Was wir nutzen:

? **MCP-Standard** für Tool-Definitionen
```csharp
// McpServer/Program.cs definiert Tools nach MCP-Standard
[McpServerToolType]
public static class CommunicationTools {
    [McpServerTool]
    public static string AnalyzeMessage(string message) { }
}
```

? **Semantic Kernel** für Orchestrierung
```csharp
// McpToolsRegistry.cs registriert Tools direkt
KernelFunctionFactory.CreateFromMethod(...)
```

? **Ollama** mit 2 Modellen (qwen2.5, phi3:mini)

### Was wir NICHT nutzen:

? **MCP-Protokoll** (stdio-Kommunikation)  
? **Laufender MCP-Server** (McpServer/Program.cs läuft nicht)

### Warum?

**MCP-Client API (0.5.0-preview.1) ist instabil:**
- Keine funktionierende Client-Implementierung
- Typen existieren nicht oder sind inkompatibel

**Unser Ansatz ist besser:**
- ? **Schneller**: Tools laufen in-process (< 1ms statt ~50ms)
- ?? **Einfacher**: Kein Server-Management, keine stdio-Probleme
- ? **Zuverlässiger**: Direkter Zugriff, einfaches Debugging

**Trade-off:**
- ?? Tools müssen in 2 Dateien synchron gehalten werden
- ?? Andere MCP-Clients können Tools nicht nutzen

---

## Projektstruktur

```
KIDT/
??? Prompts/
?   ??? tool-specialist-instructions.md   # qwen2.5 Prompt
?   ??? conversation-instructions.md      # phi3:mini Prompt
?
??? Platforms/Windows/
?   ??? ChatMcpService.cs                 # Router
?   ??? ToolSpecialistService.cs          # qwen2.5
?   ??? ConversationService.cs            # phi3:mini
?   ??? McpToolsRegistry.cs               # Tool-Registrierung
?
??? Components/Pages/
?   ??? Home.razor                        # Chat-UI
?
??? McpServer/
    ??? Program.cs                        # Tool-Definitionen (Source of Truth)
```

---

## Verfügbare Tools

Definiert in: `McpServer/Program.cs`  
Registriert in: `McpToolsRegistry.cs`

### 1. EchoMessage
```csharp
Input:  string message
Output: "[ECHO] {message}"
Zweck:  Test-Tool
```

### 2. AnalyzeMessage
```csharp
Input:  string message
Output: Wortanzahl, Zeichenanzahl (mit/ohne Leerzeichen)
Zweck:  Präzise Textanalyse
```

### 3. GetFileContent
```csharp
Input:  string filePath
Output: Datei-Inhalt als String
Zweck:  Dokumenten-Analyse
```

---

## Installation & Setup

### 1. Modelle installieren

```bash
ollama pull qwen2.5:7b
ollama pull phi3:mini
```

### 2. Ollama starten

```bash
ollama serve
# Läuft auf http://localhost:11434
```

### 3. App starten

```bash
cd KIDT
dotnet build
# Starten über Visual Studio (Windows-Projekt)
```

---

## Modell-Konfiguration

### qwen2.5:7b (Tool-Spezialist)
- **Größe**: ~4GB
- **Temperature**: 0.3 (präzise)
- **Tools**: Aktiv (AnalyzeMessage, GetFileContent, EchoMessage)
- **Zweck**: Dokumenten-Analyse, Metriken

### phi3:mini (Konversation)
- **Größe**: ~2GB
- **Temperature**: 0.5 (ausgewogen)
- **Tools**: Keine
- **MaxTokens**: Dynamisch (150 für kurze Fragen, 500 für lange)
- **Zweck**: Small Talk, allgemeine Fragen

---

## Neue Tools hinzufügen

### 1. Tool im MCP-Server definieren
**Datei:** `McpServer/Program.cs`
```csharp
[McpServerToolType]
public static class DocumentTools
{
    [McpServerTool, Description("Tool-Beschreibung")]
    public static string NewTool(
        [Description("Parameter-Beschreibung")] string param)
    {
        // Tool-Logik
        return "Ergebnis";
    }
}
```

### 2. Tool in Registry registrieren
**Datei:** `McpToolsRegistry.cs`
```csharp
var newTool = KernelFunctionFactory.CreateFromMethod(
    (string param) => /* Logik */,
    "NewTool",
    "Tool-Beschreibung",
    new[] { new KernelParameterMetadata("param") { Description = "..." } }
);

kernel.Plugins.AddFromFunctions("McpTools", new[]
{
    // ...existing tools
    newTool  // NEU
});
```

### 3. Instructions aktualisieren (optional)
**Datei:** `Prompts/tool-specialist-instructions.md`

---

## Custom Instructions

Instructions sind als **Markdown-Dateien** editierbar:

- `tool-specialist-instructions.md`: Regeln für qwen2.5
- `conversation-instructions.md`: Regeln für phi3:mini

**Vorteile:**
- Einfach editierbar (kein Code-Rebuild)
- Versionierbar (Git-Tracking)
- Testbar (verschiedene Prompts ausprobieren)

---

## Performance

### Geschwindigkeit

| Modell | Aufgabe | Zeit |
|--------|---------|------|
| phi3:mini | "Hallo" | 2-3s |
| phi3:mini | Lange Frage | 3-5s |
| qwen2.5 | Tool-Aufruf | < 1ms |
| qwen2.5 | Analyse + Antwort | 3-5s |

### Speicher

- **qwen2.5:7b**: ~4GB VRAM
- **phi3:mini**: ~2GB VRAM
- **Gesamt**: ~6GB VRAM
- **Empfohlen**: 16GB RAM + dedizierte GPU

---

## Testing

### Tool-Spezialist testen:
```
"Analysiere: Hello World"
"Wie viele Wörter hat dieser Text: Test test test"
"Lies die Datei C:\test.txt"
```

### Konversation testen:
```
"Hallo"
"Was ist künstliche Intelligenz?"
"Erzähl mir einen Witz"
```

---

## Technologie-Stack

- **.NET MAUI** (10.0) - Cross-Platform UI
- **Semantic Kernel** (1.68.0) - KI-Orchestrierung
- **Ollama** - Lokale LLM-Ausführung
- **ModelContextProtocol.Core** (0.5.0) - Tool-Standard
- **C# 14.0** - Programmiersprache

---

**Made with .NET MAUI, Semantic Kernel & Ollama**
