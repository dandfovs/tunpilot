using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TunPilot.LinuxApp;

/// <summary>Всплывающий список серверов под карточкой на «Главной»: группы, поиск, плашки, пинг цветом.
/// Повторный клик по карточке или по выбранному серверу закрывает список.</summary>
static class ServerPicker
{
    static Flyout? _open;
    static DateTime _closedAt;

    public static void Show(Control anchor, AppController app)
    {
        if (_open != null) { _open.Hide(); return; }
        // клик по карточке при открытом списке: список уже закрылся «кликом мимо» — не открываем заново
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 350) return;
        var win = LinuxShell.Instance!.Window!;
        Group? group = app.ActiveGroup;
        var groups = new WrapPanel { Margin = new Thickness(8, 8, 8, 0) };
        var search = new TextBox { Watermark = L("Поиск сервера"), Margin = new Thickness(10, 8) };
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, Height = 330 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };

        void Fill()
        {
            list.Children.Clear();
            if (group == null) return;
            var q = search.Text?.Trim() ?? "";
            var items = new List<(string Key, string Tag, string Name, ServerInfo? S)>();
            if (q.Length == 0) items.Add((group.AutoKey, group.AutoTag, L("Авто — самый быстрый"), null));
            items.AddRange(app.ServersOf(group).Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(s => (s.Key, s.Tag, s.Name, (ServerInfo?)s)));
            foreach (var it in items)
            {
                bool sel = app.Settings.SelectedServer == it.Key;
                int? d = null;
                var delay = MainWindow.Testing.Contains(it.Tag) ? "…" : MainWindow.Delay.TryGetValue(it.Tag, out d) ? (d is { } ms ? Lf($"{ms} мс") : L("таймаут")) : "";
                var dc = d is null ? Ui.Danger : d < 200 ? Ui.Accent : d < 500 ? Ui.Warn : Ui.Orange;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("62,*,Auto") };
                var badge = Badge.For(it.S, auto: it.S == null);
                var name = Ui.T(it.Name, 13.5, sel ? FontWeight.SemiBold : FontWeight.Normal, sel ? Ui.Accent : Ui.Text);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var dt = Ui.T(delay, 12.5, color: delay == "…" ? Ui.Muted : dc);
                Grid.SetColumn(name, 1); Grid.SetColumn(dt, 2);
                grid.Children.Add(badge); grid.Children.Add(name); grid.Children.Add(dt);
                var row = new Border { Padding = new Thickness(10, 8), Child = grid, Background = Ui.B(sel ? Ui.SelRow : Ui.Panel), Cursor = new Cursor(StandardCursorType.Hand) };
                row.PointerEntered += (_, _) => { if (!sel) row.Background = Ui.B(Ui.Header); };
                row.PointerExited += (_, _) => { if (!sel) row.Background = Ui.B(Ui.Panel); };
                var key = it.Key;
                row.PointerReleased += async (_, _) =>
                {
                    flyout.Hide();
                    if (!sel) await app.SelectServerAsync(key);
                };
                list.Children.Add(row);
            }
            if (items.Count == 0) list.Children.Add(new Border { Padding = new Thickness(12), Child = Ui.T(L("Ничего не найдено"), color: Ui.Muted) });
        }

        void BuildGroups()
        {
            groups.Children.Clear();
            foreach (var g in app.Groups.Groups)
            {
                var b = new Button { Content = g.Name, Classes = { g.Id == group?.Id ? "primary" : "flat" }, Margin = new Thickness(0, 0, 6, 6) };
                var gg = g;
                b.Click += (_, _) => { group = gg; BuildGroups(); Fill(); };
                groups.Children.Add(b);
            }
        }

        search.TextChanged += (_, _) => Fill();
        var test = Ui.BtnAsync(L("Проверить задержку"), async () =>
        {
            if (group == null) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) => Fill();
            timer.Start();
            try { await win.TestAsync(app.ServersOf(group).Select(s => s.Tag).ToList()); }
            finally { timer.Stop(); Fill(); }
        });
        var all = Ui.Btn(L("Все серверы…"), () => { flyout.Hide(); win.ShowPage("servers"); });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 8), Children = { test, all } };

        var root = new DockPanel { Width = Math.Max(400, anchor.Bounds.Width) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        if (app.Groups.Groups.Count > 1) { DockPanel.SetDock(groups, Dock.Top); root.Children.Add(groups); }
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(scroll);

        BuildGroups();
        Fill();
        flyout.Content = new Border { Background = Ui.B(Ui.Panel), BorderBrush = Ui.B(Ui.Grid), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
        flyout.FlyoutPresenterClasses.Add("picker");
        flyout.Opened += (_, _) => search.Focus();
        flyout.Closed += (_, _) => { _open = null; _closedAt = DateTime.UtcNow; };
        _open = flyout;
        flyout.ShowAt(anchor);
    }
}
