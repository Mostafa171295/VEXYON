using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace VEXYON;

public class Settings
{
    static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string WatchFolder { get; set; } = Path.Combine(Docs, "MyBrain");
    public string WorkFolder { get; set; } = Path.Combine(Docs, "MyBrain", "Work");
    public string ChatModel { get; set; } = "qwen3:4b";
    public string EmbedModel { get; set; } = "bge-m3";
    public bool AutoApprove { get; set; } = false;
    public bool DarkMode { get; set; } = true;

    /// <summary>كل الفولدرات/الدرايفات اللي بتتفهرس (ممكن درايف كامل).</summary>
    public List<string> IndexFolders { get; set; } = new();
    /// <summary>أسماء فولدرات بتتخطى في أي مكان (bin, node_modules, Windows ...).</summary>
    public List<string> SkipNames { get; set; } = DefaultSkipNames();
    /// <summary>أقصى عدد أجزاء بتتفهرس من الملف الواحد (حماية من اللوجات الضخمة).</summary>
    public int MaxChunksPerFile { get; set; } = 1000;
    /// <summary>رقم نسخة الفهرس: لو أقل من المطلوب بنعيد الفهرسة مرة واحدة.</summary>
    public int IndexRev { get; set; } = 0;
    /// <summary>تعليمات دائمة بتتضاف لكل محادثة (أسلوب الرد، مجالك...).</summary>
    public string CustomInstructions { get; set; } = "";
    /// <summary>بحث الإنترنت (اختياري). مقفول افتراضياً لأن البرنامج أوفلاين.</summary>
    public bool WebSearch { get; set; } = false;
    /// <summary>الذاكرة طويلة المدى (بتتخزن محلي بس).</summary>
    public bool MemoryOn { get; set; } = true;
    /// <summary>حجم سياق النموذج بالتوكن (أكبر = بيفتكر أكتر بس بياكل رام أكتر).</summary>
    public int NumCtx { get; set; } = 16384;
    /// <summary>وضع التفكير العميق (للنماذج اللي بتدعمه زي qwen3). مقفول = أسرع.</summary>
    public bool DeepThink { get; set; } = false;
    /// <summary>متوسط سرعة كل نموذج على جهازك (توكن في الثانية) - بيتحدّث بعد كل رد.</summary>
    public Dictionary<string, double> ModelSpeed { get; set; } = new();

    public static List<string> DefaultSkipNames() => new()
    {
        "bin", "obj", "node_modules", ".git", ".svn", ".vs", ".idea", ".vscode", "__pycache__",
        "venv", ".venv", "site-packages", ".nuget", ".gradle", "dist",
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", "AppData", "Recovery", "Config.Msi", "MSOCache", "$WinREAgent", "WindowsApps"
    };

    public void Normalize()
    {
        IndexFolders ??= new List<string>();
        SkipNames ??= DefaultSkipNames();
        IndexFolders = IndexFolders.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (IndexFolders.Count == 0 && !string.IsNullOrWhiteSpace(WatchFolder)) IndexFolders.Add(WatchFolder);
        CustomInstructions ??= "";
        ModelSpeed ??= new Dictionary<string, double>();
        // v10: qwen2.5:3b تحت ترخيص بحثي غير تجاري (Qwen Research License)، فبنحوّله لنموذج Apache 2.0 المسموح تجارياً
        if (string.Equals(ChatModel, "qwen2.5:3b", StringComparison.OrdinalIgnoreCase)) ChatModel = "qwen3:4b";
        NumCtx = Math.Clamp(NumCtx, 4096, 65536);
        // فولدر النسخ الاحتياطي بتاع تعديل الملفات ما يتفهرسش
        if (!SkipNames.Contains(".backup", StringComparer.OrdinalIgnoreCase)) SkipNames.Add(".backup");
        if (!SkipNames.Contains(".scripts", StringComparer.OrdinalIgnoreCase)) SkipNames.Add(".scripts");
        if (MaxChunksPerFile < 20 || MaxChunksPerFile == 300) MaxChunksPerFile = 1000;
    }

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VEXYON");
    static string File_ => Path.Combine(DataDir, "settings.json");

    public static Settings Load()
    {
        Settings st;
        try
        {
            st = File.Exists(File_)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new Settings()
                : new Settings();
        }
        catch { st = new Settings(); }
        st.Normalize();
        return st;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
