using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TunPilot.LinuxApp;

/// <summary>
/// Оболочка Linux: трей, главное окно, уведомления и диалоги. Вся логика — в общем <see cref="AppController"/>;
/// здесь реализация <see cref="IPlatform"/> для Linux.
/// </summary>
sealed class LinuxShell : IPlatform
{
    public static LinuxShell? Instance { get; private set; }
    public static WindowIcon? AppIcon { get; private set; }

    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly string[] _args;
    readonly IKillSwitch _ks = new LinuxKillSwitch();
    readonly IBypassRoutes _bypass = new LinuxBypassRoutes();
    readonly IAutostart _autostart = new LinuxAutostart();
    TrayIcon? _tray;
    NativeMenuItem? _trayStatus, _trayToggle;
    bool? _notifySend;

    public AppController App { get; private set; } = null!;
    public MainWindow? Window { get; private set; }

    public LinuxShell(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        _desktop = desktop;
        _args = args;
        Instance = this;
    }

    public void Start()
    {
        try { AppIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://TunPilot/Assets/icon.png"))); } catch { }
        App = new AppController(this);
        App.StateChanged += RefreshTray;
        App.FirstRun += () => { ShowWindow(); Window?.ShowGroups(); };
        SingleInstance.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
        CreateTray();

        bool autostart = _args.Contains("--tray");
        bool toTray = autostart ? App.Settings.AutostartToTray : App.Settings.StartMinimized;
        if (!toTray || _tray == null || Arg("--screenshot") != null) ShowWindow();
        _ = App.InitAsync();
        if (Arg("--screenshot") is { } shot) _ = ScreenshotAsync(shot);
    }

    string? Arg(string name) { var i = Array.IndexOf(_args, name); return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null; }

    /// <summary>Режим разработки: «--screenshot файл.png [--nav страница] [--settab N]» — снимок окна и выход.</summary>
    async Task ScreenshotAsync(string path)
    {
        await Task.Delay(1500);
        if (Arg("--nav") is { } nav) Window!.ShowPage(nav);
        if (Arg("--settab") is { } t && int.TryParse(t, out var tab)) Window!.SelectTab(tab);
        await Task.Delay(2000);
        var size = new PixelSize((int)Window!.Bounds.Width, (int)Window.Bounds.Height);
        using var bmp = new RenderTargetBitmap(size);
        bmp.Render(Window);
        bmp.Save(path);
        _quitting = true;
        SingleInstance.Release();
        _desktop.Shutdown();
    }

    // ───────────────────────── окно ─────────────────────────

    public void ShowWindow()
    {
        if (Window == null)
        {
            Window = new MainWindow(this);
            Window.Closing += (_, e) =>
            {
                if (_quitting || _tray == null) return;
                e.Cancel = true;   // крестик сворачивает в трей, выход — через меню
                Window.Hide();
                if (!App.Settings.TrayHintShown)
                {
                    App.Settings.TrayHintShown = true; App.Settings.Save();
                    ((IPlatform)this).Notify(L("Свёрнуто в трей"), L("TunPilot продолжает работать. Окно открывается кликом по иконке у часов."), NotifyKind.Info);
                }
            };
        }
        Window.Show();
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    bool _quitting;

    public async Task QuitAsync()
    {
        _quitting = true;
        if (_tray != null) _tray.IsVisible = false;
        await App.ShutdownAsync();
        SingleInstance.Release();
        Window?.Close();
        _desktop.Shutdown();
    }

    // ───────────────────────── трей ─────────────────────────

    void CreateTray()
    {
        try
        {
            _trayStatus = new NativeMenuItem(L("Отключено")) { IsEnabled = false };
            _trayToggle = new NativeMenuItem(L("Подключить"));
            _trayToggle.Click += async (_, _) => { if (App.Core.Running) await App.DisconnectAsync(); else await App.ConnectAsync(); };
            var open = new NativeMenuItem(L("Открыть окно"));
            open.Click += (_, _) => ShowWindow();
            var update = new NativeMenuItem(L("Обновить все подписки"));
            update.Click += async (_, _) => await App.UpdateAllAsync(manual: true);
            var quit = new NativeMenuItem(L("Выход"));
            quit.Click += async (_, _) => await QuitAsync();
            var menu = new NativeMenu();
            foreach (var i in new NativeMenuItemBase[] { open, _trayStatus, new NativeMenuItemSeparator(), _trayToggle, update, new NativeMenuItemSeparator(), quit })
                menu.Items.Add(i);
            _tray = new TrayIcon { Icon = AppIcon, ToolTipText = "TunPilot", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowWindow();
            var icons = new TrayIcons { _tray };
            TrayIcon.SetIcons(Application.Current!, icons);
        }
        catch { _tray = null; }   // нет области уведомлений (например, GNOME без расширения) — живём только окном
    }

    void RefreshTray()
    {
        bool on = App.Core.Running, busy = App.Busy;
        if (_trayStatus != null) _trayStatus.Header = busy ? L("Подключение…") : on ? Lf($"Подключено · {App.SelectedDisplay}") : L("Отключено");
        if (_trayToggle != null) { _trayToggle.Header = on ? L("Отключить") : L("Подключить"); _trayToggle.IsEnabled = !busy; }
        if (_tray != null) _tray.ToolTipText = "TunPilot — " + (on ? L("подключено") : L("отключено"));
        Window?.Refresh();
    }

    // ───────────────────────── IPlatform (Linux) ─────────────────────────

    void IPlatform.Notify(string title, string text, NotifyKind kind)
    {
        // системное уведомление (libnotify); если нет — в окне
        _notifySend ??= File.Exists("/usr/bin/notify-send");
        if (_notifySend == true)
        {
            try
            {
                var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
                psi.ArgumentList.Add("--app-name=TunPilot");
                psi.ArgumentList.Add("--icon=tunpilot");
                psi.ArgumentList.Add(kind == NotifyKind.Error ? "--urgency=critical" : "--urgency=normal");
                psi.ArgumentList.Add(title.Length > 0 ? title : "TunPilot");
                psi.ArgumentList.Add(text);
                Process.Start(psi)?.Dispose();
                return;
            }
            catch { _notifySend = false; }
        }
        Window?.Toast(title.Length > 0 ? title + ": " + text : text, kind);
    }

    void IPlatform.ShowError(string text) => Dispatcher.UIThread.Post(async () => await Dialogs.MessageAsync("TunPilot", text, error: true));
    void IPlatform.ShowInfo(string title, string text) => Dispatcher.UIThread.Post(async () => await Dialogs.MessageAsync(title, text));
    Task<bool> IPlatform.ConfirmAsync(string text) => Dialogs.ConfirmAsync(text);
    IKillSwitch IPlatform.KillSwitch => _ks;
    IBypassRoutes IPlatform.Bypass => _bypass;
    IAutostart IPlatform.Autostart => _autostart;
}
