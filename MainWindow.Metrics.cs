using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VEXYON;

/// <summary>Live hardware indicator in the header (CPU, RAM, GPU, CPU temperature) + system info for the About tab.</summary>
public partial class MainWindow
{
    sealed class Chip
    {
        public Border Box = null!;
        public TextBlock Val = null!;
        public Border Fill = null!;
        public ToolTip Tip = null!;
        // ---- history for the click-to-open chart (last minute) ----
        public string Title = "";
        public string Unit = "%";
        public double Min = 0, Max = 100;
        public double[] Guides = { 70, 90 };
        public System.Collections.Generic.List<double> Hist = new();
        public System.Collections.Generic.List<bool> BusyHist = new(); // was the model answering at that moment?
    }

    const int HistMax = 40;           // 40 samples x 1.5 s = last minute
    const double ChartW = 260, ChartH = 84;
    Popup? _chartPop;
    Chip? _chartChip;
    Canvas? _chartCanvas;
    TextBlock? _chartTitle, _chartInfo;
    long _chartClosedTick;

    Chip _chCpu = null!, _chRam = null!, _chGpu = null!, _chTemp = null!;
    SysMonitor? _mon;

    Chip MakeChip(string label)
    {
        var c = new Chip();

        var lab = new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        lab.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");

        c.Val = new TextBlock
        {
            Text = "—", FontSize = 13, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Bahnschrift, Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        c.Val.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(c.Val, 1);
        top.Children.Add(lab);
        top.Children.Add(c.Val);

        var track = new Border { CornerRadius = new CornerRadius(1.5) };
        track.SetResourceReference(Border.BackgroundProperty, "Track");
        c.Fill = new Border
        {
            CornerRadius = new CornerRadius(1.5),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(0, 1)
        };
        c.Fill.SetResourceReference(Border.BackgroundProperty, "Disabled");
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(track);
        bar.Children.Add(c.Fill);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bar);

        c.Tip = new ToolTip { FlowDirection = FlowDirection.RightToLeft, Content = "..." };
        c.Box = new Border
        {
            Width = 104,
            Padding = new Thickness(9, 5, 9, 6),
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            FlowDirection = FlowDirection.LeftToRight,
            Child = stack,
            ToolTip = c.Tip
        };
        c.Box.SetResourceReference(Border.BackgroundProperty, "Surface");
        c.Box.SetResourceReference(Border.BorderBrushProperty, "Line");
        ToolTipService.SetInitialShowDelay(c.Box, 150);
        ToolTipService.SetShowDuration(c.Box, 20000);
        c.Box.Cursor = Cursors.Hand;
        c.Box.MouseLeftButtonUp += (_, _) => ShowChart(c);
        return c;
    }

    void BuildMetrics()
    {
        MetricsPanel.Children.Clear();
        _chCpu = MakeChip("CPU");
        _chRam = MakeChip("RAM");
        _chGpu = MakeChip("GPU");
        _chTemp = MakeChip("CPU °C");
        _chCpu.Title = "CPU";
        _chRam.Title = "RAM";
        _chGpu.Title = "GPU";
        _chTemp.Title = "حرارة المعالج"; _chTemp.Unit = "°C"; _chTemp.Min = 20; _chTemp.Max = 100; _chTemp.Guides = new double[] { 70, 85 };
        foreach (var c in new[] { _chCpu, _chRam, _chGpu, _chTemp }) MetricsPanel.Children.Add(c.Box);
    }

    void StartMonitor()
    {
        try
        {
            _mon = new SysMonitor();
            _mon.Updated += s => Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(s)));
            _mon.Start();
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _mon?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    static string LoadKey(double pct) => pct >= 90 ? "Err" : pct >= 70 ? "Warn" : "Ok";

    static void SetChip(Chip c, string text, double frac, string key, string tip)
    {
        c.Val.Text = text;
        if (c.Fill.RenderTransform is ScaleTransform st) st.ScaleX = Math.Max(0, Math.Min(1, frac));
        c.Fill.SetResourceReference(Border.BackgroundProperty, key);
        c.Tip.Content = tip + "\n— دوس عليها لرسم آخر دقيقة";
    }

