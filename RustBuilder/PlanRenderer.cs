using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace RustBuilder;

/// Done — построено; Current — ставить сейчас; Upcoming — в этом шаге, но позже; Future — в следующих шагах
public enum PieceState { Done, Current, Upcoming, Future }

/// Рисует схему этажа на Canvas. Используется и в оверлее, и в превью библиотеки/каталога.
public static class PlanRenderer
{
    public static readonly Color Accent = Color.FromRgb(0xFF, 0x7A, 0x2E);
    static readonly Color EntranceColor = Color.FromRgb(0x6F, 0xD2, 0x6F);

    public static Color TierColor(string tier) => (tier ?? "twig").ToLower() switch
    {
        "wood" => Color.FromRgb(0x8B, 0x5A, 0x2B),
        "stone" => Color.FromRgb(0x80, 0x80, 0x80),
        "metal" => Color.FromRgb(0x5C, 0x6A, 0x75),
        "hqm" => Color.FromRgb(0x3F, 0x6E, 0x8C),
        _ => Color.FromRgb(0x8A, 0x7A, 0x4A),
    };

    /// Поворот схемы на rot × 90° по часовой стрелке
    public static Vector Rotate(Vector v, int rot)
    {
        for (int i = 0; i < ((rot % 4) + 4) % 4; i++) v = new Vector(-v.Y, v.X);
        return v;
    }

