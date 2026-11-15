using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace RustBuilder;

// ---------- Формат файла постройки (Builds/*.json) ----------

public class BuildPlan
{
    public string Name { get; set; } = "Без названия";
    public string Description { get; set; } = "";
    public string Author { get; set; }
    /// twig | wood | stone | metal | hqm — до чего улучшать по умолчанию
    public string Tier { get; set; } = "stone";
    public List<Piece> Pieces { get; set; } = new();
    public List<Step> Steps { get; set; } = new();

    [JsonIgnore] public string FileName { get; set; }
    [JsonIgnore] public string FullPath { get; set; }
    /// true — база пользователя (можно удалить), false — встроенная
    [JsonIgnore] public bool IsUser { get; set; }

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BuildPlan Load(string path)
    {
        var plan = JsonSerializer.Deserialize<BuildPlan>(File.ReadAllText(path), Opts)
                   ?? throw new Exception("пустой файл");
        plan.FileName = Path.GetFileName(path);
        plan.FullPath = Path.GetFullPath(path);
        plan.Steps ??= new();
        plan.Pieces ??= new();
        return plan;
    }

    public Res TotalCost(PlanGeometry geo)
    {
        var total = new Res();
        foreach (var p in Pieces) total.Add(Costs.Of(p, this, geo));
        return total;
    }

    public int FloorCount => Pieces.Count == 0 ? 1 : Pieces.Max(p => p.Level);
}

/// Встроенные базы (папка Builds рядом с exe) + базы пользователя (%AppData%\RustBuilder\Builds)
public static class BuildLibrary
{
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "Builds");

    public static List<BuildPlan> LoadAll(List<string> errors)
    {
        var list = new List<BuildPlan>();
        Directory.CreateDirectory(AppSettings.UserBuildsDir);
        foreach (var (dir, isUser) in new[] { (BundledDir, false), (AppSettings.UserBuildsDir, true) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var plan = BuildPlan.Load(f);
                    plan.IsUser = isUser;
                    list.Add(plan);
                }
                catch (Exception ex) { errors.Add($"Файл {Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        return list;
    }

    /// Проверяет файл и копирует его в папку пользователя. Возвращает путь к копии.
    public static string Import(string sourcePath)
    {
        var plan = BuildPlan.Load(sourcePath);   // бросит исключение, если JSON битый
        _ = new PlanGeometry(plan);              // бросит, если схема некорректна
        Directory.CreateDirectory(AppSettings.UserBuildsDir);

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var dest = Path.Combine(AppSettings.UserBuildsDir, name + ".json");
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(AppSettings.UserBuildsDir, $"{name} ({i}).json");
        File.Copy(sourcePath, dest);
        return dest;
    }

    /// Сохраняет скачанный JSON в папку пользователя (с той же проверкой, что и обычная загрузка)
    public static string ImportJson(string json, string fileName)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "RustBuilder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var tmp = Path.Combine(tmpDir, Path.GetFileName(fileName));
            File.WriteAllText(tmp, json);
            return Import(tmp);
        }
        finally { Directory.Delete(tmpDir, recursive: true); }
    }

    public static void Delete(BuildPlan plan)
    {
        if (!plan.IsUser) throw new InvalidOperationException("Встроенные базы удалить нельзя");
        File.Delete(plan.FullPath);
    }
}

public class Piece
{
    public string Id { get; set; }
    /// foundation | triangle | wall | doorway | window | halfwall | floor | tc
    public string Type { get; set; }
    /// Для квадратного фундамента: клетка сетки
    public int X { get; set; }
    public int Y { get; set; }
    /// Id фундамента/треугольника, к которому крепится блок
    public string On { get; set; }
    /// Сторона: N/E/S/W у квадрата, L/R у треугольника (если смотреть от основания наружу)
    public string Edge { get; set; }
    /// Этаж: 1 — первый. У перекрытия (floor) этаж = на каком уровне оно лежит (2 = пол второго этажа)
    public int Floor { get; set; } = 1;
    public string Tier { get; set; }
    /// Для doorway: wood | sheet | armored
    public string Door { get; set; }
    /// Для doorway: code | key
    public string Lock { get; set; }
    /// Для doorway: это главный вход (на схеме рисуется стрелка «вход»)
    public bool Entrance { get; set; }

    public bool IsFoundation => Type is "foundation" or "triangle";
    public bool IsEdgePiece => Type is "wall" or "doorway" or "window" or "halfwall";
    public int Level => IsFoundation ? 1 : Floor;

    public string TypeName => Type switch
    {
        "foundation" => "Квадратный фундамент",
        "triangle" => "Треугольный фундамент",
        "wall" => "Стена",
        "doorway" => "Дверной проём",
        "window" => "Оконный проём",
        "halfwall" => "Полустена",
        "floor" => "Перекрытие",
        "stairs" => "Лестница (L-образная)",
        "tc" => "Шкаф (TC)",
        _ => Type,
    };

    public string DoorName => Door?.ToLower() switch
    {
        "wood" => "деревянная дверь",
        "sheet" => "металлическая дверь",
        "armored" => "бронированная дверь",
        _ => null,
    };

    public string LockName => Lock?.ToLower() switch
    {
        "code" => "кодовый замок",
        "key" => "замок с ключом",
        _ => null,
    };
}

public class Step
{
    public string Text { get; set; } = "";
    public List<string> Pieces { get; set; } = new();
    public int? Floor { get; set; }
}

// ---------- Геометрия сетки ----------

public class PlanGeometry
{
    public readonly Dictionary<string, Point[]> Polys = new();
    public readonly HashSet<string> Triangles = new();
    static readonly double H = Math.Sqrt(3) / 2;

