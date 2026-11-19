using System.Text;
using System.Text.Json;

namespace TunPilot;

/// <summary>Импорт профиля маршрутизации Throne (ссылка вида throne://route/&lt;base64&gt;).</summary>
public static class ThroneImport
{
    const string Begin = "# >>> импорт из Throne";
    const string End = "# <<< импорт из Throne";

    /// <summary>Разбирает ссылку и создаёт (или обновляет) профиль маршрутизации с тем же именем.</summary>
    public static (RoutingProfile Profile, string Report) Apply(string link)
    {
        var payload = link.Trim();
        int slash = payload.LastIndexOf('/');
        if (payload.StartsWith("throne://", StringComparison.OrdinalIgnoreCase)) payload = payload[(slash + 1)..];
        var b64 = payload.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        var root = doc.RootElement;

        var direct = new List<string>(); var proxy = new List<string>();
        var geoDirect = new List<string>(); var geoProxy = new List<string>();
        var skipped = new List<string>();

        foreach (var rule in root.GetProperty("rules").EnumerateArray())
        {
            var name = Str(rule, "name") ?? "?";
            var action = Str(rule, "action") ?? "route";
            if (action == "hijack-dns" || rule.TryGetProperty("ip_is_private", out _)) continue; // уже встроено
            if (action != "route") { skipped.Add(Lf($"{name} (действие {action})")); continue; }

            var outbound = Str(rule, "outbound");
            List<string>? target = outbound switch { "direct" => direct, "proxy" => proxy, _ => null };
            var geoTarget = outbound == "direct" ? geoDirect : geoProxy;
            if (target == null) { skipped.Add($"{name} (outbound {outbound})"); continue; }

            foreach (var prop in rule.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "name" or "action" or "outbound" or "type": break;
                    case "process_name": target.AddRange(Items(prop.Value)); break;
                    case "process_path": target.AddRange(Items(prop.Value).Select(p => p.Trim('"'))); break;
                    case "domain":
                        // В Throne "*example.com" — маска; без звёздочки — точный домен.
                        target.AddRange(Items(prop.Value).Select(d => d.StartsWith('*') ? d.TrimStart('*', '.') : "full:" + d));
                        break;
                    case "domain_suffix": target.AddRange(Items(prop.Value).Select(d => d.TrimStart('.'))); break;
                    case "domain_keyword": target.AddRange(Items(prop.Value).Select(d => "keyword:" + d)); break;
                    case "ip_cidr": target.AddRange(Items(prop.Value)); break;
                    case "rule_set": geoTarget.AddRange(Items(prop.Value)); break;
                    default: skipped.Add(Lf($"{name} (поле {prop.Name})")); break;
                }
            }
        }

        var profileName = Str(root, "name") is { Length: > 0 } pn ? pn : L("Импорт из Throne");
        var profile = Profiles.Create(profileName);
        WriteSection(profile.DirectTxt, direct);
        WriteSection(profile.ProxyTxt, proxy);
        profile.GeoDirect = geoDirect.Distinct().ToList();
        profile.GeoProxy = geoProxy.Distinct().ToList();
        if (Str(root, "default_outbound") is "direct" or "proxy")
            profile.DefaultOutbound = Str(root, "default_outbound")!;
        profile.Save();

        var sb = new StringBuilder();
        sb.AppendLine(Lf($"Профиль «{Str(root, "name")}» импортирован."));
        sb.AppendLine(Lf($"Напрямую: {direct.Count} записей; через VPN: {proxy.Count}."));
        if (geoDirect.Count > 0) sb.AppendLine(L("Geo напрямую: ") + string.Join(", ", geoDirect));
        if (geoProxy.Count > 0) sb.AppendLine(L("Geo через VPN: ") + string.Join(", ", geoProxy));
        sb.AppendLine(L("Всё остальное: ") + (profile.DefaultOutbound == "direct" ? L("напрямую") : L("через VPN")));
        if (skipped.Count > 0) sb.AppendLine(L("Пропущено: ") + string.Join("; ", skipped));
        return (profile, sb.ToString().TrimEnd());
    }

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static IEnumerable<string> Items(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)
            : v.ValueKind == JsonValueKind.String ? [v.GetString()!] : [];

    /// <summary>Заменяет блок импорта в файле списка (строки вне блока не трогаем), без дублей.</summary>
    static void WriteSection(string path, List<string> entries)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        int b = lines.IndexOf(Begin), e = lines.IndexOf(End);
        if (b >= 0 && e > b) lines.RemoveRange(b, e - b + 1);
        var own = lines.Select(l => l.Split('#')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = entries.Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !own.Contains(x)).ToList();
        if (fresh.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add(Begin);
            lines.AddRange(fresh);
            lines.Add(End);
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }
}
