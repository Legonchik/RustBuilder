using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RustBuilder;

public partial class SettingsWindow : Window
{
    public class Row : INotifyPropertyChanged
    {
        public HotkeyAction Action { get; init; }
        public string Name => AppSettings.ActionName(Action);
        string display;
        public string Display { get => display; set { display = value; PropertyChanged?.Invoke(this, new(nameof(Display))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    readonly AppSettings s = AppSettings.Current;
    readonly MainWindow overlay;
    readonly List<Row> rows;
    Row capturing;
    bool loading = true;

    public SettingsWindow(MainWindow overlay)
    {
        InitializeComponent();
        Anim.Window(this);
        this.overlay = overlay;
        rows = Enum.GetValues(typeof(HotkeyAction)).Cast<HotkeyAction>()
                   .Select(a => new Row { Action = a, Display = Hotkey.Display(s.GetHotkey(a)) }).ToList();
        HotkeyList.ItemsSource = rows;

        PieceByPiece.IsChecked = s.PieceByPiece;
        SimpleMode.IsChecked = s.SimpleMode;
        VoiceEnabled.IsChecked = s.VoiceEnabled;
        VolumeSlider.Value = s.VoiceVolume;
        if (!Voice.Available) { VoiceMissing.Visibility = Visibility.Visible; VoiceEnabled.IsEnabled = false; VoicePanel.IsEnabled = false; }
        ScaleSlider.Value = s.Scale;
        OpacitySlider.Value = s.BackgroundOpacity;
        RepoBox.Text = s.CatalogRepo ?? "";
        Animations.IsChecked = s.Animations;
        Splash.IsChecked = s.Splash;
        InteriorsEnabled.IsChecked = s.InteriorsEnabled;
        AutoUpdate.IsChecked = s.AutoUpdate;
        DiscordBox.IsChecked = s.DiscordPresence;
        SoundsBox.IsChecked = s.SoundsEnabled;
        VersionText.Text = $"Версия {Updater.Current}";
        BuildSwatches();
        BuildLegend();
        UpdateLabels();
        loading = false;

        PreviewKeyDown += OnPreviewKeyDown;
    }

    void UpdateLabels()
    {
        ScaleValue.Text = $"{ScaleSlider.Value * 100:0}%";
        VolumeValue.Text = $"{VolumeSlider.Value:0}";
        OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";
        RotationText.Text = $"Поворот схемы: {s.Rotation % 4 * 90}°";
    }

    // ---------- Назначение клавиш ----------

    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (capturing != null) capturing.Display = Hotkey.Display(s.GetHotkey(capturing.Action));
        capturing = (Row)((Button)sender).Tag;
        capturing.Display = "Нажми сочетание…";
        KeyMessage.Text = "";
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (capturing == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Hotkey.IsModifierKey(key)) return;     // ждём основную клавишу

        var row = capturing;
        capturing = null;

        if (key == Key.Escape) { row.Display = Hotkey.Display(s.GetHotkey(row.Action)); return; }
        if (key is Key.Back or Key.Delete)
        {
            s.SetHotkey(row.Action, "");
            row.Display = Hotkey.Display("");
            s.Save();
            return;
        }

        var mods = Keyboard.Modifiers;
        var combo = Hotkey.Format(mods, key);
        if (mods == ModifierKeys.None && !(key >= Key.F1 && key <= Key.F24))
            KeyMessage.Text = "Внимание: клавиша без Ctrl/Alt/Shift перестанет работать в игре для её обычного действия.";

        // одно сочетание — одно действие
        foreach (var other in rows.Where(r => r != row && s.GetHotkey(r.Action).Equals(combo, StringComparison.OrdinalIgnoreCase)))
        {
            s.SetHotkey(other.Action, "");
            other.Display = Hotkey.Display("");
            KeyMessage.Text = $"{Hotkey.Display(combo)} было у «{other.Name}» — там теперь пусто.";
        }

        s.SetHotkey(row.Action, combo);
        row.Display = Hotkey.Display(combo);
        s.Save();
    }

    void ResetKeys_Click(object sender, RoutedEventArgs e)
    {
        s.Hotkeys.Clear();
        foreach (var r in rows) r.Display = Hotkey.Display(s.GetHotkey(r.Action));
        s.Save();
        KeyMessage.Text = "Клавиши сброшены.";
    }

    // ---------- Внешний вид ----------

    void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        s.PieceByPiece = PieceByPiece.IsChecked == true;
        s.SimpleMode = SimpleMode.IsChecked == true;
        s.VoiceEnabled = VoiceEnabled.IsChecked == true;
        s.Animations = Animations.IsChecked == true;
        s.Splash = Splash.IsChecked == true;
        bool interiorsChanged = s.InteriorsEnabled != (InteriorsEnabled.IsChecked == true);
        s.InteriorsEnabled = InteriorsEnabled.IsChecked == true;
        if (interiorsChanged) overlay.ReapplyInterior();
        s.AutoUpdate = AutoUpdate.IsChecked == true;
        s.DiscordPresence = DiscordBox.IsChecked == true;
        s.SoundsEnabled = SoundsBox.IsChecked == true;
        if (!s.DiscordPresence) DiscordPresence.Clear();
        if (!s.VoiceEnabled) Voice.Stop();
        s.Save();
        overlay.SettingsChanged();
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        s.Scale = ScaleSlider.Value;
        s.BackgroundOpacity = OpacitySlider.Value;
        s.VoiceVolume = (int)VolumeSlider.Value;
        s.Save();
        UpdateLabels();
        overlay.SettingsChanged();
    }

    // ---------- Цвет, легенда, обновления ----------

    void BuildSwatches()
    {
        ColorSwatches.Children.Clear();
        foreach (var (name, hex) in Theme.Presets)
        {
            var col = Theme.Parse(hex);
            bool sel = string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Fill = new SolidColorBrush(col), Margin = new Thickness(0, 0, 6, 0),
                Stroke = sel ? Brushes.White : null, StrokeThickness = 2,
            });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 4, 10, 4) };
            if (sel) b.Background = new SolidColorBrush(Color.FromArgb(0x66, col.R, col.G, col.B));
            b.Click += (_, _) =>
            {
                s.AccentColor = hex;
                s.Save();
                Theme.Apply(hex);
                BuildSwatches();
                BuildLegend();
                overlay.SettingsChanged();
            };
            ColorSwatches.Children.Add(b);
        }
    }

    void BuildLegend()
    {
        LegendList.Children.Clear();
        foreach (var (kind, title, text) in PlanRenderer.Legend)
        {
            var sample = new Canvas { Width = 76, Height = 46, ClipToBounds = true };
            PlanRenderer.DrawLegend(sample, kind);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var box = new Border
            {
                Child = sample, Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
            txt.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(txt);
            LegendList.Children.Add(row);
        }
    }

    void ShowSplash_Click(object sender, RoutedEventArgs e) => new SplashWindow().Show();

    void Support_Click(object sender, RoutedEventArgs e) => Support.Open();

    async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await UpdateWindow.CheckAndOffer(manual: true, owner: this);

    void TestVoice_Click(object sender, RoutedEventArgs e) => Voice.Say("Стена. Сверху. Металлическая дверь и кодовый замок.", s.VoiceVolume);

    void Rotate_Click(object sender, RoutedEventArgs e)
    {
        s.Rotation = (s.Rotation + 1) % 4;
        s.Save();
        UpdateLabels();
        overlay.SettingsChanged();
    }

    void Repo_LostFocus(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim().TrimEnd('/');
        const string gh = "https://github.com/";
        if (repo.StartsWith(gh, StringComparison.OrdinalIgnoreCase)) repo = repo[gh.Length..];
        if (repo.EndsWith(".git")) repo = repo[..^4];
        RepoBox.Text = repo;
        s.CatalogRepo = repo;
        s.Save();
    }

    void Done_Click(object sender, RoutedEventArgs e)
    {
        Repo_LostFocus(null, null);
        Close();
    }
}
