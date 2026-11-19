using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TunPilot;

/// <summary>
/// Импорт правил маршрутизации из популярных клиентов. Формат определяется сам:
/// Throne/NekoBox (throne://route/…), Happ (happ://routing/…, JSON), v2rayN (правила / профиль маршрутизации),
/// v2RayTun, Streisand и др. на Xray (config.json), Hiddify / sing-box (config.json), Clash / Mihomo (YAML),
/// а также ссылка http(s) на любой из этих файлов.
/// </summary>
public static class RouteImport
{
    public static string SupportedText =>
        L("Поддерживаются:\n") +
        L("• Throne / NekoBox — ссылка throne://route/…\n") +
        L("• Happ — ссылка happ://routing/… или JSON маршрутизации\n") +
        L("• v2rayN — экспорт правил или профиля маршрутизации (JSON)\n") +
        L("• v2RayTun, Streisand, V2Box и другие на Xray — config.json\n") +
        "• Hiddify, NekoRay, sing-box — config.json\n" +
        L("• Clash, Clash Verge, Mihomo, FlClash — config.yaml (раздел rules)\n") +
        L("• ссылка http(s):// на любой из этих файлов");

    sealed class Collected
    {
        public string Source = "", Name = "";
        public readonly List<string> Direct = [], Proxy = [], GeoDirect = [], GeoProxy = [], Skipped = [];
        public int Blocked;
        public string? Default;   // "direct" / "proxy"

        public void Add(string outbound, string entry)
        {
            if (entry.Length == 0) return;
            if (outbound == "block") { Blocked++; return; }
            var geo = entry.StartsWith("geosite-") || entry.StartsWith("geoip-");
            (outbound == "direct" ? (geo ? GeoDirect : Direct) : (geo ? GeoProxy : Proxy)).Add(entry);
        }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Похоже ли содержимое буфера на что-то, что можно импортировать (чтобы подставить его в диалог).</summary>
    public static bool LooksImportable(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("throne://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("happ://routing", StringComparison.OrdinalIgnoreCase)
               || (t.StartsWith('{') || t.StartsWith('[')) && (t.Contains("outboundTag") || t.Contains("\"route\"") || t.Contains("DirectSites") || t.Contains("\"rules\""))
               || Regex.IsMatch(t, @"^\s*-\s*(DOMAIN|DOMAIN-SUFFIX|IP-CIDR|GEOIP|GEOSITE|MATCH)\s*,", RegexOptions.Multiline);
    }

    /// <summary>Разобрать текст (или скачать по ссылке) и создать/обновить профиль маршрутизации.</summary>
    public static async Task<(RoutingProfile Profile, string Report)> ApplyAsync(string text)
    {
        if (text.Trim().StartsWith("throne://", StringComparison.OrdinalIgnoreCase)) return ThroneImport.Apply(text.Trim());
        return Save(await ParseAsync(text));
    }

    /// <summary>Для самопроверки: разобрать без сохранения.</summary>
    internal static async Task<string> DescribeAsync(string text)
    {
        var c = await ParseAsync(text);
        return $"{c.Source} name={c.Name} default={c.Default} direct={c.Direct.Count} proxy={c.Proxy.Count} " +
               $"geoDirect=[{string.Join(",", c.GeoDirect.Distinct())}] geoProxy=[{string.Join(",", c.GeoProxy.Distinct())}] blocked={c.Blocked} " +
               $"skipped=[{string.Join(",", c.Skipped.Distinct())}] sampleDirect=[{string.Join(" ", c.Direct.Take(4))}] sampleProxy=[{string.Join(" ", c.Proxy.Take(4))}]";
    }

    static async Task<Collected> ParseAsync(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (Regex.IsMatch(text, @"^https?://\S+$", RegexOptions.IgnoreCase))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, text);
            req.Headers.UserAgent.ParseAdd("clash-verge/1.0");   // некоторые серверы отдают YAML только «клиентам»
            var body = await (await Http.SendAsync(req)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            return await ParseAsync(body);
        }

        var c = new Collected();
        if (text.StartsWith("happ://", StringComparison.OrdinalIgnoreCase))
        {
            var b64 = text[(text.LastIndexOf('/') + 1)..];
            ParseHappJson(JsonNode.Parse(FromBase64(b64))!.AsObject(), c);
        }
        else if (text.StartsWith('{') || text.StartsWith('['))
        {
            var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                       ?? throw new FormatException(L("Пустой JSON."));
            ParseJson(root, c);
        }
        else if (Regex.IsMatch(text, @"^\s*-?\s*[A-Z][A-Z0-9-]+\s*,", RegexOptions.Multiline)) ParseClash(text, c);
        else
        {
            // Может быть base64 от ссылки/JSON
            string decoded;
            try { decoded = FromBase64(text); }
            catch { throw new FormatException(L("Не удалось распознать формат.\n\n") + SupportedText); }
            if (decoded.TrimStart().StartsWith("throne://")) throw new FormatException(L("Это ссылка Throne в base64 — вставьте её в виде throne://route/…"));
            return await ParseAsync(decoded);
        }

        if (c.Direct.Count + c.Proxy.Count + c.GeoDirect.Count + c.GeoProxy.Count == 0 && c.Default == null)
            throw new FormatException(Lf($"В данных ({c.Source}) не найдено правил маршрутизации."));
        return c;
    }

