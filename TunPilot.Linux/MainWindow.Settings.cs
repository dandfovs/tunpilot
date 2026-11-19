using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace TunPilot.LinuxApp;

sealed partial class MainWindow
{
    Control BuildSettings()
    {
        var s = App.Settings;
        var tabs = new TabControl { Margin = new Thickness(10, 4) };
        static StackPanel Page() => new() { Spacing = 8, Margin = new Thickness(8, 12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        static CheckBox Chk(string text, bool value) => new() { Content = text, IsChecked = value };
        static Control Row(string label, Control c) { var g = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") }; var l = Ui.T(label); Grid.SetColumn(c, 1); g.Children.Add(l); g.Children.Add(c); return g; }

        // ── общие ──
        var autostart = Chk(L("Запускать вместе с системой"), _shellAutostart.IsEnabled());
        var autoTray = Chk(L("При запуске вместе с системой — в трей, без окна"), s.AutostartToTray);
        var startMin = Chk(L("При ручном запуске (ярлык) — сразу в трей, без окна"), s.StartMinimized);
        var remember = Chk(L("Запоминать подключение (подключаться при запуске, если VPN был включён)"), s.RememberState);
        var connectOnStart = Chk(L("Всегда подключаться при запуске"), s.ConnectOnStart);
        var kill = Chk(L("Kill-switch: блокировать интернет мимо VPN"), s.KillSwitch);
        var steam = Chk(L("Ретрансляторы Steam (Dota 2) мимо TUN"), s.SteamSdrBypass);
        var localProxy = Chk(L("Локальный прокси (HTTP + SOCKS5) для программ без TUN"), s.LocalProxyEnabled);
        var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = s.LocalProxyPort, Width = 140, FormatString = "0" };
        var general = Page();
        foreach (var c in new Control[] { autostart, autoTray, startMin, remember, connectOnStart, steam, kill }) general.Children.Add(c);
        general.Children.Add(Ui.Hint(L("Интернет только через VPN. Если программа или ядро упали, либо ПК перезагрузили с включённым VPN, ") +
                                     L("интернет остаётся заблокирован, пока вы не подключитесь снова. ")));
        general.Children.Add(localProxy);
        general.Children.Add(Row(L("Порт"), port));
        general.Children.Add(Ui.Hint(L("127.0.0.1:порт — для браузеров и программ, которым можно указать прокси. Маршрутизация та же, что и в TUN.")));
        tabs.Items.Add(new TabItem { Header = L("Общие"), Content = new ScrollViewer { Content = general } });

        // ── подписки ──
        var intProvider = new RadioButton { Content = L("Интервал от провайдера (заголовок подписки)"), GroupName = "int", IsChecked = s.SubIntervalMode != "custom" };
        var intCustom = new RadioButton { Content = L("Свой интервал, минут:"), GroupName = "int", IsChecked = s.SubIntervalMode == "custom" };
        var minutes = new NumericUpDown { Minimum = 30, Maximum = 10080, Value = Math.Clamp(s.SubIntervalMinutes, 30, 10080), Width = 140, FormatString = "0" };
        var showChanges = Chk(L("Показывать изменения после ручного обновления подписки"), s.ShowSubChanges);
        var subs = Page();
        subs.Children.Add(intProvider);
        subs.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { intCustom, minutes } });
        subs.Children.Add(Ui.Hint(L("Автообновление никогда не перезапускает подключение само: изменения применятся при следующем подключении.")));
        subs.Children.Add(showChanges);
        subs.Children.Add(Ui.Btn(L("Группы и подписки…"), ShowGroups));
        tabs.Items.Add(new TabItem { Header = L("Подписки"), Content = subs });

        // ── DNS ──
        var remoteDns = new TextBox { Text = s.RemoteDns, Width = 260 };
        var localDns = new TextBox { Text = s.LocalDns, Width = 260 };
        var dns = Page();
        dns.Children.Add(Row(L("Удалённый DNS (через VPN, DoH)"), remoteDns));
        dns.Children.Add(Row(L("DNS для прямых запросов"), localDns));
        dns.Children.Add(Ui.Hint(L("Удалённый — для сайтов через VPN (DNS-over-HTTPS через выбранный сервер). ") + L("Прямой — для российских сайтов и всего, что идёт напрямую.")));
        tabs.Items.Add(new TabItem { Header = "DNS", Content = dns });

        // ── тесты ──
        var latencyUrl = new TextBox { Text = s.LatencyUrl, Width = 520 };
        var speedUrls = new TextBox { Text = string.Join("\n", s.SpeedUrls), Width = 520, Height = 90, AcceptsReturn = true };
        var streams = new NumericUpDown { Minimum = 1, Maximum = 16, Value = Math.Clamp(s.SpeedStreams, 1, 16), Width = 140, FormatString = "0" };
        var seconds = new NumericUpDown { Minimum = 3, Maximum = 30, Value = Math.Clamp(s.SpeedSeconds, 3, 30), Width = 140, FormatString = "0" };
        var tests = Page();
        tests.Children.Add(Row(L("URL теста задержки"), latencyUrl));
        tests.Children.Add(Row(L("URL теста скорости"), speedUrls));
        tests.Children.Add(Ui.Hint(L("По одному на строку; если первый недоступен, берётся следующий. Нужен большой файл (≥ 100 МБ).")));
        tests.Children.Add(Row(L("Потоков"), streams));
        tests.Children.Add(Row(L("Длительность, сек"), seconds));
        tabs.Items.Add(new TabItem { Header = L("Тесты"), Content = tests });

        // ── логи ──
        var logSb = new ComboBox { ItemsSource = new[] { "trace", "debug", "info", "warn", "error" }, SelectedItem = s.LogLevelSingBox, Width = 160 };
        var logXr = new ComboBox { ItemsSource = new[] { "debug", "info", "warning", "error", "none" }, SelectedItem = s.LogLevelXray, Width = 160 };
        var logsPage = Page();
        logsPage.Children.Add(Row(L("Уровень логов sing-box"), logSb));
        logsPage.Children.Add(Row(L("Уровень логов Xray"), logXr));
        logsPage.Children.Add(Ui.Hint(L("Подробные уровни (debug/trace) быстро раздувают журнал — включайте только для поиска проблем.")));
        tabs.Items.Add(new TabItem { Header = L("Логи"), Content = logsPage });

        // ── интерфейс ──
        var theme = new ComboBox { ItemsSource = new[] { L("Тёмная"), L("Светлая"), L("Как в системе") }, SelectedIndex = s.Theme switch { "light" => 1, "system" => 2, _ => 0 }, Width = 200 };
        var language = new ComboBox { ItemsSource = new[] { L("Как в системе"), "Русский" /*noloc*/, "English" }, SelectedIndex = s.Language switch { "ru" => 1, "en" => 2, _ => 0 }, Width = 200 };
        var ui = Page();
        ui.Children.Add(Row(L("Тема"), theme));
        ui.Children.Add(Row(Loc.En ? "Language" : "Язык / Language" /*noloc*/, language));
        ui.Children.Add(Ui.Hint(L("Смена языка перезапускает программу (VPN-подключение на пару секунд прервётся).")));
        tabs.Items.Add(new TabItem { Header = L("Интерфейс"), Content = ui });

        // ── резервная копия ──
        var backup = Page();
        backup.Children.Add(Ui.Hint(L("В копию входят настройки, группы и подписки (со ссылками!), профили маршрутизации и статистика. ") +
                                    L("Храните файл так же бережно, как пароль: по ссылкам подписок можно пользоваться вашим VPN.")));
        backup.Children.Add(Ui.BtnAsync(L("  Создать резервную копию…").Trim(), MakeBackupAsync));
        backup.Children.Add(Ui.BtnAsync(L("  Восстановить из копии…").Trim(), RestoreBackupAsync));
        backup.Children.Add(Ui.Hint(L("После восстановления программа перезапустится.")));
        tabs.Items.Add(new TabItem { Header = L("Резервная копия"), Content = backup });

        // ── о программе ──
        tabs.Items.Add(new TabItem { Header = L("О программе"), Content = new ScrollViewer { Content = BuildAbout() } });

        // ── сохранить / отменить ──
        var saveBtn = Ui.BtnAsync(L("Сохранить"), async () =>
        {
            bool core = s.RemoteDns != remoteDns.Text?.Trim() || s.LocalDns != localDns.Text?.Trim()
                || s.LogLevelSingBox != (string?)logSb.SelectedItem || s.LogLevelXray != (string?)logXr.SelectedItem
                || s.LocalProxyEnabled != (localProxy.IsChecked == true) || (localProxy.IsChecked == true && s.LocalProxyPort != (int)(port.Value ?? 2080));
            var newTheme = theme.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" };
            var newLang = language.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" };
            bool restart = newTheme != s.Theme || newLang != s.Language;
            s.AutostartToTray = autoTray.IsChecked == true;
            s.StartMinimized = startMin.IsChecked == true;
            s.RememberState = remember.IsChecked == true;
            s.ConnectOnStart = connectOnStart.IsChecked == true;
            s.LocalProxyEnabled = localProxy.IsChecked == true;
            s.LocalProxyPort = (int)(port.Value ?? 2080);
            s.SubIntervalMode = intCustom.IsChecked == true ? "custom" : "provider";
            s.SubIntervalMinutes = (int)(minutes.Value ?? 360);
            s.ShowSubChanges = showChanges.IsChecked == true;
            s.RemoteDns = remoteDns.Text?.Trim() ?? s.RemoteDns;
            s.LocalDns = localDns.Text?.Trim() ?? s.LocalDns;
            s.LatencyUrl = latencyUrl.Text?.Trim() ?? s.LatencyUrl;
            s.SpeedUrls = (speedUrls.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            s.SpeedStreams = (int)(streams.Value ?? 4);
            s.SpeedSeconds = (int)(seconds.Value ?? 8);
            s.LogLevelSingBox = (string?)logSb.SelectedItem ?? "warn";
            s.LogLevelXray = (string?)logXr.SelectedItem ?? "warning";
            s.Theme = newTheme;
            s.Language = newLang;
            if ((autostart.IsChecked == true) != _shellAutostart.IsEnabled()) App.ToggleAutostart();
            if ((steam.IsChecked == true) != s.SteamSdrBypass) await App.SetSteamBypassAsync(steam.IsChecked == true);
            if ((kill.IsChecked == true) != s.KillSwitch) App.SetKillSwitch(kill.IsChecked == true);
            await App.SettingsChangedAsync(core);
            if (restart && await Dialogs.ConfirmAsync(L("Перезапустить программу сейчас, чтобы сменить язык или тему?")))
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
                await _shell.QuitAsync();
                return;
            }
            Toast(L("✓ Сохранено"), NotifyKind.Info);
        }, primary: true);
        var revert = Ui.Btn(L("Отменить"), () => ShowPage("settings"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12), Children = { saveBtn, revert } };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        return root;
    }

