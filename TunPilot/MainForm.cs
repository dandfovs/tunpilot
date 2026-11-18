using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TunPilot;

/// <summary>Главное окно: группы и серверы, тесты, подключения, график, логи. Закрытие прячет в трей.</summary>
public sealed partial class MainForm : Form
{
    readonly TrayApp _app;

    // верх
    readonly ToolStrip _toolbar = new();
    readonly ToolStripButton _btnConnect = new() { Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    readonly ToolStripButton _btnTest = new(L("Тест задержки"));
    readonly ToolStripButton _btnSpeed = new(L("Тест скорости"));
    readonly ToolStripDropDownButton _ddUpdate = new(L("Обновить"));
    readonly ToolStripButton _btnGroups = new(L("Группы"));
    readonly ToolStripDropDownButton _ddRoutes = new(L("Маршруты"));
    readonly ToolStripButton _btnStats = new(L("Статистика"));
    readonly ToolStripDropDownButton _ddMenu = new(L("Меню"));

    // группы и подписка
    readonly ToolStrip _groupTabs = new();
    readonly ToolStrip _subBar = new();
    readonly ToolStripLabel _subTitle = new() { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    readonly UsageBar _subUsage = new();
    readonly ToolStripLabel _subTraffic = new(), _subUpdate = new(), _subExpire = new(), _subAnnounce = new();
    readonly ToolStripButton _subPortal = new(L("Портал")) { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripButton _subSupport = new(L("Поддержка")) { Alignment = ToolStripItemAlignment.Right };

    // серверы
    readonly DataGridView _servers = MakeGrid();
    List<ServerInfo> _shown = [];   // серверы активной группы в порядке строк (после строки «Авто»)
    // результаты тестов переживают пересоздание окна (смена темы)
    static readonly Dictionary<string, int?> Delay = new();
    static readonly Dictionary<string, double?> Speed = new();
    readonly Dictionary<string, double> _speedLive = new();
    readonly HashSet<string> _testing = new(), _speedQueue = new();
    CancellationTokenSource? _speedCts;
    bool _offlineTesting;
    Font? _bold;

    // низ
    readonly ToolStrip _tabs = new();
    readonly Panel _tabHost = new() { Dock = DockStyle.Fill };
    readonly DataGridView _conns = MakeGrid();
    readonly ToolStripButton _tabConns = new(L("Подключения")), _tabGraph = new(L("График")), _tabLogs = new(L("Логи")), _tabStats = new(L("Сессия"));
    readonly ToolStripButton _groupToggle = new(L("По процессам")) { CheckOnClick = true, Checked = true };
    readonly SpeedChart _chart = new() { Dock = DockStyle.Fill };
    readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None,
    };
    readonly ToolStripComboBox _logPick = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly Label _stats = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10.5f), Padding = new Padding(14) };
    Control _connsPage = null!, _logsPage = null!;
    List<Row> _rows = [];
    int _tick;

    // статус
    readonly StatusStrip _status = new() { SizingGrip = false };
    readonly ToolStripStatusLabel _stServer = new(), _stProfile = new(), _stRoutes = new(), _stProxy = new(), _stDirect = new();

    record Row(string Process, string Dest, string Net, string Outbound, long Up, long Down, long SpUp, long SpDown, int Count);

    public MainForm(TrayApp app)
    {
        _app = app;
        Text = "TunPilot";
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1060, 720);
        MinimumSize = new Size(720, 600);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        Theme.DarkScroll(_logBox);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        BuildToolbar();
        BuildGroupTabs();
        BuildSubBar();
        BuildServers();
        BuildBottom();
        BuildStatus();

        BuildShell();
        Controls.Add(_pageHost);
        Controls.Add(_nav);
        Controls.Add(_status);
        Shown += (_, _) => { _servers.ClearSelection(); _servers.CurrentCell = null; };