    // ───────────────────────── JSON-форматы ─────────────────────────

    static void ParseJson(JsonNode root, Collected c)
    {
        if (root is JsonArray arr)
        {
            // v2rayN: массив правил [{ outboundTag, domain, ip, process, … }]
            c.Source = "v2rayN"; c.Name = L("Импорт из v2rayN");
            foreach (var r in arr.OfType<JsonObject>()) XrayRule(r, c, t => XrayOutbound(t, null));
            return;
        }
        var o = root.AsObject();
        if (Has(o, "DirectSites") || Has(o, "ProxySites") || Has(o, "GlobalProxy")) { ParseHappJson(o, c); return; }
        if (o["route"] is JsonObject route) { ParseSingBox(o, route, c); return; }
        if (o["routing"] is JsonObject routing) { ParseXray(o, routing, c); return; }
        // v2rayN: профиль маршрутизации { remarks, ruleSet: "[…]" } или { rules: […] }
        if (o["ruleSet"] is JsonValue rs && rs.TryGetValue<string>(out var rsText) && JsonNode.Parse(rsText) is JsonArray rsArr)
        {
            c.Source = "v2rayN"; c.Name = Str(o, "remarks") ?? L("Импорт из v2rayN");
            foreach (var r in rsArr.OfType<JsonObject>()) XrayRule(r, c, t => XrayOutbound(t, null));
            return;
        }
        if (o["rules"] is JsonArray rules)
        {
            bool singStyle = rules.OfType<JsonObject>().Any(r => Has(r, "outbound") || Has(r, "domain_suffix") || Has(r, "action"));
            if (singStyle) ParseSingBox(o, o, c);
            else
            {
                c.Source = "Xray"; c.Name = Str(o, "remarks") ?? Str(o, "name") ?? L("Импорт правил");
                foreach (var r in rules.OfType<JsonObject>()) XrayRule(r, c, t => XrayOutbound(t, null));
            }
            return;
        }
        throw new FormatException(L("JSON не похож на настройки маршрутизации.\n\n") + SupportedText);
    }

    /// <summary>Happ: { Name, GlobalProxy, DirectSites, DirectIp, ProxySites, ProxyIp, BlockSites, BlockIp, … }.</summary>
    static void ParseHappJson(JsonObject o, Collected c)
    {
        c.Source = "Happ";
        c.Name = Str(o, "Name") ?? L("Импорт из Happ");
        foreach (var (key, outbound) in new[] { ("DirectSites", "direct"), ("DirectIp", "direct"), ("ProxySites", "proxy"), ("ProxyIp", "proxy"), ("BlockSites", "block"), ("BlockIp", "block") })
            foreach (var item in Items(Get(o, key)))
                c.Add(outbound, key.EndsWith("Ip") ? XrayIp(item, c) : XrayDomain(item, c));
        var global = Get(o, "GlobalProxy")?.ToString();
        if (global != null) c.Default = global.Equals("true", StringComparison.OrdinalIgnoreCase) ? "proxy" : "direct";
    }

