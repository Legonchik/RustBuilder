using System.Globalization;
using System.Speech.Synthesis;

namespace RustBuilder;

/// Озвучка подсказок голосом Windows. Если русского голоса нет — молчит.
public static class Voice
{
    static SpeechSynthesizer synth;
    static bool triedInit;

    public static bool Available { get { Init(); return synth != null; } }

    static void Init()
    {
        if (triedInit) return;
        triedInit = true;
        try
        {
            var s = new SpeechSynthesizer();
            var ru = s.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru");
            if (ru == null) { s.Dispose(); return; }
            s.SelectVoice(ru.VoiceInfo.Name);
            s.SetOutputToDefaultAudioDevice();
            synth = s;
        }
        catch { synth = null; }
    }

    /// Говорит фразу, прерывая предыдущую. volume 0..100, rate -3..3
    public static void Say(string text, int volume = 100, int rate = 1)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Init();
        if (synth == null) return;
        try
        {
            synth.SpeakAsyncCancelAll();
            synth.Volume = Math.Clamp(volume, 0, 100);
            synth.Rate = Math.Clamp(rate, -10, 10);
            synth.SpeakAsync(text);
        }
        catch { /* звук не критичен */ }
    }

    public static void Stop()
    {
        try { synth?.SpeakAsyncCancelAll(); } catch { }
    }

    static readonly string[] Ordinal = { "", "Первый", "Второй", "Третий", "Четвёртый", "Пятый", "Шестой" };
    public static string FloorPhrase(int floor) => floor < Ordinal.Length ? $"{Ordinal[floor]} этаж" : $"Этаж {floor}";
}
