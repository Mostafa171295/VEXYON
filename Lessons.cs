using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace VEXYON;

/// <summary>
/// ذاكرة الدروس: قواعد قصيرة عامة المساعد اتعلمها من أخطاء حصلت (خطأ في كود، أو تصحيح من المستخدم).
/// النموذج نفسه مابيتغيّرش؛ الدروس بتتضاف لتعليماته في كل محادثة. محلية في %AppData%\VEXYON\lessons.txt (سطر لكل درس).
/// </summary>
public static class Lessons
{
    public const int MaxItems = 20, MaxLen = 250, MaxPromptChars = 1500;
    static readonly object L = new();
    static string FilePath => Path.Combine(Settings.DataDir, "lessons.txt");

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

    public static string Add(string lesson)
    {
        lesson = Norm(lesson);
        if (lesson.Length < 10) return "الدرس قصير أوي. اكتب قاعدة عامة واضحة.";
        var l = Load();
        if (l.Any(x => x.Equals(lesson, StringComparison.OrdinalIgnoreCase))) return "الدرس ده محفوظ قبل كده.";
        // لما الدروس تمتلي بنشيل الأقدم بدل ما نرفض (الأحدث أهم)
        while (l.Count >= MaxItems) l.RemoveAt(0);
        l.Add(lesson); SaveAll(l);
        return "اتسجّل درس: " + lesson;
    }

    /// <summary>بتمسح كل الدروس اللي فيها النص ده وبترجّع العدد.</summary>
    public static int Forget(string text)
    {
        text = Norm(text);
        if (text.Length < 2) return 0;
        var l = Load();
        var keep = l.Where(x => x.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        int n = l.Count - keep.Count;
        if (n > 0) SaveAll(keep);
        return n;
    }

    /// <summary>النص اللي بيتضاف لـ system prompt (الأحدث الأول لو اتقطع).</summary>
    public static string ForPrompt()
    {
        var l = Load();
        if (l.Count == 0) return "";
        l.Reverse();
        var s = string.Join("; ", l);
        return s.Length > MaxPromptChars ? s[..MaxPromptChars] : s;
    }
}
