using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TunPilot;

/// <summary>Сердце программы: состояние, подключение, группы, профили, трей. Окна — только отображение.</summary>
public sealed class TrayApp : ApplicationContext, IAppState
{
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _toggle = new();
    readonly ToolStripMenuItem _killSwitch = new("Kill-switch");
    readonly ToolStripMenuItem _unblock = new(L("Снять блокировку интернета (kill-switch)"));
    List<string> _bypassCidrs = [];
    bool _ksOn;   // фильтры kill-switch сейчас стоят в системе
    readonly ToolStripMenuItem _servers = new(L("Сервер"));
    readonly ToolStripMenuItem _profiles = new(L("Профиль маршрутизации"));
    readonly CoreManager _core = new();
    readonly Settings _settings = Settings.Load();
    readonly GroupStore _groups;
    readonly System.Windows.Forms.Timer _subTimer = new() { Interval = 5 * 60 * 1000 };
    readonly SynchronizationContext _ui;
    readonly HotkeyWindow _hotkey;
    List<ServerInfo> _all = [];
    bool _busy, _pending;
    string _routesInfo = "";
    MainForm? _window;

    // ── для окон ──
    internal Settings Settings => _settings;
    internal CoreManager Core => _core;
    internal GroupStore Groups => _groups;
    internal TrafficMonitor Monitor { get; }
    internal IReadOnlyList<ServerInfo> AllServers => _all;
    CoreManager IAppState.Core => _core;
    IReadOnlyList<ServerInfo> IAppState.AllServers => _all;
    GroupStore IAppState.Groups => _groups;
    internal bool Busy => _busy;
    internal string RoutesInfo => _routesInfo;
    internal bool PendingSubUpdate => _pending;
    internal DateTime ConnectedAt { get; private set; }
    internal Icon AppIcon { get; } = Icons.PlainIcon();   // окна — без точки состояния (она только в трее)
    internal event Action? StateChanged;

    internal Group? ActiveGroup => _groups.Find(_settings.ActiveGroupId) ?? _groups.Groups.FirstOrDefault();
    internal List<ServerInfo> ServersOf(Group g) => _all.Where(s => s.GroupId == g.Id).ToList();

    /// <param name="startHidden">Не показывать окно (режим скриншотов).</param>
    /// <param name="autostart">Запуск вместе с Windows (ключ --tray из Планировщика).</param>
    public TrayApp(bool startHidden = false, bool autostart = false)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Theme.Apply(_settings.Theme);
        _groups = GroupStore.Load(_settings);
        Profiles.Init(_settings);
        CoreManager.Config = _settings;
        RebuildServers();
        Monitor = new TrafficMonitor(this);

