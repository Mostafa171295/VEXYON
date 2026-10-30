using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace VEXYON;

// ===================== Ollama model store =====================
public class ModelInfo
{
    public string Id = "";                       // مثال: qwen3:8b
    public string Rel = "";                      // مسار الـ manifest نسبةً لفولدر manifests
    public List<(string digest, long size)> Blobs = new();
    public long Size => Blobs.Sum(b => b.size);
}

public static class OllamaStore
{
    public static string ModelsDir()
    {
        var env = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim().Trim('"');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    }

    public static string BlobName(string digest) => digest.Replace(':', '-');

    public static List<ModelInfo> List(string dir)
    {
        var res = new List<ModelInfo>();
        var man = Path.Combine(dir, "manifests");
        if (!Directory.Exists(man)) return res;
        foreach (var f in Directory.EnumerateFiles(man, "*", SearchOption.AllDirectories))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f));
                var mi = new ModelInfo { Rel = Path.GetRelativePath(man, f) };
                var parts = mi.Rel.Split('\\', '/').ToList();
                var tag = parts[^1]; parts.RemoveAt(parts.Count - 1);
                if (parts.Count > 0 && parts[0] == "registry.ollama.ai") parts.RemoveAt(0);
                if (parts.Count > 0 && parts[0] == "library") parts.RemoveAt(0);
                mi.Id = string.Join("/", parts) + ":" + tag;

                void AddBlob(JsonNode? n)
                {
                    var d = n?["digest"]?.ToString();
                    if (string.IsNullOrEmpty(d)) return;
                    long sz = 0;
                    try { sz = n!["size"]!.GetValue<long>(); } catch { }
                    mi.Blobs.Add((d!, sz));
                }
                AddBlob(j?["config"]);
                if (j?["layers"] is JsonArray ls) foreach (var l in ls) AddBlob(l);
                res.Add(mi);
            }
            catch { }
        }
        return res.OrderBy(m => m.Id).ToList();
    }
}

// ===================== copy engine =====================
public record CopyItem(string Src, string Dst, long Size);

public static class Copier
{
    public static async Task Run(List<CopyItem> items, IProgress<(double pct, string msg)> prog, CancellationToken ct)
    {
        long total = Math.Max(1, items.Sum(i => i.Size));
        long done = 0;
        var buf = new byte[4 * 1024 * 1024];
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(it.Dst);
            if (File.Exists(it.Dst) && new FileInfo(it.Dst).Length == it.Size)
            {
                done += it.Size;
                prog.Report((done * 100.0 / total, "موجود: " + name));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(it.Dst)!);
            var tmp = it.Dst + ".part";
            using (var ins = new FileStream(it.Src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true))
            using (var outs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                int n;
                while ((n = await ins.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await outs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    prog.Report((done * 100.0 / total, "بينسخ: " + name));
                }
            }
            File.Move(tmp, it.Dst, true);
        }
    }
}

