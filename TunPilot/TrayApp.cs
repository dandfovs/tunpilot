using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TunPilot;

/// <summary>
/// Оболочка Windows: трей, окна, уведомления, горячая клавиша. Вся логика — в общем <see cref="AppController"/>;
/// здесь только отображение и реализация <see cref="IPlatform"/> для Windows.
/// </summary>
public sealed class TrayApp : ApplicationContext, IPlatform
{
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _toggle = new();
    readonly ToolStripMenuItem _killSwitch = new("Kill-switch");
    readonly ToolStripMenuItem _unblock = new(L("Снять блокировку интернета (kill-switch)"));
    readonly ToolStripMenuItem _servers = new(L("Сервер"));
    readonly ToolStripMenuItem _profiles = new(L("Профиль маршрутизации"));
    readonly SynchronizationContext _ui;
    readonly HotkeyWindow _hotkey;
    readonly AppController _c;
    readonly IKillSwitch _ks = new WinKillSwitch();
    readonly IBypassRoutes _bypass = new WinBypassRoutes();
    readonly IAutostart _autostart = new WinAutostart();
    MainForm? _window;

    // ── состояние для окон (берётся из общего контроллера) ──
    internal AppController Controller => _c;
    internal Settings Settings => _c.Settings;
    internal CoreManager Core => _c.Core;
    internal GroupStore Groups => _c.Groups;
    internal TrafficMonitor Monitor => _c.Monitor;
    internal IReadOnlyList<ServerInfo> AllServers => _c.AllServers;
    internal bool Busy => _c.Busy;
    internal string RoutesInfo => _c.RoutesInfo;
    internal bool PendingSubUpdate => _c.PendingSubUpdate;
    internal DateTime ConnectedAt => _c.ConnectedAt;
    internal Icon AppIcon { get; } = Icons.PlainIcon();   // окна — без точки состояния (она только в трее)
    internal event Action? StateChanged;

    internal Group? ActiveGroup => _c.ActiveGroup;
    internal List<ServerInfo> ServersOf(Group g) => _c.ServersOf(g);
    internal string SelectedTag => _c.SelectedTag;
    internal string SelectedDisplay => _c.SelectedDisplay;

