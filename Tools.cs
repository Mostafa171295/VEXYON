using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace VEXYON;

public class Tools
{
    readonly Settings _s; readonly Search _search;
    readonly Func<string, Task<bool>> _approve; readonly Action<string> _audit;

    static readonly string[] Blocked =
    {
        @"\bformat\s+[a-z]:", @"\bdiskpart\b", @"\bshutdown\b", @"\breg\s+(add|delete|import)",
        @"\b(del|erase)\b.*\s/s\b", @"\b(rd|rmdir)\b.*\s/s\b", @"remove-item.*-recurse",
        @"\bbcdedit\b", @"\bcipher\s+/w", @"\bnet\s+user\b", @"\btakeown\b", @"\bicacls\b"
    };

    static readonly string[] RecursiveDelete =
    {
        @"\b(del|erase)\b.*\s/s\b", @"\b(rd|rmdir)\b.*\s/s\b", @"remove-item.*-recurse"
    };

    public Tools(Settings s, Search search, Func<string, Task<bool>> approve, Action<string> audit)
    { _s = s; _search = search; _approve = approve; _audit = audit; }

    const string DefsJson = """
    [
     {"type":"function","function":{"name":"search_files","description":"Search the user's indexed files by meaning and keywords. Returns relevant passages with file paths. Use this first for any question about the user's files.","parameters":{"type":"object","properties":{"query":{"type":"string","description":"what to look for"}},"required":["query"]}}},
     {"type":"function","function":{"name":"read_file","description":"Read the full text of a file (pdf, docx, xlsx, text, code) inside the allowed folders.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}},
     {"type":"function","function":{"name":"list_files","description":"List files in a folder inside the allowed folders. Empty folder means the watch folder.","parameters":{"type":"object","properties":{"folder":{"type":"string"}}}}},
     {"type":"function","function":{"name":"create_file","description":"Create a text/code file inside the work folder. Relative paths are relative to the work folder.","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}}},
     {"type":"function","function":{"name":"run_command","description":"Run a Windows command line (cmd) in the work folder, e.g. python script.py or dotnet build. The user must approve it. Returns exit code and output.","parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}},
     {"type":"function","function":{"name":"find_program","description":"Find where a program is installed on this PC, by program name or exe name (e.g. AutoCAD, tesseract). Returns full paths you can run with run_command.","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}},
     {"type":"function","function":{"name":"edit_file","description":"Edit an existing file inside the work folder by replacing exact text. old_text must match the file exactly once (or set replace_all=true). Read the file first. A backup is saved automatically and the user must approve.","parameters":{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string","description":"exact text to replace"},"new_text":{"type":"string","description":"replacement text"},"replace_all":{"type":"boolean"}},"required":["path","old_text","new_text"]}}},
     {"type":"function","function":{"name":"read_excel","description":"Read an Excel (.xlsx) sheet as a table (keeps columns aligned, converts dates). Without sheet it lists all sheets and reads the first one. Prefer this over read_file for spreadsheets.","parameters":{"type":"object","properties":{"path":{"type":"string"},"sheet":{"type":"string"},"max_rows":{"type":"integer","description":"default 40, max 200"}},"required":["path"]}}},
     {"type":"function","function":{"name":"run_python","description":"Run a short Python 3 script and return its printed output. Use it for ANY calculation (arithmetic, engineering formulas, unit conversions, tables, statistics) instead of computing in your head. The script must print its results. A tested engineering library is available: `import vexyon_calc as vc` then print(vc.beam_simply_supported_udl(L_m, w_kN_m, E_MPa, I_mm4)); also beam_simply_supported_point, beam_cantilever_udl/point, beam_fixed_fixed_udl, section_rectangle/circle/tube/i_symmetric, bending_stress_MPa, axial_stress_MPa, shear_stress_rect_MPa, euler_buckling_kN, utilization, convert(value, from_unit, to_unit); call vc.help() to list them. A general math library is also available: `import vexyon_math as vm` with calc('2*(3+4)^2/7'), solve_linear, solve_quadratic(a,b,c), solve_system(A,b), find_root('x**3-2*x-5',lo,hi), derivative, integral, describe(data), linear_regression, circle/triangle_area/sphere/cylinder/cone/polygon_area, projectile, percent_change, compound_interest, loan_payment, prime_factors, gcd_lcm, convert_units(value,from,to) (temperature, mass, time, speed, volume, length, force, stress); call vm.help(). ALWAYS prefer these functions over writing formulas yourself, and print() their result so inputs and assumptions are shown. Units: m, mm, kN, kN_m, MPa, mm4. The user must approve the script.","parameters":{"type":"object","properties":{"code":{"type":"string","description":"complete Python script"}},"required":["code"]}}},
     {"type":"function","function":{"name":"folder_overview","description":"Overview of a whole folder: file counts by type, total size, largest and newest files, and short previews of some documents. Use it to summarize or understand a folder. Empty folder means the watch folder.","parameters":{"type":"object","properties":{"folder":{"type":"string"}}}}}
    ]
    """;

