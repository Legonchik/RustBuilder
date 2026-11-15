using System.Windows;
using System.Windows.Media;

namespace RustBuilder;

/// Цвет интерфейса. Все окна берут его из ресурсов приложения (DynamicResource), схема — из PlanRenderer.Accent.
public static class Theme
{
    public const string DefaultAccent = "#A970FF";

    public static readonly (string Name, string Hex)[] Presets =
    {
        ("Фиолетовый", "#A970FF"),
        ("Оранжевый", "#FF7A2E"),
        ("Красный", "#FF4D5E"),
        ("Розовый", "#F472B6"),
        ("Жёлтый", "#FACC15"),
        ("Зелёный", "#4ADE80"),
        ("Бирюзовый", "#2DD4BF"),
        ("Голубой", "#38BDF8"),
    };

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? DefaultAccent); }
        catch { return (Color)ColorConverter.ConvertFromString(DefaultAccent); }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static void Apply(string hex)
    {
        var c = Parse(hex);
        PlanRenderer.Accent = c;
        var res = Application.Current?.Resources;
        if (res == null) return;
        void Set(string key, Color col) { var b = new SolidColorBrush(col); b.Freeze(); res[key] = b; }
        Set("AccentBrush", c);
        Set("AccentDarkBrush", Mix(c, Colors.Black, 0.22));
        Set("AccentLightBrush", Mix(c, Colors.White, 0.25));
        Set("AccentPaleBrush", Mix(c, Colors.White, 0.42));
        Set("AccentWhiteBrush", Mix(c, Colors.White, 0.72));
        Set("AccentSoftBrush", Color.FromArgb(0x33, c.R, c.G, c.B));
    }
}