    public PlanGeometry(BuildPlan plan)
    {
        var ids = new HashSet<string>();
        foreach (var p in plan.Pieces)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) throw new Exception($"у блока типа '{p.Type}' нет id");
            if (!ids.Add(p.Id)) throw new Exception($"повторяется id '{p.Id}'");
        }

        foreach (var p in plan.Pieces.Where(p => p.Type == "foundation"))
            Polys[p.Id] = new[] { new Point(p.X, p.Y), new Point(p.X + 1, p.Y), new Point(p.X + 1, p.Y + 1), new Point(p.X, p.Y + 1) };

        // треугольники могут цепляться друг за друга — решаем в несколько проходов
        var pending = plan.Pieces.Where(p => p.Type == "triangle").ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(t => t.On != null && Polys.ContainsKey(t.On)).ToList();
            if (ready.Count == 0)
                throw new Exception($"треугольник '{pending[0].Id}' ссылается на несуществующий фундамент '{pending[0].On}'");
            foreach (var t in ready)
            {
                var (a, b) = Edge(t.On, t.Edge);
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var d = b - a;
                var n = new Vector(d.Y, -d.X); // наружная нормаль (обход по часовой, ось Y вниз)
                Polys[t.Id] = new[] { b, a, mid + n * H };
                Triangles.Add(t.Id);
                pending.Remove(t);
            }
        }

        foreach (var p in plan.Pieces.Where(p => !p.IsFoundation))
        {
            if (p.On == null || !Polys.ContainsKey(p.On))
                throw new Exception($"блок '{p.Id}' ссылается на несуществующий фундамент '{p.On}'");
            if (p.IsEdgePiece) Edge(p.On, p.Edge); // проверка стороны
        }
    }

    public Point Center(string id)
    {
        var v = Polys[id];
        return new Point(v.Average(p => p.X), v.Average(p => p.Y));
    }

    /// Направление «наружу» от фундамента через указанную сторону
    public Vector EdgeNormal(string id, string edge)
    {
        var (a, b) = Edge(id, edge);
        var d = b - a;
        var n = new Vector(d.Y, -d.X);
        n.Normalize();
        return n;
    }

    public (Point A, Point B) Edge(string id, string edge)
    {
        var v = Polys[id];
        int i;
        if (Triangles.Contains(id))
            i = edge?.ToUpper() switch { "L" => 1, "R" => 2, "B" => 0, _ => throw new Exception($"у треугольника '{id}' стороны только L/R, а указано '{edge}'") };
        else
            i = edge?.ToUpper() switch { "N" => 0, "E" => 1, "S" => 2, "W" => 3, _ => throw new Exception($"у квадрата '{id}' стороны N/E/S/W, а указано '{edge}'") };
        return (v[i], v[(i + 1) % v.Length]);
    }
}

// ---------- Ресурсы (примерные значения, сверяйте с актуальной версией Rust) ----------

public class Res
{
    public int Wood, Stone, Metal, Hqm, Gears;

    public void Add(Res r) { Wood += r.Wood; Stone += r.Stone; Metal += r.Metal; Hqm += r.Hqm; Gears += r.Gears; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Wood > 0) parts.Add($"дерево {Wood}");
        if (Stone > 0) parts.Add($"камень {Stone}");
        if (Metal > 0) parts.Add($"металл {Metal}");
        if (Hqm > 0) parts.Add($"МВК {Hqm}");
        if (Gears > 0) parts.Add($"шестерни {Gears}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}

public static class Costs
{
    // солома (twig), только дерево
    static readonly Dictionary<string, int> Twig = new()
    {
        ["foundation"] = 50, ["triangle"] = 25, ["wall"] = 50, ["doorway"] = 35,
        ["window"] = 35, ["halfwall"] = 25, ["floor"] = 25, ["trifloor"] = 13, ["stairs"] = 50,
    };

    // улучшение: дерево, камень, металл, МВК
    static readonly Dictionary<string, int[]> Upgrade = new()
    {
        ["foundation"] = new[] { 200, 300, 200, 25 },
        ["triangle"] = new[] { 100, 150, 100, 13 },
        ["wall"] = new[] { 200, 300, 200, 25 },
        ["doorway"] = new[] { 140, 210, 140, 18 },
        ["window"] = new[] { 140, 210, 140, 18 },
        ["halfwall"] = new[] { 100, 150, 100, 13 },
        ["floor"] = new[] { 100, 150, 100, 13 },
        ["trifloor"] = new[] { 50, 75, 50, 7 },
        ["stairs"] = new[] { 200, 300, 200, 25 },
    };

    public static Res Of(Piece p, BuildPlan plan, PlanGeometry geo)
    {
        var r = new Res();
        if (p.Type == "tc") { r.Wood = 1000; return r; }

        var key = p.Type == "floor" && geo.Triangles.Contains(p.On) ? "trifloor" : p.Type;
        if (!Twig.ContainsKey(key)) return r;
        r.Wood += Twig[key];

        var tier = (p.Tier ?? plan.Tier ?? "twig").ToLower();
        var up = Upgrade[key];
        switch (tier)
        {
            case "wood": r.Wood += up[0]; break;
            case "stone": r.Stone += up[1]; break;
            case "metal": r.Metal += up[2]; break;
            case "hqm": r.Hqm += up[3]; break;
        }

        switch (p.Door?.ToLower())
        {
            case "wood": r.Wood += 300; break;
            case "sheet": r.Metal += 150; break;
            case "armored": r.Hqm += 20; r.Gears += 5; break;
        }
        switch (p.Lock?.ToLower())
        {
            case "code": r.Metal += 100; break;
            case "key": r.Wood += 75; break;
        }
        return r;
    }
}