        _tray = new NotifyIcon { Icon = Icons.AppIcon(Color.Gray), Visible = true, ContextMenuStrip = _menu, Text = "TunPilot" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _toggle.Click += async (_, _) => { if (_core.Running) await DisconnectAsync(); else await ConnectAsync(); };
        _menu.Items.AddRange([
            new ToolStripMenuItem(L("Открыть окно"), Icons.Get(Icons.Window), (_, _) => ShowWindow()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            _status, new ToolStripSeparator(),
            _toggle, _unblock, _servers, _profiles, _killSwitch, new ToolStripSeparator(),
            new ToolStripMenuItem(L("Обновить все подписки"), Icons.Get(Icons.Refresh), async (_, _) => await UpdateAllAsync(manual: true)),
            new ToolStripMenuItem(L("Статистика трафика"), Icons.Get(Icons.Chart), (_, _) => ShowStats()),
            new ToolStripMenuItem(L("Настройки…"), Icons.Get(Icons.Settings), (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(L("Выход"), Icons.Get(Icons.Power), async (_, _) => await ExitAsync()),
        ]);
        Theme.StyleDropDown(_menu);
        _menu.Opening += (_, _) => RefreshMenu();
        _killSwitch.Click += (_, _) => SetKillSwitch(!_settings.KillSwitch);
        _menu.ShowItemToolTips = true;
        _killSwitch.ToolTipText = KillSwitch.Help;
        _unblock.ToolTipText = L("Интернет заблокирован kill-switch (программа или ядро завершились аварийно, либо ПК перезагрузили с включённым VPN).\nНажмите, чтобы снять блокировку, или просто подключитесь.");
        _unblock.Click += (_, _) => { DisableKillSwitch(); RefreshMenu(); };

        _core.UnexpectedExit += name => _ui.Post(async _ =>
        {
            Balloon(L("Соединение прервалось"), Lf($"Ядро {name} завершилось с ошибкой. Подробности — в окне, вкладка «Логи»."), ToolTipIcon.Error);
            // Ядро упало — kill-switch НЕ снимаем: именно сейчас он и нужен.
            if (name == "sing-box") await DisconnectAsync(userAction: false, keepKillSwitch: true);
        }, null);

        _subTimer.Tick += async (_, _) => await UpdateAllAsync(manual: false);
        _subTimer.Start();

        _hotkey = new HotkeyWindow(ShowWindow);
        _hotkey.Register(_settings.GlobalHotkey);

        bool toTray = startHidden || (autostart ? _settings.AutostartToTray : _settings.StartMinimized);
        if (!toTray) ShowWindow();
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        await BypassRoutes.CleanupLeftoversAsync();
        _ksOn = KillSwitch.IsActive;   // осталась блокировка после падения программы?
        if (Autostart.PointsElsewhere()) Autostart.Enable();   // программу перенесли — обновим путь
        if (_groups.Groups.Count == 0)
        {
            RefreshMenu();
            ShowWindow();
            ShowGroups();   // первый запуск — сразу предлагаем добавить подписку
        }
        else
        {
            // группы без кэша (новые/после восстановления) — скачать сразу
            foreach (var g in _groups.Groups.Where(g => g.IsSubscription && g.Body.Length == 0))
                await UpdateGroupAsync(g, manual: false, force: true);
            await UpdateAllAsync(manual: false);
        }
        RefreshMenu();
        bool restore = _settings.RememberState && _settings.WasConnected;
        bool connect = (_settings.ConnectOnStart || restore) && _all.Count > 0;
        if (_ksOn && !_settings.KillSwitch) DisableKillSwitch();
        else if (_ksOn && !connect)
            Balloon(L("Интернет заблокирован kill-switch после аварийного завершения. Подключитесь или выберите в меню трея «Снять блокировку»."), ToolTipIcon.Warning);
        RefreshMenu();
        if (connect) await ConnectAsync();
    }

    void RebuildServers()
    {
        _all = _groups.Groups.SelectMany(g => g.ParseServers()).ToList();
        if (ActiveGroup is { } ag && _settings.ActiveGroupId != ag.Id) { _settings.ActiveGroupId = ag.Id; _settings.Save(); }
    }

    // ───────────────────────── выбор сервера ─────────────────────────

    /// <summary>Тег в ядре для сохранённого выбора.</summary>
    internal string SelectedTag
    {
        get
        {
            var key = _settings.SelectedServer;
            if (key.StartsWith("auto:")) return "auto-" + key[5..];
            return _all.FirstOrDefault(s => s.Key == key)?.Tag ?? ActiveGroup?.AutoTag ?? "";
        }
    }

    internal string SelectedDisplay
    {
        get
        {
            var key = _settings.SelectedServer;
            if (key.StartsWith("auto:"))
            {
                var g = _groups.Find(key[5..]);
                var now = g != null && Monitor.AutoNow.TryGetValue(g.AutoTag, out var t) ? _all.FirstOrDefault(s => s.Tag == t)?.Name : null;
                return Lf($"Авто ({g?.Name ?? "?"})") + (now != null ? " → " + now : "");
            }
            return _all.FirstOrDefault(s => s.Key == key)?.Name ?? "—";
        }
    }

    internal async Task SelectServerAsync(string key)
    {
        _settings.SelectedServer = key;
        _settings.Save();
        if (_core.Running && !await _core.SelectAsync(SelectedTag))
            Balloon(L("Сервер появится после переподключения (изменилась подписка)."), ToolTipIcon.Info);
        RefreshMenu();
    }

    internal void SetActiveGroup(Group g)
    {
        _settings.ActiveGroupId = g.Id;
        _settings.Save();
        RefreshMenu();
    }

    // ───────────────────────── подключение ─────────────────────────

    internal async Task ConnectAsync()
    {
        if (_busy) return;
        if (_all.Count == 0) { Balloon(L("Нет серверов: добавьте подписку в «Группах»."), ToolTipIcon.Warning); return; }

        SetBusy(true);
        try
        {
            if (_pending) { RebuildServers(); _pending = false; }
            var err = await _core.PrepareAsync(_settings, _all, SelectedTag);
            if (err != null) { ShowError(err); return; }
            BypassRoutes.ResetGateway();
            await ApplyBypassAsync(quiet: true);   // до TUN: так шлюз определяется надёжнее
            if (_settings.KillSwitch) EnableKillSwitch();
            await _core.StartAsync();
            ConnectedAt = DateTime.Now;
            Monitor.ResetSession();
            _settings.WasConnected = true; _settings.Save();
            var lines = new List<string> { Lf($"Сервер: {SelectedDisplay}"), Lf($"Маршруты: {Profiles.Current.Name}") };
            if (_routesInfo.Length > 0) lines.Add(L("Игры и исключения: ") + _routesInfo);
            if (_ksOn) lines.Add(L("Kill-switch включён"));
            if (_core.GeoMissing.Count > 0) lines.Add(Lf($"Не скачались списки сайтов: {string.Join(", ", _core.GeoMissing)}"));
            Balloon(L("Подключено"), string.Join("\n", lines), ToolTipIcon.Info);
        }
        catch (Exception ex) { ShowError(ex.Message); await BypassRoutes.ClearAsync(); DisableKillSwitch(); }
        finally { SetBusy(false); }
    }

    /// <param name="userAction">Отключение пользователем (а не выход/сбой) — запоминаем «было выключено».</param>
    internal async Task DisconnectAsync(bool userAction = true, bool keepKillSwitch = false)
    {
        if (userAction && _settings.WasConnected) { _settings.WasConnected = false; _settings.Save(); }
        _core.Stop();
        try { await BypassRoutes.ClearAsync(); } catch { }
        if (!keepKillSwitch) DisableKillSwitch();
        _routesInfo = "";
        Monitor.Stats.Save();
        RefreshMenu();
    }

    internal async Task ReconnectAsync()
    {
        if (!_core.Running) { RebuildServers(); _pending = false; RefreshMenu(); return; }
        _core.Stop();
        await ConnectAsync();
    }

    /// <summary>Перезапуск с подтверждением (никогда не рвём соединения молча — вдруг идёт игра).</summary>
    internal async Task AskReconnectAsync(string why)
    {
        if (!_core.Running) { RebuildServers(); _pending = false; RefreshMenu(); return; }
        if (Confirm(Lf($"{why}\nНужен перезапуск (~2 сек обрыва соединений через TUN). Перезапустить сейчас?")))
            await ReconnectAsync();
        else { _pending = true; RefreshMenu(); }
    }

    internal async Task ApplyBypassAsync(bool quiet = false)
    {
        if (!_core.Running && !_busy) return;
        try
        {
            var cidrs = RuleLists.ReadBypassCidrs();
            if (_settings.SteamSdrBypass) cidrs.AddRange(await SteamSdr.GetCidrsAsync());
            _routesInfo = await BypassRoutes.ApplyAsync(cidrs);
            _bypassCidrs = cidrs;
            if (_ksOn) KillSwitch.UpdateBypass(cidrs);   // на лету, без перезапуска
            if (!quiet) Balloon(L("Исключения применены"), _routesInfo + L(" — без переподключения."), ToolTipIcon.Info);
        }
        catch (Exception ex) { Balloon(L("Исключения не применились"), ex.Message, ToolTipIcon.Warning); }
        RefreshMenu();
    }

    // ───────────────────────── kill-switch ─────────────────────────

    /// <summary>Переключатель из настроек/трея: применяется сразу, без переподключения.</summary>
    internal void SetKillSwitch(bool on)
    {
        _settings.KillSwitch = on; _settings.Save();
        if (on && _core.Running) EnableKillSwitch();
        else if (!on) DisableKillSwitch();
        if (on && _ksOn) Balloon(L("Kill-switch включён: интернет только через VPN."), ToolTipIcon.Info);
        RefreshMenu();
    }

    void EnableKillSwitch()
    {
        try { KillSwitch.Enable(_bypassCidrs); _ksOn = true; }
        catch (Exception ex) { Balloon(L("Kill-switch не включился: ") + ex.Message, ToolTipIcon.Warning); }
    }

    void DisableKillSwitch()
    {
        if (!_ksOn && !KillSwitch.IsActive) return;
        try { KillSwitch.Disable(); _ksOn = false; }
        catch (Exception ex) { ShowError(L("Не удалось снять блокировку kill-switch:\n") + ex.Message + L("\n\nПерезагрузка Windows снимет её точно.")); }
    }

    // ───────────────────────── подписки и группы ─────────────────────────

    internal async Task UpdateAllAsync(bool manual)
    {
        foreach (var g in _groups.Groups.Where(g => g.IsSubscription).ToList())
            await UpdateGroupAsync(g, manual, force: manual);
    }

    /// <summary>Обновить подписку группы. force — игнорировать интервал.</summary>
    internal async Task UpdateGroupAsync(Group g, bool manual, bool force = false)
    {
        if (!g.IsSubscription || g.Url.Length == 0) return;
        if (!force)
        {
            var mins = g.UpdateIntervalMinutes(_settings);
            if (mins == 0 || DateTime.UtcNow - g.UpdatedUtc < TimeSpan.FromMinutes(mins)) return;
        }
        try
        {
            var (body, info) = await Subscription.FetchAsync(g.Url);
            var oldNames = Subscription.Parse(g.Body, g.Id).Select(s => s.Name).ToList();
            bool changed = body != g.Body;
            g.Body = body;
            g.Info = info;
            g.UpdatedUtc = DateTime.UtcNow;
            g.Excluded.Clear();   // скрытые серверы возвращаются при обновлении, как в Throne
            if (g.Name.Length == 0 && info.Title.Length > 0) g.Name = info.Title;
            _groups.Save();

            var fresh = Subscription.Parse(body, g.Id);
            if (manual && _settings.ShowSubChanges)
            {
                var added = fresh.Select(s => s.Name).Except(oldNames).ToList();
                var removed = oldNames.Except(fresh.Select(s => s.Name)).ToList();
                if (added.Count + removed.Count > 0)
                    MessageBox.Show(
                        Lf($"«{g.Name}»: {fresh.Count} серверов.\n") +
                        (added.Count > 0 ? L("\nДобавлены:\n  ") + string.Join("\n  ", added) + "\n" : "") +
                        (removed.Count > 0 ? L("\nУдалены:\n  ") + string.Join("\n  ", removed) : ""),
                        L("TunPilot — изменения подписки"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                else Balloon(Lf($"«{g.Name}»: {fresh.Count} серверов, без изменений."), ToolTipIcon.Info);
            }

            if (!changed) { RefreshMenu(); return; }
            if (!_core.Running) RebuildServers();
            else if (manual) await AskReconnectAsync(Lf($"Подписка «{g.Name}» изменилась."));
            else { _pending = true; Balloon(Lf($"Подписка «{g.Name}» обновилась — применится при следующем подключении."), ToolTipIcon.Info); }
        }
        catch (Exception ex) { if (manual) ShowError(Lf($"Не удалось обновить «{g.Name}»:\n{ex.Message}")); }
        RefreshMenu();
    }

    /// <summary>Группы изменены в окне «Группы» (добавлены/удалены/отредактированы).</summary>
    internal async Task GroupsChangedAsync()
    {
        _groups.Save();
        foreach (var g in _groups.Groups.Where(g => g.IsSubscription && g.Body.Length == 0))
            await UpdateGroupAsync(g, manual: false, force: true);
        var before = string.Join(",", _all.Select(s => s.Tag + s.Raw.GetHashCode()));
        var after = string.Join(",", _groups.Groups.SelectMany(g => g.ParseServers()).Select(s => s.Tag + s.Raw.GetHashCode()));
        if (before == after) { RefreshMenu(); return; }
        if (_core.Running) await AskReconnectAsync(L("Список серверов изменился."));
        else { RebuildServers(); RefreshMenu(); }
    }

    /// <summary>Вставка ключей (vless://, hy2://…) в ручную группу «Ключи».</summary>
    internal async Task<int> AddLinksAsync(string text)
    {
        var links = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("://") && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count == 0) return 0;
        var g = ActiveGroup is { IsSubscription: false } ag ? ag
              : _groups.Groups.FirstOrDefault(x => !x.IsSubscription) ?? _groups.Add(L("Ключи"), "manual");
        var body = g.Body.TrimEnd();
        var added = links.Where(l => !body.Contains(l)).ToList();
        g.Body = (body.Length > 0 ? body + "\n" : "") + string.Join("\n", added);
        _groups.Save();
        SetActiveGroup(g);
        await GroupsChangedAsync();
        return added.Count;
    }

    /// <summary>Удалить серверы: из ручной группы — насовсем, из подписки — скрыть до обновления.</summary>
    internal async Task RemoveServersAsync(Group g, IEnumerable<ServerInfo> servers)
    {
        var list = servers.ToList();
        if (list.Count == 0) return;
        if (g.IsSubscription) g.Excluded.AddRange(list.Select(s => s.Name));
        else
        {
            var raws = list.Select(s => s.Raw).ToHashSet();
            g.Body = string.Join("\n", g.Body.Split('\n').Where(l => !raws.Contains(l.Trim())));
        }
        _groups.Save();
        await GroupsChangedAsync();
    }

    internal async Task<int> RemoveDuplicatesAsync(Group g)
    {
        var dups = ServersOf(g).GroupBy(s => s.Raw).SelectMany(x => x.Skip(1)).ToList();
        await RemoveServersAsync(g, dups);
        return dups.Count;
    }

    // ───────────────────────── маршрутизация ─────────────────────────

    internal async Task SwitchProfileAsync(RoutingProfile p)
    {
        var old = Profiles.Current;
        if (old.Name == p.Name) return;
        Profiles.SetCurrent(p, _settings);
        RuleLists.Compile();                  // списки sing-box подхватит сам
        await GeoRuleSets.EnsureAsync(_settings, p);   // и готовые наборы — тоже
        if (_core.Running)
        {
            await ApplyBypassAsync(quiet: true);
            if (p.NeedsRestartComparedTo(old)) await AskReconnectAsync(Lf($"У профиля «{p.Name}» другое «всё остальное»."));
            else Balloon(Lf($"Профиль «{p.Name}» применён без перезапуска."), ToolTipIcon.Info);
        }
        RefreshMenu();
    }

    /// <summary>
    /// Обновить ядра. Сначала всё скачивается (VPN работает), затем ядро ненадолго останавливается,
    /// файлы заменяются, конфиг проверяется новыми ядрами; не принят — возвращаются прежние версии.
    /// Kill-switch на время замены не снимается. Возвращает итог для показа.
    /// </summary>
    internal async Task<string> UpdateCoresAsync(List<CoreUpdater.Info> updates, Action<string> progress)
    {
        var ready = new List<(CoreUpdater.Info Info, string Root, string Temp)>();
        try
        {
            foreach (var u in updates)
            {
                var (root, temp) = await CoreUpdater.DownloadAsync(u, progress);
                ready.Add((u, root, temp));
            }
        }
        catch { foreach (var r in ready) CoreUpdater.Cleanup(r.Temp); throw; }

        bool wasOn = _core.Running;
        var replaced = new List<CoreUpdater.Info>();
        string result;
        try
        {
            progress(L("Останавливаю ядро…"));
            if (wasOn) await DisconnectAsync(userAction: false, keepKillSwitch: true);
            await Task.Run(CoreUpdater.KillStrayCores);
            progress(L("Заменяю файлы…"));
            foreach (var r in ready)
            {
                await Task.Run(() => CoreUpdater.Replace(r.Info, r.Root));
                replaced.Add(r.Info);
            }

            progress(L("Проверяю конфигурацию новыми ядрами…"));
            var err = await _core.PrepareAsync(_settings, _all, SelectedTag);
            if (err != null)
            {
                foreach (var u in replaced) CoreUpdater.Rollback(u.Core);
                result = L("Новая версия не приняла вашу конфигурацию — возвращена прежняя.\n\n") + err;
            }
            else
            {
                foreach (var u in replaced) CoreUpdater.Commit(u.Core);
                result = L("Ядра обновлены: ") + string.Join(", ", replaced.Select(u => $"{u.Core.Name} {u.Latest}"));
            }
        }
        catch (Exception ex)
        {
            foreach (var u in replaced) CoreUpdater.Rollback(u.Core);
            result = L("Обновление не удалось, оставлены прежние версии:\n") + ex.Message;
        }
        finally
        {
            foreach (var r in ready) CoreUpdater.Cleanup(r.Temp);
        }

        if (wasOn) { progress(L("Подключаюсь…")); await ConnectAsync(); }
        RefreshMenu();
        return result;
    }

    /// <summary>Изменился состав готовых наборов текущего профиля — скачать и применить на лету.</summary>
    internal async Task<List<string>> GeoChangedAsync()
    {
        var p = Profiles.Current;
        p.Save();
        var (_, missing) = await GeoRuleSets.EnsureAsync(_settings, p);
        if (missing.Count == 0 && _core.Running) Balloon(L("Наборы применены"), L("Без переподключения."), ToolTipIcon.Info);
        RefreshMenu();
        return missing;
    }

    internal async Task SetDefaultRouteAsync(string key)
    {
        var p = Profiles.Current;
        if (p.DefaultOutbound == key) return;
        p.DefaultOutbound = key;
        p.Save();
        await AskReconnectAsync(L("«Всё остальное» изменилось."));
    }

    internal async Task SetSteamBypassAsync(bool on)
    {
        _settings.SteamSdrBypass = on; _settings.Save();
        await ApplyBypassAsync();
    }

    internal void OpenEditor()
    {
        using var f = new ListEditorForm();
        if (f.ShowDialog() != DialogResult.OK) return;
        RuleLists.Compile();   // sing-box подхватит rule-set сам, без перезапуска
        if (f.BypassChanged) _ = ApplyBypassAsync();
        else if (_core.Running) Balloon(L("Списки применены без перезапуска."), ToolTipIcon.Info);
    }

    /// <summary>Импорт профиля маршрутизации из другого клиента (Throne, Happ, v2rayN, Xray, sing-box, Clash…).</summary>
    internal async Task ImportThroneAsync()
    {
        var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        var text = Prompt(L("Вставьте ссылку, текст настроек или ссылку на файл — или нажмите «Файл…».\n") + RouteImport.SupportedText,
            RouteImport.LooksImportable(clip) ? clip : "", multiline: true, withFile: true) ?? "";
        if (text.Trim().Length == 0) return;
        try
        {
            var (profile, report) = await RouteImport.ApplyAsync(text);
            if (Confirm(report + Lf($"\n\nСделать профиль «{profile.Name}» активным?"))) await SwitchProfileAsync(profile);
        }
        catch (Exception ex) { ShowError(L("Не удалось импортировать: ") + ex.Message); }
        RefreshMenu();
    }

    // ───────────────────────── настройки ─────────────────────────

    internal void ToggleAutostart()
    {
        if (Autostart.IsEnabled()) Autostart.Disable();
        else if (Autostart.Enable() is { } err) ShowError(L("Не удалось включить автозапуск:\n") + err);
        RefreshMenu();
    }

    /// <summary>Настройки сохранены в окне настроек.</summary>
    internal async Task SettingsChangedAsync(bool coreAffected, bool themeChanged)
    {
        _settings.Save();
        CoreManager.Config = _settings;
        _hotkey.Register(_settings.GlobalHotkey);
        if (themeChanged)
        {
            Theme.Apply(_settings.Theme);
            Theme.StyleDropDown(_menu);
            // окно пересоздаём после текущего события (кнопка «Сохранить» лежит в нём же) и возвращаемся в настройки
            _ui.Post(_ =>
            {
                bool visible = _window is { Visible: true };
                _window?.Dispose(); _window = null;
                if (visible) { ShowWindow(); _window?.ShowSettingsPage(); }
            }, null);
        }
        if (coreAffected) await AskReconnectAsync(L("Изменились настройки ядра (DNS, логи или локальный прокси)."));
        RefreshMenu();
    }

    // ───────────────────────── окна ─────────────────────────

    internal void ShowWindow()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainForm(this);
            _window.FormClosing += (_, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;   // крестик сворачивает в трей, выход — через меню
                _window.Hide();
                // подсказка — один раз за всё время, а не при каждом сворачивании
                if (!_settings.TrayHintShown)
                {
                    _settings.TrayHintShown = true; _settings.Save();
                    Balloon(L("Свёрнуто в трей"), L("TunPilot продолжает работать. Окно открывается кликом по иконке у часов."), ToolTipIcon.Info);
                }
            };
        }
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    internal void HideWindow() => _window?.Hide();

    /// <summary>Повторный запуск программы (ярлык, exe) — показать окно уже работающей копии.</summary>
    internal void ShowWindowFromOutside() => _ui.Post(_ =>
    {
        ShowWindow();
        if (_window is { } w) { w.TopMost = true; w.TopMost = false; w.Activate(); }
    }, null);

    internal void ShowStats() { using var f = new StatsForm(this); f.ShowDialog(_window); }
    internal void ShowGroups() { using var f = new GroupsForm(this); f.ShowDialog(_window); }
    /// <summary>Настройки — раздел главного окна (не отдельное окно).</summary>
    internal void ShowSettings() { ShowWindow(); _window?.ShowSettingsPage(); }

    internal async Task ExitAsync()
    {
        _tray.Visible = false;
        _hotkey.Dispose();
        _window?.Dispose();
        await DisconnectAsync(userAction: false);   // при выходе сохраняем, было ли подключено
        Monitor.Dispose();
        _core.Dispose();
        ExitThread();
    }

    // ───────────────────────── меню трея ─────────────────────────

    internal void RefreshMenu()
    {
        if (_ui != SynchronizationContext.Current) { _ui.Post(_ => RefreshMenu(), null); return; }
        bool on = _core.Running;
        _status.Text = _busy ? L("Подключение…") : on ? Lf($"Подключено · {SelectedDisplay}") : L("Отключено");
        _toggle.Text = on ? L("Отключить") : L("Подключить");
        _toggle.Image = Icons.Get(on ? Icons.Stop : Icons.Play, on ? Theme.Danger : Theme.Accent);
        _toggle.Enabled = !_busy;
        _killSwitch.Checked = _settings.KillSwitch;
        _killSwitch.Image = Icons.Get(Icons.Shield, _ksOn ? Theme.Accent : Theme.Muted);
        _killSwitch.Text = _ksOn ? L("Kill-switch (активен)") : "Kill-switch";
        _unblock.Visible = _ksOn && !on && !_busy;
        _unblock.Image = Icons.Get(Icons.Shield, Theme.Danger);
        _tray.Icon = Icons.AppIcon(_busy ? Theme.Warn : on ? Theme.Accent : Color.Gray);
        _tray.Text = Truncate("TunPilot — " + (on ? L("подключено") : L("отключено")), 63);

        _servers.DropDownItems.Clear();
        if (ActiveGroup is { } g)
        {
            var auto = new ToolStripMenuItem(Lf($"⚡ Авто ({g.Name})")) { Checked = _settings.SelectedServer == g.AutoKey };
            auto.Click += async (_, _) => await SelectServerAsync(g.AutoKey);
            _servers.DropDownItems.Add(auto);
            _servers.DropDownItems.Add(new ToolStripSeparator());
            foreach (var s in ServersOf(g))
            {
                var key = s.Key;
                var item = new ToolStripMenuItem(s.Name) { Checked = _settings.SelectedServer == key };
                item.Click += async (_, _) => await SelectServerAsync(key);
                _servers.DropDownItems.Add(item);
            }
        }
        _profiles.DropDownItems.Clear();
        foreach (var p in Profiles.All())
        {
            var item = new ToolStripMenuItem(p.Name) { Checked = p.Name == Profiles.Current.Name };
            item.Click += async (_, _) => await SwitchProfileAsync(p);
            _profiles.DropDownItems.Add(item);
        }
        Theme.StyleDropDown(_menu);
        StateChanged?.Invoke();
    }

    void SetBusy(bool b) { _busy = b; RefreshMenu(); }

    // ───────────────────────── мелочи UI ─────────────────────────

    /// <summary>Уведомление: короткий заголовок (что случилось) + пояснение. Имя программы Windows покажет само.</summary>
    // Информационные — без стандартного синего «i» (заголовок и так говорит, что это TunPilot);
    // предупреждения и ошибки сохраняют свои значки.
    internal void Balloon(string title, string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(4000, Truncate(title, 63), Truncate(text, 250), icon == ToolTipIcon.Info ? ToolTipIcon.None : icon);

    internal void Balloon(string text, ToolTipIcon icon) => Balloon(icon switch
    {
        ToolTipIcon.Warning => L("Внимание"),
        ToolTipIcon.Error => L("Ошибка"),
        _ => "",   // заголовок «TunPilot» Windows и так показывает над уведомлением
    }, text, icon);
    internal static void ShowError(string text) => MessageBox.Show(Truncate(text, 3000), "TunPilot", MessageBoxButtons.OK, MessageBoxIcon.Error);
    internal static bool Confirm(string text) => MessageBox.Show(text, "TunPilot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    internal static string? Prompt(string label, string value, bool multiline = false, bool withFile = false)
    {
        using var f = new Form
        {
            Text = "TunPilot", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, TopMost = true,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = new Font("Segoe UI", 9.5f),
        };
        Theme.DarkTitle(f);
        var lbl = new Label { Text = label, Left = 12, Top = 12, AutoSize = true, MaximumSize = new Size(536, 0) };
        f.Controls.Add(lbl);
        int top = lbl.PreferredHeight + 20;
        f.ClientSize = new Size(560, top + (multiline ? 210 : 24) + 60);
        var box = new TextBox
        {
            Text = value, Left = 12, Top = top, Width = 536, Multiline = multiline, Height = multiline ? 210 : 24,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, BackColor = Theme.Panel, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 392, Top = f.ClientSize.Height - 38, Width = 75 };
        var cancel = new Button { Text = L("Отмена"), DialogResult = DialogResult.Cancel, Left = 473, Top = f.ClientSize.Height - 38, Width = 75 };
        Theme.StyleButton(ok); Theme.StyleButton(cancel);
        if (multiline) Theme.DarkScroll(box);
        f.Controls.AddRange([box, ok, cancel]);
        if (withFile)
        {
            var file = new Button { Text = L("Файл…"), Left = 12, Top = ok.Top, Width = 90 };
            Theme.StyleButton(file);
            file.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = L("Настройки (*.json;*.yaml;*.yml;*.txt;*.conf)|*.json;*.yaml;*.yml;*.txt;*.conf|Все файлы|*.*") };
                if (dlg.ShowDialog(f) == DialogResult.OK) box.Text = File.ReadAllText(dlg.FileName).Replace("\r\n", "\n").Replace("\n", "\r\n");
            };
            f.Controls.Add(file);
        }
        if (!multiline) f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}

/// <summary>Глобальная горячая клавиша (RegisterHotKey) — работает из любой программы.</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, Id = 0x7A11;
    readonly Action _onPress;
    bool _registered;

    public HotkeyWindow(Action onPress) { _onPress = onPress; CreateHandle(new CreateParams()); }

    public bool Register(string shortcut)
    {
        if (_registered) { UnregisterHotKey(Handle, Id); _registered = false; }
        if (!TryParse(shortcut, out var mods, out var key)) return false;
        _registered = RegisterHotKey(Handle, Id, mods | 0x4000 /* MOD_NOREPEAT */, (uint)key);
        return _registered;
    }

    /// <summary>"Ctrl+Alt+V" → модификаторы и клавиша.</summary>
    public static bool TryParse(string s, out uint mods, out Keys key)
    {
        mods = 0; key = Keys.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default: if (!Enum.TryParse(part, true, out key)) return false; break;
            }
        }
        return key != Keys.None && mods != 0;
    }

    public static string Format(Keys keyData)
    {
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        var k = keyData & Keys.KeyCode;
        if (k is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.None) return "";
        parts.Add(k.ToString());
        return string.Join("+", parts);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) _onPress();
        base.WndProc(ref m);
    }

    public void Dispose() { if (_registered) UnregisterHotKey(Handle, Id); DestroyHandle(); }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
