using StykkerLlm.Core;

namespace StykkerLlm.Cli;

// stykker nodes …  – andere PCs mit Stykker, mit diesem gekoppelt (docs/nodes.md). Alles läuft über den Server
// dieses PCs (er ist der Hub); ohne Server gibt es keine Nodes.
//   nodes                         Liste: online, GPU, VRAM, laufende Modelle, Tests
//   nodes search                  im Heimnetz suchen
//   nodes pair <adresse> [code]   koppeln: ohne Code zeigt der Hub einen Code, den man am Node freigibt
//   nodes remove <id|name>        entkoppeln
//   nodes run <node> <aktion> [arg] [arg2]   eine Aktion auf dem Node (start, stop, unload, freevram …)
internal static class NodeCommand
{
    // waitForApproval = false in der TUI: dort bleibt die Eingabe frei, die Liste zeigt den neuen Node, sobald er da ist
    public static async Task<int> RunAsync(CliArgs a, CancellationToken ct, bool waitForApproval = true)
    {
        using var client = ServerCommand.TryConnect(a);
        if (client == null) { Out.Error(Strings.ServerNotRunning); return Commands.Error; }
        var words = a.Words;
        var sub = words.Count > 0 ? words[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "":
            case "list":
                return List(client.State?.Nodes ?? new NodesState(), a);
            case "search":
            {
                Console.Out.WriteLine(Out.Dim(Strings.NodeSearching));
                var r = await client.SendAsync("node.search", ct: ct).ConfigureAwait(false);
                Console.Out.WriteLine(r.Message);
                if (r.Data is { Length: > 0 } data)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data);
                    foreach (var n in doc.RootElement.EnumerateArray())
                        Console.Out.WriteLine($"  {Out.Bold(n.GetProperty("Name").GetString() ?? "")}  {n.GetProperty("Url").GetString()}" +
                            (n.GetProperty("paired").GetBoolean() ? "  " + Out.Green(Strings.NodePaired) : ""));
                }
                return r.Ok ? Commands.Ok : Commands.Error;
            }
            case "pair":
            {
                if (words.Count < 2) { Out.Error("usage: stykker nodes pair <address> [code]"); return Commands.Usage; }
                var code = words.Count > 2 ? string.Concat(words.Skip(2)) : null;
                var r = await client.SendAsync("node.pair", words[1], code, ct: ct).ConfigureAwait(false);
                if (!r.Ok) { Out.Error(r.Message); return Commands.Error; }
                if (code != null) { Console.Out.WriteLine(Out.Green(r.Message)); return Commands.Ok; }
                // Netflix-Weg: Code zeigen und warten, bis am Node freigegeben ist (Strg+C bricht ab)
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  {Out.Bold(AccessControl.Pretty(r.Data ?? ""))}   {Out.Dim(Strings.NodePairWhere)}");
                Console.Out.WriteLine();
                if (!waitForApproval) return Commands.Ok;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                        var p = (await client.GetStateAsync(ct).ConfigureAwait(false))?.Nodes.Pairing;
                        if (p == null) { Out.Error(Strings.PairExpired); return Commands.Error; }
                        if (p.Status == PairStatus.Approved) { Console.Out.WriteLine(Out.Green(p.Message)); return Commands.Ok; }
                        if (p.Status != PairStatus.Pending) { Out.Error(p.Message); return Commands.Error; }
                    }
                }
                catch (OperationCanceledException)
                {
                    await client.SendAsync("node.pair.cancel", ct: CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                return Commands.Cancelled;
            }
            case "remove" or "unpair":
            {
                if (words.Count < 2) { Out.Error("usage: stykker nodes remove <id|name>"); return Commands.Usage; }
                var r = await client.SendAsync("node.remove", words[1], ct: ct).ConfigureAwait(false);
                Console.Out.WriteLine(r.Ok ? Out.Green(r.Message) : Out.Red(r.Message));
                return r.Ok ? Commands.Ok : Commands.Error;
            }
            case "run":
            {
                if (words.Count < 3) { Out.Error("usage: stykker nodes run <node> <action> [arg] [arg2]"); return Commands.Usage; }
                var req = new ActionRequest { Action = "node.do", Arg = words[1], Name = words[2], Arg2 = words.Count > 3 ? words[3] : null, Flag = a.Yes };
                if (words.Count > 4) req.Values["arg2"] = words[4];
                var r = await client.SendAsync(req, ct).ConfigureAwait(false);
                Console.Out.WriteLine(r.Ok ? Out.Green(r.Message) : Out.Red(r.Message));
                return r.Ok ? Commands.Ok : Commands.Error;
            }
            default:
                Out.Error("usage: stykker nodes [list|search|pair <address> [code]|remove <id>|run <node> <action> [arg]]");
                return Commands.Usage;
        }
    }

    private static int List(NodesState nodes, CliArgs a)
    {
        if (a.Json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(nodes.List, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return Commands.Ok;
        }
        if (nodes.List.Count == 0) { Console.Out.WriteLine(Out.Dim(Strings.NodesEmpty)); return Commands.Ok; }
        Console.Out.WriteLine(Out.Dim(Strings.NodesSummary(nodes.List.Count, nodes.Online)));
        var rows = nodes.List.Select(n => (IReadOnlyList<string>)new[]
        {
            n.Id, Out.Dot(n.Online) + " " + n.Name, n.Url,
            n.Online ? $"{n.GpuName}" : Out.Dim(n.LastSeen is { } s ? Strings.NodeOfflineSince(DateTime.Now - s) : Strings.NodeOffline),
            n.Online ? $"{n.VramUsedGb:0.0}/{n.VramTotalGb:0} GB" : "",
            n.Online ? (n.Servers.Count == 0 ? Out.Dim(Strings.NodeNothingRunning) : string.Join(", ", n.Servers.Select(s => $"{s.Model} {s.Tps:0} t/s"))) : "",
            n.EvalCurrent,
        }).ToList();
        Out.Table(new[] { "id", "node", "url", "gpu", "vram", "running", "test" }, rows);
        if (nodes.Pairing is { Status: PairStatus.Pending } p)
            Console.Out.WriteLine(Out.Yellow($"{Strings.PairPending}: {p.Name}  {AccessControl.Pretty(p.Code)}"));
        return Commands.Ok;
    }
}
