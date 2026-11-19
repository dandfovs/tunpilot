using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TunPilot.LinuxApp;

/// <summary>Главное окно: боковое меню (Главная / Серверы / Маршруты / Трафик / Настройки) и разделы.</summary>
sealed partial class MainWindow : Window
{
    readonly LinuxShell _shell;
    AppController App => _shell.App;
    readonly ContentControl _host = new();
    readonly StackPanel _navTop = new() { Spacing = 2 };
    readonly StackPanel _navBottom = new() { Spacing = 2 };
    readonly Dictionary<string, (Border Button, Func<Control> Page)> _nav = new();
    readonly TextBlock _status = Ui.T("", 12.5, color: Ui.Muted);
    readonly TextBlock _statusRight = Ui.T("", 12.5, color: Ui.Muted);
    readonly Border _toast = new() { IsVisible = false, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 520 };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    string _current = "";
    Action? _refreshPage;

    /// <summary>Результаты тестов (общие для разделов).</summary>
    public static readonly Dictionary<string, int?> Delay = new();
    public static readonly Dictionary<string, double?> Speed = new();
    public static readonly HashSet<string> Testing = new();

    public MainWindow(LinuxShell shell)
    {
        _shell = shell;
        Title = "TunPilot";
        Icon = LinuxShell.AppIcon;
        Width = 1060; Height = 720; MinWidth = 720; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.B(Ui.Bg);

        var nav = new DockPanel { Background = Ui.B(Ui.Panel), Width = 92 };
        DockPanel.SetDock(_navBottom, Dock.Bottom);
        nav.Children.Add(_navBottom);
        nav.Children.Add(_navTop);
        AddNav("home", L("Главная"), Glyph.Home, BuildHome, _navTop);
        AddNav("servers", L("Серверы"), Glyph.Server, BuildServers, _navTop);
        AddNav("routes", L("Маршруты"), Glyph.Routes, BuildRoutes, _navTop);
        AddNav("traffic", L("Трафик"), Glyph.Chart, BuildTraffic, _navTop);
        AddNav("settings", L("Настройки"), Glyph.Settings, BuildSettings, _navBottom);

        var status = new DockPanel { Background = Ui.B(Ui.Panel), Height = 28 };
        var sl = new Border { Padding = new Thickness(12, 0), Child = _status };
        var sr = new Border { Padding = new Thickness(12, 0), Child = _statusRight };
        DockPanel.SetDock(sr, Dock.Right);
        status.Children.Add(sr); status.Children.Add(sl);

        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(nav, Dock.Left);
        root.Children.Add(status);
        root.Children.Add(nav);
        root.Children.Add(_host);
        Content = new Panel { Children = { root, _toast } };

        _tick.Tick += (_, _) => { if (IsVisible) Refresh(); };
        _tick.Start();
        ShowPage("home");
        Refresh();
    }