    const string WebDefsJson = """
    [
     {"type":"function","function":{"name":"web_search","description":"Search the internet for current information that is NOT in the user's files. Returns titles, URLs and snippets. Use only when the answer needs up-to-date or outside information.","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}},
     {"type":"function","function":{"name":"web_fetch","description":"Open a web page URL (usually from web_search results) and return its text.","parameters":{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}}}
    ]
    """;

    const string MemDefsJson = """
    [
     {"type":"function","function":{"name":"remember","description":"Save ONE short durable fact about the user to long-term memory (job, preferences, projects, names, how they like answers). Use when the user states such a fact or asks you to remember something. Never store passwords, ID/card numbers, or one-off details.","parameters":{"type":"object","properties":{"fact":{"type":"string","description":"short fact, one sentence"}},"required":["fact"]}}},
     {"type":"function","function":{"name":"forget","description":"Remove from long-term memory (facts and lessons) every saved item containing the given text. Use when the user asks you to forget something.","parameters":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}},
     {"type":"function","function":{"name":"note_lesson","description":"Save ONE short general rule you learned from a mistake, so you do better next time. Call it only after (a) a tool/script error that you then fixed, or (b) the user corrected you and you confirmed the correction. The lesson must be a reusable rule (e.g. 'In vm.integral pass the function as a string in x'), not a one-off detail, and never contain personal data or secrets.","parameters":{"type":"object","properties":{"lesson":{"type":"string","description":"one short general rule, one sentence"}},"required":["lesson"]}}}
    ]
    """;

    public static JsonArray Definitions(bool web = false, bool mem = false)
    {
        var arr = (JsonArray)JsonNode.Parse(DefsJson)!;
        if (mem)
            foreach (var d in (JsonArray)JsonNode.Parse(MemDefsJson)!) arr.Add(d!.DeepClone());
        if (web)
            foreach (var d in (JsonArray)JsonNode.Parse(WebDefsJson)!.DeepClone()) arr.Add(d!.DeepClone());
        return arr;
    }

    static string A(JsonNode? a, string k) => a?[k]?.ToString() ?? "";

