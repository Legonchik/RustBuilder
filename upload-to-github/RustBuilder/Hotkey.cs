using System.Windows.Input;

namespace RustBuilder;

/// Сочетание клавиш в виде строки "Ctrl+Alt+Right" ⇄ модификаторы + виртуальный код для RegisterHotKey
public static class Hotkey
{
    public static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        Key key = Key.None;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLower())
            {
                case "ctrl": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public static string Format(ModifierKeys m, Key key)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// Красивый вид для интерфейса: "Ctrl + Alt + →"
    public static string Display(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "— не назначено —";
        return string.Join(" + ", s.Split('+').Select(p => p switch
        {
            "Right" => "→", "Left" => "←", "Up" => "↑", "Down" => "↓",
            "PageDown" or "Next" => "PgDn", "PageUp" or "Prior" => "PgUp",
            "OemPlus" => "+", "OemMinus" => "-", "Oem3" => "~",
            _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
            _ when p.StartsWith("NumPad") => "Num" + p[6..],
            _ => p,
        }));
    }

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
