using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TunPilot.LinuxApp;

/// <summary>Значки (векторные, Material Design Icons — Apache 2.0).</summary>
static class Glyph
{
    public const string Home = "M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z";
    public const string Server = "M4,1H20A1,1 0 0,1 21,2V6A1,1 0 0,1 20,7H4A1,1 0 0,1 3,6V2A1,1 0 0,1 4,1M4,9H20A1,1 0 0,1 21,10V14A1,1 0 0,1 20,15H4A1,1 0 0,1 3,14V10A1,1 0 0,1 4,9M4,17H20A1,1 0 0,1 21,18V22A1,1 0 0,1 20,23H4A1,1 0 0,1 3,22V18A1,1 0 0,1 4,17Z";
    public const string Routes = "M13,14C9.64,14 8.54,15.35 8.18,16.24C9.25,16.7 10,17.76 10,19A3,3 0 0,1 7,22A3,3 0 0,1 4,19C4,17.69 4.83,16.58 6,16.17V7.83C4.83,7.42 4,6.31 4,5A3,3 0 0,1 7,2A3,3 0 0,1 10,5C10,6.31 9.17,7.42 8,7.83V13.12C8.88,12.47 10.16,12 12,12C14.67,12 15.56,10.66 15.85,9.77C14.77,9.32 14,8.25 14,7A3,3 0 0,1 17,4A3,3 0 0,1 20,7C20,8.34 19.12,9.5 17.91,9.86C17.65,11.29 16.68,14 13,14Z";
    public const string Chart = "M3,14L3.5,14.07L8.07,9.5C7.89,8.85 8.06,8.11 8.59,7.59C9.37,6.8 10.63,6.8 11.41,7.59C11.94,8.11 12.11,8.85 11.93,9.5L14.5,12.07L15,12C15.18,12 15.35,12 15.5,12.07L19.07,8.5C19,8.35 19,8.18 19,8A2,2 0 0,1 21,6A2,2 0 0,1 23,8A2,2 0 0,1 21,10C20.82,10 20.65,10 20.5,9.93L16.93,13.5C17,13.65 17,13.82 17,14A2,2 0 0,1 15,16A2,2 0 0,1 13,14L13.07,13.5L10.5,10.93C10.18,11 9.82,11 9.5,10.93L4.93,15.5L5,16A2,2 0 0,1 3,18A2,2 0 0,1 1,16A2,2 0 0,1 3,14Z";
    public const string Settings = "M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z";

    public static Geometry? Parse(string d) { try { return Geometry.Parse(d); } catch { return null; } }
}

/// <summary>Большая круглая кнопка подключения: кольцо, свечение, значок питания, подписи под кругом.</summary>
sealed class ConnectButton : Control
{
    public enum State { Off, Busy, On }
    State _state;
    string _detail = "";
    bool _hover;
    double _angle;
    readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public ConnectButton()
    {
        Width = 320; Height = 230;
        Cursor = new Cursor(StandardCursorType.Hand);
        _spin.Tick += (_, _) => { _angle = (_angle + 7) % 360; InvalidateVisual(); };
    }

    public void Set(State s, string detail)
    {
        if (s != _state) { _state = s; if (s == State.Busy) _spin.Start(); else _spin.Stop(); }
        _detail = detail;
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e) { _hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
    protected override void OnPointerExited(PointerEventArgs e) { _hover = false; InvalidateVisual(); base.OnPointerExited(e); }

    public event Action? Clicked;
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { Clicked?.Invoke(); base.OnPointerReleased(e); }

    public override void Render(DrawingContext g)
    {
        var accent = _state switch { State.On => Ui.Accent, State.Busy => Ui.Warn, _ => _hover ? Ui.Blend(Ui.Muted, Ui.Accent, 0.75) : Ui.Muted };
        double cx = Bounds.Width / 2, cy = 98, r = _hover ? 65 : 62;
        if (_state == State.On || _hover)
            for (int i = 7; i >= 1; i--)
            {
                double rr = r + i * 4.5;
                g.DrawEllipse(new SolidColorBrush(accent, (_state == State.On ? 13 : 7) / 255.0 * (8 - i) / 4), null, new Point(cx, cy), rr, rr);
            }
        g.DrawEllipse(Ui.B(Ui.Blend(Ui.Panel, accent, _hover ? 0.18 : 0.10)), null, new Point(cx, cy), r, r);
        if (_state == State.Busy)
        {
            g.DrawEllipse(null, new Pen(new SolidColorBrush(accent, 0.25), 4), new Point(cx, cy), r, r);
            var a1 = _angle * Math.PI / 180; var a2 = (_angle + 110) * Math.PI / 180;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), false);
                c.ArcTo(new Point(cx + r * Math.Cos(a2), cy + r * Math.Sin(a2)), new Size(r, r), 0, false, SweepDirection.Clockwise);
            }
            g.DrawGeometry(null, new Pen(Ui.B(accent), 4, lineCap: PenLineCap.Round), geo);
        }
        else g.DrawEllipse(null, new Pen(Ui.B(_state == State.Off ? Ui.Blend(Ui.Grid, accent, _hover ? 1 : 0.35) : accent), 4), new Point(cx, cy), r, r);
        g.DrawEllipse(Ui.B(Ui.Blend(Ui.Bg, accent, _hover ? 0.12 : 0.05)), null, new Point(cx, cy), r - 13, r - 13);

        // значок питания
        var pen = new Pen(Ui.B(_state == State.Off ? Ui.Blend(Ui.Muted, accent, _hover ? 1 : 0.4) : accent), 5.5, lineCap: PenLineCap.Round);
        const double ir = 20;
        var arc = new StreamGeometry();
        using (var c = arc.Open())
        {
            double s = -58 * Math.PI / 180, en = 238 * Math.PI / 180;
            c.BeginFigure(new Point(cx + ir * Math.Cos(s), cy + 3 + ir * Math.Sin(s)), false);
            c.ArcTo(new Point(cx + ir * Math.Cos(en), cy + 3 + ir * Math.Sin(en)), new Size(ir, ir), 0, true, SweepDirection.Clockwise);
        }
        g.DrawGeometry(null, pen, arc);
        g.DrawLine(pen, new Point(cx, cy - 25), new Point(cx, cy - 3));

        var title = _state switch { State.On => L("Подключено"), State.Busy => L("Подключение…"), _ => L("Отключено") };
        DrawCentered(g, title, 20, FontWeight.Bold, _state == State.Off ? Ui.Text : accent, 182);
        DrawCentered(g, _detail, 13, FontWeight.Normal, Ui.Muted, 212);
    }

    void DrawCentered(DrawingContext g, string text, double size, FontWeight w, Color c, double y)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, w), size, Ui.B(c));
        g.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, y - ft.Height / 2));
    }
}