    public async Task<string> Execute(string name, JsonNode? args, CancellationToken ct)
    {
        switch (name)
        {
            case "search_files":
            {
                var hits = await _search.Query(A(args, "query"), _s.NumCtx >= 16384 ? 7 : 4, ct);
                if (hits.Count == 0) return "مفيش نتايج. يمكن الفهرس فاضي أو الكلمات مش موجودة.";
                var sb = new StringBuilder();
                foreach (var h in hits) sb.AppendLine($"### {h.Path}\n{h.Text}\n");
                return sb.ToString();
            }
            case "read_file":
            {
                var p = Resolve(A(args, "path"), false);
                if (!File.Exists(p)) return "الملف مش موجود: " + p;
                var t = await Task.Run(() => Extractor.Extract(p), ct);
                return t.Length > 4000 ? t[..4000] + "\n...[اتقطع]" : t;
            }
            case "list_files":
            {
                var f = A(args, "folder");
                var p = string.IsNullOrWhiteSpace(f) ? (_s.IndexFolders.FirstOrDefault() ?? _s.WatchFolder) : Resolve(f, false);
                if (!Directory.Exists(p)) return "الفولدر مش موجود: " + p;
                var items = Directory.EnumerateFileSystemEntries(p).Take(200)
                    .Select(x => Directory.Exists(x) ? "[DIR] " + x : x);
                return string.Join("\n", items);
            }
            case "create_file":
            {
                var p = Resolve(A(args, "path"), true);
                if (File.Exists(p) && !_s.AutoApprove &&
                    !await _approve($"النموذج عايز يكتب فوق الملف ده:\n{p}\n\nتوافق؟"))
                    return "المستخدم رفض الكتابة فوق الملف.";
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                var bk = File.Exists(p) ? Backup(p) : null;
                File.WriteAllText(p, A(args, "content"), new UTF8Encoding(false));
                _audit("WRITE: " + p);
                return "اتكتب الملف: " + p + (bk != null ? "\n(نسخة من القديم: " + bk + ")" : "");
            }
            case "edit_file":
                return await EditFile(args);
            case "read_excel":
            {
                var p = Resolve(A(args, "path"), false);
                if (!File.Exists(p)) return "الملف مش موجود: " + p;
                int rows = int.TryParse(A(args, "max_rows"), out var mr) ? Math.Clamp(mr, 1, 200) : 40;
                var sheet = A(args, "sheet");
                var txt = await Task.Run(() => ExcelReader.Read(p, sheet, rows), ct);
                return txt.Length > 6000 ? txt[..6000] + "\n...[اتقطع]" : txt;
            }
            case "folder_overview":
                return await FolderOverview(A(args, "folder"), ct);
            case "run_python":
                return await RunPython(A(args, "code"), ct);
            case "remember":
                if (!_s.MemoryOn) return "الذاكرة مقفولة من الإعدادات.";
                _audit("MEMORY+: " + A(args, "fact"));
                return Memory.Add(A(args, "fact"));
            case "forget":
            {
                if (!_s.MemoryOn) return "الذاكرة مقفولة من الإعدادات.";
                var forgetText = A(args, "text");
                _audit("MEMORY-: " + forgetText);
                var forgetRes = Memory.Forget(forgetText);
                var lessonsRemoved = Lessons.Forget(forgetText);
                return lessonsRemoved > 0 ? forgetRes + " واتمسح " + lessonsRemoved + " درس من ذاكرة الدروس." : forgetRes;
            }
            case "note_lesson":
            {
                if (!_s.MemoryOn) return "الذاكرة مقفولة من الإعدادات.";
                var lessonText = A(args, "lesson");
                _audit("LESSON+: " + lessonText);
                return Lessons.Add(lessonText);
            }
            case "web_search":
                if (!_s.WebSearch) return "بحث الإنترنت مقفول. المستخدم لازم يفعّله من زرار 🌐 في المحادثة.";
                _audit("WEB SEARCH: " + A(args, "query"));
                return await Web.Search(A(args, "query"), ct);
            case "web_fetch":
                if (!_s.WebSearch) return "بحث الإنترنت مقفول. المستخدم لازم يفعّله من زرار 🌐 في المحادثة.";
                _audit("WEB FETCH: " + A(args, "url"));
                return await Web.Fetch(A(args, "url"), ct);
            case "find_program":
                return await ToolFinder.FindAnyAsync(A(args, "name"), ct);
            case "run_command":
                return await RunCommand(A(args, "command"), ct);
            default:
                return "أداة مش معروفة: " + name;
        }
    }

