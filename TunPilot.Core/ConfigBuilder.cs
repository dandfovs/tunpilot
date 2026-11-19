using System.Text.Json;
using System.Text.Json.Nodes;

namespace TunPilot;

public static class ConfigBuilder
{
    public const string TunName = "TunPilot";
    /// <summary>fwmark соединений sing-box на Linux (0x2023), см. packaging/tunpilot-helper.</summary>
    public const int LinuxMark = 0x2023;
    static JsonArray Arr(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i!).ToArray());

    // ───────────────────────────── sing-box ─────────────────────────────

    /// <summary>Доменные имена всех VPN-серверов (из ссылок и из полных Xray-конфигов).</summary>
    public static List<string> ServerHosts(IEnumerable<ServerInfo> servers)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            h = h.Trim().Trim('[', ']');
            // "host:port" (endpoint у WireGuard)
            int colon = h.LastIndexOf(':');
            if (colon > 0 && h.IndexOf(':') == colon && int.TryParse(h[(colon + 1)..], out _)) h = h[..colon];
            if (System.Net.IPAddress.TryParse(h, out _) || !h.Contains('.')) return;
            set.Add(h.TrimEnd('.'));
        }
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                        if (k is "address" or "endpoint" or "server" && v is JsonValue jv && jv.TryGetValue<string>(out var str)) Add(str);
                        else Walk(v);
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        foreach (var srv in servers)
        {
            Add(srv.Host);
            if (srv.XrayConfig?["outbounds"] is JsonArray obs) Walk(obs);
        }
        return set.OrderBy(x => x).ToList();
    }

    /// <param name="selectedTag">Тег, выбранный в selector при запуске (сервер или auto-&lt;группа&gt;).</param>
    public static JsonObject SingBox(Settings s, RoutingProfile p, IReadOnlyList<ServerInfo> servers,
        IReadOnlyDictionary<string, int> xrayPorts, int clashPort, string clashSecret, ISet<string> geoAvailable,
        string selectedTag)
    {
        var outbounds = new JsonArray();
        var tags = servers.Select(x => x.Tag).ToList();

        // «Авто» — своё для каждой группы (urltest по её серверам).
        var autoTags = new List<string>();
        foreach (var grp in servers.GroupBy(x => x.GroupId))
        {
            var autoTag = "auto-" + grp.Key;
            autoTags.Add(autoTag);
            outbounds.Add(new JsonObject
            {
                ["type"] = "urltest", ["tag"] = autoTag, ["outbounds"] = Arr(grp.Select(x => x.Tag)),
                ["url"] = s.LatencyUrl, ["interval"] = "3m", ["tolerance"] = 80,
            });
        }
        var all = autoTags.Concat(tags).ToList();
        if (all.Count == 0) all.Add("direct");
        var selected = all.Contains(selectedTag) ? selectedTag : all[0];
        outbounds.Insert(0, new JsonObject
        {
            ["type"] = "selector", ["tag"] = "select",
            ["outbounds"] = Arr(all), ["default"] = selected,
            ["interrupt_exist_connections"] = false,
        });

        foreach (var srv in servers)
            outbounds.Add(xrayPorts.TryGetValue(srv.Tag, out var port)
                ? new JsonObject
                {
                    ["type"] = "socks", ["tag"] = srv.Tag, ["server"] = "127.0.0.1",
                    ["server_port"] = port, ["version"] = "5",
                }
                : NativeOutbound(srv));

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var final = p.DefaultOutbound == "direct" ? "direct" : "select";

        // Порядок важен: пользовательские списки сильнее geo-наборов.
        var ruleSets = new JsonArray
        {
            new JsonObject { ["type"] = "local", ["tag"] = "user-direct", ["format"] = "source", ["path"] = RuleLists.DirectJson },
            new JsonObject { ["type"] = "local", ["tag"] = "user-proxy", ["format"] = "source", ["path"] = RuleLists.ProxyJson },
        };
        // Готовые наборы (geoip/geosite) склеены в два текстовых файла: их состав меняется без перезапуска.
        ruleSets.Add(new JsonObject { ["type"] = "local", ["tag"] = "geo-direct", ["format"] = "source", ["path"] = GeoRuleSets.DirectJson });
        ruleSets.Add(new JsonObject { ["type"] = "local", ["tag"] = "geo-proxy", ["format"] = "source", ["path"] = GeoRuleSets.ProxyJson });

        var dnsRules = new JsonArray
        {
            // Адреса самих VPN-серверов — только через локальный DNS. Иначе петля: Xray резолвит
            // сервер через системный DNS → запрос попадает в TUN → удалённый DNS через тот же Xray.
            new JsonObject { ["process_name"] = Arr([Platform.Exe("xray"), Platform.AppProcessName]), ["server"] = "local" },
        };
        var serverHosts = ServerHosts(servers);
        if (serverHosts.Count > 0)
            dnsRules.Add(new JsonObject { ["domain"] = Arr(serverHosts), ["server"] = "local" });
        dnsRules.Add(new JsonObject { ["rule_set"] = "user-proxy", ["server"] = "remote" });
        dnsRules.Add(new JsonObject { ["rule_set"] = "user-direct", ["server"] = "local" });
        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            // Xray и сама программа ходят напрямую — иначе петля (их трафик снова попал бы в TUN).
            new JsonObject { ["process_name"] = Arr([Platform.Exe("xray"), Platform.AppProcessName]), ["outbound"] = "direct" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
            new JsonObject { ["rule_set"] = "user-proxy", ["outbound"] = "select" },
            new JsonObject { ["rule_set"] = "user-direct", ["outbound"] = "direct" },
        };
        dnsRules.Add(new JsonObject { ["rule_set"] = "geo-proxy", ["server"] = "remote" });
        routeRules.Add(new JsonObject { ["rule_set"] = "geo-proxy", ["outbound"] = "select" });
        dnsRules.Add(new JsonObject { ["rule_set"] = "geo-direct", ["server"] = "local" });
        routeRules.Add(new JsonObject { ["rule_set"] = "geo-direct", ["outbound"] = "direct" });

        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = TunName,
                ["address"] = Arr(["172.19.0.1/30", "fdfe:dcba:9876::1/126"]),
                ["mtu"] = 1500, ["auto_route"] = true,   // как в Throne
                // strict_route выключен специально: иначе наши собственные маршруты «мимо TUN» не работают.
                ["strict_route"] = false,
                ["stack"] = "mixed",
            },
        };
        // Локальный прокси (HTTP+SOCKS) для программ, которые умеют работать через прокси.
        if (s.LocalProxyEnabled)
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed", ["tag"] = "mixed-in", ["listen"] = "127.0.0.1", ["listen_port"] = s.LocalProxyPort,
            });

        var route = new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "local",
            ["rule_set"] = ruleSets,
            ["rules"] = routeRules,
            ["final"] = final,
        };
        // Linux: метка на всех соединениях sing-box — kill-switch (nftables) пропускает только их и трафик в TUN.
        if (OperatingSystem.IsLinux()) route["default_mark"] = LinuxMark;

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = s.LogLevelSingBox, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = s.RemoteDns, ["detour"] = "select" },
                    new JsonObject { ["type"] = "udp", ["tag"] = "local", ["server"] = s.LocalDns },
                },
                ["rules"] = dnsRules,
                ["final"] = final == "direct" ? "local" : "remote",
                ["strategy"] = "prefer_ipv4",
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = route,
            ["experimental"] = new JsonObject
            {
                // Clash API нужен, чтобы переключать сервер в selector БЕЗ перезапуска ядра.
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashPort}", ["secret"] = clashSecret,
                },
            },
        };
    }

    static JsonObject NativeOutbound(ServerInfo s)
    {
        if (s.Protocol == "hysteria2")
        {
            var o = new JsonObject
            {
                ["type"] = "hysteria2", ["tag"] = s.Tag, ["server"] = s.Host, ["server_port"] = s.Port,
                ["password"] = s.Credential,
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true, ["server_name"] = s.P("sni", s.Host),
                    ["insecure"] = s.P("insecure") == "1", ["alpn"] = Arr(["h3"]),
                },
            };
            if (s.P("obfs") is { Length: > 0 } obfs)
                o["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = s.P("obfs-password") };
            return o;
        }

        // VLESS
        var v = new JsonObject
        {
            ["type"] = "vless", ["tag"] = s.Tag, ["server"] = s.Host, ["server_port"] = s.Port,
            ["uuid"] = s.Credential, ["packet_encoding"] = "xudp",
        };
        if (s.P("flow") is { Length: > 0 } flow) v["flow"] = flow;

        var security = s.P("security", "none");
        if (security is "tls" or "reality")
        {
            var tls = new JsonObject
            {
                ["enabled"] = true, ["server_name"] = s.P("sni", s.Host),
                ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = s.P("fp", "chrome") },
            };
            if (s.P("allowInsecure") == "1" || s.P("insecure") == "1") tls["insecure"] = true;
            if (s.P("alpn") is { Length: > 0 } alpn) tls["alpn"] = Arr(alpn.Split(','));
            if (security == "reality")
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true, ["public_key"] = s.P("pbk"), ["short_id"] = s.P("sid"),
                };
            v["tls"] = tls;
        }

        switch (s.P("type", "tcp"))
        {
            case "ws":
                var ws = new JsonObject { ["type"] = "ws", ["path"] = s.P("path", "/") };
                if (s.P("host") is { Length: > 0 } wh) ws["headers"] = new JsonObject { ["Host"] = wh };
                v["transport"] = ws;
                break;
            case "grpc":
                v["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = s.P("serviceName") };
                break;
            case "httpupgrade":
                v["transport"] = new JsonObject { ["type"] = "httpupgrade", ["path"] = s.P("path", "/"), ["host"] = s.P("host") };
                break;
            case "http" or "h2":
                var h = new JsonObject { ["type"] = "http", ["path"] = s.P("path", "/") };
                if (s.P("host") is { Length: > 0 } hh) h["host"] = Arr(hh.Split(','));
                v["transport"] = h;
                break;
        }
        return v;
    }

    // ───────────────────────────── Xray ─────────────────────────────

    public static JsonObject Xray(Settings s, IReadOnlyList<ServerInfo> servers, IReadOnlyDictionary<string, int> ports)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        var balancers = new JsonArray();
        var observed = new List<string>(); JsonNode? pingConfig = null;
        var burstObserved = new List<string>(); JsonNode? burstPing = null;
        int i = 0;
        // Первым — direct: в Xray нераспознанный трафик уходит в первый outbound.
        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        foreach (var srv in servers)
        {
            var inTag = $"in-{i}"; var prefix = $"c{i}-"; i++;
            inbounds.Add(new JsonObject
            {
                ["tag"] = inTag, ["listen"] = "127.0.0.1", ["port"] = ports[srv.Tag], ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["udp"] = true, ["ip"] = "127.0.0.1" },
                ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = Arr(["http", "tls", "quic"]) },
            });

            if (srv.XrayConfig == null)
            {
                var outTag = prefix + "proxy";
                outbounds.Add(srv.Protocol == "wireguard" ? XrayWireguard(srv, outTag) : XrayVless(srv, outTag));
                rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = Arr([inTag]), ["outboundTag"] = outTag });
                continue;
            }

            MergeFullConfig(srv.XrayConfig, inTag, prefix, outbounds, rules, balancers,
                observed, ref pingConfig, burstObserved, ref burstPing);
        }

        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = s.LogLevelXray },
            // Свой DNS, чтобы Xray не резолвил адреса серверов через системный DNS (а значит — через TUN).
            ["dns"] = new JsonObject { ["servers"] = Arr([s.LocalDns, "1.1.1.1"]), ["queryStrategy"] = "UseIPv4" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules, ["balancers"] = balancers,
            },
        };
        // Наблюдатель (пинги для балансировщиков) в Xray один на процесс — объединяем подписки всех конфигов.
        if (observed.Count > 0)
        {
            var obs = pingConfig?.DeepClone().AsObject() ?? new JsonObject();
            obs["subjectSelector"] = Arr(observed.Distinct());
            cfg["observatory"] = obs;
        }
        if (burstObserved.Count > 0)
            cfg["burstObservatory"] = new JsonObject
            {
                ["subjectSelector"] = Arr(burstObserved.Distinct()),
                ["pingConfig"] = burstPing?.DeepClone() ?? new JsonObject
                {
                    ["destination"] = "https://www.gstatic.com/generate_204", ["interval"] = "1m",
                    ["timeout"] = "2s", ["sampling"] = 3,
                },
            };
        return cfg;
    }

    /// <summary>
    /// Встраивает полный конфиг Xray из подписки в общий процесс: все теги получают префикс,
    /// правила ограничиваются своим SOCKS-входом, в конце — правило «всё остальное → первый outbound конфига».
    /// </summary>
    static void MergeFullConfig(JsonObject src, string inTag, string prefix, JsonArray outbounds, JsonArray rules,
        JsonArray balancers, List<string> observed, ref JsonNode? pingConfig,
        List<string> burstObserved, ref JsonNode? burstPing)
    {
        string P(string? tag) => prefix + tag;
        string? firstTag = null;

        foreach (var node in src["outbounds"]!.AsArray())
        {
            if (node is not JsonObject ob) continue;
            var o = ob.DeepClone().AsObject();
            var tag = o["tag"]?.GetValue<string>() ?? $"out{outbounds.Count}";
            o["tag"] = P(tag);
            firstTag ??= P(tag);
            // Цепочки прокси ссылаются на другие outbound'ы по тегу.
            if (o["streamSettings"]?["sockopt"]?["dialerProxy"] is JsonValue dp)
                o["streamSettings"]!["sockopt"]!["dialerProxy"] = P(dp.GetValue<string>());
            if (o["proxySettings"]?["tag"] is JsonValue pt)
                o["proxySettings"]!["tag"] = P(pt.GetValue<string>());
            outbounds.Add(o);
        }

        if (src["routing"]?["balancers"] is JsonArray bals)
            foreach (var node in bals)
            {
                var b = node!.DeepClone().AsObject();
                b["tag"] = P(b["tag"]?.GetValue<string>());
                // selector — префиксы тегов, поэтому префикс конфига просто дописываем спереди.
                if (b["selector"] is JsonArray sel)
                    b["selector"] = Arr(sel.Select(x => P(x!.GetValue<string>())));
                if (b["fallbackTag"] is JsonValue fb) b["fallbackTag"] = P(fb.GetValue<string>());
                balancers.Add(b);
            }

        if (src["routing"]?["rules"] is JsonArray srcRules)
            foreach (var node in srcRules)
            {
                var r = node!.DeepClone().AsObject();
                r.Remove("inboundTag");
                r.Remove("ruleTag");
                r["type"] = "field";
                r["inboundTag"] = Arr([inTag]);
                if (r["outboundTag"] is JsonValue ot) r["outboundTag"] = P(ot.GetValue<string>());
                if (r["balancerTag"] is JsonValue bt) r["balancerTag"] = P(bt.GetValue<string>());
                rules.Add(r);
            }

        rules.Add(new JsonObject
        {
            ["type"] = "field", ["inboundTag"] = Arr([inTag]), ["outboundTag"] = firstTag ?? "direct",
        });

        if (src["observatory"] is JsonObject obs)
        {
            pingConfig ??= obs;
            if (obs["subjectSelector"] is JsonArray ss) observed.AddRange(ss.Select(x => P(x!.GetValue<string>())));
        }
        if (src["burstObservatory"] is JsonObject bobs)
        {
            burstPing ??= bobs["pingConfig"];
            if (bobs["subjectSelector"] is JsonArray ss) burstObserved.AddRange(ss.Select(x => P(x!.GetValue<string>())));
        }
    }

    static JsonObject XrayWireguard(ServerInfo s, string tag)
    {
        var host = s.Host.Contains(':') ? $"[{s.Host}]" : s.Host;
        var settings = new JsonObject
        {
            ["secretKey"] = s.Credential,
            ["address"] = Arr(s.P("address", "172.16.0.2/32").Split(',', StringSplitOptions.TrimEntries)),
            ["peers"] = new JsonArray
            {
                new JsonObject
                {
                    ["publicKey"] = s.P("publickey", s.P("publicKey")), ["endpoint"] = $"{host}:{s.Port}",
                    ["allowedIPs"] = Arr(["0.0.0.0/0", "::/0"]),
                },
            },
            ["mtu"] = int.TryParse(s.P("mtu", "1280"), out var mtu) ? mtu : 1280,
        };
        if (s.P("reserved") is { Length: > 0 } r)
            settings["reserved"] = new JsonArray(r.Split(',').Select(x => (JsonNode)int.Parse(x.Trim())!).ToArray());
        return new JsonObject { ["tag"] = tag, ["protocol"] = "wireguard", ["settings"] = settings };
    }

    static JsonObject XrayVless(ServerInfo s, string tag)
    {
        var user = new JsonObject { ["id"] = s.Credential, ["encryption"] = s.P("encryption", "none") };
        if (s.P("flow") is { Length: > 0 } flow) user["flow"] = flow;

        var network = s.P("type", "tcp");
        if (network == "splithttp") network = "xhttp";
        var security = s.P("security", "none");
        var stream = new JsonObject { ["network"] = network, ["security"] = security };

        if (security == "reality")
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = s.P("sni"), ["fingerprint"] = s.P("fp", "chrome"),
                ["publicKey"] = s.P("pbk"), ["shortId"] = s.P("sid"), ["spiderX"] = s.P("spx"),
            };
        else if (security == "tls")
        {
            var tls = new JsonObject
            {
                ["serverName"] = s.P("sni", s.Host), ["fingerprint"] = s.P("fp", "chrome"),
                ["allowInsecure"] = s.P("allowInsecure") == "1",
            };
            if (s.P("alpn") is { Length: > 0 } alpn) tls["alpn"] = Arr(alpn.Split(','));
            stream["tlsSettings"] = tls;
        }

        switch (network)
        {
            case "xhttp":
                var x = new JsonObject { ["path"] = s.P("path", "/"), ["mode"] = s.P("mode", "auto") };
                if (s.P("host") is { Length: > 0 } xh) x["host"] = xh;
                if (s.P("extra") is { Length: > 0 } extra)
                {
                    try { x["extra"] = JsonNode.Parse(extra); } catch (JsonException) { /* кривой extra — пропускаем */ }
                }
                stream["xhttpSettings"] = x;
                break;
            case "ws":
                stream["wsSettings"] = new JsonObject { ["path"] = s.P("path", "/"), ["host"] = s.P("host") };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = s.P("serviceName") };
                break;
            case "kcp":
                stream["kcpSettings"] = new JsonObject { ["seed"] = s.P("seed") };
                break;
        }

        return new JsonObject
        {
            ["tag"] = tag, ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject { ["address"] = s.Host, ["port"] = s.Port, ["users"] = new JsonArray { user } },
                },
            },
            ["streamSettings"] = stream,
        };
    }
}
