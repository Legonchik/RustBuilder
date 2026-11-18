using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace RustBuilder;

/// Редактор баз: схему рисуют мышкой по сетке, шаги постройки собираются сами
public partial class EditorWindow : Window
{
    /// Видимая часть сетки (в клетках) — фиксированная, чтобы схема не «прыгала»
    static readonly Rect World = new(-6, -5, 12, 10);
    const int MaxFloors = 6;
    static readonly double TriH = Math.Sqrt(3) / 2;

    enum Tool { Square, Triangle, Erase, Wall, Doorway, Window, Halfwall, Frame, Floor, Roof, Stairs, Tc, Item }

    /// Что произойдёт по клику: подсказка, призрак блока и само действие
    record Proposal(bool Ok, string Hint, Point[] Poly = null, (Point a, Point b)? Seg = null, Point? Dot = null, Action Apply = null);

    public static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };

    /// Сохранили базу — путь к файлу в «моих базах»
    public event Action<string> Saved;

    BuildPlan plan;
    PlanGeometry geo;
    string editingPath;          // файл пользователя, который перезаписываем; null — ещё не сохраняли
    int floor = 1;
    Tool tool = Tool.Square;
    bool loading = true, dirty, piecesChanged;
    Point? mouse;
    readonly Stack<string> undo = new();
    readonly Dictionary<Tool, Button> toolButtons = new();

    public EditorWindow(BuildPlan source = null)
    {
        InitializeComponent();
        Anim.Window(this);

        if (source != null)
        {
            var src = source.Original ?? source;
            plan = JsonSerializer.Deserialize<BuildPlan>(JsonSerializer.Serialize(src, JsonOut), JsonIn);
            plan.Interiors ??= new();
            if (src.IsUser) editingPath = src.FullPath;
            else { plan.Name = src.Name.Replace(" (claude)", "") + " (моя версия)"; plan.Author = null; }
            // у мебели без угла фиксируем угол — как её и рисовали
            foreach (var g in plan.Pieces.Where(p => p.IsItem).GroupBy(p => (p.On, p.Floor)))
            {
                int auto = 0;
                foreach (var p in g) p.Slot ??= auto++;
            }
        }
        else
        {
            plan = new BuildPlan { Name = "Моя база", Tier = "stone" };
            piecesChanged = true;
        }

        AddTool(ToolsBase, Tool.Square, "▢ Фундамент", "Квадратный фундамент: клик по пустой клетке (1 этаж)");
        AddTool(ToolsBase, Tool.Triangle, "△ Треугольник", "Треугольный фундамент: клик снаружи у свободной стороны фундамента");
        AddTool(ToolsBase, Tool.Erase, "✕ Ластик", "Убрать блок (то же самое — правая кнопка мыши)");
        AddTool(ToolsWalls, Tool.Wall, "Стена", "Клик по стороне клетки");
        AddTool(ToolsWalls, Tool.Doorway, "Дверной проём", "Клик по стороне клетки. Дверь и замок — ниже");
        AddTool(ToolsWalls, Tool.Window, "Окно", "Клик по стороне клетки. Бойница — ниже");
        AddTool(ToolsWalls, Tool.Halfwall, "Полустена", "Клик по стороне клетки");
        AddTool(ToolsWalls, Tool.Frame, "Каркас", "Настенный каркас: под гаражную или двойную дверь. Дверь — ниже");
        AddTool(ToolsFloors, Tool.Floor, "Перекрытие", "Пол/потолок: со 2 этажа, клик по клетке");
        AddTool(ToolsFloors, Tool.Roof, "Крыша", "Квадратная или треугольная крыша (по форме клетки): со 2 этажа");
        AddTool(ToolsFloors, Tool.Stairs, "Подъём", "На квадрате — L-образный, на треугольнике — спиральный");
        var fill = new Button { Content = "Перекрыть всё", Margin = new Thickness(0, 0, 6, 6), ToolTip = "Положить перекрытие на все клетки этого этажа (кроме клеток над подъёмом)" };
        fill.Click += (_, _) => FillFloor();
        ToolsFloors.Children.Add(fill);
        AddTool(ToolsInside, Tool.Tc, "Шкаф (TC)", "Один на базу: клик по клетке");
        AddTool(ToolsInside, Tool.Item, "Мебель", "Предмет — ниже. Встаёт в угол клетки, ближайший к курсору");

        FillDoors(false);
        Fill(LockBox, ("code", "Кодовый замок"), ("key", "Замок с ключом"), (null, "Без замка"));
        Fill(EmbrasureBox, ("horizontal", "Горизонтальная"), ("vertical", "Вертикальная"), (null, "Без бойницы"));
        Fill(ItemBox, Piece.ItemTypes.Select(t => (t, new Piece { Type = t }.TypeName)).ToArray());
        Fill(TierBox, ("twig", "Солома"), ("wood", "Дерево"), ("stone", "Камень"), ("metal", "Металл"), ("hqm", "МВК"));
        TierBox.SelectedItem = TierBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == plan.Tier) ?? TierBox.Items[2];

        NameBox.Text = plan.Name;
        DescBox.Text = plan.Description;
        // своя база — автор по нику из профиля (чужой копии тоже ставим себя)
        if (Authors.IsClaude(plan.Author) || string.IsNullOrWhiteSpace(plan.Author)) plan.Author = AppSettings.Current.Nickname;
        AuthorBox.Text = plan.Author ?? "";
        loading = false;

        SetTool(plan.Pieces.Any(p => p.IsFoundation) ? Tool.Wall : Tool.Square);
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
    }

    // ---------- Интерфейс ----------

    void AddTool(Panel panel, Tool t, string title, string tip)
    {
        var b = new Button { Content = title, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        b.Click += (_, _) => SetTool(t);
        panel.Children.Add(b);
        toolButtons[t] = b;
    }

    static void Fill(ComboBox box, params (string tag, string title)[] items)
    {
        foreach (var (tag, title) in items) box.Items.Add(new ComboBoxItem { Content = title, Tag = tag });
        box.SelectedIndex = 0;
    }

    static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    bool doorsForFrame;
    /// Список дверей: для проёма — обычные, для каркаса — гаражная и двойные
    void FillDoors(bool frame)
    {
        doorsForFrame = frame;
        DoorBox.Items.Clear();
        if (frame) Fill(DoorBox, ("garage", "Гаражная"), ("double-armored", "Двойная бронированная (МВК)"), ("double-sheet", "Двойная металлическая"), ("double-wood", "Двойная деревянная"), (null, "Без двери"));
        else Fill(DoorBox, ("sheet", "Металлическая"), ("armored", "Бронированная"), ("wood", "Деревянная"), (null, "Без двери"));
    }

    void SetTool(Tool t)
    {
        tool = t;
        foreach (var (k, b) in toolButtons)
            if (k == t) b.Style = (Style)FindResource("PrimaryButton"); else b.ClearValue(StyleProperty);
        if (t is Tool.Doorway or Tool.Frame && doorsForFrame != (t == Tool.Frame)) FillDoors(t == Tool.Frame);
        DoorOptions.Visibility = t is Tool.Doorway or Tool.Frame ? Visibility.Visible : Visibility.Collapsed;
        WindowOptions.Visibility = t == Tool.Window ? Visibility.Visible : Visibility.Collapsed;
        ItemOptions.Visibility = t == Tool.Item ? Visibility.Visible : Visibility.Collapsed;
        if (t is Tool.Square or Tool.Triangle && floor != 1) floor = 1;
        if (t is Tool.Floor or Tool.Roof && floor == 1 && plan.Pieces.Any(p => p.IsFoundation)) floor = 2;
        Render();
    }

    void BuildFloorTabs()
    {
        FloorTabs.Children.Clear();
        int floors = Math.Min(MaxFloors, Math.Max(plan.FloorCount + 1, floor));
        for (int f = 1; f <= floors; f++)
        {
            int ff = f;
            var b = new Button { Content = $"Этаж {f}", Margin = new Thickness(0, 0, 6, 0) };
            if (f == floor) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += (_, _) => { floor = ff; Render(); };
            FloorTabs.Children.Add(b);
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox) { Undo(); e.Handled = true; }
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!dirty) return;
        var r = MessageBox.Show(this, "Сохранить базу перед закрытием?", "Редактор баз", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !TrySave(out _))) e.Cancel = true;
    }

    void Meta_Changed(object sender, TextChangedEventArgs e) { if (!loading) dirty = true; }

    void Tier_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || plan == null) return;
        plan.Tier = TagOf(TierBox) ?? "stone";
        dirty = true;
        Render();
    }

    // ---------- Координаты и геометрия ----------

    (double s, double ox, double oy) Xf => PlanRenderer.Fit(World, PlanLayer.Width, PlanLayer.Height);
    Point ToScreen(Point w) { var (s, ox, oy) = Xf; return new(ox + w.X * s, oy + w.Y * s); }
    Point ToWorld(Point p) { var (s, ox, oy) = Xf; return new((p.X - ox) / s, (p.Y - oy) / s); }

    IEnumerable<Piece> Foundations => plan.Pieces.Where(p => p.IsFoundation && geo != null && geo.Polys.ContainsKey(p.Id));

    static bool Inside(Point[] poly, Point w)
    {
        bool pos = false, neg = false;
        for (int i = 0; i < poly.Length; i++)
        {
            Point a = poly[i], b = poly[(i + 1) % poly.Length];
            double c = (b.X - a.X) * (w.Y - a.Y) - (b.Y - a.Y) * (w.X - a.X);
            if (c > 1e-9) pos = true;
            if (c < -1e-9) neg = true;
        }
        return !(pos && neg);
    }

    static double SegDist(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = Math.Clamp((p - a) * ab / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    static string EdgeKey(Point a, Point b) { var m = Mid(a, b); return $"{Math.Round(m.X, 2)}:{Math.Round(m.Y, 2)}"; }

    Piece FoundationAt(Point w) => Foundations.FirstOrDefault(f => Inside(geo.Polys[f.Id], w));

    /// Стороны, на которые можно ставить стены: у квадрата N/E/S/W, у треугольника L/R
    IEnumerable<(Piece on, string edge, Point a, Point b)> Edges()
    {
        foreach (var f in Foundations)
            foreach (var e in geo.Triangles.Contains(f.Id) ? new[] { "L", "R" } : new[] { "N", "E", "S", "W" })
            {
                var (a, b) = geo.Edge(f.Id, e);
                yield return (f, e, a, b);
            }
    }

    /// Сколько фундаментов делят каждую сторону (1 — наружная, 2 — внутренняя)
    Dictionary<string, int> EdgeUse()
    {
        var use = new Dictionary<string, int>();
        foreach (var f in Foundations)
        {
            var v = geo.Polys[f.Id];
            for (int i = 0; i < v.Length; i++)
            {
                var k = EdgeKey(v[i], v[(i + 1) % v.Length]);
                use[k] = use.GetValueOrDefault(k) + 1;
            }
        }
        return use;
    }

    (Piece on, string edge, Point a, Point b)? NearestEdge(Point w, double maxDist) =>
        Edges().Select(e => (e, d: SegDist(w, e.a, e.b), inside: Inside(geo.Polys[e.on.Id], w)))
               .Where(x => x.d <= maxDist)
               .OrderBy(x => Math.Round(x.d, 3)).ThenBy(x => x.inside ? 0 : 1)
               .Select(x => ((Piece, string, Point, Point)?)x.e).FirstOrDefault();

    Piece EdgePieceAt(string key, int f) => plan.Pieces.FirstOrDefault(p =>
        p.IsEdgePiece && p.Floor == f && geo.Polys.ContainsKey(p.On) && EdgeKey(geo.Edge(p.On, p.Edge).A, geo.Edge(p.On, p.Edge).B) == key);

    // ---------- Опора: как в Rust, блок встаёт только туда, где его что-то держит ----------

    /// Несущий блок: стена, проём, окно, каркас. Полустена низкая — на неё ничего не ставится.
    static bool Bearing(Piece p) => p != null && p.IsEdgePiece && p.Type != "halfwall";

    IEnumerable<string> TileEdgeKeys(string tile)
    {
        var v = geo.Polys[tile];
        for (int i = 0; i < v.Length; i++) yield return EdgeKey(v[i], v[(i + 1) % v.Length]);
    }

    /// Клетки-соседи по общей стороне
    IEnumerable<string> Neighbors(string tile)
    {
        var mine = TileEdgeKeys(tile).ToHashSet();
        return Foundations.Where(o => o.Id != tile && TileEdgeKeys(o.Id).Any(mine.Contains)).Select(o => o.Id);
    }

    bool HasAt(string tile, int f, string type) => plan.Pieces.Any(p => p.Type == type && p.On == tile && p.Floor == f);

    /// Под клеткой на этаже f стоит хотя бы одна несущая стена этажа f-1
    bool WallBelow(string tile, int f) => f > 1 && TileEdgeKeys(tile).Any(k => Bearing(EdgePieceAt(k, f - 1)));

    /// Почему блок здесь не держится (null — держится)
    string NoSupport(Tool t, string tile, int f, string edgeKey = null)
    {
        switch (t)
        {
            case Tool.Wall or Tool.Doorway or Tool.Window or Tool.Halfwall or Tool.Frame:
                if (f == 1) return null;   // на фундаменте
                if (Bearing(EdgePieceAt(edgeKey, f - 1))) return null;
                if (Foundations.Any(c => TileEdgeKeys(c.Id).Contains(edgeKey) && HasAt(c.Id, f, "floor"))) return null;
                return "Не на что поставить: под стеной нужна стена этажа ниже или перекрытие рядом.";
            case Tool.Floor:
                if (WallBelow(tile, f) || Neighbors(tile).Any(n => HasAt(n, f, "floor"))) return null;
                return "Перекрытие держится на стенах этажа ниже или цепляется к соседнему перекрытию.";
            case Tool.Roof:
                if (WallBelow(tile, f) || Neighbors(tile).Any(n => HasAt(n, f, "roof"))) return null;
                return "Крыша ставится на стены этажа ниже или цепляется к соседней крыше.";
            case Tool.Stairs or Tool.Tc or Tool.Item:
                if (f == 1 || HasAt(tile, f, "floor")) return null;
                return HasAt(tile, f, "roof") ? "Тут крыша — на неё ничего не ставится." : "Сначала положи перекрытие (пол этого этажа).";
        }
        return null;
    }

    /// Новый фундамент налезает на уже стоящие (касаться сторонами можно)
    bool Overlaps(Point[] poly)
    {
        static Point C(Point[] p) => new(p.Average(v => v.X), p.Average(v => v.Y));
        static IEnumerable<Point> Samples(Point[] p) { var c = C(p); return p.Select(v => c + (v - c) * 0.8).Append(c); }
        return Foundations.Any(f =>
        {
            var fp = geo.Polys[f.Id];
            return Samples(poly).Any(s => Inside(fp, s)) || Samples(fp).Any(s => Inside(poly, s));
        });
    }

    string NewId(string prefix)
    {
        var used = plan.Pieces.Select(p => p.Id).Concat(plan.Interiors.SelectMany(i => i.Pieces).Select(p => p.Id)).ToHashSet();
        for (int n = 1; ; n++) if (!used.Contains(prefix + n)) return prefix + n;
    }

    // ---------- Что делает клик ----------

    Proposal Propose(Point w, Tool t)
    {
        if (geo == null) return new(false, "Сначала исправь ошибку в схеме.");
        switch (t)
        {
            case Tool.Square:
            {
                if (floor != 1) return new(false, "Фундаменты ставятся только на 1 этаже.");
                int x = (int)Math.Floor(w.X), y = (int)Math.Floor(w.Y);
                var cell = new[] { new Point(x, y), new Point(x + 1, y), new Point(x + 1, y + 1), new Point(x, y + 1) };
                if (!Foundations.Any())   // первый фундамент — в любую клетку сетки
                {
                    if (x < World.Left || y < World.Top || x + 1 > World.Right || y + 1 > World.Bottom) return new(false, "За краем сетки.", cell);
                    return new(true, "Клик — первый квадратный фундамент.", cell, Apply: () =>
                        plan.Pieces.Add(new Piece { Id = NewId("f"), Type = "foundation", X = x, Y = y }));
                }
                // как в Rust: квадрат прилипает к свободной стороне любого фундамента — и квадрата, и треугольника
                var use = EdgeUse();
                var cand = Edges()
                    .Where(e => use.GetValueOrDefault(EdgeKey(e.a, e.b)) == 1)
                    .Where(e => (w - Mid(e.a, e.b)) * geo.EdgeNormal(e.on.Id, e.edge) > 0)
                    .Select(e => (e, poly: PlanGeometry.Attached(e.a, e.b, false)))
                    .Where(q => Inside(q.poly, w))
                    .OrderBy(q => SegDist(w, q.e.a, q.e.b)).ToList();
                if (cand.Count == 0)
                    return new(false, "Фундамент ставится вплотную к другому фундаменту: наведи курсор рядом со свободной стороной квадрата или треугольника.",
                               Foundations.Any(f => Inside(geo.Polys[f.Id], w)) ? null : cell);
                var ((on, edge, ea, eb), sq) = cand[0];
                if (Overlaps(sq)) return new(false, "Здесь квадрат налезет на другой фундамент.", sq);
                if (sq.Any(v => !World.Contains(v))) return new(false, "За краем сетки.", sq);
                // ровный квадрат по сетке храним как x, y; повёрнутый (к стороне треугольника) — как «прикреплён к стороне»
                var min = new Point(sq.Min(v => v.X), sq.Min(v => v.Y));
                bool onGrid = sq.All(v => Math.Abs(v.X - Math.Round(v.X)) < 1e-6 && Math.Abs(v.Y - Math.Round(v.Y)) < 1e-6);
                return new(true, onGrid ? "Клик — квадратный фундамент." : "Клик — квадратный фундамент к стороне треугольника (под углом, как в Rust).", sq, Apply: () =>
                    plan.Pieces.Add(onGrid
                        ? new Piece { Id = NewId("f"), Type = "foundation", X = (int)Math.Round(min.X), Y = (int)Math.Round(min.Y) }
                        : new Piece { Id = NewId("f"), Type = "foundation", On = on.Id, Edge = edge }));
            }
            case Tool.Triangle:
            {
                if (floor != 1) return new(false, "Фундаменты ставятся только на 1 этаже.");
                if (!Foundations.Any()) return new(false, "Треугольник крепится к стороне фундамента — сначала поставь квадрат.");
                var use = EdgeUse();
                var best = Edges()
                    .Where(e => use.GetValueOrDefault(EdgeKey(e.a, e.b)) == 1)
                    .Where(e => (w - Mid(e.a, e.b)) * geo.EdgeNormal(e.on.Id, e.edge) > 0)
                    .Select(e => (e, d: SegDist(w, e.a, e.b))).Where(x => x.d < 0.75).OrderBy(x => x.d).Select(x => x.e).ToList();
                if (best.Count == 0) return new(false, "Наведи курсор снаружи у свободной стороны фундамента.");
                var (on, edge, a, b) = best[0];
                var apex = Mid(a, b) + geo.EdgeNormal(on.Id, edge) * TriH;
                var tri = new[] { b, a, apex };
                var cen = new Point((a.X + b.X + apex.X) / 3, (a.Y + b.Y + apex.Y) / 3);
                if (Overlaps(tri))
                    return new(false, "Здесь треугольник налезет на другой фундамент.", tri);
                if (!World.Contains(apex)) return new(false, "За краем сетки.", tri);
                return new(true, "Клик — треугольный фундамент.", tri, Apply: () =>
                    plan.Pieces.Add(new Piece { Id = NewId("t"), Type = "triangle", On = on.Id, Edge = edge }));
            }
            case Tool.Wall or Tool.Doorway or Tool.Window or Tool.Halfwall or Tool.Frame:
            {
                var e = NearestEdge(w, 0.3);
                if (e == null) return new(false, "Наведи курсор на сторону клетки.");
                var (on, edge, a, b) = e.Value;
                var type = t switch { Tool.Wall => "wall", Tool.Doorway => "doorway", Tool.Window => "window", Tool.Frame => "frame", _ => "halfwall" };
                var old = EdgePieceAt(EdgeKey(a, b), floor);
                if (NoSupport(t, on.Id, floor, EdgeKey(a, b)) is string why) return new(false, why, Seg: (a, b));
                var piece = new Piece { Type = type, On = on.Id, Edge = edge, Floor = floor };
                if (t is Tool.Doorway or Tool.Frame) { piece.Door = TagOf(DoorBox); piece.Lock = piece.Door == null ? null : TagOf(LockBox); piece.Entrance = EntranceBox.IsChecked == true; }
                if (t == Tool.Window) piece.Embrasure = TagOf(EmbrasureBox);
                string what = new Piece { Type = type }.TypeName.ToLower();
                return new(true, old == null ? $"Клик — {what}." : $"Клик — заменить «{old.TypeName.ToLower()}» на «{what}».", Seg: (a, b), Apply: () =>
                {
                    if (old != null) plan.Pieces.Remove(old);
                    if (piece.Entrance) foreach (var p in plan.Pieces) p.Entrance = false;   // главный вход один
                    piece.Id = NewId(type switch { "wall" => "w", "doorway" => "d", "window" => "o", "frame" => "g", _ => "h" });
                    plan.Pieces.Add(piece);
                });
            }
            case Tool.Floor or Tool.Roof:
            {
                bool roof = t == Tool.Roof;
                if (floor == 1) return new(false, (roof ? "Крыша" : "Перекрытие") + " кладётся сверху — выбери этаж 2 или выше.");
                var cell = FoundationAt(w);
                if (cell == null) return new(false, "Наведи курсор на клетку базы.");
                var poly = geo.Polys[cell.Id];
                var old = plan.Pieces.FirstOrDefault(p => p.IsCover && p.On == cell.Id && p.Floor == floor);
                // крыша — скат: нижний край там, к какой стороне клетки ближе курсор
                string low = null;
                (Point a, Point b)? lowSeg = null;
                if (roof)
                {
                    var names = geo.Triangles.Contains(cell.Id) ? new[] { "B", "L", "R" } : new[] { "N", "E", "S", "W" };
                    low = names.OrderBy(n => { var (ea, eb) = geo.Edge(cell.Id, n); return SegDist(w, ea, eb); }).First();
                    lowSeg = geo.Edge(cell.Id, low);
                }
                if (old != null && old.Type == (roof ? "roof" : "floor") && (!roof || old.Edge == low))
                    return new(false, (roof ? "Крыша с таким скатом" : "Перекрытие") + " здесь уже есть." + (roof ? " Наведи к другой стороне, чтобы повернуть скат." : ""), poly, lowSeg);
                if (plan.Pieces.Any(p => p.Type == "stairs" && p.On == cell.Id && p.Floor == floor - 1)) return new(false, "Под этой клеткой подъём — тут нужен проход.", poly);
                if (NoSupport(t, cell.Id, floor) is string why) return new(false, why, poly);
                if (roof && plan.Pieces.Any(p => p.Floor == floor && p.On == cell.Id && (p.Type is "stairs" or "tc" || p.IsItem)))
                    return new(false, "На этой клетке стоит подъём, шкаф или мебель — крышу сюда не положить.", poly);
                string what = roof
                    ? (geo.Triangles.Contains(cell.Id) ? "треугольная крыша" : "квадратная крыша") + ", скат вниз к подсвеченной стороне"
                    : $"перекрытие (пол {floor} этажа)";
                return new(true, old == null ? $"Клик — {what}." : $"Клик — заменить на: {what}.", poly, lowSeg, Apply: () =>
                {
                    if (old != null) plan.Pieces.Remove(old);
                    plan.Pieces.Add(new Piece { Id = NewId(roof ? "r" : "c"), Type = roof ? "roof" : "floor", On = cell.Id, Floor = floor, Edge = low });
                });
            }
            case Tool.Stairs:
            {
                var cell = FoundationAt(w);
                if (cell == null) return new(false, "Наведи курсор на клетку базы.");
                var poly = geo.Polys[cell.Id];
                if (plan.Pieces.Any(p => p.On == cell.Id && p.Floor == floor && p.Type is "stairs" or "tc" || p.On == cell.Id && p.Floor == floor && p.IsItem))
                    return new(false, "Клетка занята (подъём, шкаф или мебель).", poly);
                if (plan.Pieces.Any(p => p.Type == "roof" && p.On == cell.Id && p.Floor == floor + 1)) return new(false, "Над этой клеткой крыша — подъёму некуда вести.", poly);
                if (NoSupport(Tool.Stairs, cell.Id, floor) is string why) return new(false, why, poly);
                return new(true, geo.Triangles.Contains(cell.Id) ? "Клик — спиральный подъём (треугольный)." : "Клик — подъём на следующий этаж.", poly, Apply: () =>
                    plan.Pieces.Add(new Piece { Id = NewId("s"), Type = "stairs", On = cell.Id, Floor = floor }));
            }
            case Tool.Tc:
            {
                var cell = FoundationAt(w);
                if (cell == null) return new(false, "Наведи курсор на клетку базы.");
                if (plan.Pieces.Any(p => p.On == cell.Id && p.Floor == floor && p.Type == "stairs")) return new(false, "Тут подъём.", geo.Polys[cell.Id]);
                if (NoSupport(Tool.Tc, cell.Id, floor) is string why) return new(false, why, geo.Polys[cell.Id]);
                var old = plan.Pieces.FirstOrDefault(p => p.Type == "tc");
                return new(true, old == null ? "Клик — шкаф (TC)." : "Клик — перенести шкаф сюда (он один на базу).", Dot: geo.Center(cell.Id), Apply: () =>
                {
                    if (old != null) plan.Pieces.Remove(old);
                    plan.Pieces.Add(new Piece { Id = "tc", Type = "tc", On = cell.Id, Floor = floor });
                });
            }
            case Tool.Item:
            {
                var cell = FoundationAt(w);
                if (cell == null) return new(false, "Наведи курсор на клетку базы.");
                if (plan.Pieces.Any(p => p.On == cell.Id && p.Floor == floor && p.Type == "stairs")) return new(false, "Тут подъём — мебель не поместится.", geo.Polys[cell.Id]);
                if (NoSupport(Tool.Item, cell.Id, floor) is string why) return new(false, why, geo.Polys[cell.Id]);
                int slots = geo.Triangles.Contains(cell.Id) ? 3 : 4;
                int slot = Enumerable.Range(0, slots).OrderBy(s => (PlanRenderer.ItemPos(geo, cell.Id, s) - w).Length).First();
                var at = PlanRenderer.ItemPos(geo, cell.Id, slot);
                var type = TagOf(ItemBox) ?? "box";
                var old = plan.Pieces.FirstOrDefault(p => p.IsItem && p.On == cell.Id && p.Floor == floor && p.Slot == slot);
                string what = new Piece { Type = type }.TypeName.ToLower();
                return new(true, old == null ? $"Клик — {what}." : $"Клик — заменить «{old.TypeName.ToLower()}» на «{what}».", Dot: at, Apply: () =>
                {
                    if (old != null) plan.Pieces.Remove(old);
                    plan.Pieces.Add(new Piece { Id = NewId(type), Type = type, On = cell.Id, Floor = floor, Slot = slot });
                });
            }
            default:
            {
                var target = EraseTarget(w);
                if (target == null) return new(false, "Наведи курсор на блок, который убрать.");
                string extra = target.IsFoundation && Dependents(target).Count > 1 ? $" и всё, что на нём ({Dependents(target).Count - 1})" : "";
                var ghost = GhostOf(target);
                return new(true, $"Клик — убрать «{target.TypeName.ToLower()}»{extra}.", ghost.poly, ghost.seg, ghost.dot, () =>
                {
                    foreach (var p in Dependents(target)) plan.Pieces.Remove(p);
                });
            }
        }
    }

    /// Блок под курсором на текущем этаже: стена → мебель → шкаф/подъём/перекрытие → фундамент
    Piece EraseTarget(Point w)
    {
        if (geo == null) return null;
        var e = NearestEdge(w, 0.2);
        if (e != null && EdgePieceAt(EdgeKey(e.Value.a, e.Value.b), floor) is Piece wall) return wall;
        var item = plan.Pieces.Where(p => p.IsItem && p.Floor == floor && geo.Polys.ContainsKey(p.On))
                              .Select(p => (p, d: (PlanRenderer.ItemPos(geo, p.On, p.Slot ?? 0) - w).Length))
                              .Where(x => x.d < 0.2).OrderBy(x => x.d).Select(x => x.p).FirstOrDefault();
        if (item != null) return item;
        var cell = FoundationAt(w);
        if (cell == null) return null;
        return plan.Pieces.FirstOrDefault(p => p.On == cell.Id && p.Floor == floor && p.Type == "tc")
            ?? plan.Pieces.FirstOrDefault(p => p.On == cell.Id && p.Floor == floor && p.Type == "stairs")
            ?? plan.Pieces.FirstOrDefault(p => p.On == cell.Id && p.Floor == floor && p.IsCover)
            ?? (floor == 1 ? cell : null);
    }

    /// Блок и всё, что на нём держится (у фундамента — стены, треугольники и т. д.)
    List<Piece> Dependents(Piece root)
    {
        var res = new List<Piece> { root };
        if (!root.IsFoundation) return res;
        for (int i = 0; i < res.Count; i++)
            foreach (var p in plan.Pieces.Where(p => p.On == res[i].Id && !res.Contains(p))) res.Add(p);
        return res;
    }

    (Point[] poly, (Point, Point)? seg, Point? dot) GhostOf(Piece p)
    {
        if (p.IsFoundation) return (geo.Polys[p.Id], null, null);
        if (p.IsEdgePiece) return (null, geo.Edge(p.On, p.Edge), null);
        if (p.IsItem) return (null, null, PlanRenderer.ItemPos(geo, p.On, p.Slot ?? 0));
        if (p.Type == "tc") return (null, null, geo.Center(p.On));
        return (geo.Polys[p.On], null, null);
    }

    // ---------- Мышь ----------

    void Board_MouseMove(object sender, MouseEventArgs e) { mouse = e.GetPosition(PlanLayer); DrawHover(); }
    void Board_MouseLeave(object sender, MouseEventArgs e) { mouse = null; DrawHover(); }

    void Board_LeftDown(object sender, MouseButtonEventArgs e) => Act(Propose(ToWorld(e.GetPosition(PlanLayer)), tool));
    void Board_RightDown(object sender, MouseButtonEventArgs e) => Act(Propose(ToWorld(e.GetPosition(PlanLayer)), Tool.Erase));

    void Act(Proposal pr)
    {
        if (!pr.Ok || pr.Apply == null) { HintText.Text = pr.Hint; return; }
        PushUndo();
        pr.Apply();
        dirty = piecesChanged = true;
        Render();
    }

    void DrawHover()
    {
        HoverLayer.Children.Clear();
        if (mouse == null) { HintText.Text = DefaultHint(); return; }
        bool erase = Mouse.RightButton == MouseButtonState.Pressed;
        var pr = Propose(ToWorld(mouse.Value), erase ? Tool.Erase : tool);
        HintText.Text = pr.Hint;
        var col = !pr.Ok ? Color.FromRgb(0xFF, 0x5A, 0x5A) : tool == Tool.Erase ? Color.FromRgb(0xFF, 0x8A, 0x5A) : PlanRenderer.Accent;
        var stroke = new SolidColorBrush(col);
        var fill = new SolidColorBrush(Color.FromArgb(0x55, col.R, col.G, col.B));
        if (pr.Poly != null)
        {
            var poly = new Shapes.Polygon { Stroke = stroke, Fill = fill, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } };
            foreach (var p in pr.Poly) poly.Points.Add(ToScreen(p));
            HoverLayer.Children.Add(poly);
        }
        if (pr.Seg is var (a, b))
        {
            Point sa = ToScreen(a), sb = ToScreen(b);
            HoverLayer.Children.Add(new Shapes.Line { X1 = sa.X, Y1 = sa.Y, X2 = sb.X, Y2 = sb.Y, Stroke = stroke, StrokeThickness = 9, Opacity = 0.75,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        if (pr.Dot is Point d)
        {
            var s = ToScreen(d);
            double r = Math.Max(9, Xf.s * 0.17);
            var el = new Shapes.Ellipse { Width = r * 2, Height = r * 2, Stroke = stroke, Fill = fill, StrokeThickness = 2 };
            Canvas.SetLeft(el, s.X - r); Canvas.SetTop(el, s.Y - r);
            HoverLayer.Children.Add(el);
        }
    }

    string DefaultHint() => plan.Pieces.Count == 0
        ? "Начни с фундамента: выбери «▢ Фундамент» и кликай по клеткам. Потом стены, двери, перекрытия."
        : $"Этаж {floor}. Наведи курсор на сетку — подсветится, куда встанет блок.";

    // ---------- Отрисовка ----------

    void Render()
    {
        try { geo = new PlanGeometry(plan); ErrorText.Text = ""; }
        catch (Exception ex) { geo = null; ErrorText.Text = "Ошибка в схеме: " + ex.Message; }
        DrawGrid();
        if (geo != null) PlanRenderer.Draw(PlanLayer, plan, geo, _ => PieceState.Done, floor, bounds: World);
        else PlanLayer.Children.Clear();
        BuildFloorTabs();
        UpdateStats();
        DrawHover();
    }

    void DrawGrid()
    {
        GridLayer.Children.Clear();
        var line = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255));
        void L(Point a, Point b)
        {
            Point sa = ToScreen(a), sb = ToScreen(b);
            GridLayer.Children.Add(new Shapes.Line { X1 = sa.X, Y1 = sa.Y, X2 = sb.X, Y2 = sb.Y, Stroke = line, StrokeThickness = 1 });
        }
        for (double x = World.Left; x <= World.Right; x++) L(new Point(x, World.Top), new Point(x, World.Bottom));
        for (double y = World.Top; y <= World.Bottom; y++) L(new Point(World.Left, y), new Point(World.Right, y));
        var label = new TextBlock { Text = $"ЭТАЖ {floor}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)) };
        Canvas.SetLeft(label, 8); Canvas.SetTop(label, 6);
        GridLayer.Children.Add(label);
    }

    void UpdateStats()
    {
        var P = plan.Pieces;
        int sq = P.Count(p => p.Type == "foundation"), tri = P.Count(p => p.Type == "triangle");
        StatsText.Text = $"Фундаменты: {sq} квадр. + {tri} треуг.\n" +
                         $"Стены и проёмы: {P.Count(p => p.IsEdgePiece)} · двери: {P.Count(p => p.Door != null)}\n" +
                         $"Бойницы: {P.Count(p => p.HasEmbrasure)} · перекрытия: {P.Count(p => p.Type == "floor")} · крыши: {P.Count(p => p.Type == "roof")}\n" +
                         $"Мебель: {P.Count(p => p.IsItem)} · этажей: {plan.FloorCount}";
        CostText.Text = geo != null && P.Count > 0 ? $"Ресурсы (до {TierBox.Text}): {plan.TotalCost(geo)}" : "";

        var checks = new List<string>();
        if (sq + tri == 0) checks.Add("• Нет ни одного фундамента.");
        if (!P.Any(p => p.Type == "tc")) checks.Add("⚠ Нет шкафа (TC) — без него базу застроят или снесут.");
        if (!P.Any(p => p.Type is "doorway" or "frame")) checks.Add("⚠ Нет ни одного дверного проёма.");
        else if (!P.Any(p => p.Entrance)) checks.Add("⚠ Отметь главный вход: «Дверной проём» → галочка «Главный вход».");
        if (geo != null)
        {
            var use = EdgeUse();
            int open = Edges().Count(e => use.GetValueOrDefault(EdgeKey(e.a, e.b)) == 1 && EdgePieceAt(EdgeKey(e.a, e.b), 1) == null);
            if (sq + tri > 0 && open > 0) checks.Add($"⚠ Дыр в наружных стенах 1 этажа: {open}.");
            // стена верхнего этажа держится на стене под ней или на перекрытии рядом
            var walls = P.Where(p => p.IsEdgePiece && geo.Polys.ContainsKey(p.On))
                         .Select(p => (p, key: EdgeKey(geo.Edge(p.On, p.Edge).A, geo.Edge(p.On, p.Edge).B))).ToList();
            var cellsByEdge = Edges().GroupBy(e => EdgeKey(e.a, e.b)).ToDictionary(g => g.Key, g => g.Select(e => e.on.Id).ToHashSet());
            int hanging = walls.Count(w => w.p.Floor > 1
                && !walls.Any(u => u.p.Floor == w.p.Floor - 1 && u.key == w.key)
                && !P.Any(f => f.IsCover && f.Floor == w.p.Floor && cellsByEdge.GetValueOrDefault(w.key)?.Contains(f.On) == true));
            if (hanging > 0) checks.Add($"⚠ Стен, которые висят в воздухе (ни стены под ними, ни перекрытия рядом): {hanging}.");
            for (int f = 2; f <= plan.FloorCount; f++)
                // глухие стены и перекрытия наверху (вторая крыша) — не повод для подъёма; двери, окна и мебель — повод
                if (P.Any(p => p.Floor == f && (p.IsItem || p.Type is "doorway" or "window" or "tc" or "stairs")) && !P.Any(p => p.Type == "stairs" && p.Floor == f - 1))
                    checks.Add($"⚠ На {f} этаж нет подъёма с {f - 1} этажа.");
        }
        int holes = P.Count(p => p.Type == "window" && !p.HasEmbrasure);
        if (holes > 0) checks.Add($"⚠ Окон без бойницы: {holes}. Пустое окно — дыра в дом, ставь в каждое бойницу.");
        int depth = RaidDepth();
        if (depth >= 0)
        {
            string layers = depth == 1 ? "слой" : depth is >= 2 and <= 4 ? "слоя" : "слоёв";
            checks.Insert(0, $"Шкаф: снаружи до него {depth} {layers} (стены, двери, перекрытия — по самому слабому пути).");
            if (depth <= 1 && sq + tri > 3)
                checks.Add("⚠ До шкафа всего один слой — такую базу рейдят очень дёшево. Добавь буфферки/соты вокруг шкафа, комнату для шкафа с дверью или вторую крышу над ним.");
        }
        if (checks.Count == 0) checks.Add("✓ Всё на месте.");
        ChecksText.Text = string.Join("\n", checks);
    }

    /// Сколько минимум блоков надо сломать снаружи до шкафа: стены/двери/окна с бойницей и перекрытия — по 1,
    /// полустена и пустой проём — 0. Учитывает крышу и шахту подъёма. -1 — шкафа нет.
    int RaidDepth()
    {
        var tc = plan.Pieces.FirstOrDefault(p => p.Type == "tc");
        if (tc == null || geo == null || !geo.Polys.ContainsKey(tc.On)) return -1;
        var tiles = Foundations.Select(f => f.Id).ToList();
        int top = plan.Pieces.Select(p => p.Level).DefaultIfEmpty(1).Max() + 1;
        var cost = new Dictionary<string, int>();
        foreach (var p in plan.Pieces.Where(p => p.IsEdgePiece && geo.Polys.ContainsKey(p.On)))
        {
            var (a, b) = geo.Edge(p.On, p.Edge);
            bool open = p.Type == "halfwall" || (p.Type is "doorway" or "frame" && p.Door == null) || (p.Type == "window" && !p.HasEmbrasure);
            cost[$"{EdgeKey(a, b)}@{p.Floor}"] = open ? 0 : 1;
        }
        var floors = plan.Pieces.Where(p => p.IsCover).Select(p => $"{p.On}@{p.Floor}").ToHashSet();
        var byKey = new Dictionary<string, List<string>>();
        var keysOf = new Dictionary<string, List<string>>();
        foreach (var t in tiles)
        {
            var v = geo.Polys[t];
            keysOf[t] = new();
            for (int i = 0; i < v.Length; i++)
            {
                var k = EdgeKey(v[i], v[(i + 1) % v.Length]);
                keysOf[t].Add(k);
                if (!byKey.ContainsKey(k)) byKey[k] = new();
                byKey[k].Add(t);
            }
        }
        int E(string k, int l) => cost.GetValueOrDefault($"{k}@{l}");
        IEnumerable<(string, int)> Next(string n)
        {
            if (n == "out")
            {
                foreach (var t in tiles)
                {
                    for (int l = 1; l <= top; l++)
                        foreach (var k in keysOf[t]) if (byKey[k].Count == 1) yield return ($"{t}@{l}", E(k, l));
                    yield return ($"{t}@{top}", 0);
                }
                yield break;
            }
            int at = n.LastIndexOf('@');
            string tile = n[..at];
            int lv = int.Parse(n[(at + 1)..]);
            foreach (var k in keysOf[tile])
            {
                var others = byKey[k].Where(o => o != tile).ToList();
                if (others.Count == 0) yield return ("out", E(k, lv));
                foreach (var o in others) yield return ($"{o}@{lv}", E(k, lv));
            }
            if (lv < top) yield return ($"{tile}@{lv + 1}", floors.Contains($"{tile}@{lv + 1}") ? 1 : 0);
            if (lv > 1) yield return ($"{tile}@{lv - 1}", floors.Contains($"{tile}@{lv}") ? 1 : 0);
        }
        var goal = $"{tc.On}@{tc.Floor}";
        var dist = new Dictionary<string, int> { ["out"] = 0 };
        var queue = new PriorityQueue<string, int>();
        queue.Enqueue("out", 0);
        while (queue.TryDequeue(out var n, out var d))
        {
            if (d > dist[n]) continue;
            if (n == goal) return d;
            foreach (var (m, c) in Next(n))
                if (!dist.TryGetValue(m, out var old) || d + c < old) { dist[m] = d + c; queue.Enqueue(m, d + c); }
        }
        return -1;
    }

    // ---------- Отмена ----------

    void PushUndo()
    {
        undo.Push(JsonSerializer.Serialize(plan.Pieces, JsonOut));
        if (undo.Count > 200) { var keep = undo.Take(150).Reverse().ToList(); undo.Clear(); foreach (var s in keep) undo.Push(s); }
    }

    void Undo()
    {
        if (undo.Count == 0) { HintText.Text = "Отменять нечего."; return; }
        plan.Pieces = JsonSerializer.Deserialize<List<Piece>>(undo.Pop(), JsonIn);
        dirty = piecesChanged = true;
        Render();
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (plan.Pieces.Count == 0) return;
        if (MessageBox.Show(this, "Убрать все блоки? Это можно отменить (Ctrl+Z).", "Редактор баз", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        PushUndo();
        plan.Pieces.Clear();
        floor = 1;
        dirty = piecesChanged = true;
        SetTool(Tool.Square);
    }

    void FillFloor()
    {
        if (floor == 1) { floor = 2; }
        var cells = Foundations.Where(c =>
            !plan.Pieces.Any(p => p.IsCover && p.On == c.Id && p.Floor == floor) &&
            !plan.Pieces.Any(p => p.Type == "stairs" && p.On == c.Id && p.Floor == floor - 1)).ToList();
        if (cells.Count == 0) { HintText.Text = $"На этаже {floor} уже всё перекрыто."; Render(); return; }
        PushUndo();
        // кладём только то, что держится: на стенах снизу или цепочкой от соседнего перекрытия
        bool added = true;
        int count = 0;
        while (added)
        {
            added = false;
            foreach (var c in cells.ToList())
                if (NoSupport(Tool.Floor, c.Id, floor) == null)
                {
                    plan.Pieces.Add(new Piece { Id = NewId("c"), Type = "floor", On = c.Id, Floor = floor });
                    cells.Remove(c);
                    added = true;
                    count++;
                }
        }
        if (count == 0) { undo.Pop(); HintText.Text = "Перекрытию не на чем держаться — сначала поставь стены этажа ниже."; Render(); return; }
        dirty = piecesChanged = true;
        SetTool(Tool.Floor);
    }

    // ---------- Шаги постройки ----------

    List<Step> GenerateSteps()
    {
        var P = plan.Pieces;
        var steps = new List<Step>();
        var used = new HashSet<string>();
        void S(string text, IEnumerable<Piece> ps, int f)
        {
            var ids = ps.Where(p => used.Add(p.Id)).Select(p => p.Id).ToList();
            if (ids.Count > 0) steps.Add(new Step { Text = text, Pieces = ids, Floor = f });
        }

        S("Квадратные фундаменты. Начни с центра и иди наружу.", P.Where(p => p.Type == "foundation"), 1);
        S("Треугольные фундаменты по краям.", P.Where(p => p.Type == "triangle"), 1);
        int maxF = plan.FloorCount;
        for (int f = 1; f <= maxF; f++)
        {
            var on = P.Where(p => p.Floor == f && !p.IsFoundation).ToList();
            if (f == 1) S("Шкаф (TC) — сразу, пока никто не застроил рядом.", on.Where(p => p.Type == "tc"), f);
            S($"Стены {f} этажа.", on.Where(p => p.Type == "wall"), f);
            S("Дверные проёмы и двери с замками.", on.Where(p => p.Type == "doorway"), f);
            S("Настенные каркасы и гаражные/двойные двери.", on.Where(p => p.Type == "frame"), f);
            var win = on.Where(p => p.Type == "window").ToList();
            S(win.Any(p => p.HasEmbrasure) ? "Окна с бойницами — для отстрела." : "Оконные проёмы.", win, f);
            S("Полустены.", on.Where(p => p.Type == "halfwall"), f);
            S("Шкаф (TC).", on.Where(p => p.Type == "tc"), f);
            S("Подъём на следующий этаж.", on.Where(p => p.Type == "stairs"), f);
            S(f == maxF - 1 && !P.Any(p => p.Floor == f + 1 && !p.IsFoundation && p.Type != "floor") ? "Крыша." : $"Перекрытие {f + 1} этажа.",
              P.Where(p => p.Type == "floor" && p.Floor == f + 1), f + 1);
            S("Крыша: квадратные и треугольные скаты.", P.Where(p => p.Type == "roof" && p.Floor == f + 1), f + 1);
        }
        for (int f = 1; f <= maxF; f++)
            S($"Мебель на {f} этаже.", P.Where(p => p.IsItem && p.Floor == f), f);
        S("Остальные блоки.", P, 1);
        return steps;
    }

    // ---------- Сохранение и публикация ----------

    bool TrySave(out string path)
    {
        path = null;
        if (geo == null) { Say("В схеме ошибка: " + ErrorText.Text); return false; }
        if (!plan.Pieces.Any(p => p.IsFoundation)) { Say("Поставь хотя бы один фундамент."); return false; }
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Say("Впиши название базы."); NameBox.Focus(); return false; }

        plan.Name = name;
        plan.Description = DescBox.Text.Trim();
        plan.Author = string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim();
        plan.Tier = TagOf(TierBox) ?? "stone";
        if (piecesChanged || plan.Steps.SelectMany(s => s.Pieces).Distinct().Count() < plan.Pieces.Count) plan.Steps = GenerateSteps();

        // мебель из интерьеров, стоявшая на убранных фундаментах, тоже уходит
        var ids = plan.Pieces.Select(p => p.Id).ToHashSet();
        foreach (var it in plan.Interiors)
        {
            it.Pieces.RemoveAll(p => p.On != null && !ids.Contains(p.On));
            var itIds = it.Pieces.Select(p => p.Id).ToHashSet();
            foreach (var s in it.Steps) s.Pieces.RemoveAll(id => !itIds.Contains(id));
            it.Steps.RemoveAll(s => s.Pieces.Count == 0);
        }
        plan.Interiors.RemoveAll(i => i.Pieces.Count == 0);

        var json = JsonSerializer.Serialize(plan, JsonOut);
        try
        {
            if (editingPath != null && File.Exists(editingPath))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "RustBuilder-edit-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(tmp, json);
                try { var check = BuildPlan.Load(tmp); _ = new PlanGeometry(check); }
                finally { File.Delete(tmp); }
                File.WriteAllText(editingPath, json);
                path = editingPath;
            }
            else path = editingPath = BuildLibrary.ImportJson(json, Catalog.FileNameFor(plan));
        }
        catch (Exception ex) { Say("Не удалось сохранить: " + ex.Message); return false; }

        dirty = piecesChanged = false;
        plan.FullPath = path;
        plan.FileName = Path.GetFileName(path);
        plan.IsUser = true;
        Saved?.Invoke(path);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave(out var path)) return;
        HintText.Text = $"Сохранено в «Мои базы»: {Path.GetFileName(path)}. Её можно выбрать в списке баз и строить.";
    }

    void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave(out var path)) return;
        LibraryWindow.Publish(this, plan, File.ReadAllText(path));
    }

    /// Для проверки без мыши (--editor-snapshot): "Square 0.5 0.5; Window 1 0.5 vertical; floor 2; hover Wall 0 0.5"
    public void DevScript(string script)
    {
        static double D(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        static void Pick(ComboBox box, string tag) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.SelectedItem;
        foreach (var cmd in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var a = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a[0] == "floor") { floor = int.Parse(a[1]); Render(); continue; }
            if (a[0] == "fill") { FillFloor(); continue; }
            if (a[0] == "json") { plan.Name = NameBox.Text; plan.Steps = GenerateSteps(); File.WriteAllText(a[1], JsonSerializer.Serialize(plan, JsonOut)); continue; }
            if (a[0] == "name") { NameBox.Text = string.Join(' ', a.Skip(1)); continue; }
            bool hover = a[0] == "hover";
            if (hover) a = a.Skip(1).ToArray();
            var t = Enum.Parse<Tool>(a[0], true);
            if (tool != t) SetTool(t);
            if (a.Length > 3)
            {
                if (t == Tool.Window) Pick(EmbrasureBox, a[3]);
                if (t == Tool.Item) Pick(ItemBox, a[3]);
                if (t == Tool.Doorway) EntranceBox.IsChecked = a[3] == "entrance";
                if (t == Tool.Frame) Pick(DoorBox, a[3]);
            }
            var w = new Point(D(a[1]), D(a[2]));
            if (hover) { mouse = ToScreen(w); DrawHover(); }
            else Act(Propose(w, t));
        }
    }

    void Say(string text) => MessageBox.Show(this, text, "Редактор баз", MessageBoxButton.OK, MessageBoxImage.Information);
}
