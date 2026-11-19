using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TunPilot.LinuxApp;

sealed partial class MainWindow
{
    static readonly SpeedChart Chart = new();

    Control BuildTraffic()
    {
        var tabs = new TabControl { Margin = new Thickness(10, 4) };

        // подключения
        var conns = new StackPanel();
        var connsView = new ScrollViewer { Content = conns };
        tabs.Items.Add(new TabItem { Header = L("Подключения"), Content = connsView });

        // график
        tabs.Items.Add(new TabItem { Header = L("График"), Content = new Border { Padding = new Thickness(8), Child = Chart } });

        // логи
        var logPick = new ComboBox { ItemsSource = new[] { "sing-box", "xray" }, SelectedIndex = 0, Width = 140 };
        var logBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("monospace"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        var logs = new DockPanel();
        var logBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6), Children = { Ui.T(L("Журнал:")), logPick } };
        DockPanel.SetDock(logBar, Dock.Top);
        logs.Children.Add(logBar); logs.Children.Add(logBox);
        tabs.Items.Add(new TabItem { Header = L("Логи"), Content = logs });

        // сессия
        var session = new WrapPanel();
        tabs.Items.Add(new TabItem { Header = L("Сессия"), Content = new ScrollViewer { Content = session } });

        int tick = 0;
        _refreshPage = () =>
        {
            tick++;
            var m = App.Monitor;
            var s = m.Last;
            switch (tabs.SelectedIndex)
            {
                case 0:
                    conns.Children.Clear();
                    if (s == null || s.Conns.Count == 0) { conns.Children.Add(new Border { Padding = new Thickness(10), Child = Ui.T(App.Core.Running ? "—" : L("Отключено"), color: Ui.Muted) }); break; }
                    var head = ConnRow(L("Процесс"), L("Назначение"), "Outbound", L("Трафик ↑ / ↓"), Ui.Muted, Ui.Muted);
                    conns.Children.Add(head);
                    foreach (var g in s.Conns.GroupBy(c => c.Process).OrderByDescending(g => g.Sum(c => c.SpUp + c.SpDown)).Take(200))
                    {
                        var first = g.First();
                        var dest = g.Count() == 1 ? first.Dest : $"{first.Dest}  +{g.Count() - 1}";
                        bool direct = first.OutTag == "direct";
                        conns.Children.Add(ConnRow(g.Key, dest, first.Outbound,
                            $"{Ui.Fmt(g.Sum(c => c.Up))} / {Ui.Fmt(g.Sum(c => c.Down))}", Ui.Text, direct ? Ui.Direct : Ui.Accent));
                    }
                    break;
                case 2:
                    if (tick % 2 == 0 || logBox.Text is null or "")
                    {
                        var path = Path.Combine(AppPaths.Logs, $"{logPick.SelectedItem}.log");
                        try
                        {
                            var lines = File.Exists(path) ? File.ReadLines(path).TakeLast(400).ToList() : [];
                            var text = lines.Count > 0 ? string.Join("\n", lines) : L("(журнал пуст — ядро ещё не запускалось)");
                            if (text != logBox.Text) { logBox.Text = text; logBox.CaretIndex = text.Length; }
                        }
                        catch { }
                    }
                    break;
                case 3:
                    session.Children.Clear();
                    var p = Profiles.Current;
                    bool on = App.Core.Running;
                    session.Children.Add(SessionCard(L("Подключение"), [
                        (L("Состояние"), on ? L("подключено") : L("отключено"), on ? Ui.Accent : Ui.Muted),
                        (L("Время работы"), on ? (DateTime.Now - App.ConnectedAt).ToString(@"hh\:mm\:ss") : "—", (Color?)null),
                        (L("Сервер"), App.SelectedDisplay, null),
                        ("Kill-switch", App.Settings.KillSwitch ? L("включён") : L("выключен"), App.Settings.KillSwitch ? Ui.Accent : null)]));
                    session.Children.Add(SessionCard(L("Трафик за сессию"), [
                        (L("Через VPN"), $"↑ {Ui.Fmt(m.ProxyUp)}    ↓ {Ui.Fmt(m.ProxyDown)}", (Color?)null),
                        (L("Напрямую"), $"↑ {Ui.Fmt(m.DirectUp)}    ↓ {Ui.Fmt(m.DirectDown)}", null),
                        (L("Активных соединений"), (s?.Conns.Count ?? 0).ToString(), null)]));
                    session.Children.Add(SessionCard(L("Маршруты"), [
                        (L("Профиль"), p.Name, (Color?)null),
                        (L("Всё остальное"), p.DefaultOutbound == "direct" ? L("напрямую") : L("через VPN"), null),
                        (L("Мимо TUN"), on && App.RoutesInfo.Length > 0 ? App.RoutesInfo : "—", null),
                        (L("Наборы напрямую"), p.GeoDirect.Count > 0 ? string.Join(", ", p.GeoDirect) : "—", Ui.Direct),
                        (L("Наборы через VPN"), p.GeoProxy.Count > 0 ? string.Join(", ", p.GeoProxy) : "—", Ui.Accent)]));
                    session.Children.Add(SessionCard(L("Прочее"), [
                        (L("Группы / серверы"), $"{App.Groups.Groups.Count} / {App.AllServers.Count}", (Color?)null),
                        (L("Локальный прокси"), App.Settings.LocalProxyEnabled ? $"127.0.0.1:{App.Settings.LocalProxyPort}" : L("выключен"), null)]));
                    break;
            }
        };
        return tabs;
    }

    static Control ConnRow(string proc, string dest, string outbound, string traffic, Color text, Color outColor)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*,2*,160"), Margin = new Thickness(10, 4) };
        void C(string t, int i, Color c) { var x = Ui.T(t, 12.5, color: c); Grid.SetColumn(x, i); g.Children.Add(x); }
        C(proc, 0, text); C(dest, 1, text); C(outbound, 2, outColor); C(traffic, 3, text);
        return g;
    }

    static Control SessionCard(string title, List<(string Key, string Value, Color? Color)> items)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (int i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = Ui.T(items[i].Key, 13, color: Ui.Muted); k.Margin = new Thickness(0, 3, 18, 3); k.VerticalAlignment = VerticalAlignment.Top;
            var v = Ui.T(items[i].Value, 13, color: items[i].Color ?? Ui.Text, wrap: true); v.Margin = new Thickness(0, 3);
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            grid.Children.Add(k); grid.Children.Add(v);
        }
        var card = Ui.Card(new StackPanel { Spacing = 8, Children = { Ui.H(title), grid } });
        card.Width = 420; card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }
}

