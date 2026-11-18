using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RustBuilder;

/// Вход в аккаунт сайта: Discord (через браузер) или почта и пароль.
public class LoginWindow : Window
{
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 13 };
    readonly Button discord, emailBtn;
    readonly TextBox email = new() { Padding = new Thickness(8, 6, 8, 6), FontSize = 14 };
    readonly PasswordBox pass = new() { Padding = new Thickness(8, 6, 8, 6), FontSize = 14 };
    CancellationTokenSource cts;

    /// Показать окно входа; true — вошёл.
    public static bool Ask(Window owner, string why = null)
    {
        if (Site.LoggedIn) return true;
        var w = new LoginWindow(why) { Owner = owner };
        return w.ShowDialog() == true;
    }

    LoginWindow(string why)
    {
        Title = "RustBuilder — Вход в аккаунт";
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x13, 0x1F)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/icon.ico")); } catch { }

        var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        p.Children.Add(new TextBlock { Text = "Вход в аккаунт", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource("AccentLightBrush") });
        p.Children.Add(new TextBlock
        {
            Text = why ?? "Тот же аккаунт, что на сайте RustBuilder: выкладывай базы и ставь лайки прямо из программы.",
            Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16), FontSize = 13,
        });

        discord = new Button
        {
            Content = "Войти через Discord", FontSize = 15, FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
        };
        discord.Click += async (_, _) => await Discord();
        p.Children.Add(discord);
        p.Children.Add(new TextBlock { Text = "Откроется браузер — войди там, потом вернись сюда.", FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 4, 0, 14) });

        p.Children.Add(new TextBlock { Text = "или по почте", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        p.Children.Add(new TextBlock { Text = "Почта", Opacity = 0.75, FontSize = 12 });
        p.Children.Add(email);
        p.Children.Add(new TextBlock { Text = "Пароль", Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        p.Children.Add(pass);
        emailBtn = new Button { Content = "Войти", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0), IsDefault = true };
        emailBtn.Click += async (_, _) => await Password();
        p.Children.Add(emailBtn);

        var reg = new TextBlock { Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Нет аккаунта? Регистрация на сайте")) { Foreground = (Brush)Application.Current.FindResource("AccentLightBrush") };
        link.Click += (_, _) => Open(Site.Web + "#/login");
        reg.Inlines.Add(link);
        p.Children.Add(reg);
        p.Children.Add(status);

        Content = new Grid { Background = Background, Children = { p } };
        Closed += (_, _) => cts?.Cancel();
        Anim.Window(this);
    }

    async Task Discord()
    {
        cts?.Cancel();
        cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Busy(true, "Жду вход в браузере… (окно можно не закрывать)");
        try { await Site.LoginDiscord(cts.Token); Done(); }
        catch (OperationCanceledException) { Busy(false, "Вход отменён — попробуй ещё раз."); }
        catch (Exception e) { Busy(false, e.Message); }
    }

    async Task Password()
    {
        if (string.IsNullOrWhiteSpace(email.Text) || pass.Password.Length == 0) { status.Text = "Впиши почту и пароль."; return; }
        Busy(true, "Вхожу…");
        try { await Site.LoginPassword(email.Text, pass.Password); Done(); }
        catch (Exception e) { Busy(false, e.Message); }
    }

    void Done()
    {
        Sfx.Copy();
        Activate();
        DialogResult = true;
    }

    void Busy(bool on, string text)
    {
        discord.IsEnabled = emailBtn.IsEnabled = !on;
        status.Text = text;
        status.Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
        if (!on) Sfx.Error();
    }

    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
}
