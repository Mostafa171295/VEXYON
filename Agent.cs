using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
namespace VEXYON;

public class Agent
{
    readonly OllamaClient _ol; readonly Settings _s; readonly Tools _tools;
    JsonArray _history = new();

    const int MaxSteps = 15;
    static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Agent(OllamaClient ol, Settings s, Tools tools) { _ol = ol; _s = s; _tools = tools; }

    public void Reset() => _history = new JsonArray();

    /// <summary>رجّع سياق محادثة قديمة (رسائل المستخدم والنموذج بس).</summary>
    public void Load(IEnumerable<(string role, string text)> msgs)
    {
        _history = new JsonArray();
        _history.Add(Msg("system", SystemPrompt()));
        foreach (var (r, t) in msgs) _history.Add(Msg(r, t));
    }

    string SystemPrompt()
    {
        var inst = ToolFinder.AgentSummary();
        return SystemPromptBase()
            + (inst.Length > 0 ? " Installed programs you can run by name in run_command: " + inst + "." : "")
            + " If a program the user mentions is not listed, call find_program to locate it."
            + (_s.WebSearch ? " Internet access is ON: for current events or facts not in the user's files use web_search, then web_fetch to read a promising page, and mention the source URLs."
                            : "")
            + (_s.MemoryOn ? MemoryPart() : "")
            + (string.IsNullOrWhiteSpace(_s.CustomInstructions) ? ""
                : " The user's standing instructions (always follow them): " + Clip(_s.CustomInstructions.Trim(), 2000));
    }

    static string MemoryPart()
    {
        var m = Memory.ForPrompt();
        var ls = Lessons.ForPrompt();
        return " Long-term memory is ON: when the user tells you a lasting fact about themselves or asks you to remember something, call remember (one short fact); when asked to forget, call forget."
             + " Learning from mistakes: after you fix a tool/script error, or after the user corrects you and you confirm the correction, call note_lesson with ONE short general rule (not a one-off detail)."
             + (m.Length > 0 ? " What you already know about the user (use it naturally, don't repeat it unprompted): " + m + "." : "")
             + (ls.Length > 0 ? " Lessons from past mistakes (apply them, newest first): " + ls + "." : "");
    }

    string SystemPromptBase() =>
        "You are VEXYON, a private offline assistant running on the user's own PC. " +
        "The user's files are indexed. For any question about their files, call search_files first " +
        "(rephrase and retry if results are weak), use read_file for details, and mention the file names you used. " +
        "Never invent file contents. Use read_excel for spreadsheets, folder_overview to summarize a whole folder, " +
        "edit_file for small changes to an existing file (read_file it first, copy old_text exactly). " +
        "For ANY calculation call run_python and print the results; never compute numbers in your head. For general math, statistics, geometry, finance and unit conversion use import vexyon_math as vm; for structural mechanics use import vexyon_calc as vc; prefer these tested libraries over writing formulas yourself, and always state the inputs, units and assumptions with the result. " +
        "Verify before answering: after solving, check the answer by a second route (substitute back with vm.verify_root / verify_quadratic / verify_system, or recompute differently and compare with vm.verify_equal) and check the units; if ok is False or a script errors, read the message, fix the cause and rerun instead of guessing. If you cannot verify, say so plainly. " +
        "To create files or run programs, use create_file / run_command; " +
        "the work folder is: " + _s.WorkFolder + ". Work in small steps, check command output, and fix errors. " +
        "Reply in the user's language, concisely.";

    static string Clip(string t, int n) => t.Length > n ? t[..n] : t;

    static JsonObject Msg(string role, string content) => new() { ["role"] = role, ["content"] = content };

    static string Visible(string raw)
    {
        var t = Regex.Replace(raw, "<think>.*?</think>", "", RegexOptions.Singleline);
        t = Regex.Replace(t, "<think>.*$", "", RegexOptions.Singleline);
        return t.TrimStart();
    }

