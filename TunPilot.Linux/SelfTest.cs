using System.Diagnostics;
using System.Net.Http;

namespace TunPilot.LinuxApp;

/// <summary>
/// Проверка без окна (для разработки): «TunPilot --selftest» — подключиться, проверить интернет через VPN,
/// включить kill-switch и убедиться, что трафик мимо TUN блокируется, затем всё выключить. Отчёт — в консоль.
/// </summary>
sealed class SelfTest : IPlatform
{
    readonly IKillSwitch _ks = new LinuxKillSwitch();
    readonly IBypassRoutes _bypass = new LinuxBypassRoutes();
    readonly IAutostart _autostart = new LinuxAutostart();
    int _fails;

    void IPlatform.Notify(string title, string text, NotifyKind kind) => Console.WriteLine($"  [notify {kind}] {title}: {text}");
    void IPlatform.ShowError(string text) => Console.WriteLine("  [error] " + text);
    void IPlatform.ShowInfo(string title, string text) => Console.WriteLine($"  [info] {title}: {text}");
    Task<bool> IPlatform.ConfirmAsync(string text) => Task.FromResult(true);
    IKillSwitch IPlatform.KillSwitch => _ks;
    IBypassRoutes IPlatform.Bypass => _bypass;
    IAutostart IPlatform.Autostart => _autostart;

    void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    static async Task<(bool Ok, string Text)> Get(string url, int seconds = 10)
    {
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
            var r = await h.GetAsync(url);
            return (r.IsSuccessStatusCode, ((int)r.StatusCode) + " " + (await r.Content.ReadAsStringAsync()).Trim().Replace('\n', ' '));
        }
        catch (Exception ex) { return (false, ex.GetBaseException().Message); }
    }

    /// <summary>Соединение мимо TUN (привязка к физическому интерфейсу, как у программы без VPN).</summary>
    static string Sh(string cmd)
    {
        var psi = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", cmd }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    public async Task<int> RunAsync()
    {
        var app = new AppController(this);
        Console.WriteLine($"servers={app.AllServers.Count} selected={app.SelectedDisplay}");
        await app.InitAsync();
        if (!app.Core.Running) await app.ConnectAsync();
        Check("connect", app.Core.Running, app.SelectedDisplay);
        await Task.Delay(3000);
        var tun = Sh("ip -br link show TunPilot");
        Check("TUN interface", tun.Contains("TunPilot"), tun);
        // первый запуск: ядро ещё скачивает наборы geoip/geosite — даём несколько попыток
        var (ok, text) = (false, "");
        for (int i = 0; i < 4 && !ok; i++) { if (i > 0) await Task.Delay(3000); (ok, text) = await Get("https://www.cloudflare.com/cdn-cgi/trace"); }
        var ip = text.Split(' ').FirstOrDefault(x => x.StartsWith("ip=")) ?? text;
        Check("internet via VPN", ok, ip);
        var dev = Sh("ip route show default | awk '{print $5; exit}'");

        app.SetKillSwitch(true);
        await Task.Delay(1000);
        Check("kill-switch on", app.KillSwitchActive && File.Exists(LinuxKillSwitch.StateFile));
        (ok, _) = await Get("https://www.cloudflare.com/cdn-cgi/trace");
        Check("internet via VPN with kill-switch", ok);
        var leak = Sh($"curl -s -m 5 --interface {dev} -o /dev/null -w '%{{http_code}}' https://1.1.1.1 || echo blocked");
        Check($"direct traffic via {dev} blocked", leak.Contains("blocked") || leak.StartsWith("000"), leak);

        await app.DisconnectAsync(userAction: false, keepKillSwitch: true);
        (ok, text) = await Get("https://www.cloudflare.com/cdn-cgi/trace", 5);
        Check("disconnected + kill-switch: internet blocked", !ok, text.Length > 60 ? text[..60] : text);

        app.DisableKillSwitch();
        await Task.Delay(500);
        Check("kill-switch off", !File.Exists(LinuxKillSwitch.StateFile));
        (ok, _) = await Get("https://www.cloudflare.com/cdn-cgi/trace");
        Check("internet without VPN", ok);
        await app.ShutdownAsync();
        Console.WriteLine(_fails == 0 ? "ALL OK" : $"{_fails} FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