        _app.StateChanged += OnStateChanged;
        _app.Monitor.Updated += OnTraffic;
        FormClosed += (_, _) => { _app.StateChanged -= OnStateChanged; _app.Monitor.Updated -= OnTraffic; };
        VisibleChanged += (_, _) => { if (Visible) OnStateChanged(); };
        OnStateChanged();
        SelectTab(_tabConns);
        ShowPage(_navHome);
    }

    // Пока тянут край окна — рисуем всё окно целиком в буфер (WS_EX_COMPOSITED): без мигания.
    // Постоянно не держим: с ним дороже обычные обновления (таблица серверов, график).
    const int GWL_EXSTYLE = -20, WS_EX_COMPOSITED = 0x02000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    void SetComposited(bool on)
    {
        if (!IsHandleCreated) return;
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        long want = on ? ex | WS_EX_COMPOSITED : ex & ~WS_EX_COMPOSITED;
        if (want != ex) SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(want));
    }

    protected override void OnResizeBegin(EventArgs e) { SetComposited(true); base.OnResizeBegin(e); }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SetComposited(false); Invalidate(true); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitle(this);
    }

    // ───────────────────────── построение ─────────────────────────

    static ToolStripMenuItem Mi(string text, char icon, EventHandler onClick, string? keys = null) =>
        new(text, Icons.Get(icon), onClick) { ShortcutKeyDisplayString = keys };

    void BuildToolbar()
    {
        _toolbar.Renderer = new Theme.Renderer();
        Theme.TipsFor(_toolbar);
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(8, 6, 8, 6);
        _toolbar.BackColor = Theme.Panel;
        _toolbar.ImageScalingSize = new Size(16, 16);

        _btnConnect.Padding = new Padding(8, 4, 10, 4);
        _btnConnect.Click += async (_, _) => { if (_app.Core.Running) await _app.DisconnectAsync(); else await _app.ConnectAsync(); };

        _btnTest.Image = Icons.Get(Icons.Timer);
        _btnTest.ToolTipText = L("Задержка всех серверов группы (Ctrl+Shift+G). Работает и без подключения.");
        _btnTest.Click += async (_, _) => await TestGroupAsync();

        _btnSpeed.Image = Icons.Get(Icons.Speed);
        _btnSpeed.ToolTipText = L("Скорость выделенного/текущего сервера (F6). Повторное нажатие — остановить.");
        _btnSpeed.Click += async (_, _) =>
        {
            if (_speedCts != null) { _speedCts.Cancel(); return; }
            await SpeedSelectedOrCurrentAsync();
        };

        _ddUpdate.Image = Icons.Get(Icons.Refresh);
        _ddUpdate.DropDownItems.AddRange([
            Mi(L("Обновить эту подписку"), Icons.Refresh, async (_, _) => await UpdateActiveAsync(), "Ctrl+U"),
            Mi(L("Обновить все подписки"), Icons.Refresh, async (_, _) => await _app.UpdateAllAsync(manual: true)),
        ]);

        _btnGroups.Image = Icons.Get(Icons.Group);
        _btnGroups.Click += (_, _) => _app.ShowGroups();

        _ddRoutes.Image = Icons.Get(Icons.Routes);
        _ddRoutes.DropDownOpening += (_, _) => BuildRoutesMenu();
        BuildRoutesMenu();

        _btnStats.Image = Icons.Get(Icons.Chart);
        _btnStats.Click += (_, _) => _app.ShowStats();

        _ddMenu.Image = Icons.Get(Icons.Settings);
        _ddMenu.Alignment = ToolStripItemAlignment.Right;
        _ddMenu.DropDownItems.AddRange([
            Mi(L("Настройки…"), Icons.Settings, (_, _) => _app.ShowSettings()),
            Mi(L("Группы и подписки…"), Icons.Group, (_, _) => _app.ShowGroups()),
            Mi(L("Статистика трафика…"), Icons.Chart, (_, _) => _app.ShowStats()),
            new ToolStripSeparator(),
            Mi(L("Вставить ключи из буфера"), Icons.Paste, async (_, _) => await PasteLinksAsync(), "Ctrl+V"),
            Mi(L("Папка с логами и настройками"), Icons.Folder, (_, _) => Process.Start("explorer.exe", AppPaths.Data)),
            new ToolStripSeparator(),
            Mi(L("Скрыть окно"), Icons.Hide, (_, _) => _app.HideWindow(), "Ctrl+H"),
            Mi(L("Выход"), Icons.Power, async (_, _) => await _app.ExitAsync()),
        ]);
        var ks = new ToolStripMenuItem("Kill-switch", Icons.Get(Icons.Shield)) { ToolTipText = KillSwitch.Help };
        ks.Click += (_, _) => _app.SetKillSwitch(!_app.Settings.KillSwitch);
        _ddMenu.DropDownItems.Insert(4, ks);
        _ddMenu.DropDown.ShowItemToolTips = true;
        _ddMenu.DropDownOpening += (_, _) => ks.Checked = _app.Settings.KillSwitch;
        foreach (var dd in new[] { _ddUpdate, _ddRoutes, _ddMenu }) Theme.StyleDropDown(dd.DropDown);

        // Подключение — на «Главной», маршруты и статистика — свои разделы в боковом меню.
        _toolbar.Items.AddRange([_btnTest, _btnSpeed, _ddUpdate, new ToolStripSeparator(), _btnGroups, _ddMenu]);
    }

    void BuildRoutesMenu()
    {
        var p = Profiles.Current;
        var items = new List<ToolStripItem>
        {
            Mi(L("Списки маршрутизации…"), Icons.List, (_, _) => _app.OpenEditor()),
            new ToolStripSeparator(),
            new ToolStripLabel(L("Профили")) { ForeColor = Theme.Muted },
        };
        foreach (var prof in Profiles.All())
        {
            var item = new ToolStripMenuItem(prof.Name, Icons.Get(Icons.Profile)) { Checked = prof.Name == p.Name };
            item.Click += async (_, _) => await _app.SwitchProfileAsync(prof);
            items.Add(item);
        }
        items.Add(Mi(L("Новый профиль…"), Icons.Add, async (_, _) =>
        {
            if (TrayApp.Prompt(L("Название нового профиля:"), "") is { Length: > 0 } n) await _app.SwitchProfileAsync(Profiles.Create(n.Trim()));
        }));
        items.Add(Mi(L("Клонировать текущий…"), Icons.Copy, async (_, _) =>
        {
            if (TrayApp.Prompt(L("Название копии:"), p.Name + L(" (копия)")) is { Length: > 0 } n) await _app.SwitchProfileAsync(Profiles.Clone(p, n.Trim()));
        }));
        items.Add(Mi(L("Переименовать текущий…"), Icons.Edit, (_, _) =>
        {
            if (TrayApp.Prompt(L("Новое название:"), p.Name) is { Length: > 0 } n && Profiles.Find(n.Trim()) == null)
            {
                p.Name = n.Trim(); p.Save(); _app.Settings.ActiveProfile = p.Name; _app.Settings.Save(); _app.RefreshMenu();
            }
        }));
        var del = Mi(L("Удалить текущий"), Icons.Delete, async (_, _) =>
        {
            var others = Profiles.All().Where(x => x.Name != p.Name).ToList();
            if (others.Count == 0 || !TrayApp.Confirm(Lf($"Удалить профиль «{p.Name}» со всеми его списками?"))) return;
            await _app.SwitchProfileAsync(others[0]);
            Profiles.Delete(p);
            _app.RefreshMenu();
        });
        del.Enabled = Profiles.All().Count > 1;
        items.Add(del);
        items.Add(Mi(L("Импорт профиля из другого клиента…"), Icons.Import, async (_, _) => await _app.ImportThroneAsync()));
        items.Add(new ToolStripSeparator());

        var rest = new ToolStripMenuItem(L("Всё остальное"), Icons.Get(Icons.Network));
        var viaVpn = new ToolStripMenuItem(L("через VPN")) { Checked = p.DefaultOutbound != "direct" };
        viaVpn.Click += async (_, _) => await _app.SetDefaultRouteAsync("proxy");
        var direct = new ToolStripMenuItem(L("напрямую")) { Checked = p.DefaultOutbound == "direct" };
        direct.Click += async (_, _) => await _app.SetDefaultRouteAsync("direct");
        rest.DropDownItems.AddRange([viaVpn, direct]);
        items.Add(rest);
        var steam = new ToolStripMenuItem(L("Ретрансляторы Steam (Dota 2) мимо TUN"), Icons.Get(Icons.Steam)) { Checked = _app.Settings.SteamSdrBypass };
        steam.Click += async (_, _) => await _app.SetSteamBypassAsync(!_app.Settings.SteamSdrBypass);
        items.Add(steam);

        _ddRoutes.DropDownItems.Clear();
        _ddRoutes.DropDownItems.AddRange(items.ToArray());
        Theme.StyleDropDown(_ddRoutes.DropDown);
    }

    void BuildGroupTabs()
    {
        _groupTabs.Renderer = new Theme.Renderer();
        _groupTabs.GripStyle = ToolStripGripStyle.Hidden;
        _groupTabs.BackColor = Theme.Bg;
        _groupTabs.Padding = new Padding(8, 4, 8, 0);
    }

    void RefreshGroupTabs()
    {
        var active = _app.ActiveGroup;
        var want = _app.Groups.Groups.Select(g => $"{g.Id}|{g.Name}|{_app.ServersOf(g).Count}|{g.Id == active?.Id}").ToList();
        if (_groupTabs.Tag is List<string> had && had.SequenceEqual(want)) return;
        _groupTabs.Tag = want;
        _groupTabs.Items.Clear();
        foreach (var g in _app.Groups.Groups)
        {
            var grp = g;
            var b = new ToolStripButton($"{g.Name}  ({_app.ServersOf(g).Count})",
                Icons.Get(g.IsSubscription ? Icons.Cloud : Icons.Key, Theme.Muted)) { Checked = g.Id == active?.Id };
            b.Click += (_, _) => { _app.SetActiveGroup(grp); };
            b.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var at = _groupTabs.PointToScreen(new Point(b.Bounds.Left + e.X, b.Bounds.Top + e.Y));
                GroupMenu(grp).Show(at);
            };
            _groupTabs.Items.Add(b);
        }
        var add = new ToolStripButton(L("Группы…"), Icons.Get(Icons.Add, Theme.Muted)) { ForeColor = Theme.Muted };
        add.Click += (_, _) => _app.ShowGroups();
        _groupTabs.Items.Add(add);
    }

    /// <summary>Меню по правому клику на вкладке группы.</summary>
    ContextMenuStrip GroupMenu(Group g)
    {
        var m = new ContextMenuStrip { Renderer = new Theme.Renderer() };
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        void Item(string text, char icon, Func<Task> run, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text, Icons.Get(icon)) { Enabled = enabled };
            it.Click += async (_, _) =>
            {
                try { await run(); }
                catch (Exception ex) { TrayApp.ShowError(ex.Message); }
            };
            m.Items.Add(it);
        }
        List<string> Tags() => _app.ServersOf(g).Select(s => s.Tag).ToList();
        void Activate() { if (_app.ActiveGroup?.Id != g.Id) _app.SetActiveGroup(g); }

        Item(L("Добавить новую подписку"), Icons.Cloud, () => GroupsForm.EditGroupAsync(_app, this, null, "subscription"));
        Item(L("Добавить группу ключей"), Icons.Key, () => GroupsForm.EditGroupAsync(_app, this, null, "manual"));
        m.Items.Add(new ToolStripSeparator());
        Item(L("Редактировать группу"), Icons.Edit, () => GroupsForm.EditGroupAsync(_app, this, g, g.Type));
        Item(L("Удалить группу"), Icons.Delete, () => GroupsForm.DeleteGroupAsync(_app, g));
        if (g.IsSubscription)
            Item(L("Обновить подписку"), Icons.Refresh, async () =>
            {
                await _app.UpdateGroupAsync(g, manual: true, force: true);
                foreach (var t in Tags()) { Delay.Remove(t); Speed.Remove(t); }
                RefreshServers();
            });
        m.Items.Add(new ToolStripSeparator());
        Item(L("Тест задержки группы"), Icons.Timer, () => { Activate(); return RunTestsAsync([g.AutoTag, .. Tags()]); },
            Tags().Count > 0);
        Item(L("Тест скорости группы"), Icons.Speed, () => { Activate(); return RunSpeedAsync(Tags()); },
            Tags().Count > 0 && _speedCts == null);
        Item(L("Очистить результаты тестов"), Icons.Clear, () =>
        {
            foreach (var t in Tags().Append(g.AutoTag)) { Delay.Remove(t); Speed.Remove(t); }
            RefreshServers();
            return Task.CompletedTask;
        });
        Item(L("Копировать ссылки серверов"), Icons.Copy, () =>
        {
            var links = string.Join("\n", _app.ServersOf(g).Select(s => s.Raw).Where(r => !string.IsNullOrEmpty(r)));
            if (links.Length > 0) Clipboard.SetText(links);
            return Task.CompletedTask;
        });
        Theme.StyleDropDown(m);
        return m;
    }

    void BuildSubBar()
    {
        _subBar.Renderer = new Theme.Renderer();
        _subBar.GripStyle = ToolStripGripStyle.Hidden;
        _subBar.BackColor = Theme.Bg;
        _subBar.Padding = new Padding(10, 2, 10, 4);
        _subTraffic.ForeColor = _subUpdate.ForeColor = _subExpire.ForeColor = Theme.Muted;
        _subAnnounce.ForeColor = Theme.Direct;
        _subAnnounce.Image = Icons.Get(Icons.Info, Theme.Direct);
        _subUpdate.Image = Icons.Get(Icons.Refresh, Theme.Muted);
        _subPortal.Image = Icons.Get(Icons.Globe);
        _subSupport.Image = Icons.Get(Icons.Chat);
        _subPortal.Click += (_, _) => OpenUrl(_app.ActiveGroup?.Info?.WebPageUrl);
        _subSupport.Click += (_, _) => OpenUrl(_app.ActiveGroup?.Info?.SupportUrl);
        _subAnnounce.Overflow = ToolStripItemOverflow.AsNeeded;
        Theme.StyleDropDown(_subBar.OverflowButton.DropDown);
        _subBar.Items.AddRange([_subPortal, _subSupport, _subTitle, _subUsage, _subTraffic, new ToolStripSeparator(),
            _subUpdate, _subExpire, new ToolStripSeparator(), _subAnnounce]);
        _subPortal.Overflow = _subSupport.Overflow = ToolStripItemOverflow.Never;
    }

    static void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void RefreshSubBar()
    {
        var g = _app.ActiveGroup;
        var info = g?.Info;
        _subBar.Visible = g is { IsSubscription: true } && info != null;
        if (!_subBar.Visible) return;
        long used = info!.Upload + info.Download;
        _subTitle.Text = info.Title.Length > 0 ? info.Title : g!.Name;
        _subUsage.Fraction = info.Total > 0 ? Math.Min(1, used / (double)info.Total) : 1;
        _subUsage.Unlimited = info.Total <= 0;
        _subTraffic.Text = $"{Fmt(used)} / {(info.Total > 0 ? Fmt(info.Total) : "∞")}";
        var mins = g!.UpdateIntervalMinutes(_app.Settings);
        _subUpdate.Text = (mins > 0 ? Lf($"каждые {(mins % 60 == 0 ? Lf($"{mins / 60} ч") : Lf($"{mins} мин"))}") : L("вручную")) +
            (g.UpdatedUtc != default ? Lf($" · обновлено {g.UpdatedUtc.ToLocalTime():dd.MM HH:mm}") : "");
        bool expired = info.Expire > 0 && DateTimeOffset.FromUnixTimeSeconds(info.Expire) < DateTimeOffset.Now;
        _subExpire.Text = info.Expire > 0 ? (expired ? L("истекла ") : L("до ")) + $"{DateTimeOffset.FromUnixTimeSeconds(info.Expire).LocalDateTime:dd.MM.yyyy}" : "";
        _subExpire.ForeColor = expired ? Theme.Danger : Theme.Muted;
        _subExpire.Visible = info.Expire > 0;
        _subAnnounce.Text = info.Announce;
        _subAnnounce.Visible = info.Announce.Length > 0;
        _subPortal.Visible = info.WebPageUrl.Length > 0;
        _subSupport.Visible = info.SupportUrl.Length > 0;
    }

    void BuildServers()
    {
        _servers.Columns.Add(Col("mark", "", 44, DataGridViewContentAlignment.MiddleCenter));
        _servers.Columns["mark"]!.DefaultCellStyle.Padding = Padding.Empty;
        _servers.Columns["mark"]!.DefaultCellStyle.ForeColor = Theme.Accent;
        var badge = new DataGridViewImageColumn
        {
            Name = "badge", HeaderText = "", Width = 58, ImageLayout = DataGridViewImageCellLayout.Normal,
            SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
        };
        badge.DefaultCellStyle.NullValue = null;
        badge.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _servers.Columns.Add(badge);
        _servers.Columns.Add(Col("name", L("Сервер"), 0));
        _servers.Columns["name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _servers.Columns.Add(Col("delay", L("Задержка"), 100, DataGridViewContentAlignment.MiddleRight));
        _servers.Columns.Add(Col("speed", L("Скорость"), 130, DataGridViewContentAlignment.MiddleRight));
        _servers.Columns.Add(Col("traffic", L("Трафик за сессию"), 170, DataGridViewContentAlignment.MiddleRight));
        _servers.MultiSelect = true;
        _servers.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await SelectRowAsync(e.RowIndex); };

        var ctx = new ContextMenuStrip();
        ctx.Items.AddRange([
            Mi(L("Выбрать сервер"), Icons.Check, async (_, _) => { if (_servers.CurrentRow != null) await SelectRowAsync(_servers.CurrentRow.Index); }, "Enter"),
            Mi(L("Тест задержки"), Icons.Timer, async (_, _) => await RunTestsAsync(SelectedTags()), "Ctrl+Shift+S"),
            Mi(L("Тест скорости"), Icons.Speed, async (_, _) => await RunSpeedAsync(SelectedTags()), "Ctrl+Shift+P"),
            Mi(L("Копировать ссылку"), Icons.Copy, (_, _) => CopyLinks(), "Ctrl+C"),
            Mi(L("Удалить"), Icons.Delete, async (_, _) => await RemoveSelectedAsync(), "Del"),
            new ToolStripSeparator(),
            Mi(L("Тест задержки всей группы"), Icons.Timer, async (_, _) => await TestGroupAsync(), "Ctrl+Shift+G"),
            Mi(L("Тест скорости всей группы"), Icons.Speed, async (_, _) => await RunSpeedAsync(_shown.Select(s => s.Tag).ToList()), "Ctrl+Alt+P"),
            Mi(L("Остановить тесты"), Icons.Stop, (_, _) => StopTests(), "Ctrl+."),
            new ToolStripSeparator(),
            Mi(L("Удалить недоступные"), Icons.Filter, async (_, _) => await RemoveUnavailableAsync(), "Ctrl+Shift+R"),
            Mi(L("Удалить дубликаты"), Icons.Copy, async (_, _) => await RemoveDuplicatesAsync(), "Ctrl+Shift+D"),
            Mi(L("Очистить результаты тестов"), Icons.Clear, (_, _) => ClearResults(), "Ctrl+Shift+C"),
        ]);
        Theme.StyleDropDown(ctx);
        _servers.ContextMenuStrip = ctx;
        _servers.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_servers.Rows[e.RowIndex].Selected)
            {
                _servers.ClearSelection();
                _servers.CurrentCell = _servers.Rows[e.RowIndex].Cells["name"];
            }
        };
    }

    void BuildBottom()
    {
        _tabs.Renderer = new Theme.Renderer();
        _tabs.GripStyle = ToolStripGripStyle.Hidden;
        _tabs.BackColor = Theme.Bg;
        _tabs.Padding = new Padding(0, 4, 0, 2);
        _tabConns.Image = Icons.Get(Icons.Network); _tabGraph.Image = Icons.Get(Icons.Chart);
        _tabLogs.Image = Icons.Get(Icons.Log); _tabStats.Image = Icons.Get(Icons.Info);
        foreach (var t in new[] { _tabConns, _tabGraph, _tabLogs, _tabStats })
        {
            t.Click += (_, _) => SelectTab(t);
            _tabs.Items.Add(t);
        }

        _conns.VirtualMode = true;
        _conns.Columns.Add(Col("proc", L("Процесс"), 0));
        _conns.Columns["proc"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _conns.Columns.Add(Col("dest", L("Назначение"), 0));
        _conns.Columns["dest"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _conns.Columns.Add(Col("net", L("Протокол"), 100));
        _conns.Columns.Add(Col("out", "Outbound", 220));
        _conns.Columns.Add(Col("traffic", L("Трафик ↑ / ↓"), 170, DataGridViewContentAlignment.MiddleRight));
        _conns.Columns.Add(Col("speed", L("Скорость ↑ / ↓"), 170, DataGridViewContentAlignment.MiddleRight));
        _conns.CellValueNeeded += ConnCellValue;
        _conns.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < _rows.Count && e.ColumnIndex == 3)
                e.CellStyle!.ForeColor = _rows[e.RowIndex].Outbound == "direct" ? Theme.Direct : Theme.Accent;
        };
        var connBar = new ToolStrip { Renderer = new Theme.Renderer(), GripStyle = ToolStripGripStyle.Hidden, BackColor = Theme.Panel, Dock = DockStyle.Top };
        _groupToggle.Image = Icons.Get(Icons.Group);
        _groupToggle.CheckedChanged += (_, _) => { if (_app.Monitor.Last is { } s) OnTraffic(s); };
        connBar.Items.Add(_groupToggle);
        _connsPage = new Panel { Dock = DockStyle.Fill };
        _connsPage.Controls.Add(_conns);
        _connsPage.Controls.Add(connBar);

        _logBox.BackColor = Theme.Panel; _logBox.ForeColor = Theme.Text;
        _logPick.Items.AddRange(["sing-box", "xray"]);
        _logPick.SelectedIndex = 0;
        Theme.StyleCombo(_logPick.ComboBox);
        _logPick.SelectedIndexChanged += (_, _) => RefreshLogs(force: true);
        var logBar = new ToolStrip { Renderer = new Theme.Renderer(), GripStyle = ToolStripGripStyle.Hidden, BackColor = Theme.Panel, Dock = DockStyle.Top };
        logBar.Items.Add(new ToolStripLabel(L("Журнал:")));
        logBar.Items.Add(_logPick);
        logBar.Items.Add(new ToolStripButton(L("Открыть папку"), Icons.Get(Icons.Folder), (_, _) => Process.Start("explorer.exe", AppPaths.Logs)));
        _logsPage = new Panel { Dock = DockStyle.Fill };
        _logsPage.Controls.Add(_logBox);
        _logsPage.Controls.Add(logBar);

        _stats.BackColor = Theme.Panel; _stats.ForeColor = Theme.Text;
    }

    void BuildStatus()
    {
        _status.Renderer = new Theme.Renderer();
        Theme.TipsFor(_status);
        _status.BackColor = Theme.Panel;
        _stProxy.ForeColor = Theme.Accent; _stDirect.ForeColor = Theme.Direct;
        _stRoutes.ForeColor = _stProfile.ForeColor = Theme.Muted;
        _stProfile.Image = Icons.Get(Icons.Routes, Theme.Muted);
        _stProxy.Image = Icons.Get(Icons.Shield, Theme.Accent);
        _stDirect.Image = Icons.Get(Icons.Plug, Theme.Direct);
        _status.Items.AddRange([_stServer, new ToolStripStatusLabel { Spring = true }, _stProfile, _stRoutes, _stProxy, _stDirect]);
        foreach (ToolStripItem i in _status.Items) i.Padding = new Padding(6, 2, 6, 2);
    }

    void SelectTab(ToolStripButton tab)
    {
        foreach (var t in new[] { _tabConns, _tabGraph, _tabLogs, _tabStats }) t.Checked = t == tab;
        _tabHost.Controls.Clear();
        _tabHost.Controls.Add(tab == _tabConns ? _connsPage : tab == _tabGraph ? _chart : tab == _tabLogs ? _logsPage : _stats);
        if (tab == _tabLogs) RefreshLogs(force: true);
        if (tab == _tabStats) RefreshSessionStats();
    }

    /// <summary>Для снимка окна при разработке: открыть график с демо-данными.</summary>
    internal void PreviewChart()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 120; i++)
            _chart.Push((long)(4e6 + 3e6 * Math.Sin(i / 9.0) + rnd.Next(0, 1_500_000)), rnd.Next(50_000, 400_000),
                        (long)(1.2e6 + 8e5 * Math.Cos(i / 13.0)), rnd.Next(20_000, 120_000));
        SelectTab(_tabGraph);
    }

    // ───────────────────────── горячие клавиши ─────────────────────────

    /// <summary>Контрол с фокусом на самом деле (ActiveControl формы указывает лишь на контейнер верхнего уровня).</summary>
    Control? Focused()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // в настройках клавиши — для полей ввода (в т.ч. запись глобальной горячей клавиши), не для окна
        if (_pageHost.Controls.Count == 1 && _pageHost.Controls[0] == _settingsHost) return BaseCmdKey(ref msg, keyData);
        bool inText = Focused() is TextBoxBase or ComboBox;
        switch (keyData)
        {
            case Keys.Control | Keys.U: _ = UpdateActiveAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.S: _ = RunTestsAsync(SelectedTags()); return true;
            case Keys.Control | Keys.Shift | Keys.G: _ = TestGroupAsync(); return true;
            case Keys.F6: _ = RunSpeedAsync([CurrentTag()]); return true;
            case Keys.Control | Keys.Shift | Keys.P: _ = RunSpeedAsync(SelectedTags()); return true;
            case Keys.Control | Keys.Alt | Keys.P: _ = RunSpeedAsync(_shown.Select(s => s.Tag).ToList()); return true;
            case Keys.Control | Keys.OemPeriod: StopTests(); return true;
            case Keys.Control | Keys.H: _app.HideWindow(); return true;
            case Keys.Control | Keys.Shift | Keys.C: ClearResults(); return true;
            case Keys.Control | Keys.Shift | Keys.D: _ = RemoveDuplicatesAsync(); return true;
            case Keys.Control | Keys.Shift | Keys.R: _ = RemoveUnavailableAsync(); return true;
            case Keys.Control | Keys.V when !inText: _ = PasteLinksAsync(); return true;
            case Keys.Control | Keys.C when ActiveControl == _servers: CopyLinks(); return true;
            case Keys.Delete when ActiveControl == _servers: _ = RemoveSelectedAsync(); return true;
            case Keys.Enter when ActiveControl == _servers && _servers.CurrentRow != null: _ = SelectRowAsync(_servers.CurrentRow.Index); return true;
        }
        return BaseCmdKey(ref msg, keyData);
    }

    // Гонка внутри WinForms: при Alt/F10 ToolStripManager перебирает все панели меню, а список в это время меняется
    // (создаются/закрываются выпадающие меню) — «Collection was modified». Нажатие просто пропускаем.
    bool BaseCmdKey(ref Message msg, Keys keyData)
    {
        try { return base.ProcessCmdKey(ref msg, keyData); }
        catch (InvalidOperationException) { return false; }
    }

    // ───────────────────────── состояние ─────────────────────────

    void OnStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnStateChanged); return; }

        bool on = _app.Core.Running, busy = _app.Busy;
        _btnConnect.Text = busy ? L("Подключение…") : on ? L("Отключить") : L("Подключить");
        _btnConnect.ForeColor = busy ? Theme.Warn : on ? Theme.Danger : Theme.Accent;
        _btnConnect.Image = Icons.Get(on ? Icons.Stop : Icons.Play, busy ? Theme.Warn : on ? Theme.Danger : Theme.Accent);
        _btnConnect.Enabled = !busy;
        _btnTest.Enabled = !busy && !_offlineTesting;
        Text = "TunPilot — " + (busy ? L("подключение…") : on ? L("подключено") : L("отключено"));

        if (!on) { _rows = []; _conns.RowCount = 0; }
        RefreshGroupTabs();
        RefreshSubBar();
        RefreshServers();
        RefreshStatus(_app.Monitor.Last);
        RefreshHome();
        if (_pageHost.Controls.Count == 1 && _pageHost.Controls[0] == _pageOf.GetValueOrDefault(_navRoutes)) QueueRebuildRoutes();
    }

    void RefreshServers()
    {
        var g = _app.ActiveGroup;
        _shown = g != null ? _app.ServersOf(g) : [];
        var keys = new List<string>();
        if (g != null) keys.Add(g.AutoKey);
        keys.AddRange(_shown.Select(s => s.Key));
        if (_servers.Rows.Count != keys.Count || _servers.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Tag!).Where((k, i) => k != keys[i]).Any())
        {
            _servers.Rows.Clear();
            foreach (var k in keys) _servers.Rows[_servers.Rows.Add()].Tag = k;
            _servers.ClearSelection();
        }
        if (g == null) return;

        var sel = _app.Settings.SelectedServer;
        var perTag = _app.Monitor.PerTag;
        for (int i = 0; i < keys.Count; i++)
        {
            var row = _servers.Rows[i];
            bool isAuto = i == 0;
            var srv = isAuto ? null : _shown[i - 1];
            var tag = isAuto ? g.AutoTag : srv!.Tag;
            bool isSel = sel == keys[i];

            row.Cells["mark"].Value = isSel ? "✓" : "";
            if (isAuto)
            {
                var now = _app.Monitor.AutoNow.TryGetValue(g.AutoTag, out var t) ? _shown.FirstOrDefault(x => x.Tag == t)?.Name : null;
                row.Cells["badge"].Value = Icons.Badge("AUTO", Theme.Warn);
                row.Cells["name"].Value = L("Авто — самый быстрый") + (now != null ? Lf($"   (сейчас: {now})") : "");
            }
            else
            {
                var (text, color) = Icons.ProtocolOf(srv!);
                row.Cells["badge"].Value = Icons.Badge(text, color);
                row.Cells["name"].Value = srv!.Name;
            }

            var dCell = row.Cells["delay"];
            if (_testing.Contains(tag)) { dCell.Value = "…"; dCell.Style.ForeColor = Theme.Muted; }
            else if (Delay.TryGetValue(tag, out var d))
            {
                dCell.Value = d is { } ms ? $"{ms} ms" : L("таймаут");
                dCell.Style.ForeColor = d is null ? Theme.Danger : d < 200 ? Theme.Accent : d < 500 ? Theme.Warn : Theme.Orange;
            }
            else dCell.Value = "";

            var spCell = row.Cells["speed"];
            if (_speedLive.TryGetValue(tag, out var live)) { spCell.Value = Lf($"{live:0.0} Мбит/с…"); spCell.Style.ForeColor = Theme.Muted; }
            else if (_speedQueue.Contains(tag)) { spCell.Value = L("в очереди"); spCell.Style.ForeColor = Theme.Muted; }
            else if (Speed.TryGetValue(tag, out var sp))
            {
                spCell.Value = sp is { } m ? Lf($"{m:0.0} Мбит/с") : L("ошибка");
                spCell.Style.ForeColor = sp is null ? Theme.Danger : sp >= 50 ? Theme.Accent : sp >= 10 ? Theme.Warn : Theme.Orange;
            }
            else spCell.Value = "";

            var traffic = isAuto ? default : perTag.GetValueOrDefault(tag);
            row.Cells["traffic"].Value = traffic.Up + traffic.Down > 0 ? $"{Fmt(traffic.Up)} ↑  {Fmt(traffic.Down)} ↓" : "";

            var style = row.DefaultCellStyle;
            style.Font = isSel ? (_bold ??= new Font(_servers.Font, FontStyle.Bold)) : _servers.Font;
            style.BackColor = isSel ? Theme.SelRow : (i % 2 == 0 ? Theme.Panel : Theme.PanelAlt);
        }
    }

    void RefreshStatus(TrafficMonitor.Snapshot? s)
    {
        bool on = _app.Core.Running;
        _stServer.Text = _app.PendingSubUpdate ? L("Изменения подписок применятся при переподключении")
            : !on ? L("Отключено") : _app.SelectedDisplay;
        _stServer.Image = Icons.Get(Icons.Shield, on ? Theme.Accent : Theme.Muted);
        _stServer.ForeColor = on ? Theme.Accent : Theme.Muted;
        _stProfile.Text = Profiles.Current.Name;
        _stRoutes.Text = on && _app.RoutesInfo.Length > 0 ? _app.RoutesInfo : "";
        _stProxy.Text = on ? $"{SpeedChart.Rate(s?.ProxyUp ?? 0)} ↑  {SpeedChart.Rate(s?.ProxyDown ?? 0)} ↓" : "";
        _stDirect.Text = on ? $"{SpeedChart.Rate(s?.DirectUp ?? 0)} ↑  {SpeedChart.Rate(s?.DirectDown ?? 0)} ↓" : "";
        _stProxy.Visible = _stDirect.Visible = on;
        _stProxy.ToolTipText = L("Через прокси"); _stDirect.ToolTipText = L("Напрямую");
    }

    // ───────────────────────── трафик (от монитора) ─────────────────────────

    void OnTraffic(TrafficMonitor.Snapshot s)
    {
        if (IsDisposed || !Visible) return;
        _tick++;
        RefreshHome();   // таймер сессии и скорость на главной
        var raw = s.Conns.Select(c => new Row(c.Process, c.Dest, c.Net, c.Outbound, c.Up, c.Down, c.SpUp, c.SpDown, 1)).ToList();
        _rows = _groupToggle.Checked
            ? raw.GroupBy(r => r.Process).Select(g => new Row(
                    g.Key,
                    string.Join(", ", g.Select(r => r.Dest.Split(':')[0]).Distinct().Take(3)) + (g.Select(r => r.Dest).Distinct().Count() > 3 ? " …" : ""),
                    string.Join("/", g.Select(r => r.Net).Distinct()),
                    string.Join(", ", g.Select(r => r.Outbound).Distinct()),
                    g.Sum(r => r.Up), g.Sum(r => r.Down), g.Sum(r => r.SpUp), g.Sum(r => r.SpDown), g.Count()))
                .OrderByDescending(r => r.SpUp + r.SpDown).ThenByDescending(r => r.Up + r.Down).ToList()
            : raw.OrderByDescending(r => r.SpUp + r.SpDown).ThenByDescending(r => r.Up + r.Down).ToList();
        _conns.RowCount = _rows.Count;
        _conns.Invalidate();
        _chart.Push(s.ProxyDown, s.ProxyUp, s.DirectDown, s.DirectUp);
        RefreshStatus(s);
        if (_tick % 2 == 0) RefreshServers();
        if (_tabLogs.Checked && _tick % 2 == 0) RefreshLogs(force: false);
        if (_tabStats.Checked) RefreshSessionStats();
    }

    void ConnCellValue(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= _rows.Count) return;
        var r = _rows[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => r.Count > 1 ? $"{r.Process}  ({r.Count})" : r.Process,
            1 => r.Dest,
            2 => r.Net,
            3 => r.Outbound,
            4 => $"{Fmt(r.Up)} ↑  {Fmt(r.Down)} ↓",
            5 => r.SpUp + r.SpDown > 0 ? $"{SpeedChart.Rate(r.SpUp)} ↑  {SpeedChart.Rate(r.SpDown)} ↓" : "",
            _ => "",
        };
    }

    // ───────────────────────── действия с серверами ─────────────────────────

    async Task SelectRowAsync(int index)
    {
        if (_servers.Rows[index].Tag is not string key) return;
        await _app.SelectServerAsync(key);
    }

    /// <summary>Теги выделенных строк (без «Авто»), иначе — текущего сервера.</summary>
    List<string> SelectedTags()
    {
        var tags = _servers.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.Index > 0 && r.Index - 1 < _shown.Count).OrderBy(r => r.Index)
            .Select(r => _shown[r.Index - 1].Tag).ToList();
        return tags.Count > 0 ? tags : [CurrentTag()];
    }

    /// <summary>Текущий сервер; для «Авто» — тот, что оно выбрало.</summary>
    string CurrentTag()
    {
        var tag = _app.SelectedTag;
        if (tag.StartsWith("auto-")) tag = _app.Monitor.AutoNow.GetValueOrDefault(tag) ?? _shown.FirstOrDefault()?.Tag ?? tag;
        return tag;
    }

    List<ServerInfo> SelectedServers() =>
        _servers.SelectedRows.Cast<DataGridViewRow>().Where(r => r.Index > 0 && r.Index - 1 < _shown.Count)
            .Select(r => _shown[r.Index - 1]).ToList();

    void CopyLinks()
    {
        var links = SelectedServers().Select(s => s.Raw).ToList();
        if (links.Count == 0) return;
        Clipboard.SetText(string.Join("\n", links));
        _app.Balloon(links.Count == 1 ? L("Ссылка скопирована.") : Lf($"Скопировано ссылок: {links.Count}."), ToolTipIcon.Info);
    }

    async Task PasteLinksAsync()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        var n = await _app.AddLinksAsync(text);
        _app.Balloon(n > 0 ? Lf($"Добавлено ключей: {n}.") : L("В буфере нет ссылок на серверы (vless://, hy2://…)."), ToolTipIcon.Info);
    }

    async Task RemoveSelectedAsync()
    {
        var list = SelectedServers();
        if (list.Count == 0 || _app.ActiveGroup is not { } g) return;
        var what = g.IsSubscription ? L("Скрыть до следующего обновления подписки") : L("Удалить");
        if (TrayApp.Confirm(Lf($"{what}: {list.Count} сервер(ов)?"))) await _app.RemoveServersAsync(g, list);
    }

    async Task RemoveUnavailableAsync()
    {
        if (_app.ActiveGroup is not { } g) return;
        var dead = _shown.Where(s => Delay.TryGetValue(s.Tag, out var d) && d == null).ToList();
        if (dead.Count == 0) { _app.Balloon(L("Недоступных нет (сначала запустите тест задержки)."), ToolTipIcon.Info); return; }
        if (TrayApp.Confirm(Lf($"Убрать недоступные серверы ({dead.Count})?") + (g.IsSubscription ? L("\nОни вернутся при следующем обновлении подписки.") : "")))
            await _app.RemoveServersAsync(g, dead);
    }

    async Task RemoveDuplicatesAsync()
    {
        if (_app.ActiveGroup is not { } g) return;
        var n = await _app.RemoveDuplicatesAsync(g);
        _app.Balloon(n > 0 ? Lf($"Удалено дубликатов: {n}.") : L("Дубликатов нет."), ToolTipIcon.Info);
    }

    void ClearResults()
    {
        foreach (var s in _shown) { Delay.Remove(s.Tag); Speed.Remove(s.Tag); }
        if (_app.ActiveGroup is { } g) Delay.Remove(g.AutoTag);
        RefreshServers();
    }

    async Task UpdateActiveAsync()
    {
        if (_app.ActiveGroup is { IsSubscription: true } g)
        {
            await _app.UpdateGroupAsync(g, manual: true, force: true);
            ClearResults();
        }
    }

    void StopTests() { _speedCts?.Cancel(); }

    // ───────────────────────── тесты ─────────────────────────

    Task TestGroupAsync()
    {
        var tags = _shown.Select(s => s.Tag).ToList();
        if (_app.ActiveGroup is { } g) tags.Insert(0, g.AutoTag);
        return RunTestsAsync(tags);
    }

    /// <summary>Подключены — меряем через рабочее ядро; нет — через временное тестовое (без TUN).</summary>
    async Task RunTestsAsync(List<string> tags)
    {
        if (_offlineTesting || _app.Busy || tags.Count == 0) return;
        if (_speedCts != null && !_app.Core.Running) { _app.Balloon(L("Дождитесь окончания теста скорости."), ToolTipIcon.Info); return; }
        // «Авто» отдельно не меряем: показываем задержку сервера, который оно выбрало (или лучшую).
        var autoTags = tags.Where(t => t.StartsWith("auto-")).ToList();
        tags = tags.Where(t => !t.StartsWith("auto-")).ToList();
        if (tags.Count == 0 && autoTags.Count > 0) tags = _shown.Select(s => s.Tag).ToList();
        _btnTest.Enabled = false;
        foreach (var t in tags.Concat(autoTags)) { _testing.Add(t); Delay.Remove(t); }
        RefreshServers();

        void Done(string tag, int? d) => BeginInvoke(() => { _testing.Remove(tag); Delay[tag] = d; RefreshServers(); });
        try
        {
            if (_app.Core.Running)
            {
                using var gate = new SemaphoreSlim(6);
                await Task.WhenAll(tags.Select(async tag =>
                {
                    await gate.WaitAsync();
                    try { Done(tag, await _app.Core.DelayAsync(tag)); } finally { gate.Release(); }
                }));
            }
            else
            {
                _offlineTesting = true;
                _btnTest.Text = L("Тест…");
                if (Process.GetProcessesByName("ThroneCore").Length > 0)
                    _app.Balloon(L("Запущен Throne: если в нём включён TUN, часть серверов в тесте покажет таймаут."), ToolTipIcon.Warning);
                await Task.Run(() => _app.Core.TestOfflineAsync(_app.Settings, _app.AllServers, tags, _ => { }, Done));
            }
        }
        catch (Exception ex) { _app.Balloon(L("Тест задержки: ") + ex.Message, ToolTipIcon.Warning); }
        finally
        {
            _offlineTesting = false;
            foreach (var t in tags) _testing.Remove(t);
            foreach (var a in autoTags)
            {
                _testing.Remove(a);
                var now = _app.Monitor.AutoNow.GetValueOrDefault(a);
                var known = _shown.Select(s => Delay.GetValueOrDefault(s.Tag)).Where(d => d != null).ToList();
                Delay[a] = now != null && Delay.GetValueOrDefault(now) is int cur ? cur : known.Count > 0 ? known.Min() : null;
            }
            _btnTest.Text = L("Тест задержки");
            _btnTest.Enabled = true;
            RefreshServers();
        }
    }

    async Task SpeedSelectedOrCurrentAsync() =>
        await RunSpeedAsync(_servers.SelectedRows.Count > 0 ? SelectedTags() : [CurrentTag()]);

    async Task RunSpeedAsync(List<string> tags)
    {
        tags = tags.Where(t => !t.StartsWith("auto-") && t.Length > 0).Distinct().ToList();
        if (_speedCts != null || tags.Count == 0 || _app.Busy) return;
        if (_offlineTesting) { _app.Balloon(L("Дождитесь окончания теста задержки."), ToolTipIcon.Info); return; }

        _speedCts = new CancellationTokenSource();
        _btnSpeed.Text = L("Остановить");
        _btnSpeed.Image = Icons.Get(Icons.Stop, Theme.Danger);
        foreach (var t in tags) { Speed.Remove(t); _speedQueue.Add(t); }
        RefreshServers();
        if (!_app.Core.Running && Process.GetProcessesByName("ThroneCore").Length > 0)
            _app.Balloon(L("Запущен Throne: если в нём включён TUN, результат теста будет искажён."), ToolTipIcon.Warning);

        void Progress(string tag, double mbps) => BeginInvoke(() => { _speedQueue.Remove(tag); _speedLive[tag] = mbps; RefreshServers(); });
        void Done(string tag, double? mbps) => BeginInvoke(() => { _speedQueue.Remove(tag); _speedLive.Remove(tag); Speed[tag] = mbps; RefreshServers(); });
        try
        {
            var ct = _speedCts.Token;
            await Task.Run(() => _app.Core.SpeedTestAsync(_app.Settings, _app.AllServers, tags, Progress, Done, ct));
        }
        catch (Exception ex) { _app.Balloon(L("Тест скорости: ") + ex.Message, ToolTipIcon.Warning); }
        finally
        {
            _speedCts.Dispose(); _speedCts = null;
            _speedQueue.Clear(); _speedLive.Clear();
            _btnSpeed.Text = L("Тест скорости");
            _btnSpeed.Image = Icons.Get(Icons.Speed);
            RefreshServers();
        }
    }

    // ───────────────────────── логи и сессия ─────────────────────────

    long _logLen = -1;
    void RefreshLogs(bool force)
    {
        var path = Path.Combine(AppPaths.Logs, $"{_logPick.SelectedItem}.log");
        try
        {
            if (!File.Exists(path)) { _logBox.Text = L("(журнал пуст — ядро ещё не запускалось)"); return; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!force && fs.Length == _logLen) return;
            _logLen = fs.Length;
            fs.Seek(Math.Max(0, fs.Length - 64 * 1024), SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var lines = sr.ReadToEnd().Split('\n').TakeLast(400);
            _logBox.Text = string.Join("\r\n", lines).TrimEnd();
            if (_logBox.Text.Length == 0) _logBox.Text = L("(пусто — ядро пишет только предупреждения и ошибки, это хороший знак)");
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        catch (IOException) { }
    }

    void RefreshSessionStats()
    {
        var s = _app.Settings;
        var m = _app.Monitor;
        bool on = _app.Core.Running;
        var p = Profiles.Current;
        var sb = new StringBuilder();
        sb.AppendLine(Lf($"Состояние          {(on ? L("подключено") : L("отключено"))}"));
        if (on) sb.AppendLine(Lf($"Время работы       {DateTime.Now - _app.ConnectedAt:hh\\:mm\\:ss}"));
        sb.AppendLine(Lf($"Сервер             {_app.SelectedDisplay}"));
        sb.AppendLine();
        sb.AppendLine(Lf($"Через прокси       {Fmt(m.ProxyUp)} ↑   {Fmt(m.ProxyDown)} ↓"));
        sb.AppendLine(Lf($"Напрямую           {Fmt(m.DirectUp)} ↑   {Fmt(m.DirectDown)} ↓"));
        sb.AppendLine(Lf($"Активных соединений {m.Last?.Conns.Count ?? 0}"));
        sb.AppendLine();
        sb.AppendLine(Lf($"Профиль            {p.Name}"));
        sb.AppendLine(Lf($"Мимо TUN           {(on && _app.RoutesInfo.Length > 0 ? _app.RoutesInfo : "—")}"));
        sb.AppendLine(Lf($"Всё остальное      {(p.DefaultOutbound == "direct" ? L("напрямую") : L("через VPN"))}"));
        sb.AppendLine(Lf($"Geo напрямую       {(p.GeoDirect.Count > 0 ? string.Join(", ", p.GeoDirect) : "—")}"));
        if (s.LocalProxyEnabled) sb.AppendLine(Lf($"Локальный прокси   127.0.0.1:{s.LocalProxyPort}"));
        sb.AppendLine();
        sb.AppendLine(Lf($"Групп / серверов   {_app.Groups.Groups.Count} / {_app.AllServers.Count}"));
        _stats.Text = sb.ToString();
    }

    // ───────────────────────── утилиты ─────────────────────────

    internal static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.0} KiB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.00} MiB",
        < 1024L * 1024 * 1024 * 1024 => $"{b / 1073741824.0:0.00} GiB",
        _ => $"{b / 1099511627776.0:0.00} TiB",
    };

    internal static DataGridViewTextBoxColumn Col(string name, string header, int width,
        DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
    {
        var c = new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = align },
        };
        if (width > 0) { c.Width = width; c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; }
        return c;
    }

    internal static DataGridView MakeGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, BorderStyle = BorderStyle.None, BackgroundColor = Theme.Panel, GridColor = Theme.Grid,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        };
        g.ColumnHeadersHeight = 30;
        g.RowTemplate.Height = 28;
        g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Header;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Header;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.DefaultCellStyle.BackColor = Theme.Panel;
        g.DefaultCellStyle.ForeColor = Theme.Text;
        g.DefaultCellStyle.SelectionBackColor = Theme.Select;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = Theme.PanelAlt;
        // двойная буферизация — без мерцания при обновлении раз в секунду
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(g, true);
        Theme.DarkScroll(g);
        return g;
    }

    /// <summary>Полоса использования трафика (зелёная, как в Throne).</summary>
    sealed class UsageBar : ToolStripItem
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Fraction { get; set; } = 1;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Unlimited { get; set; }
        public UsageBar() { AutoSize = false; Size = new Size(160, 22); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var r = new Rectangle(4, (Height - 12) / 2, Width - 8, 12);
            using var bg = new SolidBrush(Theme.Grid);
            e.Graphics.FillRectangle(bg, r);
            var fill = Unlimited ? Theme.Accent : Fraction > 0.9 ? Theme.Danger : Fraction > 0.7 ? Theme.Warn : Theme.Accent;
            using var fg = new SolidBrush(fill);
            e.Graphics.FillRectangle(fg, r with { Width = (int)(r.Width * Fraction) });
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
