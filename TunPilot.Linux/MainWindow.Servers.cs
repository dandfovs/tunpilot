using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TunPilot.LinuxApp;

sealed partial class MainWindow
{
    CancellationTokenSource? _speedCts;

    sealed class Row
    {
        public string Key = "", Tag = "";
        public ServerInfo? Server;
        public Border Root = null!;
        public TextBlock Mark = null!, Name = null!, DelayText = null!, SpeedText = null!;
    }

    Control BuildServers()
    {
        var groupBox = new ComboBox { MinWidth = 220 };
        var list = new StackPanel();
        var rows = new List<Row>();
        var subInfo = Ui.T("", 12.5, color: Ui.Muted);
        Row? selected = null;
        string shownSig = "";

        void Rebuild()
        {
            var g = App.ActiveGroup;
            var servers = g != null ? App.ServersOf(g) : [];
            var sig = (g?.Id ?? "") + "|" + string.Join(",", servers.Select(s => s.Key));
            if (sig == shownSig) return;
            shownSig = sig;
            list.Children.Clear(); rows.Clear(); selected = null;
            if (g == null) return;
            void Add(string key, string tag, ServerInfo? srv)
            {
                var r = new Row { Key = key, Tag = tag, Server = srv };
                r.Mark = Ui.T("", 14, color: Ui.Accent);
                r.Name = Ui.T(srv?.Name ?? L("Авто — самый быстрый"));
                r.DelayText = Ui.T("", 13); r.DelayText.HorizontalAlignment = HorizontalAlignment.Right;
                r.SpeedText = Ui.T("", 13); r.SpeedText.HorizontalAlignment = HorizontalAlignment.Right;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,62,*,100,130") };
                void Col(Control c, int i) { Grid.SetColumn(c, i); grid.Children.Add(c); }
                Col(r.Mark, 0); Col(Badge.For(srv, auto: srv == null), 1); Col(r.Name, 2); Col(r.DelayText, 3); Col(r.SpeedText, 4);
                r.Root = new Border { Padding = new Thickness(10, 7), Child = grid, Background = Ui.B(rows.Count % 2 == 0 ? Ui.Panel : Ui.PanelAlt), Cursor = new Cursor(StandardCursorType.Hand) };
                r.Root.PointerPressed += (_, e) =>
                {
                    selected = r;
                    if (e.ClickCount >= 2) _ = App.SelectServerAsync(r.Key);
                    Paint();
                };
                var menu = new ContextMenu();
                var miSelect = new MenuItem { Header = L("Выбрать сервер") }; miSelect.Click += (_, _) => _ = App.SelectServerAsync(r.Key);
                var miTest = new MenuItem { Header = L("Тест задержки") }; miTest.Click += (_, _) => _ = TestAsync([r.Tag]);
                var miSpeed = new MenuItem { Header = L("Тест скорости") }; miSpeed.Click += (_, _) => _ = SpeedAsync([r.Tag]);
                menu.Items.Add(miSelect); menu.Items.Add(miTest); menu.Items.Add(miSpeed);
                if (srv != null)
                {
                    var miCopy = new MenuItem { Header = L("Копировать ссылку") };
                    miCopy.Click += async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(srv.Raw); };
                    var miDel = new MenuItem { Header = L("Удалить") };
                    miDel.Click += async (_, _) => { if (App.ActiveGroup is { } ag) await App.RemoveServersAsync(ag, [srv]); };
                    menu.Items.Add(new Separator()); menu.Items.Add(miCopy); menu.Items.Add(miDel);
                }
                r.Root.ContextMenu = menu;
                rows.Add(r);
                list.Children.Add(r.Root);
            }
            Add(g.AutoKey, g.AutoTag, null);
            foreach (var s in servers) Add(s.Key, s.Tag, s);
        }

