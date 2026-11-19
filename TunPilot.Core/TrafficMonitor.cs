using System.Text.Json;

namespace TunPilot;

/// <summary>
/// Опрашивает ядро раз в секунду (пока подключено, даже со свёрнутым окном): соединения, скорости,
/// трафик по серверам за сессию; пишет почасовую статистику на диск.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    public sealed record Conn(string Process, string Dest, string Net, string OutTag, string Outbound,
        long Up, long Down, long SpUp, long SpDown);

    public sealed class Snapshot
    {
        public List<Conn> Conns { get; init; } = [];
        public long ProxyUp, ProxyDown, DirectUp, DirectDown;   // байт/с
    }

    readonly IAppState _app;
    readonly System.Threading.Timer _timer;
    readonly SynchronizationContext? _ui;
    readonly Dictionary<string, (long Up, long Down)> _prev = new();
    bool _polling;
    int _tick;

    public StatsStore Stats { get; } = StatsStore.Load();
    /// <summary>Трафик по тегам серверов за текущее подключение.</summary>
    public Dictionary<string, (long Up, long Down)> PerTag { get; } = new();
    public long ProxyUp, ProxyDown, DirectUp, DirectDown;   // суммарно за сессию
    /// <summary>Какой сервер выбрало «Авто» в каждой группе: тег авто → тег сервера.</summary>
    public Dictionary<string, string> AutoNow { get; } = new();
    public Snapshot? Last { get; private set; }
    public event Action<Snapshot>? Updated;

    /// <param name="app">Состояние приложения (ядро, серверы, группы).</param>
    /// <remarks>
    /// Создавать в UI-потоке: опрос и событие <see cref="Updated"/> выполняются в его контексте синхронизации,
    /// так что подписчики могут сразу трогать интерфейс. Без контекста (консоль, тесты) — прямо в потоке таймера.
    /// </remarks>
    public TrafficMonitor(IAppState app)
    {
        _app = app;
        _ui = SynchronizationContext.Current;
        _timer = new System.Threading.Timer(_ =>
        {
            if (_ui != null) _ui.Post(async _ => await PollAsync(), null);
            else _ = PollAsync();
        }, null, 1000, 1000);
    }

    public void ResetSession()
    {
        _prev.Clear(); PerTag.Clear(); AutoNow.Clear();
        ProxyUp = ProxyDown = DirectUp = DirectDown = 0;
        Last = null;
    }

    async Task PollAsync()
    {
        if (_polling) return;
        if (!_app.Core.Running)
        {
            if (Last != null) { Last = null; Updated?.Invoke(new Snapshot()); }
            return;
        }
        _polling = true;
        try
        {
            _tick++;
            var snap = await _app.Core.GetConnectionsAsync();
            if (snap is not { } doc || !doc.TryGetProperty("connections", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            var servers = _app.AllServers.ToDictionary(s => s.Tag);
            var groupNames = _app.Groups.Groups.ToDictionary(g => g.Id, g => g.Name);
            var seen = new HashSet<string>();
            var list = new List<Conn>();
            var res = new Snapshot();

            foreach (var c in arr.EnumerateArray())
            {
                var id = c.GetProperty("id").GetString()!;
                long up = c.GetProperty("upload").GetInt64(), down = c.GetProperty("download").GetInt64();
                var prev = _prev.GetValueOrDefault(id);
                long dU = Math.Max(0, up - prev.Up), dD = Math.Max(0, down - prev.Down);
                _prev[id] = (up, down);
                seen.Add(id);

                var chains = c.TryGetProperty("chains", out var ch) ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var outTag = chains.FirstOrDefault() ?? "?";
                bool direct = outTag == "direct";
                servers.TryGetValue(outTag, out var srv);
                string outbound = direct ? "direct" : srv?.Name ?? outTag;
                if (chains.Any(x => x.StartsWith("auto-"))) outbound += L("  (авто)");

                var md = c.GetProperty("metadata");
                string Get(string k) => md.TryGetProperty(k, out var v) ? v.ToString() : "";
                var procPath = Get("processPath");
                var proc = procPath.Length > 0 ? Path.GetFileName(procPath) : L("Система");
                var host = Get("host");
                var dest = (host.Length > 0 ? host : Get("destinationIP")) + ":" + Get("destinationPort");

                if (direct) { res.DirectUp += dU; res.DirectDown += dD; DirectUp += dU; DirectDown += dD; }
                else
                {
                    res.ProxyUp += dU; res.ProxyDown += dD; ProxyUp += dU; ProxyDown += dD;
                    var t = PerTag.GetValueOrDefault(outTag);
                    PerTag[outTag] = (t.Up + dU, t.Down + dD);
                }
                if (dU + dD > 0)
                {
                    var srvKey = direct ? "Direct" : srv != null ? $"{groupNames.GetValueOrDefault(srv.GroupId, "")}|{srv.Name}" : outTag;
                    Stats.Add("srv", srvKey, dD, dU);
                    Stats.Add("app", proc, dD, dU);
                }
                list.Add(new Conn(proc, dest, Get("network"), outTag, outbound, up, down, dU, dD));
            }
            foreach (var gone in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(gone);

            if (_tick % 5 == 1)
                foreach (var g in _app.Groups.Groups)
                    if (await _app.Core.GetAutoCurrentAsync(g.AutoTag) is { } now) AutoNow[g.AutoTag] = now;
            if (_tick % 60 == 0) Stats.Save();

            Last = new Snapshot
            {
                Conns = list, ProxyUp = res.ProxyUp, ProxyDown = res.ProxyDown,
                DirectUp = res.DirectUp, DirectDown = res.DirectDown,
            };
            Updated?.Invoke(Last);
        }
        catch { /* ядро перезапускается — пропустим тик */ }
        finally { _polling = false; }
    }

    public void Dispose() { _timer.Dispose(); Stats.Save(); }
}

/// <summary>Почасовая статистика трафика: (час, вид, ключ) → [входящий, исходящий]. Хранится 35 дней.</summary>
public sealed class StatsStore
{
    readonly Dictionary<string, long[]> _data;
    static string FilePath => Path.Combine(AppPaths.Data, "stats.json");
    static long Hour(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds() / 3600;

    StatsStore(Dictionary<string, long[]> data) => _data = data;

    public static StatsStore Load()
    {
        Dictionary<string, long[]> data;
        try { data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { data = new(); }
        var min = Hour(DateTime.UtcNow.AddDays(-35));
        foreach (var k in data.Keys.Where(k => long.TryParse(k.Split('|')[0], out var h) && h < min).ToList()) data.Remove(k);
        return new StatsStore(data);
    }

    public void Add(string kind, string key, long inBytes, long outBytes)
    {
        lock (_data)
        {
            var k = $"{Hour(DateTime.UtcNow)}|{kind}|{key}";
            if (!_data.TryGetValue(k, out var v)) _data[k] = v = new long[2];
            v[0] += inBytes; v[1] += outBytes;
        }
    }

    public void Save()
    {
        try { lock (_data) File.WriteAllText(FilePath, JsonSerializer.Serialize(_data)); } catch { }
    }

    public sealed record Row(DateTime HourLocal, string Kind, string Key, long In, long Out);

    /// <summary>Все записи начиная с момента from (UTC).</summary>
    public List<Row> Since(DateTime fromUtc)
    {
        var min = Hour(fromUtc);
        lock (_data)
            return _data.Select(kv =>
                {
                    var p = kv.Key.Split('|', 3);
                    return (h: long.Parse(p[0]), kind: p[1], key: p[2], v: kv.Value);
                })
                .Where(x => x.h >= min)
                .Select(x => new Row(DateTimeOffset.FromUnixTimeSeconds(x.h * 3600).LocalDateTime, x.kind, x.key, x.v[0], x.v[1]))
                .ToList();
    }
}
