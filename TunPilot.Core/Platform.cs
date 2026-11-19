namespace TunPilot;

/// <summary>Мелочи, которые отличаются между ОС (имена исполняемых файлов и т. п.).</summary>
public static class Platform
{
    /// <summary>Имя исполняемого файла: «xray» → «xray.exe» на Windows, «xray» на Linux/macOS.</summary>
    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Имя процесса самой программы (TunPilot.exe на Windows) — для правила «напрямую» в sing-box.</summary>
    public static string AppProcessName => Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } n ? n : Exe("TunPilot");
}
