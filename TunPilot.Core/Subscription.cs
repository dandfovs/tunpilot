using System.Text;

namespace TunPilot;

public sealed class ServerInfo
{
    /// <summary>Внутренний ASCII-тег для ядер (s01, s02…): эмодзи в тегах ломают Clash API.</summary>
    public string Tag { get; set; } = "";
    /// <summary>Название из подписки — для меню и сохранения выбора.</summary>
    public required string Name { get; set; }
    public required string Protocol { get; init; }   // vless | hysteria2
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Credential { get; init; } // uuid / пароль
    public required Dictionary<string, string> Params { get; init; }
    /// <summary>Полный конфиг Xray из подписки (формат JSON): запускается в Xray как есть.</summary>
    public System.Text.Json.Nodes.JsonObject? XrayConfig { get; init; }
    /// <summary>Группа (подписка), которой принадлежит сервер.</summary>
    public string GroupId { get; set; } = "";
    /// <summary>Исходная ссылка или JSON-конфиг — для «Копировать ссылку» и поиска дубликатов.</summary>
    public string Raw { get; set; } = "";
    /// <summary>Ключ для сохранения выбора: «группа/имя».</summary>
    public string Key => $"{GroupId}/{Name}";

    public string P(string key, string def = "") => Params.TryGetValue(key, out var v) && v.Length > 0 ? v : def;

    /// <summary>
    /// Серверы, которые sing-box не умеет (xhttp, VLESS-шифрование, экзотические транспорты),
    /// обслуживает Xray как локальный SOCKS.
    /// </summary>
    public bool NeedsXray
    {
        get
        {
            if (XrayConfig != null || Protocol == "wireguard") return true;
            if (Protocol != "vless") return false;
            var type = P("type", "tcp");
            var enc = P("encryption", "none");
            return enc != "none" || type is not ("tcp" or "ws" or "grpc" or "httpupgrade" or "http" or "h2");
        }
    }
}

/// <summary>Сведения о подписке из заголовков ответа (как показывает Throne/Happ).</summary>
public sealed class SubscriptionInfo
{
    public string Title { get; set; } = "";
    public string Announce { get; set; } = "";
    public long Upload { get; set; }
    public long Download { get; set; }
    public long Total { get; set; }          // 0 — безлимит
    public long Expire { get; set; }         // unix-время, 0 — бессрочно
    public int UpdateIntervalHours { get; set; }
    public string WebPageUrl { get; set; } = "";
    public string SupportUrl { get; set; } = "";

    static string FilePath => Path.Combine(AppPaths.Data, "subscription-info.json");

    public static SubscriptionInfo? Load()
    {
        try { return File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<SubscriptionInfo>(File.ReadAllText(FilePath)) : null; }
        catch { return null; }
    }

    public void Save() => File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));

    public static SubscriptionInfo FromHeaders(System.Net.Http.HttpResponseMessage resp)
    {
        string H(string name) =>
            resp.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
            : resp.Content.Headers.TryGetValues(name, out var cv) ? string.Join(",", cv) : "";
        // значения бывают в виде "base64:…"
        static string Decode(string s)
        {
            if (!s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) return s.Trim();
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s[7..].Trim())).Trim(); } catch { return s; }
        }

        var info = new SubscriptionInfo
        {
            Title = Decode(H("profile-title")),
            Announce = Decode(H("announce")),
            WebPageUrl = H("profile-web-page-url"),
            SupportUrl = H("support-url"),
            UpdateIntervalHours = int.TryParse(H("profile-update-interval"), out var h) ? h : 0,
        };
        foreach (var part in H("subscription-userinfo").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !long.TryParse(kv[1], out var n)) continue;
            switch (kv[0].ToLowerInvariant())
            {
                case "upload": info.Upload = n; break;
                case "download": info.Download = n; break;
                case "total": info.Total = n; break;
                case "expire": info.Expire = n; break;
            }
        }
        return info;
    }
}

