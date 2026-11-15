using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RustBuilder;

public partial class MainWindow : Window
{
    readonly AppSettings settings = AppSettings.Current;
    readonly List<string> hotkeyErrors = new();
    int stepIdx, pieceIdx, viewFloor = 1;
    bool locked, hidden;
    IntPtr hwnd;
    LibraryWindow library;
    SettingsWindow settingsWindow;

    BuildPlan plan;
    PlanGeometry geo;
    string planError;
    Dictionary<string, int> pieceStep = new();

    public MainWindow()
    {
        AppSettings.ReadOnly = Environment.GetCommandLineArgs().Any(a => a.EndsWith("snapshot"));
        InitializeComponent();
        Logo.Source = SplashWindow.IconFrame(64) ?? Logo.Source;   // чёткий кадр иконки, а не 16×16
        locked = settings.Locked;
        PlaceOnScreen(settings.Left, settings.Top);
        ApplyAppearance();

        MouseLeftButtonDown += (_, e) =>
        {
            if (locked || e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();                 // блокирует до отпускания кнопки мыши
            settings.Left = Left;
            settings.Top = Top;
            settings.Save();
        };
    }

    /// Не даём окну оказаться за пределами экрана (например, после смены монитора)
    void PlaceOnScreen(double left, double top)
    {
        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120);
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80);
    }

    public void ApplyAppearance()
    {
        RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(settings.Scale, 0.6, 2.0);
        var a = (byte)(Math.Clamp(settings.BackgroundOpacity, 0.2, 1.0) * 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(a, 0x15, 0x12, 0x1C));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        RegisterHotkeys();
        ApplyLock();
        RestoreLastBuild();

        // Некоторые игры перекрывают topmost-окна — периодически поднимаем себя наверх (без захвата фокуса)
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { if (!hidden) Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); };
        t.Start();

        if (Environment.GetCommandLineArgs().Contains("--update-now"))   // для проверки автообновления без окна
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var r = await Updater.CheckAsync();
                    if (r != null) { await Updater.InstallAsync(r, null); }
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "update-error.txt"), ex.ToString()); }
                Application.Current.Shutdown();
            });
            return;
        }
        bool snapshot = HandleSnapshotArgs();
        if (snapshot) return;

        void AfterIntro()
        {
            PlayIntro();
            if (plan == null) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, OpenLibrary);   // первый запуск — сразу даём выбрать базу
            if (settings.AutoUpdate)
            {
                var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                upd.Tick += async (_, _) => { upd.Stop(); await UpdateWindow.CheckAndOffer(); };
                upd.Start();
            }
        }

        if (settings.Splash && settings.Animations)
        {
            Root.Opacity = 0;                       // оверлей появится после заставки
            var splash = new SplashWindow();
            splash.Finished += AfterIntro;
            splash.Show();
        }
        else AfterIntro();
    }

    void PlayIntro() => Anim.Intro(Root, Body, Logo, Root);

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotkeys();
        library?.Close();
        settingsWindow?.Close();
        base.OnClosed(e);
    }

    // ---------- Горячие клавиши ----------

    public void RegisterHotkeys()
    {
        UnregisterHotkeys();
        hotkeyErrors.Clear();
        if (AppSettings.ReadOnly) return;    // снимки для разработки: клавиши не нужны и не должны мешать запущенной копии
        var failed = new List<string>();
        foreach (HotkeyAction a in Enum.GetValues(typeof(HotkeyAction)))
        {
            var combo = settings.GetHotkey(a);
            if (string.IsNullOrWhiteSpace(combo)) continue;
            if (!Hotkey.TryParse(combo, out var mods, out var vk) ||
                !Native.RegisterHotKey(hwnd, (int)a, mods | Native.MOD_NOREPEAT, vk))
                failed.Add(Hotkey.Display(combo));
        }
        if (failed.Count > 0) hotkeyErrors.Add("Не удалось занять клавиши (заняты другой программой?): " + string.Join(", ", failed));
        if (IsLoaded) Render();
    }

    public void UnregisterHotkeys()
    {
        foreach (HotkeyAction a in Enum.GetValues(typeof(HotkeyAction))) Native.UnregisterHotKey(hwnd, (int)a);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        Do((HotkeyAction)wParam.ToInt32());
        return IntPtr.Zero;
    }

    void Do(HotkeyAction a)
    {
        switch (a)
        {
            case HotkeyAction.Next: Next(); break;
            case HotkeyAction.Prev: Prev(); break;
            case HotkeyAction.NextStep: GoTo(stepIdx + 1, 0); SpeakCurrent(); break;
            case HotkeyAction.PrevStep: GoTo(stepIdx - 1, 0); SpeakCurrent(); break;
            case HotkeyAction.FloorUp: ChangeFloor(+1); break;
            case HotkeyAction.FloorDown: ChangeFloor(-1); break;
            case HotkeyAction.Rotate: Rotate(); break;
            case HotkeyAction.Library: OpenLibrary(); break;
            case HotkeyAction.Settings: OpenSettings(); break;
            case HotkeyAction.Hide: hidden = !hidden; if (hidden) Hide(); else { Show(); PlayIntro(); } break;
            case HotkeyAction.Lock: ToggleLock(); break;
            case HotkeyAction.Reload: ReloadCurrent(); break;
            case HotkeyAction.Speak: lastSpokenFloor = 0; SpeakCurrent(); break;
        }
    }

    string Key(HotkeyAction a) => Hotkey.Display(settings.GetHotkey(a));

    // ---------- Режимы окна ----------

    void ToggleLock()
    {
        locked = !locked;
        settings.Locked = locked;
        settings.Save();
        ApplyLock();
        Render();
    }

    void ApplyLock()
    {
        if (hwnd != IntPtr.Zero)
        {
            var ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
            ex = locked
                ? ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE   // клики проходят в игру
                : ex & ~Native.WS_EX_TRANSPARENT & ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        }

        Root.BorderBrush = new SolidColorBrush(locked ? Color.FromArgb(0x40, 255, 255, 255) : PlanRenderer.Accent);
        Toolbar.Visibility = NavBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Cursor = locked ? null : Cursors.SizeAll;
    }

    // ---------- Окна ----------

    void OpenLibrary()
    {
        if (library != null) { library.Activate(); return; }
        library = new LibraryWindow(plan?.FullPath);
        library.Chosen += p => { lastSpokenFloor = 0; SelectPlan(p, 0, 0); SpeakCurrent(); };
        library.Closed += (_, _) => library = null;
        library.Show();
        library.Activate();
    }

    void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) => { settingsWindow = null; RegisterHotkeys(); };
        UnregisterHotkeys();   // иначе окно настроек не увидит нажатия, которые сейчас заняты
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    /// Включили/выключили интерьеры — пересобираем текущую базу (шаги стройки остаются на месте)
    public void ReapplyInterior()
    {
        if (plan == null) return;
        SelectPlan(plan.Original ?? plan, stepIdx, pieceIdx);   // GoTo сам обрежет шаг, если шагов стало меньше
        library?.Reload(plan.FullPath);
    }

    /// Вызывается окном настроек после изменений
    public void SettingsChanged()
    {
        ApplyAppearance();
        if (plan != null) GoTo(stepIdx, pieceIdx); else Render();
    }

    // ---------- Выбор базы ----------

    void RestoreLastBuild()
    {
        if (settings.LastBuild != null && File.Exists(settings.LastBuild))
        {
            try
            {
                var p = BuildPlan.Load(settings.LastBuild);
                p.IsUser = !settings.LastBuild.StartsWith(BuildLibrary.BundledDir, StringComparison.OrdinalIgnoreCase);
                SelectPlan(p, settings.LastStep, settings.LastPiece);
                return;
            }
            catch { /* файл испортился — пусть выберут заново */ }
        }
        plan = null;
        Render();
    }

    void ReloadCurrent()
    {
        if (plan == null) return;
        try { SelectPlan(BuildPlan.Load(plan.FullPath), stepIdx, pieceIdx); }
        catch (Exception ex) { planError = $"Ошибка в {plan.FileName}: {ex.Message}"; geo = null; Render(); }
        library?.Reload(plan?.FullPath);
    }

    void SelectPlan(BuildPlan p, int step, int piece)
    {
        plan = p.WithInterior(settings.GetInterior(p.Original ?? p));
        try { geo = new PlanGeometry(plan); planError = null; }
        catch (Exception ex) { geo = null; planError = $"Ошибка в {plan.FileName}: {ex.Message}"; }

        pieceStep = new();
        for (int i = 0; i < plan.Steps.Count; i++)
            foreach (var id in plan.Steps[i].Pieces)
                pieceStep.TryAdd(id, i);

        settings.LastBuild = plan.FullPath;
        GoTo(step, piece);
        Anim.PlanIn(Plan);     // новая база — схема «собирается» волной
    }

    // ---------- Навигация ----------

    List<Piece> StepPieces(int step) =>
        plan == null || step < 0 || step >= plan.Steps.Count ? new()
        : plan.Steps[step].Pieces.Select(id => plan.Pieces.FirstOrDefault(p => p.Id == id)).Where(p => p != null).ToList();

    bool PieceMode => settings.PieceByPiece && StepPieces(stepIdx).Count > 1;

    void Next()
    {
        if (plan == null) return;
        var before = (stepIdx, pieceIdx);
        if (PieceMode && pieceIdx < StepPieces(stepIdx).Count - 1) GoTo(stepIdx, pieceIdx + 1);
        else GoTo(stepIdx + 1, 0);
        if ((stepIdx, pieceIdx) != before)
        {
            bool last = stepIdx == plan.Steps.Count - 1 && (!PieceMode || pieceIdx >= StepPieces(stepIdx).Count - 1);
            if (last) Sfx.Finish(); else Sfx.Step();
        }
        SpeakCurrent();
    }

    void Prev()
    {
        if (plan == null) return;
        if (PieceMode && pieceIdx > 0) GoTo(stepIdx, pieceIdx - 1);
        else if (stepIdx > 0) GoTo(stepIdx - 1, settings.PieceByPiece ? int.MaxValue : 0);
        SpeakCurrent();
    }

    void GoTo(int step, int piece)
    {
        if (plan == null) { Render(); return; }
        stepIdx = Math.Clamp(step, 0, Math.Max(plan.Steps.Count - 1, 0));
        var pcs = StepPieces(stepIdx);
        pieceIdx = Math.Clamp(piece, 0, Math.Max(pcs.Count - 1, 0));
        viewFloor = CurrentFloor();
        settings.LastStep = stepIdx;
        settings.LastPiece = pieceIdx;
        settings.Save();
        Render();
    }

    void ChangeFloor(int delta)
    {
        if (plan == null) return;
        viewFloor = Math.Clamp(viewFloor + delta, 1, plan.FloorCount);
        Render();
    }

    void Rotate()
    {
        settings.Rotation = (settings.Rotation + 1) % 4;
        settings.Save();
        Render();
    }

    int CurrentFloor()
    {
        if (plan.Steps.Count == 0) return 1;
        var s = plan.Steps[stepIdx];
        var pcs = StepPieces(stepIdx);
        if (PieceMode) return pcs[pieceIdx].Level;
        if (s.Floor.HasValue) return s.Floor.Value;
        return pcs.FirstOrDefault()?.Level ?? 1;
    }

    PieceState StateOf(Piece p)
    {
        if (!pieceStep.TryGetValue(p.Id, out var s) || s < stepIdx) return PieceState.Done;
        if (s > stepIdx) return PieceState.Future;
        if (!PieceMode) return PieceState.Current;
        int j = plan.Steps[stepIdx].Pieces.IndexOf(p.Id);
        return j < pieceIdx ? PieceState.Done : j == pieceIdx ? PieceState.Current : PieceState.Upcoming;
    }

    int? NumberOf(Piece p)
    {
        if (!pieceStep.TryGetValue(p.Id, out var s) || s != stepIdx) return null;
        var pcs = StepPieces(stepIdx);
        if (pcs.Count < 2) return null;
        return pcs.FindIndex(x => x.Id == p.Id) + 1;
    }

    // ---------- Что ставить: коротко ----------

    /// Направление стороны на схеме (с учётом поворота): 0 = вправо, 2 = вниз, 4 = влево, 6 = вверх
    int Sector(Vector n)
    {
        var r = PlanRenderer.Rotate(n, settings.Rotation);
        var ang = Math.Atan2(r.Y, r.X) * 180 / Math.PI;           // ось Y вниз
        return (int)Math.Round(((ang % 360) + 360) % 360 / 45.0) % 8;
    }

    static readonly string[] Arrows = { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" };
    static readonly string[] Sides = { "справа", "снизу справа", "снизу", "снизу слева", "слева", "сверху слева", "сверху", "сверху справа" };

    string Short(Piece p) => p.Type switch
    {
        "foundation" => "ФУНДАМЕНТ",
        "triangle" => "ТРЕУГОЛЬНИК",
        "wall" => "СТЕНА",
        "doorway" => p.Door != null ? "ПРОЁМ + ДВЕРЬ" : "ДВЕРНОЙ ПРОЁМ",
        "window" => p.HasEmbrasure ? (p.VerticalEmbrasure ? "ОКНО + ВЕРТ. БОЙНИЦА" : "ОКНО + ГОР. БОЙНИЦА") : "ОКНО",
        "halfwall" => "ПОЛУСТЕНА",
        "floor" => "ПОТОЛОК",
        "roof" => "КРЫША",
        "frame" => p.Door == "garage" ? "КАРКАС + ГАРАЖКА" : p.Door != null ? "КАРКАС + ДВОЙНАЯ ДВЕРЬ" : "НАСТЕННЫЙ КАРКАС",
        "stairs" => geo != null && geo.Triangles.Contains(p.On) ? "СПИРАЛЬНЫЙ ПОДЪЁМ" : "ПОДЪЁМ",
        "tc" => "ШКАФ",
        "box" => "ЯЩИК",
        "furnace" => "ПЕЧКА",
        "workbench" => "ВЕРСТАК",
        "repair" => "РЕМ. ВЕРСТАК",
        "bag" => "СПАЛЬНИК",
        "locker" => "ШКАФЧИК",
        _ => p.Type.ToUpper(),
    };

    static string Plural(string type) => type switch
    {
        "foundation" => "ФУНДАМЕНТЫ", "triangle" => "ТРЕУГОЛЬНИКИ", "wall" => "СТЕНЫ", "doorway" => "ПРОЁМЫ",
        "window" => "ОКНА", "halfwall" => "ПОЛУСТЕНЫ", "floor" => "ПОТОЛКИ", "roof" => "КРЫШИ", "frame" => "КАРКАСЫ", "stairs" => "ПОДЪЁМЫ", "tc" => "ШКАФЫ",
        "box" => "ЯЩИКИ", "furnace" => "ПЕЧКИ", "workbench" => "ВЕРСТАКИ", "repair" => "РЕМ. ВЕРСТАКИ", "bag" => "СПАЛЬНИКИ", "locker" => "ШКАФЧИКИ",
        _ => type.ToUpper(),
    };

    record PieceInfo(string Title, string Where, string Extra, string Speech, int Sector);

    PieceInfo Info(Piece p)
    {
        bool roofSlope = p.Type == "roof" && p.Edge != null;
        int sector = p.IsEdgePiece || roofSlope ? Sector(geo.EdgeNormal(p.On, p.Edge)) : -1;
        string where = roofSlope ? $"{Arrows[sector]}  скат вниз — {Sides[sector]}"
                     : sector >= 0 ? $"{Arrows[sector]}  {Sides[sector]}" : "в подсвеченную клетку";
        var extras = new List<string>();
        if (p.DoorName != null) extras.Add(p.DoorName);
        if (p.LockName != null) extras.Add(p.LockName);
        if (p.EmbrasureName != null) extras.Add(p.EmbrasureName + " — для отстрела");
        if (p.Entrance) extras.Add("это вход");
        string title = Short(p);
        string spoken = p.HasEmbrasure ? (p.VerticalEmbrasure ? "Окно с вертикальной бойницей" : "Окно с горизонтальной бойницей") : char.ToUpper(title[0]) + title[1..].ToLower();
        string speech = spoken + ". " + (sector >= 0 ? Sides[sector] : "") + ".";
        if (p.DoorName != null) speech += " " + char.ToUpper(p.DoorName[0]) + p.DoorName[1..] + (p.LockName != null ? " и " + p.LockName : "") + ".";
        return new PieceInfo(title, where, string.Join(" · ", extras), speech, sector);
    }

    int lastSpokenFloor;

    void SpeakCurrent()
    {
        if (!settings.VoiceEnabled || plan == null || geo == null || plan.Steps.Count == 0) return;
        var pcs = StepPieces(stepIdx);
        string text;
        int floor;
        if (pcs.Count == 0) { text = plan.Steps[stepIdx].Text; floor = viewFloor; }
        else if (PieceMode || pcs.Count == 1) { var p = pcs[PieceMode ? pieceIdx : 0]; text = Info(p).Speech; floor = p.Level; }
        else
        {
            var types = pcs.Select(p => p.Type).Distinct().ToList();
            text = types.Count == 1
                ? $"{(pcs.All(p => p.HasEmbrasure) ? "окна с бойницами" : Plural(types[0]).ToLower())}, {pcs.Count} штук. Все подсвеченные."
                : string.Join(", ", pcs.GroupBy(Short).Select(g => g.Key.ToLower().Replace(" + ", " и "))) + ". Все подсвеченные.";
            floor = pcs[0].Level;
        }
        if (floor != lastSpokenFloor && floor > 0) text = Voice.FloorPhrase(floor) + ". " + text;
        lastSpokenFloor = floor;
        Voice.Say(text, settings.VoiceVolume);
    }

    // ---------- Иконка блока ----------

    void DrawIcon(Piece p, int sector)
    {
        PieceIcon.Children.Clear();
        var accent = PlanRenderer.Accent;
        var faint = Color.FromArgb(0x50, 255, 255, 255);
        double s = 52, m = 8;
        var cell = new System.Windows.Shapes.Rectangle { Width = s - 2 * m, Height = s - 2 * m, Stroke = new SolidColorBrush(faint), StrokeThickness = 1.5 };
        System.Windows.Controls.Canvas.SetLeft(cell, m);
        System.Windows.Controls.Canvas.SetTop(cell, m);

        void Line(double x1, double y1, double x2, double y2, double th, Color c) => PieceIcon.Children.Add(new System.Windows.Shapes.Line
        { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(c), StrokeThickness = th, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        void Glyph(string t, double size)
        {
            var tb = new System.Windows.Controls.TextBlock { Text = t, FontSize = size, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(accent) };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Controls.Canvas.SetLeft(tb, (s - tb.DesiredSize.Width) / 2);
            System.Windows.Controls.Canvas.SetTop(tb, (s - tb.DesiredSize.Height) / 2);
            PieceIcon.Children.Add(tb);
        }

        if (p == null) { Glyph("🔨", 26); return; }

        switch (p.Type)
        {
            case "foundation":
                cell.Fill = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B));
                cell.Stroke = new SolidColorBrush(accent);
                PieceIcon.Children.Add(cell);
                break;
            case "triangle":
                PieceIcon.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new(s / 2, m), new(s - m, s - m), new(m, s - m) },
                    Fill = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), Stroke = new SolidColorBrush(accent), StrokeThickness = 1.5,
                });
                break;
            case "floor":
                PieceIcon.Children.Add(cell);
                for (int i = 1; i < 6; i++) Line(m + i * (s - 2 * m) / 6, m, m, m + i * (s - 2 * m) / 6, 1.5, accent);
                for (int i = 1; i < 6; i++) Line(s - m, m + i * (s - 2 * m) / 6, m + i * (s - 2 * m) / 6, s - m, 1.5, accent);
                break;
            case "roof":   // скат: конёк сверху, стрелка вниз по скату (повёрнута к нужной стороне)
            {
                var roofCanvas = new System.Windows.Controls.Canvas { Width = s, Height = s };
                var verts = geo != null && geo.Triangles.Contains(p.On)
                    ? new[] { new Point(m, s - m), new Point(s - m, s - m), new Point(s / 2, m) }
                    : new[] { new Point(m, m), new Point(s - m, m), new Point(s - m, s - m), new Point(m, s - m) };
                roofCanvas.Children.Add(new System.Windows.Shapes.Polygon { Points = new PointCollection(verts), Stroke = new SolidColorBrush(faint), StrokeThickness = 1.5 });
                PlanRenderer.RoofSlope(roofCanvas, verts, verts.Length == 4 ? 2 : 0, accent);
                if (sector >= 0) roofCanvas.RenderTransform = new RotateTransform(sector * 45 - 90, s / 2, s / 2);   // низ ската → в сторону свеса
                PieceIcon.Children.Add(roofCanvas);
                break;
            }
            case "stairs" when geo != null && geo.Triangles.Contains(p.On):
                PieceIcon.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new(s / 2, m), new(s - m, s - m), new(m, s - m) },
                    Stroke = new SolidColorBrush(faint), StrokeThickness = 1.5,
                });
                Glyph("↻", 20);
                break;
            case "stairs":
                PieceIcon.Children.Add(cell);
                for (int i = 1; i <= 4; i++) Line(m + 3, m + i * (s - 2 * m) / 5, s - m - 3, m + i * (s - 2 * m) / 5, 2, accent);
                break;
            case "tc":
                Glyph("TC", 18);
                break;
            case var it when p.IsItem:
                var (letter, col) = PlanRenderer.ItemLook(it);
                var box = new System.Windows.Shapes.Rectangle
                {
                    Width = 32, Height = 32, RadiusX = 5, RadiusY = 5,
                    Fill = new SolidColorBrush(col), Stroke = new SolidColorBrush(accent), StrokeThickness = 2.5,
                };
                System.Windows.Controls.Canvas.SetLeft(box, 10);
                System.Windows.Controls.Canvas.SetTop(box, 10);
                PieceIcon.Children.Add(box);
                var lt = new System.Windows.Controls.TextBlock { Text = letter, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x12, 0x1C)) };
                lt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                System.Windows.Controls.Canvas.SetLeft(lt, (s - lt.DesiredSize.Width) / 2);
                System.Windows.Controls.Canvas.SetTop(lt, (s - lt.DesiredSize.Height) / 2);
                PieceIcon.Children.Add(lt);
                break;
            default:   // стены: клетка, у которой выделена нужная сторона
                PieceIcon.Children.Add(cell);
                var c = new Point(s / 2, s / 2);
                double h = (s - 2 * m) / 2;
                var dir = sector >= 0 ? sector : 6;
                double ang = dir * 45 * Math.PI / 180;
                var n = new Vector(Math.Cos(ang), Math.Sin(ang));
                var t = new Vector(-n.Y, n.X);
                double reach = dir % 2 == 0 ? h : h * 0.72;   // диагональная сторона — ближе к центру
                var mid = c + n * reach;
                var a = mid - t * h * 0.9; var b = mid + t * h * 0.9;
                double th = 6;
                if (p.Type == "frame")
                {
                    var g1 = a + (b - a) * 0.12; var g2 = a + (b - a) * 0.88;
                    Line(a.X, a.Y, g1.X, g1.Y, th, accent);
                    Line(g2.X, g2.Y, b.X, b.Y, th, accent);
                    if (p.Door == "garage") Line(g1.X, g1.Y, g2.X, g2.Y, 4, Color.FromRgb(0xD8, 0xB0, 0x40));
                    else if (p.Door != null)
                    {
                        var e1 = g1 - n * 9 + t * 4; var e2 = g2 - n * 9 - t * 4;
                        Line(g1.X, g1.Y, e1.X, e1.Y, 3, Colors.White);
                        Line(g2.X, g2.Y, e2.X, e2.Y, 3, Colors.White);
                    }
                }
                else if (p.Type is "doorway" or "window")
                {
                    var g1 = a + (b - a) * 0.32; var g2 = a + (b - a) * 0.68;
                    Line(a.X, a.Y, g1.X, g1.Y, th, accent);
                    Line(g2.X, g2.Y, b.X, b.Y, th, accent);
                    if (p.Type == "window") Line(g1.X, g1.Y, g2.X, g2.Y, 1.5, accent);
                    else if (p.Door != null) { var e = g1 - n * 9 + t * 4; Line(g1.X, g1.Y, e.X, e.Y, 3, Colors.White); }
                }
                else Line(a.X, a.Y, b.X, b.Y, p.Type == "halfwall" ? 3.5 : th, accent);
                break;
        }
    }

    // ---------- Отрисовка ----------

    void Render()
    {
        Plan.Children.Clear();
        bool simple = settings.SimpleMode;
        Hint.Text = !locked
            ? "Окно можно тащить мышью. Нажми «Закрепить», и клики будут проходить в игру."
            : simple
                ? $"{Key(HotkeyAction.Next)} — дальше     {Key(HotkeyAction.Prev)} — назад"
                : $"{Key(HotkeyAction.Next)} далее · {Key(HotkeyAction.Prev)} назад · {Key(HotkeyAction.Speak)} повторить · " +
                  $"{Key(HotkeyAction.Rotate)} повернуть · {Key(HotkeyAction.Lock)} разблокировать · {Key(HotkeyAction.Settings)} настройки";
        if (hotkeyErrors.Count > 0) Hint.Text = string.Join("\n", hotkeyErrors) + "\n" + Hint.Text;
        DiscordPresence.Update(plan?.Name, stepIdx + 1, plan?.Steps.Count ?? 0, viewFloor);

        StepCost.Visibility = TotalCost.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;

        if (plan == null)
        {
            TitleText.Text = "RustBuilder";
            AuthorBadge.Visibility = Visibility.Collapsed;
            StepCounter.Text = FloorText.Text = StepCost.Text = TotalCost.Text = "";
            StepText.Text = $"Сначала выбери базу: нажми «Базы» ({Key(HotkeyAction.Library)}).";
            StepText.Visibility = Visibility.Visible;
            PlanBox.Visibility = PieceBox.Visibility = ProgressBox.Visibility = Visibility.Collapsed;
            return;
        }
        PlanBox.Visibility = ProgressBox.Visibility = Visibility.Visible;

        TitleText.Text = plan.Name;
        AuthorBadgeText.Text = Authors.BadgeText(plan.Author);
        AuthorBadge.Background = Authors.BadgeBrush(plan.Author);
        AuthorBadge.Visibility = AuthorBadgeText.Text == null ? Visibility.Collapsed : Visibility.Visible;
        StepCounter.Text = simple || plan.Steps.Count == 0 ? "" : $"шаг {stepIdx + 1}/{plan.Steps.Count}";
        string[] rot = { "", " · схема повёрнута на 90°", " · схема повёрнута на 180°", " · схема повёрнута на 270°" };
        FloorText.Text = (plan.FloorCount > 1 ? $"Этаж {viewFloor} из {plan.FloorCount}" : "") + (simple ? "" : rot[settings.Rotation % 4])
                         + (plan.InteriorName != null ? $" · интерьер «{plan.InteriorName}»" : "");
        FloorText.Visibility = FloorText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (geo == null)
        {
            StepText.Text = planError;
            StepText.Visibility = Visibility.Visible;
            StepCost.Text = TotalCost.Text = "";
            PieceBox.Visibility = Visibility.Collapsed;
            return;
        }

        // прогресс по всей базе
        int total = plan.Steps.Sum(s => s.Pieces.Count);
        int done = plan.Steps.Take(stepIdx).Sum(s => s.Pieces.Count) + (PieceMode ? pieceIdx : 0);
        bool lastStep = stepIdx == plan.Steps.Count - 1;
        double frac = total == 0 ? 0 : lastStep && StepPieces(stepIdx).Count == 0 ? 1 : (double)done / total;
        ProgressFill.Width = 394 * Math.Clamp(frac, 0, 1);

        var step = plan.Steps.Count > 0 ? plan.Steps[stepIdx] : null;
        var pcs = StepPieces(stepIdx);
        PieceBox.Visibility = Visibility.Visible;
        StepText.Text = step?.Text ?? "";
        StepText.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;

        if (pcs.Count == 0)
        {
            DrawIcon(null, -1);
            PieceTitle.Text = lastStep ? "ФИНИШ" : "СДЕЛАЙ";
            PieceWhere.Text = "";
            PieceExtra.Text = step?.Text ?? "";
            PieceCounter.Text = "";
            StepText.Visibility = Visibility.Collapsed;
        }
        else if (PieceMode || pcs.Count == 1)
        {
            var p = pcs[PieceMode ? pieceIdx : 0];
            var info = Info(p);
            DrawIcon(p, info.Sector);
            PieceTitle.Text = info.Title;
            PieceWhere.Text = info.Where + (p.Level != viewFloor ? $" (этаж {p.Level})" : p.Level > 1 && simple ? $"  ·  этаж {p.Level}" : "");
            PieceExtra.Text = info.Extra;
            PieceCounter.Text = PieceMode ? $"{pieceIdx + 1}/{pcs.Count}" : "";
        }
        else
        {
            var types = pcs.Select(p => p.Type).Distinct().ToList();
            DrawIcon(pcs[0], -1);
            PieceTitle.Text = types.Count == 1
                ? $"{(pcs.All(p => p.HasEmbrasure) ? "ОКНА + БОЙНИЦЫ" : Plural(types[0]))} × {pcs.Count}"
                : string.Join(", ", pcs.GroupBy(Short).Select(g => g.Count() > 1 ? $"{g.Key} × {g.Count()}" : g.Key));
            PieceWhere.Text = "все подсвеченные на схеме";
            PieceExtra.Text = simple ? step.Text : "";
            PieceCounter.Text = "";
        }
        PieceExtra.Visibility = PieceExtra.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PieceWhere.Visibility = PieceWhere.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var stepRes = new Res();
        foreach (var p in pcs) stepRes.Add(Costs.Of(p, plan, geo));
        StepCost.Text = pcs.Count > 0 ? "На весь шаг: " + stepRes : "";
        TotalCost.Text = $"Вся база (до {plan.Tier}): " + plan.TotalCost(geo);

        PlanRenderer.Draw(Plan, plan, geo, StateOf, viewFloor, settings.Rotation, NumberOf);
    }

    // ---------- Кнопки ----------

    void Library_Click(object sender, RoutedEventArgs e) => OpenLibrary();
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();
    void Exit_Click(object sender, RoutedEventArgs e) => Close();
    void Prev_Click(object sender, RoutedEventArgs e) => Prev();
    void Next_Click(object sender, RoutedEventArgs e) => Next();
    void Speak_Click(object sender, RoutedEventArgs e) { lastSpokenFloor = 0; SpeakCurrent(); }
    void Rotate_Click(object sender, RoutedEventArgs e) => Rotate();
    void FloorUp_Click(object sender, RoutedEventArgs e) => ChangeFloor(+1);
    void FloorDown_Click(object sender, RoutedEventArgs e) => ChangeFloor(-1);

    // ---------- Для разработки ----------
    // --snapshot out.png <файл базы> <шаг> [блок] [поворот]  — снимок оверлея
    // --library-snapshot out.png                           — снимок окна баз
    // --catalog-snapshot out.png <репозиторий или папка>     — снимок каталога
    // --settings-snapshot out.png                          — снимок настроек

    bool HandleSnapshotArgs()
    {
        var args = Environment.GetCommandLineArgs();
        string Arg(int i) => i < args.Length ? args[i] : null;

        int i = Array.IndexOf(args, "--snapshot");
        if (i >= 0 && Arg(i + 1) != null)
        {
            if (Arg(i + 5) != null) settings.Rotation = int.Parse(Arg(i + 5));
            if (Arg(i + 6) != null) settings.PieceByPiece = Arg(i + 6) == "piece";
            if (Arg(i + 2) != null) SelectPlan(BuildPlan.Load(Arg(i + 2)), int.Parse(Arg(i + 3) ?? "0"), int.Parse(Arg(i + 4) ?? "0"));
            SaveSnapshot(Root, Arg(i + 1));
            return true;
        }
        i = Array.IndexOf(args, "--catalog-snapshot");
        if (i >= 0 && Arg(i + 2) != null)
        {
            settings.CatalogRepo = Arg(i + 2);
            var cat = new CatalogWindow();
            cat.Show();
            var path = Arg(i + 1);
            var videosOut = Arg(i + 3);     // необязательно: снимок окна видео-гайдов
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (videosOut != null && cat.OpenFirstVideos() is Window vw)
                {
                    _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(() => SaveSnapshot((FrameworkElement)vw.Content, videosOut)));
                    return;
                }
                SaveSnapshot((FrameworkElement)cat.Content, path);
            }));
            return true;
        }
        i = Array.IndexOf(args, "--library-snapshot");
        if (i >= 0 && Arg(i + 1) != null)
        {
            var lib = new LibraryWindow();
            lib.Show();
            SaveSnapshot((FrameworkElement)lib.Content, Arg(i + 1));
            return true;
        }
        i = Array.IndexOf(args, "--editor-snapshot");     // out.png [base.json|-] [скрипт кликов]
        if (i >= 0 && Arg(i + 1) != null)
        {
            var src = Arg(i + 2) is string b && b != "-" ? BuildPlan.Load(b) : null;
            var ed = new EditorWindow(src);
            ed.Show();
            if (Arg(i + 3) != null) ed.DevScript(Arg(i + 3));
            SaveSnapshot((FrameworkElement)ed.Content, Arg(i + 1));
            return true;
        }
        i = Array.IndexOf(args, "--settings-snapshot");
        if (i >= 0 && Arg(i + 1) != null)
        {
            var sw = new SettingsWindow(this);
            sw.Show();
            SaveSnapshot((FrameworkElement)sw.Content, Arg(i + 1));
            return true;
        }
        return false;
    }

    void SaveSnapshot(FrameworkElement el, string path)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * (el == Root ? RootScale.ScaleX : 1)),
                                             (int)Math.Ceiling(el.ActualHeight * (el == Root ? RootScale.ScaleY : 1)), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
            Application.Current.Shutdown();
        });
    }
}
