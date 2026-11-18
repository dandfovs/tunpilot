using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TunPilotSetup
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Contains("--uninstall"))
            {
                var self = Application.ExecutablePath;
                int di = Array.IndexOf(args, "--dir");
                var dir = di >= 0 && di + 1 < args.Length ? args[di + 1] : Path.GetDirectoryName(self);

                // Uninstall.exe лежит в папке программы и не может удалить сам себя —
                // копируем себя во временную папку и продолжаем оттуда.
                if (!args.Contains("--temp"))
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "TunPilot-uninstall.exe");
                    File.Copy(self, tmp, true);
                    Process.Start(new ProcessStartInfo(tmp, $"--uninstall --temp --dir \"{dir}\"") { UseShellExecute = false });
                    return;
                }

                Application.Run(new SetupForm(uninstall: true, uninstallDir: dir));
                // временная копия удалит себя через пару секунд после выхода
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
                        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                }
                catch { }
                return;
            }

            Application.Run(new SetupForm(uninstall: false, uninstallDir: null));
        }
    }
}
