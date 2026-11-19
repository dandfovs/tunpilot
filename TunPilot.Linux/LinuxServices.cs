using System.Diagnostics;

namespace TunPilot.LinuxApp;

/// <summary>
/// Запуск привилегированного помощника tunpilot-helper (nftables, ip rule).
/// От root — напрямую; иначе через pkexec (политика polkit разрешает это активному пользователю без пароля).
/// </summary>
static class Helper
{
    /// <summary>Установленный помощник (/opt/tunpilot) или лежащий рядом с программой (сборка для разработки).</summary>
    static string Path_ => File.Exists("/opt/tunpilot/tunpilot-helper") ? "/opt/tunpilot/tunpilot-helper"
        : Path.Combine(AppPaths.Base, "tunpilot-helper");

    static bool IsRoot => Environment.UserName == "root";

    public static (int Code, string Output) Run(params string[] args)
    {
        var psi = IsRoot ? new ProcessStartInfo(Path_) : new ProcessStartInfo("pkexec", "");
        if (!IsRoot) psi.ArgumentList.Add(Path_);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("tunpilot-helper did not start");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        // pkexec может ждать окно пароля (нет политики/агента) — не висим бесконечно
        if (!p.WaitForExit(60000))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException(L("Нет ответа от tunpilot-helper (pkexec ждёт подтверждения администратора)."));
        }
        var outText = outTask.Result; var errText = errTask.Result;
        if (p.ExitCode != 0) throw new InvalidOperationException((errText.Trim().Length > 0 ? errText : outText).Trim());
        return (p.ExitCode, outText.Trim());
    }

    /// <summary>Список CIDR во временный файл (помощник читает только из файла — без аргументов-строк).</summary>
    public static string WriteList(IEnumerable<string> cidrs)
    {
        var f = Path.Combine(Path.GetTempPath(), $"tunpilot-{Environment.UserName}-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(f, cidrs.Where(c => c.Contains('.')).Distinct());
        return f;
    }
}

/// <summary>Kill-switch на nftables: наружу только через TUN, ядра (по метке 0x2023), локальная сеть и исключения.</summary>
sealed class LinuxKillSwitch : IKillSwitch
{
    /// <summary>Помощник хранит список исключений включённого kill-switch здесь (его же читает служба при загрузке).
    /// Проверка без прав root и без pkexec.</summary>
    public const string StateFile = "/var/lib/tunpilot/killswitch.list";

    public bool IsActive => File.Exists(StateFile);

    public void Enable(IEnumerable<string> bypassCidrs)
    {
        var f = Helper.WriteList(bypassCidrs);
        try { Helper.Run("ks-enable", f); } finally { try { File.Delete(f); } catch { } }
    }

    public void UpdateBypass(IEnumerable<string> bypassCidrs) => Enable(bypassCidrs);   // таблица пересоздаётся атомарно
    public void Disable() => Helper.Run("ks-disable");
    public string RecoveryHint => L("\n\nСнять вручную: sudo /opt/tunpilot/tunpilot-helper ks-disable");
}

/// <summary>Маршруты «мимо TUN»: правила ip rule с приоритетом выше, чем у sing-box (меняются на лету).</summary>
sealed class LinuxBypassRoutes : IBypassRoutes
{
    public async Task<string> ApplyAsync(IEnumerable<string> cidrs)
    {
        var list = cidrs.ToList();
        if (list.Count == 0) { await ClearAsync(); return ""; }
        var f = Helper.WriteList(list);
        try
        {
            var (_, output) = await Task.Run(() => Helper.Run("bypass-apply", f));
            int n = int.TryParse(output, out var x) ? x : list.Count;
            return Loc.En ? $"{n} {(n == 1 ? "subnet" : "subnets")} bypassing VPN" : $"{n} {Plural(n)} мимо VPN";
        }
        finally { try { File.Delete(f); } catch { } }
    }

    static string Plural(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        return m10 == 1 && m100 != 11 ? "подсеть" /*noloc*/ : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "подсети" /*noloc*/ : "подсетей" /*noloc*/;
    }

    public Task ClearAsync() => Task.Run(() => { try { Helper.Run("bypass-clear"); } catch { } });
    public Task CleanupLeftoversAsync() => ClearAsync();
    public void ResetGateway() { }   // шлюз берёт сама таблица main
}

/// <summary>Автозапуск через XDG (~/.config/autostart/tunpilot.desktop) — работает в GNOME, KDE, Xfce и др.</summary>
sealed class LinuxAutostart : IAutostart
{
    static string File_ => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "tunpilot.desktop");

    static string Exe => Environment.ProcessPath ?? "/opt/tunpilot/TunPilot";

    public bool IsEnabled() => File.Exists(File_);

    public string? Enable()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_,
                "[Desktop Entry]\nType=Application\nName=TunPilot\nComment=VPN client\n" +
                $"Exec=\"{Exe}\" --tray\nIcon=tunpilot\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Disable() { try { File.Delete(File_); } catch { } }

    public bool PointsElsewhere()
    {
        try { return IsEnabled() && !File.ReadAllText(File_).Contains(Exe); } catch { return false; }
    }
}
