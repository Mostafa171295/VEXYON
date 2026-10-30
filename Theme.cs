using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
namespace VEXYON;

/// <summary>Dark / light themes. Replaces the brush resources at runtime (all XAML uses DynamicResource).</summary>
public static class Theme
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static readonly string[] Keys =
        { "Blue", "DeepBlue", "Ink", "SubInk", "SoftBlue", "Line", "Surface", "SurfaceHover", "HeaderBg",
          "Ok", "Warn", "Err", "Disabled", "ReadOnlyBg", "Track" };

    static readonly string[] Dark =
        { "#A18BFF", "#4A2BD9", "#F8F8FF", "#CDCFF2", "#1A1840", "#2E2B66", "#0E0E26", "#16163A", "#07071A",
          "#4ADE80", "#FBBF24", "#F87171", "#3A3A66", "#0A0A1D", "#1B1B3D" };

    static readonly string[] Light =
        { "#5B3DF5", "#3A22B8", "#17163A", "#52507F", "#EFEBFF", "#D3CCF7", "#FFFFFF", "#F1EEFF", "#FFFFFF",
          "#15803D", "#B45309", "#B91C1C", "#CFCBE8", "#F7F6FF", "#E6E1FF" };

    static LinearGradientBrush Grad(Point a, Point b, params (string c, double o)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        foreach (var (c, o) in stops) g.GradientStops.Add(new GradientStop(C(c), o));
        g.Freeze();
        return g;
    }

    static DropShadowEffect Glow(string color, double blur, double opacity)
    {
        var e = new DropShadowEffect { Color = C(color), BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
        e.Freeze();
        return e;
    }

    public static void Apply(bool dark)
    {
        var r = Application.Current.Resources;
        var v = dark ? Dark : Light;
        for (int i = 0; i < Keys.Length; i++)
        {
            var b = new SolidColorBrush(C(v[i]));
            b.Freeze();
            r[Keys[i]] = b;
        }

        if (dark)
        {
            r["BlueGrad"] = Grad(new Point(0, 0), new Point(1, 1), ("#A58BFF", 0), ("#6D4BFF", 0.55), ("#4A2BD9", 1));
            r["WinBg"] = Grad(new Point(0, 0), new Point(0, 1), ("#0B0A24", 0), ("#05050F", 0.55));
            r["GlowLine"] = Grad(new Point(0, 0), new Point(1, 0), ("#006D4BFF", 0), ("#8B6BFF", 0.5), ("#006D4BFF", 1));
            r["TitleGrad"] = Grad(new Point(0, 0), new Point(1, 0), ("#F4F4FF", 0), ("#B9B2FF", 0.6), ("#8B6BFF", 1));
            r["GlowSoft"] = Glow("#7C5CFF", 16, 0.85);
            r["GlowStrong"] = Glow("#7C5CFF", 26, 1);
            r["GlowText"] = Glow("#8B6BFF", 14, 0.65);
        }
        else
        {
            r["BlueGrad"] = Grad(new Point(0, 0), new Point(1, 1), ("#8B6BFF", 0), ("#5B3DF5", 0.55), ("#3A22B8", 1));
            r["WinBg"] = Grad(new Point(0, 0), new Point(0, 1), ("#F3F0FF", 0), ("#FFFFFF", 0.5));
            r["GlowLine"] = Grad(new Point(0, 0), new Point(1, 0), ("#005B3DF5", 0), ("#7C5CFF", 0.5), ("#005B3DF5", 1));
            r["TitleGrad"] = Grad(new Point(0, 0), new Point(1, 0), ("#1E1B4B", 0), ("#4C2FD6", 0.7), ("#7C5CFF", 1));
            r["GlowSoft"] = Glow("#7C5CFF", 14, 0.40);
            r["GlowStrong"] = Glow("#7C5CFF", 22, 0.55);
            r["GlowText"] = Glow("#7C5CFF", 10, 0.0);
        }
    }
}
