using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VEXYON;

/// <summary>
/// سرعة رد النموذج: بعد كل رد بيظهر في الشريط السفلي كام كلمة/توكن في الثانية،
/// ومتوسط كل نموذج بيتحفظ عشان تقارن بينهم على جهازك.
/// الأرقام (عدد التوكنز وزمن التوليد) جاية من Ollama نفسه، فمش تقدير.
/// </summary>
public partial class MainWindow
{
    readonly object _speedLock = new();
    int _replyTokens;
    double _replySecs;

    void InitSpeed()
    {
        _ol.Generated += (_, tokens, secs) =>
        {
            lock (_speedLock) { _replyTokens += tokens; _replySecs += secs; }
        };
    }

    void ResetSpeed()
    {
        lock (_speedLock) { _replyTokens = 0; _replySecs = 0; }
    }

    static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"[\p{L}\p{N}]+").Count;

    /// <summary>بيتنادى بعد ما الرد يخلص.</summary>
    void ShowSpeed(string? answer)
    {
        int tok; double secs;
        lock (_speedLock) { tok = _replyTokens; secs = _replySecs; }
        if (tok < 5 || secs < 0.05) return;

        double tps = tok / secs;
        double wps = CountWords(answer) / secs;
        string model = _s.ChatModel;

        // متوسط متحرك لكل نموذج (الردود القصيرة جداً مابتأثرش في المقارنة)
        if (tok >= 20)
        {
            _s.ModelSpeed[model] = _s.ModelSpeed.TryGetValue(model, out var prev) && prev > 0
                ? prev * 0.7 + tps * 0.3
                : tps;
            try { _s.Save(); } catch { }
        }

        SpeedText.Text = wps >= 0.5
            ? $"⚡ {wps:0.0} كلمة/ث  ·  {tps:0.0} توكن/ث"
            : $"⚡ {tps:0.0} توكن/ث";
        RefreshSpeedTip(model, tok, secs);
    }

    /// <summary>لما النموذج يتغيّر: نعرض متوسطه المحفوظ (لو معروف) بدل رقم نموذج تاني.</summary>
    void RefreshSpeedLabel()
    {
        if (_s.ModelSpeed.TryGetValue(_s.ChatModel, out var avg) && avg > 0)
        {
            SpeedText.Text = $"⚡ متوسط ~{avg:0.0} توكن/ث";
            RefreshSpeedTip(_s.ChatModel, 0, 0);
        }
        else
        {
            SpeedText.Text = "";
        }
    }

    void RefreshSpeedTip(string model, int tok, double secs)
    {
        var lines = new System.Collections.Generic.List<string>();
        if (tok > 0) lines.Add($"آخر رد ({model}): {tok} توكن في {secs:0.0} ثانية");
        lines.Add("الكلمات محسوبة من الرد الظاهر، والتوكنز من Ollama مباشرة.");
        lines.Add("التوكن أصغر من الكلمة (العربي غالباً توكن أو اتنين للكلمة).");
        if (_s.ModelSpeed.Count > 0)
        {
            lines.Add("");
            lines.Add("مقارنة النماذج على جهازك (متوسط توكن/ث):");
            foreach (var kv in _s.ModelSpeed.OrderByDescending(k => k.Value))
                lines.Add($"{(kv.Key.Equals(_s.ChatModel, StringComparison.OrdinalIgnoreCase) ? "✔ " : "   ")}{kv.Key}  —  {kv.Value:0.0}");
        }
        SpeedText.ToolTip = new System.Windows.Controls.ToolTip
        {
            FlowDirection = System.Windows.FlowDirection.RightToLeft,
            Content = string.Join("\n", lines)
        };
    }
}
