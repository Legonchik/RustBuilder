using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace RustBuilder;

/// Анимации появления окон
public static class Anim
{
    static bool Enabled => AppSettings.Current.Animations && !AppSettings.ReadOnly;

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly IEasingFunction Back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };

    static DoubleAnimation A(double from, double to, int ms, int delay = 0, IEasingFunction ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease ?? Ease };

    /// Оверлей: выезжает и раскрывается, содержимое появляется лесенкой, логотип крутится, рамка вспыхивает
    public static void Intro(FrameworkElement root, Panel stagger = null, FrameworkElement logo = null, Border glow = null)
    {
        if (!Enabled) return;

        var scale = new ScaleTransform(0.86, 0.86);
        var move = new TranslateTransform(0, 22);
        root.RenderTransformOrigin = new Point(0.5, 0);
        root.RenderTransform = new TransformGroup { Children = { scale, move } };
        root.BeginAnimation(UIElement.OpacityProperty, A(0, 1, 280));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, A(0.86, 1, 560, 0, Back));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, A(0.86, 1, 560, 0, Back));
        move.BeginAnimation(TranslateTransform.YProperty, A(22, 0, 520));

        if (stagger != null)
        {
            int i = 0;
            foreach (UIElement ch in stagger.Children)
            {
                if (ch.Visibility != Visibility.Visible) continue;
                var tt = new TranslateTransform(0, 12);
                ch.RenderTransform = tt;
                int delay = 140 + i * 60;
                ch.BeginAnimation(UIElement.OpacityProperty, A(0, 1, 320, delay));
                tt.BeginAnimation(TranslateTransform.YProperty, A(12, 0, 420, delay));
                i++;
            }
        }

        if (logo != null)
        {
            var rt = new RotateTransform();
            var sc = new ScaleTransform();
            logo.RenderTransformOrigin = new Point(0.5, 0.5);
            logo.RenderTransform = new TransformGroup { Children = { sc, rt } };
            rt.BeginAnimation(RotateTransform.AngleProperty, A(-200, 0, 900, 120, Back));
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, A(0.2, 1, 700, 120, Back));
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, A(0.2, 1, 700, 120, Back));
        }

        if (glow != null)
        {
            var shadow = new DropShadowEffect { Color = PlanRenderer.Accent, ShadowDepth = 0, BlurRadius = 0, Opacity = 0.9 };
            glow.Effect = shadow;
            var pulse = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(250) };
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), Ease));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), Ease));
            pulse.Completed += (_, _) => glow.Effect = null;   // эффект дорогой — убираем после вспышки
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, pulse);
        }
    }

    /// Схема «собирается»: блоки появляются волной от центра
    public static void PlanIn(Canvas canvas)
    {
        if (!Enabled || canvas.Children.Count == 0) return;
        var c = new Point(canvas.Width / 2, canvas.Height / 2);
        double maxD = Math.Sqrt(c.X * c.X + c.Y * c.Y);
        foreach (UIElement ch in canvas.Children)
        {
            // где элемент: у фигур — по координатам, у текста — по Canvas.Left/Top
            Point at = ch switch
            {
                System.Windows.Shapes.Line l => new Point((l.X1 + l.X2) / 2, (l.Y1 + l.Y2) / 2),
                System.Windows.Shapes.Polygon p when p.Points.Count > 0 => new Point(p.Points.Average(q => q.X), p.Points.Average(q => q.Y)),
                _ => new Point(Canvas.GetLeft(ch) is var x && !double.IsNaN(x) ? x : c.X, Canvas.GetTop(ch) is var y && !double.IsNaN(y) ? y : c.Y),
            };
            double d = (at - c).Length / maxD;                 // 0 в центре, 1 в углу
            int delay = (int)(d * 420);
            var tt = new TranslateTransform(0, 10);
            ch.RenderTransform = tt;
            ch.BeginAnimation(UIElement.OpacityProperty, A(0, 1, 260, delay));
            tt.BeginAnimation(TranslateTransform.YProperty, A(10, 0, 380, delay, Back));
        }
    }

    /// Обычные окна (базы, настройки, каталог): содержимое проявляется и чуть увеличивается
    public static void Window(Window w)
    {
        w.Loaded += (_, _) =>
        {
            if (!Enabled || w.Content is not FrameworkElement root) return;
            var scale = new ScaleTransform(0.95, 0.95);
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = scale;
            root.BeginAnimation(UIElement.OpacityProperty, A(0, 1, 260));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, A(0.95, 1, 380, 0, Back));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, A(0.95, 1, 380, 0, Back));
        };
    }
}
