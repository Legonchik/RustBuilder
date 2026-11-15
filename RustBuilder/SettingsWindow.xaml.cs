using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        this.overlay = overlay;
        rows = Enum.GetValues(typeof(HotkeyAction)).Cast<HotkeyAction>()
                   .Select(a => new Row { Action = a, Display = Hotkey.Display(s.GetHotkey(a)) }).ToList();
        HotkeyList.ItemsSource = rows;

        PieceByPiece.IsChecked = s.PieceByPiece;
        ScaleSlider.Value = s.Scale;
        OpacitySlider.Value = s.BackgroundOpacity;
        RepoBox.Text = s.CatalogRepo ?? "";
        UpdateLabels();
        loading = false;

        PreviewKeyDown += OnPreviewKeyDown;
    }

    void UpdateLabels()
    {
        ScaleValue.Text = $"{ScaleSlider.Value * 100:0}%";
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
        s.Save();
        overlay.SettingsChanged();
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        s.Scale = ScaleSlider.Value;
        s.BackgroundOpacity = OpacitySlider.Value;
        s.Save();
        UpdateLabels();
        overlay.SettingsChanged();
    }

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
