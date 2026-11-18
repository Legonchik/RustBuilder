using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RustBuilder;

/// Аккаунт сайта RustBuilder (Supabase): вход через Discord или почту, выкладывание баз, лайки.
/// Ключ ниже — публичный (anon), он и так есть в коде сайта. Сессия хранится зашифрованной (DPAPI) только на этом ПК.
public static class Site
{
    public const string Web = "https://legonchik.github.io/RustBuilder/";
    const string Api = "https://vbmnwhhycfaspadfgkxi.supabase.co";
    const string Key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InZibW53aGh5Y2Zhc3BhZGZna3hpIiwicm9sZSI6ImFub24iLCJpYXQiOjE3OTQ3NTk0NDcsImV4cCI6MjExMDMzNTQ0N30.b_OwazZjBa9bgHJRTEK8fhl-J6uXr3Wg1Kec7QxqZ-0";
    /// Порт для возврата из браузера после входа через Discord. Адрес http://127.0.0.1:47821/** должен быть в Supabase → Redirect URLs.
    public const int CallbackPort = 47821;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string SessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustBuilder", "account.bin");

    public class Session
    {
        public string Access { get; set; }
        public string Refresh { get; set; }
        public DateTime Expires { get; set; }
        public string UserId { get; set; }
        public string Email { get; set; }
        public string Nick { get; set; }
    }

    public static Session Current { get; private set; }
    public static bool LoggedIn => Current != null;
    public static event Action Changed;
    static HashSet<string> myLikes;

    // ─── сессия ──────────────────────────────────────────────────────────────

