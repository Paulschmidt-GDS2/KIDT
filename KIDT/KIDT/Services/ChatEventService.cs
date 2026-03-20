namespace KIDT.Services;

/// <summary>
/// Event-Service für Kommunikation zwischen MainLayout und Home-Komponente
/// </summary>
public static class ChatEventService
{
    // Event das ausgelöst wird wenn "Neuer Chat" geklickt wird
    public static event Action OnNewChatRequested = null!; // Wird vor Verwendung immer gesetzt (null! unterdrückt Warnung)

    // Methode zum Auslösen des Events (von MainLayout aufgerufen)
    public static void TriggerNewChat()
    {
        if (OnNewChatRequested != null) // Check: Gibt es Listener?
        {
            OnNewChatRequested.Invoke(); // Ja -> Löse Event aus (alle Listener werden benachrichtigt)
        }
    }
}