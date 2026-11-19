using static TunPilotSetup.Lang;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace TunPilotSetup
{
    /// <summary>Установка, обновление и удаление TunPilot: файлы, ярлыки, автозапуск, запись в «Программы и компоненты».</summary>
    static class Installer
    {
        public const string AppName = "TunPilot";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TunPilot";
        const string TaskName = "TunPilot";           // как в Autostart.cs программы
        public const string AppId = "TunPilot.VpnClient"; // как в AppIdentity.cs программы

        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        static string ProgramFiles =>
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TunPilot.lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TunPilot.lnk");

        static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        /// <summary>Папка уже установленной версии (из «Программ и компонентов») или null.</summary>
        public static string ExistingDir
        {
            get
            {
                using (var k = Hklm.OpenSubKey(UninstallKey))
                {
                    var dir = k?.GetValue("InstallLocation") as string;
                    return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "TunPilot.exe")) ? dir : null;
                }
            }
        }

        public static string ExistingVersion
        {
            get { using (var k = Hklm.OpenSubKey(UninstallKey)) return k?.GetValue("DisplayVersion") as string; }
        }

        public static string DefaultDir => ExistingDir ?? Path.Combine(ProgramFiles, AppName);

        /// <summary>Под какой процессор собрана программа внутри установщика: "x64" или "arm64".</summary>
        public static string PayloadArch
        {
            get
            {
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload-arch.txt"))
                        if (s != null) using (var r = new StreamReader(s)) return r.ReadToEnd().Trim().ToLowerInvariant();
                }
                catch { }
                return "x64";
            }
        }

        /// <summary>
        /// Компьютер на ARM64 (Snapdragon и т. п.). Через IsWow64Process2 — он отвечает правду даже из эмуляции x64
        /// (обычные проверки в эмуляции говорят «x64»).
        /// </summary>
        public static bool OsIsArm64
        {
            get
            {
                try
                {
                    ushort process, native;
                    if (IsWow64Process2(Process.GetCurrentProcess().Handle, out process, out native)) return native == 0xAA64;   // IMAGE_FILE_MACHINE_ARM64
                }
                catch { }   // Windows старше 10 1709 — ARM64 там не бывает
                return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        /// <summary>Сравнение с установленной версией: &gt;0 — этот установщик новее, 0 — та же, &lt;0 — старее; null — не установлено.</summary>
        public static int? CompareWithInstalled()
        {
            var installed = ExistingVersion;
            if (ExistingDir == null || string.IsNullOrEmpty(installed)) return null;
            System.Version a, b;
            if (!System.Version.TryParse(Version, out a) || !System.Version.TryParse(installed, out b)) return null;
            return a.CompareTo(b);
        }

        public static bool AppRunning => Process.GetProcessesByName("TunPilot").Length > 0;

        /// <summary>Закрыть работающий TunPilot (ядра закроются вместе с ним).</summary>
        public static void StopApp()
        {
            foreach (var p in Process.GetProcessesByName("TunPilot"))
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            Thread.Sleep(500);
            // на случай, если ядра пережили программу
            foreach (var name in new[] { "sing-box", "xray" })
                foreach (var p in Process.GetProcessesByName(name))
                    try
                    {
                        var path = p.MainModule?.FileName ?? "";
                        if (path.IndexOf("TunPilot", StringComparison.OrdinalIgnoreCase) >= 0) { p.Kill(); p.WaitForExit(3000); }
                    }
                    catch { }
        }

        // ───────────────────────── установка ─────────────────────────

        public static void Install(string dir, bool desktop, bool autostart, Action<int, string> progress)
        {
            progress(2, T("Подготовка…", "Preparing…"));
            if (AppRunning) StopApp();
            Directory.CreateDirectory(dir);

            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (payload == null) throw new InvalidOperationException(T("Установщик собран без файлов программы (payload.zip).", "The installer was built without the program files (payload.zip)."));
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                    var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                    long total = Math.Max(1, files.Sum(e => e.Length)), done = 0;
                    foreach (var e in files)
                    {
                        var target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // защита от «../» в архиве
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        CopyWithRetry(e, target);
                        done += e.Length;
                        progress(5 + (int)(80 * done / total), T("Копирование: ", "Copying: ") + e.FullName.Replace('/', '\\'));
                    }
                }
            }

            // маленький Uninstall.exe приходит в архиве; копия себя — только если его там почему-то нет
            progress(87, T("Программа удаления…", "Uninstaller…"));
            var self = Assembly.GetExecutingAssembly().Location;
            var uninstaller = Path.Combine(dir, "Uninstall.exe");
            if (!File.Exists(uninstaller) && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            progress(90, T("Ярлыки…", "Shortcuts…"));
            var exe = Path.Combine(dir, "TunPilot.exe");
            Shortcut.Create(StartMenuShortcut, exe, dir, T("TunPilot — VPN-клиент", "TunPilot — VPN client"), AppId);
            if (desktop) Shortcut.Create(DesktopShortcut, exe, dir, T("TunPilot — VPN-клиент", "TunPilot — VPN client"), AppId);
            else TryDelete(DesktopShortcut);

            progress(94, T("Автозапуск…", "Autostart…"));
            if (autostart)
                Schtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /F");
            else
                Schtasks($"/Delete /TN \"{TaskName}\" /F");

            progress(97, T("Регистрация в «Программах и компонентах»…", "Registering in Installed apps…"));
            using (var k = Hklm.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            progress(100, T("Готово", "Done"));
        }

        static void CopyWithRetry(ZipArchiveEntry e, string target)
        {
            for (int i = 0; ; i++)
            {
                try { e.ExtractToFile(target, true); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(400); }   // файл ещё держит закрывающийся процесс
            }
        }

        public static bool AutostartEnabled => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

        static int Schtasks(string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                       { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.StandardOutput.ReadToEnd(); p.WaitForExit(15000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static long DirSize(string dir)
        {
            try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        // ───────────────────────── удаление ─────────────────────────

        public static void Uninstall(string dir, bool removeData, Action<int, string> progress)
        {
            progress(5, T("Закрываю TunPilot…", "Closing TunPilot…"));
            StopApp();

            // снять блокировку kill-switch, если она стоит (иначе после удаления не будет интернета)
            var exe = Path.Combine(dir, "TunPilot.exe");
            if (File.Exists(exe))
            {
                progress(20, T("Снимаю блокировку kill-switch…", "Removing the kill-switch block…"));
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo(exe, "--killswitch-off --quiet") { UseShellExecute = false, CreateNoWindow = true }))
                        p.WaitForExit(15000);
                }
                catch { }
            }

            progress(35, T("Удаляю автозапуск и ярлыки…", "Removing autostart and shortcuts…"));
            Schtasks($"/Delete /TN \"{TaskName}\" /F");
            TryDelete(StartMenuShortcut);
            TryDelete(DesktopShortcut);

            progress(50, T("Удаляю файлы программы…", "Deleting program files…"));
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch { Thread.Sleep(500); }
            }

            if (removeData)
            {
                progress(80, T("Удаляю настройки и подписки…", "Deleting settings and subscriptions…"));
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunPilot");
                try { if (Directory.Exists(data)) Directory.Delete(data, true); } catch { }
            }

            progress(95, T("Удаляю запись из «Программ и компонентов»…", "Removing from Installed apps…"));
            try { Hklm.DeleteSubKeyTree(UninstallKey, false); } catch { }
            progress(100, T("Готово", "Done"));
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
