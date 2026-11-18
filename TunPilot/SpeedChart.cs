using System.Drawing.Drawing2D;

namespace TunPilot;

/// <summary>График скорости в реальном времени: прокси/напрямую, загрузка/отдача, последние N секунд.</summary>
public sealed class SpeedChart : Control
{
    const int Points = 120;   // 2 минуты при опросе раз в секунду

    sealed record Series(string Name, Color Color, bool Dashed)
    {
        public readonly Queue<long> Values = new(Enumerable.Repeat(0L, Points));
    }

    readonly Series[] _series =
    [
        new(L("Прокси ↓"), Theme.Accent, false),
        new(L("Прокси ↑"), Theme.Accent, true),
        new(L("Напрямую ↓"), Theme.Direct, false),
        new(L("Напрямую ↑"), Theme.Direct, true),
    ];

    public SpeedChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
    }

    /// <summary>Добавить точку (байт/с).</summary>
    public void Push(long proxyDown, long proxyUp, long directDown, long directUp)
    {
        long[] v = [proxyDown, proxyUp, directDown, directUp];
        for (int i = 0; i < _series.Length; i++)
        {
            _series[i].Values.Enqueue(v[i]);
            while (_series[i].Values.Count > Points) _series[i].Values.Dequeue();
        }
        Invalidate();
    }

    public void Clear()
    {
        foreach (var s in _series) { s.Values.Clear(); for (int i = 0; i < Points; i++) s.Values.Enqueue(0); }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        const int left = 100, right = 16, top = 34, bottom = 26;
        var plot = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));

        // масштаб: «круглое» значение сверху
        long max = Math.Max(_series.Max(s => s.Values.Max()), 16 * 1024);
        double niceMax = Nice(max * 1.15);

        using var grid = new Pen(Theme.Grid);
        using var muted = new SolidBrush(Theme.Muted);
        var fmtRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        for (int i = 0; i <= 4; i++)
        {
            int y = plot.Bottom - plot.Height * i / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(Rate(niceMax * i / 4), Font, muted, new RectangleF(0, y - 10, left - 8, 20), fmtRight);
        }
        // подписи времени
        var fmtCenter = new StringFormat { Alignment = StringAlignment.Center };
        foreach (var sec in new[] { 120, 90, 60, 30, 0 })
        {
            int x = plot.Right - plot.Width * sec / Points;
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            // крайние подписи прижимаем внутрь, чтобы не обрезались
            var f = sec == 0 ? new StringFormat { Alignment = StringAlignment.Far }
                  : sec == Points ? new StringFormat { Alignment = StringAlignment.Near } : fmtCenter;
            g.DrawString(sec == 0 ? L("сейчас") : Lf($"−{sec} с"), Font, muted, x, plot.Bottom + 4, f);
        }

        // линии: сначала залитая площадь загрузки, потом линии
        foreach (var s in _series)
        {
            var pts = s.Values.Select((v, i) => new PointF(
                plot.Left + plot.Width * i / (float)(Points - 1),
                plot.Bottom - (float)(plot.Height * Math.Min(1, v / niceMax)))).ToArray();
            if (!s.Dashed)
            {
                using var path = new GraphicsPath();
                path.AddLines(pts);
                path.AddLine(pts[^1], new PointF(plot.Right, plot.Bottom));
                path.AddLine(new PointF(plot.Right, plot.Bottom), new PointF(plot.Left, plot.Bottom));
                using var fill = new SolidBrush(Color.FromArgb(28, s.Color));
                g.FillPath(fill, path);
            }
            using var pen = new Pen(s.Color, s.Dashed ? 1.5f : 2f) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawLines(pen, pts);
        }

        // легенда с текущими значениями
        float lx = plot.Left;
        foreach (var s in _series)
        {
            using var pen = new Pen(s.Color, 2f) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawLine(pen, lx, 17, lx + 18, 17);
            var text = $"{s.Name}  {Rate(s.Values.Last())}";
            using var br = new SolidBrush(ForeColor);
            g.DrawString(text, Font, br, lx + 24, 8);
            lx += 24 + g.MeasureString(text, Font).Width + 22;
        }
    }

    static double Nice(double v)
    {
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * exp;
    }

    /// <summary>Байт/с → «12.3 Мбит/с» (в битах, как привыкли считать скорость сети).</summary>
    public static string Rate(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits switch
        {
            < 1000 => Lf($"{bits:0} бит/с"),
            < 1_000_000 => Lf($"{bits / 1000:0.#} Кбит/с"),
            < 1_000_000_000 => Lf($"{bits / 1_000_000:0.#} Мбит/с"),
            _ => Lf($"{bits / 1_000_000_000:0.##} Гбит/с"),
        };
    }
}
