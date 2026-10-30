using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace VEXYON;

public partial class MainWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly OllamaClient _ol = new();
    Db _db = null!; Search _search = null!; Indexer _indexer = null!; Agent _agent = null!;
    CancellationTokenSource? _scanCts;
    bool _busy;
    Conversation _conv = new();

    public MainWindow()
    {
        InitializeComponent();
        BuildNav();
        BuildMetrics();
        StartMonitor();
        InitSpeed();
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) UpdateNav(); };
        ApplyTheme();
        Loaded += async (_, _) => await Startup();
    }

    void ApplyTheme()
    {
        Theme.Apply(_s.DarkMode);
        ThemeBtn.Content = _s.DarkMode ? "☀" : "🌙";
    }

    void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        _s.DarkMode = !_s.DarkMode;
        ApplyTheme();
        try { _s.Save(); } catch { }
        if (_db != null) { BuildToolsTab(); RefreshConvList(); _ = RefreshOllamaStatus(); }
    }

    async Task Startup()
    {
        Directory.CreateDirectory(_s.WatchFolder);
        Directory.CreateDirectory(_s.WorkFolder);
        _db = new Db(Path.Combine(Settings.DataDir, "brain.db"));
        if (_s.IndexRev < 2)
        {
            // حد الأجزاء اتغيّر: نعيد الفهرسة مرة واحدة عشان الملفات الكبيرة تتفهرس كاملة
            try { _db.Clear(); } catch { }
            _s.IndexRev = 2; _s.Save();
        }
        _search = new Search(_db, _ol, _s);
        _agent = new Agent(_ol, _s, new Tools(_s, _search, Approve, Audit));
        _indexer = new Indexer(_db, _ol, _s);
        _indexer.Log += m => Dispatcher.Invoke(() => Log(m));
        _indexer.Progress += (i, n, name) => Dispatcher.Invoke(() =>
        {
            IndexBar.Maximum = n; IndexBar.Value = i; IndexStatus.Text = $"{i}/{n}  {name}";
        });

        BuildFolders();
        SkipBox.Text = string.Join(", ", _s.SkipNames);
        WorkBox.Text = _s.WorkFolder;
        ChatModelBox.Text = _s.ChatModel;
        InstructionsBox.Text = _s.CustomInstructions;
        EmbedModelBox.Text = _s.EmbedModel;
        AutoApproveBox.IsChecked = _s.AutoApprove;
        WebToggle.IsChecked = _s.WebSearch; DeepToggle.IsChecked = _s.DeepThink; NumCtxBox.Text = _s.NumCtx.ToString();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersion.Text = "v" + (ver?.ToString(3) ?? "1.0.0");
        AboutSys.Text = BuildAboutSys();
        MemoryOnBox.IsChecked = _s.MemoryOn; MemoryBox.Text = Memory.Text(); LessonsBox.Text = Lessons.Text();

        ShowWelcome();
        RefreshConvList();
        BuildToolsTab();
        _ = DetectToolsAsync();
        await RefreshOllamaStatus();
        Tabs.SelectedItem = _ready ? TabChat : TabGuide;
        _guideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _guideTimer.Tick += async (_, _) => { if (Tabs.SelectedItem == TabGuide) await RefreshGuide(); };
        _guideTimer.Start();
        _indexer.StartWatching();
        RefreshStats();
        _ = RunScan();
    }

    // ---------- helpers ----------
    void Log(string m)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {m}\n");
        LogBox.ScrollToEnd();
        StatusText.Text = m;
    }

    void Audit(string m)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(Path.Combine(Settings.DataDir, "audit.log"), $"{DateTime.Now:s}  {m}\n");
        }
        catch { }
    }


    static bool HasArabic(string t) => t.Any(ch => ch >= '\u0600' && ch <= '\u06FF');

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // ---------- chat rendering ----------
    FrameworkElement? _thinking;
    StackPanel? _stepsPanel; TextBlock? _stepsHeader; int _stepsCount;
    bool _welcome;

    static IEnumerable<(bool code, string text)> SplitCode(string t)
    {
        var parts = t.Split("```");
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (i % 2 == 1)
            {
                int nl = p.IndexOf('\n');
                if (nl >= 0 && nl < 20 && !p[..nl].Trim().Contains(' ')) p = p[(nl + 1)..];
                yield return (true, p.Trim('\r', '\n'));
            }
            else yield return (false, p.Trim());
        }
    }

    TextBlock LinkButton(string label, Action act)
    {
        var tb = new TextBlock { Text = label, FontSize = 12, Cursor = Cursors.Hand, Margin = new Thickness(2, 4, 2, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Blue");
        tb.MouseLeftButtonUp += (_, _) =>
        {
            try { act(); } catch { return; }
            var old = tb.Text; tb.Text = "تم ✔";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            t.Tick += (_, _) => { tb.Text = old; t.Stop(); };
            t.Start();
        };
        return tb;
    }

    TextBox MakeText(string text, bool user)
    {
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4),
            FlowDirection = HasArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        if (user) tb.Foreground = Brushes.White; else tb.SetResourceReference(TextBox.ForegroundProperty, "Ink");
        return tb;
    }

    FrameworkElement MakeCodeBlock(string code)
    {
        var tb = new TextBox
        {
            Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
            FontSize = 13, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(2)
        };
        tb.SetResourceReference(TextBox.ForegroundProperty, "Ink");
        var head = LinkButton("نسخ الكود", () => Clipboard.SetText(code));
        head.HorizontalAlignment = HorizontalAlignment.Right;
        var sp = new StackPanel();
        sp.Children.Add(head); sp.Children.Add(tb);
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 6), Margin = new Thickness(0, 4, 0, 4), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "ReadOnlyBg");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    void AddBubble(string text, bool user)
    {
        RemoveThinking();
        RemoveLive();
        var content = new StackPanel();
        if (user) content.Children.Add(MakeText(text, true));
        else
        {
            foreach (var (code, seg) in SplitCode(text))
            {
                if (string.IsNullOrWhiteSpace(seg)) continue;
                content.Children.Add(code ? MakeCodeBlock(seg) : MakeText(seg, false));
            }
            var cp = LinkButton("نسخ", () => Clipboard.SetText(text));
            cp.HorizontalAlignment = HorizontalAlignment.Left;
            content.Children.Add(cp);
        }
        var border = new Border
        {
            Child = content, CornerRadius = new CornerRadius(14), Margin = new Thickness(6, 5, 6, 5),
            Padding = new Thickness(10, 6, 10, 6), MaxWidth = 680,
            HorizontalAlignment = user ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            BorderThickness = new Thickness(user ? 0 : 1)
        };
        border.SetResourceReference(Border.BackgroundProperty, user ? "BlueGrad" : "SoftBlue");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        ChatPanel.Children.Add(border);
        ChatScroll.ScrollToEnd();
    }

    void ShowThinking()
    {
        RemoveThinking();
        var tb = new TextBlock { Text = "بيفكر…", FontSize = 13, Margin = new Thickness(14, 4, 14, 8) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(700))
        { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        tb.BeginAnimation(UIElement.OpacityProperty, anim);
        _thinking = tb;
        ChatPanel.Children.Add(tb);
        ChatScroll.ScrollToEnd();
    }

    void RemoveThinking()
    {
        if (_thinking == null) return;
        _thinking.BeginAnimation(UIElement.OpacityProperty, null);
        ChatPanel.Children.Remove(_thinking);
        _thinking = null;
    }

    void ResetSteps() { _stepsPanel = null; _stepsHeader = null; _stepsCount = 0; }

    void UpdateStepsHeader()
    {
        if (_stepsHeader == null || _stepsPanel == null) return;
        bool open = _stepsPanel.Visibility == Visibility.Visible;
        _stepsHeader.Text = (open ? "▾  " : "◂  ") + (_stepsCount == 1 ? "خطوة واحدة" : $"{_stepsCount} خطوات");
    }

    void AddNote(string text)
    {
        if (_stepsPanel == null)
        {
            var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            var header = new TextBlock { Cursor = Cursors.Hand, FontSize = 12, Margin = new Thickness(12, 2, 12, 2) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            header.MouseLeftButtonUp += (_, _) =>
            {
                panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                UpdateStepsHeader();
            };
            _stepsPanel = panel; _stepsHeader = header;
            int idx = _thinking != null ? ChatPanel.Children.IndexOf(_thinking) : ChatPanel.Children.Count;
            if (idx < 0) idx = ChatPanel.Children.Count;
            ChatPanel.Children.Insert(idx, header);
            ChatPanel.Children.Insert(idx + 1, panel);
        }
        _stepsCount++;
        var line = new TextBlock
        {
            Text = text, FontSize = 12, Margin = new Thickness(24, 0, 12, 2),
            TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
        _stepsPanel.Children.Add(line);
        UpdateStepsHeader();
        ChatScroll.ScrollToEnd();
    }

    void ShowWelcome()
    {
        ChatPanel.Children.Clear();
        _welcome = true;
        var title = new TextBlock { Text = "اسألني عن ملفاتك، أو اطلب مني أنفّذ لك مهمة", FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10, 40, 10, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var sub = new TextBlock { Text = "ملفاتك بتتفهرس أوفلاين، وأي أمر بيغيّر حاجة بيطلب موافقتك الأول.", HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 18) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
        var wp = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (label, prompt) in new[]
        {
            ("فين ملف عقد المقاول؟", "فين ملف عقد المقاول؟"),
            ("لخّصلي ملفات الفولدر", "لخّصلي أهم النقاط في الملفات اللي في الفولدر"),
            ("اعرض الملفات", "اعرض الملفات اللي في فولدر المراقبة"),
            ("اكتب سكربت بايثون", "اعمل ملف hello.py يطبع جدول الضرب لرقم 7، وبعدين شغّله وقولي النتيجة.")
        })
        {
            var btn = new Button { Content = label, Tag = prompt, Style = (Style)FindResource("Ghost"), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(4) };
            btn.Click += Suggest_Click;
            wp.Children.Add(btn);
        }
        ChatPanel.Children.Add(title); ChatPanel.Children.Add(sub); ChatPanel.Children.Add(wp);
    }

    async void Suggest_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;
        InputBox.Text = (string)((Button)s).Tag;
        await SendMessage();
    }

    void InputBox_TextChanged(object s, TextChangedEventArgs e) =>
        InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- sidebar navigation ----------
    readonly List<(Border box, TextBlock icon, TextBlock label, TabItem tab)> _nav = new();

    void BuildNav()
    {
        NavPanel.Children.Clear(); _nav.Clear();
        void Add(string glyph, string text, TabItem tab)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon); row.Children.Add(label);
            var box = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            box.MouseLeftButtonUp += (_, _) => Tabs.SelectedItem = tab;
            NavPanel.Children.Add(box);
            _nav.Add((box, icon, label, tab));
        }
        Add("\uE8BD", "المحادثة", TabChat);
        Add("\uE80F", "ابدأ هنا", TabGuide);
        Add("\uE8F1", "المكتبة", TabLib);
        Add("\uE90F", "الأدوات", TabTools);
        Add("\uE713", "الإعدادات", TabSettings);
        Add("\uE81C", "السجل", TabLog);
        Add("\uE946", "عن VEXYON", TabAbout);
        UpdateNav();
    }

    void UpdateNav()
    {
        foreach (var (box, icon, label, tab) in _nav)
        {
            bool sel = Tabs.SelectedItem == tab;
            if (sel) box.SetResourceReference(Border.BackgroundProperty, "SoftBlue");
            else { box.ClearValue(Border.BackgroundProperty); box.Background = Brushes.Transparent; }
            var key = sel ? "Blue" : "SubInk";
            icon.SetResourceReference(TextBlock.ForegroundProperty, key);
            label.SetResourceReference(TextBlock.ForegroundProperty, key);
        }
    }

    // ---------- status bar: model switcher ----------
    List<string> _models = new();

    static bool IsEmbedModel(string n) =>
        n.Contains("embed", StringComparison.OrdinalIgnoreCase) || n.Contains("bge", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("minilm", StringComparison.OrdinalIgnoreCase) || n.Contains("nomic", StringComparison.OrdinalIgnoreCase);

    void UpdateModelChip()
    {
        ModelChipText.Text = _s.ChatModel + "  ▾";
        bool missing = _models.Count > 0 && !OllamaClient.HasModel(_models, _s.ChatModel);
        ModelChipText.SetResourceReference(TextBlock.ForegroundProperty, missing ? "Warn" : "Ink");
        RefreshSpeedLabel();
    }

    void FillInstalledPanel()
    {
        InstalledPanel.Children.Clear();
        foreach (var n in _models.Where(m => !IsEmbedModel(m)))
        {
            var btn = new Button { Content = n, Tag = n, Style = (Style)FindResource("Ghost"), FlowDirection = FlowDirection.LeftToRight };
            btn.Click += Preset_Click;
            InstalledPanel.Children.Add(btn);
        }
        if (InstalledPanel.Children.Count == 0)
        {
            var t = new TextBlock { Text = "(مفيش نماذج منزّلة لسه)" };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            InstalledPanel.Children.Add(t);
        }
    }

    async void ModelChip_Click(object s, MouseButtonEventArgs e)
    {
        if (_busy) return;
        try { if (await _ol.IsUp()) _models = await _ol.ListModels(); } catch { }
        ModelPopupPanel.Children.Clear();
        var list = _models.Where(m => !IsEmbedModel(m)).ToList();
        if (list.Count == 0)
        {
            var t = new TextBlock { Text = "مفيش نماذج منزّلة (أو Ollama مش شغال)", Margin = new Thickness(8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            ModelPopupPanel.Children.Add(t);
        }
        foreach (var n in list)
        {
            var name = n;
            bool cur = name.Equals(_s.ChatModel, StringComparison.OrdinalIgnoreCase);
            var tb = new TextBlock { Text = (cur ? "✔  " : "     ") + name, FlowDirection = FlowDirection.LeftToRight };
            tb.SetResourceReference(TextBlock.ForegroundProperty, cur ? "Blue" : "Ink");
            var item = new Border { Child = tb, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
            item.MouseLeave += (_, _) => { item.ClearValue(Border.BackgroundProperty); item.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (_, _) =>
            {
                ModelPopup.IsOpen = false;
                if (cur || _busy) return;
                _s.ChatModel = name; ChatModelBox.Text = name; _s.Save();
                ReloadAgent(); UpdateModelChip();
                Log("اتغيّر نموذج المحادثة: " + name);
            };
            ModelPopupPanel.Children.Add(item);
        }
        ModelPopup.IsOpen = true;
    }

    // ---------- approval overlay ----------
    TaskCompletionSource<bool>? _approveTcs;

    Task<bool> Approve(string msg)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.Invoke(() =>
        {
            _approveTcs = tcs;
            ApproveText.Text = msg;
            ApproveText.ScrollToHome();
            ApproveOverlay.Visibility = Visibility.Visible;
            ApproveNo.Focus();
        });
        return tcs.Task;
    }

    void ApproveYes_Click(object s, RoutedEventArgs e) { ApproveOverlay.Visibility = Visibility.Collapsed; _approveTcs?.TrySetResult(true); }
    void ApproveNo_Click(object s, RoutedEventArgs e) { ApproveOverlay.Visibility = Visibility.Collapsed; _approveTcs?.TrySetResult(false); }

    void RefreshStats()
    {
        try { var (f, c) = _db.Stats(); StatsText.Text = $"ملفات مفهرسة: {f}   |   قطع نص: {c}"; FileCountText.Text = $"{f:N0} ملف مفهرس"; }
        catch { }
    }

    async Task RefreshOllamaStatus()
    {
        if (!await _ol.IsUp())
        {
            OllamaStatus.Text = "⚠ Ollama مش شغال. اضغط (تحميل Ollama) وثبّته مرة واحدة، وبعدين اضغط فحص.";
            OllamaStatus.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Err");
            StatusOllama.Text = "Ollama متوقف";
            UpdateModelChip();
            await RefreshGuide();
            return;
        }
        var m = await _ol.ListModels();
        _models = m; FillInstalledPanel();
        bool c = OllamaClient.HasModel(m, _s.ChatModel), e = OllamaClient.HasModel(m, _s.EmbedModel);
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, c && e ? "Ok" : "Warn");
        StatusOllama.Text = c && e ? "Ollama شغّال" : "Ollama شغّال (نموذج ناقص)";
        UpdateModelChip();
        OllamaStatus.Text = $"✔ Ollama شغال  |  نموذج المحادثة: {(c ? "موجود" : "ناقص")}  |  نموذج الفهرسة: {(e ? "موجود" : "ناقص")}";
        OllamaStatus.SetResourceReference(TextBlock.ForegroundProperty, c && e ? "Ok" : "Warn");
        await RefreshGuide();
    }

    async Task RunScan()
    {
        if (_indexer.Busy) return;
        _scanCts = new CancellationTokenSource();
        ScanBtn.IsEnabled = false; StopBtn.IsEnabled = true;
        try { await _indexer.Scan(_scanCts.Token); }
        catch (OperationCanceledException) { Log("اتوقفت الفهرسة"); }
        catch (Exception ex) { Log("خطأ فهرسة: " + ex.Message); }
        finally { ScanBtn.IsEnabled = true; StopBtn.IsEnabled = false; RefreshStats(); _ = RefreshGuide(); }
    }

    async Task<bool> EnsureReady()
    {
        if (!await _ol.IsUp())
        {
            AddBubble("⚠ Ollama مش شغال على جهازك (" + _ol.BaseUrl + ").\n" +
                      "افتح برنامج Ollama من قائمة ابدأ، أو اكتب في CMD:  ollama serve\n" +
                      "وبعدها ابعت رسالتك تاني. (لو مش متثبت: تبويب \"ابدأ هنا\" فيه زرار التحميل)", false);
            _ = RefreshGuide();
            return false;
        }
        List<string> m;
        try { m = await _ol.ListModels(); }
        catch (Exception ex) { AddBubble("مقدرتش أقرأ النماذج من Ollama: " + ex.Message, false); return false; }
        if (!OllamaClient.HasModel(m, _s.ChatModel))
        {
            var have = m.Count > 0 ? string.Join(" ، ", m) : "(مفيش نماذج منزّلة)";
            AddBubble($"⚠ نموذج المحادثة ({_s.ChatModel}) مش منزّل عندك.\n" +
                      $"المنزّل حاليًا: {have}\n" +
                      $"نزّله من CMD بالأمر:  ollama pull {_s.ChatModel}\n" +
                      "أو غيّر اسم نموذج المحادثة في تبويب الإعدادات لنموذج منزّل عندك.", false);
            _ = RefreshGuide();
            return false;
        }
        return true;
    }

    // ---------- chat ----------
    async void Send_Click(object s, RoutedEventArgs e)
    {
        if (_busy) { _chatCts?.Cancel(); return; }
        await SendMessage();
    }
    void NewChat_Click(object s, RoutedEventArgs e) => NewConv();

    // ---------- saved conversations ----------
    void Backup_Click(object s, RoutedEventArgs e) { try { _s.Save(); } catch { } new BackupDialog(this, _s).ShowDialog(); }

    void Restore_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "اختار فولدر النسخة الاحتياطية" };
        if (dlg.ShowDialog(this) != true) return;
        var dir = dlg.FolderName;
        if (!new[] { "Models", "Data", "Installers", "App" }.Any(n => Directory.Exists(Path.Combine(dir, n))))
        {
            MessageBox.Show(this, "الفولدر ده مش شكله نسخة VEXYON احتياطية.", "VEXYON", MessageBoxButton.OK, MessageBoxImage.Warning,
                MessageBoxResult.OK, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            return;
        }
        var oldEmbed = _s.EmbedModel;
        new RestoreDialog(this, _s, dir).ShowDialog();
        ChatModelBox.Text = _s.ChatModel; EmbedModelBox.Text = _s.EmbedModel; AutoApproveBox.IsChecked = _s.AutoApprove; InstructionsBox.Text = _s.CustomInstructions; MemoryBox.Text = Memory.Text();
        ApplyTheme(); ReloadAgent(); RefreshConvList(); _ = RefreshOllamaStatus();
        if (!oldEmbed.Equals(_s.EmbedModel)) { _db.Clear(); _ = RunScan(); }
    }

    void ReloadAgent() => _agent.Load(_conv.Messages.Select(m => (m.Role, m.Text)));

    void NewConv()
    {
        if (_busy) return;
        _conv = new Conversation();
        _agent.Reset();
        ResetSteps();
        ShowWelcome();
        RefreshConvList();
        Tabs.SelectedItem = TabChat;
    }

    void OpenConv(Conversation c)
    {
        if (_busy) return;
        _conv = c;
        _welcome = false; ResetSteps();
        ChatPanel.Children.Clear();
        Tabs.SelectedItem = TabChat;
        foreach (var m in c.Messages) AddBubble(m.Text, m.Role == "user");
        ReloadAgent();
        RefreshConvList();
        ChatScroll.ScrollToEnd();
    }

    void DeleteConv(Conversation c)
    {
        if (_busy) return;
        var r = MessageBox.Show(this, "تحذف المحادثة دي نهائياً؟\n\n" + c.Title, "تأكيد الحذف",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (r != MessageBoxResult.Yes) return;
        ChatStore.Delete(c.Id);
        if (c.Id == _conv.Id) NewConv(); else RefreshConvList();
    }

    void RefreshConvList()
    {
        if (ConvPanel == null) return;
        ConvPanel.Children.Clear();
        var all = ChatStore.All();
        if (all.Count == 0)
        {
            var empty = new TextBlock { Text = "مفيش محادثات محفوظة لسه.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            ConvPanel.Children.Add(empty);
            return;
        }
        foreach (var c in all)
        {
            var conv = c;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sp = new StackPanel();
            var title = new TextBlock { Text = conv.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, ToolTip = conv.Title };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var date = new TextBlock { Text = conv.Updated.ToString("yyyy-MM-dd HH:mm"), FontSize = 11, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right };
            date.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            sp.Children.Add(title); sp.Children.Add(date);

            var del = new TextBlock { Text = "🗑", FontSize = 16, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "حذف المحادثة" };
            del.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            del.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; DeleteConv(conv); };
            Grid.SetColumn(del, 1);
            g.Children.Add(sp); g.Children.Add(del);

            bool current = conv.Id == _conv.Id;
            var item = new Border { Child = g, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
            if (current) item.SetResourceReference(Border.BackgroundProperty, "SoftBlue");
            item.SetResourceReference(Border.BorderBrushProperty, current ? "Blue" : "Line");
            item.MouseLeftButtonUp += (_, _) => OpenConv(conv);
            ConvPanel.Children.Add(item);
        }
    }

    void InputBox_KeyDown(object s, KeyEventArgs e)
    {
        // Enter = إرسال ، Shift+Enter = سطر جديد
        if ((e.Key == Key.Enter || e.Key == Key.Return) && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendMessage();
        }
    }

    CancellationTokenSource? _chatCts;
    readonly List<string> _attach = new();
    Border? _live; TextBox? _liveText;
    static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    async Task SendMessage()
    {
        var text = InputBox.Text.Trim();
        if ((text.Length == 0 && _attach.Count == 0) || _busy) return;
        if (!await EnsureReady()) return;
        if (text.Length == 0) text = "اشرحلي المرفق وخلّصلي أهم النقاط.";
        var files = _attach.ToList();
        InputBox.Clear(); _attach.Clear(); RefreshAttach();
        if (_welcome) { ChatPanel.Children.Clear(); _welcome = false; }
        ResetSteps();
        var shown = files.Count > 0 ? string.Join("\n", files.Select(f => "📎 " + Path.GetFileName(f))) + "\n" + text : text;
        AddBubble(shown, true);
        _conv.Messages.Add(new ChatMsg { Role = "user", Text = shown });
        if (string.IsNullOrEmpty(_conv.Title))
        {
            var t1 = text.Replace("\r", " ").Replace("\n", " ").Trim();
            _conv.Title = t1.Length > 40 ? t1[..40] + "…" : t1;
        }
        _busy = true; SendBtn.Content = "■"; SendBtn.ToolTip = "إيقاف الرد"; StatusText.Text = "بيفكر...";
        ShowThinking();
        _chatCts = new CancellationTokenSource();
        ResetSpeed();
        try
        {
            var images = new List<string>();
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                try
                {
                    if (ImgExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    {
                        if (new FileInfo(f).Length > 8_000_000) { sb.Append($"[الصورة {name} كبيرة جداً وماتبعتتش]\n\n"); continue; }
                        images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(f)));
                        sb.Append($"[صورة مرفقة: {name}]\n\n");
                    }
                    else
                    {
                        var body = await Task.Run(() => Extractor.Extract(f));
                        bool cut = body.Length > 5000;
                        if (cut) body = body[..5000];
                        sb.Append($"[ملف مرفق: {name}]\n{body}{(cut ? "\n...[اتقطع: الملف كبير، حطه في المكتبة عشان يتفهرس كامل]" : "")}\n\n");
                    }
                }
                catch (Exception ex) { sb.Append($"[تعذّرت قراءة {name}: {ex.Message}]\n\n"); }
            }
            var prompt = sb.ToString() + text;

            var ans = await _agent.Ask(prompt,
                n => Dispatcher.Invoke(() => { AddNote(n); Log(n); }),
                _chatCts.Token,
                t => Dispatcher.Invoke(() => OnStream(t)),
                images.Count > 0 ? images.ToArray() : null);
            RemoveLive();
            MemoryBox.Text = Memory.Text();
            AddBubble(string.IsNullOrWhiteSpace(ans) ? "(مفيش رد)" : ans, false);
            ShowSpeed(ans);
            if (!string.IsNullOrWhiteSpace(ans)) _conv.Messages.Add(new ChatMsg { Role = "assistant", Text = ans });
        }
        catch (OperationCanceledException)
        {
            var part = _liveText?.Text ?? "";
            RemoveLive();
            AddBubble(string.IsNullOrWhiteSpace(part) ? "اتوقف الرد." : part + "\n\n(اتوقف الرد)", false);
            if (!string.IsNullOrWhiteSpace(part)) _conv.Messages.Add(new ChatMsg { Role = "assistant", Text = part });
        }
        catch (Exception ex) { RemoveLive(); AddBubble("حصل خطأ: " + ex.Message, false); }
        finally
        {
            RemoveThinking(); RemoveLive();
            _busy = false; SendBtn.Content = "↑"; SendBtn.ToolTip = "إرسال (Enter)"; SendBtn.IsEnabled = true; StatusText.Text = "";
            _conv.Updated = DateTime.Now;
            try { ChatStore.Save(_conv); } catch { }
            RefreshConvList();
        }
    }

    // ---------- streaming bubble ----------
    void OnStream(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            RemoveLive();
            if (_busy && _thinking == null) ShowThinking();
            return;
        }
        if (_live == null)
        {
            RemoveThinking();
            _liveText = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(4)
            };
            _liveText.SetResourceReference(TextBox.ForegroundProperty, "Ink");
            _live = new Border
            {
                Child = _liveText, CornerRadius = new CornerRadius(14), Margin = new Thickness(6, 5, 6, 5),
                Padding = new Thickness(10, 6, 10, 6), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Right,
                BorderThickness = new Thickness(1)
            };
            _live.SetResourceReference(Border.BackgroundProperty, "SoftBlue");
            _live.SetResourceReference(Border.BorderBrushProperty, "Line");
            ChatPanel.Children.Add(_live);
        }
        _liveText!.Text = text;
        _liveText.FlowDirection = HasArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ChatScroll.ScrollToEnd();
    }

    void RemoveLive()
    {
        if (_live == null) return;
        ChatPanel.Children.Remove(_live);
        _live = null; _liveText = null;
    }

    // ---------- attachments ----------
    void Attach_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "اختار ملف أو صورة",
            Filter = "ملفات وصور|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.json;*.cs;*.py;*.png;*.jpg;*.jpeg;*.webp|كل الملفات|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) if (!_attach.Contains(f)) _attach.Add(f);
        RefreshAttach();
    }

    void RefreshAttach()
    {
        AttachPanel.Children.Clear();
        AttachPanel.Visibility = _attach.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var f in _attach.ToList())
        {
            var file = f;
            var name = new TextBlock { Text = "📎 " + Path.GetFileName(file), VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var x = new TextBlock { Text = "  ✕", Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "شيل المرفق" };
            x.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            x.MouseLeftButtonUp += (_, _) => { _attach.Remove(file); RefreshAttach(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(name); row.Children.Add(x);
            var chip = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BackgroundProperty, "SoftBlue");
            chip.SetResourceReference(Border.BorderBrushProperty, "Line");
            AttachPanel.Children.Add(chip);
        }
    }

    // ---------- library ----------
    async void Reindex_Click(object s, RoutedEventArgs e) => await RunScan();
    void Stop_Click(object s, RoutedEventArgs e) => _scanCts?.Cancel();
    void OpenWatch_Click(object s, RoutedEventArgs e) => Process.Start("explorer.exe", _s.WatchFolder);

    void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) != true) return;
        AddRoot(dlg.FolderName);
    }

    void AddRoot(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return;
        if (_s.IndexFolders.Any(x => x.TrimEnd('\\', '/').Equals(path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) return;
        _s.IndexFolders.Add(path);
        _s.Save();
        BuildFolders();
        _indexer.StartWatching();
        _ = RunScan();
    }

    void BuildFolders()
    {
        FoldersPanel.Children.Clear();
        if (_s.IndexFolders.Count == 0)
        {
            var empty = new TextBlock { Text = "مفيش فولدرات - اضغط (إضافة فولدر / درايف)", TextWrapping = TextWrapping.Wrap };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SubInk");
            FoldersPanel.Children.Add(empty);
            return;
        }
        foreach (var f in _s.IndexFolders.ToList())
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBlock
            {
                Text = "📁  " + f, ToolTip = f, FlowDirection = FlowDirection.LeftToRight,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var rm = new Button { Content = "إزالة", Style = (Style)FindResource("Ghost"), Tag = f };
            Grid.SetColumn(rm, 1);
            rm.Click += (sender, args) =>
            {
                _s.IndexFolders.Remove((string)((Button)sender).Tag);
                _s.Save();
                BuildFolders();
                _indexer.StartWatching();
                _ = RunScan();
            };
            g.Children.Add(tb);
            g.Children.Add(rm);
            FoldersPanel.Children.Add(g);
        }
    }

    void SaveSkip_Click(object s, RoutedEventArgs e)
    {
        _s.SkipNames = SkipBox.Text
            .Split(new[] { ',', '،', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _s.Save();
        _indexer.StartWatching();
        _ = RunScan();
    }

    void ResetSkip_Click(object s, RoutedEventArgs e)
    {
        _s.SkipNames = Settings.DefaultSkipNames();
        SkipBox.Text = string.Join(", ", _s.SkipNames);
        _s.Save();
        _indexer.StartWatching();
        _ = RunScan();
    }

    // ---------- settings ----------
    async void CheckOllama_Click(object s, RoutedEventArgs e) => await RefreshOllamaStatus();

    void OpenOllama_Click(object s, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ollama.com/download") { UseShellExecute = true });

    void ChangeWork_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) != true) return;
        _s.WorkFolder = dlg.FolderName; WorkBox.Text = dlg.FolderName; _s.Save();
        ReloadAgent();
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        bool embedChanged = !_s.EmbedModel.Equals(EmbedModelBox.Text.Trim());
        _s.ChatModel = ChatModelBox.Text.Trim();
        _s.EmbedModel = EmbedModelBox.Text.Trim();
        _s.AutoApprove = AutoApproveBox.IsChecked == true;
        _s.CustomInstructions = InstructionsBox.Text.Trim();
        _s.MemoryOn = MemoryOnBox.IsChecked == true;
        if (int.TryParse(NumCtxBox.Text.Trim(), out var nc)) _s.NumCtx = Math.Clamp(nc, 4096, 65536);
        NumCtxBox.Text = _s.NumCtx.ToString();
        Memory.SaveAll(MemoryBox.Text.Split('\n'));
        MemoryBox.Text = Memory.Text();
        Lessons.SaveAll(LessonsBox.Text.Split('\n'));
        LessonsBox.Text = Lessons.Text();
        _s.Save();
        if (embedChanged) _db.Clear();
        ReloadAgent();
        PullStatus.Text = "اتحفظ.";
        _ = RefreshOllamaStatus();
        if (embedChanged) _ = RunScan();
    }

    async void Setup_Click(object s, RoutedEventArgs e)
    {
        if (!await _ol.IsUp()) { await RefreshOllamaStatus(); return; }
        SetupBtn.IsEnabled = false;
        try
        {
            var installed = await _ol.ListModels();
            foreach (var model in new[] { _s.EmbedModel, _s.ChatModel })
            {
                if (OllamaClient.HasModel(installed, model)) continue;
                await _ol.Pull(model, (st, pct) => Dispatcher.Invoke(() =>
                {
                    PullStatus.Text = $"{model}: {st}"; PullBar.Value = pct;
                }), CancellationToken.None);
            }
            PullStatus.Text = "النماذج جاهزة. من هنا تقدر تشتغل من غير نت.";
        }
        catch (Exception ex) { PullStatus.Text = "فشل التنزيل (محتاج نت): " + ex.Message; }
        finally
        {
            SetupBtn.IsEnabled = true;
            await RefreshOllamaStatus();
            _ = RunScan();
        }
    }

    // ---------- guide + tools ----------
    bool _ready;
    System.Windows.Threading.DispatcherTimer? _guideTimer;

    static string RamAdvice()
    {
        double gb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        string rec = gb <= 9 ? "qwen3:4b (خفيف)" : gb <= 20 ? "qwen3:4b أو qwen3:8b" : "qwen3:8b أو qwen3:14b";
        return $"جهازك: حوالي {gb:0} جيجا رام. المقترح للمحادثة: {rec}. السرعة بتتحدد بذاكرة كارت الشاشة: 4 جيجا = نماذج 4B، 8 جيجا = 8B، 12 جيجا فأكتر = 14B.";
    }

    static void SetStatus(TextBlock t, string text, bool ok)
    {
        t.Text = text;
        t.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Ok" : "Warn");
    }

    async Task RefreshGuide()
    {
        try
        {
            bool up = await _ol.IsUp();
            List<string> m = up ? await _ol.ListModels() : new List<string>();
            bool c = OllamaClient.HasModel(m, _s.ChatModel), e = OllamaClient.HasModel(m, _s.EmbedModel);
            _ready = up && c && e;
            SetStatus(St1, up ? "✔ Ollama شغال" : "✘ Ollama مش شغال أو مش متثبت", up);
            if (!up) SetStatus(St2, "— لازم الخطوة 1 الأول", false);
            else if (c && e) SetStatus(St2, $"✔ النماذج جاهزة ({_s.ChatModel} + {_s.EmbedModel})", true);
            else SetStatus(St2, "✘ ناقص: " + string.Join(" + ", new[] { c ? "" : _s.ChatModel, e ? "" : _s.EmbedModel }.Where(x => x.Length > 0)), false);
            int files = 0;
            try { files = _db.Stats().files; } catch { }
            SetStatus(St3, files > 0 ? $"✔ {files} ملف مفهرس" : "لسه مفيش ملفات - حط ملفاتك في الفولدر", files > 0);
            SetStatus(St4, _ready ? "✔ جاهز تبدأ" : "— كمّل الخطوات اللي فوق", _ready);
            RamText.Text = RamAdvice();
        }
        catch { }
    }

    async void RefreshGuide_Click(object s, RoutedEventArgs e) => await RefreshGuide();
    void GoTools_Click(object s, RoutedEventArgs e) => Tabs.SelectedItem = TabTools;

    void StartChat_Click(object s, RoutedEventArgs e)
    {
        Tabs.SelectedItem = TabChat;
        InputBox.Focus();
    }

    void Example_Click(object s, RoutedEventArgs e)
    {
        InputBox.Text = (string)((Button)s).Tag;
        Tabs.SelectedItem = TabChat;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    async void CalcTest_Click(object s, RoutedEventArgs e)
    {
        var btn = (Button)s;
        btn.IsEnabled = false;
        try
        {
            var r = await Tools.CalcSelfTest();
            MessageBox.Show(this, r, "فحص مكتبة الحسابات", MessageBoxButton.OK, MessageBoxImage.Information,
                MessageBoxResult.OK, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
        finally { btn.IsEnabled = true; }
    }

    void MailDev_Click(object s, RoutedEventArgs e)
    {
        try { OpenUrl("mailto:mostafakhalaf861@gmail.com?subject=VEXYON"); } catch { }
    }

    void ShowEula_Click(object s, RoutedEventArgs e)
    {
        string text;
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/EULA.txt"));
            using var sr = new StreamReader(info.Stream, System.Text.Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        catch { text = "تعذّر تحميل اتفاقية الاستخدام."; }

        Brush? R(string k) => TryFindResource(k) as Brush;
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14,
            Margin = new Thickness(16), BorderThickness = new Thickness(0),
            Background = R("Surface") ?? Brushes.White, Foreground = R("Ink") ?? Brushes.Black,
            FlowDirection = FlowDirection.RightToLeft
        };
        var win = new Window
        {
            Title = "اتفاقية الاستخدام — VEXYON", Owner = this, Width = 680, Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = R("Surface") ?? Brushes.White, Content = box
        };
        win.ShowDialog();
    }

    void DeepToggle_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _s.DeepThink = DeepToggle.IsChecked == true;
        _s.Save();
    }

    void WebToggle_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _s.WebSearch = WebToggle.IsChecked == true;
        _s.Save();
    }

    void InstructionsBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (InstructionsCount != null) InstructionsCount.Text = $"{InstructionsBox.Text.Length} / 2000";
    }

    void InstPreset_Click(object s, RoutedEventArgs e)
    {
        var line = (string)((Button)s).Tag;
        if (InstructionsBox.Text.Contains(line)) return;
        InstructionsBox.Text = (InstructionsBox.Text.TrimEnd() + "\n" + line).Trim();
        InstructionsBox.CaretIndex = InstructionsBox.Text.Length;
    }

    void Preset_Click(object s, RoutedEventArgs e)
    {
        ChatModelBox.Text = (string)((Button)s).Tag;
        PullStatus.Text = "اتغيّر اسم النموذج. دوس حفظ، وبعدين تجهيز النماذج.";
    }

    static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    void OpenLibrary_Click(object s, RoutedEventArgs e) => OpenUrl("https://ollama.com/library");

    void OpenDataDir_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.DataDir);
        Process.Start("explorer.exe", Settings.DataDir);
    }

    void RefreshTools_Click(object s, RoutedEventArgs e) => _ = DetectToolsAsync();

    bool _detecting;

    async Task DetectToolsAsync()
    {
        if (_detecting) return;
        _detecting = true;
        try
        {
            Log("بدوّر على البرامج المثبّتة في كل الجهاز...");
            await ToolFinder.DetectAllAsync();
            BuildToolsTab();
            Log("خلص فحص البرامج: لقيت " + ToolFinder.FoundCount + " برنامج.");
        }
        catch (Exception ex) { Log("تعذّر فحص البرامج: " + ex.Message); }
        finally { _detecting = false; }
    }

    record ToolInfo(string Name, string Desc, string Url, Func<bool>? Detect, string Tip = "");

    static string LocalApp => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProgFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    static bool OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(Path.Combine(dir.Trim().Trim('"'), exe))) return true; } catch { }
        }
        return false;
    }

    static bool AnyFile(params string[] files) =>
        files.Any(f => { try { return File.Exists(f); } catch { return false; } });

    static List<ToolInfo> Catalog() => new()
    {
        new("Ollama", "محرك تشغيل النماذج على جهازك. ده الإجباري الوحيد.",
            "https://ollama.com/download",
            () => OnPath("ollama.exe") || AnyFile(Path.Combine(LocalApp, "Programs", "Ollama", "ollama.exe")),
            "بعد التثبيت ارجع لتبويب ابدأ هنا ونزّل النماذج."),

        new("AnythingLLM", "تطبيق جاهز للمحادثة مع مستنداتك (مساحات عمل، بحث في الملفات، وكلاء). مكمّل أو بديل للبرنامج ده بواجهة تانية.",
            "https://docs.anythingllm.com/installation-desktop/windows",
            () => AnyFile(Path.Combine(LocalApp, "Programs", "AnythingLLM", "AnythingLLM.exe")),
            "بعد التثبيت اختار Ollama كمزوّد النموذج وحط العنوان http://127.0.0.1:11434 عشان يستخدم نفس النماذج اللي عندك."),

        new("LM Studio", "بديل لـ Ollama بواجهة رسومية لتنزيل النماذج وتجربتها.",
            "https://lmstudio.ai",
            () => AnyFile(Path.Combine(LocalApp, "Programs", "LM Studio", "LM Studio.exe")),
            "اختياري. البرنامج ده متظبط على Ollama."),

        new("Python", "بيشغّل السكربتات اللي النموذج بيكتبها (أتمتة، معالجة ملفات، تحويلات).",
            "https://www.python.org/downloads/",
            () => OnPath("python.exe") || AnyFile(@"C:\Windows\py.exe"),
            "وقت التثبيت علّم على Add Python to PATH."),

        new(".NET 8 SDK", "لبناء البرنامج ده وتشغيل مشاريع C#.",
            "https://dotnet.microsoft.com/download/dotnet/8.0",
            () => { var d = Path.Combine(ProgFiles, "dotnet", "sdk"); return Directory.Exists(d) && Directory.EnumerateDirectories(d).Any(); },
            "محتاجه مرة واحدة لتشغيل Build.bat."),

        new("Visual Studio Code", "محرر أكواد خفيف لمراجعة وتعديل الكود اللي النموذج بيكتبه.",
            "https://code.visualstudio.com/download",
            () => AnyFile(Path.Combine(LocalApp, "Programs", "Microsoft VS Code", "Code.exe"),
                          Path.Combine(ProgFiles, "Microsoft VS Code", "Code.exe"))),

        new("Git", "لحفظ نسخ من مشاريعك وتتبّع تغييرات الكود.",
            "https://git-scm.com/download/win",
            () => OnPath("git.exe") || AnyFile(Path.Combine(ProgFiles, "Git", "cmd", "git.exe"))),

        new("Tesseract OCR", "بيقرا النص من الصور وملفات PDF الممسوحة ضوئياً.",
            "https://github.com/UB-Mannheim/tesseract/wiki",
            () => OnPath("tesseract.exe") || AnyFile(Path.Combine(ProgFiles, "Tesseract-OCR", "tesseract.exe")),
            "ثبّت لغة العربية (ara) أثناء التثبيت. النموذج يقدر يشغّله بأمر tesseract بعد موافقتك."),

        new("ODA File Converter", "بيحوّل ملفات DWG وDXF بدون أوتوكاد (مفيد لملفات الرسم).",
            "https://www.opendesign.com/guestfiles/oda_file_converter",
            () => AnyFile(Path.Combine(ProgFiles, "ODA", "ODAFileConverter", "ODAFileConverter.exe")),
            "فيه واجهة وسطر أوامر، فالنموذج يقدر يشغّله لتحويل الملفات بموافقتك."),

        new("LibreOffice", "بيفتح ويحوّل ملفات Word وExcel وPowerPoint القديمة (.doc و.xls و.ppt) لـ PDF أو docx، فتتفهرس.",
            "https://www.libreoffice.org/download/download-libreoffice/",
            () => AnyFile(Path.Combine(ProgFiles, "LibreOffice", "program", "soffice.exe")),
            "النموذج يقدر يحوّل بأمر soffice --headless --convert-to pdf بعد موافقتك. المسار: C:\\Program Files\\LibreOffice\\program\\soffice.exe"),

        new("Pandoc", "محوّل مستندات بين الصيغ (Markdown وWord وHTML وPDF وغيرها).",
            "https://pandoc.org/installing.html",
            () => OnPath("pandoc.exe") || AnyFile(Path.Combine(LocalApp, "Pandoc", "pandoc.exe"))),

        new("7-Zip", "فك وضغط الملفات (zip و7z و rar وغيرها) عشان تفهرس محتوى الأرشيفات.",
            "https://www.7-zip.org/download.html",
            () => AnyFile(Path.Combine(ProgFiles, "7-Zip", "7z.exe"))),

        new("FFmpeg", "تحويل الصوت والفيديو. بيجهّز التسجيلات لو ضفت لاحقاً تحويل كلام لنص.",
            "https://ffmpeg.org/download.html",
            () => OnPath("ffmpeg.exe")),

        new("Node.js", "لتشغيل سكربتات JavaScript اللي النموذج بيكتبها.",
            "https://nodejs.org/en/download",
            () => OnPath("node.exe") || AnyFile(Path.Combine(ProgFiles, "nodejs", "node.exe"))),

        new("Everything", "بحث فوري في أسماء كل الملفات على الجهاز.",
            "https://www.voidtools.com/downloads/",
            () => AnyFile(Path.Combine(ProgFiles, "Everything", "Everything.exe")),
            "عشان النموذج يستخدمه محتاج كمان أداة سطر الأوامر es.exe من نفس الموقع."),

        new("AutoCAD", "برنامج الرسم الهندسي. النموذج يقدر يشغّل accoreconsole (نسخة بدون واجهة) مع ملفات .scr أو AutoLISP بموافقتك.",
            "https://www.autodesk.com/products/autocad/overview", null,
            "اشتغل دايماً على نسخة احتياطية من الرسمة."),

        new("Microsoft Word", "النموذج يقدر يفتح ملفاتك بيه، أو يكتب ملفات docx مباشرة من غير ما يفتحه.",
            "https://www.microsoft.com/microsoft-365/word", null),

        new("Microsoft Excel", "نفس الفكرة: فتح الجداول أو إنشاء ملفات xlsx.",
            "https://www.microsoft.com/microsoft-365/excel", null),

        new("Microsoft PowerPoint", "فتح العروض أو إنشاء ملفات pptx.",
            "https://www.microsoft.com/microsoft-365/powerpoint", null),

        new("Jan", "تطبيق محادثة محلي بديل بواجهة بسيطة، يشتغل مع نماذج على جهازك.",
            "https://jan.ai",
            null,
            "اختياري. مش متكامل مع البرنامج ده."),
    };

    void BuildToolsTab()
    {
        ToolsPanel.Children.Clear();
        foreach (var t in Catalog())
        {
            bool? found = null;
            if (t.Detect != null)
            {
                try { found = t.Detect(); } catch { found = null; }
            }
            var foundPath = ToolFinder.PathOf(t.Name);
            if (foundPath != null) found = true;
            else if (found != true && ToolFinder.Knows(t.Name))
                found = ToolFinder.Done ? false : null;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.Bold, FontSize = 15, Foreground = Res("Ink") });
            sp.Children.Add(new TextBlock { Text = t.Desc, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubInk") });
            if (!string.IsNullOrEmpty(t.Tip))
                sp.Children.Add(new TextBlock
                {
                    Text = "💡 " + t.Tip, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    Foreground = Res("Blue"), Margin = new Thickness(0, 3, 0, 0)
                });
            if (found != null)
                sp.Children.Add(new TextBlock
                {
                    Text = found == true ? "✔ مثبّت" : "لم يُكتشف على جهازك",
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0),
                    Foreground = found == true ? Res("Ok") : Res("Warn")
                });

            if (foundPath != null)
                sp.Children.Add(new TextBlock
                {
                    Text = foundPath, FontSize = 11, Foreground = Res("SubInk"),
                    FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap
                });

            var btn = new Button
            {
                Content = found == true ? "فتح الموقع" : "تحميل",
                Tag = t.Url, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center
            };
            if (found == true) btn.Style = (Style)FindResource("Ghost");
            btn.Click += (sender, args) => OpenUrl((string)((Button)sender).Tag);
            Grid.SetColumn(btn, 1);

            g.Children.Add(sp);
            g.Children.Add(btn);
            ToolsPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = g });
        }
    }
}