    static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));

    public static void Draw(Canvas canvas, BuildPlan plan, PlanGeometry geo, Func<Piece, PieceState> stateOf, int floor,
                            int rotation = 0, Func<Piece, int?> numberOf = null, double maxScale = 70)
    {
        canvas.Children.Clear();
        if (geo == null || geo.Polys.Count == 0) return;

        Point Rot(Point p) => (Point)Rotate((Vector)p, rotation);
        var all = geo.Polys.Values.SelectMany(v => v).Select(Rot).ToList();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double W = canvas.Width, H = canvas.Height, m = 30;
        double scale = Math.Min(maxScale, Math.Min((W - 2 * m) / Math.Max(maxX - minX, 0.01), (H - 2 * m) / Math.Max(maxY - minY, 0.01)));
        double ox = (W - (maxX - minX) * scale) / 2 - minX * scale;
        double oy = (H - (maxY - minY) * scale) / 2 - minY * scale;
        Point T(Point p) { var r = Rot(p); return new(ox + r.X * scale, oy + r.Y * scale); }
        Vector TV(Vector v) => Rotate(v, rotation);

        var labels = new List<(Point at, int n, PieceState st)>();
        void Label(Piece p, Point screenAt)
        {
            var n = numberOf?.Invoke(p);
            if (n.HasValue) labels.Add((screenAt, n.Value, stateOf(p)));
        }

        // 1) основа этажа: фундаменты на 1-м этаже, перекрытия на верхних
        foreach (var p in plan.Pieces.Where(p => p.IsFoundation))
        {
            if (floor == 1) { DrawPoly(geo.Polys[p.Id], p, stateOf(p)); Label(p, T(geo.Center(p.Id))); }
            else DrawPoly(geo.Polys[p.Id], null, PieceState.Future, ghost: true);
        }
        if (floor > 1)
        {
            foreach (var p in plan.Pieces.Where(p => p.Type == "floor" && p.Floor == floor))
            {
                DrawPoly(geo.Polys[p.On], p, stateOf(p));
                Label(p, T(geo.Center(p.On)));
            }
            // стены этажа ниже — бледно, для ориентации
            foreach (var p in plan.Pieces.Where(p => p.IsEdgePiece && p.Floor == floor - 1))
            {
                var (a, b) = geo.Edge(p.On, p.Edge);
                Seg(canvas, T(a), T(b), 2, Color.FromArgb(0x38, 255, 255, 255));
            }
        }

        // 2) лестницы
        foreach (var p in plan.Pieces.Where(p => p.Type == "stairs" && p.Floor == floor))
        {
            var st = stateOf(p);
            DrawPoly(geo.Polys[p.On], p, st);
            var v = geo.Polys[p.On].Select(T).ToArray();
            var col = st == PieceState.Future ? Color.FromArgb(0x60, 255, 255, 255) : st is PieceState.Current or PieceState.Upcoming ? Accent : Colors.White;
            for (int i = 1; i <= 4; i++)   // ступеньки
            {
                double t = i / 5.0;
                Seg(canvas, v[0] + (v[3] - v[0]) * t, v[1] + (v[2] - v[1]) * t, 1.5, Color.FromArgb(0x90, col.R, col.G, col.B));
            }
            Text(canvas, "▲", T(geo.Center(p.On)), 16, col, bold: true);
            Label(p, T(geo.Center(p.On)) + new Vector(0, 14));
        }

        // 3) стены
        foreach (var p in plan.Pieces.Where(p => p.IsEdgePiece && p.Floor == floor))
        {
            var (a, b) = geo.Edge(p.On, p.Edge);
            var st = stateOf(p);
            DrawEdge(canvas, T(a), T(b), p, st);
            if (p.Entrance && p.Type == "doorway")
            {
                var n = TV(geo.EdgeNormal(p.On, p.Edge));
                var mid = T(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
                var from = mid + n * scale * 0.62;
                var to = mid + n * scale * 0.16;
                Seg(canvas, from, to, 2.5, EntranceColor);
                var side = new Vector(-n.Y, n.X);
                Seg(canvas, to, to + n * 8 + side * 6, 2.5, EntranceColor);
                Seg(canvas, to, to + n * 8 - side * 6, 2.5, EntranceColor);
                Text(canvas, "вход", mid + n * scale * 0.8, 11, EntranceColor, bold: true);
            }
            // номер — прямо на середине стены: у соседних стен в углу номера тогда не налезают друг на друга
            Label(p, T(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2)));
        }

        // 4) шкаф
        foreach (var p in plan.Pieces.Where(p => p.Type == "tc" && p.Floor == floor))
        {
            var c = T(geo.Center(p.On));
            DrawTc(canvas, c, stateOf(p), scale);
            Label(p, c + new Vector(0, Math.Max(16, scale * 0.32) / 2 + 9));
        }

        // 5) номера блоков текущего шага — поверх всего
        foreach (var (at, n, st) in labels)
        {
            bool cur = st == PieceState.Current;
            double r = cur ? 10 : 8;
            var circle = new Shapes.Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = st == PieceState.Done ? B(Color.FromRgb(0x3A, 0x3F, 0x45), 0xE0) : cur ? B(Accent) : B(Color.FromRgb(0x2A, 0x2D, 0x31), 0xE8),
                Stroke = st == PieceState.Done ? B(Colors.White, 0x60) : B(Accent), StrokeThickness = cur ? 0 : 1.5,
            };
            Canvas.SetLeft(circle, at.X - r);
            Canvas.SetTop(circle, at.Y - r);
            canvas.Children.Add(circle);
            Text(canvas, st == PieceState.Done ? "✓" : n.ToString(), at, cur ? 12 : 10,
                 st == PieceState.Done ? Color.FromArgb(0xB0, 255, 255, 255) : cur ? Colors.Black : Accent, bold: true);
        }

        void DrawPoly(Point[] verts, Piece p, PieceState st, bool ghost = false)
        {
            var poly = new Shapes.Polygon { Points = new PointCollection(verts.Select(T)) };
            if (ghost)
            {
                poly.Stroke = B(Colors.White, 0x30);
                poly.StrokeThickness = 1;
                poly.StrokeDashArray = new DoubleCollection { 3, 3 };
            }
            else switch (st)
            {
                case PieceState.Done:
                    poly.Fill = B(TierColor(p.Tier ?? plan.Tier), 0x90);
                    poly.Stroke = B(Colors.White, 0xC0);
                    poly.StrokeThickness = 1;
                    break;
                case PieceState.Current:
                    poly.Fill = B(Accent, 0x80);
                    poly.Stroke = B(Accent);
                    poly.StrokeThickness = 3;
                    break;
                case PieceState.Upcoming:
                    poly.Fill = B(Accent, 0x22);
                    poly.Stroke = B(Accent, 0xA0);
                    poly.StrokeThickness = 1.5;
                    poly.StrokeDashArray = new DoubleCollection { 4, 2 };
                    break;
                default:
                    poly.Stroke = B(Colors.White, 0x55);
                    poly.StrokeThickness = 1;
                    poly.StrokeDashArray = new DoubleCollection { 4, 3 };
                    break;
            }
            canvas.Children.Add(poly);
        }
    }

    static void Seg(Canvas canvas, Point p1, Point p2, double th, Color c, DoubleCollection dash = null) =>
        canvas.Children.Add(new Shapes.Line
        {
            X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
            Stroke = new SolidColorBrush(c), StrokeThickness = th, StrokeDashArray = dash,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });

    static void Text(Canvas canvas, string s, Point center, double size, Color c, bool bold = false)
    {
        var t = new TextBlock { Text = s, Foreground = new SolidColorBrush(c), FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(t, center.X - t.DesiredSize.Width / 2);
        Canvas.SetTop(t, center.Y - t.DesiredSize.Height / 2);
        canvas.Children.Add(t);
    }

    static void DrawEdge(Canvas canvas, Point a, Point b, Piece p, PieceState st)
    {
        Color col = st switch
        {
            PieceState.Done => Color.FromRgb(0xE8, 0xEC, 0xF0),
            PieceState.Current => Accent,
            PieceState.Upcoming => Color.FromArgb(0xA0, Accent.R, Accent.G, Accent.B),
            _ => Color.FromArgb(0x50, 255, 255, 255),
        };
        double thick = st == PieceState.Current ? 7 : 4.5;
        Point L(double t) => a + (b - a) * t;

        void S(Point p1, Point p2, double th, Color c, DoubleCollection dash = null) =>
            canvas.Children.Add(new Shapes.Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = new SolidColorBrush(c), StrokeThickness = th, StrokeDashArray = dash,
            });

        switch (p.Type)
        {
            case "wall":
                S(a, b, thick, col);
                break;
            case "halfwall":
                S(a, b, thick * 0.6, col, new DoubleCollection { 1.5, 1 });
                break;
            case "window":
                S(a, L(0.3), thick, col);
                S(L(0.7), b, thick, col);
                S(L(0.3), L(0.7), 1.5, col);
                break;
            case "doorway":
                S(a, L(0.3), thick, col);
                S(L(0.7), b, thick, col);
                if (p.Door != null)
                {
                    var dc = p.Door.ToLower() switch
                    {
                        "wood" => Color.FromRgb(0xA0, 0x6A, 0x35),
                        "armored" => Color.FromRgb(0x4A, 0x8F, 0xC0),
                        _ => Color.FromRgb(0xB8, 0xC0, 0xC8),
                    };
                    if (st == PieceState.Future) dc = Color.FromArgb(0x50, dc.R, dc.G, dc.B);
                    var hinge = L(0.3);
                    var d = (b - a) * 0.4;
                    var n = new Vector(d.Y, -d.X);
                    S(hinge, hinge + (d * 0.5 + n * 0.85), 3, dc);
                }
                break;
        }
    }

    static void DrawTc(Canvas canvas, Point c, PieceState st, double scale)
    {
        double s = Math.Max(16, scale * 0.32);
        var col = st is PieceState.Current or PieceState.Upcoming ? Accent
                : st == PieceState.Done ? Color.FromRgb(0x6F, 0xC2, 0x6F) : Color.FromArgb(0x60, 255, 255, 255);
        var rect = new Shapes.Rectangle
        {
            Width = s, Height = s, RadiusX = 2, RadiusY = 2,
            Stroke = new SolidColorBrush(col), StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)),
        };
        Canvas.SetLeft(rect, c.X - s / 2);
        Canvas.SetTop(rect, c.Y - s / 2);
        canvas.Children.Add(rect);
        Text(canvas, "TC", c, s * 0.45, col, bold: true);
    }
}
