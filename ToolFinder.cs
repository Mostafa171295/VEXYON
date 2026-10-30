using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
namespace VEXYON;

/// <summary>
/// بيدوّر على البرامج المثبّتة في كل أماكن الجهاز:
/// 1) سجل ويندوز (App Paths + قايمة البرامج المثبّتة)
/// 2) متغير PATH
/// 3) فولدرات التثبيت الشائعة على كل الأقراص (Program Files, Tools, Apps ...)
/// 4) مسح خفيف لجذر كل قرص ثابت
/// والنتيجة بتتحفظ في %AppData%\VEXYON\tools.json عشان الفتحة الجاية تبقى فورية.
/// </summary>
public static class ToolFinder
{
    public record Spec(string Name, string[] Exes, string[] Apps);

    // Name لازم يطابق اسم الأداة في Catalog (MainWindow.xaml.cs)
    public static readonly Spec[] Specs =
    {
        new("Ollama", new[] { "ollama.exe" }, new[] { "Ollama" }),
        new("AnythingLLM", new[] { "AnythingLLM.exe" }, new[] { "AnythingLLM" }),
        new("LM Studio", new[] { "LM Studio.exe" }, new[] { "LM Studio" }),
        new("Python", new[] { "python.exe", "py.exe" }, new[] { "Python" }),
        new(".NET 8 SDK", new[] { "dotnet.exe" }, new[] { ".NET SDK", "Microsoft .NET SDK" }),
        new("Visual Studio Code", new[] { "Code.exe" }, new[] { "Visual Studio Code" }),
        new("Git", new[] { "git.exe" }, new[] { "Git" }),
        new("Tesseract OCR", new[] { "tesseract.exe" }, new[] { "Tesseract" }),
        new("ODA File Converter", new[] { "ODAFileConverter.exe" }, new[] { "ODA File Converter" }),
        new("LibreOffice", new[] { "soffice.exe" }, new[] { "LibreOffice" }),
        new("Pandoc", new[] { "pandoc.exe" }, new[] { "Pandoc" }),
        new("7-Zip", new[] { "7z.exe" }, new[] { "7-Zip" }),
        new("FFmpeg", new[] { "ffmpeg.exe" }, Array.Empty<string>()),
        new("Node.js", new[] { "node.exe" }, new[] { "Node.js" }),
        new("Everything", new[] { "Everything.exe", "es.exe" }, new[] { "Everything" }),
        new("Jan", new[] { "Jan.exe" }, Array.Empty<string>()),
        new("AutoCAD", new[] { "acad.exe", "accoreconsole.exe" }, new[] { "AutoCAD" }),
        new("Microsoft Word", new[] { "WINWORD.EXE" }, new[] { "Microsoft 365", "Microsoft Office" }),
        new("Microsoft Excel", new[] { "EXCEL.EXE" }, new[] { "Microsoft 365", "Microsoft Office" }),
        new("Microsoft PowerPoint", new[] { "POWERPNT.EXE" }, new[] { "Microsoft 365", "Microsoft Office" }),
    };

    record Entry(string Name, string? Location, string? Icon);

    static readonly object Lock = new();
    static readonly Dictionary<string, string> _exePaths = new(StringComparer.OrdinalIgnoreCase);
    static string CacheFile => Path.Combine(Settings.DataDir, "tools.json");

    /// <summary>true بعد ما أول فحص كامل يخلص.</summary>
    public static bool Done { get; private set; }

    static ToolFinder() { LoadCache(); }