public static class BackupPlan
{
    public static void AddModels(List<CopyItem> items, IEnumerable<ModelInfo> models, string srcDir, string dstDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            var sm = Path.Combine(srcDir, "manifests", m.Rel);
            var dm = Path.Combine(dstDir, "manifests", m.Rel);
            if (File.Exists(sm) && seen.Add(dm)) items.Add(new CopyItem(sm, dm, new FileInfo(sm).Length));
            foreach (var (digest, _) in m.Blobs)
            {
                var sb = Path.Combine(srcDir, "blobs", OllamaStore.BlobName(digest));
                var db = Path.Combine(dstDir, "blobs", OllamaStore.BlobName(digest));
                if (File.Exists(sb) && seen.Add(db)) items.Add(new CopyItem(sb, db, new FileInfo(sb).Length));
            }
        }
    }

    public static void AddDir(List<CopyItem> items, string srcDir, string dstDir, string? skipUnder = null)
    {
        if (!Directory.Exists(srcDir)) return;
        foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (skipUnder != null && f.StartsWith(skipUnder, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new CopyItem(f, Path.Combine(dstDir, Path.GetRelativePath(srcDir, f)), new FileInfo(f).Length));
        }
    }

    public static void WriteExtras(string root, List<ModelInfo> models, List<string> installers, bool app, bool settings, bool chats)
    {
        var j = new JsonObject
        {
            ["app"] = "VEXYON",
            ["created"] = DateTime.Now.ToString("s"),
            ["models"] = new JsonArray(models.Select(m => (JsonNode?)JsonValue.Create(m.Id)).ToArray()),
            ["installers"] = new JsonArray(installers.Select(f => (JsonNode?)JsonValue.Create(Path.GetFileName(f))).ToArray()),
            ["app_files"] = app,
            ["settings"] = settings,
            ["chats"] = chats
        };
        File.WriteAllText(Path.Combine(root, "backup.json"),
            j.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));

        var readme =
            "VEXYON - نسخة احتياطية / حزمة أوفلاين\r\n" +
            "========================================\r\n\r\n" +
            "طريقة التشغيل على جهاز جديد (من غير نت):\r\n" +
            "1) ثبّت Ollama من فولدر Installers (ملف OllamaSetup.exe).\r\n" +
            "2) انسخ النماذج: شغّل Restore-Models.bat (أو من VEXYON: الإعدادات > استرجاع نسخة).\r\n" +
            "3) شغّل VEXYON.exe من فولدر App.\r\n" +
            "4) ثبّت باقي البرامج اللي في Installers لو محتاجها (Python وغيره).\r\n\r\n" +
            "ملحوظة: الفهرس مش متضمّن. حط ملفاتك في فولدر المراقبة وهتتفهرس على الجهاز الجديد.\r\n" +
            "قبل التوزيع: راجع تراخيص النماذج والبرامج.\r\n";
        File.WriteAllText(Path.Combine(root, "README.txt"), readme, new UTF8Encoding(true));

        if (models.Count > 0)
        {
            var bat =
                "@echo off\r\nchcp 65001 >nul\r\n" +
                "set DEST=%USERPROFILE%\\.ollama\\models\r\n" +
                "echo Copying models to %DEST% ...\r\n" +
                "xcopy \"%~dp0Models\\*\" \"%DEST%\\\" /E /I /Y\r\n" +
                "echo.\r\necho Done. Install Ollama first (Installers folder) if you have not, then run VEXYON.\r\npause\r\n";
            File.WriteAllText(Path.Combine(root, "Restore-Models.bat"), bat, new UTF8Encoding(false));
        }
    }
}

// ===================== UI helpers =====================
public static class Ui
{
    public static string Gb(long b) => b >= (1L << 30) ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";

    public static TextBlock T(string text, string brush = "Ink", bool bold = false, double size = 14, Thickness? m = null)
    {
        var t = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Margin = m ?? new Thickness(0, 0, 0, 6)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock H(string text) => T(text, "Blue", true, 16, new Thickness(0, 14, 0, 6));

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }
}

public abstract class DialogBase : Window
{
    protected readonly StackPanel Panel = new() { Margin = new Thickness(16) };
    protected readonly ProgressBar Bar = new() { Margin = new Thickness(0, 8, 0, 6) };
    protected readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap };
    protected readonly Button Go = new() { Width = 160 };
    protected readonly Button Stop = new() { Width = 110, Content = "إغلاق" };
    protected CancellationTokenSource? Cts;
    protected bool Running;

    protected DialogBase(Window owner, string title)
    {
        Owner = owner; Title = title; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 620; Height = 720; MinHeight = 480; FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI, Tahoma"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "WinBg");
        SetResourceReference(ForegroundProperty, "Ink");
        Status.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
        Stop.Style = (Style)Application.Current.FindResource("Ghost");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Panel });
        var foot = new StackPanel { Margin = new Thickness(16, 0, 16, 14) };
        foot.Children.Add(Bar); foot.Children.Add(Status);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        btns.Children.Add(Go); btns.Children.Add(Stop);
        foot.Children.Add(btns);
        Grid.SetRow(foot, 1);
        root.Children.Add(foot);
        Content = root;

        Stop.Click += (_, _) => { if (Running) Cts?.Cancel(); else Close(); };
        Closing += (_, _) => { if (Running) Cts?.Cancel(); };
    }

    protected void Begin() { Running = true; Go.IsEnabled = false; Cts = new CancellationTokenSource(); Stop.Content = "إيقاف"; }
    protected void End() { Running = false; Go.IsEnabled = true; Stop.Content = "إغلاق"; }

    protected IProgress<(double pct, string msg)> Prog() =>
        new Progress<(double pct, string msg)>(p => { Bar.Value = p.pct; Status.Text = p.msg; });

    protected static bool EnoughSpace(string path, long need, out string msg)
    {
        msg = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (drive.AvailableFreeSpace < need + 200_000_000)
            {
                msg = $"المساحة مش كفاية: محتاج {Ui.Gb(need)} والمتاح {Ui.Gb(drive.AvailableFreeSpace)}.";
                return false;
            }
        }
        catch { }
        return true;
    }
}

