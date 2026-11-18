using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TunPilot;

/// <summary>
/// Маршруты «мимо TUN»: точечные маршруты через физический шлюз. Они точнее общих
/// маршрутов TUN (0.0.0.0/1 и 128.0.0.0/1), поэтому Windows выбирает их.
/// Добавляются и снимаются на лету, перезапуск ядра на них не влияет.
/// </summary>
public static class BypassRoutes
{
    record Gateway(string Address, int IfIndex);

    static string StatePath => Path.Combine(AppPaths.Runtime, "routes.json");
    static readonly SemaphoreSlim Lock = new(1, 1);
    static HashSet<string> _active = [];
    static Gateway? _gw;

    /// <summary>Приводит таблицу маршрутов к нужному набору CIDR'ов (добавляет новые, снимает лишние).</summary>
    public static async Task<string> ApplyAsync(IEnumerable<string> cidrs)
    {
        await Lock.WaitAsync();
        try
        {
            _gw ??= FindPhysicalGateway() ?? throw new InvalidOperationException(L("Не найден основной шлюз сети."));
            var want = cidrs.ToHashSet();
            int added = 0, removed = 0, failed = 0;
            foreach (var c in _active.Except(want).ToList())
            {
                await Route("DELETE", c); _active.Remove(c); removed++;
            }
            foreach (var c in want.Except(_active).ToList())
            {
                if (await Route("ADD", c)) { _active.Add(c); added++; } else failed++;
            }
            SaveState();
            return (Loc.En
                ? $"{_active.Count} {(_active.Count == 1 ? "subnet" : "subnets")} bypassing VPN"
                : $"{_active.Count} {Plural(_active.Count, "подсеть", "подсети", "подсетей")} мимо VPN") +
                   (failed > 0 ? Lf($" (не удалось добавить: {failed})") : "");
        }
        finally { Lock.Release(); }
    }

    public static Task ClearAsync() => ApplyAsync([]);

