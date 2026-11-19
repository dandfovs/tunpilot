using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TunPilot;

/// <summary>Запуск/остановка sing-box и Xray, переключение сервера через Clash API.</summary>
public sealed class CoreManager : IDisposable
{
    Process? _singBox, _xray;
    int _clashPort;
    string _secret = "";
    bool _stopping;
    readonly ChildJob _job = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public bool Running => _singBox is { HasExited: false };
    /// <summary>Geo-наборы, которые не удалось найти при последней подготовке.</summary>
    public List<string> GeoMissing { get; private set; } = [];
    /// <summary>Ядро упало само (не по нашей команде). Аргумент — имя ядра.</summary>
    public event Action<string>? UnexpectedExit;

    public static string SingBoxConfigPath => Path.Combine(AppPaths.Runtime, "sing-box.json");
    public static string XrayConfigPath => Path.Combine(AppPaths.Runtime, "xray.json");

    /// <summary>Генерирует конфиги и проверяет их самими ядрами. Возвращает null или текст ошибки.</summary>
    public async Task<string?> PrepareAsync(Settings s, IReadOnlyList<ServerInfo> servers, string selectedTag)
    {
        Config = s;
        RuleLists.Compile();
        var (geo, missing) = await GeoRuleSets.EnsureAsync(s, Profiles.Current);
        GeoMissing = missing;
        var xrayServers = servers.Where(x => x.NeedsXray).ToList();
        var ports = xrayServers.ToDictionary(x => x.Tag, _ => FreePort());
        _xrayPorts = ports;
        _clashPort = FreePort();
        _secret = Guid.NewGuid().ToString("N");

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SingBoxConfigPath, ConfigBuilder.SingBox(s, Profiles.Current, servers, ports, _clashPort, _secret, geo, selectedTag).ToJsonString(opts));
        if (xrayServers.Count > 0)
            File.WriteAllText(XrayConfigPath, ConfigBuilder.Xray(s, xrayServers, ports).ToJsonString(opts));
        else if (File.Exists(XrayConfigPath))
            File.Delete(XrayConfigPath);

