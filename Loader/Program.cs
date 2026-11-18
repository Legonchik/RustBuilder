using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RustBuilderLoader;

/// Установщик для сайта. Скачивает последний RustBuilder-*-win-x64.zip с GitHub Releases,
/// распаковывает в %LocalAppData%\Programs\RustBuilder, делает ярлыки и запускает.
/// Базы и настройки пользователя лежат в %AppData%\RustBuilder — их установщик не трогает.
static class Program
{
    public const string Repo = "Legonchik/RustBuilder";
    public const string Site = "https://legonchik.github.io/RustBuilder/";
    public static string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RustBuilder");
    public static string ExePath => Path.Combine(InstallDir, "RustBuilder.exe");

    [STAThread]
    static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        // для проверки без окна: RustBuilderSetup.exe --quiet <папка>
        if (args.Length >= 2 && args[0] == "--quiet")
        {
            InstallDir = Path.GetFullPath(args[1]);
            try
            {
                var rel = Installer.FindLatest().GetAwaiter().GetResult();
                Installer.Install(rel, null).GetAwaiter().GetResult();
                Console.WriteLine($"ok {rel.Tag} -> {InstallDir}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length >= 2 && args[0] == "--shot")   // картинка окна для README/проверки
        {
            var f = new SetupForm { Opacity = 0, ShowInTaskbar = false };
            f.Shown += async (_, _) =>
            {
                await Task.Delay(2500);
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args[1]);
                f.Close();
            };
            Application.Run(f);
            return 0;
        }
        Application.Run(new SetupForm());
        return 0;
    }

    public static Icon AppIcon(int size)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico");
        return new Icon(s, size, size);
    }
}

record Release(string Tag, string ZipUrl, long Size);
static class Installer
{
    static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };

    static Installer()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RustBuilder-Setup/1.0");
    }

    /// Последний релиз и его zip для Windows x64
    public static async Task<Release> FindLatest()
    {
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Program.Repo}/releases/latest");
        var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        // ассеты: ищем zip с win-x64 в имени
        foreach (Match m in Regex.Matches(json, "\"size\"\\s*:\\s*(\\d+)[\\s\\S]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
        {
            var url = m.Groups[2].Value;
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && url.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Release(tag, url, long.Parse(m.Groups[1].Value));
        }
        throw new Exception($"В релизе {tag} нет файла RustBuilder-…-win-x64.zip.");
    }

    public static string InstalledVersion()
    {
        try { return File.Exists(Program.ExePath) ? FileVersionInfo.GetVersionInfo(Program.ExePath).ProductVersion?.Split('+')[0] : null; }
        catch { return null; }
    }

    /// Скачать zip и распаковать поверх папки установки. progress: 0..1 и текст.
    public static async Task Install(Release rel, IProgress<(double, string)> progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "RustBuilderSetup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var resp = await http.GetAsync(rel.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.Size;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long done = 0;
                int n;
                var sw = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    done += n;
                    if (sw.ElapsedMilliseconds > 100 || done == total)
                    {
                        sw.Restart();
                        progress?.Report((total > 0 ? 0.9 * done / total : 0, $"Скачиваю {rel.Tag}: {done / 1048576.0:0.0} из {total / 1048576.0:0.0} МБ"));
                    }
                }
            }

            progress?.Report((0.92, "Распаковываю…"));
            await Task.Run(() => Extract(tmp));
            progress?.Report((1, "Готово"));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    static void Extract(string zip)
    {
        Directory.CreateDirectory(Program.InstallDir);
        // старые .old от автообновления больше не нужны
        foreach (var old in Directory.GetFiles(Program.InstallDir, "*.old")) try { File.Delete(old); } catch { }
        using var archive = ZipFile.OpenRead(zip);
        var root = Path.GetFullPath(Program.InstallDir + Path.DirectorySeparatorChar);
        foreach (var e in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(Program.InstallDir, e.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // защита от «../» в архиве
            if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            e.ExtractToFile(path, true);
        }
        if (!File.Exists(Program.ExePath)) throw new Exception("В архиве нет RustBuilder.exe.");
    }

    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RustBuilder.lnk");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RustBuilder.lnk");

    /// Ярлык через WScript.Shell (есть в любой Windows)
    public static void Shortcut(string lnk, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(type);
        object sc = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        var t = sc.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "RustBuilder — гайд по стройке баз в Rust" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    public static bool AppRunning() =>
        Process.GetProcessesByName("RustBuilder").Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, Program.ExePath, StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        });

    public static void Uninstall()
    {
        foreach (var l in new[] { DesktopLink, StartMenuLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }
        if (Directory.Exists(Program.InstallDir)) Directory.Delete(Program.InstallDir, true);
    }
}

