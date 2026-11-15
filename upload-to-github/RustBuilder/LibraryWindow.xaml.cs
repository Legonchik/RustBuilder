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
        public string Badge => Authors.BadgeText(Plan.Author);
        public System.Windows.Media.Brush BadgeBrush => Authors.BadgeBrush(Plan.Author);
        public Visibility BadgeVisible => Badge == null ? Visibility.Collapsed : Visibility.Visible;
        public string Subtitle
        {
            get
            {
                var src = Plan.IsUser ? "моя" : "встроенная";
                if (Error != null) return $"{src} · ⚠ ошибка в файле";
                int f = Plan.Pieces.Count(p => p.IsFoundation);
                return $"{src} · {f} фунд. · {Plan.FloorCount} эт." + (Plan.Interiors.Count > 0 ? $" · интерьеров: {Plan.Interiors.Count}" : $" · {Plan.Steps.Count} шагов");
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
        Anim.Window(this);
        UpdateProfileBtn();
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
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ChooseBtn.IsEnabled = ExportBtn.IsEnabled = EditBtn.IsEnabled = it != null && it.Error == null;
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
        InteriorTabs.Children.Clear();
        InteriorBox.Visibility = it.Error == null && it.Plan.Interiors.Count > 0 && AppSettings.Current.InteriorsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        if (it.Error != null) { StatsText.Text = CostText.Text = ""; return; }

        // интерьер: выбор сохраняется для этой базы и подмешивается в постройку
        var settings = AppSettings.Current;
        int chosen = settings.GetInterior(it.Plan);
        void Tab(string title, int index)
        {
            var b = new Button { Content = title, Margin = new Thickness(0, 0, 6, 6) };
            if (index == chosen) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += (_, _) => { settings.SetInterior(it.Plan, index); settings.Save(); ShowSelected(); };
            InteriorTabs.Children.Add(b);
        }
        for (int i = 0; i < it.Plan.Interiors.Count; i++) Tab(it.Plan.Interiors[i].Name, i);
        if (it.Plan.Interiors.Count > 0) Tab("Без мебели", -1);
        InteriorDesc.Text = chosen >= 0 && chosen < it.Plan.Interiors.Count ? it.Plan.Interiors[chosen].Description : "Только стройка, мебель расставишь сам.";

        var view = it.Plan.WithInterior(chosen);
        PlanGeometry viewGeo;
        try { viewGeo = new PlanGeometry(view); }
        catch (Exception ex) { ErrorText.Text = "Ошибка в интерьере: " + ex.Message; view = it.Plan; viewGeo = it.Geo; }

        int floors = view.FloorCount;
        for (int f = 1; f <= floors; f++)
        {
            int floor = f;
            var b = new Button { Content = $"Этаж {f}", Margin = new Thickness(0, 0, 6, 0) };
            if (f == previewFloor) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += (_, _) => { previewFloor = floor; ShowSelected(); };
            FloorTabs.Children.Add(b);
        }

        previewFloor = Math.Min(previewFloor, floors);
        PlanRenderer.Draw(Preview, view, viewGeo, _ => PieceState.Done, previewFloor);
        Anim.PlanIn(Preview);

        int f1 = view.Pieces.Count(p => p.IsFoundation);
        int walls = view.Pieces.Count(p => p.IsEdgePiece);
        int doors = view.Pieces.Count(p => p.Door != null);
        int items = view.Pieces.Count(p => p.IsItem);
        StatsText.Text = $"Фундаментов: {f1} · стен и проёмов: {walls} · дверей: {doors} · мебели: {items} · этажей: {floors} · шагов: {view.Steps.Count}" +
                         $"\nФайл: {it.Plan.FileName} ({(it.Plan.IsUser ? "моя база" : "встроенная")})";
        CostText.Text = $"Всего (до {it.Plan.Tier}): {view.TotalCost(viewGeo)}";
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
        catalog.BuildRequested += path => { Reload(path); Choose(); };
        catalog.Closed += (_, _) => catalog = null;
        catalog.Show();
    }

    void Upload_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected;
        if (it == null || !it.Plan.IsUser || it.Error != null) return;
        Publish(this, it.Plan, File.ReadAllText(it.Plan.FullPath));
    }

    /// Открывает GitHub с готовым файлом базы в каталоге (pull request подтверждает сам пользователь)
    public static void Publish(Window owner, BuildPlan plan, string json)
    {
        var s = AppSettings.Current;
        if (!Catalog.IsGitHubRepo(s.CatalogRepo))
        {
            MessageBox.Show(owner, "Каталог не настроен. Открой «Каталог онлайн…» и впиши репозиторий GitHub.", "RustBuilder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Authors.IsClaude(plan.Author) && !plan.IsUser)
        {
            MessageBox.Show(owner, "Это встроенная база Claude — она уже есть у всех. Сделай свою версию: «✏ Изменить», и её можно выложить.", "RustBuilder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var nick = Profile.Require(owner);
        if (nick == null) return;
        // автор базы в каталоге — ник из профиля
        var node = System.Text.Json.Nodes.JsonNode.Parse(json).AsObject();
        node["author"] = nick;
        plan.Author = nick;
        json = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var (url, inUrl) = Catalog.UploadUrl(s.CatalogRepo, s.CatalogBranch, Catalog.FileNameFor(plan), json);
        Clipboard.SetText(json);

        var msg = "Сейчас откроется GitHub с новым файлом в каталоге.\n\n" +
                  "1. Войди в GitHub, если попросит.\n" +
                  (inUrl ? "2. Файл уже заполнен" : "2. Вставь содержимое базы в большое поле (Ctrl+V), оно уже в буфере обмена") +
                  ". Если поле пустое, тоже вставь через Ctrl+V.\n" +
                  "3. Нажми «Propose changes» или «Commit changes», затем «Create pull request».\n\n" +
                  "После проверки база появится в каталоге у всех.";
        if (MessageBox.Show(owner, msg, "Выложить в каталог", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void UpdateProfileBtn() =>
        ProfileBtn.Content = string.IsNullOrWhiteSpace(AppSettings.Current.Nickname) ? "👤 Профиль" : "👤 " + AppSettings.Current.Nickname;

    void Profile_Click(object sender, RoutedEventArgs e) { Profile.Ask(this); UpdateProfileBtn(); }

    void Support_Click(object sender, RoutedEventArgs e) => Support.Open();

    EditorWindow editor;

    void OpenEditor(BuildPlan source)
    {
        if (editor != null) { editor.Activate(); return; }
        editor = new EditorWindow(source) { Owner = this };
        editor.Saved += path => Reload(path);
        editor.Closed += (_, _) => editor = null;
        editor.Show();
    }

    void NewBase_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        var it = Selected;
        if (it != null && it.Error == null) OpenEditor(it.Plan);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.UserBuildsDir);
        Process.Start("explorer.exe", AppSettings.UserBuildsDir);
    }
}
