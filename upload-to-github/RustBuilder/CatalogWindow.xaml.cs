using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RustBuilder;

/// Каталог баз: карточки с превью, лайки (👍 на странице базы на GitHub), «Копировать» и «Строить».
public partial class CatalogWindow : Window
{
    /// База скопирована в «Мои базы» (путь к файлу)
    public event Action<string> Downloaded;
    /// Нажали «Строить»: база уже скопирована, её надо открыть в оверлее
    public event Action<string> BuildRequested;

    enum Sort { Popular, New }
    enum Filter { All, Players, Claude }

    readonly AppSettings settings = AppSettings.Current;
    List<CatalogEntry> entries = new();
    Dictionary<string, List<VideoGuide>> videos = new();
    readonly Dictionary<string, (BuildPlan plan, PlanGeometry geo, string json)> cache = new();
    readonly Dictionary<string, string> copied = new();          // файл каталога → путь в «Моих базах» (в этой сессии)
    readonly Dictionary<string, TextBlock> likeLabels = new();
    readonly SemaphoreSlim loadGate = new(4);
    Sort sort = Sort.Popular;
    Filter filter = Filter.All;
    bool refreshLikesOnReturn;
    int loadToken;
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };

    public CatalogWindow()
    {
        InitializeComponent();
        Anim.Window(this);
        RepoBox.Text = settings.CatalogRepo ?? "";
        BuildChips();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))); };
        Loaded += async (_, _) => await LoadIndex();
        // вернулись из браузера после лайка — подтягиваем свежие счётчики
        Activated += async (_, _) =>
        {
            if (!refreshLikesOnReturn) return;
            refreshLikesOnReturn = false;
            await Task.Delay(1500);
            await RefreshLikes(fresh: true);
        };
        SizeChanged += (_, _) => FitCards();
    }

    // ---------- Сортировка и фильтр ----------

    void BuildChips()
    {
        void Chips<T>(Panel panel, (T value, string title)[] items, Func<T> get, Action<T> set)
        {
            panel.Children.Clear();
            foreach (var (value, title) in items)
            {
                var b = new Button { Content = title, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
                if (EqualityComparer<T>.Default.Equals(value, get())) b.Style = (Style)FindResource("PrimaryButton");
                b.Click += (_, _) => { set(value); BuildChips(); ShowCards(); };
                panel.Children.Add(b);
            }
        }
        Chips(SortPanel, new[] { (Sort.Popular, "🔥 Популярные"), (Sort.New, "🆕 Новые") }, () => sort, v => sort = v);
        Chips(FilterPanel, new[] { (Filter.All, "Все"), (Filter.Players, "👤 Игроков"), (Filter.Claude, "✳ Claude") }, () => filter, v => filter = v);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowCards();
    }

    IEnumerable<CatalogEntry> Visible()
    {
        var q = Search.Text.Trim();
        var list = entries.Where(e => filter switch
        {
            Filter.Claude => Authors.IsClaude(e.Author),
            Filter.Players => !Authors.IsClaude(e.Author),
            _ => true,
        });
        if (q.Length > 0)
            list = list.Where(e => (e.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                || (e.Author ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        return sort == Sort.Popular
            ? list.OrderByDescending(e => e.Likes).ThenByDescending(e => e.Added ?? "").ThenBy(e => e.Name)
            : list.OrderByDescending(e => e.Added ?? "").ThenByDescending(e => e.Likes).ThenBy(e => e.Name);
    }

    // ---------- Загрузка ----------

    async Task LoadIndex()
    {
        Cards.Children.Clear();
        cache.Clear();
        CountText.Text = "";
        if (!Catalog.IsConfigured(settings.CatalogRepo))
        {
            StatusText.Text = "Каталог не настроен.\nВпиши внизу справа репозиторий GitHub в виде «владелец/репозиторий» и нажми OK.";
            return;
        }
        StatusText.Text = "Загружаю каталог…";
        try
        {
            var idx = await Catalog.LoadIndex(settings.CatalogRepo, settings.CatalogBranch);
            entries = idx.Bases ?? new();
            StatusText.Text = entries.Count == 0 ? "В каталоге пока нет баз. Будь первым — выложи свою из редактора!" : "";
            await RefreshLikes(fresh: false, redraw: false);
            videos = await Catalog.LoadVideos(settings.CatalogRepo, settings.CatalogBranch);
            ShowCards();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить каталог:\n" + ex.Message;
            Sfx.Error();
        }
    }

    async Task RefreshLikes(bool fresh, bool redraw = true)
    {
        var likes = await Catalog.LoadLikes(settings.CatalogRepo, fresh);
        foreach (var e in entries) e.Likes = e.Issue is int n && likes.TryGetValue(n, out var c) ? c : 0;
        if (!redraw) return;
        foreach (var e in entries)
            if (likeLabels.TryGetValue(e.File, out var tb)) tb.Text = LikeText(e);
    }

    async Task<(BuildPlan plan, PlanGeometry geo, string json)?> LoadPlan(CatalogEntry e)
    {
        if (cache.TryGetValue(e.File, out var item)) return item;
        await loadGate.WaitAsync();
        try
        {
            if (cache.TryGetValue(e.File, out item)) return item;
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
            return item;
        }
        catch { return null; }
        finally { loadGate.Release(); }
    }

    // ---------- Карточки ----------

    const double CardW = 300;

    void FitCards()
    {
        // карточки растягиваются, чтобы ряд заполнял ширину без пустого хвоста справа
        double avail = Scroll.ActualWidth - 20;
        if (avail <= 0) return;
        int cols = Math.Max(1, (int)(avail / (CardW + 12)));
        double w = Math.Floor(avail / cols) - 12;
        foreach (FrameworkElement c in Cards.Children) c.Width = w;
    }

    void ShowCards()
    {
        int token = ++loadToken;
        Cards.Children.Clear();
        likeLabels.Clear();
        var list = Visible().ToList();
        CountText.Text = entries.Count == 0 ? "" : $"баз: {list.Count} из {entries.Count}";
        if (entries.Count > 0) StatusText.Text = list.Count == 0 ? "Ничего не нашлось." : "";
        int i = 0;
        foreach (var e in list)
        {
            var card = Card(e, token);
            Cards.Children.Add(card);
            // карточки появляются по очереди
            card.Opacity = 0;
            card.RenderTransform = new TranslateTransform(0, 12);
            var delay = TimeSpan.FromMilliseconds(Math.Min(i++, 12) * 35);
            if (!settings.Animations || AppSettings.ReadOnly) { card.Opacity = 1; card.RenderTransform = null; continue; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = delay });
            card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = ease });
        }
        FitCards();
    }

    static string LikeText(CatalogEntry e) => (AppSettings.Current.LikedBases.Contains(e.File) ? "♥ " : "♡ ") + e.Likes;

    FrameworkElement Card(CatalogEntry e, int token)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var root = new Border
        {
            Width = CardW, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        root.MouseEnter += (_, _) => root.BorderBrush = accent;
        root.MouseLeave += (_, _) => root.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        var stack = new StackPanel();
        root.Child = stack;

        // превью схемы: клик — следующий этаж
        var canvas = new Canvas { Width = 276, Height = 160, ClipToBounds = true };
        var floorLabel = new TextBlock { FontSize = 10, Opacity = 0.6, Margin = new Thickness(8, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var loading = new TextBlock { Text = "загружаю схему…", Opacity = 0.45, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
            Child = new Grid { Children = { new Viewbox { Child = canvas, Stretch = Stretch.Uniform }, floorLabel, loading } },
            ToolTip = "Клик — следующий этаж", Height = 160,
        };
        stack.Children.Add(previewBox);
        int floor = 1;
        void Draw(BuildPlan plan, PlanGeometry geo)
        {
            PlanRenderer.Draw(canvas, plan, geo, _ => PieceState.Done, floor, maxScale: 34);
            floorLabel.Text = plan.FloorCount > 1 ? $"этаж {floor} из {plan.FloorCount}" : "";
        }
        async Task LoadAndDraw()
        {
            var item = await LoadPlan(e);
            if (token != loadToken) return;
            if (item is not { } it) { loading.Text = "не удалось загрузить схему"; return; }
            loading.Visibility = Visibility.Collapsed;
            Draw(it.plan, it.geo);
            previewBox.MouseLeftButtonUp += (_, _) => { floor = floor % it.plan.FloorCount + 1; Draw(it.plan, it.geo); };
        }
        _ = LoadAndDraw();

        // название + автор
        var title = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var badgeText = Authors.BadgeText(e.Author);
        if (badgeText != null)
        {
            var badge = new Border
            {
                Background = Authors.BadgeBrush(e.Author), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = badgeText, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            DockPanel.SetDock(badge, Dock.Right);
            title.Children.Add(badge);
        }
        title.Children.Add(new TextBlock { Text = e.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = e.Name });
        stack.Children.Add(title);
        // характеристики + кнопка видео-гайдов
        var stats = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        int vc = VideosFor(e).Count;
        var vid = new Button
        {
            Content = vc > 0 ? $"🎬 {vc} видео" : "🎬 ＋ видео", FontSize = 11, Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = vc > 0 ? "Видео-гайды к этой базе" : "Видео пока нет — предложи своё",
        };
        if (vc > 0) vid.Foreground = (Brush)FindResource("AccentLightBrush");
        vid.Click += (_, _) => OpenVideos(e);
        DockPanel.SetDock(vid, Dock.Right);
        stats.Children.Add(vid);
        stats.Children.Add(new TextBlock
        {
            Text = $"{e.Foundations} фунд. · {e.Floors} эт. · {e.Steps} шагов · до {TierName(e.Tier)}" + (string.IsNullOrEmpty(e.Added) ? "" : $" · {e.Added}"),
            FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(stats);
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(e.Description) ? " " : e.Description, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, Height = 34, Margin = new Thickness(0, 5, 0, 0), ToolTip = e.Description,
        });

        // кнопки: ♥ лайк · Копировать · Строить
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var likeText = new TextBlock { Text = LikeText(e), FontSize = 14, FontWeight = FontWeights.SemiBold };
        likeLabels[e.File] = likeText;
        var like = new Button
        {
            Content = likeText, Padding = new Thickness(10, 4, 10, 4),
            ToolTip = e.Issue == null ? "Лайки для этой базы появятся после ближайшего обновления каталога" : "Открыть страницу базы на GitHub и поставить 👍",
        };
        if (AppSettings.Current.LikedBases.Contains(e.File)) likeText.Foreground = (Brush)FindResource("AccentLightBrush");
        like.Click += (_, _) => Like(e, likeText);
        var build = new Button { Content = "▶ Строить", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        build.Click += async (_, _) => await Build(e);
        var copy = new Button { Content = copied.ContainsKey(e.File) ? "✓ У меня" : "⧉ Копировать", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Скопировать в «Мои базы» — можно изменить в редакторе" };
        copy.Click += async (_, _) => { if (await Copy(e) != null) copy.Content = "✓ У меня"; };
        DockPanel.SetDock(like, Dock.Left);
        DockPanel.SetDock(build, Dock.Right);
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(like);
        row.Children.Add(build);
        row.Children.Add(copy);
        stack.Children.Add(row);
        return root;
    }

    static string TierName(string tier) => (tier ?? "stone").ToLower() switch
    {
        "twig" => "соломы", "wood" => "дерева", "metal" => "металла", "hqm" => "МВК", _ => "камня",
    };

    // ---------- Действия ----------

    List<VideoGuide> VideosFor(CatalogEntry e) => e.File != null && videos.TryGetValue(e.File, out var l) ? l : new();

    public Window OpenVideos(CatalogEntry e)
    {
        if (!Catalog.IsGitHubRepo(settings.CatalogRepo) && VideosFor(e).Count == 0 && !AppSettings.ReadOnly)
        {
            ShowToast("Видео-гайды работают только с каталогом на GitHub.");
            return null;
        }
        var w = new VideosWindow(e, VideosFor(e), Catalog.IsGitHubRepo(settings.CatalogRepo) ? settings.CatalogRepo : Catalog.DefaultRepo) { Owner = this };
        w.Show();
        return w;
    }

    /// Для снимков: открыть видео первой базы, у которой они есть
    public Window OpenFirstVideos() => entries.FirstOrDefault(e => VideosFor(e).Count > 0) is { } e ? OpenVideos(e) : null;

    void Like(CatalogEntry e, TextBlock label)
    {
        if (e.Issue is not int issue || !Catalog.IsGitHubRepo(settings.CatalogRepo))
        {
            ShowToast("Лайки для этой базы появятся после ближайшего обновления каталога.");
            Sfx.Error();
            return;
        }
        Sfx.Like();
        if (!settings.LikedBases.Contains(e.File)) { settings.LikedBases.Add(e.File); settings.Save(); }
        label.Text = LikeText(e);
        label.Foreground = (Brush)FindResource("AccentLightBrush");
        Pulse(label);
        refreshLikesOnReturn = true;
        try { Process.Start(new ProcessStartInfo(Catalog.LikeUrl(settings.CatalogRepo, issue)) { UseShellExecute = true }); }
        catch { }
        ShowToast("Открыл страницу базы на GitHub — нажми там 👍. Вернёшься сюда — счётчик обновится.");
    }

    async Task<string> Copy(CatalogEntry e, bool quiet = false)
    {
        if (copied.TryGetValue(e.File, out var done)) { if (!quiet) ShowToast($"«{e.Name}» уже в «Моих базах»."); return done; }
        var item = await LoadPlan(e);
        if (item == null) { ShowToast("Не удалось загрузить схему. Проверь интернет."); Sfx.Error(); return null; }
        try
        {
            var path = BuildLibrary.ImportJson(item.Value.json, System.IO.Path.GetFileName(e.File));
            copied[e.File] = path;
            Downloaded?.Invoke(path);
            if (!quiet) { Sfx.Copy(); ShowToast($"«{e.Name}» скопирована в «Мои базы». Её можно изменить в редакторе."); }
            return path;
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось сохранить базу: " + ex.Message);
            Sfx.Error();
            return null;
        }
    }

    async Task Build(CatalogEntry e)
    {
        var path = await Copy(e, quiet: true);
        if (path == null) return;
        Sfx.Build();
        BuildRequested?.Invoke(path);
    }

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        toastTimer.Stop();
        toastTimer.Start();
    }

    static void Pulse(FrameworkElement el)
    {
        var st = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        var anim = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadIndex();
        await RefreshLikes(fresh: true);
        if (sort == Sort.Popular) ShowCards();
    }

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