public static class Subscription
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    /// <summary>
    /// Скачивает подписку и кладёт в кэш. Сначала просим полный формат Xray-JSON
    /// (провайдер отдаёт его клиентам вроде Happ — там больше серверов, балансировщики
    /// и маршруты «YT без рекламы»); если не вышло — обычный список ссылок.
    /// </summary>
    public static async Task<(string Body, SubscriptionInfo Info)> FetchAsync(string url)
    {
        foreach (var ua in new[] { "Happ/1.0", "sing-box" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(ua);
                using var resp = await Http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = Decode(await resp.Content.ReadAsStringAsync());
                if (Parse(body).Count == 0) continue;
                return (body, SubscriptionInfo.FromHeaders(resp));
            }
            catch (HttpRequestException) when (ua != "sing-box") { }
        }
        throw new InvalidDataException(L("Подписка не содержит серверов."));
    }

    static string Decode(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith('[') || t.Contains("://")) return t;
        var b64 = t.Replace("\r", "").Replace("\n", "").Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    public static List<ServerInfo> Parse(string text, string groupId = "g")
    {
        var list = new List<ServerInfo>();
        var used = new HashSet<string>();
        IEnumerable<ServerInfo?> parsed = text.TrimStart().StartsWith('[')
            ? ParseXrayJson(text)
            : text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(TryParseLink);
        foreach (var s in parsed)
        {
            if (s == null) continue;
            // Имена должны быть уникальными (по ним сохраняется выбранный сервер).
            var name = string.IsNullOrWhiteSpace(s.Name) ? $"{s.Host}:{s.Port}" : s.Name;
            var baseName = name;
            for (int i = 2; !used.Add(name) || name == "auto"; i++) name = $"{baseName} ({i})";
            s.Name = name;
            s.Tag = $"{groupId}s{list.Count + 1:00}";   // ASCII-тег для ядер, уникален между группами
            s.GroupId = groupId;
            list.Add(s);
        }
        return list;
    }

    static IEnumerable<ServerInfo?> ParseXrayJson(string text)
    {
        System.Text.Json.Nodes.JsonArray arr;
        try { arr = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsArray(); }
        catch { yield break; }
        foreach (var node in arr)
        {
            if (node is not System.Text.Json.Nodes.JsonObject cfg || cfg["outbounds"] is not System.Text.Json.Nodes.JsonArray) continue;
            yield return new ServerInfo
            {
                Name = (cfg["remarks"]?.GetValue<string>() ?? "").Trim(),
                Protocol = "xray-json", Host = "", Port = 0, Credential = "",
                Params = new(), XrayConfig = cfg, Raw = cfg.ToJsonString(),
            };
        }
    }

    static ServerInfo? TryParseLink(string link)
    {
        try
        {
            int schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return null;
            var scheme = link[..schemeEnd].ToLowerInvariant();
            if (scheme == "hy2") scheme = "hysteria2";
            if (scheme == "wg") scheme = "wireguard";
            if (scheme is not ("vless" or "hysteria2" or "wireguard")) return null;

            var rest = link[(schemeEnd + 3)..];
            string name = "";
            int hash = rest.IndexOf('#');
            if (hash >= 0) { name = Uri.UnescapeDataString(rest[(hash + 1)..]); rest = rest[..hash]; }

            string query = "";
            int q = rest.IndexOf('?');
            if (q >= 0) { query = rest[(q + 1)..]; rest = rest[..q]; }
            rest = rest.TrimEnd('/');

            int at = rest.LastIndexOf('@');
            if (at < 0) return null;
            var cred = Uri.UnescapeDataString(rest[..at]);
            var hostPort = rest[(at + 1)..];

            string host; int port;
            if (hostPort.StartsWith('['))
            {
                int close = hostPort.IndexOf(']');
                host = hostPort[1..close];
                port = int.Parse(hostPort[(close + 2)..]);
            }
            else
            {
                int colon = hostPort.LastIndexOf(':');
                host = hostPort[..colon];
                port = int.Parse(hostPort[(colon + 1)..].Split('/')[0]);
            }

            var prm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=');
                var k = eq < 0 ? kv : kv[..eq];
                var v = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
                prm[k] = v;
            }

            return new ServerInfo
            {
                Name = name.Trim(), Protocol = scheme, Host = host, Port = port,
                Credential = cred, Params = prm, Raw = link.Trim(),
            };
        }
        catch { return null; }
    }
}
