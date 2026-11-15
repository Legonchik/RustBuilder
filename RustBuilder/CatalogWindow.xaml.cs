using System.Windows;
using System.Windows.Controls;

namespace RustBuilder;

public partial class CatalogWindow : Window
{
    /// Вызывается после скачивания базы (путь к сохранённому файлу)
    public event Action<string> Downloaded;

    readonly AppSettings settings = AppSettings.Current;
    List<CatalogEntry> entries = new();
    readonly Dictionary<string, (BuildPlan plan, PlanGeometry geo, string json)> cache = new();
    int previewFloor = 1;
    int loadToken;

    public CatalogWindow()
    {
        InitializeComponent();
        RepoBox.Text = settings.CatalogRepo ?? "";
        Loaded += async (_, _) => await LoadIndex();
    }

    CatalogEntry Selected => List.SelectedItem as CatalogEntry;

    async Task LoadIndex()
    {
        List.ItemsSource = null;
        cache.Clear();
        ClearPreview();
        if (!Catalog.IsConfigured(settings.CatalogRepo))
        {
            StatusText.Text = "Каталог не настроен.\nВпиши внизу слева репозиторий GitHub в виде «владелец/репозиторий» и нажми OK.";
            return;
        }
        StatusText.Text = "Загружаю каталог…";
        try
        {
            var idx = await Catalog.LoadIndex(settings.CatalogRepo, settings.CatalogBranch);
            entries = idx.Bases ?? new();
            ApplyFilter();
            StatusText.Text = entries.Count == 0 ? "В каталоге пока нет баз. Будь первым!" : "Выбери базу слева";
            if (entries.Count > 0) List.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить каталог:\n" + ex.Message;
        }
    }

    void ApplyFilter()
    {
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.ItemsSource = q.Length == 0
            ? entries
            : entries.Where(e => (e.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                              || (e.Author ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        previewFloor = 1;
        await ShowSelected();
    }

    void ClearPreview()
    {
        Preview.Children.Clear();
        FloorTabs.Children.Clear();
        NameText.Text = DescText.Text = StatsText.Text = CostText.Text = "";
        DownloadBtn.IsEnabled = false;
    }

    async Task ShowSelected()
    {
        var e = Selected;
        int token = ++loadToken;
        ClearPreview();
        if (e == null) return;

        NameText.Text = e.Name;
        DescText.Text = e.Description;
        StatsText.Text = $"Автор: {(string.IsNullOrEmpty(e.Author) ? "—" : e.Author)}" +
                         (string.IsNullOrEmpty(e.Added) ? "" : $" · добавлена {e.Added}") +
                         $"\nФундаментов: {e.Foundations} · этажей: {e.Floors} · шагов: {e.Steps}";

        if (!cache.TryGetValue(e.File, out var item))
        {
            StatusText.Text = "Загружаю схему…";
            try
            {
                var json = await Catalog.LoadBase(settings.CatalogRepo, settings.CatalogBranch, e);
                var tmp = System.IO.Path.GetTempFileName();
                try
                {
                    System.IO.File.WriteAllText(tmp, json);
                    var plan = BuildPlan.Load(tmp);
                    item = (plan, new PlanGeometry(plan), json);
                }
                finally { System.IO.File.Delete(tmp); }
                cache[e.File] = item;
            }
            catch (Exception ex)
            {
                if (token == loadToken) StatusText.Text = "Не удалось загрузить схему:\n" + ex.Message;
                return;
            }
        }
        if (token != loadToken) return; // пока грузили, выбрали другую базу

        StatusText.Text = "";
        for (int f = 1; f <= item.plan.FloorCount; f++)
        {
            int floor = f;
            var b = new Button { Content = $"Этаж {f}", Margin = new Thickness(0, 0, 6, 0) };
            if (f == previewFloor) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += async (_, _) => { previewFloor = floor; await ShowSelected(); };
            FloorTabs.Children.Add(b);
        }
        PlanRenderer.Draw(Preview, item.plan, item.geo, _ => PieceState.Done, previewFloor);
        CostText.Text = $"Всего (до {item.plan.Tier}): {item.plan.TotalCost(item.geo)}";
        DownloadBtn.IsEnabled = true;
    }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        var entry = Selected;
        if (entry == null || !cache.TryGetValue(entry.File, out var item)) return;
        try
        {
            var path = BuildLibrary.ImportJson(item.json, System.IO.Path.GetFileName(entry.File));
            Downloaded?.Invoke(path);
            MessageBox.Show(this, $"База «{entry.Name}» добавлена в «Мои базы».", "RustBuilder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить базу:\n" + ex.Message, "RustBuilder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadIndex();

    async void SaveRepo_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim().TrimEnd('/');
        // позволяем вставить полную ссылку https://github.com/владелец/репозиторий
        const string gh = "https://github.com/";
        if (repo.StartsWith(gh, StringComparison.OrdinalIgnoreCase)) repo = repo[gh.Length..];
        if (repo.EndsWith(".git")) repo = repo[..^4];
        RepoBox.Text = repo;
        settings.CatalogRepo = repo;
        settings.Save();
        await LoadIndex();
    }
}
