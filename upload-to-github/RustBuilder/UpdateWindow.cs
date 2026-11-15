using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RustBuilder;

/// Окно «Вышла новая версия»: обновить сейчас / позже / пропустить
public class UpdateWindow : Window
{
    readonly Updater.Release release;
    readonly Button updateBtn, laterBtn, skipBtn;
    readonly ProgressBar bar;
    readonly TextBlock status;

    public UpdateWindow(Updater.Release r)
    {
        release = r;
        Title = "Обновление RustBuilder";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x1F));
        FontFamily = new FontFamily("Segoe UI");
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/icon.ico")); } catch { }

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"Вышла версия {r.Version}!", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentLightBrush"],
        });
        root.Children.Add(new TextBlock
        {
            Text = $"У тебя {Updater.Current}. Обновление скачается само, программа перезапустится. Твои базы и настройки останутся.",
            Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(r.Notes))
            root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0), MaxHeight = 220,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = r.Notes.Replace("**", "").Replace("### ", "").Replace("`", ""),
                        Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                },
            });

        bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 12, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed,
                                Foreground = (Brush)Application.Current.Resources["AccentBrush"], Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), BorderThickness = new Thickness(0) };
        status = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(bar);
        root.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        skipBtn = new Button { Content = "Пропустить версию", Margin = new Thickness(0, 0, 6, 0) };
        laterBtn = new Button { Content = "Позже", Margin = new Thickness(0, 0, 6, 0) };
        updateBtn = new Button { Content = "Обновить сейчас", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        buttons.Children.Add(skipBtn);
        buttons.Children.Add(laterBtn);
        buttons.Children.Add(updateBtn);
        root.Children.Add(buttons);
        Content = root;

        skipBtn.Click += (_, _) => { AppSettings.Current.SkippedVersion = r.Version.ToString(); AppSettings.Current.Save(); Close(); };
        laterBtn.Click += (_, _) => Close();
        updateBtn.Click += async (_, _) => await DoUpdate();
        Anim.Window(this);
    }

    async Task DoUpdate()
    {
        updateBtn.IsEnabled = laterBtn.IsEnabled = skipBtn.IsEnabled = false;
        bar.Visibility = Visibility.Visible;
        status.Text = "Скачиваю…";
        try
        {
            await Updater.InstallAsync(release, new Progress<double>(p => { bar.Value = p; status.Text = $"Скачиваю… {p:P0}"; }));
            status.Text = "Готово, перезапускаю…";
            Application.Current.Shutdown();
        }
        catch (Updater.SameVersionException ex)
        {
            // больше не предлагаем этот релиз — иначе окно будет выскакивать при каждом запуске
            AppSettings.Current.SkippedVersion = release.Version.ToString();
            AppSettings.Current.Save();
            status.Text = "Обновление не нужно: " + ex.Message;
            laterBtn.IsEnabled = true;
            laterBtn.Content = "Закрыть";
        }
        catch (Exception ex)
        {
            status.Text = "Не получилось обновиться: " + ex.Message +
                          "\nСкачай новую версию вручную со страницы Releases на GitHub.";
            laterBtn.IsEnabled = true;
            laterBtn.Content = "Закрыть";
        }
    }

    static void Say(Window owner, string text)
    {
        if (owner != null) MessageBox.Show(owner, text, "RustBuilder"); else MessageBox.Show(text, "RustBuilder");
    }

    /// Тихая проверка при запуске; manual = true — по кнопке в настройках (тогда говорим и «обновлений нет»)
    public static async Task CheckAndOffer(bool manual = false, Window owner = null)
    {
        try
        {
            var r = await Updater.CheckAsync();
            if (r == null)
            {
                if (manual) Say(owner, $"У тебя последняя версия ({Updater.Current}).");
                return;
            }
            if (!manual && r.Version.ToString() == AppSettings.Current.SkippedVersion) return;
            new UpdateWindow(r).Show();
        }
        catch (Exception ex)
        {
            if (manual) Say(owner, "Не удалось проверить обновления: " + ex.Message);
        }
    }
}
