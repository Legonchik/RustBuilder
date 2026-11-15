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
        Root.Background = new SolidColorBrush(Color.FromArgb(a, 0x14, 0x16, 0x19));
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

        if (!HandleSnapshotArgs() && plan == null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, OpenLibrary);   // первый запуск — сразу даём выбрать базу
    }

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
            case HotkeyAction.NextStep: GoTo(stepIdx + 1, 0); break;
            case HotkeyAction.PrevStep: GoTo(stepIdx - 1, 0); break;
            case HotkeyAction.FloorUp: ChangeFloor(+1); break;
            case HotkeyAction.FloorDown: ChangeFloor(-1); break;
            case HotkeyAction.Rotate: Rotate(); break;
            case HotkeyAction.Library: OpenLibrary(); break;
            case HotkeyAction.Settings: OpenSettings(); break;
            case HotkeyAction.Hide: hidden = !hidden; if (hidden) Hide(); else Show(); break;
            case HotkeyAction.Lock: ToggleLock(); break;
            case HotkeyAction.Reload: ReloadCurrent(); break;
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

        Root.BorderBrush = new SolidColorBrush(locked ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromRgb(0xE0, 0x58, 0x3A));
        Toolbar.Visibility = NavBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Cursor = locked ? null : Cursors.SizeAll;
    }

    // ---------- Окна ----------

    void OpenLibrary()
    {
        if (library != null) { library.Activate(); return; }
        library = new LibraryWindow(plan?.FullPath);
        library.Chosen += p => SelectPlan(p, 0, 0);
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
        plan = p;
        try { geo = new PlanGeometry(plan); planError = null; }
        catch (Exception ex) { geo = null; planError = $"Ошибка в {plan.FileName}: {ex.Message}"; }

        pieceStep = new();
        for (int i = 0; i < plan.Steps.Count; i++)
            foreach (var id in plan.Steps[i].Pieces)
                pieceStep.TryAdd(id, i);

        settings.LastBuild = plan.FullPath;
        GoTo(step, piece);
    }

    // ---------- Навигация ----------

    List<Piece> StepPieces(int step) =>
        plan == null || step < 0 || step >= plan.Steps.Count ? new()
        : plan.Steps[step].Pieces.Select(id => plan.Pieces.FirstOrDefault(p => p.Id == id)).Where(p => p != null).ToList();

    bool PieceMode => settings.PieceByPiece && StepPieces(stepIdx).Count > 1;

    void Next()
    {
        if (plan == null) return;
        if (PieceMode && pieceIdx < StepPieces(stepIdx).Count - 1) GoTo(stepIdx, pieceIdx + 1);
        else GoTo(stepIdx + 1, 0);
    }

    void Prev()
    {
        if (plan == null) return;
        if (PieceMode && pieceIdx > 0) GoTo(stepIdx, pieceIdx - 1);
        else if (stepIdx > 0) GoTo(stepIdx - 1, settings.PieceByPiece ? int.MaxValue : 0);
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

    // ---------- Описание блока словами ----------

    static readonly string[] Dirs = { "правой", "нижней правой", "нижней", "нижней левой", "левой", "верхней левой", "верхней", "верхней правой" };

    string DirWord(Vector n)
    {
        var r = PlanRenderer.Rotate(n, settings.Rotation);
        var ang = Math.Atan2(r.Y, r.X) * 180 / Math.PI;           // 0 = вправо, 90 = вниз (ось Y вниз)
        int sector = (int)Math.Round(((ang % 360) + 360) % 360 / 45.0) % 8;
        return Dirs[sector];
    }

    string Describe(Piece p)
    {
        string floor = p.Level > 1 ? $" (этаж {p.Level})" : "";
        string text = p.Type switch
        {
            "foundation" or "triangle" => $"{p.TypeName}: ставь в подсвеченную клетку",
            "floor" => $"Перекрытие над подсвеченной клеткой{floor}",
            "stairs" => $"Лестница в подсвеченной клетке{floor}",
            "tc" => $"Шкаф (TC) в подсвеченную клетку{floor}",
            _ when p.IsEdgePiece => $"{p.TypeName} — на {DirWord(geo.EdgeNormal(p.On, p.Edge))} стороне клетки{floor}",
            _ => p.TypeName,
        };
        var extras = new[] { p.DoorName, p.LockName }.Where(x => x != null).ToList();
        if (extras.Count > 0) text += " + " + string.Join(", ", extras);
        if (p.Entrance) text += " — это вход";
        return text;
    }

    // ---------- Отрисовка ----------

    void Render()
    {
        Plan.Children.Clear();
        Hint.Text = locked
            ? $"{Key(HotkeyAction.Next)} далее · {Key(HotkeyAction.Prev)} назад · {Key(HotkeyAction.Rotate)} повернуть · " +
              $"{Key(HotkeyAction.Lock)} разблокировать · {Key(HotkeyAction.Settings)} настройки"
            : "Окно разблокировано: его можно тащить мышью. «Закрепить» — и клики будут проходить в игру.";
        if (hotkeyErrors.Count > 0) Hint.Text = string.Join("\n", hotkeyErrors) + "\n" + Hint.Text;

        if (plan == null)
        {
            TitleText.Text = "RustBuilder";
            StepCounter.Text = FloorText.Text = StepCost.Text = TotalCost.Text = "";
            StepText.Text = $"База не выбрана. Нажми «Базы» (или {Key(HotkeyAction.Library)}) и выбери, что строить.";
            PlanBox.Visibility = PieceBox.Visibility = Visibility.Collapsed;
            return;
        }
        PlanBox.Visibility = Visibility.Visible;

        TitleText.Text = plan.Name;
        StepCounter.Text = plan.Steps.Count > 0 ? $"шаг {stepIdx + 1}/{plan.Steps.Count}" : "";
        string[] rot = { "", " · схема повёрнута на 90°", " · схема повёрнута на 180°", " · схема повёрнута на 270°" };
        FloorText.Text = $"Этаж {viewFloor} из {plan.FloorCount}" + rot[settings.Rotation % 4];

        if (geo == null)
        {
            StepText.Text = planError;
            StepCost.Text = TotalCost.Text = "";
            PieceBox.Visibility = Visibility.Collapsed;
            return;
        }

        StepText.Text = plan.Steps.Count > 0 ? plan.Steps[stepIdx].Text : "";

        var pcs = StepPieces(stepIdx);
        if (PieceMode)
        {
            PieceBox.Visibility = Visibility.Visible;
            PieceCounter.Text = $"БЛОК {pieceIdx + 1} ИЗ {pcs.Count}" + (pieceIdx == pcs.Count - 1 ? " · последний в этом шаге" : "");
            PieceText.Text = Describe(pcs[pieceIdx]);
            if (viewFloor != pcs[pieceIdx].Level) PieceText.Text += $"\n(сейчас показан этаж {viewFloor})";
        }
        else if (pcs.Count == 1)
        {
            PieceBox.Visibility = Visibility.Visible;
            PieceCounter.Text = "СТАВИМ";
            PieceText.Text = Describe(pcs[0]);
        }
        else PieceBox.Visibility = Visibility.Collapsed;

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
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(() => SaveSnapshot((FrameworkElement)cat.Content, path)));
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
