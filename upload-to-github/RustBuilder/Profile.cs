using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RustBuilder;

/// Автор базы: значок «✳ Claude» у встроенных баз, «👤 ник» у баз игроков
public static class Authors
{
    public const string Claude = "Claude";
    static readonly Brush ClaudeBrush = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
    static readonly Brush PlayerBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static bool IsClaude(string author) => string.Equals(author?.Trim(), Claude, StringComparison.OrdinalIgnoreCase);
    public static string BadgeText(string author) =>
        IsClaude(author) ? "✳ Claude" : string.IsNullOrWhiteSpace(author) ? null : "👤 " + author.Trim();
    public static Brush BadgeBrush(string author) => IsClaude(author) ? ClaudeBrush : PlayerBrush;

    /// Ник для публикации: «Claude» занято встроенными базами
    public static string Validate(string nick)
    {
        nick = nick?.Trim() ?? "";
        if (nick.Length < 2) return "Ник — хотя бы 2 символа.";
        if (nick.Length > 24) return "Ник — не длиннее 24 символов.";
        if (IsClaude(nick) || nick.Contains("claude", StringComparison.OrdinalIgnoreCase)) return "Ник «Claude» занят встроенными базами — выбери другой.";
        return null;
    }
}

/// Окно профиля: ник автора. Без него выкладывать базы нельзя.
public static class Profile
{
    /// Спрашивает ник (или меняет текущий). null — пользователь отказался.
    public static string Ask(Window owner, string reason = null)
    {
        var s = AppSettings.Current;
        var win = new Window
        {
            Title = "RustBuilder — Профиль", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner, Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21)), Foreground = Brushes.White,
        };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "👤 Профиль автора", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new TextBlock
        {
            Text = (reason != null ? reason + "\n\n" : "") +
                   "Ник показывается у твоих баз в каталоге. Сам файл выкладывается через твой аккаунт GitHub — это и есть вход: войти попросит GitHub, пароль RustBuilder не видит.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 10),
        });
        var box = new TextBox { Text = s.Nickname ?? "", Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) };
        root.Children.Add(box);
        var err = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x7A)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(err);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4) };
        if (Application.Current?.TryFindResource("PrimaryButton") is Style st) ok.Style = st;
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        win.Content = root;

        string result = null;
        ok.Click += (_, _) =>
        {
            var problem = Authors.Validate(box.Text);
            if (problem != null) { err.Text = problem; return; }
            result = box.Text.Trim();
            s.Nickname = result;
            s.Save();
            win.DialogResult = true;
        };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }

    /// Ник для публикации: если профиля нет — просит создать
    public static string Require(Window owner) =>
        string.IsNullOrWhiteSpace(AppSettings.Current.Nickname) || Authors.Validate(AppSettings.Current.Nickname) != null
            ? Ask(owner, "Чтобы выкладывать базы, создай профиль: придумай ник.")
            : AppSettings.Current.Nickname;
}
