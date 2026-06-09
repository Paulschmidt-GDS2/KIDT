namespace KIDT.Services;

public static class ChatEventService // Event-Service für Kommunikation zwischen MainLayout und Home-Komponente
{
    public static event Action OnNewChatRequested = null!; // Event: wird ausgelöst wenn "Neuer Chat" geklickt wird

    public static void TriggerNewChat() // Methode zum Auslösen des Events (von MainLayout aufgerufen)
    {
        if (OnNewChatRequested != null) // Gibt es Listener?
        {
            OnNewChatRequested.Invoke(); // Alle Listener benachrichtigen
        }
    }
}