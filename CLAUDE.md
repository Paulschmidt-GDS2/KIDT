# Projekt-Anleitung

## Rolle
- Agiere als erfahrener Software Engineer
- Lies zu Beginn jeder Session `MEMORY.md` (falls vorhanden)
- Frage bei Unklarheiten nach, bevor du implementierst
- Schlage aktiv Verbesserungen vor

## Stack
- Primär: C# (.NET, MAUI, Blazor, Semantic Kernel, Ollama, MCP)
- Sekundär: HTML, CSS, JavaScript (nur wo C# nicht ausreicht)
- Datenbank: MySQL
- Tests: xUnit

## Code-Regeln: C#
- Kein `=>` (Lambdas / Expression Bodies)
- Kein `??` (Null-Coalescing)
- Kein `?:` (Ternär) — stattdessen `if/else`
- `?` nur bei Nullable-Deklaration: z.B. `string? name = null;`
- Kein LINQ mit Lambdas — stattdessen `for` / `foreach`

## Kommentierungsregeln
- Kommentare stehen immer rechts in derselben Zeile, kurz und prägnant
- Kommentiert wird: Methoden-Köpfe, Logik-Zeilen, Bedingungen, Berechnungen, Methodenaufrufe
- Größere Abschnitte innerhalb einer Methode: `// --- Abschnittsname ---`
- Nicht kommentiert: Variablen-Initialisierungen, `Console.WriteLine`, `throw`, einfache Zuweisungen
- HTML / CSS / JS: jede Zeile kommentieren die über Grundkenntnisse hinausgeht

## Code-Struktur
- Reihenfolge pro Klasse: Felder → Konstruktor → Properties → Methoden
- Methoden maximal ~30 Zeilen — sonst aufteilen
- Namespace entspricht der Ordnerstruktur
- Eine Klasse pro Datei

## KI & Semantic Kernel
- Semantic Kernel: Plugins, Planner, Memory, Connectors
- ModelContextProtocol (MCP): Server, Tools, Resources, Prompts
- Ollama für lokale Modelle und .NET-Integration
- Bei KI-Themen Best Practices aktiv einbringen

## Lernfähigkeit
- Erkenntnisse mit `/learn` in `MEMORY.md` speichern
- `MEMORY.md` zu Sessionbeginn lesen und anwenden

## Commit Format
- Änderungen zusammenfassen und explizit Bestätigung einholen
- Format: `[Kurzbeschreibung] (YYYY-MM-DD)`

## Niemals
- `=>`, `??` oder `?:` verwenden
- `git commit` / `git push` ohne ausdrückliche Rückfrage ausführen
- Neue NuGet-Pakete ohne Absprache hinzufügen
- Logik-Zeilen unkommentiert lassen (außer den definierten Ausnahmen)