/// <summary>Цветная плашка протокола (VLESS, XHTTP, HY2…).</summary>
static class Badge
{
    public static Border For(ServerInfo? s, bool auto = false)
    {
        var (text, color) = auto ? ("AUTO", Ui.Warn) : Protocol(s!);
        return new Border
        {
            Width = 50, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(color, 0.6), Background = new SolidColorBrush(color, 0.16),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Ui.B(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    static (string, Color) Protocol(ServerInfo s)
    {
        string proto = s.Protocol, net = s.P("type", "tcp");
        if (s.XrayConfig?["outbounds"] is System.Text.Json.Nodes.JsonArray obs)
        {
            var proxies = obs.OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(o => o["protocol"]?.GetValue<string>() is not ("freedom" or "blackhole" or "dns")).ToList();
            if (proxies.Count(o => o["tag"]?.GetValue<string>() is not { } t || !t.StartsWith("YT")) > 3) return ("MIX", Ui.Warn);
            var main = proxies.FirstOrDefault();
            proto = main?["protocol"]?.GetValue<string>() ?? "xray";
            net = main?["streamSettings"]?["network"]?.GetValue<string>() ?? "tcp";
        }
        return proto switch
        {
            "hysteria" or "hysteria2" => ("HY2", Ui.Orange),
            "wireguard" => ("WG", Ui.Direct),
            "vless" when net is "xhttp" or "splithttp" => ("XHTTP", Color.FromRgb(180, 120, 230)),
            "vless" => ("VLESS", Ui.Accent),
            "trojan" => ("TROJAN", Ui.Warn),
            _ => (proto.ToUpperInvariant().Length > 6 ? "XRAY" : proto.ToUpperInvariant(), Ui.Muted),
        };
    }
}

/// <summary>Плитка быстрого действия на главной: значок-точка, подпись, переключатель или стрелка.</summary>
sealed class Tile : Border
{
    readonly TextBlock _title = Ui.T("");
    readonly ToggleSwitch? _toggle;
    public event Action? Activated;

    public Tile(string title, bool toggle)
    {
        _title.Text = title;
        Background = Ui.B(Ui.Panel); BorderBrush = Ui.B(Ui.Grid); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(4);
        Padding = new Thickness(12, 4); Height = 44; Cursor = new Cursor(StandardCursorType.Hand);
        var dock = new DockPanel();
        if (toggle)
        {
            _toggle = new ToggleSwitch { OnContent = "", OffContent = "", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            DockPanel.SetDock(_toggle, Dock.Right);
            dock.Children.Add(_toggle);
        }
        else
        {
            var arrow = Ui.T("›", 18, color: Ui.Muted);
            DockPanel.SetDock(arrow, Dock.Right);
            dock.Children.Add(arrow);
        }
        dock.Children.Add(_title);
        Child = dock;
        PointerEntered += (_, _) => Background = Ui.B(Ui.Header);
        PointerExited += (_, _) => Background = Ui.B(Ui.Panel);
        PointerReleased += (_, _) => Activated?.Invoke();
    }

    public string Title { set => _title.Text = value; }
    public bool Checked { set { if (_toggle != null) _toggle.IsChecked = value; } }
}