    public static void LoadSaved()
    {
        try
        {
            if (!File.Exists(SessionFile)) return;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(SessionFile), null, DataProtectionScope.CurrentUser);
            Current = JsonSerializer.Deserialize<Session>(raw);
        }
        catch { Current = null; }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile));
            if (Current == null) { File.Delete(SessionFile); return; }
            File.WriteAllBytes(SessionFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(Current), null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    public static void Logout()
    {
        Current = null; myLikes = null; Save(); Changed?.Invoke();
    }

    static async Task SetSession(JsonNode t)
    {
        Current = new Session
        {
            Access = (string)t["access_token"],
            Refresh = (string)t["refresh_token"],
            Expires = DateTime.UtcNow.AddSeconds(((int?)t["expires_in"] ?? 3600) - 60),
            UserId = (string)t["user"]?["id"],
            Email = (string)t["user"]?["email"],
        };
        myLikes = null;
        await RefreshProfile();
        Save();
        Changed?.Invoke();
    }

    /// Живой токен: обновляет просроченный. false — нужно войти заново.
    static async Task<bool> EnsureToken()
    {
        if (Current == null) return false;
        if (DateTime.UtcNow < Current.Expires) return true;
        try
        {
            var t = await Auth("refresh_token", new JsonObject { ["refresh_token"] = Current.Refresh });
            var nick = Current.Nick;
            await SetSession(t);
            Current.Nick ??= nick;
            return true;
        }
        catch (SiteException e) when (e.Status is 400 or 401) { Logout(); return false; }
    }

    public static async Task RefreshProfile()
    {
        if (Current == null) return;
        try
        {
            var arr = await Rest(HttpMethod.Get, $"profiles?id=eq.{Current.UserId}&select=nick", auth: true, skipEnsure: true);
            Current.Nick = (string)arr?.AsArray().FirstOrDefault()?["nick"];
            if (!string.IsNullOrWhiteSpace(Current.Nick) && AppSettings.Current.Nickname != Current.Nick)
            {
                AppSettings.Current.Nickname = Current.Nick;
                AppSettings.Current.Save();
            }
            Save();
        }
        catch { }
    }

    // ─── вход ────────────────────────────────────────────────────────────────

    public static async Task LoginPassword(string email, string password) =>
        await SetSession(await Auth("password", new JsonObject { ["email"] = email.Trim(), ["password"] = password }));

    /// Вход через Discord: открывает браузер, ждёт возврата на 127.0.0.1 и меняет код на сессию (PKCE).
    public static async Task LoginDiscord(CancellationToken ct)
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(48));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirect = $"http://127.0.0.1:{CallbackPort}/callback";
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try { listener.Start(); }
        catch (SocketException) { throw new SiteException(0, "Порт для входа занят. Закрой другие окна входа RustBuilder и попробуй ещё раз."); }
        try
        {
            var url = $"{Api}/auth/v1/authorize?provider=discord&redirect_to={Uri.EscapeDataString(redirect)}" +
                      $"&code_challenge={challenge}&code_challenge_method=s256";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            string code = null, error = null;
            while (code == null && error == null)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, ct);
                var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];      // GET /callback?code=... HTTP/1.1
                var path = line.Split(' ').ElementAtOrDefault(1) ?? "";
                if (!path.StartsWith("/callback")) { await Reply(stream, 404, "Not found"); continue; }
                var q = System.Web.HttpUtility.ParseQueryString(path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "");
                code = q["code"];
                error = code == null ? (q["error_description"] ?? q["error"] ?? "вход отменён") : null;
                await Reply(stream, 200, Page(code != null));
            }
            if (error != null) throw new SiteException(0, error);
            await SetSession(await Auth("pkce", new JsonObject { ["auth_code"] = code, ["code_verifier"] = verifier }));
        }
        finally { listener.Stop(); }
    }

    static string Page(bool ok) =>
        "<!doctype html><meta charset=utf-8><title>RustBuilder</title><body style=\"margin:0;height:100vh;display:grid;place-items:center;" +
        "background:#100b19;color:#f5f0ff;font:18px Segoe UI,Arial\"><div style=\"text-align:center\"><div style=\"font-size:56px\">" +
        (ok ? "✓" : "✗") + "</div><h2>" + (ok ? "Готово! Возвращайся в RustBuilder" : "Вход не удался") +
        "</h2><p style=\"opacity:.7\">Эту вкладку можно закрыть.</p></div>";

    static async Task Reply(Stream s, int status, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head); await s.WriteAsync(body); await s.FlushAsync();
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ─── базы и лайки ────────────────────────────────────────────────────────

    /// Отправить базу на проверку. Возвращает null при успехе или понятный текст ошибки.
    public static async Task<string> Submit(string json, string name, string description)
    {
        if (!await EnsureToken()) return "Нужно войти в аккаунт.";
        var plan = JsonNode.Parse(json).AsObject();
        plan["author"] = Current.Nick;
        var pieces = plan["pieces"]?.AsArray() ?? new JsonArray();
        static bool IsFnd(JsonNode p) => (string)p?["type"] is "foundation" or "triangle";
        var body = new JsonObject
        {
            ["name"] = name, ["description"] = string.IsNullOrWhiteSpace(description) ? null : description,
            ["tier"] = (string)plan["tier"] ?? "stone",
            ["foundations"] = pieces.Count(IsFnd),
            ["floors"] = Math.Max(1, pieces.Select(p => IsFnd(p) ? 1 : (int?)p?["floor"] ?? 1).DefaultIfEmpty(1).Max()),
            ["steps"] = plan["steps"]?.AsArray().Count ?? 0,
            ["data"] = plan.DeepClone(),
        };
        try { await Rest(HttpMethod.Post, "submissions", body, auth: true); return null; }
        catch (SiteException e) { return e.Message; }
    }

    public record SiteBase(string Id, string Name, string Description, string Tier, int Foundations, int Floors, int Steps, string Author, string Created);

    public static async Task<List<SiteBase>> LoadApproved()
    {
        var arr = await Rest(HttpMethod.Get, "submissions?status=eq.approved&select=id,name,description,tier,foundations,floors,steps,created_at,profiles(nick)&order=created_at.desc");
        return arr.AsArray().Select(x => new SiteBase((string)x["id"], (string)x["name"], (string)x["description"], (string)x["tier"],
            (int?)x["foundations"] ?? 0, (int?)x["floors"] ?? 1, (int?)x["steps"] ?? 0, (string)x["profiles"]?["nick"],
            ((string)x["created_at"])?[..10])).ToList();
    }

    public static async Task<string> LoadBaseJson(string id)
    {
        var arr = await Rest(HttpMethod.Get, $"submissions?id=eq.{Uri.EscapeDataString(id)}&select=data");
        return arr.AsArray().FirstOrDefault()?["data"]?.ToJsonString() ?? throw new Exception("база не найдена");
    }

    public static async Task<Dictionary<string, int>> LikeCounts()
    {
        try { return (await Rest(HttpMethod.Get, "like_counts?select=base,n")).AsArray().ToDictionary(x => (string)x["base"], x => (int)x["n"]); }
        catch { return new(); }
    }

    public static async Task<HashSet<string>> MyLikes()
    {
        if (myLikes != null) return myLikes;
        if (!await EnsureToken()) return new();
        try
        {
            var arr = await Rest(HttpMethod.Get, $"likes?user_id=eq.{Current.UserId}&select=base", auth: true);
            return myLikes = arr.AsArray().Select(x => (string)x["base"]).ToHashSet();
        }
        catch { return new(); }
    }

    /// Поставить или снять лайк. Возвращает true, если теперь лайк стоит.
    public static async Task<bool> ToggleLike(string key)
    {
        if (!await EnsureToken()) throw new SiteException(401, "Нужно войти в аккаунт.");
        var mine = await MyLikes();
        if (mine.Contains(key))
        {
            await Rest(HttpMethod.Delete, $"likes?user_id=eq.{Current.UserId}&base=eq.{Uri.EscapeDataString(key)}", auth: true);
            mine.Remove(key); return false;
        }
        await Rest(HttpMethod.Post, "likes", new JsonObject { ["base"] = key }, auth: true);
        mine.Add(key); return true;
    }

    // ─── HTTP ────────────────────────────────────────────────────────────────

    public class SiteException(int status, string message) : Exception(message) { public int Status { get; } = status; }

    static async Task<JsonNode> Auth(string grant, JsonObject body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/auth/v1/token?grant_type={grant}") { Content = JsonContent(body) };
        req.Headers.Add("apikey", Key);
        return await Send(req);
    }

    static async Task<JsonNode> Rest(HttpMethod m, string path, JsonNode body = null, bool auth = false, bool skipEnsure = false)
    {
        if (auth && !skipEnsure && !await EnsureToken()) throw new SiteException(401, "Нужно войти в аккаунт.");
        using var req = new HttpRequestMessage(m, $"{Api}/rest/v1/{path}");
        req.Headers.Add("apikey", Key);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (auth ? Current.Access : Key));
        if (body != null) { req.Content = JsonContent(body); req.Headers.Add("Prefer", "return=minimal"); }
        return await Send(req);
    }

    static StringContent JsonContent(JsonNode n) => new(n.ToJsonString(), Encoding.UTF8, "application/json");

    static async Task<JsonNode> Send(HttpRequestMessage req)
    {
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req); }
        catch (Exception) { throw new SiteException(0, "Нет связи с сайтом. Проверь интернет."); }
        var text = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        string msg = text;
        try { var j = JsonNode.Parse(text); msg = (string)(j?["msg"] ?? j?["message"] ?? j?["error_description"] ?? j?["error"]) ?? text; } catch { }
        throw new SiteException((int)res.StatusCode, Friendly(msg));
    }

    static string Friendly(string m) => m switch
    {
        _ when m.Contains("NO_NICK") => "Сначала придумай ник на сайте (Профиль).",
        _ when m.Contains("TOO_MANY_PENDING") => "У тебя уже 3 базы на проверке — подожди, пока их посмотрят.",
        _ when m.Contains("Invalid login credentials") => "Неверная почта или пароль.",
        _ when m.Contains("Email not confirmed") => "Почта не подтверждена — открой письмо и нажми ссылку.",
        _ when m.Contains("JWT expired") => "Сессия истекла — войди ещё раз.",
        _ when m.Contains("duplicate key") => "Уже сделано.",
        _ => m,
    };
}
