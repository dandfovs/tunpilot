using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TunPilot;

/// <summary>Иконки из системного шрифта значков Windows (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
static class Icons
{
    public const char Play = '', Stop = '', Refresh = '', Speed = '', Timer = '',
        Settings = '', Group = '', Routes = '', Chart = '', Folder = '',
        Power = '', Add = '', Edit = '', Delete = '', Copy = '', Paste = '',
        Link = '', Globe = '', Chat = '', Import = '', Shield = '', Bolt = '',
        List = '', Save = '', Open = '', Keyboard = '', Info = '', Window = '',
        Check = '', Filter = '', Download = '', Upload = '', Network = '',
        History = '', Cloud = '', Profile = '', Brush = '', Clear = '',
        Steam = '', Log = '', Test = '', Hide = '', Server = '', Key = '',
        Home = '', Backup = '', Dns = '', Plug = '';

    static readonly string FontName = InstalledFonts();
    public static string FontFamilyName => FontName;
    static readonly Dictionary<(char, int, int), Bitmap> Cache = new();
    static readonly Dictionary<string, Bitmap> BadgeCache = new();

    static string InstalledFonts()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    /// <summary>Иконка-битмап для меню/кнопок. По умолчанию — цвет текста темы.</summary>
    public static Bitmap Get(char glyph, Color? color = null, int size = 16)
    {
        var c = color ?? Theme.Text;
        var key = (glyph, c.ToArgb(), size);
        if (Cache.TryGetValue(key, out var bmp)) return bmp;
        bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(FontName, size * 0.72f, GraphicsUnit.Pixel);
            using var br = new SolidBrush(c);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph.ToString(), font, br, new RectangleF(0, 0, size, size + 1), sf);
        }
        return Cache[key] = bmp;
    }

    static Bitmap? _appBitmap;

    /// <summary>Иконка программы (из exe) — в нужном размере.</summary>
    static Bitmap AppBitmap()
    {
        if (_appBitmap != null) return _appBitmap;
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ico != null) { using var sized = new Icon(ico, 32, 32); return _appBitmap = sized.ToBitmap(); }
        }
        catch { }
        return _appBitmap = new Bitmap(32, 32);
    }

    /// <summary>Иконка программы без точки состояния — для заголовков окон.</summary>
    public static Icon PlainIcon()
    {
        try { if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } ico) return ico; } catch { }
        return Icon.FromHandle(AppBitmap().GetHicon());
    }

    /// <summary>Иконка программы с точкой состояния в углу (серая — отключено, жёлтая — подключение, зелёная — подключено).</summary>
    public static Icon AppIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(AppBitmap(), 0, 0, 32, 32);
            using var ring = new SolidBrush(Color.FromArgb(14, 16, 22));
            g.FillEllipse(ring, 19, 19, 13, 13);
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, 21, 21, 9, 9);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Значок протокола для таблицы серверов: цветная плашка с подписью.</summary>
    public static Bitmap Badge(string text, Color color)
    {
        var key = $"{text}|{color.ToArgb()}";
        if (BadgeCache.TryGetValue(key, out var cached)) return cached;
        var bmp = new Bitmap(46, 18);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var path = Rounded(new Rectangle(0, 1, 45, 16), 4);
            using var fill = new SolidBrush(Color.FromArgb(40, color));
            using var pen = new Pen(Color.FromArgb(150, color));
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var br = new SolidBrush(color);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, br, new RectangleF(0, 1, 46, 17), sf);
        }
        return BadgeCache[key] = bmp;
    }

    /// <summary>Что показать в плашке для сервера: XHTTP / HY2 / VLESS / WG / Xray.</summary>
    public static (string Text, Color Color) ProtocolOf(ServerInfo s)
    {
        string proto = s.Protocol, net = s.P("type", "tcp");
        if (s.XrayConfig?["outbounds"] is System.Text.Json.Nodes.JsonArray obs)
        {
            var proxies = obs.OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(o => o["protocol"]?.GetValue<string>() is not ("freedom" or "blackhole" or "dns")).ToList();
            // Много выходов без вспомогательных (YT_*) — конфиг-балансировщик вроде «АВТО-ВЫБОР».
            if (proxies.Count(o => o["tag"]?.GetValue<string>() is not { } t || !t.StartsWith("YT")) > 3)
                return ("MIX", Theme.Warn);
            var main = proxies.FirstOrDefault();
            proto = main?["protocol"]?.GetValue<string>() ?? "xray";
            net = main?["streamSettings"]?["network"]?.GetValue<string>() ?? "tcp";
        }
        return proto switch
        {
            "hysteria" or "hysteria2" => ("HY2", Theme.Orange),
            "wireguard" => ("WG", Theme.Direct),
            "vless" when net is "xhttp" or "splithttp" => ("XHTTP", Color.FromArgb(180, 120, 230)),
            "vless" => ("VLESS", Theme.Accent),
            "trojan" => ("TROJAN", Theme.Warn),
            _ => (proto.ToUpperInvariant().Length > 6 ? "XRAY" : proto.ToUpperInvariant(), Theme.Muted),
        };
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
