using System.Text.Json;

namespace StykkerLlm.Core;

// Die Nodes im Zustand des Hubs (Teil "nodes" von /api/state). Nicht der ganze Zustand jedes Nodes – nur, was die
// Seite Nodes, die Verteilung der Tests und der Vergleich brauchen. Fenster, TUI und Web lesen dasselbe.
public sealed class NodesState
{
    public List<RemoteNode> List { get; } = new();
    public RemoteNodePairing? Pairing { get; init; }
    public int Online => List.Count(n => n.Online);
}

public sealed class RemoteNodePairing
{
    public string Url { get; init; } = "";
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public DateTime Expires { get; init; }
    public string Status { get; init; } = PairStatus.Pending;
    public string Message { get; init; } = "";
}

public sealed class RemoteNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Online { get; init; }
    public DateTime? LastSeen { get; init; }
    public string Error { get; init; } = "";
    public string GpuName { get; init; } = "";
    public double GpuUtil { get; init; }
    public double VramUsedGb { get; init; }
    public double VramTotalGb { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double CpuPercent { get; init; }
    public bool ProxyRunning { get; init; }
    public bool ProxyLan { get; init; }
    public int ProxyPort { get; init; }
    public string EvalCurrent { get; init; } = "";     // "gemma-4 · hard 12/27" oder leer
    public int EvalQueued { get; init; }
    public List<RemoteNodeServer> Servers { get; } = new();
    public List<RemoteNodeProfile> Profiles { get; } = new();
    public List<RemoteNodeModel> Models { get; } = new();
    public List<RemoteNodeBest> Best { get; } = new();

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;
    public double VramFreeGb => Math.Max(0, VramTotalGb - VramUsedGb);
    public bool Busy => EvalCurrent.Length > 0 || EvalQueued > 0;
    // Basis-URL des Proxys im Netz (nur wenn er läuft und im LAN angeboten wird)
    public string ProxyUrl => ProxyRunning && ProxyLan && ProxyPort > 0 ? NetAddr.Url(Host, ProxyPort) : "";
}

public sealed class RemoteNodeServer
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string State { get; init; } = "";
    public string Backend { get; init; } = "";
    public double Tps { get; init; }
    public bool Manual { get; init; }
}

public sealed class RemoteNodeProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
}

// Ein Modell für Tests auf dem Node (gefundene GGUF und Profile – dieselbe Liste wie dort unter Models)
public sealed class RemoteNodeModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string File { get; init; } = "";
    public double SizeGb { get; init; }
    public bool Enabled { get; init; }
}

// Beste gemessene Geschwindigkeit je Modell aus dem Verlauf des Nodes (für den Vergleich)
public sealed class RemoteNodeBest
{
    public string Model { get; init; } = "";
    public string File { get; init; } = "";
    public int Ctx { get; init; }
    public double BestTps { get; init; }
    public double MaxVramGb { get; init; }
}

public static class NodeStateJson
{
    public const int MaxBest = 40;