    /// <param name="startHidden">Не показывать окно (режим скриншотов).</param>
    /// <param name="autostart">Запуск вместе с Windows (ключ --tray из Планировщика).</param>
    public TrayApp(bool startHidden = false, bool autostart = false)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);   // контроллер и монитор работают в контексте UI
        _c = new AppController(this);
        Theme.Apply(_c.Settings.Theme);

        _tray = new NotifyIcon { Icon = Icons.AppIcon(Color.Gray), Visible = true, ContextMenuStrip = _menu, Text = "TunPilot" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _toggle.Click += async (_, _) => { if (_c.Core.Running) await _c.DisconnectAsync(); else await _c.ConnectAsync(); };
        _menu.Items.AddRange([
            new ToolStripMenuItem(L("Открыть окно"), Icons.Get(Icons.Window), (_, _) => ShowWindow()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            _status, new ToolStripSeparator(),
            _toggle, _unblock, _servers, _profiles, _killSwitch, new ToolStripSeparator(),
            new ToolStripMenuItem(L("Обновить все подписки"), Icons.Get(Icons.Refresh), async (_, _) => await _c.UpdateAllAsync(manual: true)),
            new ToolStripMenuItem(L("Статистика трафика"), Icons.Get(Icons.Chart), (_, _) => ShowStats()),
            new ToolStripMenuItem(L("Настройки…"), Icons.Get(Icons.Settings), (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(L("Выход"), Icons.Get(Icons.Power), async (_, _) => await ExitAsync()),
        ]);
        Theme.StyleDropDown(_menu);
        _menu.Opening += (_, _) => RefreshMenu();
        _killSwitch.Click += (_, _) => _c.SetKillSwitch(!_c.Settings.KillSwitch);
        _menu.ShowItemToolTips = true;
        _killSwitch.ToolTipText = KillSwitch.Help;
        _unblock.ToolTipText = L("Интернет заблокирован kill-switch (программа или ядро завершились аварийно, либо ПК перезагрузили с включённым VPN).\nНажмите, чтобы снять блокировку, или просто подключитесь.");
        _unblock.Click += (_, _) => _c.DisableKillSwitch();

        _c.StateChanged += RefreshMenu;
        _c.FirstRun += () => { ShowWindow(); ShowGroups(); };   // первый запуск — сразу предлагаем добавить подписку

        _hotkey = new HotkeyWindow(ShowWindow);
        _hotkey.Register(_c.Settings.GlobalHotkey);

        bool toTray = startHidden || (autostart ? _c.Settings.AutostartToTray : _c.Settings.StartMinimized);
        if (!toTray) ShowWindow();
        _ = _c.InitAsync();
    }

    // ───────────────────────── действия (из окон и меню) — в контроллер ─────────────────────────

    internal Task SelectServerAsync(string key) => _c.SelectServerAsync(key);
    internal void SetActiveGroup(Group g) => _c.SetActiveGroup(g);
    internal Task ConnectAsync() => _c.ConnectAsync();
    internal Task DisconnectAsync(bool userAction = true, bool keepKillSwitch = false) => _c.DisconnectAsync(userAction, keepKillSwitch);
    internal Task ApplyBypassAsync(bool quiet = false) => _c.ApplyBypassAsync(quiet);
    internal void SetKillSwitch(bool on) => _c.SetKillSwitch(on);
    internal Task UpdateAllAsync(bool manual) => _c.UpdateAllAsync(manual);
    internal Task UpdateGroupAsync(Group g, bool manual, bool force = false) => _c.UpdateGroupAsync(g, manual, force);
    internal Task GroupsChangedAsync() => _c.GroupsChangedAsync();
    internal Task<int> AddLinksAsync(string text) => _c.AddLinksAsync(text);
    internal Task RemoveServersAsync(Group g, IEnumerable<ServerInfo> servers) => _c.RemoveServersAsync(g, servers);
    internal Task<int> RemoveDuplicatesAsync(Group g) => _c.RemoveDuplicatesAsync(g);
    internal Task SwitchProfileAsync(RoutingProfile p) => _c.SwitchProfileAsync(p);
    internal Task<string> UpdateCoresAsync(List<CoreUpdater.Info> updates, Action<string> progress) => _c.UpdateCoresAsync(updates, progress);
    internal Task<List<string>> GeoChangedAsync() => _c.GeoChangedAsync();
    internal Task SetDefaultRouteAsync(string key) => _c.SetDefaultRouteAsync(key);
    internal Task SetSteamBypassAsync(bool on) => _c.SetSteamBypassAsync(on);
    internal void ToggleAutostart() => _c.ToggleAutostart();

    internal void OpenEditor()
    {
        using var f = new ListEditorForm();
        if (f.ShowDialog() != DialogResult.OK) return;
        _ = _c.ListsChangedAsync(f.BypassChanged);
    }

    /// <summary>Импорт профиля маршрутизации из другого клиента (Throne, Happ, v2rayN, Xray, sing-box, Clash…).</summary>
    internal async Task ImportThroneAsync()
    {
        var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        var text = Prompt(L("Вставьте ссылку, текст настроек или ссылку на файл — или нажмите «Файл…».\n") + RouteImport.SupportedText,
            RouteImport.LooksImportable(clip) ? clip : "", multiline: true, withFile: true) ?? "";
        await _c.ImportRoutesAsync(text);
    }

    /// <summary>Настройки сохранены в окне настроек.</summary>
    internal async Task SettingsChangedAsync(bool coreAffected, bool themeChanged)
    {
        _hotkey.Register(_c.Settings.GlobalHotkey);
        if (themeChanged)
        {
            Theme.Apply(_c.Settings.Theme);
            Theme.StyleDropDown(_menu);
            // окно пересоздаём после текущего события (кнопка «Сохранить» лежит в нём же) и возвращаемся в настройки
            _ui.Post(_ =>
            {
                bool visible = _window is { Visible: true };
                _window?.Dispose(); _window = null;
                if (visible) { ShowWindow(); _window?.ShowSettingsPage(); }
            }, null);
        }
        await _c.SettingsChangedAsync(coreAffected);
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
                if (!_c.Settings.TrayHintShown)
                {
                    _c.Settings.TrayHintShown = true; _c.Settings.Save();
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
        await _c.ShutdownAsync();   // отключиться (запомнив, было ли подключено), сохранить статистику, остановить ядра
        ExitThread();
    }

    // ───────────────────────── меню трея ─────────────────────────

    internal void RefreshMenu()
    {
        if (_ui != SynchronizationContext.Current) { _ui.Post(_ => RefreshMenu(), null); return; }
        bool on = _c.Core.Running, busy = _c.Busy, ks = _c.KillSwitchActive;
        _status.Text = busy ? L("Подключение…") : on ? Lf($"Подключено · {SelectedDisplay}") : L("Отключено");
        _toggle.Text = on ? L("Отключить") : L("Подключить");
        _toggle.Image = Icons.Get(on ? Icons.Stop : Icons.Play, on ? Theme.Danger : Theme.Accent);
        _toggle.Enabled = !busy;
        _killSwitch.Checked = _c.Settings.KillSwitch;
        _killSwitch.Image = Icons.Get(Icons.Shield, ks ? Theme.Accent : Theme.Muted);
        _killSwitch.Text = ks ? L("Kill-switch (активен)") : "Kill-switch";
        _unblock.Visible = ks && !on && !busy;
        _unblock.Image = Icons.Get(Icons.Shield, Theme.Danger);
        _tray.Icon = Icons.AppIcon(busy ? Theme.Warn : on ? Theme.Accent : Color.Gray);
        _tray.Text = Truncate("TunPilot — " + (on ? L("подключено") : L("отключено")), 63);

        _servers.DropDownItems.Clear();
        if (ActiveGroup is { } g)
        {
            var auto = new ToolStripMenuItem(Lf($"⚡ Авто ({g.Name})")) { Checked = _c.Settings.SelectedServer == g.AutoKey };
            auto.Click += async (_, _) => await _c.SelectServerAsync(g.AutoKey);
            _servers.DropDownItems.Add(auto);
            _servers.DropDownItems.Add(new ToolStripSeparator());
            foreach (var s in ServersOf(g))
            {
                var key = s.Key;
                var item = new ToolStripMenuItem(s.Name) { Checked = _c.Settings.SelectedServer == key };
                item.Click += async (_, _) => await _c.SelectServerAsync(key);
                _servers.DropDownItems.Add(item);
            }
        }
        _profiles.DropDownItems.Clear();
        foreach (var p in Profiles.All())
        {
            var item = new ToolStripMenuItem(p.Name) { Checked = p.Name == Profiles.Current.Name };
            item.Click += async (_, _) => await _c.SwitchProfileAsync(p);
            _profiles.DropDownItems.Add(item);
        }
        Theme.StyleDropDown(_menu);
        StateChanged?.Invoke();
    }

    // ───────────────────────── IPlatform (Windows) ─────────────────────────

    void IPlatform.Notify(string title, string text, NotifyKind kind) =>
        Balloon(title, text, kind switch { NotifyKind.Warning => ToolTipIcon.Warning, NotifyKind.Error => ToolTipIcon.Error, _ => ToolTipIcon.Info });
    void IPlatform.ShowError(string text) => ShowError(text);
    void IPlatform.ShowInfo(string title, string text) => MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    Task<bool> IPlatform.ConfirmAsync(string text) => Task.FromResult(Confirm(text));
    IKillSwitch IPlatform.KillSwitch => _ks;
    IBypassRoutes IPlatform.Bypass => _bypass;
    IAutostart IPlatform.Autostart => _autostart;

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
