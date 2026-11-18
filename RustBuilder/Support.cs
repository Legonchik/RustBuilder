using System.Diagnostics;
using System.Windows;

namespace RustBuilder;

/// Поддержать проект донатом (CloudTips). Программа бесплатная, донат — по желанию.
public static class Support
{
    public const string Url = "https://pay.cloudtips.ru/p/94c686ed";
    public const string Site = "https://legonchik.github.io/RustBuilder/";

    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch { MessageBox.Show("Открой в браузере:\n" + Url, "Поддержать RustBuilder"); }
    }

    /// Обработчик для кнопок «❤ Поддержать» в XAML
    public static void Click(object sender, RoutedEventArgs e) => Open();
}