    void ApplySnapshot(SysSnapshot s)
    {
        Push(_chCpu, s.CpuPct);
        Push(_chRam, s.RamPct);
        Push(_chGpu, s.GpuPct ?? double.NaN);
        Push(_chTemp, s.CpuTemp ?? double.NaN);
        if (_chartPop?.IsOpen == true) DrawChart();
        CheckAlerts(s);

        // CPU
        SetChip(_chCpu, $"{s.CpuPct:0}%", s.CpuPct / 100.0, LoadKey(s.CpuPct),
            $"المعالج: {s.CpuName}\nالاستخدام الحالي: {s.CpuPct:0}%\nعدد الأنوية المنطقية: {Environment.ProcessorCount}");

        // RAM
        string ramTip = $"الرام: {s.RamUsedGb:0.0} من {s.RamTotalGb:0.0} جيجا ({s.RamPct:0}%)\nVEXYON: {s.VexyonMb:0} ميجا";
        if (s.OllamaMb > 1) ramTip += $"\nOllama (النموذج): {s.OllamaMb / 1024.0:0.0} جيجا";
        SetChip(_chRam, $"{s.RamPct:0}%", s.RamPct / 100.0, LoadKey(s.RamPct), ramTip);

        // GPU
        if (s.GpuPct is double g)
        {
            string tip = "استخدام كارت الشاشة (الأعلى بين الكروت)";
            if (s.GpuDetail.Length > 0) tip += "\n" + s.GpuDetail;
            SetChip(_chGpu, $"{g:0}%", g / 100.0, LoadKey(g), tip);
        }
        else
        {
            SetChip(_chGpu, "—", 0, "Disabled", "قراءة استخدام كارت الشاشة مش متاحة على الجهاز ده (محتاج ويندوز 10 أو أحدث وتعريف كارت حديث).");
        }

        // CPU temperature
        if (s.CpuTemp is double t)
        {
            string key = t >= 85 ? "Err" : t >= 70 ? "Warn" : "Ok";
            SetChip(_chTemp, $"{t:0}°C", (t - 20) / 80.0, key,
                $"درجة حرارة المعالج: {t:0}°C\nالمصدر: حساس ACPI في ويندوز (قراءة تقريبية)\nأخضر أقل من 70° | أصفر 70 لـ 85° | أحمر أكتر من 85°");
        }
        else
        {
            SetChip(_chTemp, "—", 0, "Disabled",
                "درجة حرارة المعالج مش متاحة من ويندوز على جهازك.\nكتير من اللابتوبات مش بتعرضها من غير برنامج مراقبة بتعريف خاص.");
        }
    }

    // =====================================================================
    //  1) رسم آخر دقيقة: دوس على CPU / RAM / GPU / الحرارة
    // =====================================================================
    void Push(Chip c, double v)
    {
        c.Hist.Add(v);
        c.BusyHist.Add(_busy);
        if (c.Hist.Count > HistMax) { c.Hist.RemoveAt(0); c.BusyHist.RemoveAt(0); }
    }

    void EnsureChartPopup()
    {
        if (_chartPop != null) return;

        _chartTitle = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13, FlowDirection = FlowDirection.RightToLeft };
        _chartTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        _chartCanvas = new Canvas { Width = ChartW, Height = ChartH, ClipToBounds = true };
        var plot = new Border
        {
            Child = _chartCanvas, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left
        };
        plot.SetResourceReference(Border.BackgroundProperty, "ReadOnlyBg");
        plot.SetResourceReference(Border.BorderBrushProperty, "Line");

