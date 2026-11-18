using System.Diagnostics;
using System.IO.Compression;

namespace TunPilot;

/// <summary>Настройки по вкладкам (как «Основные настройки» в Throne, только нужное).</summary>
public sealed class SettingsForm : Form
{
    readonly TrayApp _app;
    readonly Settings _s;
    readonly ToolStrip _tabs = new();
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12) };
    readonly Dictionary<ToolStripButton, Control> _pages = new();

    // общие
    readonly CheckBox _startMin = Chk(L("При ручном запуске (ярлык) — сразу в трей, без окна"));
    readonly CheckBox _autoTray = Chk(L("При запуске вместе с Windows — в трей, без окна"));
    readonly CheckBox _autostart = Chk(L("Запускать вместе с Windows"));
    readonly CheckBox _remember = Chk(L("Запоминать подключение (подключаться при запуске, если VPN был включён)"));
    readonly CheckBox _connectOnStart = Chk(L("Всегда подключаться при запуске"));
    readonly CheckBox _killSwitch = Chk(L("Kill-switch: блокировать интернет мимо VPN"));
    readonly CheckBox _steam = Chk(L("Ретрансляторы Steam (Dota 2) мимо TUN"));
    readonly CheckBox _localProxy = Chk(L("Локальный прокси (HTTP + SOCKS5) для программ без TUN"));
    readonly NumericUpDown _localPort = Num(1024, 65535);
    // подписки
    readonly RadioButton _intProvider = new() { Text = L("Интервал от провайдера (заголовок подписки)"), AutoSize = true };
    readonly RadioButton _intCustom = new() { Text = L("Свой интервал, минут:"), AutoSize = true };
    readonly NumericUpDown _intMinutes = Num(30, 10080);
    readonly CheckBox _showChanges = Chk(L("Показывать изменения после ручного обновления подписки"));
    // DNS
    readonly ComboBox _remoteDns = Combo(true, "1.1.1.1", "8.8.8.8", "9.9.9.9", "1.0.0.1", "8.8.4.4");
    readonly ComboBox _localDns = Combo(true, "77.88.8.8", "8.8.8.8", "1.1.1.1", "94.140.14.14");
    // тесты
    readonly TextBox _latencyUrl = new() { Width = 520 };
    readonly TextBox _speedUrls = new() { Width = 520, Height = 90, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false };
    readonly NumericUpDown _streams = Num(1, 16);
    readonly NumericUpDown _seconds = Num(3, 30);
    // логи
    readonly ComboBox _logSb = Combo(false, "trace", "debug", "info", "warn", "error");
    readonly ComboBox _logXr = Combo(false, "debug", "info", "warning", "error", "none");
    // интерфейс
    readonly ComboBox _theme = Combo(false, L("Тёмная"), L("Светлая"), L("Как в системе"));
    // названия языков — каждое на своём языке, чтобы его можно было найти при любом текущем
    readonly ComboBox _language = Combo(false, L("Как в системе"), "Русский", "English");
    // клавиши
    readonly TextBox _hotkey = new() { Width = 220, ReadOnly = true };

    readonly bool _embedded;

    /// <param name="embedded">Встроенный раздел главного окна (а не отдельное окно).</param>
    public SettingsForm(TrayApp app, bool embedded = false)
    {
        _app = app;
        _embedded = embedded;
        _s = app.Settings;
        Text = L("TunPilot — настройки");
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 680);
        MinimumSize = new Size(760, 480);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg; ForeColor = Theme.Text; Theme.DarkTitle(this);

        _tabs.Renderer = new Theme.Renderer();
        _tabs.GripStyle = ToolStripGripStyle.Hidden;
        _tabs.LayoutStyle = ToolStripLayoutStyle.Flow;   // не влезают — переносятся на вторую строку, а не прячутся
        _tabs.BackColor = Theme.Panel;
        _tabs.Padding = new Padding(8, 4, 8, 4);
        AddPage(L("Общие"), Icons.Settings, GeneralPage());
        AddPage(L("Подписки"), Icons.Cloud, SubsPage());
        AddPage("DNS", Icons.Dns, DnsPage());
        AddPage(L("Тесты"), Icons.Speed, TestsPage());
        AddPage(L("Логи"), Icons.Log, LogsPage());
        AddPage(L("Интерфейс"), Icons.Brush, UiPage());
        AddPage(L("Клавиши"), Icons.Keyboard, KeysPage());
        AddPage(L("Резервная копия"), Icons.Backup, BackupPage());
        AddPage(L("О программе"), Icons.Info, AboutPage());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8) };
        Button cancel, ok;
        if (embedded)
        {
            // внутри главного окна: «Сохранить» применяет и остаётся на месте, «Отменить» возвращает сохранённые значения
            TopLevel = false; FormBorderStyle = FormBorderStyle.None; Dock = DockStyle.Fill;
            cancel = new Button { Text = L("Отменить"), Size = new Size(130, 32) };   // вернуть сохранённые значения
            ok = new Button { Text = L("Сохранить"), Size = new Size(130, 32), Margin = new Padding(0, 3, 8, 3) };
            cancel.Click += (_, _) => LoadValues();
            ok.Click += async (_, _) =>
            {
                if (!Theme.SetBusy(ok, true)) return;
                try { await SaveAsync(); }
                finally
                {
                    if (!ok.IsDisposed)
                    {
                        Theme.SetBusy(ok, false); ok.Text = L("✓ Сохранено");
                        var t = new System.Windows.Forms.Timer { Interval = 1500 };
                        t.Tick += (_, _) => { t.Dispose(); if (!ok.IsDisposed) ok.Text = L("Сохранить"); };
                        t.Start();
                    }
                }
            };
        }
        else
        {
            cancel = new Button { Text = L("Отмена"), Width = 100, DialogResult = DialogResult.Cancel };
            ok = new Button { Text = L("Сохранить"), Width = 110 };
            ok.Click += async (_, _) => { await SaveAsync(); DialogResult = DialogResult.OK; };
            CancelButton = cancel;
        }
        bottom.Controls.AddRange([cancel, ok]);

        Controls.Add(_host);
        Controls.Add(_tabs);
        Controls.Add(bottom);
        LoadValues();
        foreach (var p in _pages.Values) Theme.StyleTree(p);
        Theme.StyleTree(bottom);
        Select(_pages.Keys.First());
    }

    // ───────────────────────── страницы ─────────────────────────

    void AddPage(string title, char icon, Control page)
    {
        var b = new ToolStripButton(title, Icons.Get(icon));
        b.Click += (_, _) => Select(b);
        _tabs.Items.Add(b);
        _pages[b] = page;
    }

    /// <summary>Перечитать сохранённые значения (при каждом открытии раздела).</summary>
    internal void ReloadValues() => LoadValues();

    /// <summary>Для скриншотов: открыть вкладку по номеру.</summary>
    internal void SelectPage(int i) => Select(_pages.Keys.ElementAt(Math.Clamp(i, 0, _pages.Count - 1)));

    void Select(ToolStripButton b)
    {
        foreach (var k in _pages.Keys) k.Checked = k == b;
        _host.Controls.Clear();
        _host.Controls.Add(_pages[b]);
        ActiveControl = null;   // без автоматического выделения текста в первом поле
    }

    // Dock = Top + AutoSize: строки не растягиваются на всю высоту (иначе подпись последней строки «уезжает» вниз).
    static TableLayoutPanel Page() => new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };

    static void Row(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 14, 0) });
        t.Controls.Add(c);
    }

    static void Full(TableLayoutPanel t, Control c)
    {
        t.Controls.Add(c);
        t.SetColumnSpan(c, 2);
    }

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 10), MaximumSize = new Size(680, 0) };

    Control GeneralPage()
    {
        var t = Page();
        foreach (var c in new[] { _autostart, _autoTray, _startMin, _remember, _connectOnStart, _steam }) Full(t, c);
        Full(t, Hint(L("Автозапуск создаёт задачу в Планировщике Windows с правами администратора — без окна UAC при входе. ") +
                     L("Если программа уже работает в трее, повторный запуск ярлыка просто откроет её окно.")));
        Full(t, _killSwitch);
        Theme.Tip().SetToolTip(_killSwitch, KillSwitch.Help);
        Full(t, Hint(L("Интернет только через VPN. Если программа или ядро упали, либо ПК перезагрузили с включённым VPN, ") +
                     L("интернет остаётся заблокирован, пока вы не подключитесь снова. ") +
                     L("Снимается только вашим действием: «Отключить», «Выход» или «Снять блокировку» в трее ") +
                     L("(аварийно: TunPilot.exe --killswitch-off). Локальная сеть и исключения «мимо TUN» (Dota, Steam) работают. ") +
                     L("Совет: включите автозапуск и «Запоминать подключение», чтобы после перезагрузки VPN поднимался сам.")));
        Full(t, _localProxy);
        Row(t, L("Порт"), _localPort);
        Full(t, Hint(L("127.0.0.1:порт — для браузеров и программ, которым можно указать прокси. Маршрутизация та же, что и в TUN.")));
        return t;
    }

    Control SubsPage()
    {
        var t = Page();
        Full(t, _intProvider);
        var custom = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        custom.Controls.AddRange([_intCustom, _intMinutes]);
        Full(t, custom);
        Full(t, Hint(L("Автообновление никогда не перезапускает подключение само: изменения применятся при следующем подключении.")));
        Full(t, _showChanges);
        return t;
    }

    Control DnsPage()
    {
        var t = Page();
        Row(t, L("Удалённый DNS (через VPN, DoH)"), _remoteDns);
        Row(t, L("DNS для прямых запросов"), _localDns);
        Full(t, Hint(L("Удалённый — для сайтов через VPN (DNS-over-HTTPS через выбранный сервер). ") +
                     L("Прямой — для российских сайтов и всего, что идёт напрямую.")));
        return t;
    }

    Control TestsPage()
    {
        var t = Page();
        Row(t, L("URL теста задержки"), _latencyUrl);
        Row(t, L("URL теста скорости"), _speedUrls);
        Full(t, Hint(L("По одному на строку; если первый недоступен, берётся следующий. Нужен большой файл (≥ 100 МБ).")));
        Row(t, L("Потоков"), _streams);
        Row(t, L("Длительность, сек"), _seconds);
        return t;
    }

    Control LogsPage()
    {
        var t = Page();
        Row(t, L("Уровень логов sing-box"), _logSb);
        Row(t, L("Уровень логов Xray"), _logXr);
        Full(t, Hint(L("Подробные уровни (debug/trace) быстро раздувают журнал — включайте только для поиска проблем.")));
        return t;
    }

    Control UiPage()
    {
        var t = Page();
        Row(t, L("Тема"), _theme);
        Row(t, Loc.En ? "Language" : "Язык / Language", _language);
        Full(t, Hint(L("Смена языка перезапускает программу (VPN-подключение на пару секунд прервётся).")));
        return t;
    }

    Control KeysPage()
    {
        var t = Page();
        _hotkey.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.Back or Keys.Delete or Keys.Escape) { _hotkey.Text = ""; return; }
            var s = HotkeyWindow.Format(e.KeyData);
            if (s.Contains('+')) _hotkey.Text = s;
        };
        Row(t, L("Показать окно (из любой программы)"), _hotkey);
        Full(t, Hint(L("Щёлкните в поле и нажмите сочетание, например Ctrl+Alt+V. Backspace — убрать.")));
        Full(t, new Label
        {
            AutoSize = true, Font = new Font("Consolas", 9.5f), Margin = new Padding(0, 8, 0, 0),
            Text = L("В главном окне:\n") +
                   L("  Ctrl+U            обновить подписку\n") +
                   L("  Ctrl+Shift+S      тест задержки выделенных\n") +
                   L("  Ctrl+Shift+G      тест задержки группы\n") +
                   L("  F6                тест скорости текущего\n") +
                   L("  Ctrl+Shift+P      тест скорости выделенных\n") +
                   L("  Ctrl+Alt+P        тест скорости группы\n") +
                   L("  Ctrl+.            остановить тесты\n") +
                   L("  Ctrl+V            вставить ключи из буфера\n") +
                   L("  Ctrl+C / Del      копировать / удалить выделенные\n") +
                   L("  Ctrl+Shift+R      удалить недоступные\n") +
                   L("  Ctrl+Shift+D      удалить дубликаты\n") +
                   L("  Ctrl+Shift+C      очистить результаты тестов\n") +
                   L("  Ctrl+H            скрыть окно"),
        });
        return t;
    }

    Control BackupPage()
    {
        var t = Page();
        var make = new Button { Text = L("  Создать резервную копию…"), Image = Icons.Get(Icons.Save), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true };
        make.Click += (_, _) => MakeBackup();
        var restore = new Button { Text = L("  Восстановить из копии…"), Image = Icons.Get(Icons.Open), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true };
        restore.Click += async (_, _) => await RestoreAsync();
        Full(t, Hint(L("В копию входят настройки, группы и подписки (со ссылками!), профили маршрутизации и статистика. ") +
                     L("Храните файл так же бережно, как пароль: по ссылкам подписок можно пользоваться вашим VPN.")));
        Full(t, make);
        Full(t, restore);
        Full(t, Hint(L("После восстановления программа перезапустится.")));
        return t;
    }

    /// <summary>«О программе»: версия, лицензия, ссылки, версии ядер, папка данных, проверка обновлений.</summary>
    Control AboutPage()
    {
        var t = Page();

        // шапка: иконка + название + версия
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        var logo = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 14, 0) };
        try { using var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (ico != null) logo.Image = new Icon(ico, 64, 64).ToBitmap(); } catch { }
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        titles.Controls.Add(new Label { Text = "TunPilot", AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 0) });
        titles.Controls.Add(new Label { Text = Lf($"Версия {Updater.VersionText}"), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 0, 0) });
        head.Controls.AddRange([logo, titles]);
        Full(t, head);

        Full(t, Hint(L("VPN-клиент для Windows с TUN-режимом. Маршруты, исключения и серверы меняются без перезапуска TUN — подключение не рвётся, пока вы, например, играете.")));

        // ссылки
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        Button LinkBtn(string text, char icon, string url)
        {
            var b = new Button { Text = "  " + text, Image = Icons.Get(icon), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            return b;
        }
        links.Controls.AddRange([
            LinkBtn(L("Страница на GitHub"), Icons.Globe, Updater.PageUrl),
            LinkBtn(L("Сообщить о проблеме"), Icons.Chat, Updater.PageUrl + "/issues"),
        ]);
        Full(t, links);

        // проверка обновлений
        var updRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        var check = new Button { Text = "  " + L("Проверить обновления"), Image = Icons.Get(Icons.Refresh), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true };
        var updStatus = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0), ForeColor = SystemColors.GrayText };
        check.Click += async (_, _) =>
        {
            if (!Theme.SetBusy(check, true)) return;   // уже идёт
            updStatus.ForeColor = Theme.Muted;
            updStatus.Text = L("Проверяю…");
            try
            {
                var latest = await Updater.LatestAsync();
                if (latest is not { } r) updStatus.Text = L("На GitHub пока нет опубликованных версий.");
                else if (Updater.IsNewer(r.Tag))
                {
                    updStatus.ForeColor = Theme.Accent;
                    updStatus.Text = Lf($"Доступна новая версия {r.Tag.TrimStart('v')}");
                    if (TrayApp.Confirm(Lf($"Доступна версия {r.Tag.TrimStart('v')} (у вас {Updater.VersionText}).\nОткрыть страницу загрузки?")))
                        Process.Start(new ProcessStartInfo(r.Url) { UseShellExecute = true });
                }
                else updStatus.Text = Lf($"У вас последняя версия ({Updater.VersionText}).");
            }
            catch (Exception ex)
            {
                updStatus.ForeColor = Theme.Danger;
                updStatus.Text = L("Не удалось проверить: ") + ex.Message;
            }
            finally { Theme.SetBusy(check, false); }
        };
        updRow.Controls.AddRange([check, updStatus]);
        Full(t, updRow);

        // компоненты и лицензии
        var sb = new Label { AutoSize = true, Text = "sing-box …", Margin = new Padding(0, 4, 0, 0) };
        var xr = new Label { AutoSize = true, Text = "Xray …", Margin = new Padding(0, 4, 0, 0) };
        Row(t, L("Лицензия"), new Label { Text = "GPL-3.0", AutoSize = true, Margin = new Padding(0, 9, 0, 0) });
        Row(t, L("Ядра"), sb);
        Row(t, "", xr);

        // обновление ядер
        var coreRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
        var coreBtn = new Button { Text = "  " + L("Обновить ядра"), Image = Icons.Get(Icons.Download), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true };
        var coreStatus = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0), ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0) };
        coreBtn.Click += async (_, _) =>
        {
            if (!Theme.SetBusy(coreBtn, true)) return;
            coreStatus.ForeColor = Theme.Muted;
            void Status(string s) { if (!coreStatus.IsDisposed) coreStatus.Text = s; }
            try
            {
                Status(L("Проверяю…"));
                var infos = await CoreUpdater.CheckAsync();
                var upd = infos.Where(i => i.HasUpdate).ToList();
                if (upd.Count == 0) { Status(L("Ядра актуальны: ") + string.Join(", ", infos.Select(i => $"{i.Core.Name} {i.Current}"))); return; }
                var list = string.Join("\n", upd.Select(i => $"• {i.Core.Name}: {i.Current} → {i.Latest}"));
                if (!TrayApp.Confirm(L("Доступны новые версии ядер:\n") + list + "\n\n" +
                                     L("Если VPN подключён, он переподключится (~5 сек). Если новая версия не примет вашу конфигурацию, вернётся прежняя.\nОбновить?")))
                { Status(""); return; }
                var result = await _app.UpdateCoresAsync(upd, s => BeginInvoke(() => Status(s)));
                coreStatus.ForeColor = result.StartsWith(L("Ядра обновлены: ")) ? Theme.Accent : Theme.Danger;
                Status(result.Split('\n')[0]);
                if (coreStatus.ForeColor == Theme.Danger) TrayApp.ShowError(result);
                sb.Text = $"sing-box {await Updater.CoreVersionAsync(AppPaths.SingBoxExe)} — GPL-3.0 (SagerNet)";
                xr.Text = $"Xray {await Updater.CoreVersionAsync(AppPaths.XrayExe)} — MPL-2.0 (XTLS)";
            }
            catch (Exception ex)
            {
                coreStatus.ForeColor = Theme.Danger;
                Status(L("Не удалось проверить: ") + ex.Message);
            }
            finally { Theme.SetBusy(coreBtn, false); }
        };
        coreRow.Controls.AddRange([coreBtn, coreStatus]);
        Row(t, "", coreRow);
        // версии узнаём в фоне; результат — через контекст UI-потока (у встроенной страницы окна ещё может не быть)
        var ui = SynchronizationContext.Current;
        _ = Task.Run(async () =>
        {
            var vs = await Updater.CoreVersionAsync(AppPaths.SingBoxExe);
            var vx = await Updater.CoreVersionAsync(AppPaths.XrayExe);
            ui?.Post(_ =>
            {
                if (sb.IsDisposed) return;
                sb.Text = $"sing-box {vs} — GPL-3.0 (SagerNet)";
                xr.Text = $"Xray {vx} — MPL-2.0 (XTLS)";
            }, null);
        });

        // папка данных
        var data = new LinkLabel { Text = AppPaths.Data, AutoSize = true, Margin = new Padding(0, 9, 0, 0), LinkColor = Theme.Direct, ActiveLinkColor = Theme.Accent };
        data.LinkClicked += (_, _) => { try { Process.Start("explorer.exe", AppPaths.Data); } catch { } };
        Row(t, L("Настройки и логи"), data);

        Full(t, Hint(L("TunPilot — свободная программа с открытым исходным кодом. Её можно использовать, изучать, менять и распространять на условиях лицензии GPL-3.0.")));
        return t;
    }

    // ───────────────────────── значения ─────────────────────────

    void LoadValues()
    {
        _startMin.Checked = _s.StartMinimized;
        _autoTray.Checked = _s.AutostartToTray;
        _autostart.Checked = Autostart.IsEnabled();
        _remember.Checked = _s.RememberState;
        _connectOnStart.Checked = _s.ConnectOnStart;
        _steam.Checked = _s.SteamSdrBypass;
        _killSwitch.Checked = _s.KillSwitch;
        _localProxy.Checked = _s.LocalProxyEnabled;
        _localPort.Value = Math.Clamp(_s.LocalProxyPort, 1024, 65535);
        _intProvider.Checked = _s.SubIntervalMode != "custom";
        _intCustom.Checked = _s.SubIntervalMode == "custom";
        _intMinutes.Value = Math.Clamp(_s.SubIntervalMinutes, 30, 10080);
        _showChanges.Checked = _s.ShowSubChanges;
        _remoteDns.Text = _s.RemoteDns;
        _localDns.Text = _s.LocalDns;
        _latencyUrl.Text = _s.LatencyUrl;
        _speedUrls.Text = string.Join("\r\n", _s.SpeedUrls);
        _streams.Value = Math.Clamp(_s.SpeedStreams, 1, 16);
        _seconds.Value = Math.Clamp(_s.SpeedSeconds, 3, 30);
        _logSb.SelectedItem = _s.LogLevelSingBox; if (_logSb.SelectedIndex < 0) _logSb.SelectedItem = "warn";
        _logXr.SelectedItem = _s.LogLevelXray; if (_logXr.SelectedIndex < 0) _logXr.SelectedItem = "warning";
        _theme.SelectedIndex = _s.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        _language.SelectedIndex = _s.Language switch { "ru" => 1, "en" => 2, _ => 0 };
        _hotkey.Text = _s.GlobalHotkey;
    }

    async Task SaveAsync()
    {
        bool core = _s.RemoteDns != _remoteDns.Text.Trim() || _s.LocalDns != _localDns.Text.Trim()
            || _s.LogLevelSingBox != (string)_logSb.SelectedItem! || _s.LogLevelXray != (string)_logXr.SelectedItem!
            || _s.LocalProxyEnabled != _localProxy.Checked || (_localProxy.Checked && _s.LocalProxyPort != (int)_localPort.Value);
        var theme = _theme.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" };
        var language = _language.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" };
        if (language != _s.Language)
        {
            // язык меняется только перезапуском: тексты уже разложены по окнам и меню
            bool wasEn = Loc.En;
            Loc.Apply(language);
            bool changes = Loc.En != wasEn;
            Loc.En = wasEn;
            _s.Language = language;
            _s.Save();
            if (changes && TrayApp.Confirm(L("Перезапустить программу сейчас, чтобы сменить язык?")))
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
                await _app.ExitAsync();
                return;
            }
        }
        bool themeChanged = theme != _s.Theme;
        bool steamChanged = _s.SteamSdrBypass != _steam.Checked;

        _s.StartMinimized = _startMin.Checked;
        _s.AutostartToTray = _autoTray.Checked;
        _s.RememberState = _remember.Checked;
        _s.ConnectOnStart = _connectOnStart.Checked;
        _s.SteamSdrBypass = _steam.Checked;
        _s.LocalProxyEnabled = _localProxy.Checked;
        _s.LocalProxyPort = (int)_localPort.Value;
        _s.SubIntervalMode = _intCustom.Checked ? "custom" : "provider";
        _s.SubIntervalMinutes = (int)_intMinutes.Value;
        _s.ShowSubChanges = _showChanges.Checked;
        _s.RemoteDns = _remoteDns.Text.Trim();
        _s.LocalDns = _localDns.Text.Trim();
        _s.LatencyUrl = _latencyUrl.Text.Trim();
        _s.SpeedUrls = _speedUrls.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        _s.SpeedStreams = (int)_streams.Value;
        _s.SpeedSeconds = (int)_seconds.Value;
        _s.LogLevelSingBox = (string)_logSb.SelectedItem!;
        _s.LogLevelXray = (string)_logXr.SelectedItem!;
        _s.Theme = theme;
        _s.GlobalHotkey = _hotkey.Text;
        if (_autostart.Checked != Autostart.IsEnabled()) _app.ToggleAutostart();

        await _app.SettingsChangedAsync(core, themeChanged);
        if (steamChanged) await _app.ApplyBypassAsync(quiet: true);
        if (_killSwitch.Checked != _s.KillSwitch) _app.SetKillSwitch(_killSwitch.Checked);
    }

    // ───────────────────────── резервная копия ─────────────────────────

    static readonly string[] BackupItems = ["settings.json", "groups.json", "stats.json", "groups", "profiles"];

    void MakeBackup()
    {
        using var dlg = new SaveFileDialog { Filter = L("Резервная копия TunPilot (*.zip)|*.zip"), FileName = $"TunPilot-{DateTime.Now:yyyy-MM-dd}.zip" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _app.Monitor.Stats.Save();
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            foreach (var item in BackupItems)
            {
                var path = Path.Combine(AppPaths.Data, item);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, item);
                else if (Directory.Exists(path))
                    foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, Path.GetRelativePath(AppPaths.Data, f).Replace('\\', '/'));
            }
            _app.Balloon(L("Резервная копия сохранена."), ToolTipIcon.Info);
        }
        catch (Exception ex) { TrayApp.ShowError(L("Не удалось создать копию:\n") + ex.Message); }
    }

    async Task RestoreAsync()
    {
        using var dlg = new OpenFileDialog { Filter = L("Резервная копия TunPilot (*.zip)|*.zip") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!TrayApp.Confirm(L("Текущие настройки, группы и профили будут заменены данными из копии. Продолжить?"))) return;
        try
        {
            using (var zip = ZipFile.OpenRead(dlg.FileName))
            {
                if (zip.GetEntry("settings.json") == null) throw new InvalidDataException(L("Это не резервная копия TunPilot."));
                foreach (var dir in new[] { "groups", "profiles" })
                    if (Directory.Exists(Path.Combine(AppPaths.Data, dir))) Directory.Delete(Path.Combine(AppPaths.Data, dir), true);
                foreach (var e in zip.Entries.Where(e => e.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(AppPaths.Data, e.FullName));
                    if (!target.StartsWith(Path.GetFullPath(AppPaths.Data), StringComparison.OrdinalIgnoreCase)) continue;   // защита от путей ../
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                }
            }
            // перезапуск: новый процесс дождётся, пока этот освободит «единственный экземпляр»
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
            await _app.ExitAsync();
        }
        catch (Exception ex) { TrayApp.ShowError(L("Не удалось восстановить:\n") + ex.Message); }
    }

    // ───────────────────────── фабрики ─────────────────────────

    static CheckBox Chk(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 100 };
    static ComboBox Combo(bool editable, params string[] items)
    {
        var c = new ComboBox { Width = 260, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        return c;
    }
}
