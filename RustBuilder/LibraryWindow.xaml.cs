using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace RustBuilder;

public partial class LibraryWindow : Window
{
    public class Item
    {
        public BuildPlan Plan { get; init; }
        public PlanGeometry Geo { get; init; }
        public string Error { get; init; }
        public string Title => Plan.Name;
        public string Subtitle
        {
            get
            {
                var src = Plan.IsUser ? "моя" : "встроенная";
                if (Error != null) return $"{src} · ⚠ ошибка в файле";
                int f = Plan.Pieces.Count(p => p.IsFoundation);
                return $"{src} · {f} фунд. · {Plan.FloorCount} эт. · {Plan.Steps.Count} шагов";
            }
        }
    }

    /// Вызывается, когда пользователь выбрал базу для постройки
    public event Action<BuildPlan> Chosen;

    List<Item> items = new();
    int previewFloor = 1;

    public LibraryWindow(string selectPath = null)
    {
        InitializeComponent();
        Reload(selectPath);
    }

    Item Selected => List.SelectedItem as Item;

    public void Reload(string selectFullPath = null)
    {
        var errors = new List<string>();
        items = BuildLibrary.LoadAll(errors).Select(p =>
        {
            try { return new Item { Plan = p, Geo = new PlanGeometry(p) }; }
            catch (Exception ex) { return new Item { Plan = p, Error = ex.Message }; }
        }).ToList();
        ApplyFilter();

        var toSelect = items.FirstOrDefault(i => i.Plan.FullPath == selectFullPath) ?? items.FirstOrDefault();
        if (toSelect != null && List.Items.Contains(toSelect)) List.SelectedItem = toSelect;
        if (errors.Count > 0) ErrorText.Text = "Не загрузились: " + string.Join("; ", errors);
    }

    void ApplyFilter()
    {
        var q = Search.Text.Trim();
        List.ItemsSource = string.IsNullOrEmpty(q)
            ? items
            : items.Where(i => i.Plan.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        previewFloor = 1;
        ShowSelected();
    }

    void ShowSelected()
    {
        var it = Selected;
        ChooseBtn.IsEnabled = ExportBtn.IsEnabled = it != null && it.Error == null;
        DeleteBtn.IsEnabled = it?.Plan.IsUser == true;
        UploadBtn.IsEnabled = it?.Plan.IsUser == true && it.Error == null;
        FloorTabs.Children.Clear();
        Preview.Children.Clear();

        if (it == null)
        {
            NameText.Text = "Нет баз";
            DescText.Text = "Нажми «Загрузить свою базу…», чтобы добавить .json-файл.";
            StatsText.Text = CostText.Text = ErrorText.Text = "";
            return;
        }

        NameText.Text = it.Plan.Name;
        DescText.Text = it.Plan.Description;
        ErrorText.Text = it.Error != null ? "Ошибка: " + it.Error : "";
        if (it.Error != null) { StatsText.Text = CostText.Text = ""; return; }

        int floors = it.Plan.FloorCount;
        for (int f = 1; f <= floors; f++)
        {
            int floor = f;
            var b = new Button { Content = $"Этаж {f}", Margin = new Thickness(0, 0, 6, 0) };
            if (f == previewFloor) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += (_, _) => { previewFloor = floor; ShowSelected(); };
            FloorTabs.Children.Add(b);
        }

        PlanRenderer.Draw(Preview, it.Plan, it.Geo, _ => PieceState.Done, previewFloor);

        int f1 = it.Plan.Pieces.Count(p => p.IsFoundation);
        int walls = it.Plan.Pieces.Count(p => p.IsEdgePiece);
        int doors = it.Plan.Pieces.Count(p => p.Door != null);
        StatsText.Text = $"Фундаментов: {f1} · стен и проёмов: {walls} · дверей: {doors} · этажей: {floors} · шагов: {it.Plan.Steps.Count}" +
                         $"\nФайл: {it.Plan.FileName} ({(it.Plan.IsUser ? "моя база" : "встроенная")})";
        CostText.Text = $"Всего (до {it.Plan.Tier}): {it.Plan.TotalCost(it.Geo)}";
    }

    void Choose()
    {
        var it = Selected;
        if (it == null || it.Error != null) return;
        Chosen?.Invoke(it.Plan);
        Close();
    }

    void Choose_Click(object sender, RoutedEventArgs e) => Choose();

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Схема базы RustBuilder (*.json)|*.json", Multiselect = true, Title = "Выбери файлы баз" };
        if (dlg.ShowDialog(this) != true) return;

        string last = null;
        var failed = new List<string>();
        foreach (var f in dlg.FileNames)
        {
            try { last = BuildLibrary.Import(f); }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        Reload(last);
        if (failed.Count > 0)
            MessageBox.Show(this, "Не удалось загрузить:\n\n" + string.Join("\n", failed), "RustBuilder", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected;
        if (it == null) return;
        var dlg = new SaveFileDialog { Filter = "Схема базы RustBuilder (*.json)|*.json", FileName = it.Plan.FileName };
        if (dlg.ShowDialog(this) == true) File.Copy(it.Plan.FullPath, dlg.FileName, overwrite: true);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected;
        if (it == null || !it.Plan.IsUser) return;
        if (MessageBox.Show(this, $"Удалить базу «{it.Plan.Name}»? Файл будет удалён без возможности восстановления.",
                "RustBuilder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BuildLibrary.Delete(it.Plan);
        Reload();
    }

    CatalogWindow catalog;

    void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (catalog != null) { catalog.Activate(); return; }
        catalog = new CatalogWindow { Owner = this };
        catalog.Downloaded += path => Reload(path);
        catalog.Closed += (_, _) => catalog = null;
        catalog.Show();
    }

    void Upload_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected;
        if (it == null || !it.Plan.IsUser || it.Error != null) return;
        var s = AppSettings.Current;
        if (!Catalog.IsGitHubRepo(s.CatalogRepo))
        {
            MessageBox.Show(this, "Каталог не настроен. Открой «Каталог онлайн…» и впиши репозиторий GitHub.", "RustBuilder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var json = File.ReadAllText(it.Plan.FullPath);
        var (url, inUrl) = Catalog.UploadUrl(s.CatalogRepo, s.CatalogBranch, Catalog.FileNameFor(it.Plan), json);
        Clipboard.SetText(json);

        var msg = "Сейчас откроется GitHub с новым файлом в каталоге.\n\n" +
                  "1. Войди в GitHub, если попросит.\n" +
                  (inUrl ? "2. Файл уже заполнен" : "2. Вставь содержимое базы в большое поле (Ctrl+V), оно уже в буфере обмена") +
                  ". Если поле пустое, тоже вставь через Ctrl+V.\n" +
                  "3. Нажми «Propose changes» или «Commit changes», затем «Create pull request».\n\n" +
                  "После проверки база появится в каталоге у всех.";
        if (MessageBox.Show(this, msg, "Выложить в каталог", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.UserBuildsDir);
        Process.Start("explorer.exe", AppSettings.UserBuildsDir);
    }
}
