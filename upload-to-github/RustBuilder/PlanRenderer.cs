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
    public static Color Accent { get; set; } = Color.FromRgb(0xA9, 0x70, 0xFF);
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

    /// Масштаб и сдвиг, чтобы мировой прямоугольник box вписался в холст W×H с полями 30 px
    public static (double scale, double ox, double oy) Fit(Rect box, double W, double H, double maxScale = 70)
    {
        const double m = 30;
        double scale = Math.Min(maxScale, Math.Min((W - 2 * m) / Math.Max(box.Width, 0.01), (H - 2 * m) / Math.Max(box.Height, 0.01)));
        return (scale, (W - box.Width * scale) / 2 - box.X * scale, (H - box.Height * scale) / 2 - box.Y * scale);
    }

    public static void Draw(Canvas canvas, BuildPlan plan, PlanGeometry geo, Func<Piece, PieceState> stateOf, int floor,
                            int rotation = 0, Func<Piece, int?> numberOf = null, double maxScale = 70, Rect? bounds = null)
    {
        canvas.Children.Clear();
        if (geo == null || geo.Polys.Count == 0) return;

        Point Rot(Point p) => (Point)Rotate((Vector)p, rotation);
        Rect box;
        if (bounds is Rect fixedBox) box = fixedBox;    // редактор: сетка не «прыгает», когда добавляешь блоки
        else
        {
            var all = geo.Polys.Values.SelectMany(v => v).Select(Rot).ToList();
            box = new Rect(new Point(all.Min(p => p.X), all.Min(p => p.Y)), new Point(all.Max(p => p.X), all.Max(p => p.Y)));
        }
        var (scale, ox, oy) = Fit(box, canvas.Width, canvas.Height, maxScale);
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
            foreach (var p in plan.Pieces.Where(p => p.IsCover && p.Floor == floor))
            {
                var st = stateOf(p);
                DrawPoly(geo.Polys[p.On], p, st);
                if (p.Type == "roof")   // крыша — скат в одну сторону: черепица, конёк сверху, стрелка вниз по скату
                {
                    var rc = st == PieceState.Future ? Color.FromArgb(0x50, 255, 255, 255) : Color.FromArgb(0xD0, 255, 255, 255);
                    int low = p.Edge != null ? geo.EdgeIndex(p.On, p.Edge) : 0;
                    RoofSlope(canvas, geo.Polys[p.On].Select(T).ToArray(), low, rc);
                }
                Label(p, T(geo.Center(p.On)) + (p.Type == "roof" ? new Vector(0, 12) : new Vector()));
            }
            // стены этажа ниже — бледно, для ориентации
            foreach (var p in plan.Pieces.Where(p => p.IsEdgePiece && p.Floor == floor - 1))
            {
                var (a, b) = geo.Edge(p.On, p.Edge);
                Seg(canvas, T(a), T(b), 2, Color.FromArgb(0x38, 255, 255, 255));
            }
        }

        // 2) подъёма
        foreach (var p in plan.Pieces.Where(p => p.Type == "stairs" && p.Floor == floor))
        {
            var st = stateOf(p);
            DrawPoly(geo.Polys[p.On], p, st);
            var v = geo.Polys[p.On].Select(T).ToArray();
            var col = st == PieceState.Future ? Color.FromArgb(0x60, 255, 255, 255) : st is PieceState.Current or PieceState.Upcoming ? Accent : Colors.White;
            StairLines(canvas, v, Color.FromArgb(0x90, col.R, col.G, col.B));
            Text(canvas, v.Length == 3 ? "↻" : "▲", T(geo.Center(p.On)), 16, col, bold: true);
            Label(p, T(geo.Center(p.On)) + new Vector(0, 14));
        }

        // 3) стены
        foreach (var p in plan.Pieces.Where(p => p.IsEdgePiece && p.Floor == floor))
        {
            var (a, b) = geo.Edge(p.On, p.Edge);
            var st = stateOf(p);
            DrawEdge(canvas, T(a), T(b), p, st);
            if (p.Entrance && p.Type is "doorway" or "frame")
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
            // номер — у середины стены, но сдвинут внутрь клетки, чтобы не закрывать проём, дверь или бойницу
            var m = T(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            var inward = T(geo.Center(p.On)) - m;
            if (inward.Length > 0.1) { var len = inward.Length; inward.Normalize(); m += inward * Math.Min(len * 0.6, Math.Max(15, scale * 0.24)); }
            Label(p, m);
        }

        // 4) шкаф
        foreach (var p in plan.Pieces.Where(p => p.Type == "tc" && p.Floor == floor))
        {
            var c = T(geo.Center(p.On));
            DrawTc(canvas, c, stateOf(p), scale);
            Label(p, c + new Vector(0, Math.Max(16, scale * 0.32) / 2 + 9));
        }

        // 5) мебель: по углам клетки, чтобы не налезать на шкаф в центре
        foreach (var grp in plan.Pieces.Where(p => p.IsItem && p.Floor == floor).GroupBy(p => p.On))
        {
            int auto = 0;
            foreach (var p in grp)
            {
                int slot = p.Slot ?? auto++;
                var c = T(ItemPos(geo, p.On, slot));
                double s = Math.Max(13, scale * 0.24);
                DrawItem(canvas, c, p.Type, stateOf(p), s);
                Label(p, c + new Vector(s * 0.55, -s * 0.55));
            }
        }

        // 6) номера блоков текущего шага — поверх всего
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
                 st == PieceState.Done ? Color.FromArgb(0xB0, 255, 255, 255) : cur ? Colors.White : Accent, bold: true);
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

    /// Крыша-скат. v — вершины клетки на экране, low — номер нижней стороны (свес).
    /// Квадратная: верхний край (конёк) — противоположная сторона. Треугольная: верх — вершина напротив свеса.
    public static void RoofSlope(Canvas canvas, Point[] v, int low, Color c)
    {
        int n = v.Length;
        Point a = v[low % n], b = v[(low + 1) % n];
        Point hiA = n == 4 ? v[(low + 3) % n] : v[(low + 2) % n];
        Point hiB = n == 4 ? v[(low + 2) % n] : v[(low + 2) % n];
        var faint = Color.FromArgb((byte)(c.A * 0.55), c.R, c.G, c.B);
        for (int i = 1; i <= 4; i++)   // ряды черепицы параллельно свесу
        {
            double t = i / 5.0;
            Seg(canvas, a + (hiA - a) * t, b + (hiB - b) * t, 1.3, faint);
        }
        Seg(canvas, a, b, 1.3, faint);
        if (n == 4) Seg(canvas, hiA, hiB, 3.5, c);   // конёк
        else
        {
            var dot = new Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(dot, hiA.X - 3.5); Canvas.SetTop(dot, hiA.Y - 3.5);
            canvas.Children.Add(dot);
        }
        // стрелка «вниз по скату»: от верха к свесу
        var top = new Point((hiA.X + hiB.X) / 2, (hiA.Y + hiB.Y) / 2);
        var bottom = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var from = top + (bottom - top) * 0.3;
        var to = top + (bottom - top) * 0.72;
        var d = to - from;
        if (d.Length < 1) return;
        d.Normalize();
        var side = new Vector(-d.Y, d.X);
        Seg(canvas, from, to, 2, c);
        Seg(canvas, to, to - d * 6 + side * 4.5, 2, c);
        Seg(canvas, to, to - d * 6 - side * 4.5, 2, c);
    }

    /// Ступеньки: у квадрата — полоски поперёк, у треугольника — спираль (полоски от центра к сторонам)
    static void StairLines(Canvas canvas, Point[] v, Color c)
    {
        if (v.Length == 3)
        {
            var ctr = new Point((v[0].X + v[1].X + v[2].X) / 3, (v[0].Y + v[1].Y + v[2].Y) / 3);
            for (int i = 0; i < 3; i++)
                for (int k = 1; k <= 2; k++)
                {
                    var e = v[i] + (v[(i + 1) % 3] - v[i]) * (k / 3.0);
                    Seg(canvas, ctr + (e - ctr) * 0.35, e, 1.5, c);
                }
            return;
        }
        for (int i = 1; i <= 4; i++)
        {
            double t = i / 5.0;
            Seg(canvas, v[0] + (v[3] - v[0]) * t, v[1] + (v[2] - v[1]) * t, 1.5, c);
        }
    }

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
                if (p.HasEmbrasure)
                {
                    if (p.VerticalEmbrasure)   // вертикальная бойница — узкая планка поперёк проёма
                    {
                        var nv = new Vector((b - a).Y, -(b - a).X); nv.Normalize();
                        S(L(0.5) - nv * thick * 1.1, L(0.5) + nv * thick * 1.1, thick * 0.55, col);
                    }
                    else S(L(0.4), L(0.6), thick * 0.75, col);   // горизонтальная — короткая планка вдоль проёма
                }
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
            case "frame":   // каркас: короткие столбы по краям, широкий проём под гаражку или двойную дверь
                S(a, L(0.12), thick, col);
                S(L(0.88), b, thick, col);
                if (p.Door != null)
                {
                    var kind = p.Door.ToLower();
                    var dc = kind switch
                    {
                        "double-wood" => Color.FromRgb(0xA0, 0x6A, 0x35),
                        "double-armored" => Color.FromRgb(0x4A, 0x8F, 0xC0),
                        "garage" => Color.FromRgb(0xD8, 0xB0, 0x40),
                        _ => Color.FromRgb(0xB8, 0xC0, 0xC8),
                    };
                    if (st == PieceState.Future) dc = Color.FromArgb(0x50, dc.R, dc.G, dc.B);
                    if (kind == "garage")   // гаражка — рифлёная полоса во весь проём
                        S(L(0.12), L(0.88), thick * 0.8, dc, new DoubleCollection { 0.6, 0.4 });
                    else                    // двойная — две створки от краёв
                    {
                        var d = (b - a) * 0.38;
                        var n = new Vector(d.Y, -d.X);
                        S(L(0.12), L(0.12) + d * 0.5 + n * 0.85, 3, dc);
                        S(L(0.88), L(0.88) - d * 0.5 + n * 0.85, 3, dc);
                    }
                }
                else S(L(0.12), L(0.88), 1, col, new DoubleCollection { 2, 3 });
                break;
        }
    }

    /// Легенда: (ключ, название, объяснение). Образец рисует DrawLegend.
    public static readonly (string Kind, string Title, string Text)[] Legend =
    {
        ("current", "Ставить сейчас", "Светится цветом интерфейса, с номером в кружке. Это то, что нужно поставить прямо сейчас."),
        ("upcoming", "Дальше в этом шаге", "Пунктир цветом интерфейса — эти блоки идут следом."),
        ("future", "Будет позже", "Бледный белый пунктир — до этого ещё дойдёшь."),
        ("done", "Уже стоит", "Залито цветом материала: дерево коричневое, камень серый, металл тёмный. Номер с ✓ — поставлено."),
        ("wall", "Стена", "Толстая сплошная линия по стороне клетки."),
        ("halfwall", "Полустена", "Тонкая пунктирная линия. За ней прячутся на отстрелочной крыше."),
        ("window", "Оконный проём", "Линия с разрывом и тонкой перемычкой посередине."),
        ("embrasure", "Окно + горизонтальная бойница", "Как окно, но в разрыве короткая планка вдоль стены. Широкий обзор по сторонам."),
        ("embrasure-v", "Окно + вертикальная бойница", "Как окно, но в разрыве планка поперёк стены. Удобно целиться вверх и вниз."),
        ("door", "Проём + дверь", "Разрыв в стене и створка: коричневая — деревянная, светлая — металлическая, синяя — бронированная."),
        ("entrance", "Вход", "Зелёная стрелка «вход» показывает, откуда заходить в базу."),
        ("frame", "Настенный каркас", "Короткие столбы по краям и широкий проём. Пустой — тонкий пунктир."),
        ("garage", "Каркас + гаражная дверь", "Жёлтая рифлёная полоса во весь проём."),
        ("double", "Каркас + двойная дверь", "Две створки от краёв: коричневые — деревянная, светлые — металлическая, синие — бронированная (МВК)."),
        ("stairs", "Подъём", "Клетка с полосками-ступеньками и ▲ — тут подъём на этаж выше."),
        ("stairs-tri", "Спиральный подъём", "Треугольник со ступеньками по кругу и ↻ — подъём на треугольнике."),
        ("floor", "Потолок / пол этажа", "Залитая клетка на верхних этажах."),
        ("roof", "Крыша (квадратная)", "Скат в одну сторону: толстая линия — верхний край (конёк), стрелка показывает вниз по скату, к свесу."),
        ("roof-tri", "Крыша (треугольная)", "Треугольный скат: точка — верх, стрелка — вниз к свесу."),
        ("ghost", "Этаж ниже", "На верхних этажах фундаменты снизу — тонкий пунктир, стены снизу — бледные линии. Чтобы не потеряться."),
        ("tc", "Шкаф (TC)", "Квадратик с буквами TC в центре клетки."),
        ("box", "Большой ящик", "Коричневый квадратик «Я» в углу клетки. Вся мебель — из вариантов интерьера (бета)."),
        ("furnace", "Печка", "Оранжевый квадратик «П»."),
        ("workbench", "Верстак", "Синий квадратик «В»."),
        ("repair", "Ремонтный верстак", "Бирюзовый квадратик «Р»."),
        ("bag", "Спальник", "Зелёный квадратик «С» — точка возрождения."),
        ("locker", "Шкафчик для одежды", "Серый квадратик «Ш» — хранит броню и оружие."),
    };

    /// Рисует образец для легенды на маленьком холсте (примерно 76×46)
    public static void DrawLegend(Canvas canvas, string kind)
    {
        canvas.Children.Clear();
        double W = canvas.Width, H = canvas.Height;
        var cell = new[] { new Point(W / 2 - 18, H / 2 - 18), new Point(W / 2 + 18, H / 2 - 18), new Point(W / 2 + 18, H / 2 + 18), new Point(W / 2 - 18, H / 2 + 18) };
        var center = new Point(W / 2, H / 2);
        void Poly(Brush fill, Brush stroke, double th, DoubleCollection dash = null) =>
            canvas.Children.Add(new Shapes.Polygon { Points = new PointCollection(cell), Fill = fill, Stroke = stroke, StrokeThickness = th, StrokeDashArray = dash });
        Point a = new(W / 2 - 26, H / 2), b = new(W / 2 + 26, H / 2);
        Piece P(string type) => new() { Type = type };

        switch (kind)
        {
            case "current": Poly(B(Accent, 0x80), B(Accent), 3); Badge(canvas, center, "1", true); break;
            case "upcoming": Poly(B(Accent, 0x22), B(Accent, 0xA0), 1.5, new DoubleCollection { 4, 2 }); break;
            case "future": Poly(null, B(Colors.White, 0x55), 1, new DoubleCollection { 4, 3 }); break;
            case "done": Poly(B(TierColor("stone"), 0x90), B(Colors.White, 0xC0), 1); Badge(canvas, center, "✓", false); break;
            case "wall": DrawEdge(canvas, a, b, P("wall"), PieceState.Done); break;
            case "halfwall": DrawEdge(canvas, a, b, P("halfwall"), PieceState.Done); break;
            case "window": DrawEdge(canvas, a, b, P("window"), PieceState.Done); break;
            case "embrasure": DrawEdge(canvas, a, b, new Piece { Type = "window", Embrasure = "horizontal" }, PieceState.Done); break;
            case "embrasure-v": DrawEdge(canvas, a, b, new Piece { Type = "window", Embrasure = "vertical" }, PieceState.Done); break;
            case "door":
                a = new(W / 2 - 26, H / 2 + 8); b = new(W / 2 + 26, H / 2 + 8);
                DrawEdge(canvas, a, b, new Piece { Type = "doorway", Door = "sheet" }, PieceState.Done); break;
            case "entrance":
                Seg(canvas, new Point(W / 2, H - 4), new Point(W / 2, 12), 2.5, EntranceColor);
                Seg(canvas, new Point(W / 2, 12), new Point(W / 2 - 6, 20), 2.5, EntranceColor);
                Seg(canvas, new Point(W / 2, 12), new Point(W / 2 + 6, 20), 2.5, EntranceColor);
                Text(canvas, "вход", new Point(W / 2 + 20, H / 2 + 6), 10, EntranceColor, bold: true); break;
            case "stairs":
                Poly(B(TierColor("stone"), 0x90), B(Colors.White, 0xC0), 1);
                StairLines(canvas, cell, Color.FromArgb(0x90, 255, 255, 255));
                Text(canvas, "▲", center, 14, Colors.White, bold: true); break;
            case "stairs-tri":
            {
                var tri = new[] { new Point(W / 2 - 22, H / 2 + 17), new Point(W / 2, H / 2 - 21), new Point(W / 2 + 22, H / 2 + 17) };
                canvas.Children.Add(new Shapes.Polygon { Points = new PointCollection(tri), Fill = B(TierColor("stone"), 0x90), Stroke = B(Colors.White, 0xC0), StrokeThickness = 1 });
                StairLines(canvas, tri, Color.FromArgb(0x90, 255, 255, 255));
                Text(canvas, "↻", new Point(W / 2, H / 2 + 4), 13, Colors.White, bold: true); break;
            }
            case "floor": Poly(B(TierColor("stone"), 0x90), B(Colors.White, 0xC0), 1); break;
            case "roof":
                Poly(B(TierColor("stone"), 0x90), B(Colors.White, 0xC0), 1);
                RoofSlope(canvas, cell, 2, Color.FromArgb(0xD0, 255, 255, 255)); break;
            case "roof-tri":
            {
                var tri = new[] { new Point(W / 2 - 22, H / 2 + 17), new Point(W / 2 + 22, H / 2 + 17), new Point(W / 2, H / 2 - 21) };
                canvas.Children.Add(new Shapes.Polygon { Points = new PointCollection(tri), Fill = B(TierColor("stone"), 0x90), Stroke = B(Colors.White, 0xC0), StrokeThickness = 1 });
                RoofSlope(canvas, tri, 0, Color.FromArgb(0xD0, 255, 255, 255)); break;
            }
            case "frame": DrawEdge(canvas, a, b, P("frame"), PieceState.Done); break;
            case "garage": DrawEdge(canvas, a, b, new Piece { Type = "frame", Door = "garage" }, PieceState.Done); break;
            case "double":
                a = new(W / 2 - 28, H / 2 + 8); b = new(W / 2 + 28, H / 2 + 8);
                DrawEdge(canvas, a, b, new Piece { Type = "frame", Door = "double-sheet" }, PieceState.Done); break;
            case "ghost":
                Poly(null, B(Colors.White, 0x30), 1, new DoubleCollection { 3, 3 });
                Seg(canvas, cell[0], cell[1], 2, Color.FromArgb(0x38, 255, 255, 255)); break;
            case "tc": DrawTc(canvas, center, PieceState.Done, 60); break;
            default:
                if (Piece.ItemTypes.Contains(kind)) DrawItem(canvas, center, kind, PieceState.Done, 22);
                break;
        }
    }

    static void Badge(Canvas canvas, Point at, string s, bool cur)
    {
        double r = 9;
        var circle = new Shapes.Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = cur ? B(Accent) : B(Color.FromRgb(0x3A, 0x3F, 0x45), 0xE0),
            Stroke = cur ? null : B(Colors.White, 0x60), StrokeThickness = 1.5,
        };
        Canvas.SetLeft(circle, at.X - r);
        Canvas.SetTop(circle, at.Y - r);
        canvas.Children.Add(circle);
        Text(canvas, s, at, 11, cur ? Colors.White : Color.FromArgb(0xB0, 255, 255, 255), bold: true);
    }

    /// Точка для предмета в клетке: у квадрата — 4 угла, у треугольника — 3 угла
    public static Point ItemPos(PlanGeometry geo, string on, int slot)
    {
        var center = geo.Center(on);
        var v = geo.Polys[on];
        if (geo.Triangles.Contains(on))
        {
            var corner = v[new[] { 2, 0, 1 }[((slot % 3) + 3) % 3]];
            return center + (corner - center) * 0.5;
        }
        // угол клетки по её вершинам — так работает и у квадрата, повёрнутого к стороне треугольника
        return center + (v[((slot % 4) + 4) % 4] - center) * 0.54;
    }

    public static (string Letter, Color Color) ItemLook(string type) => type switch
    {
        "box" => ("Я", Color.FromRgb(0xC0, 0x84, 0x4A)),
        "furnace" => ("П", Color.FromRgb(0xFF, 0x8A, 0x3D)),
        "workbench" => ("В", Color.FromRgb(0x4F, 0x9D, 0xE0)),
        "repair" => ("Р", Color.FromRgb(0x3F, 0xC1, 0xA9)),
        "bag" => ("С", Color.FromRgb(0x6F, 0xD2, 0x6F)),
        "locker" => ("Ш", Color.FromRgb(0xB8, 0xC0, 0xC8)),
        _ => ("?", Colors.White),
    };

    static void DrawItem(Canvas canvas, Point c, string type, PieceState st, double s)
    {
        var (letter, col) = ItemLook(type);
        byte a = st == PieceState.Future ? (byte)0x50 : (byte)0xFF;
        var rect = new Shapes.Rectangle
        {
            Width = s, Height = s, RadiusX = 3, RadiusY = 3,
            Fill = B(col, st == PieceState.Future ? (byte)0x30 : (byte)0xC0),
            Stroke = st is PieceState.Current or PieceState.Upcoming ? B(Accent) : B(Colors.White, (byte)(a / 2)),
            StrokeThickness = st == PieceState.Current ? 2.5 : 1,
        };
        Canvas.SetLeft(rect, c.X - s / 2);
        Canvas.SetTop(rect, c.Y - s / 2);
        canvas.Children.Add(rect);
        Text(canvas, letter, c, s * 0.62, Color.FromArgb(a, 0x15, 0x12, 0x1C), bold: true);
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
