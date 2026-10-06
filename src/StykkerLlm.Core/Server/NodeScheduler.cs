namespace StykkerLlm.Core;

// Verteilte Tests (docs/nodes.md, N3): welcher PC testet welche Modelldatei? Jede Datei geht an einen PC, auf dem
// sie liegt (dort steht sie als Modell für Tests). Unter mehreren gewinnt der mit der wenigsten Arbeit
// (laufender Test + wartende Aufträge + was in dieser Runde schon zugeteilt wurde), bei Gleichstand der mit dem
// meisten freien VRAM, dann dieser PC selbst (kein Umweg übers Netz).
public static class NodeScheduler
{
    public sealed record Assignment(string File, string NodeId, string NodeName, string ModelId);

    public static List<Assignment> Assign(IEnumerable<string> files, RemoteNode self, IEnumerable<RemoteNode> nodes)
    {
        var pcs = new List<RemoteNode> { self };
        pcs.AddRange(nodes.Where(n => n.Online));
        var load = pcs.ToDictionary(n => n.Id, n => (n.EvalCurrent.Length > 0 ? 1 : 0) + n.EvalQueued);
        var result = new List<Assignment>();
        foreach (var file in files.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var holders = pcs
                .Select(n => (Node: n, Model: n.Models.FirstOrDefault(m => m.Enabled && m.File.Equals(file, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Model != null)
                .OrderBy(x => load[x.Node.Id])
                .ThenByDescending(x => x.Node.VramFreeGb)
                .ThenBy(x => x.Node.Id == self.Id ? 0 : 1)
                .ToList();
            if (holders.Count == 0) continue;
            var pick = holders[0];
            load[pick.Node.Id]++;
            result.Add(new Assignment(file, pick.Node.Id, pick.Node.Name, pick.Model!.Id));
        }
        return result;
    }

    // Alle Dateien, die irgendwo als Modell für Tests stehen (für die Auswahl „automatisch“)
    public static List<string> Files(RemoteNode self, IEnumerable<RemoteNode> nodes) =>
        new[] { self }.Concat(nodes.Where(n => n.Online))
            .SelectMany(n => n.Models.Where(m => m.Enabled).Select(m => m.File))
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
}
