using System.Text.Json;

namespace TunPilot;

static class AppPaths
{
    public static readonly string Base = AppContext.BaseDirectory;

    // Данные пользователя живут отдельно от exe, чтобы пересборка/перенос программы их не трогали.
    /// <summary>Другая папка данных («--data папка» — демо-данные для скриншотов при разработке).</summary>
    public static string? DataOverride;
    public static string Data => Ensure(DataOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunPilot"));
    public static string Lists => Ensure(Path.Combine(Data, "lists"));          // старое место списков (миграция)
    public static string Profiles => Ensure(Path.Combine(Data, "profiles"));
    public static string GroupsDir => Ensure(Path.Combine(Data, "groups"));
    public static string Runtime => Ensure(Path.Combine(Data, "runtime"));
    public static string Logs => Ensure(Path.Combine(Data, "logs"));

    public static string SingBoxExe => Path.Combine(Base, "cores", "sing-box", "sing-box.exe");
    public static string XrayDir => Path.Combine(Base, "cores", "xray");
    public static string XrayExe => Path.Combine(XrayDir, "xray.exe");

    static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

public class Settings
{
    // ── выбор ──
    /// <summary>Ключ выбранного сервера: "auto:&lt;группа&gt;" или "&lt;группа&gt;/&lt;имя&gt;".</summary>
    public string SelectedServer { get; set; } = "";
    public string ActiveGroupId { get; set; } = "";
    public string ActiveProfile { get; set; } = "";

    // ── поведение ──
    /// <summary>Автоматически пускать ретрансляторы Steam (Dota 2) мимо TUN.</summary>
    public bool SteamSdrBypass { get; set; } = true;
    public bool ConnectOnStart { get; set; } = false;
    /// <summary>«Запоминать подключение»: при запуске подключаться, если при выходе было подключено.</summary>
    public bool RememberState { get; set; } = true;
    public bool WasConnected { get; set; }
    /// <summary>Ручной запуск (ярлык/exe): сразу в трей, без окна.</summary>
    public bool StartMinimized { get; set; } = false;
    /// <summary>Запуск вместе с Windows: в трей, без окна.</summary>
    public bool AutostartToTray { get; set; } = true;
    /// <summary>Подсказка «свёрнуто в трей» уже показывалась.</summary>
    public bool TrayHintShown { get; set; }

    // ── подписки ──
    /// <summary>"provider" — интервал из заголовка подписки, "custom" — свой.</summary>
    public string SubIntervalMode { get; set; } = "provider";
    public int SubIntervalMinutes { get; set; } = 360;
    public bool ShowSubChanges { get; set; } = true;

    // ── DNS ──
    public string LocalDns { get; set; } = "77.88.8.8";
    public string RemoteDns { get; set; } = "1.1.1.1";

    // ── тесты ──
    public string LatencyUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public List<string> SpeedUrls { get; set; } =
    [
        "https://fsn1-speed.hetzner.com/1GB.bin",
        "https://proof.ovh.net/files/1Gb.dat",
        "https://speed.cloudflare.com/__down?bytes=1000000000",
    ];
    public int SpeedStreams { get; set; } = 4;
    public int SpeedSeconds { get; set; } = 8;

    // ── логи ──
    public string LogLevelSingBox { get; set; } = "warn";
    public string LogLevelXray { get; set; } = "warning";

    // ── интерфейс ──
    /// <summary>"dark", "light" или "system".</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Язык интерфейса: "auto" (как в Windows), "ru" или "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Глобальная клавиша «показать окно», например "Ctrl+Alt+V". Пусто — выключено.</summary>
    public string GlobalHotkey { get; set; } = "";

    // ── локальный прокси ──
    public bool LocalProxyEnabled { get; set; } = false;
    /// <summary>Kill-switch: пока VPN подключён (и если программа упала) — интернет только через TUN.</summary>
    public bool KillSwitch { get; set; } = false;
    public int LocalProxyPort { get; set; } = 2080;

    /// <summary>Откуда качать .srs: {kind} = geoip|geosite, {name} = имя набора.</summary>
    public string GeoUrlTemplate { get; set; } =
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/sing-box/rule-set-{kind}/{name}.srs";

    // ── устаревшее: читается только для переноса старых данных ──
    public string? SubscriptionUrl { get; set; }
    public DateTime SubscriptionUpdatedUtc { get; set; }
    public string? DefaultOutbound { get; set; }
    public List<string>? GeoDirect { get; set; }
    public List<string>? GeoProxy { get; set; }

    static string FilePath => Path.Combine(AppPaths.Data, "settings.json");
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* битый файл — начинаем с дефолтов */ }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
}