        void Paint()
        {
            var sel = App.Settings.SelectedServer;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                bool isSel = sel == r.Key;
                r.Mark.Text = isSel ? "✓" : "";
                r.Name.FontWeight = isSel ? FontWeight.SemiBold : FontWeight.Normal;
                r.Root.Background = Ui.B(isSel ? Ui.SelRow : r == selected ? Ui.Header : i % 2 == 0 ? Ui.Panel : Ui.PanelAlt);
                if (Testing.Contains(r.Tag)) { r.DelayText.Text = "…"; r.DelayText.Foreground = Ui.B(Ui.Muted); }
                else if (Delay.TryGetValue(r.Tag, out var d))
                {
                    r.DelayText.Text = d is { } ms ? Lf($"{ms} мс") : L("таймаут");
                    r.DelayText.Foreground = Ui.B(d is null ? Ui.Danger : d < 200 ? Ui.Accent : d < 500 ? Ui.Warn : Ui.Orange);
                }
                else r.DelayText.Text = "";
                if (Speed.TryGetValue(r.Tag, out var sp))
                {
                    r.SpeedText.Text = sp is { } m ? Lf($"{m:0.0} Мбит/с") : L("ошибка");
                    r.SpeedText.Foreground = Ui.B(sp is null ? Ui.Danger : sp >= 50 ? Ui.Accent : sp >= 10 ? Ui.Warn : Ui.Orange);
                }
                else r.SpeedText.Text = "";
            }
        }

        void FillGroups()
        {
            var names = App.Groups.Groups.Select(g => $"{g.Name}  ({App.ServersOf(g).Count})").ToList();
            if (groupBox.ItemCount != names.Count || !names.SequenceEqual(groupBox.Items.Cast<string>()))
            {
                groupBox.ItemsSource = names;
            }
            var idx = App.Groups.Groups.FindIndex(g => g.Id == App.ActiveGroup?.Id);
            if (groupBox.SelectedIndex != idx) groupBox.SelectedIndex = idx;
            var gi = App.ActiveGroup?.Info;
            subInfo.Text = gi == null ? "" : (gi.Title.Length > 0 ? gi.Title + " · " : "") +
                (gi.Total > 0 ? Lf($"осталось {Ui.Fmt(Math.Max(0, gi.Total - gi.Upload - gi.Download))} из {Ui.Fmt(gi.Total)}")
                              : Lf($"использовано {Ui.Fmt(gi.Upload + gi.Download)}, без лимита"));
        }
        groupBox.SelectionChanged += (_, _) =>
        {
            if (groupBox.SelectedIndex >= 0 && groupBox.SelectedIndex < App.Groups.Groups.Count && App.Groups.Groups[groupBox.SelectedIndex].Id != App.ActiveGroup?.Id)
                App.SetActiveGroup(App.Groups.Groups[groupBox.SelectedIndex]);
        };

        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[]
        {
            groupBox,
            Ui.BtnAsync(L("Тест задержки"), () => TestAsync(rows.Select(r => r.Tag).ToList())),
            Ui.BtnAsync(L("Тест скорости"), () => SpeedAsync(selected is { Server: not null } s ? [s.Tag] : [App.SelectedTag])),
            Ui.BtnAsync(L("Обновить подписку"), async () => { if (App.ActiveGroup is { IsSubscription: true } g) await App.UpdateGroupAsync(g, manual: true, force: true); }),
            Ui.Btn(L("Группы…"), ShowGroups),
            Ui.BtnAsync(L("Вставить ключи из буфера"), async () =>
            {
                var text = Clipboard != null ? await Clipboard.TryGetTextAsync() ?? "" : "";
                var n = await App.AddLinksAsync(text);
                Toast(n > 0 ? Lf($"Добавлено ключей: {n}.") : L("В буфере нет ссылок на серверы (vless://, hy2://…)."), NotifyKind.Info);
            }),
        })
        { c.Margin = new Thickness(0, 0, 8, 8); bar.Children.Add(c); }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("34,62,*,100,130"), Margin = new Thickness(10, 0) };
        void H(string t, int i, bool right = false) { var x = Ui.T(t, 12.5, color: Ui.Muted); if (right) x.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(x, i); header.Children.Add(x); }
        H(L("Сервер"), 2); H(L("Задержка"), 3, true); H(L("Скорость"), 4, true);

        var page = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Spacing = 6, Children = { bar, subInfo, header } };
        DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(top);
        page.Children.Add(new Border { BorderBrush = Ui.B(Ui.Grid), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = list } });

        _refreshPage = () => { FillGroups(); Rebuild(); Paint(); };
        return page;
    }

    /// <summary>Тест задержки: подключены — через рабочее ядро, нет — через временное (без TUN).</summary>
    internal async Task TestAsync(List<string> tags)
    {
        var real = tags.Where(t => !t.StartsWith("auto-")).ToList();
        if (real.Count == 0) real = App.ActiveGroup is { } g ? App.ServersOf(g).Select(s => s.Tag).ToList() : [];
        foreach (var t in real) { Testing.Add(t); Delay.Remove(t); }
        Refresh();
        void Done(string tag, int? d) => Dispatcher.UIThread.Post(() => { Testing.Remove(tag); Delay[tag] = d; Refresh(); });
        try
        {
            if (App.Core.Running)
            {
                using var gate = new SemaphoreSlim(6);
                await Task.WhenAll(real.Select(async tag =>
                {
                    await gate.WaitAsync();
                    try { Done(tag, await App.Core.DelayAsync(tag)); } finally { gate.Release(); }
                }));
            }
            else await Task.Run(() => App.Core.TestOfflineAsync(App.Settings, App.AllServers, real, _ => { }, Done));
        }
        catch (Exception ex) { Toast(L("Тест задержки: ") + ex.Message, NotifyKind.Warning); }
        finally { foreach (var t in real) Testing.Remove(t); Refresh(); }
    }

    async Task SpeedAsync(List<string> tags)
    {
        tags = tags.Where(t => t.Length > 0 && !t.StartsWith("auto-")).Distinct().ToList();
        if (tags.Count == 0 || _speedCts != null) return;
        _speedCts = new CancellationTokenSource();
        void Progress(string tag, double mbps) => Dispatcher.UIThread.Post(() => { Speed[tag] = mbps; Refresh(); });
        void Done(string tag, double? mbps) => Dispatcher.UIThread.Post(() => { Speed[tag] = mbps; Refresh(); });
        try { await Task.Run(() => App.Core.SpeedTestAsync(App.Settings, App.AllServers, tags, Progress, Done, _speedCts.Token)); }
        catch (Exception ex) { Toast(L("Тест скорости: ") + ex.Message, NotifyKind.Warning); }
        finally { _speedCts = null; Refresh(); }
    }
}
