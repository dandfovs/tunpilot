using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TunPilot;

/// <summary>
/// Готовые наборы правил (geoip-ru, geosite-category-ru…). Качаем двоичные .srs, раскладываем их в текст
/// (sing-box rule-set decompile) и склеиваем в два набора — «напрямую» и «через VPN».
/// sing-box следит за этими файлами и перечитывает их сам, поэтому добавить или убрать набор можно
/// без переподключения.
/// </summary>
public static class GeoRuleSets
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim Lock = new(1, 1);
    static string Dir => Directory.CreateDirectory(Path.Combine(AppPaths.Runtime, "geo")).FullName;

    public static string PathOf(string name) => Path.Combine(Dir, name + ".srs");
    public static string DirectJson => Path.Combine(AppPaths.Runtime, "geo-direct.json");
    public static string ProxyJson => Path.Combine(AppPaths.Runtime, "geo-proxy.json");

    /// <summary>Популярные наборы для быстрого добавления (имя → описание).</summary>
    public static readonly (string Name, string Title)[] Popular =
    [
        ("geoip-ru", L("Российские IP-адреса")),
        ("geosite-category-ru", L("Российские сайты (.ru, .рф, .su и крупные сервисы)")),
        ("geosite-ru-available-only-inside", L("Сайты, работающие только из России (Госуслуги, банки…)")),
        ("geosite-ru-blocked", L("Сайты, заблокированные в России")),
        ("geoip-ru-blocked", L("IP-адреса, заблокированные в России")),
        ("geosite-youtube", "YouTube"),
        ("geosite-google", "Google"),
        ("geosite-telegram", L("Telegram (сайты)")),
        ("geoip-telegram", L("Telegram (IP-адреса)")),
        ("geosite-discord", "Discord"),
        ("geosite-openai", "ChatGPT / OpenAI"),
        ("geosite-twitter", "X / Twitter"),
        ("geosite-meta", "Instagram, Facebook, WhatsApp"),
        ("geosite-steam", "Steam"),
        ("geosite-microsoft", "Microsoft"),
    ];

    public static string TitleOf(string name) => Popular.FirstOrDefault(p => p.Name == name).Title ?? "";

    /// <summary>Похожие наборы на случай, если нужного нет в репозитории (geosite-ru из профилей Throne).</summary>
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geosite-ru"] = "geosite-category-ru",
    };

    /// <summary>
    /// Скачивает недостающие/устаревшие наборы профиля и пересобирает склеенные файлы (sing-box подхватит сам).
    /// Возвращает доступные наборы и те, что не удалось получить.
    /// </summary>
    public static async Task<(HashSet<string> Available, List<string> Missing)> EnsureAsync(Settings s, RoutingProfile p, bool force = false)
    {
        await Lock.WaitAsync();
        try
        {
            var available = new HashSet<string>();
            var missing = new List<string>();
            foreach (var name in p.GeoDirect.Concat(p.GeoProxy).Distinct())
            {
                var path = PathOf(name);
                bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(1);
                if (force || !fresh)
                {
                    try
                    {
                        byte[] bytes;
                        try { bytes = await Download(s, name); }
                        catch (HttpRequestException) when (Aliases.TryGetValue(name, out var alias)) { bytes = await Download(s, alias); }
                        await File.WriteAllBytesAsync(path, bytes);
                    }
                    catch { /* нет в репозитории или нет сети — используем старую копию, если есть */ }
                }
                if (File.Exists(path)) available.Add(name); else missing.Add(name);
            }
            await Task.Run(() =>
            {
                WriteMerged(DirectJson, p.GeoDirect.Where(available.Contains));
                WriteMerged(ProxyJson, p.GeoProxy.Where(available.Contains));
            });
            return (available, missing);
        }
        finally { Lock.Release(); }
    }

    static Task<byte[]> Download(Settings s, string name)
    {
        var kind = name.StartsWith("geoip", StringComparison.OrdinalIgnoreCase) ? "geoip" : "geosite";
        return Http.GetByteArrayAsync(s.GeoUrlTemplate.Replace("{kind}", kind).Replace("{name}", name));
    }

    /// <summary>Склеивает текстовые версии наборов в один файл-набор (правила объединяются через ИЛИ).</summary>
    static void WriteMerged(string target, IEnumerable<string> names)
    {
        var rules = new JsonArray();
        int version = 2;
        foreach (var name in names)
        {
            if (Decompiled(name) is not { } json) continue;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
                if (doc["version"] is JsonValue vv && vv.TryGetValue<int>(out var v)) version = Math.Max(version, v);
                if (doc["rules"] is JsonArray rs)
                    foreach (var r in rs) rules.Add(r!.DeepClone());
            }
            catch { }
        }
        if (rules.Count == 0) rules.Add(new JsonObject { ["domain"] = new JsonArray("never-matches.invalid") });
        var text = new JsonObject { ["version"] = version, ["rules"] = rules }.ToJsonString();
        // пишем только при изменении — иначе sing-box будет зря перечитывать
        if (File.Exists(target) && File.ReadAllText(target) == text) return;
        File.WriteAllText(target, text);
    }

    /// <summary>Текстовая версия набора (кэш рядом с .srs; пересобирается, если .srs обновился).</summary>
    static string? Decompiled(string name)
    {
        var srs = PathOf(name);
        var json = Path.ChangeExtension(srs, ".json");
        if (!File.Exists(srs)) return null;
        if (File.Exists(json) && File.GetLastWriteTimeUtc(json) >= File.GetLastWriteTimeUtc(srs)) return json;
        try
        {
            var psi = new ProcessStartInfo(AppPaths.SingBoxExe, $"rule-set decompile --output \"{json}\" \"{srs}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(20000);
            return proc.ExitCode == 0 && File.Exists(json) ? json : null;
        }
        catch { return null; }
    }
}
