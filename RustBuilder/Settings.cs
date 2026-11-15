using System.IO;
using System.Text.Json;

namespace RustBuilder;

/// Действия, которые можно повесить на горячие клавиши
public enum HotkeyAction
{
    Next = 1, Prev, NextStep, PrevStep, FloorUp, FloorDown, Rotate, Library, Settings, Hide, Lock, Reload,
}

/// Настройки пользователя: %AppData%\RustBuilder\settings.json
public class AppSettings
{
    public double Left { get; set; } = 24;
    public double Top { get; set; } = 140;
    /// false = окно можно двигать и нажимать кнопки; true = клики проходят в игру
    public bool Locked { get; set; } = false;
    /// Полный путь к выбранной базе
    public string LastBuild { get; set; }
    public int LastStep { get; set; }
    public int LastPiece { get; set; }

    /// «Далее» переключает по одному блоку, а не целым шагом
    public bool PieceByPiece { get; set; } = true;
    /// Поворот схемы: 0..3 (× 90° по часовой)
    public int Rotation { get; set; }
    /// Размер оверлея: 0.7 … 1.6
    public double Scale { get; set; } = 1.0;
    /// Непрозрачность фона оверлея: 0.3 … 1.0
    public double BackgroundOpacity { get; set; } = 0.85;

    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// Каталог баз: "владелец/репозиторий" на GitHub (или путь к локальной папке — для проверки)
    public string CatalogRepo { get; set; } = Catalog.DefaultRepo;
    public string CatalogBranch { get; set; } = "main";

    public static readonly Dictionary<HotkeyAction, string> DefaultHotkeys = new()
    {
        [HotkeyAction.Next] = "Ctrl+Alt+Right",
        [HotkeyAction.Prev] = "Ctrl+Alt+Left",
        [HotkeyAction.NextStep] = "Ctrl+Alt+PageDown",
        [HotkeyAction.PrevStep] = "Ctrl+Alt+PageUp",
        [HotkeyAction.FloorUp] = "Ctrl+Alt+Up",
        [HotkeyAction.FloorDown] = "Ctrl+Alt+Down",
        [HotkeyAction.Rotate] = "Ctrl+Alt+T",
        [HotkeyAction.Library] = "Ctrl+Alt+B",
        [HotkeyAction.Settings] = "Ctrl+Alt+S",
        [HotkeyAction.Hide] = "Ctrl+Alt+H",
        [HotkeyAction.Lock] = "Ctrl+Alt+L",
        [HotkeyAction.Reload] = "Ctrl+Alt+R",
    };

    public static string ActionName(HotkeyAction a) => a switch
    {
        HotkeyAction.Next => "Следующий блок",
        HotkeyAction.Prev => "Предыдущий блок",
        HotkeyAction.NextStep => "Следующий шаг целиком",
        HotkeyAction.PrevStep => "Предыдущий шаг целиком",
        HotkeyAction.FloorUp => "Этаж выше",
        HotkeyAction.FloorDown => "Этаж ниже",
        HotkeyAction.Rotate => "Повернуть схему",
        HotkeyAction.Library => "Открыть базы",
        HotkeyAction.Settings => "Открыть настройки",
        HotkeyAction.Hide => "Скрыть / показать оверлей",
        HotkeyAction.Lock => "Закрепить / разблокировать окно",
        HotkeyAction.Reload => "Перечитать файл базы",
        _ => a.ToString(),
    };

    public string GetHotkey(HotkeyAction a) =>
        Hotkeys.TryGetValue(a.ToString(), out var s) ? s : DefaultHotkeys.GetValueOrDefault(a, "");

    public void SetHotkey(HotkeyAction a, string combo) => Hotkeys[a.ToString()] = combo ?? "";

    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustBuilder");
    public static string UserBuildsDir => Path.Combine(AppDir, "Builds");
    static string FilePath => Path.Combine(AppDir, "settings.json");

    /// Общие настройки для всех окон
    public static AppSettings Current { get; } = Load();

    /// В режиме снимков для разработки настройки пользователя не трогаем
    public static bool ReadOnly { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(s.CatalogRepo)) s.CatalogRepo = Catalog.DefaultRepo;
                return s;
            }
        }
        catch { /* битый файл — начинаем с чистых настроек */ }
        return new AppSettings();
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не критично */ }
    }
}
