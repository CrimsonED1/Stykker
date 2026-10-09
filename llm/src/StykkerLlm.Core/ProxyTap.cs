using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// Liest eine Antwort eines OpenAI-kompatiblen Servers mit (SSE-Stream oder ganzes JSON) und zählt nur Zahlen und Namen:
// Zeit bis zum ersten Token, Denk-Token (reasoning_content) und Antwort-Token (content), Werkzeugaufrufe, finish_reason.
// Prompt- oder Antworttexte werden nie gespeichert.
public sealed class ProxyTap
{
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _all = new();   // nur für Antworten ohne Stream (ein JSON-Dokument)
    public bool Stream { get; set; }
    public DateTime? FirstToken { get; private set; }
    public DateTime? FirstContent { get; private set; }
    public DateTime? FirstReasoning { get; private set; }
    public int Reasoning { get; private set; }
    public int Content { get; private set; }
    public List<string> Tools { get; } = new();
    public string? FinishReason { get; private set; }
    public int? PredictedN { get; private set; }
    // Anthropic-Format (/v1/messages): Zeichenanteile von Denken und Antwort und die vom Server gemeldeten Ausgabe-Token
    private int _reasoningChars, _contentChars, _anthropicOut;
    private bool _anthropic;

    public void Feed(ReadOnlySpan<byte> chunk, DateTime now)
    {
        var text = Encoding.UTF8.GetString(chunk);
        if (!Stream) { if (_all.Length < 4_000_000) _all.Append(text); return; }
        foreach (var ch in text)
        {
            if (ch == '\n') { ParseLine(_line.ToString(), now); _line.Clear(); }
            else if (ch != '\r') { if (_line.Length < 2_000_000) _line.Append(ch); }
        }
    }