    /// <param name="onText">بيستقبل النص المرئي المتجمّع وقت الـ streaming؛ نص فاضي = امسح اللي اتعرض.</param>
    /// <param name="images">صور base64 (للنماذج اللي بتدعم الرؤية).</param>
    public async Task<string> Ask(string user, Action<string> status, CancellationToken ct,
                                  Action<string>? onText = null, string[]? images = null)
    {
        if (_history.Count == 0) _history.Add(Msg("system", SystemPrompt()));
        else _history[0] = Msg("system", SystemPrompt());
        var userMsg = Msg("user", user);
        _history.Add(userMsg);

        bool forceFinal = false, nudged = false;
        for (int step = 0; step < MaxSteps; step++)
        {
            Trim();
            Action<string>? cb = onText == null ? null : raw => onText(Visible(raw));
            if (images is { Length: > 0 })
                userMsg["images"] = new JsonArray(images.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            JsonNode reply;
            try { reply = await _ol.Chat(_s.ChatModel, _history, forceFinal ? null : Tools.Definitions(_s.WebSearch, _s.MemoryOn), ct, cb, _s.NumCtx, _s.DeepThink); }
            finally { userMsg.Remove("images"); }

            var calls = reply["tool_calls"] as JsonArray;
            if (calls == null || calls.Count == 0)
            {
                var txt = reply["content"]?.ToString() ?? "";
                txt = Regex.Replace(txt, "<think>.*?</think>", "", RegexOptions.Singleline);
                txt = Regex.Replace(txt, "<think>.*$", "", RegexOptions.Singleline).Trim();
                if (txt.Length == 0)
                {
                    onText?.Invoke("");
                    // النموذج رجّع رد فاضي: نطلب منه الإجابة النهائية من غير أدوات (مرة واحدة)
                    if (!nudged)
                    {
                        nudged = true; forceFinal = true;
                        status("↻ الرد فاضي، بطلب من النموذج يجاوب تاني...");
                        _history.Add(Msg("user", "جاوب دلوقتي على سؤالي الأخير بنص عادي وبشكل مباشر، واعتمد على نتائج الأدوات اللي فوق لو فيه."));
                        continue;
                    }
                    return FallbackFromTools();
                }
                _history.Add(JsonNode.Parse(reply.ToJsonString()));
                return txt;
            }
            _history.Add(JsonNode.Parse(reply.ToJsonString()));
            onText?.Invoke("");

            foreach (var call in calls)
            {
                var fn = call?["function"];
                var name = fn?["name"]?.ToString() ?? "";
                var args = fn?["arguments"];
                if (args is JsonValue)
                {
                    try { args = JsonNode.Parse(args.ToString()); } catch { }
                }
                status($"⚙ {name}: {args?.ToJsonString()}");
                string result;
                try { result = await _tools.Execute(name, args, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = "خطأ: " + ex.Message; }
                _history.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = result });
            }
        }
        return "وصلت للحد الأقصى من الخطوات (" + MaxSteps + ") من غير ما أخلص. جرّب تقسّم المهمة.";
    }

    // لو النموذج فضل يرد فاضي: نعرض آخر نتيجة أداة بدل "مفيش رد"
    string FallbackFromTools()
    {
        for (int i = _history.Count - 1; i > 0; i--)
        {
            if (_history[i]?["role"]?.ToString() == "tool")
            {
                var t = _history[i]?["content"]?.ToString() ?? "";
                if (t.Length > 1500) t = t[..1500] + "...";
                return "النموذج ماقدرش يصيغ إجابة، دي أقرب نتيجة لقيتها:\n\n" + t;
            }
        }
        return "النموذج ماردّش. جرّب تعيد صياغة السؤال أو غيّر النموذج من الإعدادات.";
    }

    void Trim()
    {
        // القياس بنص عربي حقيقي (مش \uXXXX)، والحد بيتناسب مع حجم السياق من الإعدادات
        int limit = (int)(_s.NumCtx * 1.1);
        while (_history.ToJsonString(Raw).Length > limit)
        {
            int lastUser = -1;
            for (int i = _history.Count - 1; i > 0; i--)
                if (_history[i]?["role"]?.ToString() == "user") { lastUser = i; break; }
            if (lastUser <= 1) break;
            _history.RemoveAt(1);
            while (_history.Count > 2 && _history[1]?["role"]?.ToString() != "user") _history.RemoveAt(1);
        }
    }
}
