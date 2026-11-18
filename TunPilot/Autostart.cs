using System.Diagnostics;

namespace TunPilot;

/// <summary>
/// Автозапуск через Планировщик задач с наивысшими правами. Ключ реестра Run не подходит:
/// программы, требующие администратора, Windows оттуда молча не запускает.
/// </summary>
public static class Autostart
{
    const string TaskName = "TunPilot";
    static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\" /XML").Code == 0;

    /// <summary>Есть задача, но указывает на старый путь (программу перенесли)?</summary>
    public static bool PointsElsewhere()
    {
        var (code, xml) = Run($"/Query /TN \"{TaskName}\" /XML");
        return code == 0 && (!xml.Contains(ExePath, StringComparison.OrdinalIgnoreCase) || !xml.Contains("--tray"));
    }

    public static string? Enable()
    {
        // Задержка 15 сек — чтобы сеть успела подняться до подключения.
        var (code, output) = Run(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /F");
        return code == 0 ? null : output;
    }

    public static void Disable() => Run($"/Delete /TN \"{TaskName}\" /F");

    static (int Code, string Output) Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}
