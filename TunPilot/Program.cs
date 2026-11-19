namespace TunPilot;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // язык интерфейса — до создания любых окон (TUNPILOT_LANG=en/ru — для снимков при разработке)
        // язык интерфейса — до создания любых окон («--lang en» — для снимков при разработке)
        int di = Array.IndexOf(args, "--data");
        if (di >= 0 && di + 1 < args.Length) AppPaths.DataOverride = args[di + 1];
        int li = Array.IndexOf(args, "--lang");
        try { Loc.Apply(li >= 0 && li + 1 < args.Length ? args[li + 1] : Settings.Load().Language); } catch { }
        if (args.Length > 0 && args[0].StartsWith("--test") || args.Contains("--selftest")) { DevTool(args); return; }

        // Аварийное снятие блокировки kill-switch (например, если программу удалили или она не запускается).
        if (args.Contains("--killswitch-off"))
        {
            bool quiet = args.Contains("--quiet");   // из удаления программы — без окон
            try { KillSwitch.Disable(); if (!quiet) MessageBox.Show(L("Блокировка kill-switch снята."), "TunPilot"); }
            catch (Exception ex) { if (!quiet) MessageBox.Show(L("Не удалось снять блокировку:\n") + ex.Message, "TunPilot"); }
            return;
        }

        using var mutex = new Mutex(false, "TunPilot.SingleInstance");
        // После восстановления копии новый процесс ждёт, пока старый завершится.
        bool owned;
        try { owned = mutex.WaitOne(args.Contains("--restarted") ? 20000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Уже запущен (например, после автозапуска сидит в трее) — просим открыть его окно.
            if (!args.Contains("--tray") && !args.Contains("--screenshot"))
            {
                AllowSetForegroundWindow(-1);   // разрешаем первому процессу вывести окно на передний план
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); }
                catch { MessageBox.Show(L("TunPilot уже запущен — иконка в трее."), "TunPilot"); }
            }
            return;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();

        // Необработанные ошибки: пишем подробности в журнал и продолжаем работу (VPN не роняем).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            var path = LogError(e.Exception);
            if (Interlocked.Exchange(ref _errorShown, 1) == 1) return;   // не сыпать окнами, если ошибка повторяется
            MessageBox.Show(Lf($"Внутренняя ошибка: {e.Exception.Message}\n\nПрограмма продолжает работать. Подробности записаны в\n{path}"),
                "TunPilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _errorShown = 0;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogError(ex); };

        if (args.Length > 1 && args[0] == "--screenshot")
        {
            // Для разработки: отрисовать главное окно в PNG без подключения.
            var app = new TrayApp(startHidden: true);
            var f = new MainForm(app) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
            f.Show();
            if (args.Contains("--chart")) f.PreviewChart();
            if (args.Contains("--demo")) f.PreviewDemo();
            if (args.Contains("--connected")) f.PreviewConnected();
            if (args.Contains("--session")) f.PreviewSession();
            if (args.Contains("--settab")) f.ShowSettingsTab(args.SkipWhile(a => a != "--settab").Skip(1).Select(int.Parse).FirstOrDefault());
            if (args.SkipWhile(a => a != "--nav").Skip(1).Select(int.Parse).FirstOrDefault() is var nav and > 0) f.ShowPageIndex(nav);
            int page = args.SkipWhile(a => a != "--page").Skip(1).Select(int.Parse).FirstOrDefault();
            Form? extra = args.Contains("--settings") ? new SettingsForm(app) : args.Contains("--groups") ? new GroupsForm(app)
                        : args.Contains("--stats") ? new StatsForm(app) : args.Contains("--picker") ? f.PreviewPicker() : args.Contains("--lists") ? new ListEditorForm() : null;
            Form shot = f;
            if (extra != null) { extra.StartPosition = FormStartPosition.Manual; extra.Location = new Point(-4000, -4000); extra.Show(); shot = extra; if (extra is SettingsForm sf) sf.SelectPage(page); }
            var until = DateTime.Now.AddSeconds(2);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(20); }
            try
            {
                using var bmp = new Bitmap(shot.Width, shot.Height);
                shot.DrawToBitmap(bmp, new Rectangle(Point.Empty, shot.Size));
                bmp.Save(args[1]);
            }
            catch (Exception ex) { File.WriteAllText(args[1] + ".err.txt", ex.ToString()); }
            Environment.Exit(0);
        }

        AppIdentity.Apply();   // ID приложения + ярлык в «Пуске»: иконка в шапке уведомлений Windows
        var tray = new TrayApp(autostart: args.Contains("--tray"));
        // Сигнал от второго запуска (ярлык/exe) — показать окно.
        new Thread(() => { while (showEvent.WaitOne()) tray.ShowWindowFromOutside(); }) { IsBackground = true }.Start();
        Application.Run(tray);
    }

    const string ShowEventName = "TunPilot.ShowWindow";
    static int _errorShown;

    /// <summary>Дописать ошибку в Logs\errors.log (журнал не растёт бесконечно — режем до 512 КБ).</summary>
    internal static string LogError(Exception ex)
    {
        var path = Path.Combine(AppPaths.Logs, "errors.log");
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n");
        }
        catch { }
        return path;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Режимы для разработки (без UI и без TUN): самопроверка конфигов, тесты задержки/скорости.</summary>
    static void DevTool(string[] args)
    {
        var report = Path.Combine(AppPaths.Data, "selftest.txt");
        try
        {
            var s = Settings.Load();
            var groups = GroupStore.Load(s);
            Profiles.Init(s);
            CoreManager.Config = s;
            if (args.Contains("--refresh"))
                foreach (var g in groups.Groups.Where(g => g.IsSubscription))
                {
                    var (body, info) = Subscription.FetchAsync(g.Url).GetAwaiter().GetResult();
                    g.Body = body; g.Info = info; g.UpdatedUtc = DateTime.UtcNow;
                }
            groups.Save();
            var servers = groups.Groups.SelectMany(g => g.ParseServers()).ToList();
            using var core = new CoreManager();

            if (args[0] == "--testcoreupdate")
            {
                // проверка версий + скачивание/проверка/распаковка обоих ядер во временную папку (без замены файлов)
                var outp = new List<string>();
                var infos = CoreUpdater.CheckAsync().GetAwaiter().GetResult();
                foreach (var i in infos)
                {
                    outp.Add($"{i.Core.Name}: current={i.Current} latest={i.Latest} update={i.HasUpdate} digest={(i.DigestUrl != null)} asset={Path.GetFileName(i.AssetUrl)}");
                    var (root, temp) = CoreUpdater.DownloadAsync(i, s => outp.Add("  " + s)).GetAwaiter().GetResult();
                    outp.Add($"  ready: {string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName))}");
                    CoreUpdater.Cleanup(temp);
                    outp.Add($"  cleaned: {!Directory.Exists(temp)}");
                }
                File.WriteAllLines(Path.Combine(AppPaths.Data, "testcoreupdate.txt"), outp);
                return;
            }
            if (args[0] == "--testloc")
            {
                // все ли размеченные строки переведены (ключи — из инструмента извлечения)
                var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
                var missing = keys.Keys.Where(k => !Strings.En.ContainsKey(k)).ToList();
                var unused = Strings.En.Keys.Where(k => !keys.ContainsKey(k)).ToList();
                File.WriteAllLines(Path.Combine(AppPaths.Data, "testloc.txt"),
                    new[] { $"dict={Strings.En.Count} used={keys.Count} missing={missing.Count} unused={unused.Count}" }
                    .Concat(missing.Select(m => "MISSING: " + m.Replace("\n", "\\n")))
                    .Concat(unused.Select(m => "UNUSED: " + m.Replace("\n", "\\n"))));
                return;
            }            if (args[0] == "--testidentity")
            {
                AppIdentity.Apply();
                File.WriteAllText(Path.Combine(AppPaths.Data, "testidentity.txt"), AppIdentity.Describe());
                return;
            }
            if (args[0] == "--testimport")
            {
                var outp = new List<string>();
                foreach (var file in args.Skip(1))
                    try { outp.Add(Path.GetFileName(file) + ": " + RouteImport.DescribeAsync(File.ReadAllText(file)).GetAwaiter().GetResult()); }
                    catch (Exception ex) { outp.Add(Path.GetFileName(file) + ": ERROR " + ex.Message.Split('\n')[0]); }
                File.WriteAllLines(Path.Combine(AppPaths.Data, "testimport.txt"), outp);
                return;
            }
            if (args[0] == "--testdelay")
            {
                var res = new System.Collections.Concurrent.ConcurrentDictionary<string, int?>();
                core.TestOfflineAsync(s, servers, servers.Select(x => x.Tag), _ => { }, (t, d) => res[t] = d).GetAwaiter().GetResult();
                File.WriteAllLines(Path.Combine(AppPaths.Data, "testdelay.txt"),
                    res.OrderBy(k => k.Key).Select(k => $"{k.Key} {(k.Value?.ToString() ?? "timeout")}"));
                return;
            }
            if (args[0] == "--testspeed")
            {
                var tags = args.Skip(1).Where(a => !a.StartsWith("--")).DefaultIfEmpty(servers.FirstOrDefault()?.Tag ?? "").ToList();
                var res = new System.Collections.Concurrent.ConcurrentDictionary<string, double?>();
                core.SpeedTestAsync(s, servers, tags, (_, _) => { }, (t, m) => res[t] = m, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllLines(Path.Combine(AppPaths.Data, "testspeed.txt"),
                    res.Select(k => $"{k.Key} {(k.Value is { } v ? v.ToString("0.0") + " Mbit/s" : "fail")}"));
                return;
            }

            var err = core.PrepareAsync(s, servers, groups.Groups.FirstOrDefault()?.AutoTag ?? "").GetAwaiter().GetResult();
            File.WriteAllText(report,
                $"groups={groups.Groups.Count} [{string.Join(", ", groups.Groups.Select(g => $"{g.Id}:{g.Name}:{g.Type}:{g.ParseServers().Count}"))}]\n" +
                $"servers={servers.Count} xray={servers.Count(x => x.NeedsXray)}\n" +
                $"profiles=[{string.Join(", ", Profiles.All().Select(p => p.Name))}] active={Profiles.Current.Name}\n" +
                $"{BypassRoutes.DescribeGateway()}\nbypass_cidrs={RuleLists.ReadBypassCidrs().Count}\n" +
                $"geo_missing={string.Join(",", core.GeoMissing)}\n" +
                $"check={(err ?? "OK")}\n" +
                $"killswitch: {KillSwitch.Describe()}\n" +
                $"toolstrip_tooltip={typeof(ToolStrip).GetProperty("ToolTip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.PropertyType.Name ?? "MISSING"}\n");
        }
        catch (Exception ex) { File.WriteAllText(report, "EXCEPTION: " + ex); }
    }
}