    /// <summary>Xray (v2RayTun, Streisand, V2Box…): routing.rules + типы outbounds по тегам.</summary>
    static void ParseXray(JsonObject o, JsonObject routing, Collected c)
    {
        c.Source = "Xray"; c.Name = Str(o, "remarks") ?? L("Импорт из Xray");
        var kinds = new Dictionary<string, string>();
        string? first = null;
        if (o["outbounds"] is JsonArray obs)
            foreach (var ob in obs.OfType<JsonObject>())
            {
                var kind = Str(ob, "protocol") switch { "freedom" => "direct", "blackhole" => "block", "dns" => "skip", _ => "proxy" };
                first ??= kind;
                if (Str(ob, "tag") is { } tag) kinds[tag] = kind;
            }
        if (first is "direct" or "proxy") c.Default = first;
        if (routing["rules"] is JsonArray rules)
            foreach (var r in rules.OfType<JsonObject>()) XrayRule(r, c, t => XrayOutbound(t, kinds));
    }

    static string XrayOutbound(string? tag, Dictionary<string, string>? kinds)
    {
        if (tag == null) return "proxy";
        if (kinds != null && kinds.TryGetValue(tag, out var k)) return k;
        var t = tag.ToLowerInvariant();
        return t.Contains("direct") || t == "freedom" ? "direct" : t.Contains("block") || t.Contains("reject") || t == "blackhole" ? "block" : "proxy";
    }

    static void XrayRule(JsonObject r, Collected c, Func<string?, string> outboundOf)
    {
        if (Get(r, "enabled") is JsonValue en && en.TryGetValue<bool>(out var on) && !on) return;
        if (Str(r, "inboundTag") is { } || r["inboundTag"] is JsonArray) return;   // служебные (dns-in, api…)
        var outbound = outboundOf(Str(r, "outboundTag") ?? (Has(r, "balancerTag") ? "proxy" : null));
        if (outbound == "skip") return;
        bool any = false;
        foreach (var d in Items(r["domain"])) { c.Add(outbound, XrayDomain(d, c)); any = true; }
        foreach (var ip in Items(r["ip"])) { c.Add(outbound, XrayIp(ip, c)); any = true; }
        foreach (var p in Items(r["process"])) { c.Add(outbound, p); any = true; }
        if (!any && (Has(r, "port") || Has(r, "protocol") || Has(r, "network")))
            c.Skipped.Add(L("правило по порту/протоколу") + (Str(r, "remarks") is { } rem ? $" ({rem})" : ""));
    }