/// Окно установщика в стиле RustBuilder: тёмное, с фиолетовым акцентом
class SetupForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x15, 0x17, 0x1B);
    static readonly Color Panel2 = Color.FromArgb(0x24, 0x27, 0x2D);
    static readonly Color Accent = Color.FromArgb(0xA9, 0x70, 0xFF);
    static readonly Color Muted = Color.FromArgb(0x9A, 0xA0, 0xA8);

    readonly Label status = new();
    readonly Label folder = new();
    readonly CheckBox desktop = new();
    readonly FlatButton main = new(true), second = new(false);
    readonly ProgressLine bar = new();
    Release release;
    bool busy, installed;
    Point drag;

    public SetupForm()
    {
        Text = "Установка RustBuilder";
        Icon = Program.AppIcon(32);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(480, 320);
        BackColor = Bg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        var logo = new PictureBox { Image = Program.AppIcon(256).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(32, 34, 64, 64), BackColor = Color.Transparent };
        var title = new Label { Text = "RustBuilder", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(108, 34), BackColor = Color.Transparent };
        var sub = new Label { Text = "Пошаговый гайд по стройке баз в Rust поверх игры", ForeColor = Muted, AutoSize = true, Location = new Point(111, 74), BackColor = Color.Transparent };
        var close = new Label { Text = "✕", Font = new Font("Segoe UI", 11f), ForeColor = Muted, AutoSize = true, Location = new Point(450, 10), Cursor = Cursors.Hand };
        close.Click += (_, _) => { if (!busy) Close(); };

        status.SetBounds(32, 124, 416, 22);
        status.Text = "Ищу последнюю версию…";
        bar.SetBounds(32, 152, 416, 6);
        bar.Fill = Accent;
        bar.Track = Panel2;

        desktop.Text = "Ярлык на рабочем столе";
        desktop.Checked = true;
        desktop.AutoSize = true;
        desktop.Location = new Point(32, 176);
        desktop.FlatStyle = FlatStyle.Flat;

        folder.SetBounds(32, 204, 416, 36);
        folder.ForeColor = Muted;
        folder.Font = new Font("Segoe UI", 8.5f);
        folder.Text = "Папка: " + Program.InstallDir + "\nТвои базы и настройки сохранятся. В игру программа не внедряется.";

        main.SetBounds(288, 256, 160, 40);
        main.Text = "Установить";
        main.Enabled = false;
        main.Click += async (_, _) => await MainAction();
        second.SetBounds(32, 256, 120, 40);
        second.Text = "Сайт";
        second.Click += (_, _) => SecondAction();

        Controls.AddRange(new Control[] { logo, title, sub, close, status, bar, desktop, folder, main, second });

        // окно без рамки — тащим за любое пустое место
        foreach (Control c in new Control[] { this, logo, title, sub })
        {
            c.MouseDown += (_, e) => drag = e.Location;
            c.MouseMove += (s, e) => { if (e.Button == MouseButtons.Left) { var p = PointToScreen(e.Location); Location = new Point(p.X - drag.X - (s == this ? 0 : ((Control)s).Left), p.Y - drag.Y - (s == this ? 0 : ((Control)s).Top)); } };
        }

        Shown += async (_, _) => await Check();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(0x3A, 0x3E, 0x46));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    async Task Check()
    {
        var have = Installer.InstalledVersion();
        installed = have != null;
        try
        {
            release = await Installer.FindLatest();
            var latest = release.Tag.TrimStart('v', 'V');
            if (!installed) { status.Text = $"Доступна версия {latest} · {release.Size / 1048576.0:0} МБ"; main.Text = "Установить"; }
            else if (have == latest || have.StartsWith(latest + ".")) { status.Text = $"Установлена последняя версия {have}"; main.Text = "Запустить"; }
            else { status.Text = $"Установлена {have}, есть новая {latest}"; main.Text = "Обновить"; }
        }
        catch (Exception ex)
        {
            status.Text = installed ? $"Установлена версия {have}. GitHub недоступен." : "Не удалось связаться с GitHub. Проверь интернет.";
            main.Text = installed ? "Запустить" : "Повторить";
            if (!installed) Log(ex);
        }
        if (installed) { second.Text = "Удалить"; desktop.Checked = File.Exists(Installer.DesktopLink); }
        main.Enabled = true;
    }

    async Task MainAction()
    {
        if (busy) return;
        if (main.Text == "Запустить") { Launch(); return; }
        if (main.Text == "Повторить" || release == null) { main.Enabled = false; status.Text = "Ищу последнюю версию…"; await Check(); return; }

        while (Installer.AppRunning())
        {
            var r = MessageBox.Show(this, "RustBuilder сейчас запущен. Закрой его и нажми «Повторить».", "Установка RustBuilder", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information);
            if (r != DialogResult.Retry) return;
        }

        busy = true;
        main.Enabled = second.Enabled = desktop.Enabled = false;
        try
        {
            var progress = new Progress<(double v, string text)>(p => { bar.Value = p.v; status.Text = p.text; });
            await Installer.Install(release, progress);
            try
            {
                Installer.Shortcut(Installer.StartMenuLink, Program.ExePath);
                if (desktop.Checked) Installer.Shortcut(Installer.DesktopLink, Program.ExePath);
                else if (File.Exists(Installer.DesktopLink)) File.Delete(Installer.DesktopLink);
            }
            catch (Exception ex) { Log(ex); }
            status.Text = $"Готово! RustBuilder {release.Tag.TrimStart('v', 'V')} установлен.";
            installed = true;
            main.Text = "Запустить";
            second.Text = "Удалить";
        }
        catch (Exception ex)
        {
            Log(ex);
            status.Text = "Ошибка: " + ex.Message;
            bar.Value = 0;
            main.Text = "Установить";
        }
        finally
        {
            busy = false;
            main.Enabled = second.Enabled = desktop.Enabled = true;
        }
    }

    void SecondAction()
    {
        if (busy) return;
        if (!installed) { Process.Start(new ProcessStartInfo(Program.Site) { UseShellExecute = true }); return; }
        if (MessageBox.Show(this, "Удалить RustBuilder с компьютера?\n\nТвои базы и настройки (%AppData%\\RustBuilder) останутся.",
                "Удаление RustBuilder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (Installer.AppRunning()) { MessageBox.Show(this, "Сначала закрой RustBuilder.", "Удаление RustBuilder"); return; }
        try
        {
            Installer.Uninstall();
            installed = false;
            bar.Value = 0;
            status.Text = "RustBuilder удалён.";
            main.Text = release != null ? "Установить" : "Повторить";
            second.Text = "Сайт";
        }
        catch (Exception ex) { status.Text = "Не удалось удалить: " + ex.Message; }
    }

    void Launch()
    {
        try { Process.Start(new ProcessStartInfo(Program.ExePath) { UseShellExecute = true, WorkingDirectory = Program.InstallDir }); Close(); }
        catch (Exception ex) { status.Text = "Не удалось запустить: " + ex.Message; }
    }

    static void Log(Exception ex)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "RustBuilderSetup-error.txt"), ex.ToString()); } catch { }
    }
}

/// Плоская кнопка: основная — фиолетовая, второстепенная — серая
class FlatButton : Button
{
    public FlatButton(bool primary)
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = primary ? Color.FromArgb(0xA9, 0x70, 0xFF) : Color.FromArgb(0x2C, 0x30, 0x37);
        FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0xB9, 0x88, 0xFF) : Color.FromArgb(0x38, 0x3C, 0x44);
        FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0x93, 0x58, 0xEA) : Color.FromArgb(0x24, 0x27, 0x2D);
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", primary ? 11f : 10f);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }
}

/// Тонкая полоса прогресса со скруглёнными краями
class ProgressLine : Control
{
    double value;
    public Color Fill = Color.MediumPurple, Track = Color.DimGray;
    public double Value { get => value; set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

    public ProgressLine() { DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.Black);
        Pill(g, new RectangleF(0, 0, Width - 1, Height - 1), Track);
        if (value > 0) Pill(g, new RectangleF(0, 0, Math.Max(Height, (float)((Width - 1) * value)), Height - 1), Fill);
    }

    static void Pill(Graphics g, RectangleF r, Color c)
    {
        using var path = new GraphicsPath();
        float d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }
}
