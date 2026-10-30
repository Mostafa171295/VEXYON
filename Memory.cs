using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace VEXYON;

/// <summary>ذاكرة طويلة المدى: حقائق عن المستخدم بتتحفظ محلي في %AppData%\VEXYON\memory.txt (سطر لكل معلومة).</summary>
public static class Memory
{
    public const int MaxItems = 50, MaxLen = 200;
    static readonly object L = new();
    static string FilePath => Path.Combine(Settings.DataDir, "memory.txt");

    public static List<string> Load()
    {
        lock (L)
        {
            try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new(); }
            catch { return new(); }
        }
    }

    public static void SaveAll(IEnumerable<string> items)
    {
        lock (L)
        {
            Directory.CreateDirectory(Settings.DataDir);
            var list = items.Select(x => Norm(x)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxItems);
            File.WriteAllLines(FilePath, list);
        }
    }

    static string Norm(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > MaxLen ? s[..MaxLen] : s;
    }

    public static string Text() => string.Join("\n", Load());

    public static string Add(string fact)
    {
        fact = Norm(fact);
        if (fact.Length < 3) return "المعلومة فاضية.";
        var l = Load();
        if (l.Any(x => x.Equals(fact, StringComparison.OrdinalIgnoreCase))) return "المعلومة دي محفوظة قبل كده.";
        if (l.Count >= MaxItems) return $"الذاكرة مليانة ({MaxItems}). المستخدم لازم يمسح حاجة من الإعدادات.";
        l.Add(fact); SaveAll(l);
        return "اتحفظت في الذاكرة: " + fact;
    }

    public static string Forget(string text)
    {
        text = Norm(text);
        if (text.Length < 2) return "حدد إيه اللي أنسيه.";
        var l = Load();
        var keep = l.Where(x => x.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        int n = l.Count - keep.Count;
        if (n == 0) return "ملقتش معلومة بالشكل ده في الذاكرة.";
        SaveAll(keep);
        return $"اتمسح {n} معلومة من الذاكرة.";
    }

    /// <summary>النص اللي بيتضاف لـ system prompt.</summary>
    public static string ForPrompt()
    {
        var l = Load();
        if (l.Count == 0) return "";
        var s = string.Join("; ", l);
        return s.Length > 1800 ? s[..1800] : s;
    }
}