    void AddNav(string key, string text, string glyph, Func<Control> page, StackPanel where)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = Glyph.Parse(glyph), Width = 22, Height = 22, Stretch = Stretch.Uniform, Fill = Ui.B(Ui.Muted), HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 11.5, Foreground = Ui.B(Ui.Muted), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var b = new Border
        {
            Height = 64, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, BorderThickness = new Thickness(3, 0, 0, 0),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } },
        };
        b.PointerReleased += (_, _) => ShowPage(key);
        b.PointerEntered += (_, _) => { if (_current != key) b.Background = Ui.B(Ui.PanelAlt); };
        b.PointerExited += (_, _) => { if (_current != key) b.Background = Brushes.Transparent; };
        where.Children.Add(b);
        _nav[key] = (b, page);
    }

    public void ShowPage(string key)
    {
        _current = key;
        foreach (var (k, (b, _)) in _nav)
        {
            bool sel = k == key;
            b.Background = sel ? Ui.B(Ui.Header) : Brushes.Transparent;
            b.BorderBrush = sel ? Ui.B(Ui.Accent) : Brushes.Transparent;
            var sp = (StackPanel)b.Child!;
            ((Avalonia.Controls.Shapes.Path)sp.Children[0]).Fill = Ui.B(sel ? Ui.Accent : Ui.Muted);
            ((TextBlock)sp.Children[1]).Foreground = Ui.B(sel ? Ui.Accent : Ui.Muted);
        }
        _refreshPage = null;
        _host.Content = _nav[key].Page();
        Refresh();
    }

    /// <summary>Обновить то, что на экране (раз в секунду и при изменении состояния).</summary>
    public void Refresh()
    {
        bool on = App.Core.Running;
        Title = "TunPilot — " + (App.Busy ? L("подключение…") : on ? L("подключено") : L("отключено"));
        _status.Text = App.PendingSubUpdate ? L("Изменения подписок применятся при переподключении") : on ? App.SelectedDisplay : L("Отключено");
        _status.Foreground = Ui.B(on ? Ui.Accent : Ui.Muted);
        _statusRight.Text = Profiles.Current.Name;
        _refreshPage?.Invoke();
    }

    /// <summary>Уведомление внутри окна (если в системе нет notify-send).</summary>
    public void Toast(string text, NotifyKind kind)
    {
        _toast.Background = Ui.B(Ui.Header);
        _toast.BorderBrush = Ui.B(kind == NotifyKind.Error ? Ui.Danger : kind == NotifyKind.Warning ? Ui.Warn : Ui.Accent);
        _toast.BorderThickness = new Thickness(1);
        _toast.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B(Ui.Text) };
        _toast.IsVisible = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) => { t.Stop(); _toast.IsVisible = false; };
        t.Start();
    }

    public void ShowGroups() => _ = GroupsWindow.ShowAsync(this, App);

    /// <summary>Выбрать вкладку на текущей странице (для снимков экрана при разработке).</summary>
    public void SelectTab(int i)
    {
        var tabs = (_host.Content as Control)?.GetLogicalDescendants().OfType<TabControl>().FirstOrDefault()
                   ?? _host.Content as TabControl;
        if (tabs != null && i < tabs.ItemCount) tabs.SelectedIndex = i;
    }

    // ───────────────────────── Главная ─────────────────────────

    Control BuildHome()
    {
        var button = new ConnectButton { HorizontalAlignment = HorizontalAlignment.Center };
        button.Clicked += async () => { if (App.Busy) return; if (App.Core.Running) await App.DisconnectAsync(); else await App.ConnectAsync(); };
        var speed = Ui.T("", 13, color: Ui.Muted); speed.HorizontalAlignment = HorizontalAlignment.Center;
        var hint = Ui.T("", 13, color: Ui.Muted); hint.HorizontalAlignment = HorizontalAlignment.Center;

        // карточка сервера
        var badgeHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.T("", 15, FontWeight.SemiBold);
        var sub = Ui.T("", 12.5, color: Ui.Muted);
        var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,30") };
        cardGrid.Children.Add(badgeHost);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };
        Grid.SetColumn(texts, 1); cardGrid.Children.Add(texts);
        // стрелка — линией, а не символом: «⌄» есть не во всех шрифтах (на Fedora показывался квадратиками)
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L5,5 L10,0"), Stroke = Ui.B(Ui.Muted), StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 11, Height = 7,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 2); cardGrid.Children.Add(chevron);
        var card = new Border { Background = Ui.B(Ui.Panel), BorderBrush = Ui.B(Ui.Grid), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Child = cardGrid, Cursor = new Cursor(StandardCursorType.Hand) };
        card.PointerEntered += (_, _) => card.Background = Ui.B(Ui.Header);
        card.PointerExited += (_, _) => card.Background = Ui.B(Ui.Panel);
        card.PointerReleased += (_, _) => ServerPicker.Show(card, App);

        var tProfile = new Tile("", false);
        var tKill = new Tile("Kill-switch", true);
        var tDota = new Tile(L("Dota мимо VPN"), true);
        var tLists = new Tile(L("Мои исключения"), false);
        tProfile.Activated += () => ShowPage("routes");
        tKill.Activated += () => App.SetKillSwitch(!App.Settings.KillSwitch);
        tDota.Activated += async () => await App.SetSteamBypassAsync(!App.Settings.SteamSdrBypass);
        tLists.Activated += () => _ = ListEditorWindow.ShowAsync(this, App);
        ToolTip.SetTip(tKill, KillSwitch_Help);
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), RowDefinitions = new RowDefinitions("Auto,10,Auto") };
        void Place(Control c, int col, int row) { Grid.SetColumn(c, col); Grid.SetRow(c, row); tiles.Children.Add(c); }
        Place(tProfile, 0, 0); Place(tKill, 2, 0); Place(tDota, 0, 2); Place(tLists, 2, 2);

        // подписка
        var subLeft = Ui.T("", 12.5, color: Ui.Muted);
        var subRight = Ui.T("", 12.5, color: Ui.Muted);
        var bar = new ProgressBar { Height = 5, MinHeight = 5, Minimum = 0, Maximum = 1, Foreground = Ui.B(Ui.Accent), Background = Ui.B(Ui.Grid) };
        var announce = Ui.T("", 12.5, color: Ui.Direct, wrap: true);
        var subRow = new DockPanel();
        DockPanel.SetDock(subRight, Dock.Right); subRow.Children.Add(subRight); subRow.Children.Add(subLeft);
        var subPanel = new StackPanel { Spacing = 6, Children = { subRow, bar, announce } };

        var column = new StackPanel { Width = 460, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 40) };
        column.Children.Add(button);
        column.Children.Add(speed);
        column.Children.Add(hint);
        column.Children.Add(card);
        column.Children.Add(tiles);
        column.Children.Add(subPanel);

        _refreshPage = () =>
        {
            bool on = App.Core.Running, busy = App.Busy;
            button.Set(busy ? ConnectButton.State.Busy : on ? ConnectButton.State.On : ConnectButton.State.Off,
                on ? L("в сети ") + (DateTime.Now - App.ConnectedAt).ToString(@"hh\:mm\:ss") : busy ? L("это займёт пару секунд") : L("нажмите, чтобы подключиться"));
            var s = App.Monitor.Last;
            speed.Text = on ? $"↓ {Ui.Rate(s?.ProxyDown ?? 0)}   ↑ {Ui.Rate(s?.ProxyUp ?? 0)}" : "";
            hint.Text = App.PendingSubUpdate ? L("Подписка обновилась — изменения применятся при переподключении")
                : on && App.RoutesInfo.Length > 0 ? L("Игры и исключения: ") + App.RoutesInfo : "";

            var key = App.Settings.SelectedServer;
            if (key.StartsWith("auto:"))
            {
                var ag = App.Groups.Find(key[5..]) ?? App.ActiveGroup;
                var nowTag = ag != null ? App.Monitor.AutoNow.GetValueOrDefault(ag.AutoTag) : null;
                var now = nowTag != null ? App.AllServers.FirstOrDefault(x => x.Tag == nowTag) : null;
                badgeHost.Content = Badge.For(null, auto: true);
                name.Text = L("Авто — самый быстрый");
                sub.Text = (ag?.Name ?? "—") + (now != null ? Lf($" · сейчас {now.Name}") : "");
            }
            else if (App.AllServers.FirstOrDefault(x => x.Key == key) is { } srv)
            {
                badgeHost.Content = Badge.For(srv);
                name.Text = srv.Name;
                sub.Text = (App.Groups.Find(srv.GroupId)?.Name ?? "") + DelayText(srv.Tag);
            }
            else
            {
                badgeHost.Content = null;
                name.Text = App.AllServers.Count == 0 ? L("Нет серверов") : L("Сервер не выбран");
                sub.Text = App.AllServers.Count == 0 ? L("Добавьте подписку: Серверы → Группы") : L("Нажмите «Сменить»");
            }

            tProfile.Title = Profiles.Current.Name;
            tKill.Checked = App.Settings.KillSwitch;
            tDota.Checked = App.Settings.SteamSdrBypass;

            var g = App.ActiveGroup is { IsSubscription: true, Info: not null } ag2 ? ag2 : App.Groups.Groups.FirstOrDefault(x => x is { IsSubscription: true, Info: not null });
            subPanel.IsVisible = g != null;
            if (g?.Info is { } i)
            {
                long used = i.Upload + i.Download;
                subLeft.Text = (i.Title.Length > 0 ? i.Title : g.Name) + " · " +
                    (i.Total > 0 ? Lf($"осталось {Ui.Fmt(Math.Max(0, i.Total - used))} из {Ui.Fmt(i.Total)}") : Lf($"использовано {Ui.Fmt(used)}, без лимита"));
                bool expired = i.Expire > 0 && DateTimeOffset.FromUnixTimeSeconds(i.Expire) < DateTimeOffset.Now;
                subRight.Text = i.Expire > 0 ? (expired ? L("истекла ") : L("до ")) + DateTimeOffset.FromUnixTimeSeconds(i.Expire).LocalDateTime.ToString("dd.MM.yyyy") : "";
                subRight.Foreground = Ui.B(expired ? Ui.Danger : Ui.Muted);
                double frac = i.Total > 0 ? Math.Min(1, used / (double)i.Total) : 1;
                bar.Value = frac;
                bar.Foreground = Ui.B(i.Total <= 0 ? Ui.Accent : frac > 0.9 ? Ui.Danger : frac > 0.7 ? Ui.Warn : Ui.Accent);
                announce.Text = i.Announce;
                announce.IsVisible = i.Announce.Length > 0;
            }
        };
        return new ScrollViewer { Content = column };
    }

    static string KillSwitch_Help =>
        L("Kill-switch не пускает трафик в интернет мимо VPN.\n\n") +
        L("• Включается при подключении: интернет идёт только через TUN.\n") +
        L("• Если ядро или TunPilot упали — интернет блокируется.\n") +
        L("• Работают: локальная сеть и исключения «мимо TUN» (Dota, Steam).\n\n");

    public static string DelayText(string tag) =>
        Delay.TryGetValue(tag, out var d) ? (d is { } ms ? Lf($" · {ms} мс") : L(" · таймаут")) : "";
}