/// <summary>График скорости: последние 2 минуты, через VPN (зелёный) и напрямую (синий).</summary>
sealed class SpeedChart : Control
{
    readonly Queue<(long Proxy, long Direct)> _data = new();
    public SpeedChart()
    {
        // точки добавляет монитор трафика раз в секунду, даже когда график не на экране
        if (LinuxShell.Instance?.App is { } app) app.Monitor.Updated += s => Push(s.ProxyDown + s.ProxyUp, s.DirectDown + s.DirectUp);
    }

    public void Push(long proxy, long direct)
    {
        _data.Enqueue((proxy, direct));
        while (_data.Count > 120) _data.Dequeue();
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        g.FillRectangle(Ui.B(Ui.Panel), new Rect(Bounds.Size));
        if (w < 50 || h < 50) return;
        var max = Math.Max(1024 * 128, _data.Count == 0 ? 0 : _data.Max(d => Math.Max(d.Proxy, d.Direct)));
        var gridPen = new Pen(Ui.B(Ui.Grid), 1);
        for (int i = 1; i <= 4; i++)
        {
            double y = h - h * i / 4.0;
            g.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            var ft = new FormattedText(Ui.Rate(max * i / 4), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 11, Ui.B(Ui.Muted));
            g.DrawText(ft, new Point(6, y + 2));
        }
        void Line(Func<(long, long), long> pick, Color c)
        {
            var arr = _data.ToArray();
            if (arr.Length < 2) return;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var p = new Point(w - (arr.Length - 1 - i) * w / 119.0, h - h * pick(arr[i]) / (double)max);
                    if (i == 0) ctx.BeginFigure(p, false); else ctx.LineTo(p);
                }
            }
            g.DrawGeometry(null, new Pen(Ui.B(c), 2), geo);
        }
        Line(d => d.Item2, Ui.Direct);
        Line(d => d.Item1, Ui.Accent);
    }
}