        var (code, output) = await RunToEnd(AppPaths.SingBoxExe, $"check -c \"{SingBoxConfigPath}\"");
        if (code != 0) return L("sing-box отклонил конфиг:\n") + output;
        if (xrayServers.Count > 0)
        {
            (code, output) = await RunToEnd(AppPaths.XrayExe, $"run -test -c \"{XrayConfigPath}\"");
            if (code != 0) return L("Xray отклонил конфиг:\n") + output;
        }
        return null;
    }

    public async Task StartAsync()
    {
        Stop();
        _stopping = false;
        if (File.Exists(XrayConfigPath))
            _xray = Launch(AppPaths.XrayExe, $"run -c \"{XrayConfigPath}\"", "xray");
        _singBox = Launch(AppPaths.SingBoxExe, $"run -c \"{SingBoxConfigPath}\" -D \"{AppPaths.Runtime}\" --disable-color", "sing-box");

        // Даём ядру поднять TUN; если оно сразу упало — показываем хвост лога.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(150);
            if (_singBox.HasExited)
            {
                var tail = TailLog("sing-box");
                Stop();
                throw new InvalidOperationException(L("sing-box завершился при запуске:\n") + tail);
            }
        }
    }

    public void Stop()
    {
        _stopping = true;
        foreach (var p in new[] { _singBox, _xray })
        {
            try { if (p is { HasExited: false }) { p.Kill(true); p.WaitForExit(3000); } } catch { }
            p?.Dispose();
        }
        _singBox = _xray = null;
    }

    /// <summary>Переключение сервера на лету: TUN и уже открытые соединения не трогаются.</summary>
    public async Task<bool> SelectAsync(string tag)
    {
        if (!Running) return false;
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"http://127.0.0.1:{_clashPort}/proxies/select")
        {
            Content = JsonContent.Create(new { name = tag }),
        };
        req.Headers.Authorization = new("Bearer", _secret);
        try { using var r = await Http.SendAsync(req); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    /// <summary>Тест задержки сервера через ядро (Clash API). null — не ответил.</summary>
    public Task<int?> DelayAsync(string tag, int timeoutMs = 8000) =>
        Running ? Measure(_xrayPorts, _clashPort, _secret, tag, timeoutMs) : Task.FromResult<int?>(null);

    Dictionary<string, int> _xrayPorts = new();

    /// <summary>Текущие настройки (адреса тестов, потоки и т.п.) — задаются при подготовке и из окна настроек.</summary>
    public static Settings? Config { get; set; }
    static string LatencyUrl => Config?.LatencyUrl is { Length: > 0 } u ? u : "https://www.gstatic.com/generate_204";

    /// <summary>
    /// Как в Throne: время ответа по уже установленному соединению (≈ пинг через туннель).
    /// Для серверов в Xray — два запроса через его SOCKS-вход на одном keep-alive соединении,
    /// меряем второй. Для серверов sing-box — его URL-тест (он включает установку соединения).
    /// </summary>
    static Task<int?> Measure(IReadOnlyDictionary<string, int> xrayPorts, int clash, string secret, string tag, int timeoutMs) =>
        xrayPorts.TryGetValue(tag, out var port) ? WarmRtt(port, timeoutMs) : Delay(clash, secret, tag, timeoutMs);

    static async Task<int?> WarmRtt(int socksPort, int timeoutMs)
    {
        using var handler = SocksHandler(socksPort);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        var url = LatencyUrl;
        try
        {
            using (var warm = await http.GetAsync(url)) if ((int)warm.StatusCode >= 400) return null;
            int best = int.MaxValue;
            for (int i = 0; i < 2; i++)   // лучший из двух — меньше влияния случайных всплесков
            {
                var sw = Stopwatch.StartNew();
                using var r = await http.GetAsync(url);
                sw.Stop();
                if ((int)r.StatusCode < 400) best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }
            return best == int.MaxValue ? null : Math.Max(1, best);
        }
        catch { return null; }
    }

    /// <summary>
    /// Тест задержки БЕЗ подключения: поднимаем ядра в тестовом режиме — без TUN и без входов,
    /// системный трафик не трогаем; меряем через Clash API и сразу гасим.
    /// </summary>
    public Task TestOfflineAsync(Settings s, IReadOnlyList<ServerInfo> servers, IEnumerable<string> tags,
        Action<string> onStart, Action<string, int?> onResult) =>
        WithTestCoresAsync(s, servers, async (ports, clash, secret) =>
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(tags.Select(async tag =>
            {
                await gate.WaitAsync();
                try { onStart(tag); onResult(tag, await Measure(ports, clash, secret, tag, 10000)); }
                finally { gate.Release(); }
            }));
        });

    /// <summary>
    /// Тест скорости: качаем тестовый файл через SOCKS-вход сервера в Xray ~8 секунд.
    /// Подключены — через рабочее ядро, иначе через временное тестовое. Серверы по одному,
    /// чтобы не делить канал. onProgress — текущая скорость, onResult — итог (Мбит/с, null — не вышло).
    /// </summary>
    public async Task SpeedTestAsync(Settings s, IReadOnlyList<ServerInfo> servers, IReadOnlyList<string> tags,
        Action<string, double> onProgress, Action<string, double?> onResult, CancellationToken ct)
    {
        async Task Run(IReadOnlyDictionary<string, int> ports)
        {
            foreach (var tag in tags)
            {
                if (ct.IsCancellationRequested) break;
                onResult(tag, ports.TryGetValue(tag, out var port)
                    ? await SpeedMbps(port, TimeSpan.FromSeconds(Math.Clamp(s.SpeedSeconds, 3, 30)), mbps => onProgress(tag, mbps), ct)
                    : null);
            }
        }
        if (Running) await Run(_xrayPorts);
        else await WithTestCoresAsync(s, servers, (ports, _, _) => Run(ports));
    }

    // Тестовые файлы: если один недоступен (блокировки), берём следующий.
    static readonly string[] SpeedUrls =
    [
        "https://fsn1-speed.hetzner.com/1GB.bin",
        "https://proof.ovh.net/files/1Gb.dat",
        "https://speed.cloudflare.com/__down?bytes=1000000000",
    ];

    static async Task<double?> SpeedMbps(int socksPort, TimeSpan duration, Action<double> progress, CancellationToken ct)
    {
        var urls = Environment.GetEnvironmentVariable("TUNPILOT_SPEED_URL") is { Length: > 0 } o ? [o]
            : Config?.SpeedUrls is { Count: > 0 } cu ? cu.ToArray() : SpeedUrls;
        foreach (var url in urls)
        {
            if (ct.IsCancellationRequested) return null;
            if (await SpeedMbps(socksPort, url, duration, progress, ct) is { } r) return r;
        }
        return null;
    }

    /// <summary>
    /// HTTP-клиент через SOCKS5-вход Xray. Своё рукопожатие вместо встроенного прокси .NET:
    /// передаём серверу ИМЯ домена (ATYP=3), чтобы адрес резолвил Xray, а не системный DNS
    /// (тот может вернуть подменённый fake-ip, если включён чужой TUN).
    /// </summary>
    /// <summary>HTTP-клиент через SOCKS-вход сервера в Xray (если подключены и сервер в Xray), иначе null.</summary>
    public HttpClient? ProxiedClient(string tag)
    {
        if (!Running) return null;
        if (tag.StartsWith("auto-") || !_xrayPorts.ContainsKey(tag)) tag = _xrayPorts.Keys.FirstOrDefault() ?? "";
        return _xrayPorts.TryGetValue(tag, out var port) ? new HttpClient(SocksHandler(port)) : null;
    }

    static SocketsHttpHandler SocksHandler(int socksPort) => new()
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 1,
        ConnectCallback = async (ctx, ct) =>
        {
            var sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await sock.ConnectAsync(IPAddress.Loopback, socksPort, ct);
                var s = new NetworkStream(sock, ownsSocket: true);
                await s.WriteAsync(new byte[] { 5, 1, 0 }, ct);                 // SOCKS5, без авторизации
                var buf = new byte[262];
                await s.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
                if (buf[1] != 0) throw new IOException(L("SOCKS: метод отклонён"));
                var host = System.Text.Encoding.ASCII.GetBytes(ctx.DnsEndPoint.Host);
                var req = new byte[7 + host.Length];
                req[0] = 5; req[1] = 1; req[2] = 0; req[3] = 3; req[4] = (byte)host.Length;
                host.CopyTo(req, 5);
                req[^2] = (byte)(ctx.DnsEndPoint.Port >> 8); req[^1] = (byte)ctx.DnsEndPoint.Port;
                await s.WriteAsync(req, ct);
                await s.ReadExactlyAsync(buf.AsMemory(0, 4), ct);
                if (buf[1] != 0) throw new IOException(Lf($"SOCKS: ошибка {buf[1]}"));
                int skip = buf[3] switch { 1 => 4, 4 => 16, 3 => -1, _ => throw new IOException(L("SOCKS: неизвестный адрес")) };
                if (skip < 0) { await s.ReadExactlyAsync(buf.AsMemory(0, 1), ct); skip = buf[0]; }
                await s.ReadExactlyAsync(buf.AsMemory(0, skip + 2), ct);       // адрес + порт ответа
                return s;
            }
            catch { sock.Dispose(); throw; }
        },
    };

    static async Task<double?> SpeedMbps(int socksPort, string url, TimeSpan duration, Action<double> progress, CancellationToken ct)
    {
        int Streams = Math.Clamp(Config?.SpeedStreams ?? 4, 1, 16);   // как обычные спидтесты: несколько параллельных загрузок
        using var handler = SocksHandler(socksPort);
        handler.MaxConnectionsPerServer = Streams;
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.9.1");   // часть серверов молчит на запросы без UA
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        var sw = new Stopwatch();
        int started = 0;

        async Task One()
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return;
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                // отсчёт времени — с первого ответившего потока
                if (Interlocked.Increment(ref started) == 1) { lock (sw) sw.Start(); cts.CancelAfter(duration + TimeSpan.FromSeconds(1)); }
                var buf = new byte[128 * 1024];
                while (sw.Elapsed < duration)
                {
                    int n = await stream.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref total, n);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || Interlocked.Read(ref total) == 0)
            {
                try { File.AppendAllText(Path.Combine(AppPaths.Logs, "speedtest.log"), $"{DateTime.Now:HH:mm:ss} {url}: {ex.GetType().Name}: {ex.Message}\n"); } catch { }
            }
            catch (OperationCanceledException) { }
        }

        cts.CancelAfter(TimeSpan.FromSeconds(8));   // на ответ сервера — 8 сек
        var work = Task.WhenAll(Enumerable.Range(0, Streams).Select(_ => One()));
        while (!work.IsCompleted)
        {
            await Task.WhenAny(work, Task.Delay(400));
            if (sw.IsRunning && sw.Elapsed.TotalSeconds > 0.3)
                progress(Interlocked.Read(ref total) * 8 / sw.Elapsed.TotalSeconds / 1_000_000);
        }
        sw.Stop();
        if (ct.IsCancellationRequested || total == 0 || sw.Elapsed.TotalSeconds < 0.5) return null;
        return total * 8 / sw.Elapsed.TotalSeconds / 1_000_000;
    }

    /// <summary>Поднимает ядра в тестовом режиме (без TUN и входов), выполняет body и гасит их.</summary>
    async Task WithTestCoresAsync(Settings s, IReadOnlyList<ServerInfo> servers,
        Func<IReadOnlyDictionary<string, int>, int, string, Task> body)
    {
        Config = s;
        RuleLists.Compile();
        var (geo, _) = await GeoRuleSets.EnsureAsync(s, Profiles.Current);
        var xrayServers = servers.Where(x => x.NeedsXray).ToList();
        var ports = xrayServers.ToDictionary(x => x.Tag, _ => FreePort());
        int clash = FreePort();
        var secret = Guid.NewGuid().ToString("N");

        var sb = ConfigBuilder.SingBox(s, Profiles.Current, servers, ports, clash, secret, geo, "");
        sb["inbounds"] = new System.Text.Json.Nodes.JsonArray();   // никакого TUN
        var sbPath = Path.Combine(AppPaths.Runtime, "test-sing-box.json");
        var xrPath = Path.Combine(AppPaths.Runtime, "test-xray.json");
        File.WriteAllText(sbPath, sb.ToJsonString());
        if (xrayServers.Count > 0) File.WriteAllText(xrPath, ConfigBuilder.Xray(s, xrayServers, ports).ToJsonString());

        var testDir = Directory.CreateDirectory(Path.Combine(AppPaths.Runtime, "test")).FullName;
        Process? xr = null, sbp = null;
        try
        {
            if (xrayServers.Count > 0) xr = StartQuiet(AppPaths.XrayExe, $"run -c \"{xrPath}\"");
            sbp = StartQuiet(AppPaths.SingBoxExe, $"run -c \"{sbPath}\" -D \"{testDir}\" --disable-color");

            // ждём, пока поднимется Clash API
            for (int i = 0; i < 40 && !sbp.HasExited; i++)
            {
                try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, clash); break; }
                catch { await Task.Delay(100); }
            }
            if (sbp.HasExited) throw new InvalidOperationException(L("Тестовое ядро не запустилось."));
            await Task.Delay(400);
            await body(ports, clash, secret);
        }
        finally
        {
            foreach (var p in new[] { sbp, xr })
                try { if (p is { HasExited: false }) p.Kill(true); p?.Dispose(); } catch { }
        }
    }

    Process StartQuiet(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
        })!;
        _job.Add(p);
        return p;
    }

    static async Task<int?> Delay(int port, string secret, string tag, int timeoutMs)
    {
        var url = $"http://127.0.0.1:{port}/proxies/{tag}/delay?timeout={timeoutMs}" +
                  "&url=" + Uri.EscapeDataString(LatencyUrl);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", secret);
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs + 2000);
            using var r = await SlowHttp.SendAsync(req, cts.Token);
            var doc = await r.Content.ReadFromJsonAsync<JsonElement>();
            return doc.TryGetProperty("delay", out var d) && d.GetInt32() > 0 ? d.GetInt32() : null;
        }
        catch { return null; }
    }

    /// <summary>Снимок активных соединений ядра (для вкладки «Подключения»).</summary>
    public async Task<JsonElement?> GetConnectionsAsync()
    {
        if (!Running) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_clashPort}/connections");
        req.Headers.Authorization = new("Bearer", _secret);
        try
        {
            using var r = await Http.SendAsync(req);
            return await r.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return null; }
    }

    static readonly HttpClient SlowHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Какой сервер сейчас реально выбран в «Авто».</summary>
    public async Task<string?> GetAutoCurrentAsync(string autoTag)
    {
        if (!Running) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_clashPort}/proxies/{autoTag}");
        req.Headers.Authorization = new("Bearer", _secret);
        try
        {
            using var r = await Http.SendAsync(req);
            var doc = await r.Content.ReadFromJsonAsync<JsonElement>();
            return doc.TryGetProperty("now", out var now) ? now.GetString() : null;
        }
        catch { return null; }
    }

    Process Launch(string exe, string args, string name)
    {
        var log = new StreamWriter(Path.Combine(AppPaths.Logs, name + ".log"), append: false) { AutoFlush = true };
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            },
            EnableRaisingEvents = true,
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.Exited += (_, _) =>
        {
            lock (log) log.Dispose();
            if (!_stopping) UnexpectedExit?.Invoke(name);
        };
        p.Start();
        _job.Add(p);   // если TunPilot упадёт, Windows сама убьёт ядра
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static string TailLog(string name)
    {
        Thread.Sleep(200);
        try
        {
            var lines = File.ReadAllLines(Path.Combine(AppPaths.Logs, name + ".log"));
            return string.Join("\n", lines.TakeLast(15));
        }
        catch { return L("(лог недоступен)"); }
    }

    static async Task<(int, string)> RunToEnd(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await outTask + await errTask).Trim());
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose() { Stop(); _job.Dispose(); }
}

/// <summary>
/// Job object с KILL_ON_JOB_CLOSE: дочерние процессы умирают вместе с нами (только Windows;
/// на других ОС — заглушка, там ядра останавливает сама программа при выходе).
/// </summary>
sealed class ChildJob : IDisposable
{
    readonly IntPtr _handle;

    public ChildJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        _handle = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(_handle, 9 /* ExtendedLimitInformation */, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Add(Process p) { if (_handle == IntPtr.Zero) return; try { AssignProcessToJobObject(_handle, p.Handle); } catch { } }
    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
}
