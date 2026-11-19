using Avalonia;

namespace TunPilot.LinuxApp;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int di = Array.IndexOf(args, "--data");
        if (di >= 0 && di + 1 < args.Length) AppPaths.DataOverride = args[di + 1];
        int li = Array.IndexOf(args, "--lang");
        try { Loc.Apply(li >= 0 && li + 1 < args.Length ? args[li + 1] : Settings.Load().Language); } catch { }

        if (args.Contains("--selftest")) return new SelfTest().RunAsync().GetAwaiter().GetResult();

        // одна копия программы: вторая просит первую показать окно и выходит
        if (!SingleInstance.TryAcquire(args.Contains("--tray")))
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
