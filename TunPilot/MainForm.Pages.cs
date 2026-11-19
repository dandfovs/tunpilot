using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TunPilot;

/// <summary>
/// Разделы главного окна: боковое меню (Главная / Серверы / Маршруты / Трафик / Настройки)
/// и простой главный экран — большая кнопка, текущий сервер, быстрые переключатели, подписка.
/// </summary>
public sealed partial class MainForm
{
    readonly Panel _nav = new() { Dock = DockStyle.Left, Width = 86 };
    readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    readonly List<NavButton> _navButtons = [];
    readonly Dictionary<NavButton, Control> _pageOf = [];
    NavButton _navHome = null!, _navServers = null!, _navRoutes = null!, _navTraffic = null!, _navSettings = null!;
    readonly Panel _settingsHost = new() { Dock = DockStyle.Fill };
    SettingsForm? _settingsView;

    // главный экран
    readonly ConnectButton _bigConnect = new();
    readonly Label _homeHint = new() { AutoSize = false, Height = 22, TextAlign = ContentAlignment.MiddleCenter };
    readonly Label _homeSpeed = new() { AutoSize = false, Height = 22, TextAlign = ContentAlignment.MiddleCenter };
    readonly ServerCard _card = new();


    readonly Tile _tileProfile = new(), _tileKill = new(), _tileDota = new(), _tileLists = new();
    readonly Label _homeSubLeft = new() { AutoSize = false, AutoEllipsis = true }, _homeSubRight = new() { AutoSize = true };
    readonly ProgressLine _homeSubBar = new() { Height = 5 };
    readonly Label _homeAnnounce = new() { AutoSize = false, AutoEllipsis = true };
    Panel _homeColumn = null!, _homeSubPanel = null!;

