namespace TunPilot;

/// <summary>
/// «Мозг» TunPilot без интерфейса: состояние, подключение, выбор сервера, подписки и группы, профили,
/// исключения, kill-switch, обновление ядер. Общий для всех ОС; всё платформенное — через <see cref="IPlatform"/>.
/// Создавать в UI-потоке: события и таймеры выполняются в его контексте синхронизации.
/// </summary>
public sealed class AppController : IAppState, IDisposable
{
    readonly IPlatform _p;
    readonly CoreManager _core = new();
    readonly Settings _settings = Settings.Load();
    readonly GroupStore _groups;
    readonly SynchronizationContext? _ui;
    readonly System.Threading.Timer _subTimer;
    List<ServerInfo> _all = [];
    List<string> _bypassCidrs = [];
    bool _busy, _pending, _ksOn;
    string _routesInfo = "";

    public Settings Settings => _settings;
    public CoreManager Core => _core;
    public GroupStore Groups => _groups;
    public TrafficMonitor Monitor { get; }
    public IReadOnlyList<ServerInfo> AllServers => _all;
    public bool Busy => _busy;
    public string RoutesInfo => _routesInfo;
    public bool PendingSubUpdate => _pending;
    /// <summary>Фильтры kill-switch сейчас стоят в системе.</summary>
    public bool KillSwitchActive => _ksOn;
    public DateTime ConnectedAt { get; private set; }

    /// <summary>Что-то изменилось — оболочке пора обновить меню/окна (всегда в UI-потоке).</summary>
    public event Action? StateChanged;
    /// <summary>Групп нет (первый запуск) — оболочка показывает окно и предлагает добавить подписку.</summary>
    public event Action? FirstRun;

    public Group? ActiveGroup => _groups.Find(_settings.ActiveGroupId) ?? _groups.Groups.FirstOrDefault();
    public List<ServerInfo> ServersOf(Group g) => _all.Where(s => s.GroupId == g.Id).ToList();

