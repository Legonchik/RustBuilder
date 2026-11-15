using System.IO;
using System.Text.Json;

namespace RustBuilder;

/// Действия, которые можно повесить на горячие клавиши
public enum HotkeyAction
{
    Next = 1, Prev, NextStep, PrevStep, FloorUp, FloorDown, Rotate, Library, Settings, Hide, Lock, Reload, Speak,
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
    /// Простой режим: иконка + пара слов, без цен и длинных пояснений
    public bool SimpleMode { get; set; } = true;
    /// Озвучивать, что ставить
    public bool VoiceEnabled { get; set; } = true;
    public int VoiceVolume { get; set; } = 100;

    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// Выбранный интерьер для каждой базы (по имени файла): -1 — без интерьера
    public Dictionary<string, int> Interiors { get; set; } = new();
    public int GetInterior(BuildPlan p) =>
        !InteriorsEnabled || p.Interiors.Count == 0 ? -1 : Interiors.TryGetValue(p.FileName ?? "", out var i) ? i : 0;
    /// Варианты интерьера (бета): выключено — базы строятся без мебели, выбор интерьера скрыт
    public bool InteriorsEnabled { get; set; } = true;
    public void SetInterior(BuildPlan p, int i) { if (p.FileName != null) Interiors[p.FileName] = i; }

    /// Проверять обновления на GitHub при запуске
    public bool AutoUpdate { get; set; } = true;
    /// Версия, которую пользователь попросил пропустить
    public string SkippedVersion { get; set; }
    /// Профиль: ник автора, под которым база выкладывается в каталог
    public string Nickname { get; set; }
    /// Статус в Discord (Rich Presence): что за база строится
    public bool DiscordPresence { get; set; } = true;
    public string DiscordAppId { get; set; }
    /// Короткие звуки: лайк, копирование, «строить», шаг, база готова
    public bool SoundsEnabled { get; set; } = true;
    /// Базы из каталога, на которые пользователь нажимал «♥» (для закрашенного сердечка)
    public List<string> LikedBases { get; set; } = new();
    /// Цвет интерфейса (#RRGGBB)
    public string AccentColor { get; set; } = Theme.DefaultAccent;
    /// Заставка при запуске
    public bool Splash { get; set; } = true;
    /// Красивая анимация появления окон
    public bool Animations { get; set; } = true;

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
        [HotkeyAction.Speak] = "Ctrl+Alt+V",
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
        HotkeyAction.Speak => "Повторить голосом",
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
                // старый адрес отдельного репозитория (его так и не создали) → каталог внутри основного репозитория
                if (string.IsNullOrWhiteSpace(s.CatalogRepo) || s.CatalogRepo.Equals("Legonchik/rustbuilder-bases", StringComparison.OrdinalIgnoreCase))
                    s.CatalogRepo = Catalog.DefaultRepo;
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
