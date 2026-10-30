using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json.Nodes;
namespace VEXYON;

public class OllamaClient
{
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>بيتنادى بعد كل رد من النموذج: (اسم النموذج، عدد التوكنز اللي اتولّدت، زمن التوليد بالثواني) - الأرقام من Ollama نفسه.</summary>
    public event Action<string, int, double>? Generated;

    void ReportStats(string model, JsonNode? j)
    {
        try
        {
            int n = j?["eval_count"]?.GetValue<int>() ?? 0;
            double ns = j?["eval_duration"]?.GetValue<double>() ?? 0;
            if (n > 0 && ns > 0) Generated?.Invoke(model, n, ns / 1e9);
        }
        catch { }
    }

    public async Task<bool> IsUp()
    {
        try
        {
            using var cts = new CancellationTokenSource(3000);
            var r = await _http.GetAsync(BaseUrl + "/api/tags", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModels()
    {
        var txt = await _http.GetStringAsync(BaseUrl + "/api/tags");
        var list = new List<string>();
        var arr = JsonNode.Parse(txt)?["models"] as JsonArray;
        if (arr != null)
            foreach (var m in arr)
                list.Add(m?["name"]?.ToString() ?? "");
        return list;
    }

    public static bool HasModel(List<string> installed, string model) =>
        installed.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                           (!model.Contains(':') && n.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase)));

    public async Task<List<float[]>> Embed(string model, IList<string> inputs, CancellationToken ct)
    {
        var items = inputs.Select(t => (JsonNode?)JsonValue.Create(t.Length > 6000 ? t[..6000] : t)).ToArray();
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray(items) };
        using var resp = await _http.PostAsync(BaseUrl + "/api/embed",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var txt = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new Exception("Ollama embed: " + txt);
        var result = new List<float[]>();
        var arr = (JsonArray)JsonNode.Parse(txt)!["embeddings"]!;
        foreach (var node in arr)
        {
            var v = ((JsonArray)node!).Select(x => x!.GetValue<float>()).ToArray();
            double norm = Math.Sqrt(v.Sum(x => (double)x * x));
            if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
            result.Add(v);
        }
        return result;
    }

    /// <summary>لو onText اتبعت: الرد بيوصل حتة حتة (stream) وكل مرة بنبعت النص المتجمّع لحد دلوقتي.</summary>
    static readonly HashSet<string> NoThink = new(StringComparer.OrdinalIgnoreCase);

    public async Task<JsonNode> Chat(string model, JsonArray messages, JsonArray? tools, CancellationToken ct,
                                     Action<string>? onText = null, int numCtx = 8192, bool think = false)
    {
        bool tryThink = !NoThink.Contains(model);
        try { return await ChatOnce(model, messages, tools, ct, onText, numCtx, tryThink ? think : (bool?)null); }
        catch (Exception ex) when (tryThink && ex.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            NoThink.Add(model); // النموذج مابيدعمش think: نكمل من غيرها
            return await ChatOnce(model, messages, tools, ct, onText, numCtx, null);
        }
    }

    async Task<JsonNode> ChatOnce(string model, JsonArray messages, JsonArray? tools, CancellationToken ct,
                                  Action<string>? onText, int numCtx, bool? think)
    {
        bool stream = onText != null;
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = JsonNode.Parse(messages.ToJsonString()),
            ["options"] = new JsonObject { ["num_ctx"] = numCtx, ["temperature"] = 0.3 }
        };
        if (think.HasValue) body["think"] = think.Value;
        if (tools != null) body["tools"] = tools;

        if (!stream)
        {
            using var resp0 = await _http.PostAsync(BaseUrl + "/api/chat",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var txt = await resp0.Content.ReadAsStringAsync(ct);
            if (!resp0.IsSuccessStatusCode) throw new Exception("Ollama chat: " + txt);
            var root = JsonNode.Parse(txt)!;
            ReportStats(model, root);
            return JsonNode.Parse(root["message"]!.ToJsonString())!;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new Exception("Ollama chat: " + await resp.Content.ReadAsStringAsync(ct));
        using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        var sb = new StringBuilder();
        var calls = new JsonArray();
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var j = JsonNode.Parse(line);
            if (j?["error"] != null) throw new Exception("Ollama chat: " + j["error"]!.ToString());
            var m = j?["message"];
            var piece = m?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) { sb.Append(piece); onText!(sb.ToString()); }
            if (m?["tool_calls"] is JsonArray tc)
                foreach (var c in tc) calls.Add(JsonNode.Parse(c!.ToJsonString()));
            if (j?["done"]?.GetValue<bool>() == true) ReportStats(model, j);
        }
        var res = new JsonObject { ["role"] = "assistant", ["content"] = sb.ToString() };
        if (calls.Count > 0) res["tool_calls"] = calls;
        return res;
    }

    public async Task Pull(string model, Action<string, double> progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/pull")
        {
            Content = new StringContent(
                new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString(),
                Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var j = JsonNode.Parse(line);
            var status = j?["status"]?.ToString() ?? "";
            double total = j?["total"]?.GetValue<double>() ?? 0;
            double done = j?["completed"]?.GetValue<double>() ?? 0;
            progress(status, total > 0 ? done / total * 100 : 0);
            if (j?["error"] != null) throw new Exception(j["error"]!.ToString());
        }
    }
}