    internal static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        return m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? few : many;
    }

    /// <summary>Для самопроверки: какой шлюз будет использован.</summary>
    public static string DescribeGateway() =>
        FindPhysicalGateway() is { } g ? $"gw={g.Address} if={g.IfIndex}" : "gw=NOT FOUND";

    /// <summary>При старте убираем маршруты, оставшиеся после аварийного завершения.</summary>
    public static async Task CleanupLeftoversAsync()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var st = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
            if (st != null)
            {
                _gw = new Gateway(st.Gateway, st.IfIndex);
                _active = st.Routes.ToHashSet();
                await ClearAsync();
            }
        }
        catch { }
        _gw = null;
        File.Delete(StatePath);
    }

    static async Task<bool> Route(string verb, string cidr)
    {
        var (net, mask) = ToNetMask(cidr);
        var args = verb == "ADD"
            ? $"ADD {net} MASK {mask} {_gw!.Address} METRIC 1 IF {_gw.IfIndex}"
            : $"DELETE {net} MASK {mask} {_gw!.Address}";
        var psi = new ProcessStartInfo("route.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 0 && !output.Contains("fail", StringComparison.OrdinalIgnoreCase)
                               && !output.Contains("ошибка" /*noloc*/, StringComparison.OrdinalIgnoreCase);
    }

    static (string, string) ToNetMask(string cidr)
    {
        var parts = cidr.Split('/');
        int len = int.Parse(parts[1]);
        uint mask = len == 0 ? 0 : uint.MaxValue << (32 - len);
        var ipBytes = IPAddress.Parse(parts[0]).GetAddressBytes();
        uint ip = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]) & mask;
        static string Fmt(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
        return (Fmt(ip), Fmt(mask));
    }

    static Gateway? FindPhysicalGateway()
    {
        // Лучший интерфейс к внешнему адресу — если TUN ещё не поднят, это и есть физический.
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !n.Name.Contains(ConfigBuilder.TunName, StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                        // У адаптеров без IPv4 GetIPv4Properties() бросает исключение
                        // («Требуемый протокол не настроен в данной системе»).
                        && n.Supports(NetworkInterfaceComponent.IPv4))
            .Select(n =>
            {
                try
                {
                    var props = n.GetIPProperties();
                    return new
                    {
                        Nic = n,
                        Gw = props.GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)),
                        Index = props.GetIPv4Properties()?.Index ?? -1,
                    };
                }
                catch (NetworkInformationException) { return null; }
            })
            .Where(x => x != null).Select(x => x!)
            .Where(x => x.Gw != null && x.Index >= 0)
            .ToList();

        // Надёжнее всего — маршрут по умолчанию 0.0.0.0/0 с наименьшей метрикой среди физических адаптеров
        // (маршрут самого TUN отсеивается: его интерфейса нет среди кандидатов).
        foreach (var dr in DefaultRoutes())
        {
            var match = candidates.FirstOrDefault(c => c.Nic.GetIPProperties().UnicastAddresses
                .Any(u => u.Address.ToString() == dr.IfAddr));
            if (match != null && !IsVirtualLan(match.Nic)) return new Gateway(dr.Gateway, match.Index);
        }

        if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()), out var best) == 0)
        {
            var b = candidates.FirstOrDefault(c => c.Index == best);
            if (b != null) return new Gateway(b.Gw!.ToString(), b.Index);
        }
        var any = candidates.FirstOrDefault(c => !IsVirtualLan(c.Nic)) ?? candidates.FirstOrDefault();
        return any == null ? null : new Gateway(any.Gw!.ToString(), any.Index);
    }

    /// <summary>Разбирает «route print -4 0.0.0.0»: строки вида «0.0.0.0 0.0.0.0 шлюз интерфейс метрика».</summary>
    /// <summary>Игровые/виртуальные локалки: у них бывает свой 0.0.0.0/0, но интернет идёт не через них.</summary>
    static bool IsVirtualLan(NetworkInterface n) =>
        new[] { "Radmin", "ZeroTier", "Hamachi", "VirtualBox", "Hyper-V", "VMware", "Tailscale", "WireGuard" }
            .Any(k => n.Description.Contains(k, StringComparison.OrdinalIgnoreCase)
                   || n.Name.Contains(k, StringComparison.OrdinalIgnoreCase));

    static List<(string Gateway, string IfAddr)> DefaultRoutes()
    {
        try
        {
            var psi = new ProcessStartInfo("route.exe", "print -4 0.0.0.0")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Split('\n')
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(f => f.Length == 5 && f[0] == "0.0.0.0" && f[1] == "0.0.0.0"
                            && IPAddress.TryParse(f[2], out _) && IPAddress.TryParse(f[3], out _)
                            && int.TryParse(f[4], out _))
                .OrderBy(f => int.Parse(f[4]))
                .Select(f => (f[2], f[3]))
                .ToList();
        }
        catch { return []; }
    }

    record State(string Gateway, int IfIndex, List<string> Routes);

    static void SaveState()
    {
        if (_active.Count == 0) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(_gw!.Address, _gw.IfIndex, _active.ToList())));
    }

    /// <summary>Сбросить кэш шлюза (например, после смены сети).</summary>
    public static void ResetGateway() { if (_active.Count == 0) _gw = null; }

    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out int bestIfIndex);
}

/// <summary>Адреса ретрансляторов Steam Datagram Relay (через них идёт игровой трафик Dota 2).</summary>
public static class SteamSdr
{
    static string CachePath => Path.Combine(AppPaths.Data, "steam-sdr.txt");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<List<string>> GetCidrsAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=570");
            using var doc = JsonDocument.Parse(json);
            var nets = new SortedSet<string>();
            foreach (var pop in doc.RootElement.GetProperty("pops").EnumerateObject())
            {
                if (!pop.Value.TryGetProperty("relays", out var relays)) continue;
                foreach (var r in relays.EnumerateArray())
                    if (r.TryGetProperty("ipv4", out var ip) && IPAddress.TryParse(ip.GetString(), out var a))
                    {
                        var b = a.GetAddressBytes();
                        nets.Add($"{b[0]}.{b[1]}.{b[2]}.0/24");   // ретрансляторы стоят пачками в подсетях Valve
                    }
            }
            if (nets.Count > 0) File.WriteAllLines(CachePath, nets);
            return nets.ToList();
        }
        catch
        {
            return File.Exists(CachePath) ? File.ReadAllLines(CachePath).ToList() : [];
        }
    }
}
