using System.Threading;

namespace StykkerStarter.App;

// Der zweite Start meldet dem ersten über ein benanntes Ereignis, dass er das Fenster öffnen soll.
internal static class SingleInstance
{
    public static string EventName(string mutexName) => mutexName + ".open";

    public static void SignalOpen(string mutexName)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName(mutexName));
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }   // der erste Starter ist gerade dabei zu enden
    }

    // Wartet im Hintergrund auf die Meldungen; jede bedeutet „Fenster öffnen“
    public static EventWaitHandle Listen(string mutexName, Action onOpen)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(mutexName));
        var thread = new Thread(() =>
        {
            // Beim Beenden wird das Ereignis geschlossen, während dieser Thread wartet: dann endet er leise
            try
            {
                while (ev.WaitOne()) onOpen();
            }
            catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true,
            Name = "Stykker Starter open requests",
        };
        thread.Start();
        return ev;
    }
}
