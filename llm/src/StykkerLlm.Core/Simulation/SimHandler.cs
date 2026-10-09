using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StykkerLlm.Core;

// HTTP-Seite der Simulation: antwortet im Namen der simulierten Server (llama-server, Ollama, LM Studio mit eigener Engine),
// ohne dass irgendein Socket geöffnet wird. Unbekannte Adressen verhalten sich wie "Verbindung abgelehnt".
public sealed class SimHandler : HttpMessageHandler
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly SimWorld _w;

    public SimHandler(SimWorld world) => _w = world;

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static string Q(string s) => JsonSerializer.Serialize(s);
    private static string N(double v) => v.ToString("0.###", Inv);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        string path = uri.AbsolutePath;
        SimWorld.Server? s;
        bool engineSide;
        lock (_w.Gate)
        {
            s = _w.Servers.FirstOrDefault(x => x.Port == uri.Port && IsLocal(uri.Host));
            engineSide = false;
            if (s == null)
            {
                s = _w.Servers.FirstOrDefault(x => x.Spec.Kind == BackendKind.LmStudio && x.EnginePort == uri.Port && IsLocal(uri.Host));
                engineSide = s != null;
            }
        }
        if (s == null) throw new HttpRequestException("connection refused (simulated)");

        string? body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : null;
        switch (s.Spec.Kind)
        {
            case BackendKind.Ollama: return Ollama(s, request, path);
            case BackendKind.LmStudio when !engineSide: return LmStudio(s, path);
            case BackendKind.Vllm: return Vllm(s, path);
            default:
            {
                // Engine von LM Studio: nur mit dem Schlüssel aus der Kommandozeile
                if (engineSide)
                {
                    var auth = request.Headers.Authorization?.Parameter ?? "";
                    if (auth != s.ApiKey) return Json("{\"error\":{\"message\":\"Invalid API Key\",\"type\":\"authentication_error\",\"code\":401}}", HttpStatusCode.Unauthorized);
                }
                return await Llama(s, request, path, body, ct);
            }
        }
    }

    private static bool IsLocal(string host) => host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    // ── llama-server ──

    private async Task<HttpResponseMessage> Llama(SimWorld.Server s, HttpRequestMessage req, string path, string? body, CancellationToken ct)
    {
        var spec = s.Spec;
        string file = spec.Model + ".gguf";
        if (spec.Strata)
            switch (path)
            {
                case "/health":
                    return Json("{\"status\": \"ok\", \"max_context\": " + spec.Context + ", \"model\": " + Q(spec.Model) + ", \"loaded\": true, \"service\": \"strata\"}");
                case "/slots":
                    lock (_w.Gate) return Json("[{\"id\": 0, \"n_ctx\": " + spec.Context + ", \"is_processing\": " + (s.Slots[0].Busy ? "true" : "false") + "}]");
                case "/metrics":
                    lock (_w.Gate) return Json(StrataMetricsJson(s));
                case "/props":
                    return Json("{\"default_generation_settings\": {\"n_ctx\": " + spec.Context + ", \"params\": {\"n_predict\": -1}}, \"total_slots\": 1, \"model_alias\": " + Q(spec.Model) + "}");
                case "/v1/models":
                    return Json("{\"object\": \"list\", \"data\": [{\"id\": " + Q(spec.Model) + ", \"object\": \"model\", \"owned_by\": \"strata\"}]}");
            }
        switch (path)
        {
            case "/props":
                lock (_w.Gate)
                    return Json("{\"model_path\":" + Q(@"C:\models\" + file) + ",\"build_info\":\"b6000-sim\",\"total_slots\":" + spec.Slots +
                                ",\"model_alias\":" + Q(spec.Kind == BackendKind.LmStudio ? spec.Model : spec.Name) + ",\"default_generation_settings\":{\"n_ctx\":" + s.SlotContext +
                                "},\"model_ftype\":\"Q4_K_M\",\"is_sleeping\":false}");
            case "/v1/models":
                return Json("{\"data\":[{\"id\":" + Q(file) + ",\"meta\":{\"size\":" + (long)(spec.ModelGb * 1073741824) + "}}]}");
            case "/slots":
                lock (_w.Gate) return Json(SlotsJson(s));
            case "/health":
                return Json("{\"status\":\"ok\"}");
            case "/metrics":
                // Prometheus-Text wie beim echten llama-server (Warteschlange und spekulatives Decoding sichtbar machen)
                lock (_w.Gate)
                    return Text("# TYPE llamacpp:requests_processing gauge\nllamacpp:requests_processing " + s.BusySlots +
                                "\n# TYPE llamacpp:requests_deferred gauge\nllamacpp:requests_deferred " + s.Queue + "\n" +
                                SpecMetrics(s));
            case "/tokenize" when req.Method == HttpMethod.Post:
            {
                int n = Math.Max(1, (int)Math.Ceiling(ContentLength(body) / 3.2));
                return Json("{\"tokens\":[" + string.Join(",", Enumerable.Repeat("1", Math.Min(n, 200000))) + "]}");
            }
            case "/v1/chat/completions" when req.Method == HttpMethod.Post:
                return await ChatAsync(s, body, ct);
            default:
                return Json("{\"error\":{\"message\":\"not found\",\"code\":404}}", HttpStatusCode.NotFound);
        }
    }

    // Spekulatives Decoding: nur wenn der simulierte Server ein Entwurfsmodell hat.
    // Die Zählernamen sind die des echten llama-server (tools/server/server-task.cpp, Stand 2026-10-05).
    private static string SpecMetrics(SimWorld.Server s)
    {
        if (!s.Draft.Active) return "";
        var c = s.Draft;
        return "# TYPE llamacpp:spec_decode_num_draft_tokens_total counter\n" +
               "llamacpp:spec_decode_num_draft_tokens_total " + c.DraftTokens + "\n" +
               "# TYPE llamacpp:spec_decode_num_accepted_tokens_total counter\n" +
               "llamacpp:spec_decode_num_accepted_tokens_total " + c.DraftAccepted + "\n" +
               "# TYPE llamacpp:spec_decode_num_drafts_total counter\n" +
               "llamacpp:spec_decode_num_drafts_total " + c.DraftSteps + "\n" +
               "# TYPE llamacpp:spec_decode_num_accepted_tokens_per_pos_total counter\n" +
               "llamacpp:spec_decode_num_accepted_tokens_per_pos_total{position=\"0\"} " + c.DraftAccepted + "\n";
    }

    // Länge des Textes im Anfragekörper (für die Prompt-Größe des Benchmarks)
    private static int ContentLength(string? body)
    {
        if (string.IsNullOrEmpty(body)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString()!.Length;
            int len = 0;
            if (r.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    if (m.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String) len += mc.GetString()!.Length;
            return len;
        }
        catch { return body.Length; }
    }

    // Antwort ohne Stream mit "timings" (Benchmark): Zeiten aus der Simulation, die echte Wartezeit bleibt kurz
    private async Task<HttpResponseMessage> ChatAsync(SimWorld.Server s, string? body, CancellationToken ct)
    {
        var spec = s.Spec;
        int promptTokens = Math.Max(8, (int)Math.Ceiling(ContentLength(body) / 3.2) + 12);
        int maxTokens = 128; bool tools = false;
        try
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            if (doc.RootElement.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var m) && m > 0) maxTokens = m;
            tools = doc.RootElement.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0;
        }
        catch { }
        double fill = Math.Min(0.6, promptTokens / (double)Math.Max(1, spec.Context) * 0.6);   // je voller der Kontext, desto langsamer
        double genTps = (spec.TpsMin + spec.TpsMax) / 2 * (1 - fill);
        double promptTps = genTps * 11;
        int pred = tools ? 24 : Math.Min(maxTokens, 150);
        double pms = promptTokens / promptTps * 1000, gms = pred / genTps * 1000;
        await Task.Delay((int)Math.Min(120, 20 + (pms + gms) / 40), ct);
        string message = tools
            ? "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]}"
            : "{\"role\":\"assistant\",\"content\":\"The sky looks blue because air scatters short wavelengths of sunlight more strongly than long ones.\"}";
        return Json("{\"choices\":[{\"index\":0,\"finish_reason\":\"" + (tools ? "tool_calls" : pred >= maxTokens ? "length" : "stop") + "\",\"message\":" + message + "}]," +
                    "\"usage\":{\"prompt_tokens\":" + promptTokens + ",\"completion_tokens\":" + pred + "}," +
                    "\"timings\":{\"cache_n\":0,\"prompt_n\":" + promptTokens + ",\"prompt_ms\":" + N(pms) + ",\"predicted_n\":" + pred + ",\"predicted_ms\":" + N(gms) + "}}");
    }

    // Strata-/metrics (Ausschnitt): "live" für die laufende Anfrage, "requests" neueste zuerst
    private static string StrataMetricsJson(SimWorld.Server s)
    {
        var sl = s.Slots[0];
        string live = !sl.Busy
            ? "{\"state\": \"idle\", \"queued\": " + s.Queue + ", \"phase\": null, \"prompt_tokens\": null, \"prompt_read\": null, \"prompt_total\": null, \"generated\": null, \"max_tokens\": null, \"tok_s\": null}"
            : sl.Prompting
                ? "{\"state\": \"reading\", \"queued\": " + s.Queue + ", \"phase\": \"reading the prompt\", \"prompt_tokens\": " + sl.PromptTokens + ", \"prompt_read\": " + (int)sl.PromptDone +
                  ", \"prompt_total\": " + sl.PromptTokens + ", \"generated\": 0, \"max_tokens\": " + sl.MaxTokens + ", \"tok_s\": null}"
                : "{\"state\": \"generating\", \"queued\": " + s.Queue + ", \"phase\": \"" + ((int)sl.Gen < sl.ReasoningTokens ? "thinking" : "answering") + "\", \"prompt_tokens\": " + sl.PromptTokens +
                  ", \"prompt_read\": null, \"prompt_total\": null, \"generated\": " + (int)sl.Gen + ", \"max_tokens\": " + sl.MaxTokens + ", \"tok_s\": " + N(sl.Tps) + "}";
        var req = string.Join(", ", s.StrataDone.Select(d =>
            "{\"time\": " + N(d.Time) + ", \"duration_s\": " + N(d.Seconds) + ", \"finish\": " + Q(d.Finish) + ", \"prompt_tokens\": " + d.PromptTokens +
            ", \"prompt_ms\": " + N(d.PromptMs) + ", \"output_tokens\": " + d.OutputTokens + ", \"decode_tok_s\": " + N(d.DecodeTps) + "}"));
        return "{\"engine\": {\"model\": " + Q(s.Spec.Model) + ", \"context\": " + s.Spec.Context + ", \"engine\": \"sim\"}, \"live\": " + live +
               ", \"requests\": [" + req + "], \"totals\": {\"requests\": " + s.TaskCounter + "}}";
    }

    private static string SlotsJson(SimWorld.Server s)
    {
        var sb = new StringBuilder("[");
        int ctx = s.SlotContext;
        bool first = true;
        foreach (var sl in s.Slots)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(sl.Id).Append(",\"n_ctx\":").Append(ctx).Append(",\"speculative\":false,\"is_processing\":").Append(sl.Busy ? "true" : "false");
            if (sl.Busy)
            {
                int gen = (int)sl.Gen;
                int inCtx = sl.Prompting ? sl.PromptTokens : sl.PromptTokens + gen;
                sb.Append(",\"id_task\":").Append(sl.Task).Append(",\"n_prompt_tokens\":").Append(inCtx)
                  .Append(",\"n_prompt_tokens_processed\":").Append((int)sl.PromptDone)
                  .Append(",\"next_token\":{\"n_decoded\":").Append(gen).Append(",\"n_remain\":").Append(Math.Max(0, sl.MaxTokens - gen)).Append('}')
                  .Append(",\"params\":{\"n_predict\":").Append(sl.MaxTokens).Append(",\"max_tokens\":").Append(sl.MaxTokens).Append('}');
            }
            sb.Append('}');
        }
        return sb.Append(']').ToString();
    }

    // ── Ollama ──

    private HttpResponseMessage Ollama(SimWorld.Server s, HttpRequestMessage req, string path)
    {
        var spec = s.Spec;
        switch (path)
        {
            case "/api/version": return Json("{\"version\":\"0.34.4\"}");
            case "/api/ps":
            {
                if (!s.Loaded) return Json("{\"models\":[]}");
                long size = (long)(spec.ModelGb * 1073741824);
                string exp = DateTime.Now.AddMinutes(5).ToString("o", Inv);
                return Json("{\"models\":[{\"name\":" + Q(spec.Model) + ",\"model\":" + Q(spec.Model) + ",\"size\":" + size + ",\"size_vram\":" + size +
                            ",\"expires_at\":\"" + exp + "\",\"details\":{\"parameter_size\":\"3.2B\",\"quantization_level\":\"Q4_K_M\",\"family\":\"llama\"},\"context_length\":" + spec.Context + "}]}");
            }
            case "/api/generate" when req.Method == HttpMethod.Post:
                s.Loaded = false;   // Entladen (keep_alive 0) wirkt auf die Simulation
                return Json("{\"done\":true,\"done_reason\":\"unload\"}");
            default:
                return Json("{\"error\":\"not found\"}", HttpStatusCode.NotFound);
        }
    }

    // ── vLLM ──
    // Wie ein echter vLLM: /version für die Erkennung, /v1/models für das Modell und /metrics für die Messwerte.
    private HttpResponseMessage Vllm(SimWorld.Server s, string path)
    {
        var spec = s.Spec;
        switch (path)
        {
            case "/version": return Json("{\"version\": \"0.11.0\"}");
            case "/health": return Text(string.Empty);
            case "/v1/models":
                return Json("{\"object\": \"list\", \"data\": [{\"id\": " + Q(spec.Model) + ", \"object\": \"model\", \"owned_by\": \"vllm\"}]}");
            case "/metrics":
            {
                // Prometheus-Text mit den Zählern, die VllmApi liest (mit Label, wie es vLLM schreibt)
                lock (_w.Gate)
                {
                    double tps = s.Slots.Where(x => x.Busy).Sum(x => x.Tps);
                    return Text(
                        "# HELP vllm:num_requests_running Number of requests currently running on GPU.\n" +
                        "# TYPE vllm:num_requests_running gauge\nvllm:num_requests_running{model_name=\"" + spec.Model + "\"} " + s.BusySlots + "\n" +
                        "# TYPE vllm:num_requests_waiting gauge\nvllm:num_requests_waiting{model_name=\"" + spec.Model + "\"} " + s.Queue + "\n" +
                        "# TYPE vllm:avg_generation_throughput_toks_per_s gauge\nvllm:avg_generation_throughput_toks_per_s{model_name=\"" + spec.Model + "\"} " + N(tps) + "\n" +
                        "# TYPE vllm:gpu_cache_usage_perc gauge\nvllm:gpu_cache_usage_perc{model_name=\"" + spec.Model + "\"} " + N(Math.Min(0.98, spec.ModelGb / 24.0)) + "\n");
                }
            }
            case "/v1/chat/completions":
                return Json("{\"error\":{\"message\":\"The simulator does not answer requests for vLLM.\",\"type\":\"simulator\"}}");
            default:
                return Json("{\"object\": \"error\", \"message\": \"Not Found\", \"code\": 404}", HttpStatusCode.NotFound);
        }
    }

    // ── LM Studio ──

    private HttpResponseMessage LmStudio(SimWorld.Server s, string path)
    {
        if (path != "/api/v0/models") return Json("{\"error\":\"Unexpected endpoint or method.\"}", HttpStatusCode.NotFound);
        var spec = s.Spec;
        return Json("{\"object\":\"list\",\"data\":[" +
                    "{\"id\":" + Q(spec.Model) + ",\"object\":\"model\",\"type\":\"llm\",\"publisher\":\"sim\",\"arch\":\"gemma3\",\"compatibility_type\":\"gguf\",\"quantization\":\"Q4_K_M\",\"state\":\"loaded\",\"max_context_length\":131072,\"loaded_context_length\":" + spec.Context + "}," +
                    "{\"id\":\"qwen2.5-coder-7b-instruct\",\"object\":\"model\",\"type\":\"llm\",\"publisher\":\"sim\",\"arch\":\"qwen2\",\"compatibility_type\":\"gguf\",\"quantization\":\"Q4_K_M\",\"state\":\"not-loaded\",\"max_context_length\":32768}," +
                    "{\"id\":\"text-embedding-nomic-embed-text-v1.5\",\"object\":\"model\",\"type\":\"embeddings\",\"publisher\":\"sim\",\"arch\":\"nomic-bert\",\"compatibility_type\":\"gguf\",\"quantization\":\"Q4_K_M\",\"state\":\"not-loaded\",\"max_context_length\":2048}]}");
    }
}
