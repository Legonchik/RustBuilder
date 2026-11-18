using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;

namespace RustBuilder;

/// Заставка при запуске: вспышка, кольца, логотип, название по буквам. Клик — пропустить.
public class SplashWindow : Window
{
    const double S = 560;
    readonly Grid root = new() { Width = S, Height = S };
    readonly Storyboard sb = new();
    bool closing;

    public event Action Finished;

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;          // не забираем фокус у игры
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = root;
        MouseLeftButtonDown += (_, _) => Finish();

        var accent = PlanRenderer.Accent;
        Color A(byte a) => Color.FromArgb(a, accent.R, accent.G, accent.B);

        // 1) мягкое свечение-пятно
        var glow = new Shapes.Ellipse
        {
            Width = S, Height = S, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5),
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new(A(0xC0), 0), new(A(0x55), 0.35), new(Color.FromArgb(0x60, 0x15, 0x12, 0x1C), 0.6), new(Colors.Transparent, 1),
            }),
            RenderTransform = new ScaleTransform(0.3, 0.3),
        };
        root.Children.Add(glow);
        Fade(glow, 0, 1, 0, 500);
        Scale(glow, 0.3, 1, 0, 900, new CubicEase { EasingMode = EasingMode.EaseOut });

        // 2) кольца: крутятся в разные стороны
        for (int i = 0; i < 3; i++)
        {
            double d = 250 + i * 70;
            var ring = new Shapes.Ellipse
            {
                Width = d, Height = d, Stroke = new SolidColorBrush(A((byte)(0xD0 - i * 0x30))), StrokeThickness = 3 - i * 0.6,
                StrokeDashArray = i == 1 ? new DoubleCollection { 1, 3 } : new DoubleCollection { 18 + i * 6, 8 + i * 4 },
                StrokeDashCap = PenLineCap.Round, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var tg = new TransformGroup();
            var sc = new ScaleTransform(0.6, 0.6);
            var rt = new RotateTransform();
            tg.Children.Add(sc); tg.Children.Add(rt);
            ring.RenderTransform = tg;
            root.Children.Add(ring);
            int delay = 150 + i * 120;
            Fade(ring, 0, 1, delay, 400);
            Add(new DoubleAnimation(0.6, 1, Ms(700)) { BeginTime = Ms(delay), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } }, ring, "RenderTransform.Children[0].ScaleX");
            Add(new DoubleAnimation(0.6, 1, Ms(700)) { BeginTime = Ms(delay), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } }, ring, "RenderTransform.Children[0].ScaleY");
            Add(new DoubleAnimation(0, i % 2 == 0 ? 360 : -360, Ms(2600 + i * 600)), ring, "RenderTransform.Children[1].Angle");
        }

        // 3) ударная волна, когда «приземляется» логотип
        for (int i = 0; i < 2; i++)
        {
            var wave = new Shapes.Ellipse
            {
                Width = S, Height = S, Stroke = new SolidColorBrush(A(0xFF)), StrokeThickness = 6, Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.2, 0.2),
            };
            root.Children.Add(wave);
            int delay = 520 + i * 160;
            Add(new DoubleAnimationUsingKeyFrames
            {
                BeginTime = Ms(delay),
                KeyFrames = { new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(Ms(0))), new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(750)), new QuadraticEase()) },
            }, wave, "Opacity");
            Scale(wave, 0.2, 1, delay, 750, new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        // 4) искры: разлетаются от центра
        var rnd = new Random(7);
        for (int i = 0; i < 18; i++)
        {
            double ang = i * Math.PI * 2 / 18 + rnd.NextDouble() * 0.3, dist = 170 + rnd.NextDouble() * 90;
            var dot = new Shapes.Ellipse
            {
                Width = 6, Height = 6, Fill = new SolidColorBrush(i % 3 == 0 ? Colors.White : A(0xFF)), Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform(),
            };
            root.Children.Add(dot);
            int delay = 540 + rnd.Next(0, 120);
            Add(new DoubleAnimationUsingKeyFrames
            {
                BeginTime = Ms(delay),
                KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(0))), new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ms(900)), new QuadraticEase { EasingMode = EasingMode.EaseIn }) },
            }, dot, "Opacity");
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Add(new DoubleAnimation(0, Math.Cos(ang) * dist, Ms(900)) { BeginTime = Ms(delay), EasingFunction = ease }, dot, "RenderTransform.X");
            Add(new DoubleAnimation(0, Math.Sin(ang) * dist, Ms(900)) { BeginTime = Ms(delay), EasingFunction = ease }, dot, "RenderTransform.Y");
        }

        // 5) логотип: влетает с поворотом и пружинит
        var logo = new Image
        {
            Width = 150, Height = 150, Margin = new Thickness(0, 0, 0, 70), Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect { Color = accent, ShadowDepth = 0, BlurRadius = 40, Opacity = 1 },
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        logo.Source = IconFrame(256);
        var ltg = new TransformGroup();
        ltg.Children.Add(new ScaleTransform(0.1, 0.1));
        ltg.Children.Add(new RotateTransform(-120));
        logo.RenderTransform = ltg;
        root.Children.Add(logo);
        Fade(logo, 0, 1, 80, 350);
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        Add(new DoubleAnimation(0.1, 1, Ms(650)) { BeginTime = Ms(80), EasingFunction = back }, logo, "RenderTransform.Children[0].ScaleX");
        Add(new DoubleAnimation(0.1, 1, Ms(650)) { BeginTime = Ms(80), EasingFunction = back }, logo, "RenderTransform.Children[0].ScaleY");
        Add(new DoubleAnimation(-120, 0, Ms(800)) { BeginTime = Ms(80), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, logo, "RenderTransform.Children[1].Angle");
        Add(new DoubleAnimationUsingKeyFrames      // пульс свечения
        {
            BeginTime = Ms(600),
            KeyFrames = { new EasingDoubleKeyFrame(90, KeyTime.FromTimeSpan(Ms(250))), new EasingDoubleKeyFrame(35, KeyTime.FromTimeSpan(Ms(900))) },
        }, logo, "Effect.BlurRadius");

        // 6) название по буквам
        var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 200, 0, 0) };
        string word = "RustBuilder";
        for (int i = 0; i < word.Length; i++)
        {
            var t = new TextBlock
            {
                Text = word[i].ToString(), FontSize = 46, FontWeight = FontWeights.Black, FontFamily = new FontFamily("Segoe UI"),
                Foreground = i < 4 ? Brushes.White : new SolidColorBrush(Theme.Parse(null) == accent ? Color.FromRgb(0xC9, 0xA8, 0xFF) : Mix(accent, Colors.White, 0.35)),
                Opacity = 0, RenderTransform = new TranslateTransform(0, 26),
                Effect = new DropShadowEffect { Color = accent, ShadowDepth = 0, BlurRadius = 18, Opacity = 0.9 },
            };
            title.Children.Add(t);
            int delay = 700 + i * 55;
            Fade(t, 0, 1, delay, 260);
            Add(new DoubleAnimation(26, 0, Ms(420)) { BeginTime = Ms(delay), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } }, t, "RenderTransform.Y");
        }
        root.Children.Add(title);

        var sub = new TextBlock
        {
            Text = "гайд по стройке прямо поверх игры", FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 290, 0, 0), Opacity = 0,
        };
        root.Children.Add(sub);
        Fade(sub, 0, 1, 1400, 400);

        // 7) уход: всё увеличивается и тает
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = new ScaleTransform(1, 1);
        Add(new DoubleAnimation(1, 0, Ms(450)) { BeginTime = Ms(2350), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } }, root, "Opacity");
        Add(new DoubleAnimation(1, 1.18, Ms(450)) { BeginTime = Ms(2350), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } }, root, "RenderTransform.ScaleX");
        Add(new DoubleAnimation(1, 1.18, Ms(450)) { BeginTime = Ms(2350), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } }, root, "RenderTransform.ScaleY");

        sb.Completed += (_, _) => Finish();
        Loaded += (_, _) => sb.Begin(this, true);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// Кадр иконки нужного размера: BitmapImage из .ico берёт первый (16×16) — отсюда «мыло» при увеличении
    public static BitmapSource IconFrame(int px)
    {
        try
        {
            var dec = BitmapDecoder.Create(new Uri("pack://application:,,,/icon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= px)
                ?? dec.Frames.OrderBy(f => f.PixelWidth).Last();
        }
        catch { return null; }
    }

    void Add(Timeline a, DependencyObject target, string path)
    {
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(path));
        sb.Children.Add(a);
    }

    void Fade(UIElement e, double from, double to, double delay, double ms) =>
        Add(new DoubleAnimation(from, to, Ms(ms)) { BeginTime = Ms(delay) }, e, "Opacity");

    void Scale(UIElement e, double from, double to, double delay, double ms, IEasingFunction ease)
    {
        Add(new DoubleAnimation(from, to, Ms(ms)) { BeginTime = Ms(delay), EasingFunction = ease }, e, "RenderTransform.ScaleX");
        Add(new DoubleAnimation(from, to, Ms(ms)) { BeginTime = Ms(delay), EasingFunction = ease }, e, "RenderTransform.ScaleY");
    }

    void Finish()
    {
        if (closing) return;
        closing = true;
        sb.Stop(this);
        Close();
        Finished?.Invoke();
    }
}
