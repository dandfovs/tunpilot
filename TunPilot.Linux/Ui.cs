using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace TunPilot.LinuxApp;

/// <summary>Цвета и общие стили — те же, что у Windows-версии, чтобы программы выглядели одинаково.</summary>
static class Ui
{
    public static Color Bg, Panel, PanelAlt, Header, Grid, SelRow, Text, Muted;
    public static readonly Color Accent = Color.FromRgb(87, 199, 120), Direct = Color.FromRgb(110, 170, 240),
        Warn = Color.FromRgb(222, 186, 70), Orange = Color.FromRgb(230, 140, 60), Danger = Color.FromRgb(235, 90, 90);
    public static bool Light { get; private set; }

    public static void ApplyPalette(bool light)
    {
        Light = light;
        if (!light)
        {
            Bg = Color.FromRgb(24, 25, 28); Panel = Color.FromRgb(32, 34, 38); PanelAlt = Color.FromRgb(37, 39, 44);
            Header = Color.FromRgb(42, 44, 50); Grid = Color.FromRgb(48, 50, 56); SelRow = Color.FromRgb(30, 58, 46);
            Text = Color.FromRgb(222, 224, 228); Muted = Color.FromRgb(140, 145, 155);
        }
        else
        {
            Bg = Color.FromRgb(240, 242, 245); Panel = Colors.White; PanelAlt = Color.FromRgb(247, 248, 250);
            Header = Color.FromRgb(232, 235, 240); Grid = Color.FromRgb(222, 225, 230); SelRow = Color.FromRgb(222, 244, 229);
            Text = Color.FromRgb(28, 30, 34); Muted = Color.FromRgb(110, 115, 125);
        }
    }

    public static IBrush B(Color c) => new SolidColorBrush(c);
    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Стили поверх Fluent: тёмные кнопки, поля и списки в цветах TunPilot.</summary>
    public static Styles AppStyles()
    {
        var s = new Styles();
        s.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(TemplatedControl.FontSizeProperty, 13.5) } });
        // заголовки вкладок Fluent по умолчанию 24px — слишком крупно для настроек
        s.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TemplatedControl.FontSizeProperty, 15.0), new Setter(Layoutable.MinHeightProperty, 36.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0)) } });
        s.Add(new Style(x => x.OfType<Button>().Class("flat"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, B(Header)),
                new Setter(TemplatedControl.BorderBrushProperty, B(Grid)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 6)),
            },
        });
        s.Add(new Style(x => x.OfType<Button>().Class("primary"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, B(Accent)),
                new Setter(TemplatedControl.ForegroundProperty, Brush.Parse("#0E1410")),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
            },
        });
        return s;
    }

    // ── маленькие помощники для построения интерфейса в коде ──

    public static TextBlock T(string text, double size = 13.5, FontWeight? weight = null, Color? color = null, bool wrap = false) => new()
    {
        Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal,
        Foreground = B(color ?? Text), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Hint(string text) => T(text, 12.5, color: Muted, wrap: true);

    public static TextBlock H(string text) => T(text, 15, FontWeight.SemiBold);

    public static Button Btn(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, Classes = { primary ? "primary" : "flat" } };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button BtnAsync(string text, Func<Task> onClick, bool primary = false)
    {
        var b = new Button { Content = text, Classes = { primary ? "primary" : "flat" } };
        bool busy = false;
        b.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try { await onClick(); } finally { busy = false; }
        };
        return b;
    }

    public static Border Card(Control child, Thickness? padding = null) => new()
    {
        Background = B(Panel), BorderBrush = B(Grid), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = padding ?? new Thickness(14), Child = child,
    };

    public static string Fmt(long bytes)
    {
        string[] u = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    public static string Rate(long bytesPerSec)
    {
        double bits = bytesPerSec * 8.0;
        return bits >= 1e9 ? Lf($"{bits / 1e9:0.##} Гбит/с") : bits >= 1e6 ? Lf($"{bits / 1e6:0.#} Мбит/с")
             : bits >= 1e3 ? Lf($"{bits / 1e3:0.#} Кбит/с") : Lf($"{bits:0} бит/с");
    }
}
