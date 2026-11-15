using System.Text.RegularExpressions;

namespace RustBuilder;

public class VideoIndex
{
    public int Version { get; set; }
    public List<VideoGuide> Videos { get; set; } = new();
}

/// Одобренный видео-гайд к базе. Ссылки и картинки повторно проверяются здесь:
/// даже если videos.json кто-то подменит, программа откроет только YouTube/Rutube.
public class VideoGuide
{
    public string Base { get; set; }
    public string Provider { get; set; }
    public string Id { get; set; }
    public string Url { get; set; }
    public string Title { get; set; }
    public string Channel { get; set; }
    public string Thumbnail { get; set; }
    public string About { get; set; }
    public string By { get; set; }
    public int Issue { get; set; }
    public string Added { get; set; }

    static readonly Regex YouTubeId = new(@"^[\w-]{11}$");
    static readonly Regex RutubeId = new(@"^[0-9a-f]{32}$");

    /// Каноническая ссылка, собранная из Id, а не из присланного текста
    public string SafeUrl => Provider switch
    {
        "youtube" when Id != null && YouTubeId.IsMatch(Id) => $"https://www.youtube.com/watch?v={Id}",
        "rutube" when Id != null && RutubeId.IsMatch(Id) => $"https://rutube.ru/video/{Id}/",
        _ => null,
    };

    public bool IsSafe => Base != null && SafeUrl != null;

    public string SafeThumbnail
    {
        get
        {
            if (Provider == "youtube" && SafeUrl != null) return $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg";
            if (Uri.TryCreate(Thumbnail, UriKind.Absolute, out var u) && u.Scheme == "https" &&
                (u.Host == "rutube.ru" || u.Host.EndsWith(".rutube.ru") || u.Host.EndsWith(".rtbcdn.ru"))) return Thumbnail;
            return null;
        }
    }

    public string ProviderName => Provider == "rutube" ? "Rutube" : "YouTube";
    public string Meta => string.Join(" · ", new[] { Channel, ProviderName, string.IsNullOrEmpty(By) ? null : "прислал " + By }.Where(s => !string.IsNullOrEmpty(s)));
}