    // маршруты
    readonly FlowLayoutPanel _routesBody = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(24, 18, 24, 18),
    };

    const int ColumnWidth = 460;
    ServerPicker? _picker;
    DateTime _pickerClosedAt;
    bool _routesRebuildQueued;

    /// <summary>
    /// Перестроить «Маршруты» после текущего события. Сразу нельзя: если это вызвано кликом по переключателю
    /// на этой же странице, Windows ещё перебирает его соседей — удаление контролов даёт
    /// «Collection was modified».
    /// </summary>
    void QueueRebuildRoutes()
    {
        if (_routesRebuildQueued || IsDisposed) return;
        _routesRebuildQueued = true;
        BeginInvoke(() => { _routesRebuildQueued = false; if (!IsDisposed) RebuildRoutes(); });
    }

    // ───────────────────────── каркас ─────────────────────────

    void BuildShell()
    {
        _nav.BackColor = Theme.Panel;
        _nav.Padding = new Padding(0, 8, 0, 8);
        _navHome = Nav(L("Главная"), Icons.Home);
        _navServers = Nav(L("Серверы"), Icons.Server);
        _navRoutes = Nav(L("Маршруты"), Icons.Routes);
        _navTraffic = Nav(L("Трафик"), Icons.Chart);
        _navSettings = new NavButton(L("Настройки"), Icons.Settings) { Dock = DockStyle.Bottom };
        _navSettings.Click += (_, _) => ShowPage(_navSettings);
        _nav.Controls.Add(_navSettings);
        // Dock=Top складывает снизу вверх — добавляем в обратном порядке
        foreach (var b in _navButtons.AsEnumerable().Reverse()) _nav.Controls.Add(b);

        _pageOf[_navHome] = BuildHomePage();
        _pageOf[_navServers] = BuildServersPage();
        _pageOf[_navRoutes] = BuildRoutesPage();
        _pageOf[_navTraffic] = BuildTrafficPage();
        _pageOf[_navSettings] = _settingsHost;   // наполняется при первом открытии (не тормозим старт окна)
    }

    NavButton Nav(string text, char icon)
    {
        var b = new NavButton(text, icon) { Dock = DockStyle.Top };
        b.Click += (_, _) => ShowPage(b);
        _navButtons.Add(b);
        return b;
    }

    internal void ShowSettingsPage() => ShowPage(_navSettings);
    internal void ShowSettingsTab(int i) { ShowPage(_navSettings); _settingsView?.SelectPage(i); }

    void ShowPage(NavButton b)
    {
        foreach (var n in _navButtons) n.Selected = n == b;
        _navSettings.Selected = b == _navSettings;
        if (b == _navSettings)
        {
            if (_settingsView == null || _settingsView.IsDisposed)
            {
                _settingsView = new SettingsForm(_app, embedded: true);
                _settingsHost.Controls.Add(_settingsView);
                _settingsView.Show();
            }
            else _settingsView.ReloadValues();
        }
        var page = _pageOf[b];
        if (_pageHost.Controls.Count == 1 && _pageHost.Controls[0] == page) return;
        _pageHost.SuspendLayout();
        _pageHost.Controls.Clear();
        _pageHost.Controls.Add(page);
        _pageHost.ResumeLayout();
        if (b == _navRoutes) RebuildRoutes();
        if (b == _navHome) RefreshHome();
    }

    internal void ShowServersPage() => ShowPage(_navServers);
    internal void PreviewSession() { ShowPage(_navTraffic); SelectTab(_tabStats); }
    /// <summary>Для скриншотов с демо-данными: правдоподобные результаты тестов.</summary>
    internal void PreviewDemo()
    {
        var rnd = new Random(7);
        foreach (var s in _app.AllServers) Delay[s.Tag] = rnd.Next(35, 190);
        foreach (var s in _app.AllServers.Take(4)) Speed[s.Tag] = Math.Round(40 + rnd.NextDouble() * 160, 1);
        RefreshServers();
        RefreshHome();
    }
    /// <summary>Для снимка при разработке: как выглядит кнопка в состоянии «подключено».</summary>
    internal void PreviewConnected() { _bigConnect.State = ConnectState.On; _bigConnect.Detail = L("в сети 00:12:34"); }
    /// <summary>Для снимка при разработке: список выбора сервера.</summary>
    internal Form PreviewPicker() => new ServerPicker(this, _card);
    internal void ShowPageIndex(int i) => ShowPage(i >= _navButtons.Count ? _navSettings : _navButtons[Math.Max(0, i)]);

    Control BuildServersPage()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var grid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        grid.Controls.Add(_servers);
        page.Controls.Add(grid);
        page.Controls.Add(_subBar);
        page.Controls.Add(_groupTabs);
        page.Controls.Add(_toolbar);
        return page;
    }

    Control BuildTrafficPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        var stats = new ToolStripButton(L("Статистика за период…"), Icons.Get(Icons.History)) { Alignment = ToolStripItemAlignment.Right };
        stats.Click += (_, _) => _app.ShowStats();
        _tabs.Items.Add(stats);
        page.Controls.Add(_tabHost);
        page.Controls.Add(_tabs);
        return page;
    }

    // ───────────────────────── главная ─────────────────────────

    Control BuildHomePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _homeColumn = new Panel { Width = ColumnWidth };

        int y = 0;
        _bigConnect.Location = new Point((ColumnWidth - _bigConnect.Width) / 2, y);
        _bigConnect.Click += async (_, _) =>
        {
            if (_app.Busy) return;
            if (_app.Core.Running) await _app.DisconnectAsync(); else await _app.ConnectAsync();
        };
        y += _bigConnect.Height + 10;
        _homeSpeed.SetBounds(0, y, ColumnWidth, 22); y += 22;
        _homeHint.SetBounds(0, y, ColumnWidth, 22); y += 34;

        // карточка сервера: вся карточка — кнопка, справа стрелка (поворачивается, пока открыт список)
        _card.SetBounds(0, y, ColumnWidth, 62);
        _card.Click += (_, _) => ShowServerMenu(_card);
        y += 62 + 12;

        // быстрые переключатели 2×2
        int tw = (ColumnWidth - 10) / 2;
        _tileProfile.Setup(Icons.Routes, "", TileKind.Menu);
        _tileKill.Setup(Icons.Shield, "Kill-switch", TileKind.Toggle);
        _tileDota.Setup(Icons.Steam, L("Dota мимо VPN"), TileKind.Toggle);
        _tileLists.Setup(Icons.List, L("Мои исключения"), TileKind.Link);
        _tileProfile.SetBounds(0, y, tw, 44); _tileKill.SetBounds(tw + 10, y, tw, 44);
        _tileDota.SetBounds(0, y + 54, tw, 44); _tileLists.SetBounds(tw + 10, y + 54, tw, 44);
        _tileProfile.Activated += () => ShowProfileMenu(_tileProfile);
        _tileKill.Activated += () => _app.SetKillSwitch(!_app.Settings.KillSwitch);
        _tileDota.Activated += async () => await _app.SetSteamBypassAsync(!_app.Settings.SteamSdrBypass);
        _tileLists.Activated += () => _app.OpenEditor();
        var tips = Theme.Tip();
        tips.SetToolTip(_tileKill, KillSwitch.Help);
        tips.SetToolTip(_tileDota, L("Игровой трафик Dota 2 (ретрансляторы Steam) идёт напрямую, мимо VPN — ниже пинг.\nВключается и выключается без переподключения."));
        tips.SetToolTip(_tileLists, L("Сайты и подсети: напрямую, через VPN или мимо TUN.\nИзменения применяются без переподключения."));
        tips.SetToolTip(_tileProfile, L("Профиль маршрутов: какие сайты идут через VPN, а какие напрямую."));
        y += 108 + 14;

        // подписка
        _homeSubPanel = new Panel { Bounds = new Rectangle(0, y, ColumnWidth, 62) };
        _homeSubLeft.SetBounds(0, 0, ColumnWidth - 140, 20);
        _homeSubRight.Location = new Point(ColumnWidth - 120, 0);
        _homeSubBar.SetBounds(0, 24, ColumnWidth, 5);
        _homeAnnounce.SetBounds(0, 36, ColumnWidth, 20);
        _homeSubPanel.Controls.AddRange([_homeSubLeft, _homeSubRight, _homeSubBar, _homeAnnounce]);
        _homeSubPanel.Layout += (_, _) =>
        {
            _homeSubRight.Left = ColumnWidth - _homeSubRight.Width;
            _homeSubLeft.Width = ColumnWidth - (_homeSubRight.Text.Length > 0 ? _homeSubRight.Width + 12 : 0);
        };
        y += 62;

        _homeColumn.Height = y;
        _homeColumn.Controls.AddRange([_bigConnect, _homeSpeed, _homeHint, _card, _tileProfile, _tileKill, _tileDota, _tileLists, _homeSubPanel]);
        page.Controls.Add(_homeColumn);
        page.Resize += (_, _) =>
        {
            _homeColumn.Left = Math.Max(16, (page.ClientSize.Width - ColumnWidth) / 2);
            _homeColumn.Top = Math.Max(16, (page.ClientSize.Height - _homeColumn.Height) / 3);
        };

        _homeSpeed.ForeColor = _homeHint.ForeColor = _homeSubLeft.ForeColor = _homeSubRight.ForeColor = Theme.Muted;

        _homeAnnounce.ForeColor = Theme.Direct;

        return page;
    }

    void RefreshHome()
    {
        if (_homeColumn == null) return;
        bool on = _app.Core.Running, busy = _app.Busy;
        _bigConnect.State = busy ? ConnectState.Busy : on ? ConnectState.On : ConnectState.Off;
        _bigConnect.Detail = on ? L("в сети ") + (DateTime.Now - _app.ConnectedAt).ToString(@"hh\:mm\:ss") : busy ? L("это займёт пару секунд") : L("нажмите, чтобы подключиться");
        _bigConnect.Invalidate();

        var s = _app.Monitor.Last;
        _homeSpeed.Text = on ? $"↓ {SpeedChart.Rate(s?.ProxyDown ?? 0)}   ↑ {SpeedChart.Rate(s?.ProxyUp ?? 0)}" : "";
        _homeHint.Text = _app.PendingSubUpdate ? L("Подписка обновилась — изменения применятся при переподключении")
            : on && _app.RoutesInfo.Length > 0 ? L("Игры и исключения: ") + _app.RoutesInfo : "";
        _homeHint.ForeColor = _app.PendingSubUpdate ? Theme.Warn : Theme.Muted;

        // текущий сервер
        var key = _app.Settings.SelectedServer;
        var g = _app.ActiveGroup;
        if (key.StartsWith("auto:"))
        {
            var ag = _app.Groups.Find(key[5..]) ?? g;
            var nowTag = ag != null ? _app.Monitor.AutoNow.GetValueOrDefault(ag.AutoTag) : null;
            var now = nowTag != null ? _app.AllServers.FirstOrDefault(x => x.Tag == nowTag) : null;
            _card.Badge = Icons.Badge("AUTO", Theme.Warn);
            _card.Title = L("Авто — самый быстрый");
            _card.Sub = $"{ag?.Name ?? "—"}" + (now != null ? Lf($" · сейчас {now.Name}") + DelayText(now.Tag) : "");
        }
        else if (_app.AllServers.FirstOrDefault(x => x.Key == key) is { } srv)
        {
            var (text, color) = Icons.ProtocolOf(srv);
            _card.Badge = Icons.Badge(text, color);
            _card.Title = srv.Name;
            _card.Sub = (_app.Groups.Find(srv.GroupId)?.Name ?? "") + DelayText(srv.Tag);
        }
        else
        {
            _card.Badge = null;
            _card.Title = _app.AllServers.Count == 0 ? L("Нет серверов") : L("Сервер не выбран");
            _card.Sub = _app.AllServers.Count == 0 ? L("Добавьте подписку: Серверы → Группы") : L("Нажмите «Сменить»");
        }

        _card.Invalidate();
        _tileProfile.Title = Profiles.Current.Name;
        _tileKill.Checked = _app.Settings.KillSwitch;
        _tileDota.Checked = _app.Settings.SteamSdrBypass;
        foreach (var t in new[] { _tileProfile, _tileKill, _tileDota, _tileLists }) t.Invalidate();

        // подписка
        var sg = g is { IsSubscription: true, Info: not null } ? g : _app.Groups.Groups.FirstOrDefault(x => x is { IsSubscription: true, Info: not null });
        _homeSubPanel.Visible = sg != null;
        if (sg?.Info is { } i)
        {
            long used = i.Upload + i.Download;
            _homeSubLeft.Text = $"{(i.Title.Length > 0 ? i.Title : sg.Name)} · " +
                (i.Total > 0 ? Lf($"осталось {Fmt(Math.Max(0, i.Total - used))} из {Fmt(i.Total)}") : Lf($"использовано {Fmt(used)}, без лимита"));
            bool expired = i.Expire > 0 && DateTimeOffset.FromUnixTimeSeconds(i.Expire) < DateTimeOffset.Now;
            _homeSubRight.Text = i.Expire > 0 ? (expired ? L("истекла ") : L("до ")) + DateTimeOffset.FromUnixTimeSeconds(i.Expire).LocalDateTime.ToString("dd.MM.yyyy") : "";
            _homeSubRight.ForeColor = expired ? Theme.Danger : Theme.Muted;
            _homeSubBar.Unlimited = i.Total <= 0;   // без лимита — полоса целиком зелёная, как на вкладке «Серверы»
            _homeSubBar.Fraction = i.Total > 0 ? Math.Min(1, used / (double)i.Total) : 1;
            _homeSubBar.Invalidate();
            _homeAnnounce.Text = i.Announce;
            _homeAnnounce.Visible = i.Announce.Length > 0;
            _homeSubPanel.PerformLayout();
        }
    }

    static string DelayText(string tag) =>
        Delay.TryGetValue(tag, out var d) ? (d is { } ms ? Lf($" · {ms} мс") : L(" · таймаут")) : "";

    void ShowServerMenu(Control anchor)
    {
        // Повторный клик по карточке закрывает список. Клик мимо закрывает его раньше (уход фокуса),
        // поэтому «только что закрылся» тоже считаем закрытием, а не поводом открыть снова.
        if (_picker is { IsDisposed: false }) { _picker.Close(); return; }
        if ((DateTime.Now - _pickerClosedAt).TotalMilliseconds < 300) return;
        var picker = _picker = new ServerPicker(this, anchor);
        _card.Open = true;
        picker.FormClosed += (_, _) =>
        {
            _pickerClosedAt = DateTime.Now;
            _picker = null;
            if (!_card.IsDisposed) _card.Open = false;
        };
        picker.Show(this);
    }

    /// <summary>Прежнее меню выбора (оставлено на случай, если понадобится компактный вариант).</summary>
    void ShowServerContextMenu(Control anchor)
    {
        var m = new ContextMenuStrip { Renderer = new Theme.Renderer(), ShowCheckMargin = true, ShowImageMargin = false };
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        var g = _app.ActiveGroup;
        if (_app.Groups.Groups.Count > 1)
        {
            var groups = new ToolStripMenuItem(Lf($"Группа: {g?.Name}"));
            foreach (var grp in _app.Groups.Groups)
            {
                var gi = new ToolStripMenuItem($"{grp.Name} ({_app.ServersOf(grp).Count})") { Checked = grp.Id == g?.Id };
                gi.Click += (_, _) => { _app.SetActiveGroup(grp); BeginInvoke(() => ShowServerMenu(anchor)); };
                groups.DropDownItems.Add(gi);
            }
            m.Items.Add(groups);
            m.Items.Add(new ToolStripSeparator());
        }
        if (g != null)
        {
            var auto = new ToolStripMenuItem(L("Авто — самый быстрый")) { Checked = _app.Settings.SelectedServer == g.AutoKey };
            auto.Click += async (_, _) => await _app.SelectServerAsync(g.AutoKey);
            m.Items.Add(auto);
            foreach (var s in _app.ServersOf(g))
            {
                var key = s.Key;
                var it = new ToolStripMenuItem(s.Name) { Checked = _app.Settings.SelectedServer == key, ShortcutKeyDisplayString = DelayText(s.Tag).TrimStart(' ', '·', ' ') };
                it.Click += async (_, _) => await _app.SelectServerAsync(key);
                m.Items.Add(it);
            }
        }
        m.Items.Add(new ToolStripSeparator());
        var test = new ToolStripMenuItem(L("Проверить задержку"));
        test.Click += async (_, _) => await TestGroupAsync();
        var all = new ToolStripMenuItem(L("Все серверы и тесты…"));
        all.Click += (_, _) => ShowPage(_navServers);
        m.Items.AddRange([test, all]);
        Theme.StyleDropDown(m);
        m.MaximumSize = new Size(560, Screen.FromControl(this).WorkingArea.Height - 80);
        m.Show(anchor, new Point(0, anchor.Height));
    }

    void ShowProfileMenu(Control anchor)
    {
        var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        foreach (var p in Profiles.All())
        {
            var it = new ToolStripMenuItem(p.Name, Icons.Get(Icons.Profile)) { Checked = p.Name == Profiles.Current.Name };
            it.Click += async (_, _) => await _app.SwitchProfileAsync(p);
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        var manage = new ToolStripMenuItem(L("Настроить маршруты…"), Icons.Get(Icons.Settings));
        manage.Click += (_, _) => ShowPage(_navRoutes);
        m.Items.Add(manage);
        Theme.StyleDropDown(m);
        m.Show(anchor, new Point(0, anchor.Height));
    }

    // ───────────────────────── маршруты ─────────────────────────

    Control BuildRoutesPage()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        Theme.DarkScroll(_routesBody);
        // При изменении ширины только подгоняем ширину рядов кнопок — без пересоздания страницы (иначе мигает).
        _routesBody.Resize += (_, _) =>
        {
            int w = Math.Max(480, _routesBody.ClientSize.Width - _routesBody.Padding.Horizontal - 8);
            _routesBody.SuspendLayout();
            foreach (Control c in _routesBody.Controls)
                if (c is FlowLayoutPanel row && row.MaximumSize.Width != w) row.MaximumSize = new Size(w, 0);
            _routesBody.ResumeLayout();
        };
        page.Controls.Add(_routesBody);
        return page;
    }

    /// <summary>Строка «Напрямую: [geoip-ru ×] [geosite-… ×] [+ Добавить]».</summary>
    Control GeoBlock(string title, List<string> list, List<string> other, Color color)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = true, Margin = new Padding(3, 2, 0, 6),
            MaximumSize = new Size(Math.Max(480, _routesBody.ClientSize.Width - _routesBody.Padding.Horizontal - 8), 0),
        };
        row.Controls.Add(new Label { Text = title + ":", AutoSize = true, ForeColor = color, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 7, 8, 0), MinimumSize = new Size(86, 0) });
        var tips = Theme.Tip();
        foreach (var name in list.ToList())
        {
            var chip = new Button
            {
                Text = name + "   ✕", AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = Theme.Header, ForeColor = Theme.Text, Margin = new Padding(0, 2, 6, 2), Padding = new Padding(2, 0, 2, 0),
            };
            chip.FlatAppearance.BorderColor = Theme.Blend(Theme.Grid, color, 0.5f);
            chip.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Header, Theme.Danger, 0.25f);
            var desc = GeoRuleSets.TitleOf(name);
            tips.SetToolTip(chip, (desc.Length > 0 ? desc + "\n" : "") + L("Нажмите, чтобы убрать"));
            chip.Click += async (_, _) => { list.Remove(name); await _app.GeoChangedAsync(); QueueRebuildRoutes(); };
            row.Controls.Add(chip);
        }
        var add = new Button { Text = L("+ Добавить"), AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        Theme.StyleButton(add);
        add.Click += (_, _) =>
        {
            var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
            m.Closed += (_, _) => BeginInvoke(m.Dispose);
            async Task AddSet(string n)
            {
                n = n.Trim().ToLowerInvariant().Replace(':', '-');
                if (n.Length == 0 || list.Contains(n)) return;
                if (!n.StartsWith("geoip-") && !n.StartsWith("geosite-")) n = "geosite-" + n;
                other.Remove(n);   // один набор — только в одном списке
                list.Add(n);
                var missing = await _app.GeoChangedAsync();
                if (missing.Contains(n))
                {
                    list.Remove(n);
                    await _app.GeoChangedAsync();
                    TrayApp.ShowError(Lf($"Набор «{n}» не найден в репозитории наборов.\nПроверьте название (например geosite-youtube, geoip-ru)."));
                }
                QueueRebuildRoutes();
            }
            foreach (var (n, t) in GeoRuleSets.Popular.Where(x => !list.Contains(x.Name)))
            {
                var it = new ToolStripMenuItem(t) { ShortcutKeyDisplayString = n };
                it.Click += async (_, _) => await AddSet(n);
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            var custom = new ToolStripMenuItem(L("Другой набор по названию…"), Icons.Get(Icons.Edit));
            custom.Click += async (_, _) =>
            {
                if (TrayApp.Prompt(L("Название набора (как в Throne/v2rayN), например geosite-netflix или geoip-ru:"), "") is { Length: > 0 } n)
                    await AddSet(n);
            };
            m.Items.Add(custom);
            Theme.StyleDropDown(m);
            m.Show(add, new Point(0, add.Height));
        };
        row.Controls.Add(add);
        if (list.Count == 0) row.Controls.Add(new Label { Text = L("нет"), AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(8, 7, 0, 0) });
        return row;
    }

    void RebuildRoutes()
    {
        var p = Profiles.Current;
        _routesBody.SuspendLayout();
        _routesBody.Controls.Clear();

        Label H(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 14, 0, 6) };
        Label Hint(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 8) };
        Button B(string t, char icon, Func<Task> run)
        {
            var b = new Button
            {
                Text = "  " + t, Image = Icons.Get(icon), TextImageRelation = TextImageRelation.ImageBeforeText,
                AutoSize = true, Padding = new Padding(6, 0, 8, 0), Margin = new Padding(0, 0, 8, 0),
            };
            Theme.StyleButton(b);
            b.Click += async (_, _) => { try { await run(); } catch (Exception ex) { TrayApp.ShowError(ex.Message); } QueueRebuildRoutes(); };
            return b;
        }
        FlowLayoutPanel Row(params Control[] cs)
        {
            // ширина ряда — по окну: кнопки переносятся, только когда реально не влезают
            var r = new FlowLayoutPanel
            {
                AutoSize = true, WrapContents = true, Margin = new Padding(3, 2, 0, 4),
                MaximumSize = new Size(Math.Max(480, _routesBody.ClientSize.Width - _routesBody.Padding.Horizontal - 8), 0),
            };
            r.Controls.AddRange(cs);
            return r;
        }

        _routesBody.Controls.Add(H(L("Профиль")));
        _routesBody.Controls.Add(Hint(L("Профиль — набор правил: какие сайты идут через VPN, а какие напрямую. Переключение обычно проходит без переподключения.")));
        foreach (var prof in Profiles.All())
        {
            var rb = new RadioButton
            {
                Text = prof.Name, Checked = prof.Name == p.Name, AutoSize = true, ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 2),
            };
            rb.CheckedChanged += async (_, _) => { if (rb.Checked && prof.Name != Profiles.Current.Name) { await _app.SwitchProfileAsync(prof); QueueRebuildRoutes(); } };
            _routesBody.Controls.Add(rb);
        }
        _routesBody.Controls.Add(Row(
            B(L("Новый"), Icons.Add, async () =>
            {
                if (TrayApp.Prompt(L("Название нового профиля:"), "") is { Length: > 0 } n) await _app.SwitchProfileAsync(Profiles.Create(n.Trim()));
            }),
            B(L("Копия текущего"), Icons.Copy, async () =>
            {
                if (TrayApp.Prompt(L("Название копии:"), p.Name + L(" (копия)")) is { Length: > 0 } n) await _app.SwitchProfileAsync(Profiles.Clone(p, n.Trim()));
            }),
            B(L("Переименовать"), Icons.Edit, () =>
            {
                if (TrayApp.Prompt(L("Новое название:"), p.Name) is { Length: > 0 } n && Profiles.Find(n.Trim()) == null)
                {
                    p.Name = n.Trim(); p.Save(); _app.Settings.ActiveProfile = p.Name; _app.Settings.Save(); _app.RefreshMenu();
                }
                return Task.CompletedTask;
            }),
            B(L("Удалить"), Icons.Delete, async () =>
            {
                var others = Profiles.All().Where(x => x.Name != p.Name).ToList();
                if (others.Count == 0) { TrayApp.ShowError(L("Нельзя удалить единственный профиль.")); return; }
                if (!TrayApp.Confirm(Lf($"Удалить профиль «{p.Name}» со всеми его списками?"))) return;
                await _app.SwitchProfileAsync(others[0]);
                Profiles.Delete(p);
                _app.RefreshMenu();
            }),
            B(L("Импорт из другого клиента…"), Icons.Import, () => _app.ImportThroneAsync())));

        _routesBody.Controls.Add(H(L("Готовые наборы (geoip / geosite)")));
        _routesBody.Controls.Add(Hint(L("Большие готовые списки: российские сайты и IP, YouTube, Telegram и другие. ") +
                                      L("Обновляются автоматически раз в сутки. Добавить или убрать можно без переподключения.")));
        _routesBody.Controls.Add(GeoBlock(L("Напрямую"), p.GeoDirect, p.GeoProxy, Theme.Direct));
        _routesBody.Controls.Add(GeoBlock(L("Через VPN"), p.GeoProxy, p.GeoDirect, Theme.Accent));

        _routesBody.Controls.Add(H(L("Списки сайтов и подсетей")));
        _routesBody.Controls.Add(Hint(L("Напрямую, через VPN и мимо TUN (для игр). Изменения применяются сразу, без переподключения.")));
        _routesBody.Controls.Add(Row(B(L("Открыть списки"), Icons.List, () => { _app.OpenEditor(); return Task.CompletedTask; })));

        _routesBody.Controls.Add(H(L("Всё остальное")));
        _routesBody.Controls.Add(Hint(L("Куда идут сайты, которых нет в списках. Смена требует переподключения (программа спросит).")));
        var viaVpn = new RadioButton { Text = L("через VPN"), Checked = p.DefaultOutbound != "direct", AutoSize = true, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 2) };
        var direct = new RadioButton { Text = L("напрямую"), Checked = p.DefaultOutbound == "direct", AutoSize = true, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 2) };
        viaVpn.CheckedChanged += async (_, _) => { if (viaVpn.Checked) await _app.SetDefaultRouteAsync("proxy"); };
        direct.CheckedChanged += async (_, _) => { if (direct.Checked) await _app.SetDefaultRouteAsync("direct"); };
        _routesBody.Controls.Add(Row(viaVpn, direct));

        _routesBody.Controls.Add(H(L("Игры")));
        var steam = new CheckBox { Text = L("Ретрансляторы Steam (Dota 2) мимо VPN"), Checked = _app.Settings.SteamSdrBypass, AutoSize = true, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 2) };
        steam.CheckedChanged += async (_, _) => await _app.SetSteamBypassAsync(steam.Checked);
        _routesBody.Controls.Add(steam);
        _routesBody.Controls.Add(Hint(L("Игровой трафик идёт напрямую — ниже пинг. Включается без переподключения.")));

        _routesBody.ResumeLayout();
    }

}
