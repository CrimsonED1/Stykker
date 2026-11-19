using System.Runtime.Versioning;
using Stykker.Shared.Windows;
using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Meldungen im Tray, wenn der Nutzer etwas wissen sollte, ohne dass die Seite offen ist: eine Konfliktkopie, eine
// angehaltene Massenlöschung, eine volle Platte. Höchstens eine Meldung je Minute, damit es nicht nervt.
[SupportedOSPlatform("windows")]
public static class TrayNotices
{
    public static void Start(MeshNode node, TrayIcon tray, CancellationToken stopping)
    {
        var last = DateTime.MinValue;
        var gate = new Lock();
        void Show(string title, string text)
        {
            lock (gate)
            {
                if (DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                last = DateTime.UtcNow;
            }
            tray.ShowBalloon(title, text);
        }

        node.Recorded += activity =>
        {
            if (activity.Kind == ActivityKind.Conflict)
                Show("Changed on two PCs", $"Both versions are kept: {activity.Path}");
            else if (activity.Kind == ActivityKind.Error && activity.Message?.StartsWith("Not enough free disk space", StringComparison.Ordinal) == true)
                Show("Disk full", $"{activity.Path} cannot be received: not enough free space.");
        };

        // Angehaltene Löschungen entstehen im Scan; ein Blick alle 10 Sekunden genügt.
        _ = Task.Run(async () =>
        {
            int held = 0;
            while (!stopping.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                int now = node.HeldDeletes;
                if (now > 0 && held == 0) Show("Deletion held", $"{now} files vanished from the shared folder. Open StykkerMeshShare to confirm or undo.");
                held = now;
            }
        });
    }
}