    IAutostart _shellAutostart => ((IPlatform)_shell).Autostart;

    Control BuildAbout()
    {
        var p = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (LinuxShell.AppIcon != null) head.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TunPilot/Assets/icon.png"))), Width = 64, Height = 64 });
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Ui.T("TunPilot", 24, FontWeight.Bold), Ui.T(Lf($"Версия {Updater.VersionText}"), color: Ui.Muted) } });
        p.Children.Add(head);
        p.Children.Add(Ui.Hint(L("VPN-клиент для Linux с TUN-режимом. Маршруты, исключения и серверы меняются без перезапуска TUN — подключение не рвётся, пока вы, например, играете.")));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        links.Children.Add(Ui.Btn(L("Страница на GitHub"), () => Open(Updater.PageUrl)));
        links.Children.Add(Ui.Btn(L("Сообщить о проблеме"), () => Open(Updater.PageUrl + "/issues")));
        p.Children.Add(links);

        var updStatus = Ui.T("", 13, color: Ui.Muted);
        var check = Ui.BtnAsync(L("Проверить обновления"), async () =>
        {
            updStatus.Text = L("Проверяю…");
            try
            {
                var latest = await Updater.LatestAsync();
                if (latest is not { } r) updStatus.Text = L("На GitHub пока нет опубликованных версий.");
                else if (Updater.IsNewer(r.Tag))
                {
                    updStatus.Text = Lf($"Доступна новая версия {r.Tag.TrimStart('v')}");
                    updStatus.Foreground = Ui.B(Ui.Accent);
                    if (await Dialogs.ConfirmAsync(Lf($"Доступна версия {r.Tag.TrimStart('v')} (у вас {Updater.VersionText}).\nОткрыть страницу загрузки?"))) Open(r.Url);
                }
                else updStatus.Text = Lf($"У вас последняя версия ({Updater.VersionText}).");
            }
            catch (Exception ex) { updStatus.Text = L("Не удалось проверить: ") + ex.Message; updStatus.Foreground = Ui.B(Ui.Danger); }
        });
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { check, updStatus } });

        var sb = Ui.T("sing-box …"); var xr = Ui.T("Xray …");
        var coreStatus = Ui.T("", 13, color: Ui.Muted, wrap: true);
        var coreBtn = Ui.BtnAsync(L("Обновить ядра"), async () =>
        {
            coreStatus.Text = L("Проверяю…");
            try
            {
                var infos = await CoreUpdater.CheckAsync();
                var upd = infos.Where(i => i.HasUpdate).ToList();
                if (upd.Count == 0) { coreStatus.Text = L("Ядра актуальны: ") + string.Join(", ", infos.Select(i => $"{i.Core.Name} {i.Current}")); return; }
                var list = string.Join("\n", upd.Select(i => $"• {i.Core.Name}: {i.Current} → {i.Latest}"));
                if (!await Dialogs.ConfirmAsync(L("Доступны новые версии ядер:\n") + list + "\n\n" +
                    L("Если VPN подключён, он переподключится (~5 сек). Если новая версия не примет вашу конфигурацию, вернётся прежняя.\nОбновить?"))) { coreStatus.Text = ""; return; }
                var result = await App.UpdateCoresAsync(upd, t => Avalonia.Threading.Dispatcher.UIThread.Post(() => coreStatus.Text = t));
                coreStatus.Text = result.Split('\n')[0];
                await LoadCoreVersions();
            }
            catch (Exception ex) { coreStatus.Text = L("Не удалось проверить: ") + ex.Message; }
        });
        async Task LoadCoreVersions()
        {
            var vs = await Updater.CoreVersionAsync(AppPaths.SingBoxExe);
            var vx = await Updater.CoreVersionAsync(AppPaths.XrayExe);
            sb.Text = $"sing-box {vs} — GPL-3.0 (SagerNet)";
            xr.Text = $"Xray {vx} — MPL-2.0 (XTLS)";
        }
        _ = LoadCoreVersions();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
        void Cell(Control c, int col, int row) { c.Margin = new Thickness(0, 3); Grid.SetColumn(c, col); Grid.SetRow(c, row); grid.Children.Add(c); }
        Cell(Ui.T(L("Лицензия"), color: Ui.Muted), 0, 0); Cell(Ui.T("GPL-3.0"), 1, 0);
        Cell(Ui.T(L("Ядра"), color: Ui.Muted), 0, 1); Cell(sb, 1, 1); Cell(xr, 1, 2);
        // в /opt ядра принадлежат root и имеют setcap — их обновляет пакет, а не программа
        bool coresWritable = IsWritable(Path.GetDirectoryName(AppPaths.SingBoxExe)!);
        Cell(coresWritable ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { coreBtn, coreStatus } }
                           : Ui.Hint(L("Ядра обновляются вместе с пакетом TunPilot (apt / dnf).")), 1, 3);
        Cell(Ui.T(L("Настройки и логи"), color: Ui.Muted), 0, 4);
        var dataLink = Ui.Btn(AppPaths.Data, () => Open(AppPaths.Data)); dataLink.Classes.Clear(); dataLink.Background = Brushes.Transparent; dataLink.Foreground = Ui.B(Ui.Direct);
        Cell(dataLink, 1, 4);
        p.Children.Add(grid);
        p.Children.Add(Ui.Hint(L("TunPilot — свободная программа с открытым исходным кодом. Её можно использовать, изучать, менять и распространять на условиях лицензии GPL-3.0.")));
        return p;
    }

    static bool IsWritable(string dir)
    {
        try { var f = Path.Combine(dir, ".w" + Environment.ProcessId); File.WriteAllText(f, ""); File.Delete(f); return true; } catch { return false; }
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false })?.Dispose(); } catch { }
    }

    async Task MakeBackupAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"TunPilot-backup-{DateTime.Now:yyyy-MM-dd}.zip", DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            App.Monitor.Stats.Save();
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var name in new[] { "settings.json", "groups.json", "stats.json" })
                if (File.Exists(Path.Combine(AppPaths.Data, name))) zip.CreateEntryFromFile(Path.Combine(AppPaths.Data, name), name);
            foreach (var dir in new[] { "groups", "profiles" })
            {
                var root = Path.Combine(AppPaths.Data, dir);
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(f, Path.GetRelativePath(AppPaths.Data, f).Replace('\\', '/'));
            }
            Toast(L("Резервная копия сохранена."), NotifyKind.Info);
        }
        catch (Exception ex) { await Dialogs.MessageAsync("TunPilot", L("Не удалось создать копию:\n") + ex.Message, error: true); }
    }

    async Task RestoreBackupAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (!await Dialogs.ConfirmAsync(L("Текущие настройки, группы и профили будут заменены данными из копии. Продолжить?"))) return;
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.GetEntry("settings.json") == null && zip.GetEntry("groups.json") == null) throw new InvalidDataException(L("Это не резервная копия TunPilot."));
                await App.DisconnectAsync(userAction: false);
                var root = Path.GetFullPath(AppPaths.Data) + Path.DirectorySeparatorChar;
                foreach (var e in zip.Entries.Where(e => e.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(AppPaths.Data, e.FullName));
                    if (!target.StartsWith(root, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, true);
                }
            }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            await _shell.QuitAsync();
        }
        catch (Exception ex) { await Dialogs.MessageAsync("TunPilot", L("Не удалось восстановить:\n") + ex.Message, error: true); }
    }
}