        _chartInfo = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = ChartW, FlowDirection = FlowDirection.RightToLeft };
        _chartInfo.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        var legend = new TextBlock
        {
            Text = "المنطقة المظللة = الوقت اللي النموذج كان بيرد فيه", FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap, MaxWidth = ChartW, FlowDirection = FlowDirection.RightToLeft
        };
        legend.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");

        var stack = new StackPanel();
        stack.Children.Add(_chartTitle);
        stack.Children.Add(plot);
        stack.Children.Add(_chartInfo);
        stack.Children.Add(legend);

        var card = new Border
        {
            Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5), FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 6, 0, 0)
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Blue");

        _chartPop = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, Child = card };
        _chartPop.Closed += (_, _) => _chartClosedTick = Environment.TickCount64;
    }

    void ShowChart(Chip c)
    {
        // دوسة تانية على نفس الخانة وهي مفتوحة = قفل (الـ Popup بيتقفل لوحده أول ما الماوس يتضغط بره)
        if (ReferenceEquals(_chartChip, c) && Environment.TickCount64 - _chartClosedTick < 300) { _chartChip = null; return; }

        EnsureChartPopup();
        _chartChip = c;
        _chartPop!.PlacementTarget = c.Box;
        _chartTitle!.Text = "آخر دقيقة — " + c.Title;
        DrawChart();
        _chartPop.IsOpen = true;
    }

    void DrawChart()
    {
        if (_chartChip == null || _chartCanvas == null || _chartInfo == null) return;
        var c = _chartChip;
        var cv = _chartCanvas;
        cv.Children.Clear();

        double W = ChartW, H = ChartH;
        int n = c.Hist.Count;
        double step = W / (HistMax - 1);
        double X(int i) => W - (n - 1 - i) * step;                // أحدث قراءة على اليمين
        double Y(double v) => H - 2 - Math.Max(0, Math.Min(1, (v - c.Min) / (c.Max - c.Min))) * (H - 4);

        // 1) مناطق "النموذج بيرد"
        var warn = TryFindResource("Warn") as Brush ?? Brushes.Orange;
        int i = 0;
        while (i < n)
        {
            if (!c.BusyHist[i]) { i++; continue; }
            int j = i;
            while (j + 1 < n && c.BusyHist[j + 1]) j++;
            double x0 = Math.Max(0, X(i) - step / 2), x1 = Math.Min(W, X(j) + step / 2);
            var band = new Rectangle { Width = Math.Max(1, x1 - x0), Height = H, Fill = warn, Opacity = 0.18 };
            Canvas.SetLeft(band, x0);
            cv.Children.Add(band);
            i = j + 1;
        }

        // 2) خطوط الحدود (70/90 للنسب، 70/85 للحرارة)
        foreach (var g in c.Guides)
        {
            double y = Y(g);
            var ln = new Line { X1 = 0, X2 = W, Y1 = y, Y2 = y, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
            ln.SetResourceReference(Shape.StrokeProperty, "Disabled");
            cv.Children.Add(ln);
        }

        // 3) الخط نفسه
        var pts = new PointCollection();
        for (int k = 0; k < n; k++)
            if (!double.IsNaN(c.Hist[k])) pts.Add(new Point(X(k), Y(c.Hist[k])));

        var vals = c.Hist.Where(v => !double.IsNaN(v)).ToList();
        if (pts.Count >= 2)
        {
            var area = new PointCollection(pts);
            area.Add(new Point(pts[pts.Count - 1].X, H));
            area.Add(new Point(pts[0].X, H));
            var fill = new Polygon { Points = area, Opacity = 0.14 };
            fill.SetResourceReference(Shape.FillProperty, "Blue");
            cv.Children.Add(fill);

            var line = new Polyline { Points = pts, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
            line.SetResourceReference(Shape.StrokeProperty, "Blue");
            cv.Children.Add(line);
        }
        else if (vals.Count == 0)
        {
            var none = new TextBlock { Text = "مفيش قراءة متاحة", FontSize = 12, FlowDirection = FlowDirection.RightToLeft };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            Canvas.SetLeft(none, 12); Canvas.SetTop(none, H / 2 - 9);
            cv.Children.Add(none);
        }

        // 4) ملخص
        if (vals.Count == 0) { _chartInfo.Text = "القراءة دي مش متاحة على جهازك."; return; }
        string u = c.Unit;
        string info = $"الحالي {vals[vals.Count - 1]:0}{u}   |   المتوسط {vals.Average():0}{u}   |   الأعلى {vals.Max():0}{u}";

        var busyVals = new System.Collections.Generic.List<double>();
        var idleVals = new System.Collections.Generic.List<double>();
        for (int k = 0; k < n; k++)
        {
            if (double.IsNaN(c.Hist[k])) continue;
            (c.BusyHist[k] ? busyVals : idleVals).Add(c.Hist[k]);
        }
        if (busyVals.Count > 0 && idleVals.Count > 0)
            info += $"\nوقت رد النموذج: متوسط {busyVals.Average():0}{u}  (وقت الراحة: {idleVals.Average():0}{u})";
        else if (busyVals.Count > 0)
            info += $"\nوقت رد النموذج: متوسط {busyVals.Average():0}{u}";
        _chartInfo.Text = info;
    }

    // =====================================================================
    //  2) تنبيه لما الجهاز يتقل (رام عالية أو حرارة عالية)
    // =====================================================================
    const double RamHigh = 90, RamBack = 85;     // يتنبّه عند 90% ويتصفّر تحت 85%
    const double TempHigh = 85, TempBack = 80;   // نفس الفكرة للحرارة
    const int NeedSamples = 3;                   // 3 قراءات ورا بعض (~4.5 ثانية) عشان مايزعقش على لحظة عابرة
    static readonly TimeSpan ToastCooldown = TimeSpan.FromMinutes(3);

    int _ramHi, _tempHi;
    DateTime _ramToastAt = DateTime.MinValue, _tempToastAt = DateTime.MinValue;
    DispatcherTimer? _toastTimer;

    void CheckAlerts(SysSnapshot s)
    {
        if (s.RamPct >= RamHigh) _ramHi++; else if (s.RamPct < RamBack) _ramHi = 0;
        double t = s.CpuTemp ?? 0;
        if (t >= TempHigh) _tempHi++; else if (t < TempBack) _tempHi = 0;

        var now = DateTime.Now;
        bool ram = _ramHi >= NeedSamples && now - _ramToastAt > ToastCooldown;
        bool hot = _tempHi >= NeedSamples && now - _tempToastAt > ToastCooldown;
        if (!ram && !hot) return;

        var parts = new System.Collections.Generic.List<string>();
        if (ram)
        {
            _ramToastAt = now;
            string p = $"الرام وصلت {s.RamPct:0}% ({s.RamUsedGb:0.0} من {s.RamTotalGb:0.0} جيجا).";
            if (s.OllamaMb > 1024) p += $" النموذج واخد منها {s.OllamaMb / 1024.0:0.0} جيجا.";
            parts.Add(p + " اقفل البرامج التقيلة (المتصفح بتابات كتير مثلاً) أو غيّر لنموذج أخف.");
        }
        if (hot)
        {
            _tempToastAt = now;
            parts.Add($"حرارة المعالج {t:0}°م وده عالي. سيب الجهاز يهدى شوية، وتأكد إن فتحات التهوية مش مسدودة، أو غيّر لنموذج أخف.");
        }
        ShowToast(string.Join("\n", parts), hot);
    }

    void ShowToast(string text, bool severe)
    {
        ToastText.Text = text;
        LoadToast.SetResourceReference(Border.BorderBrushProperty, severe ? "Err" : "Warn");
        LoadToast.Visibility = Visibility.Visible;

        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _toastTimer.Tick += (_, _) => { _toastTimer!.Stop(); LoadToast.Visibility = Visibility.Collapsed; };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        LoadToast.Visibility = Visibility.Collapsed;
    }

    void ToastModel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            // تغيير النموذج مش متاح وهو بيرد
            ToastText.Text = "استنى لحد ما الرد يخلص، وبعدين دوس على اسم النموذج في الشريط اللي تحت واختار نموذج أخف.";
            _toastTimer?.Stop(); _toastTimer?.Start();
            return;
        }
        ToastClose_Click(sender, e);
        ModelChip_Click(this, null!);
    }

    string BuildAboutSys()
    {
        var gpus = SysMonitor.GpuNames();
        var lines = new System.Collections.Generic.List<string>
        {
            $"المعالج: {SysMonitor.CpuName}  ({Environment.ProcessorCount} نواة منطقية)",
            $"الرام: {SysMonitor.TotalRamGb():0.0} جيجا",
            "كارت الشاشة: " + (gpus.Count > 0 ? string.Join("  |  ", gpus) : "غير معروف"),
            "النظام: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ".NET: " + Environment.Version,
            "فولدر البيانات: " + Settings.DataDir
        };
        return string.Join("\n", lines);
    }
}