    /// <summary>Домен в стиле Xray → строка списка TunPilot (или имя geo-набора).</summary>
    static string XrayDomain(string d, Collected c)
    {
        d = d.Trim();
        if (d.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
        {
            var name = d[8..].Split('@')[0].ToLowerInvariant();
            return name == "private" ? "" : "geosite-" + name;
        }
        if (d.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) return d[7..];
        if (d.StartsWith("full:", StringComparison.OrdinalIgnoreCase)) return d;
        if (d.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase)) return d;
        if (d.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase)) return d;
        if (d.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) || d.StartsWith("dotless:", StringComparison.OrdinalIgnoreCase))
        { c.Skipped.Add(d); return ""; }
        return "keyword:" + d;   // в Xray строка без префикса — подстрока
    }

    static string XrayIp(string ip, Collected c)
    {
        ip = ip.Trim();
        if (ip.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
        {
            var name = ip[6..].TrimStart('!').ToLowerInvariant();
            return name == "private" ? "" : "geoip-" + name;
        }
        if (ip.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)) { c.Skipped.Add(ip); return ""; }
        return ip;
    }

    /// <summary>sing-box (Hiddify, NekoRay…): route.rules, route.rule_set, route.final.</summary>
    static void ParseSingBox(JsonObject o, JsonObject route, Collected c)
    {
        c.Source = "sing-box"; c.Name = Str(o, "name") ?? L("Импорт из sing-box");
        var kinds = new Dictionary<string, string>();
        if (o["outbounds"] is JsonArray obs)
            foreach (var ob in obs.OfType<JsonObject>())
                if (Str(ob, "tag") is { } tag)
                    kinds[tag] = Str(ob, "type") switch { "direct" => "direct", "block" => "block", "dns" => "skip", _ => "proxy" };
        // имена наборов: тег → имя файла (geosite-ru.srs → geosite-ru)
        var sets = new Dictionary<string, string>();
        if (route["rule_set"] is JsonArray rsDefs)
            foreach (var rs in rsDefs.OfType<JsonObject>())
                if (Str(rs, "tag") is { } tag)
                {
                    var src = Str(rs, "url") ?? Str(rs, "path") ?? tag;
                    var file = Path.GetFileNameWithoutExtension(src.Split('?')[0]);
                    sets[tag] = file.StartsWith("geo") ? file.ToLowerInvariant() : tag;
                }
        string Out(string? tag) => tag == null ? "proxy" : kinds.TryGetValue(tag, out var k) ? k : XrayOutbound(tag, null);

        if (route["rules"] is JsonArray rules)
            foreach (var r in rules.OfType<JsonObject>())
            {
                var action = Str(r, "action") ?? "route";
                if (action is "sniff" or "hijack-dns" or "resolve") continue;
                if (Has(r, "ip_is_private") || Has(r, "protocol") && !Has(r, "domain") && !Has(r, "ip_cidr")) continue;
                if (Has(r, "inbound") || Has(r, "clash_mode")) continue;
                var outbound = action == "reject" ? "block" : Out(Str(r, "outbound"));
                if (outbound == "skip") continue;
                foreach (var (key, val) in r)
                {
                    switch (key)
                    {
                        case "domain": foreach (var d in Items(val)) c.Add(outbound, "full:" + d); break;
                        case "domain_suffix": foreach (var d in Items(val)) c.Add(outbound, d.TrimStart('.')); break;
                        case "domain_keyword": foreach (var d in Items(val)) c.Add(outbound, "keyword:" + d); break;
                        case "domain_regex": foreach (var d in Items(val)) c.Add(outbound, "regexp:" + d); break;
                        case "ip_cidr": foreach (var d in Items(val)) c.Add(outbound, d); break;
                        case "process_name": foreach (var d in Items(val)) c.Add(outbound, d); break;
                        case "process_path": foreach (var d in Items(val)) c.Add(outbound, d); break;
                        case "geosite": foreach (var d in Items(val)) c.Add(outbound, "geosite-" + d.ToLowerInvariant()); break;
                        case "geoip": foreach (var d in Items(val)) if (d != "private") c.Add(outbound, "geoip-" + d.ToLowerInvariant()); break;
                        case "rule_set": foreach (var d in Items(val)) c.Add(outbound, sets.GetValueOrDefault(d, d)); break;
                        case "outbound" or "action" or "type" or "name" or "invert" or "mode": break;
                        default: c.Skipped.Add(Lf($"поле {key}")); break;
                    }
                }
            }
        if (Str(route, "final") is { } fin && Out(fin) is "direct" or "proxy") c.Default = Out(fin);
        else if (kinds.Count > 0) c.Default = "proxy";
    }

    // ───────────────────────── Clash / Mihomo ─────────────────────────

    static void ParseClash(string yaml, Collected c)
    {
        c.Source = "Clash"; c.Name = L("Импорт из Clash");
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('-')) line = line[1..].Trim();
            line = line.Trim('\'', '"');
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || !Regex.IsMatch(parts[0], "^[A-Z][A-Z0-9-]+$")) continue;
            var type = parts[0];
            if (type is "MATCH" or "FINAL") { c.Default = ClashTarget(parts[1]); continue; }
            if (parts.Length < 3) continue;
            string value = parts[1], outbound = ClashTarget(parts[2]);
            switch (type)
            {
                case "DOMAIN": c.Add(outbound, "full:" + value); break;
                case "DOMAIN-SUFFIX": c.Add(outbound, value.TrimStart('.')); break;
                case "DOMAIN-KEYWORD": c.Add(outbound, "keyword:" + value); break;
                case "DOMAIN-REGEX": c.Add(outbound, "regexp:" + value); break;
                case "IP-CIDR" or "IP-CIDR6": c.Add(outbound, value); break;
                case "GEOIP": if (!value.Equals("LAN", StringComparison.OrdinalIgnoreCase) && !value.Equals("private", StringComparison.OrdinalIgnoreCase)) c.Add(outbound, "geoip-" + value.ToLowerInvariant()); break;
                case "GEOSITE": c.Add(outbound, "geosite-" + value.ToLowerInvariant()); break;
                case "PROCESS-NAME": c.Add(outbound, value); break;
                case "PROCESS-PATH": c.Add(outbound, value); break;
                default: c.Skipped.Add(type); break;
            }
        }
    }

    static string ClashTarget(string t) =>
        t.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ? "direct"
        : t.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase) ? "block" : "proxy";

    // ───────────────────────── сохранение ─────────────────────────

    static (RoutingProfile, string) Save(Collected c)
    {
        var profile = Profiles.Find(c.Name) ?? Profiles.Create(c.Name);
        var marker = $"импорт из {c.Source}" /*noloc*/;   // метка блока в файле списка — не переводится
        WriteSection(profile.DirectTxt, c.Direct, marker);
        WriteSection(profile.ProxyTxt, c.Proxy, marker);
        profile.GeoDirect = profile.GeoDirect.Concat(c.GeoDirect).Distinct().ToList();
        profile.GeoProxy = profile.GeoProxy.Concat(c.GeoProxy).Distinct().ToList();
        if (c.Default != null) profile.DefaultOutbound = c.Default;
        profile.Save();

        var sb = new StringBuilder();
        sb.AppendLine(Lf($"Профиль «{profile.Name}» импортирован ({c.Source})."));
        sb.AppendLine(Lf($"Напрямую: {c.Direct.Distinct().Count()} записей; через VPN: {c.Proxy.Distinct().Count()}."));
        if (c.GeoDirect.Count > 0) sb.AppendLine(L("Наборы напрямую: ") + string.Join(", ", c.GeoDirect.Distinct()));
        if (c.GeoProxy.Count > 0) sb.AppendLine(L("Наборы через VPN: ") + string.Join(", ", c.GeoProxy.Distinct()));
        sb.AppendLine(L("Всё остальное: ") + (profile.DefaultOutbound == "direct" ? L("напрямую") : L("через VPN")));
        if (c.Blocked > 0) sb.AppendLine(Lf($"Правила блокировки пропущены: {c.Blocked} (TunPilot не блокирует сайты)."));
        if (c.Skipped.Count > 0) sb.AppendLine(L("Не поддерживается и пропущено: ") + string.Join(", ", c.Skipped.Distinct().Take(12)) + (c.Skipped.Distinct().Count() > 12 ? "…" : ""));
        return (profile, sb.ToString().TrimEnd());
    }

    /// <summary>Заменяет блок импорта этого источника в файле списка (свои строки не трогаем), без дублей.</summary>
    internal static void WriteSection(string path, List<string> entries, string marker)
    {
        string begin = $"# >>> {marker}", end = $"# <<< {marker}";
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        int b = lines.IndexOf(begin), e = lines.IndexOf(end);
        if (b >= 0 && e > b) lines.RemoveRange(b, e - b + 1);
        var own = lines.Select(l => l.Split('#')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = entries.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !own.Contains(x)).ToList();
        if (fresh.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add(begin);
            lines.AddRange(fresh);
            lines.Add(end);
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    // ───────────────────────── мелочи ─────────────────────────

    static string FromBase64(string s)
    {
        var b64 = s.Trim().Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    static JsonNode? Get(JsonObject o, string key) =>
        o.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    static bool Has(JsonObject o, string key) => Get(o, key) != null;

    static string? Str(JsonObject o, string key) =>
        Get(o, key) is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static IEnumerable<string> Items(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToString() ?? "").Where(s => s.Length > 0),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };
}
