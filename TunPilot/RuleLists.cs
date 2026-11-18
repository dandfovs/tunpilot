using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TunPilot;

/// <summary>
/// Пользовательские списки. Direct/proxy компилируются в local rule-set sing-box,
/// который ядро перечитывает само при изменении файла — без перезапуска TUN.
/// Bypass — CIDR'ы, которые идут через обычный шлюз Windows, вообще минуя TUN.
/// </summary>
public static class RuleLists
{
    // Списки активного профиля маршрутизации.
    public static string DirectTxt => Profiles.Current.DirectTxt;
    public static string ProxyTxt => Profiles.Current.ProxyTxt;
    public static string BypassTxt => Profiles.Current.BypassTxt;
    public static string DirectJson => Path.Combine(AppPaths.Runtime, "ruleset-direct.json");
    public static string ProxyJson => Path.Combine(AppPaths.Runtime, "ruleset-proxy.json");

    public static string Help =>
        L("Одна запись на строку, # — комментарий.\n") +
        L("  example.com        — домен и все поддомены\n") +
        L("  full:example.com   — только этот домен\n") +
        L("  keyword:google     — домены, содержащие слово\n") +
        L("  regexp:^ads\\.     — регулярное выражение\n") +
        L("  1.2.3.4 / 1.2.3.0/24 — IP или подсеть\n") +
        L("  game.exe           — программа по имени процесса");

    public static void EnsureDefaults() => EnsureDefaults(Profiles.Current);

    public static void EnsureDefaults(RoutingProfile p)
    {
        Directory.CreateDirectory(p.Dir);
        string DirectTxt = p.DirectTxt, ProxyTxt = p.ProxyTxt, BypassTxt = p.BypassTxt;
        if (!File.Exists(DirectTxt))
            File.WriteAllText(DirectTxt,
                L("# Напрямую, без VPN (трафик всё ещё проходит через TUN)\n") +
                "dota2.exe\nsteam.exe\nsteamwebhelper.exe\n");
        if (!File.Exists(ProxyTxt))
            File.WriteAllText(ProxyTxt,
                L("# Всегда через VPN (важно, если по умолчанию выбрано «напрямую»)\n"));
        if (!File.Exists(BypassTxt))
            File.WriteAllText(BypassTxt,
                L("# Полностью мимо TUN: IP/подсети идут через обычный шлюз Windows.\n") +
                L("# Перезапуск VPN этих соединений не касается. Только IPv4.\n") +
                L("# Ретрансляторы Steam (Dota 2) добавляются автоматически, если включено в настройках.\n"));
    }

    public static void Compile()
    {
        EnsureDefaults();
        WriteInPlace(DirectJson, ToRuleSet(ReadEntries(DirectTxt)));
        WriteInPlace(ProxyJson, ToRuleSet(ReadEntries(ProxyTxt)));
    }

    public static List<string> ReadEntries(string path) =>
        !File.Exists(path) ? [] :
        File.ReadAllLines(path)
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .ToList();

    public static List<string> ReadBypassCidrs() =>
        ReadEntries(BypassTxt).Select(NormalizeCidr).Where(c => c != null && !c.Contains(':')).Select(c => c!).ToList();

    static string? NormalizeCidr(string entry)
    {
        var parts = entry.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        int len = parts.Length > 1 && int.TryParse(parts[1], out var l) && l >= 0 && l <= max ? l : max;
        return $"{ip}/{len}";
    }

    static JsonObject ToRuleSet(List<string> entries)
    {
        var domain = new List<string>(); var suffix = new List<string>(); var keyword = new List<string>();
        var cidr = new List<string>(); var proc = new List<string>(); var procPath = new List<string>(); var regex = new List<string>();

        foreach (var e in entries)
        {
            if (e.StartsWith("full:", StringComparison.OrdinalIgnoreCase)) domain.Add(e[5..].Trim());
            else if (e.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase)) regex.Add(e[7..].Trim());
            else if (e.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase)) keyword.Add(e[8..].Trim());
            else if (e.Contains('\\')) procPath.Add(e);
            else if (e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc.Add(e);
            else if (NormalizeCidr(e) is { } c) cidr.Add(c);
            else suffix.Add(e.TrimStart('.').ToLowerInvariant());
        }

        // Каждый тип — отдельное правило (внутри одного правила разные поля объединяются через И).
        var rules = new JsonArray();
        void Add(string key, List<string> vals)
        {
            if (vals.Count > 0)
                rules.Add(new JsonObject { [key] = new JsonArray(vals.Select(x => (JsonNode)x!).ToArray()) });
        }
        Add("domain", domain); Add("domain_suffix", suffix); Add("domain_keyword", keyword); Add("domain_regex", regex);
        Add("ip_cidr", cidr); Add("process_name", proc); Add("process_path", procPath);
        if (rules.Count == 0) Add("domain", ["never-matches.invalid"]);

        return new JsonObject { ["version"] = 3, ["rules"] = rules };
    }

    // Пишем поверх существующего файла (а не через rename), чтобы файловый наблюдатель sing-box его не потерял.
    static void WriteInPlace(string path, JsonObject json) =>
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