    // ---------- cache ----------
    static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile));
            if (d == null) return;
            foreach (var kv in d)
                if (File.Exists(kv.Value)) _exePaths[kv.Key] = kv.Value;
        }
        catch { }
    }

    static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            Dictionary<string, string> copy;
            lock (Lock) { copy = new Dictionary<string, string>(_exePaths); }
            File.WriteAllText(CacheFile,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------- public queries ----------
    public static bool Knows(string tool) =>
        Specs.Any(s => s.Name.Equals(tool, StringComparison.OrdinalIgnoreCase));

    public static string? PathOf(string tool)
    {
        var spec = Specs.FirstOrDefault(s => s.Name.Equals(tool, StringComparison.OrdinalIgnoreCase));
        if (spec == null) return null;
        lock (Lock)
        {
            foreach (var e in spec.Exes)
                if (_exePaths.TryGetValue(e, out var p) && File.Exists(p)) return p;
        }
        return null;
    }

    public static int FoundCount => Specs.Count(s => PathOf(s.Name) != null);

    static readonly HashSet<string> AgentSkip = new(StringComparer.OrdinalIgnoreCase)
        { "Ollama", "AnythingLLM", "LM Studio", "Jan", "Everything" };

    /// <summary>أسماء البرامج اللي النموذج يقدر يشغّلها بالاسم (من غير مسار).</summary>
    public static string AgentSummary()
    {
        var names = new List<string>();
        foreach (var s in Specs)
        {
            if (AgentSkip.Contains(s.Name)) continue;
            var p = PathOf(s.Name);
            if (p != null) names.Add(Path.GetFileNameWithoutExtension(p));
        }
        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>فولدرات البرامج المكتشفة، بتتضاف في آخر PATH لأوامر النموذج.</summary>
    public static string ExtraPath()
    {
        var dirs = new List<string>();
        lock (Lock)
        {
            foreach (var p in _exePaths.Values)
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d) &&
                    !d.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    dirs.Add(d);
            }
        }
        return string.Join(";", dirs.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // ---------- full detection ----------
    public static Task DetectAllAsync(CancellationToken ct = default) =>
        Task.Run(() => DetectAll(ct), ct);

    static void DetectAll(CancellationToken ct)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var want = Specs.SelectMany(s => s.Exes).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1) App Paths في السجل + PATH
            foreach (var exe in want)
            {
                var p = FromAppPaths(exe) ?? FromPath(exe);
                if (p != null) found[exe] = p;
            }

            // 2) قايمة البرامج المثبّتة في السجل (بتدلّنا على مكان التثبيت)
            var entries = UninstallEntries();
            foreach (var spec in Specs)
            {
                var missing = spec.Exes.Where(e => !found.ContainsKey(e)).ToList();
                if (missing.Count == 0) continue;
                foreach (var en in entries.Where(x => spec.Apps.Any(a => NameMatches(x.Name, a))))
                {
                    foreach (var dir in CandidateDirs(en))
                    {
                        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Walk(dir, 3, missing.ToHashSet(StringComparer.OrdinalIgnoreCase), hits, deadline, ct);
                        foreach (var kv in hits) found.TryAdd(kv.Key, kv.Value);
                        missing = missing.Where(e => !found.ContainsKey(e)).ToList();
                        if (missing.Count == 0) break;
                    }
                    if (missing.Count == 0) break;
                }
            }

            // 3) مسح أماكن التثبيت الشائعة على كل الأقراص، وبعدين جذر كل قرص
            var still = want.Where(e => !found.ContainsKey(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (still.Count > 0)
            {
                var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in PriorityRoots()) Walk(root, 4, still, hits, deadline, ct);
                if (hits.Count < still.Count)
                    foreach (var root in FixedDrives()) Walk(root, 3, still, hits, deadline, ct);
                foreach (var kv in hits) found.TryAdd(kv.Key, kv.Value);
            }

            lock (Lock)
            {
                foreach (var kv in found) _exePaths[kv.Key] = kv.Value;
                foreach (var k in _exePaths.Keys.ToList())
                    if (!File.Exists(_exePaths[k])) _exePaths.Remove(k);
            }
            SaveCache();
        }
        finally { Done = true; }
    }

    // ---------- بحث عن أي برنامج بالاسم (أداة find_program للنموذج) ----------
    public static Task<string> FindAnyAsync(string query, CancellationToken ct) =>
        Task.Run(() => FindAny(query, ct), ct);

    static string FindAny(string query, CancellationToken ct)
    {
        query = (query ?? "").Trim().Trim('"');
        if (query.Length < 2)
            return "اكتب اسم البرنامج أو اسم الملف التنفيذي (مثلاً tesseract أو AutoCAD).";
        var exe = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query : query + ".exe";
        var lines = new List<string>();

        var p = FromAppPaths(exe) ?? FromPath(exe);
        if (p != null) lines.Add("EXE: " + p);

        foreach (var en in UninstallEntries().Where(e => NameMatches(e.Name, query)).Take(5))
        {
            lines.Add("INSTALLED: " + en.Name);
            foreach (var dir in CandidateDirs(en).Take(2))
            {
                lines.Add("  folder: " + dir);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.exe")
                        .Where(fn => !Regex.IsMatch(Path.GetFileName(fn), "unins|setup|update", RegexOptions.IgnoreCase))
                        .Take(8))
                        lines.Add("  exe: " + f);
                }
                catch { }
            }
        }

        if (lines.Count == 0)
        {
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe };
            var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow.AddSeconds(25);
            foreach (var r in PriorityRoots()) Walk(r, 4, want, hits, deadline, ct);
            if (hits.Count == 0)
                foreach (var r in FixedDrives()) Walk(r, 4, want, hits, deadline, ct);
            foreach (var kv in hits) lines.Add("EXE: " + kv.Value);
        }

        return lines.Count == 0
            ? "ملقيتش البرنامج ده على الجهاز: " + query
            : string.Join("\n", lines.Take(20));
    }

    // ---------- helpers ----------
    static bool NameMatches(string displayName, string app) =>
        Regex.IsMatch(displayName,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(app) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase);

    static readonly string[] BadParts =
    {
        @"\WindowsApps\", @"\venv\", @"\.venv\", @"\site-packages\",
        @"\$Recycle.Bin\", @"\Temp\", @"\node_modules\", @"\Package Cache\"
    };

    static bool Good(string path) =>
        !BadParts.Any(b => path.Contains(b, StringComparison.OrdinalIgnoreCase));

    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "WinSxS",
        "Temp", "tmp", "Recovery", "Config.Msi", "Documents and Settings", "site-packages",
        "venv", ".venv", "__pycache__", "Package Cache", "Installer"
    };

    static IEnumerable<string> FixedDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok = false;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    static IEnumerable<string> PriorityRoots()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop")
        };
        foreach (var drive in FixedDrives())
            foreach (var n in new[] { "Program Files", "Program Files (x86)", "Programs", "Tools", "Apps", "Software", "Portable" })
                list.Add(Path.Combine(drive, n));
        return list
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string? FromAppPaths(string exe)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                var v = k?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    v = v.Trim().Trim('"');
                    if (File.Exists(v) && Good(v)) return v;
                }
            }
            catch { }
        }
        return null;
    }

    static string? FromPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                var f = Path.Combine(dir, exe);
                if (File.Exists(f)) return f;
            }
            catch { }
        }
        return null;
    }

    static List<Entry> UninstallEntries()
    {
        var list = new List<Entry>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var root = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        var name = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var loc = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                        var icon = k.GetValue("DisplayIcon") as string;
                        list.Add(new Entry(name, loc, CleanIcon(icon)));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    static string? CleanIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        if (s.StartsWith("\""))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) return s.Substring(1, end - 1);
        }
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s.Substring(comma + 1).Trim(), out _)) s = s.Substring(0, comma);
        return s.Trim('"');
    }

    static IEnumerable<string> CandidateDirs(Entry en)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(en.Location)) dirs.Add(en.Location!);
        if (!string.IsNullOrWhiteSpace(en.Icon))
        {
            try
            {
                var d = Path.GetDirectoryName(en.Icon);
                if (!string.IsNullOrEmpty(d)) dirs.Add(d);
            }
            catch { }
        }
        return dirs
            .Where(d => { try { return Directory.Exists(d); } catch { return false; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>مسح فولدر لحد عمق معين، بيسجل أول نتيجة لكل اسم ملف مطلوب.</summary>
    static void Walk(string root, int maxDepth, HashSet<string> want,
        Dictionary<string, string> hits, DateTime deadline, CancellationToken ct)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return;
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var n = Path.GetFileName(f);
                    if (want.Contains(n) && !hits.ContainsKey(n) && Good(f)) hits[n] = f;
                }
                if (hits.Count >= want.Count) return;
                if (depth >= maxDepth) continue;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (Skip.Contains(name)) continue;
                    try
                    {
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push((d, depth + 1));
                }
            }
            catch { }
        }
    }
}