    public AppController(IPlatform platform)
    {
        _p = platform;
        _ui = SynchronizationContext.Current;
        _groups = GroupStore.Load(_settings);
        Profiles.Init(_settings);
        CoreManager.Config = _settings;
        RebuildServers();
        Monitor = new TrafficMonitor(this);

        _core.UnexpectedExit += name => Post(async () =>
        {
            _p.Notify(L("Соединение прервалось"), Lf($"Ядро {name} завершилось с ошибкой. Подробности — в окне, вкладка «Логи»."), NotifyKind.Error);
            // Ядро упало — kill-switch НЕ снимаем: именно сейчас он и нужен.
            if (name == "sing-box") await DisconnectAsync(userAction: false, keepKillSwitch: true);
        });

        // подписки — проверка раз в 5 минут (каждая группа обновляется по своему интервалу)
        _subTimer = new System.Threading.Timer(_ => Post(async () => await UpdateAllAsync(manual: false)), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    void Post(Func<Task> work)
    {
        if (_ui != null) _ui.Post(async _ => await work(), null);
        else _ = work();
    }

    void Changed()
    {
        if (_ui != null && SynchronizationContext.Current != _ui) { _ui.Post(_ => StateChanged?.Invoke(), null); return; }
        StateChanged?.Invoke();
    }

    /// <summary>Запуск: уборка после сбоя, первые обновления подписок, автоподключение.</summary>
    public async Task InitAsync()
    {
        await _p.Bypass.CleanupLeftoversAsync();
        _ksOn = _p.KillSwitch.IsActive;   // осталась блокировка после падения программы?
        if (_p.Autostart.IsEnabled() && _p.Autostart.PointsElsewhere()) _p.Autostart.Enable();   // программу перенесли — обновим путь
        if (_groups.Groups.Count == 0)
        {
            Changed();
            FirstRun?.Invoke();
        }
        else
        {
            // группы без кэша (новые/после восстановления) — скачать сразу
            foreach (var g in _groups.Groups.Where(g => g.IsSubscription && g.Body.Length == 0))
                await UpdateGroupAsync(g, manual: false, force: true);
            await UpdateAllAsync(manual: false);
        }
        bool restore = _settings.RememberState && _settings.WasConnected;
        bool connect = (_settings.ConnectOnStart || restore) && _all.Count > 0;
        if (_ksOn && !_settings.KillSwitch) DisableKillSwitch();
        else if (_ksOn && !connect)
            _p.Notify(L("Внимание"), L("Интернет заблокирован kill-switch после аварийного завершения. Подключитесь или выберите в меню трея «Снять блокировку»."), NotifyKind.Warning);
        Changed();
        if (connect) await ConnectAsync();
    }

    void RebuildServers()
    {
        _all = _groups.Groups.SelectMany(g => g.ParseServers()).ToList();
        if (ActiveGroup is { } ag && _settings.ActiveGroupId != ag.Id) { _settings.ActiveGroupId = ag.Id; _settings.Save(); }
    }

    // ───────────────────────── выбор сервера ─────────────────────────

    /// <summary>Тег в ядре для сохранённого выбора.</summary>
    public string SelectedTag
    {
        get
        {
            var key = _settings.SelectedServer;
            if (key.StartsWith("auto:")) return "auto-" + key[5..];
            return _all.FirstOrDefault(s => s.Key == key)?.Tag ?? ActiveGroup?.AutoTag ?? "";
        }
    }

    public string SelectedDisplay
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

    public async Task SelectServerAsync(string key)
    {
        _settings.SelectedServer = key;
        _settings.Save();
        if (_core.Running && !await _core.SelectAsync(SelectedTag))
            _p.Notify("", L("Сервер появится после переподключения (изменилась подписка)."), NotifyKind.Info);
        Changed();
    }

    public void SetActiveGroup(Group g)
    {
        _settings.ActiveGroupId = g.Id;
        _settings.Save();
        Changed();
    }

    // ───────────────────────── подключение ─────────────────────────

    public async Task ConnectAsync()
    {
        if (_busy) return;
        if (_all.Count == 0) { _p.Notify(L("Внимание"), L("Нет серверов: добавьте подписку в «Группах»."), NotifyKind.Warning); return; }

        SetBusy(true);
        try
        {
            if (_pending) { RebuildServers(); _pending = false; }
            var err = await _core.PrepareAsync(_settings, _all, SelectedTag);
            if (err != null) { _p.ShowError(err); return; }
            _p.Bypass.ResetGateway();
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
            _p.Notify(L("Подключено"), string.Join("\n", lines), NotifyKind.Info);
        }
        catch (Exception ex) { _p.ShowError(ex.Message); await _p.Bypass.ClearAsync(); DisableKillSwitch(); }
        finally { SetBusy(false); }
    }

    /// <param name="userAction">Отключение пользователем (а не выход/сбой) — запоминаем «было выключено».</param>
    public async Task DisconnectAsync(bool userAction = true, bool keepKillSwitch = false)
    {
        if (userAction && _settings.WasConnected) { _settings.WasConnected = false; _settings.Save(); }
        _core.Stop();
        try { await _p.Bypass.ClearAsync(); } catch { }
        if (!keepKillSwitch) DisableKillSwitch();
        _routesInfo = "";
        Monitor.Stats.Save();
        Changed();
    }

    public async Task ReconnectAsync()
    {
        if (!_core.Running) { RebuildServers(); _pending = false; Changed(); return; }
        _core.Stop();
        await ConnectAsync();
    }

    /// <summary>Перезапуск с подтверждением (никогда не рвём соединения молча — вдруг идёт игра).</summary>
    public async Task AskReconnectAsync(string why)
    {
        if (!_core.Running) { RebuildServers(); _pending = false; Changed(); return; }
        if (await _p.ConfirmAsync(Lf($"{why}\nНужен перезапуск (~2 сек обрыва соединений через TUN). Перезапустить сейчас?")))
            await ReconnectAsync();
        else { _pending = true; Changed(); }
    }

    public async Task ApplyBypassAsync(bool quiet = false)
    {
        if (!_core.Running && !_busy) return;
        try
        {
            var cidrs = RuleLists.ReadBypassCidrs();
            if (_settings.SteamSdrBypass) cidrs.AddRange(await SteamSdr.GetCidrsAsync());
            _routesInfo = await _p.Bypass.ApplyAsync(cidrs);
            _bypassCidrs = cidrs;
            if (_ksOn) _p.KillSwitch.UpdateBypass(cidrs);   // на лету, без перезапуска
            if (!quiet) _p.Notify(L("Исключения применены"), _routesInfo + L(" — без переподключения."), NotifyKind.Info);
        }
        catch (Exception ex) { _p.Notify(L("Исключения не применились"), ex.Message, NotifyKind.Warning); }
        Changed();
    }

    // ───────────────────────── kill-switch ─────────────────────────

    /// <summary>Переключатель из настроек/трея: применяется сразу, без переподключения.</summary>
    public void SetKillSwitch(bool on)
    {
        _settings.KillSwitch = on; _settings.Save();
        if (on && _core.Running) EnableKillSwitch();
        else if (!on) DisableKillSwitch();
        if (on && _ksOn) _p.Notify("", L("Kill-switch включён: интернет только через VPN."), NotifyKind.Info);
        Changed();
    }

    void EnableKillSwitch()
    {
        try { _p.KillSwitch.Enable(_bypassCidrs); _ksOn = true; }
        catch (Exception ex) { _p.Notify(L("Внимание"), L("Kill-switch не включился: ") + ex.Message, NotifyKind.Warning); }
    }

    /// <summary>Снять блокировку kill-switch (в т. ч. оставшуюся после сбоя).</summary>
    public void DisableKillSwitch()
    {
        if (!_ksOn && !_p.KillSwitch.IsActive) return;
        try { _p.KillSwitch.Disable(); _ksOn = false; }
        catch (Exception ex) { _p.ShowError(L("Не удалось снять блокировку kill-switch:\n") + ex.Message + _p.KillSwitch.RecoveryHint); }
        Changed();
    }

    // ───────────────────────── подписки и группы ─────────────────────────

    public async Task UpdateAllAsync(bool manual)
    {
        foreach (var g in _groups.Groups.Where(g => g.IsSubscription).ToList())
            await UpdateGroupAsync(g, manual, force: manual);
    }

    /// <summary>Обновить подписку группы. force — игнорировать интервал.</summary>
    public async Task UpdateGroupAsync(Group g, bool manual, bool force = false)
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
                    _p.ShowInfo(L("TunPilot — изменения подписки"),
                        Lf($"«{g.Name}»: {fresh.Count} серверов.\n") +
                        (added.Count > 0 ? L("\nДобавлены:\n  ") + string.Join("\n  ", added) + "\n" : "") +
                        (removed.Count > 0 ? L("\nУдалены:\n  ") + string.Join("\n  ", removed) : ""));
                else _p.Notify("", Lf($"«{g.Name}»: {fresh.Count} серверов, без изменений."), NotifyKind.Info);
            }

            if (!changed) { Changed(); return; }
            if (!_core.Running) RebuildServers();
            else if (manual) await AskReconnectAsync(Lf($"Подписка «{g.Name}» изменилась."));
            else { _pending = true; _p.Notify("", Lf($"Подписка «{g.Name}» обновилась — применится при следующем подключении."), NotifyKind.Info); }
        }
        catch (Exception ex) { if (manual) _p.ShowError(Lf($"Не удалось обновить «{g.Name}»:\n{ex.Message}")); }
        Changed();
    }

    /// <summary>Группы изменены (добавлены/удалены/отредактированы).</summary>
    public async Task GroupsChangedAsync()
    {
        _groups.Save();
        foreach (var g in _groups.Groups.Where(g => g.IsSubscription && g.Body.Length == 0))
            await UpdateGroupAsync(g, manual: false, force: true);
        var before = string.Join(",", _all.Select(s => s.Tag + s.Raw.GetHashCode()));
        var after = string.Join(",", _groups.Groups.SelectMany(g => g.ParseServers()).Select(s => s.Tag + s.Raw.GetHashCode()));
        if (before == after) { Changed(); return; }
        if (_core.Running) await AskReconnectAsync(L("Список серверов изменился."));
        else { RebuildServers(); Changed(); }
    }

    /// <summary>Вставка ключей (vless://, hy2://…) в ручную группу «Ключи».</summary>
    public async Task<int> AddLinksAsync(string text)
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
    public async Task RemoveServersAsync(Group g, IEnumerable<ServerInfo> servers)
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

    public async Task<int> RemoveDuplicatesAsync(Group g)
    {
        var dups = ServersOf(g).GroupBy(s => s.Raw).SelectMany(x => x.Skip(1)).ToList();
        await RemoveServersAsync(g, dups);
        return dups.Count;
    }

    // ───────────────────────── маршрутизация ─────────────────────────

    public async Task SwitchProfileAsync(RoutingProfile p)
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
            else _p.Notify("", Lf($"Профиль «{p.Name}» применён без перезапуска."), NotifyKind.Info);
        }
        Changed();
    }

    /// <summary>Изменился состав готовых наборов текущего профиля — скачать и применить на лету.</summary>
    public async Task<List<string>> GeoChangedAsync()
    {
        var p = Profiles.Current;
        p.Save();
        var (_, missing) = await GeoRuleSets.EnsureAsync(_settings, p);
        if (missing.Count == 0 && _core.Running) _p.Notify(L("Наборы применены"), L("Без переподключения."), NotifyKind.Info);
        Changed();
        return missing;
    }

    public async Task SetDefaultRouteAsync(string key)
    {
        var p = Profiles.Current;
        if (p.DefaultOutbound == key) return;
        p.DefaultOutbound = key;
        p.Save();
        await AskReconnectAsync(L("«Всё остальное» изменилось."));
    }

    public async Task SetSteamBypassAsync(bool on)
    {
        _settings.SteamSdrBypass = on; _settings.Save();
        await ApplyBypassAsync();
    }

    /// <summary>Списки маршрутов отредактированы и сохранены.</summary>
    public async Task ListsChangedAsync(bool bypassChanged)
    {
        RuleLists.Compile();   // sing-box подхватит rule-set сам, без перезапуска
        if (bypassChanged) await ApplyBypassAsync();
        else if (_core.Running) _p.Notify("", L("Списки применены без перезапуска."), NotifyKind.Info);
    }

    /// <summary>Импорт профиля маршрутизации из другого клиента; текст уже введён пользователем.</summary>
    public async Task ImportRoutesAsync(string text)
    {
        if (text.Trim().Length == 0) return;
        try
        {
            var (profile, report) = await RouteImport.ApplyAsync(text);
            if (await _p.ConfirmAsync(report + Lf($"\n\nСделать профиль «{profile.Name}» активным?"))) await SwitchProfileAsync(profile);
        }
        catch (Exception ex) { _p.ShowError(L("Не удалось импортировать: ") + ex.Message); }
        Changed();
    }

    // ───────────────────────── ядра и настройки ─────────────────────────

    /// <summary>
    /// Обновить ядра. Сначала всё скачивается (VPN работает), затем ядро ненадолго останавливается,
    /// файлы заменяются, конфиг проверяется новыми ядрами; не принят — возвращаются прежние версии.
    /// Kill-switch на время замены не снимается. Возвращает итог для показа.
    /// </summary>
    public async Task<string> UpdateCoresAsync(List<CoreUpdater.Info> updates, Action<string> progress)
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
        Changed();
        return result;
    }

    public void ToggleAutostart()
    {
        if (_p.Autostart.IsEnabled()) _p.Autostart.Disable();
        else if (_p.Autostart.Enable() is { } err) _p.ShowError(L("Не удалось включить автозапуск:\n") + err);
        Changed();
    }

    /// <summary>Настройки сохранены; coreAffected — изменилось то, что требует перезапуска ядра.</summary>
    public async Task SettingsChangedAsync(bool coreAffected)
    {
        _settings.Save();
        CoreManager.Config = _settings;
        if (coreAffected) await AskReconnectAsync(L("Изменились настройки ядра (DNS, логи или локальный прокси)."));
        Changed();
    }

    /// <summary>Выход: отключиться (запомнив, было ли подключено), сохранить статистику, остановить ядра.</summary>
    public async Task ShutdownAsync()
    {
        await DisconnectAsync(userAction: false);
        Dispose();
    }

    void SetBusy(bool b) { _busy = b; Changed(); }

    public void Dispose()
    {
        _subTimer.Dispose();
        Monitor.Dispose();
        _core.Dispose();
    }
}