    // Ende der Antwort: letzte Zeile bzw. das ganze JSON auswerten
    public void Complete(DateTime now)
    {
        if (Stream) { if (_line.Length > 0) ParseLine(_line.ToString(), now); _line.Clear(); if (_anthropic) ReconcileAnthropic(); return; }
        try
        {
            using var doc = JsonDocument.Parse(_all.ToString());
            var r = doc.RootElement;
            if (r.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "message") { CompleteAnthropic(r); return; }
            if (r.TryGetProperty("timings", out var tm) && tm.ValueKind == JsonValueKind.Object && tm.TryGetProperty("predicted_n", out var pn) && pn.TryGetInt32(out var pni)) PredictedN = pni;
            if (!r.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0) return;
            var c0 = ch[0];
            if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) FinishReason = fr.GetString();
            int completion = r.TryGetProperty("usage", out var u) && u.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cti) ? cti : PredictedN ?? 0;
            if (c0.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                int cc = StrLen(msg, "content"), rc = StrLen(msg, "reasoning_content") + StrLen(msg, "reasoning");
                // Token nach Zeichenanteil schätzen (ohne Stream gibt es keine einzelnen Token)
                if (cc + rc > 0 && completion > 0) { Reasoning = (int)Math.Round(completion * (double)rc / (cc + rc)); Content = completion - Reasoning; }
                else if (completion > 0) Content = completion;
                if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray()) AddTool(tc);
            }
            else if (c0.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) Content = completion > 0 ? completion : Math.Max(1, tx.GetString()!.Length / 4);
        }
        catch { }
    }

    // Anthropic, Antwort ohne Stream: {"type":"message","content":[{"type":"thinking"|"text"|"tool_use",...}],"stop_reason":...,"usage":{...}}
    private void CompleteAnthropic(JsonElement r)
    {
        _anthropic = true;
        if (r.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String) FinishReason = MapStop(sr.GetString());
        if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var oti)) _anthropicOut = oti;
        if (r.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var b in blocks.EnumerateArray())
            {
                string kind = b.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String ? bt.GetString() ?? "" : "";
                if (kind == "thinking") _reasoningChars += StrLen(b, "thinking");
                else if (kind == "text") _contentChars += StrLen(b, "text");
                else if (kind == "tool_use" && b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name && !Tools.Contains(name)) Tools.Add(name);
            }
        Reasoning = 0; Content = 0;
        SplitByChars();
        PredictedN = _anthropicOut > 0 ? _anthropicOut : null;
    }

    // Token nach Zeichenanteil auf Denken und Antwort verteilen (die Deltas des Servers sind keine einzelnen Token)
    private void SplitByChars()
    {
        int total = _anthropicOut > 0 ? _anthropicOut : Reasoning + Content;
        int chars = _reasoningChars + _contentChars;
        if (total <= 0) return;
        if (chars <= 0) { Content = total; Reasoning = 0; return; }
        Reasoning = (int)Math.Round(total * (double)_reasoningChars / chars);
        Content = total - Reasoning;
    }

    private void ReconcileAnthropic()
    {
        if (_anthropicOut > 0) SplitByChars();   // sonst bleibt es bei der Zahl der Deltas
    }

    // stop_reason von Anthropic auf die Namen von OpenAI abbilden
    private static string? MapStop(string? s) => s switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        _ => s,
    };

    // Anthropic-Ereignisse (SSE): message_start (input_tokens), content_block_start (thinking | text | tool_use mit Name),
    // content_block_delta (thinking_delta | text_delta | input_json_delta), message_delta (stop_reason, output_tokens)
    private bool ParseAnthropic(JsonElement r, string type, DateTime now)
    {
        switch (type)
        {
            case "message_start":
                _anthropic = true;
                return true;
            case "content_block_start":
                _anthropic = true;
                if (r.TryGetProperty("content_block", out var cb) && cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("type", out var ct) && ct.GetString() == "tool_use")
                {
                    if (cb.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String && tn.GetString() is { Length: > 0 } toolName && !Tools.Contains(toolName)) Tools.Add(toolName);
                    FirstToken ??= now;
                }
                return true;
            case "content_block_delta":
            {
                _anthropic = true;
                if (!r.TryGetProperty("delta", out var d) || d.ValueKind != JsonValueKind.Object) return true;
                string dt = d.TryGetProperty("type", out var dtp) && dtp.ValueKind == JsonValueKind.String ? dtp.GetString() ?? "" : "";
                if (dt == "thinking_delta" && StrLen(d, "thinking") is > 0 and var tl) { Reasoning++; _reasoningChars += tl; FirstReasoning ??= now; FirstToken ??= now; }
                else if (dt == "text_delta" && StrLen(d, "text") is > 0 and var xl) { Content++; _contentChars += xl; FirstContent ??= now; FirstToken ??= now; }
                else if (dt == "input_json_delta") FirstToken ??= now;
                return true;
            }
            case "message_delta":
            {
                _anthropic = true;
                if (r.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    FinishReason = MapStop(sr.GetString());
                if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var oti)) { _anthropicOut = oti; PredictedN = oti; }
                return true;
            }
            case "content_block_stop": case "message_stop": case "ping":
                return true;
        }
        return false;
    }

    private static int StrLen(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Length : 0;

    private void AddTool(JsonElement tc)
    {
        if (tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            && n.GetString() is { Length: > 0 } name && !Tools.Contains(name))
            Tools.Add(name);
    }

    private void ParseLine(string line, DateTime now)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var payload = line[5..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.TryGetProperty("type", out var anyType) && anyType.ValueKind == JsonValueKind.String && ParseAnthropic(r, anyType.GetString() ?? "", now)) return;
            if (r.TryGetProperty("timings", out var tm) && tm.ValueKind == JsonValueKind.Object && tm.TryGetProperty("predicted_n", out var pn) && pn.TryGetInt32(out var pni)) PredictedN = pni;
            if (!r.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0) return;
            var c0 = ch[0];
            if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) FinishReason = fr.GetString();
            if (!c0.TryGetProperty("delta", out var d) || d.ValueKind != JsonValueKind.Object) return;
            bool token = false;
            if (StrLen(d, "content") > 0) { Content++; FirstContent ??= now; token = true; }
            if (StrLen(d, "reasoning_content") > 0 || StrLen(d, "reasoning") > 0) { Reasoning++; FirstReasoning ??= now; token = true; }
            if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray()) AddTool(tc);
                token = true;
            }
            if (token) FirstToken ??= now;
        }
        catch { }
    }
}

