using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RustBuilder;

/// Видео-гайды к одной базе: только одобренные модератором. «Смотреть» открывает браузер,
/// «⚑» — форму жалобы, «＋ Предложить своё» — форму заявки на GitHub (с автопроверкой).
public class VideosWindow : Window
{
    readonly string repo;

    public VideosWindow(CatalogEntry entry, List<VideoGuide> videos, string repo)
    {
        this.repo = repo;
        Title = $"RustBuilder — Видео-гайды: {entry.Name}";
        Width = 560; Height = 600; MinWidth = 420; MinHeight = 320;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x1F)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/icon.ico")); } catch { }

        var root = new DockPanel { Margin = new Thickness(18, 16, 18, 14), Background = Background };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "🎬 Видео-гайды", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource("AccentLightBrush") });
        head.Children.Add(new TextBlock { Text = entry.Name, FontSize = 13, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // низ: правила и кнопка «Предложить»
        var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var suggest = new Button { Content = "＋ Предложить своё видео", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(12, 5, 12, 5) };
        suggest.Click += (_, _) => Open(Catalog.SuggestVideoUrl(repo, entry.File));
        DockPanel.SetDock(suggest, Dock.Right);
        foot.Children.Add(suggest);
        foot.Children.Add(new TextBlock
        {
            Text = "Только YouTube и Rutube. Робот проверяет ссылку, мат, 18+, читы, казино и скам, потом видео смотрит модератор. Нарушил — бан на отправку.",
            FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        var list = new StackPanel();
        if (videos.Count == 0)
            list.Children.Add(new TextBlock
            {
                Text = "К этой базе пока нет видео.\nСнял, как строишь её? Предложи — после проверки оно появится здесь у всех.",
                Opacity = 0.6, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 60, 0, 0),
            });
        foreach (var v in videos.OrderByDescending(v => v.Added ?? "")) list.Children.Add(Row(v));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = new Grid { Background = Background, Children = { root } };
        Anim.Window(this);
    }

    FrameworkElement Row(VideoGuide v)
    {
        var row = new DockPanel();
        var border = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(9), Child = row,
            Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8),
        };

        // превью (картинки только с серверов YouTube/Rutube)
        var thumb = new Border { Width = 160, Height = 90, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), Margin = new Thickness(0, 0, 12, 0), Cursor = System.Windows.Input.Cursors.Hand };
        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = "▶", FontSize = 30, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (v.SafeThumbnail is string src && !AppSettings.ReadOnly)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = new Uri(src); bmp.DecodePixelWidth = 320; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                grid.Children.Insert(0, new Image { Source = bmp, Stretch = Stretch.UniformToFill });
            }
            catch { }
        }
        thumb.Child = grid;
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 6, 6);
        thumb.MouseLeftButtonUp += (_, _) => Watch(v);
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);

        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = v.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = v.Title });
        info.Children.Add(new TextBlock { Text = v.Meta, FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(v.About))
            info.Children.Add(new TextBlock { Text = v.About, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = v.About });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var watch = new Button { Content = "▶ Смотреть", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(10, 3, 10, 3) };
        watch.Click += (_, _) => Watch(v);
        var report = new Button { Content = "⚑ Пожаловаться", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Мат, 18+, реклама читов или скама, видео не про эту базу" };
        report.Click += (_, _) => Open(Catalog.ReportVideoUrl(repo, v));
        buttons.Children.Add(watch);
        buttons.Children.Add(report);
        info.Children.Add(buttons);
        row.Children.Add(info);
        return border;
    }

    static void Watch(VideoGuide v)
    {
        if (v.SafeUrl is string url) { Sfx.Build(); Open(url); }
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
