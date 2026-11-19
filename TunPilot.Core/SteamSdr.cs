using System.Net;
using System.Text.Json;

namespace TunPilot;

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