    public static void Write(Utf8JsonWriter w, NodeRegistry? nodes)
    {
        w.WriteStartObject("nodes");
        if (nodes?.Pairing is { } p)
        {
            w.WriteStartObject("pairing");
            w.WriteString("url", p.Url);
            w.WriteString("name", p.Name);
            w.WriteString("code", p.Code);
            w.WriteString("expires", p.Expires.ToString("o"));
            w.WriteString("status", p.Status);
            w.WriteString("message", p.Message);
            w.WriteEndObject();
        }
        w.WriteStartArray("list");
        foreach (var l in nodes?.Nodes ?? Array.Empty<NodeLink>())
        {
            var s = l.State;
            w.WriteStartObject();
            w.WriteString("id", l.Entry.Id);
            w.WriteString("name", l.Entry.Name);
            w.WriteString("url", l.Entry.Url);
            w.WriteBoolean("online", s != null);
            if (l.LastSeen is { } seen) w.WriteString("lastSeen", seen.ToString("o"));
            w.WriteString("error", l.Error ?? "");
            if (s != null) WriteSummary(w, s);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter w, StateSnapshot s)
    {
        if (s.Gpu is { } g)
        {
            w.WriteString("gpu", g.Name);
            w.WriteNumber("gpuUtil", Math.Round(g.Util, 1));
            w.WriteNumber("vramUsed", Math.Round(g.MemUsedGb, 2));
            w.WriteNumber("vramTotal", Math.Round(g.MemTotalGb, 2));
        }
        if (s.Sys is { } sys)
        {
            w.WriteNumber("ramUsed", Math.Round(sys.RamUsedGb, 2));
            w.WriteNumber("ramTotal", Math.Round(sys.RamTotalGb, 2));
            w.WriteNumber("cpu", Math.Round(sys.CpuPercent, 1));
        }
        w.WriteBoolean("proxyRunning", s.Proxy.Running);
        w.WriteBoolean("proxyLan", s.Proxy.BindLan);
        w.WriteNumber("proxyPort", s.Proxy.Port);
        if (s.Eval is { } ev)
        {
            var run = ev.Jobs.FirstOrDefault(j => j.State == "running");
            if (run != null) w.WriteString("evalCurrent", $"{run.Model} · {run.Suite} {run.Done}/{run.Total}");
            w.WriteNumber("evalQueued", ev.Jobs.Count(j => j.State == "queued"));
            w.WriteStartArray("models");
            foreach (var m in ev.Models)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("name", m.Name);
                w.WriteString("file", m.ModelFile.Length > 0 ? Path.GetFileName(m.ModelFile) : "");
                w.WriteNumber("sizeGb", Math.Round(m.SizeGb, 2));
                w.WriteBoolean("enabled", m.Enabled);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("servers");
        foreach (var sv in s.Servers.Where(x => x.Online))
        {
            w.WriteStartObject();
            w.WriteString("key", sv.Key);
            w.WriteString("name", sv.Name);
            // Ohne geladenes Modell (Ollama im Leerlauf: "–") steht der Name des Servers da
            w.WriteString("model", sv.Model is "–" or "" ? sv.Name : sv.Model);
            w.WriteString("state", sv.State);
            w.WriteString("backend", sv.Backend);
            w.WriteNumber("tps", Math.Round(sv.Current, 1));
            w.WriteBoolean("manual", sv.Manual);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("profiles");
        foreach (var pr in s.Profiles.Where(x => x.CanStart))
        {
            w.WriteStartObject();
            w.WriteString("id", pr.Id);
            w.WriteString("name", pr.Name);
            w.WriteString("model", pr.ModelPath.Length > 0 ? Path.GetFileName(pr.ModelPath) : "");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("best");
        foreach (var h in s.History.Where(x => x.BestTps > 0).OrderByDescending(x => x.BestTps).Take(MaxBest))
        {
            w.WriteStartObject();
            w.WriteString("model", h.Name);
            w.WriteString("file", h.ModelPath.Length > 0 ? Path.GetFileName(h.ModelPath) : "");
            w.WriteNumber("ctx", h.Ctx);
            w.WriteNumber("bestTps", Math.Round(h.BestTps, 1));
            w.WriteNumber("maxVram", Math.Round(h.MaxVramGb, 2));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // Dieselbe Sicht ohne den Umweg über /api/state (die Web-Seiten laufen im Server selbst). Bewusst über das JSON:
    // so sehen Web, Fenster und TUI garantiert dieselben Felder.
    public static NodesState FromRegistry(NodeRegistry? nodes)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            Write(w, nodes);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return Read(doc.RootElement);
    }

    // Dieser PC in derselben Form wie ein Node (für den Vergleich „Modelle je Node“ und die Verteilung der Tests)
    public static RemoteNode Self(StateSnapshot s, string url)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("nodes");
            w.WriteStartArray("list");
            w.WriteStartObject();
            w.WriteString("id", SelfId);
            w.WriteString("name", Environment.MachineName);
            w.WriteString("url", url);
            w.WriteBoolean("online", true);
            WriteSummary(w, s);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return Read(doc.RootElement).List[0];
    }

    public const string SelfId = "self";

    public static NodesState Read(JsonElement root)
    {
        if (J.Obj(root, "nodes") is not { } n) return new();
        RemoteNodePairing? pairing = null;
        if (J.Obj(n, "pairing") is { } p)
            pairing = new RemoteNodePairing
            {
                Url = J.Str(p, "url") ?? "", Name = J.Str(p, "name") ?? "", Code = J.Str(p, "code") ?? "",
                Expires = J.Time(p, "expires"), Status = J.Str(p, "status") ?? PairStatus.Pending, Message = J.Str(p, "message") ?? "",
            };
        var state = new NodesState { Pairing = pairing };
        foreach (var e in J.Arr(n, "list"))
        {
            var node = new RemoteNode
            {
                Id = J.Str(e, "id") ?? "", Name = J.Str(e, "name") ?? "", Url = J.Str(e, "url") ?? "",
                Online = J.Bool(e, "online"), LastSeen = J.TimeOrNull(e, "lastSeen"), Error = J.Str(e, "error") ?? "",
                GpuName = J.Str(e, "gpu") ?? "", GpuUtil = J.Dbl(e, "gpuUtil"), VramUsedGb = J.Dbl(e, "vramUsed"), VramTotalGb = J.Dbl(e, "vramTotal"),
                RamUsedGb = J.Dbl(e, "ramUsed"), RamTotalGb = J.Dbl(e, "ramTotal"), CpuPercent = J.Dbl(e, "cpu"),
                ProxyRunning = J.Bool(e, "proxyRunning"), ProxyLan = J.Bool(e, "proxyLan"), ProxyPort = J.Int(e, "proxyPort"),
                EvalCurrent = J.Str(e, "evalCurrent") ?? "", EvalQueued = J.Int(e, "evalQueued"),
            };
            foreach (var x in J.Arr(e, "servers"))
                node.Servers.Add(new RemoteNodeServer
                {
                    Key = J.Str(x, "key") ?? "", Name = J.Str(x, "name") ?? "", Model = J.Str(x, "model") ?? "", State = J.Str(x, "state") ?? "",
                    Backend = J.Str(x, "backend") ?? "", Tps = J.Dbl(x, "tps"), Manual = J.Bool(x, "manual"),
                });
            foreach (var x in J.Arr(e, "profiles"))
                node.Profiles.Add(new RemoteNodeProfile { Id = J.Str(x, "id") ?? "", Name = J.Str(x, "name") ?? "", Model = J.Str(x, "model") ?? "" });
            foreach (var x in J.Arr(e, "models"))
                node.Models.Add(new RemoteNodeModel
                {
                    Id = J.Str(x, "id") ?? "", Name = J.Str(x, "name") ?? "", File = J.Str(x, "file") ?? "", SizeGb = J.Dbl(x, "sizeGb"), Enabled = J.Bool(x, "enabled"),
                });
            foreach (var x in J.Arr(e, "best"))
                node.Best.Add(new RemoteNodeBest
                {
                    Model = J.Str(x, "model") ?? "", File = J.Str(x, "file") ?? "", Ctx = J.Int(x, "ctx"), BestTps = J.Dbl(x, "bestTps"), MaxVramGb = J.Dbl(x, "maxVram"),
                });
            state.List.Add(node);
        }
        return state;
    }
}
