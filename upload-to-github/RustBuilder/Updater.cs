using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace RustBuilder;

/// Автообновление через GitHub Releases: качает zip нового релиза, подменяет файлы и перезапускается.
public static class Updater
{
    public const string Repo = "Legonchik/RustBuilder";

    public record Release(Version Version, string Tag, string ZipUrl, string Notes);

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RustBuilder-Updater");
        return http;
    }

    /// Последний релиз, если он новее текущей версии; иначе null
    public static async Task<Release> CheckAsync()
    {
        using var http = Http();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        if (v <= Current) return null;

        string zip = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = a.GetProperty("browser_download_url").GetString();
                if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        if (zip == null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new Release(v, tag, zip, notes);
    }

    /// Скачивает и ставит обновление. progress: 0..1. После успеха запускает новую версию — текущую надо закрыть.
    public static async Task InstallAsync(Release r, IProgress<double> progress)
    {
        var appDir = AppContext.BaseDirectory;
        var exe = Environment.ProcessPath ?? Path.Combine(appDir, "RustBuilder.exe");
        var work = Path.Combine(Path.GetTempPath(), "RustBuilder-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "update.zip");

        using (var http = Http())
        using (var resp = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var unpacked = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, unpacked);
        // zip может содержать файлы в корне или в одной папке внутри
        var newExe = Directory.GetFiles(unpacked, "RustBuilder.exe", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new Exception("в архиве нет RustBuilder.exe");
        var srcDir = Path.GetDirectoryName(newExe)!;

        // защита от вечного цикла: в релизе с тегом новее лежит та же (или старая) программа
        var fv = FileVersionInfo.GetVersionInfo(newExe);
        if (Version.TryParse(fv.FileVersion, out var nv) && new Version(nv.Major, nv.Minor, Math.Max(nv.Build, 0)) <= Current)
        {
            try { Directory.Delete(work, true); } catch { }
            throw new SameVersionException($"в релизе {r.Tag} лежит программа версии {nv.Major}.{nv.Minor}.{Math.Max(nv.Build, 0)}, " +
                                           $"а у тебя уже {Current}. Обновлять нечего — это ошибка в релизе, а не у тебя.");
        }

        // запущенный exe нельзя перезаписать, но можно переименовать
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(appDir, Path.GetRelativePath(srcDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }
        catch
        {
            if (!File.Exists(exe)) File.Move(old, exe);   // откат
            throw;
        }

        try { Directory.Delete(work, true); } catch { }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = appDir });
    }

    /// В скачанном релизе версия не новее текущей
    public class SameVersionException(string message) : Exception(message);

    /// Удаляет остатки прошлого обновления
    public static void Cleanup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* старый процесс ещё не закрылся — удалим в следующий раз */ }
    }
}