    // ---------- backup + edit_file + folder_overview ----------
    string Backup(string p)
    {
        var dir = Path.Combine(_s.WorkFolder, ".backup");
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Path.GetFileName(p));
        File.Copy(p, dst, true);
        return dst;
    }

    static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    static string DiffPreview(string o, string n)
    {
        string Lines(string x, string mark) =>
            string.Join("\n", Clip(x, 800).Split('\n').Select(l => mark + l.TrimEnd('\r')));
        return Lines(o, "− ") + "\n" + Lines(n, "+ ");
    }

    async Task<string> EditFile(JsonNode? args)
    {
        var p = Resolve(A(args, "path"), true);
        if (!File.Exists(p)) return "الملف مش موجود: " + p + " (استخدم create_file للملفات الجديدة).";
        var oldT = A(args, "old_text"); var newT = A(args, "new_text");
        bool all = A(args, "replace_all").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (oldT.Length == 0) return "old_text فاضي.";

        string content;
        try { content = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(p)); }
        catch (Exception) { return "الملف مش UTF-8، مش هقدر أعدّله بأمان."; }

        if (content.Contains("\r\n"))
        {
            oldT = oldT.Replace("\r\n", "\n").Replace("\n", "\r\n");
            newT = newT.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        int count = 0, pos = 0;
        while ((pos = content.IndexOf(oldT, pos, StringComparison.Ordinal)) >= 0) { count++; pos += oldT.Length; }
        if (count == 0) return "النص القديم مش موجود بالظبط (راجع المسافات والسطور). اقرا الملف بـ read_file وانسخ النص زي ما هو.";
        if (count > 1 && !all) return $"النص موجود {count} مرة. زوّد سياق حواليه عشان يبقى فريد، أو استخدم replace_all=true.";

        if (!_s.AutoApprove &&
            !await _approve($"النموذج عايز يعدّل الملف:\n{p}\n\n{DiffPreview(oldT, newT)}\n\nعدد الاستبدالات: {count}\nهتتحفظ نسخة احتياطية قبل التعديل.\n\nتوافق؟"))
            return "المستخدم رفض التعديل.";

        var bk = Backup(p);
        var result = all ? content.Replace(oldT, newT, StringComparison.Ordinal)
                         : content[..content.IndexOf(oldT, StringComparison.Ordinal)] + newT +
                           content[(content.IndexOf(oldT, StringComparison.Ordinal) + oldT.Length)..];
        await File.WriteAllBytesAsync(p, new UTF8Encoding(false).GetBytes(result));
        _audit($"EDIT: {p} ({count})");
        return $"اتعدّل الملف ({count} استبدال): {p}\nنسخة من القديم: {bk}";
    }

    async Task<string> FolderOverview(string folder, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(folder) ? (_s.IndexFolders.FirstOrDefault() ?? _s.WatchFolder) : Resolve(folder, false);
        if (!Directory.Exists(root)) return "الفولدر مش موجود: " + root;
        var text = await Task.Run(() =>
        {
            var skip = new HashSet<string>(_s.SkipNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var files = new List<FileInfo>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && files.Count < 20000)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(d))
                        if (!skip.Contains(Path.GetFileName(sub))) stack.Push(sub);
                    foreach (var f in Directory.EnumerateFiles(d)) files.Add(new FileInfo(f));
                }
                catch { }
            }
            if (files.Count == 0) return "الفولدر فاضي: " + root;

            var sb = new StringBuilder();
            sb.AppendLine("الفولدر: " + root);
            sb.AppendLine($"عدد الملفات: {files.Count}{(files.Count >= 20000 ? "+" : "")}   الحجم الكلي: {Ui.Gb(files.Sum(f => f.Length))}");
            sb.AppendLine("\nحسب النوع:");
            foreach (var g in files.GroupBy(f => f.Extension.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(10))
                sb.AppendLine($"- {(g.Key.Length == 0 ? "(بدون امتداد)" : g.Key)}: {g.Count()} ملف ({Ui.Gb(g.Sum(f => f.Length))})");
            sb.AppendLine("\nأكبر الملفات:");
            foreach (var f in files.OrderByDescending(f => f.Length).Take(5))
                sb.AppendLine($"- {Path.GetRelativePath(root, f.FullName)} ({Ui.Gb(f.Length)})");
            sb.AppendLine("\nأحدث الملفات:");
            foreach (var f in files.OrderByDescending(f => f.LastWriteTime).Take(5))
                sb.AppendLine($"- {Path.GetRelativePath(root, f.FullName)} ({f.LastWriteTime:yyyy-MM-dd})");

            sb.AppendLine("\nمعاينة محتوى:");
            string[] previewExt = { ".txt", ".md", ".csv", ".json", ".docx", ".pdf", ".py", ".cs" };
            int n = 0;
            foreach (var f in files.Where(f => previewExt.Contains(f.Extension.ToLowerInvariant()) && f.Length < 3_000_000)
                                   .OrderByDescending(f => f.LastWriteTime))
            {
                if (n >= 8) break;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var c = Extractor.Extract(f.FullName).Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (c.Length == 0) continue;
                    sb.AppendLine($"- {Path.GetRelativePath(root, f.FullName)}: {Clip(c, 200)}");
                    n++;
                }
                catch { }
            }
            return sb.ToString();
        }, ct);
        return text.Length > 4500 ? text[..4500] + "\n...[اتقطع]" : text;
    }

    string Resolve(string p, bool write)
    {
        if (string.IsNullOrWhiteSpace(p)) throw new Exception("المسار فاضي");
        var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(_s.WorkFolder, p));
        var roots = write ? new[] { _s.WorkFolder } : new[] { _s.WorkFolder, _s.WatchFolder }.Concat(_s.IndexFolders).ToArray();
        foreach (var r in roots)
        {
            var rr = Path.GetFullPath(r).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(rr, StringComparison.OrdinalIgnoreCase)) return full;
        }
        throw new Exception("المسار برا الفولدرات المسموحة: " + full);
    }

    public static string LibDir => Path.Combine(Settings.DataDir, "lib");

    /// <summary>بتفك مكتبة الحسابات المدمجة في فولدر البيانات (بتتحدّث مع كل إصدار).</summary>
    static void EnsureCalcLib()
    {
        try
        {
            Directory.CreateDirectory(LibDir);
            foreach (var n in new[] { "vexyon_calc.py", "test_vexyon_calc.py", "vexyon_math.py", "test_vexyon_math.py" })
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/" + n));
                if (info == null) continue;
                using var src = info.Stream;
                using var dst = File.Create(Path.Combine(LibDir, n));
                src.CopyTo(dst);
            }
        }
        catch { }
    }

    static (string exe, string[] pre) PyExe()
    {
        var exe = ToolFinder.PathOf("Python") ?? "python";
        var pre = Path.GetFileNameWithoutExtension(exe).Equals("py", StringComparison.OrdinalIgnoreCase)
            ? new[] { "-3" } : Array.Empty<string>();
        return (exe, pre);
    }

    /// <summary>بتشغّل اختبارات مكتبة الحسابات وبترجّع النتيجة (للزرار في الإعدادات).</summary>
    public static async Task<string> CalcSelfTest(CancellationToken ct = default)
    {
        EnsureCalcLib();
        var (exe, pre) = PyExe();
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = LibDir, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in pre) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-m", "unittest", "discover", "-s", ".", "-p", "test_vexyon*.py" }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch { return "بايثون مش متثبّت أو مش لاقيه. نزّله من تبويب الأدوات."; }
        using (proc)
        {
            var o = proc.StandardOutput.ReadToEndAsync();
            var e = proc.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return "انتهت مهلة الاختبار."; }
            var text = ((await o) + (await e)).Trim();
            return (proc.ExitCode == 0 ? "✔ كل الاختبارات نجحت\n\n" : "✘ فيه اختبارات فشلت، متعتمدش على المكتبة:\n\n")
                   + (text.Length > 2500 ? text[^2500..] : text);
        }
    }

    static readonly string[] PyBlocked =
    {
        @"\bsubprocess\b", @"\bos\s*\.\s*(system|popen|remove|unlink|rmdir|removedirs|startfile)\b", @"\bshutil\s*\.\s*rmtree\b",
        @"\bsocket\b", @"\bctypes\b", @"\bwinreg\b", @"\b(urllib|requests|http\.client|ftplib|smtplib)\b",
        @"__import__", @"\bexec\s*\(", @"\beval\s*\(",
        // تقوية (v10): استيراد ديناميكي، تحايل بالـ builtins، حذف/نقل/قتل عمليات، تشغيل عمليات بطرق تانية
        @"\bimportlib\b", @"(?<![\w.])compile\s*\(", @"__builtins__", @"\bmultiprocessing\b", @"\bpty\b", @"\bwebbrowser\b",
        @"\bos\s*\.\s*(rename|replace|chmod|chown|kill|killpg|fork|spawn\w*|exec\w*)\b",
        @"\bfrom\s+os\s+import\b[^\n]*\b(system|popen|remove|unlink|rmdir|startfile|rename|kill|spawn\w*|exec\w*)",
        @"\.\s*(unlink|rmdir)\s*\(", @"\bshutil\s*\.\s*(move|rmtree)\b",
        @"\bgetattr\s*\(\s*(os|sys|builtins)\b", @"\bsys\s*\.\s*modules\b"
    };

    async Task<string> RunPython(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return "الكود فاضي.";
        if (code.Length > 8000) return "الكود طويل أوي (أقصى 8000 حرف). قسّمه.";
        foreach (var b in PyBlocked)
            if (Regex.IsMatch(code, b))
                return "الكود فيه عملية ممنوعة لأسباب أمان (تشغيل أوامر/حذف ملفات/شبكة). الحسابات والكتابة في فولدر الشغل مسموحة.";

        var (exe, pre) = PyExe();
        EnsureCalcLib();

        if (!_s.AutoApprove &&
            !await _approve($"النموذج عايز يشغّل كود بايثون ده:\n\n{Clip(code, 2500)}\n\nفي الفولدر:\n{_s.WorkFolder}\n\nتوافق؟"))
            return "المستخدم رفض تشغيل الكود.";

        Directory.CreateDirectory(_s.WorkFolder);
        var dir = Path.Combine(_s.WorkFolder, ".scripts");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".py");
        await File.WriteAllTextAsync(file, code, new UTF8Encoding(false), ct);
        _audit("PYTHON: " + file);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _s.WorkFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in pre) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(file);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONPATH"] = LibDir;
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception)
        {
            return "بايثون مش متثبّت أو مش لاقيه. نزّله من تبويب الأدوات (ومعاه Add to PATH) وجرّب تاني.";
        }
        using (proc)
        {
            var o = proc.StandardOutput.ReadToEndAsync();
            var e = proc.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return "انتهت المهلة (30 ثانية) واتقفل الكود.";
            }
            var text = (await o) + (await e);
            if (text.Length > 3000) text = text[..3000] + "\n...[اتقطع]";
            if (text.Trim().Length == 0) text = "(مفيش مخرجات. استخدم print() عشان تطبع النتيجة)";
            return $"exit code {proc.ExitCode}\n{text}";
        }
    }

    async Task<string> RunCommand(string cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "الأمر فاضي";
        foreach (var b in Blocked)
            if (Regex.IsMatch(cmd, b, RegexOptions.IgnoreCase))
                return "الأمر ده ممنوع لأسباب أمان.";
        // لو الأمر بيشغّل ملف سكربت (bat/cmd/ps1): نقرا محتواه ونعرضه، ونمنع الأوامر الخطيرة جواه
        var extra = new StringBuilder();
        bool warnDelete = false;
        foreach (Match m in Regex.Matches(cmd, @"[^\s""&|<>]+\.(bat|cmd|ps1)", RegexOptions.IgnoreCase))
        {
            try
            {
                var sp = m.Value.Trim('"');
                var full = Path.GetFullPath(Path.IsPathRooted(sp) ? sp : Path.Combine(_s.WorkFolder, sp));
                if (!File.Exists(full)) continue;
                var content = File.ReadAllText(full);
                foreach (var b in Blocked)
                {
                    if (!Regex.IsMatch(content, b, RegexOptions.IgnoreCase | RegexOptions.Multiline)) continue;
                    if (RecursiveDelete.Contains(b)) warnDelete = true;
                    else return $"الملف {Path.GetFileName(full)} فيه أمر ممنوع لأسباب أمان.";
                }
                extra.Append($"\n\n--- محتوى {Path.GetFileName(full)} ---\n")
                     .Append(content.Length > 3000 ? content[..3000] + "\n...[اتقطع]" : content);
            }
            catch { }
        }
        if (!_s.AutoApprove)
        {
            var warn = warnDelete ? "⚠ السكربت فيه أوامر حذف متكرر (/s). راجع المسارات كويس قبل ما توافق.\n\n" : "";
            if (!await _approve($"{warn}النموذج عايز ينفّذ الأمر ده:\n\n{cmd}{extra}\n\nفي الفولدر:\n{_s.WorkFolder}\n\nتوافق؟"))
                return "المستخدم رفض تنفيذ الأمر.";
        }

        Directory.CreateDirectory(_s.WorkFolder);
        _audit("RUN: " + cmd);
        var psi = new ProcessStartInfo("cmd.exe", "/c chcp 65001 >nul & " + cmd)
        {
            WorkingDirectory = _s.WorkFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var extraPath = ToolFinder.ExtraPath();
        if (extraPath.Length > 0)
            psi.Environment["PATH"] = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + extraPath;
        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEndAsync();
        var e = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return "انتهت المهلة (60 ثانية) واتقفل الأمر.";
        }
        var text = (await o) + (await e);
        if (text.Length > 2500) text = text[..2500] + "\n...[اتقطع]";
        return $"exit code {proc.ExitCode}\n{text}";
    }
}