// ===================== Backup dialog =====================
public class BackupDialog : DialogBase
{
    readonly Settings _s;
    readonly List<(CheckBox cb, ModelInfo m)> _models = new();
    readonly List<(CheckBox cb, string path, long size)> _inst = new();
    readonly StackPanel _instPanel = new();
    readonly CheckBox _app = new() { Content = "ملفات البرنامج نفسه (VEXYON) عشان يشتغل على أي جهاز", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    readonly CheckBox _set = new() { Content = "الإعدادات والتعليمات والذاكرة (النماذج المختارة، الوضع الداكن...)", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    readonly CheckBox _chats = new() { Content = "المحادثات المحفوظة", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
    readonly TextBox _dest = new() { IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight };
    readonly TextBlock _size = Ui.T("", "SubInk", false, 13);

    static bool Match(string id, string model) =>
        id.Equals(model, StringComparison.OrdinalIgnoreCase) ||
        (!model.Contains(':') && id.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));

    public BackupDialog(Window owner, Settings s) : base(owner, "نسخ احتياطي / حزمة أوفلاين")
    {
        _s = s;
        _dest.Style = (Style)Application.Current.FindResource("Input");

        Panel.Children.Add(Ui.T("نسخ احتياطي / حزمة أوفلاين", "Ink", true, 20));
        Panel.Children.Add(Ui.T("اختار اللي عايز تحفظه. الفهرس مش بيتنسخ (صاحبك هيفهرس ملفاته هو).", "SubInk"));

        // ---- models
        Panel.Children.Add(Ui.H("النماذج اللي نزّلتها بالنت"));
        var dir = OllamaStore.ModelsDir();
        var list = OllamaStore.List(dir);
        if (list.Count == 0) Panel.Children.Add(Ui.T("مفيش نماذج لقيناها في:\n" + dir, "SubInk"));
        foreach (var m in list)
        {
            var cb = new CheckBox
            {
                Content = $"{m.Id}    ({Ui.Gb(m.Size)})", Margin = new Thickness(0, 3, 0, 3),
                IsChecked = Match(m.Id, _s.ChatModel) || Match(m.Id, _s.EmbedModel)
            };
            cb.Checked += (_, _) => UpdateSize(); cb.Unchecked += (_, _) => UpdateSize();
            _models.Add((cb, m));
            Panel.Children.Add(cb);
        }

        // ---- installers
        Panel.Children.Add(Ui.H("ملفات تثبيت البرامج"));
        Panel.Children.Add(Ui.T("لازم تحط ملف تثبيت Ollama (OllamaSetup.exe) عشان صاحبك يقدر يشغّل النماذج. لو مش لاقيه تحت، نزّله مرة من ollama.com وضيفه.", "SubInk", false, 13));
        Panel.Children.Add(_instPanel);
        ScanDownloads();
        var add = new Button { Content = "＋ إضافة ملف تثبيت", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.FindResource("Ghost") };
        add.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = "ملفات تثبيت|*.exe;*.msi;*.zip|كل الملفات|*.*" };
            if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) AddInstaller(f, true);
        };
        Panel.Children.Add(add);

        // ---- extras
        Panel.Children.Add(Ui.H("معاهم كمان"));
        Panel.Children.Add(_app); Panel.Children.Add(_set); Panel.Children.Add(_chats);

