using System.IO;
using System.Media;

namespace RustBuilder;

/// Короткие звуки интерфейса. Синтезируются в коде (без файлов): мягкие синусы с затуханием.
/// Выключаются в настройках («Звуки интерфейса»).
public static class Sfx
{
    const int Rate = 44100;
    static readonly Dictionary<string, byte[]> cache = new();

    /// Нота: частота (Гц), начало и длительность (с), громкость 0..1
    record Note(double F, double At, double Len, double Vol = 0.5);

    static readonly Dictionary<string, Note[]> Sounds = new()
    {
        // лайк — две восходящие ноты, «пинг»
        ["like"] = new[] { new Note(784, 0, 0.12, 0.45), new Note(1175, 0.07, 0.22, 0.4) },
        // скопировать — мягкий щелчок
        ["copy"] = new[] { new Note(660, 0, 0.07, 0.4), new Note(990, 0.04, 0.09, 0.3) },
        // строить — восходящее арпеджио
        ["build"] = new[] { new Note(523, 0, 0.14, 0.4), new Note(659, 0.08, 0.14, 0.4), new Note(784, 0.16, 0.14, 0.4), new Note(1047, 0.24, 0.3, 0.45) },
        // шаг вперёд — тихий тик
        ["step"] = new[] { new Note(880, 0, 0.05, 0.22) },
        // база достроена
        ["finish"] = new[] { new Note(523, 0, 0.18, 0.4), new Note(784, 0.12, 0.18, 0.4), new Note(1047, 0.24, 0.45, 0.45), new Note(1319, 0.24, 0.45, 0.25) },
        // ошибка — низкая нисходящая
        ["error"] = new[] { new Note(330, 0, 0.12, 0.45), new Note(247, 0.1, 0.2, 0.45) },
    };

    public static void Like() => Play("like");
    public static void Copy() => Play("copy");
    public static void Build() => Play("build");
    public static void Step() => Play("step");
    public static void Finish() => Play("finish");
    public static void Error() => Play("error");

    static void Play(string name)
    {
        if (AppSettings.ReadOnly || !AppSettings.Current.SoundsEnabled) return;
        try
        {
            byte[] wav;
            lock (cache) if (!cache.TryGetValue(name, out wav)) cache[name] = wav = Render(Sounds[name], AppSettings.Current.VoiceVolume);
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();   // асинхронно, не блокирует окно
        }
        catch { /* нет звуковой карты — молчим */ }
    }

    static byte[] Render(Note[] notes, int volumePercent)
    {
        double total = notes.Max(n => n.At + n.Len) + 0.02;
        int count = (int)(total * Rate);
        var mix = new double[count];
        foreach (var n in notes)
        {
            int start = (int)(n.At * Rate), len = (int)(n.Len * Rate);
            for (int i = 0; i < len && start + i < count; i++)
            {
                double t = (double)i / Rate;
                double attack = Math.Min(1, i / (Rate * 0.005));             // 5 мс — без щелчка в начале
                double decay = Math.Exp(-t * 5.5 / n.Len);                    // затухание к концу ноты
                double tone = Math.Sin(2 * Math.PI * n.F * t) + 0.25 * Math.Sin(4 * Math.PI * n.F * t);
                mix[start + i] += tone * attack * decay * n.Vol;
            }
        }
        double gain = Math.Clamp(volumePercent / 100.0, 0.1, 1) * 0.55;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + count * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(count * 2);
        foreach (var s in mix) w.Write((short)(Math.Clamp(s * gain, -1, 1) * 32000));
        w.Flush();
        return ms.ToArray();
    }
}
