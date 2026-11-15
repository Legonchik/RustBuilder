using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustBuilder;

public class CatalogEntry
{
    public string File { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Author { get; set; }
    public string Added { get; set; }
    public string Tier { get; set; }
    public int Foundations { get; set; }
    public int Floors { get; set; }
    public int Steps { get; set; }
    /// Номер issue со страницей лайков (заполняет GitHub Actions); null — лайков ещё нет
    public int? Issue { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Likes { get; set; }

    public string Badge => Authors.BadgeText(Author);
    public System.Windows.Media.Brush BadgeBrush => Authors.BadgeBrush(Author);
    public System.Windows.Visibility BadgeVisible => Badge == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public string Subtitle =>
        string.Join(" · ", new[]
        {
            $"{Foundations} фунд.", $"{Floors} эт.",
            string.IsNullOrEmpty(Added) ? null : Added,
        }.Where(s => s != null));
}

public class CatalogIndex
{
    public int Version { get; set; }
    public List<CatalogEntry> Bases { get; set; } = new();
}

/// Онлайн-каталог баз в публичном репозитории GitHub.
/// Чтение: raw.githubusercontent.com (без авторизации). Публикация: через pull request в браузере.
public static class Catalog
{
    /// Официальный каталог. Заполнить после создания репозитория на GitHub: "владелец/репозиторий"
    /// Можно с подпапкой: "владелец/репозиторий/папка" — каталог лежит в папке основного репозитория
    public const string DefaultRepo = "Legonchik/RustBuilder/catalog-repo";

    static readonly HttpClient Http = CreateHttp();
    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    static readonly Regex RepoRx = new(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$");

    /// "владелец/репозиторий/папка" → ("владелец/репозиторий", "папка/")
    public static (string Repo, string Prefix) Split(string repo)
    {
        var parts = repo.Split('/');
        return (parts[0] + "/" + parts[1], parts.Length > 2 ? string.Join("/", parts.Skip(2)) + "/" : "");
    }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RustBuilder/1.0");
        return h;
    }

    public static bool IsGitHubRepo(string repo) => repo != null && RepoRx.IsMatch(repo);
    static bool IsLocal(string repo) => !string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo);
    public static bool IsConfigured(string repo) => IsGitHubRepo(repo) || IsLocal(repo);

    static async Task<string> GetText(string repo, string branch, string path)
    {
        if (IsLocal(repo)) return await File.ReadAllTextAsync(Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsGitHubRepo(repo)) throw new Exception("каталог не настроен");
        // параметр t обходит кэш, чтобы новые базы появлялись сразу
        var (ownerRepo, prefix) = Split(repo);
        var url = $"https://raw.githubusercontent.com/{ownerRepo}/{branch}/{string.Join("/", (prefix + path).Split('/').Select(Uri.EscapeDataString))}?t={DateTime.UtcNow.Ticks}";
        using var resp = await Http.GetAsync(url);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new Exception($"не найдено: {repo}/{path} (репозиторий публичный? ветка '{branch}'?)");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public static async Task<CatalogIndex> LoadIndex(string repo, string branch)
    {
        var json = await GetText(repo, branch, "index.json");
        return JsonSerializer.Deserialize<CatalogIndex>(json, Opts) ?? new CatalogIndex();
    }

    // ---------- Лайки: 👍 и ❤ на issue базы (метка «base») ----------

    static string LikesCachePath => Path.Combine(AppSettings.AppDir, "likes-cache.json");
    record LikesCache(string Repo, DateTime At, Dictionary<int, int> Likes);

    /// Номер issue → число лайков. Без авторизации GitHub даёт 60 запросов в час, поэтому кэшируем на 3 минуты.
    /// fresh — игнорировать кэш (после нажатия «♥»). При ошибке сети отдаём старый кэш или пустой словарь.
    public static async Task<Dictionary<int, int>> LoadLikes(string repo, bool fresh = false)
    {
        LikesCache cached = null;
        try { if (File.Exists(LikesCachePath)) cached = JsonSerializer.Deserialize<LikesCache>(await File.ReadAllTextAsync(LikesCachePath)); } catch { }
        if (cached?.Repo != repo) cached = null;
        if (!IsGitHubRepo(repo)) return new();
        if (cached != null && !fresh && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(3)) return cached.Likes;
        try
        {
            var likes = new Dictionary<int, int>();
            for (int page = 1; page <= 10; page++)
            {
                var json = await Http.GetStringAsync($"https://api.github.com/repos/{Split(repo).Repo}/issues?labels=base&state=all&per_page=100&page={page}");
                using var doc = JsonDocument.Parse(json);
                int n = 0;
                foreach (var i in doc.RootElement.EnumerateArray())
                {
                    n++;
                    var r = i.GetProperty("reactions");
                    likes[i.GetProperty("number").GetInt32()] = r.GetProperty("+1").GetInt32() + r.GetProperty("heart").GetInt32();
                }
                if (n < 100) break;
            }
            Directory.CreateDirectory(AppSettings.AppDir);
            await File.WriteAllTextAsync(LikesCachePath, JsonSerializer.Serialize(new LikesCache(repo, DateTime.UtcNow, likes)));
            return likes;
        }
        catch { return cached?.Likes ?? new(); }
    }

    public static string LikeUrl(string repo, int issue) => $"https://github.com/{Split(repo).Repo}/issues/{issue}";

    // ---------- Видео-гайды (videos.json, только одобренные модератором) ----------

    /// Файл базы → одобренные видео. При ошибке — пусто (видео не обязательны).
    public static async Task<Dictionary<string, List<VideoGuide>>> LoadVideos(string repo, string branch)
    {
        try
        {
            var json = await GetText(repo, branch, "videos.json");
            var data = JsonSerializer.Deserialize<VideoIndex>(json, Opts);
            return (data?.Videos ?? new()).Where(v => v.IsSafe)
                .GroupBy(v => v.Base).ToDictionary(g => g.Key, g => g.ToList());
        }
        catch { return new(); }
    }

    public static string SuggestVideoUrl(string repo, string baseFile) =>
        $"https://github.com/{Split(repo).Repo}/issues/new?template=video-guide.yml&base={Uri.EscapeDataString(baseFile)}";

    public static string ReportVideoUrl(string repo, VideoGuide v) =>
        $"https://github.com/{Split(repo).Repo}/issues/new?template=video-report.yml&url={Uri.EscapeDataString(v.SafeUrl ?? "")}" +
        $"&details={Uri.EscapeDataString($"Заявка #{v.Issue}, база {v.Base}")}";

    public static Task<string> LoadBase(string repo, string branch, CatalogEntry e)
    {
        if (e.File == null || !e.File.StartsWith("bases/") || e.File.Contains("..")) throw new Exception("некорректный путь к файлу в index.json");
        return GetText(repo, branch, e.File);
    }

    /// Имя файла для публикации: латиница, без пробелов + короткий случайный хвост
    public static string FileNameFor(BuildPlan plan)
    {
        var map = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя".Zip(new[]
        {
            "a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya"
        }).ToDictionary(p => p.First, p => p.Second);
        var sb = new StringBuilder();
        foreach (var ch in (plan.Name ?? "base").ToLowerInvariant())
        {
            if (map.TryGetValue(ch, out var t)) sb.Append(t);
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else sb.Append('-');
        }
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        if (slug.Length == 0) slug = "base";
        return $"{slug}-{Guid.NewGuid().ToString("N")[..4]}.json";
    }

    /// Ссылка «создать файл» на GitHub. Для чужого репозитория GitHub сам сделает форк и предложит pull request.
    /// Если содержимое слишком длинное для ссылки, вернётся ссылка без него (текст тогда вставляют из буфера обмена).
    public static (string url, bool contentInUrl) UploadUrl(string repo, string branch, string fileName, string json)
    {
        var (ownerRepo, prefix) = Split(repo);
        var baseUrl = $"https://github.com/{ownerRepo}/new/{branch}/{prefix}bases?filename={Uri.EscapeDataString(fileName)}";
        var withValue = baseUrl + "&value=" + Uri.EscapeDataString(json);
        return withValue.Length <= 7500 ? (withValue, true) : (baseUrl, false);
    }
}