        // ---- destination
        Panel.Children.Add(Ui.H("مكان الحفظ (فلاشة أو هارد)"));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pick = new Button { Content = "اختيار...", Margin = new Thickness(6, 0, 0, 0), Style = (Style)Application.Current.FindResource("Ghost") };
        pick.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = "اختار مكان حفظ النسخة" };
            if (d.ShowDialog(this) == true) _dest.Text = d.FolderName;
        };
        Grid.SetColumn(pick, 1);
        g.Children.Add(_dest); g.Children.Add(pick);
        Panel.Children.Add(g);
        Panel.Children.Add(_size);

        Go.Content = "ابدأ النسخ";
        Go.Click += Go_Click;
        UpdateSize();
    }

    void ScanDownloads()
    {
        try
        {
            var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dl)) return;
            string[] keys = { "ollama", "python", "vscode", "tesseract", "libreoffice", "pandoc", "7z", "ffmpeg", "anythingllm", "odafileconverter", "git-" };
            foreach (var f in Directory.EnumerateFiles(dl))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".exe" && ext != ".msi" && ext != ".zip") continue;
                var n = Path.GetFileName(f).ToLowerInvariant();
                if (keys.Any(k => n.Contains(k))) AddInstaller(f, true);
            }
        }
        catch { }
    }

    void AddInstaller(string path, bool isChecked)
    {
        if (_inst.Any(x => x.path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        long sz = 0; try { sz = new FileInfo(path).Length; } catch { }
        var cb = new CheckBox { Content = $"{Path.GetFileName(path)}    ({Ui.Gb(sz)})", ToolTip = path, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
        cb.Checked += (_, _) => UpdateSize(); cb.Unchecked += (_, _) => UpdateSize();
        _inst.Add((cb, path, sz));
        _instPanel.Children.Add(cb);
        UpdateSize();
    }

    void UpdateSize()
    {
        long t = _models.Where(x => x.cb.IsChecked == true).Sum(x => x.m.Size)
               + _inst.Where(x => x.cb.IsChecked == true).Sum(x => x.size);
        _size.Text = "الحجم التقريبي (من غير ملفات البرنامج): " + Ui.Gb(t);
    }

    async void Go_Click(object s, RoutedEventArgs e)
    {
        if (Running) return;
        if (string.IsNullOrWhiteSpace(_dest.Text)) { Status.Text = "اختار مكان الحفظ الأول."; return; }
        var selModels = _models.Where(x => x.cb.IsChecked == true).Select(x => x.m).ToList();
        var selInst = _inst.Where(x => x.cb.IsChecked == true).Select(x => x.path).ToList();
        bool app = _app.IsChecked == true, set = _set.IsChecked == true, chats = _chats.IsChecked == true;
        if (selModels.Count == 0 && selInst.Count == 0 && !app && !set && !chats) { Status.Text = "اختار حاجة واحدة على الأقل."; return; }

        var root = Path.Combine(_dest.Text, "VEXYON-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm"));
        try
        {
            try { _s.Save(); } catch { }
            var items = new List<CopyItem>();
            BackupPlan.AddModels(items, selModels, OllamaStore.ModelsDir(), Path.Combine(root, "Models"));
            foreach (var f in selInst)
                items.Add(new CopyItem(f, Path.Combine(root, "Installers", Path.GetFileName(f)), new FileInfo(f).Length));
            if (app) BackupPlan.AddDir(items, AppContext.BaseDirectory, Path.Combine(root, "App"), root);
            if (set)
            {
                var sf = Path.Combine(Settings.DataDir, "settings.json");
                if (File.Exists(sf)) items.Add(new CopyItem(sf, Path.Combine(root, "Data", "settings.json"), new FileInfo(sf).Length));
                var mf = Path.Combine(Settings.DataDir, "memory.txt");
                if (File.Exists(mf)) items.Add(new CopyItem(mf, Path.Combine(root, "Data", "memory.txt"), new FileInfo(mf).Length));
            }
            if (chats) BackupPlan.AddDir(items, ChatStore.Dir, Path.Combine(root, "Data", "chats"));

            long need = items.Sum(i => i.Size);
            if (!EnoughSpace(root, need, out var msg)) { Status.Text = msg; return; }

            Begin();
            Directory.CreateDirectory(root);
            await Copier.Run(items, Prog(), Cts!.Token);
            BackupPlan.WriteExtras(root, selModels, selInst, app, set, chats);
            Bar.Value = 100;
            Status.Text = "تم ✔  النسخة اتحفظت في:\n" + root;
        }
        catch (OperationCanceledException) { Status.Text = "اتوقف النسخ."; }
        catch (Exception ex) { Status.Text = "خطأ: " + ex.Message; }
        finally { End(); }
    }
}

// ===================== Restore dialog =====================
public class RestoreDialog : DialogBase
{
    readonly Settings _s;
    readonly string _dir;
    readonly List<(CheckBox cb, ModelInfo m)> _models = new();
    readonly CheckBox? _set, _chats;

    public RestoreDialog(Window owner, Settings s, string dir) : base(owner, "استرجاع نسخة احتياطية")
    {
        _s = s; _dir = dir;
        Panel.Children.Add(Ui.T("استرجاع نسخة احتياطية", "Ink", true, 20));
        Panel.Children.Add(Ui.T(dir, "SubInk", false, 12));

        var modelsDir = Path.Combine(dir, "Models");
        var list = OllamaStore.List(modelsDir);
        if (list.Count > 0)
        {
            Panel.Children.Add(Ui.H("النماذج"));
            foreach (var m in list)
            {
                var cb = new CheckBox { Content = $"{m.Id}    ({Ui.Gb(m.Size)})", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
                _models.Add((cb, m));
                Panel.Children.Add(cb);
            }
        }

        var sf = Path.Combine(dir, "Data", "settings.json");
        var cd = Path.Combine(dir, "Data", "chats");
        bool hasSet = File.Exists(sf), hasChats = Directory.Exists(cd) && Directory.EnumerateFiles(cd, "*.json").Any();
        if (hasSet || hasChats) Panel.Children.Add(Ui.H("بيانات البرنامج"));
        if (hasSet)
        {
            _set = new CheckBox { Content = "الإعدادات والتعليمات والذاكرة (بتتدمج مع الحالية) - فولدراتك الحالية متتغيّرش", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            Panel.Children.Add(_set);
        }
        if (hasChats)
        {
            _chats = new CheckBox { Content = "المحادثات المحفوظة", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            Panel.Children.Add(_chats);
        }

        AddFolderInfo("Installers", "ملفات تثبيت البرامج (ثبّتها بنفسك، وابدأ بـ Ollama)", "فتح فولدر التثبيت");
        AddFolderInfo("App", "ملفات البرنامج نفسه (شغّل VEXYON.exe من جواه)", "فتح فولدر البرنامج");

        Go.Content = "ابدأ الاسترجاع";
        Go.Click += Go_Click;
        if (_models.Count == 0 && _set == null && _chats == null)
        {
            Go.IsEnabled = false;
            Status.Text = "مفيش نماذج أو بيانات تتسترجع في النسخة دي.";
        }
    }

    void AddFolderInfo(string sub, string text, string btnText)
    {
        var p = Path.Combine(_dir, sub);
        if (!Directory.Exists(p)) return;
        Panel.Children.Add(Ui.H(sub));
        Panel.Children.Add(Ui.T(text, "SubInk", false, 13));
        var b = new Button { Content = btnText, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.FindResource("Ghost") };
        b.Click += (_, _) => Ui.OpenFolder(p);
        Panel.Children.Add(b);
    }

    async void Go_Click(object s, RoutedEventArgs e)
    {
        if (Running) return;
        try
        {
            var items = new List<CopyItem>();
            var dst = OllamaStore.ModelsDir();
            var sel = _models.Where(x => x.cb.IsChecked == true).Select(x => x.m).ToList();
            BackupPlan.AddModels(items, sel, Path.Combine(_dir, "Models"), dst);
            if (_chats?.IsChecked == true) BackupPlan.AddDir(items, Path.Combine(_dir, "Data", "chats"), ChatStore.Dir);

            long need = items.Sum(i => i.Size);
            if (!EnoughSpace(dst, need, out var msg)) { Status.Text = msg; return; }

            Begin();
            await Copier.Run(items, Prog(), Cts!.Token);

            if (_set?.IsChecked == true)
            {
                var j = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(_dir, "Data", "settings.json")));
                if (j != null)
                {
                    _s.ChatModel = j.ChatModel; _s.EmbedModel = j.EmbedModel;
                    _s.AutoApprove = j.AutoApprove; _s.DarkMode = j.DarkMode;
                    if (j.SkipNames != null && j.SkipNames.Count > 0) _s.SkipNames = j.SkipNames;
                    _s.MaxChunksPerFile = j.MaxChunksPerFile;
                    if (!string.IsNullOrWhiteSpace(j.CustomInstructions)) _s.CustomInstructions = j.CustomInstructions;
                    _s.Save();
                }
                // الذاكرة: دمج مع الموجود حالياً (مفيش حاجة بتتمسح)
                var bm = Path.Combine(_dir, "Data", "memory.txt");
                if (File.Exists(bm))
                {
                    var merged = Memory.Load();
                    merged.AddRange(File.ReadAllLines(bm));
                    Memory.SaveAll(merged);
                }
            }
            Bar.Value = 100;
            Status.Text = "تم ✔  لو النماذج ماظهرتش في Ollama، اقفله وافتحه تاني.";
        }
        catch (OperationCanceledException) { Status.Text = "اتوقف الاسترجاع."; }
        catch (Exception ex) { Status.Text = "خطأ: " + ex.Message; }
        finally { End(); }
    }
}
