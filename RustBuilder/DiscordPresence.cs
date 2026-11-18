using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RustBuilder;

/// Статус в Discord (Rich Presence): «Строит базу 2x2 с сотами · шаг 5/19».
/// Работает через локальный канал самого Discord (discord-ipc-N) — с игрой это никак не связано.
public static class DiscordPresence
{
    /// ID приложения на discord.com/developers. Можно переопределить в settings.json (DiscordAppId).
    const string DefaultAppId = "1571510137019957248";
    static string AppId => string.IsNullOrWhiteSpace(AppSettings.Current.DiscordAppId) ? DefaultAppId : AppSettings.Current.DiscordAppId.Trim();

    static readonly object gate = new();
    static NamedPipeClientStream pipe;
    static object pending;            // последняя активность, которую надо отправить (null — очистить)
    static bool hasPending, running;
    static readonly long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static bool Available => !string.IsNullOrEmpty(AppId);

    /// Обновить статус. Discord разрешает ~5 обновлений за 20 с, поэтому шлём не чаще раза в 4 с — последнее значение.
    public static void Update(string baseName, int step, int steps, int floor)
    {
        if (!Available || AppSettings.ReadOnly) return;
        if (!AppSettings.Current.DiscordPresence) { Clear(); return; }
        var activity = new Dictionary<string, object>
        {
            ["details"] = Trim(baseName == null ? "Выбирает базу" : "Строит: " + baseName),
            ["state"] = baseName == null ? "Библиотека баз" : $"Шаг {step} из {steps} · этаж {floor}",
            ["timestamps"] = new { start = started },
            ["assets"] = new { large_image = "logo", large_text = "RustBuilder — гайд по стройке в Rust" },
            ["buttons"] = new[]
            {
                new { label = "Сайт RustBuilder", url = "https://legonchik.github.io/RustBuilder/" },
                new { label = "Скачать на GitHub", url = "https://github.com/Legonchik/RustBuilder/releases/latest" },
            },
        };
        Queue(activity);
    }

    public static void Clear() { if (Available && pipe != null) Queue(null); }

    static string Trim(string s) => s.Length <= 120 ? s : s[..117] + "…";

    static void Queue(object activity)
    {
        lock (gate)
        {
            pending = activity;
            hasPending = true;
            if (running) return;
            running = true;
        }
        Task.Run(Loop);
    }

    static async Task Loop()
    {
        while (true)
        {
            object act;
            lock (gate)
            {
                if (!hasPending) { running = false; return; }
                act = pending;
                hasPending = false;
            }
            try
            {
                if (pipe == null || !pipe.IsConnected) Connect();
                if (pipe != null)
                    Send(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = act }, nonce = Guid.NewGuid().ToString() });
            }
            catch { Drop(); }
            await Task.Delay(4000);
        }
    }

    static void Connect()
    {
        Drop();
        for (int i = 0; i < 10; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { p.Connect(150); }
            catch { p.Dispose(); continue; }
            pipe = p;
            Send(0, new { v = 1, client_id = AppId });
            _ = Task.Run(() => Drain(p));
            return;
        }
    }

    /// Ответы Discord нам не нужны, но их надо вычитывать, чтобы канал не забился
    static void Drain(NamedPipeClientStream p)
    {
        var buf = new byte[8192];
        try { while (p.IsConnected && p.Read(buf, 0, buf.Length) > 0) { } } catch { }
        lock (gate) if (pipe == p) pipe = null;
    }

    static void Send(int op, object payload)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    static void Drop()
    {
        try { pipe?.Dispose(); } catch { }
        pipe = null;
    }
}
