using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TunPilot;

/// <summary>Свои контролы главного окна (с анимацией наведения и переключения).</summary>
public sealed partial class MainForm
{
    enum ConnectState { Off, Busy, On }

    /// <summary>
    /// Большая круглая кнопка подключения: плавная смена цвета, вращающаяся дуга при подключении,
    /// «волна» в момент подключения и подсветка при наведении.
    /// </summary>
    sealed class ConnectButton : Control
    {
        ConnectState _state;
        public string Detail = "";
        readonly Theme.Anim _hover, _color, _ripple;
        readonly System.Windows.Forms.Timer _spin = new() { Interval = 16 };
        Color _from = Theme.Muted, _to = Theme.Muted;
        float _angle;
        const int CircleSize = 196, TitleY = 174;   // круг с запасом под свечение; подписи сразу под кольцом
        readonly Font _titleFont = new("Segoe UI", 14f, FontStyle.Bold), _detailFont = new("Segoe UI", 9.5f);

        public ConnectButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(320, TitleY + 56);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            _hover = new Theme.Anim(this, 140);
            _color = new Theme.Anim(this, 320);
            _ripple = new Theme.Anim(this, 650);
            _spin.Tick += (_, _) => { _angle = (_angle + 7) % 360; Invalidate(); };
            Disposed += (_, _) => _spin.Dispose();
        }

        static Color ColorOf(ConnectState s) => s switch { ConnectState.On => Theme.Accent, ConnectState.Busy => Theme.Warn, _ => Theme.Muted };

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ConnectState State
        {
            get => _state;
            set
            {
                if (value == _state) return;
                _from = Theme.Blend(_from, _to, _color.Eased);   // продолжаем с текущего цвета, если переход ещё идёт
                _to = ColorOf(value);
                _color.Snap(0); _color.Target = 1;
                if (value == ConnectState.On && _state == ConnectState.Busy) { _ripple.Snap(0); _ripple.Target = 1; }
                _state = value;
                if (value == ConnectState.Busy) _spin.Start(); else _spin.Stop();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Target = 0; base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float h = _hover.Eased;
            var stateColor = Theme.Blend(_from, _to, _color.Eased);
            // когда отключено, наведение «примеряет» зелёный — подсказка, что будет по клику
            var accent = _state == ConnectState.Off ? Theme.Blend(stateColor, Theme.Accent, 0.75f * h) : stateColor;
            float cx = Width / 2f, cy = CircleSize / 2f, R = 62 + 3 * h;

            // мягкое свечение вокруг круга
            float glow = _state switch { ConnectState.On => 1f, ConnectState.Busy => 0.6f, _ => 0f } * (0.75f + 0.25f * h) + (_state == ConnectState.Off ? 0.55f * h : 0);
            if (glow > 0.01f)
                for (int i = 7; i >= 1; i--)
                {
                    float rr = R + i * 4.5f;
                    using var gb = new SolidBrush(Color.FromArgb((int)(13 * glow), accent));
                    g.FillEllipse(gb, cx - rr, cy - rr, rr * 2, rr * 2);
                }

            // волна в момент подключения
            if (_ripple.Value is > 0 and < 1)
            {
                float k = _ripple.Eased, rr = R + 30 * k;
                using var wave = new Pen(Color.FromArgb((int)(170 * (1 - k)), accent), 2.5f);
                g.DrawEllipse(wave, cx - rr, cy - rr, rr * 2, rr * 2);
            }

            // круг: заливка, кольцо, внутренний диск
            using (var fill = new SolidBrush(Theme.Blend(Theme.Panel, accent, 0.10f + 0.08f * h))) g.FillEllipse(fill, cx - R, cy - R, R * 2, R * 2);
            var ringRect = new RectangleF(cx - R, cy - R, R * 2, R * 2);
            if (_state == ConnectState.Busy)
            {
                using var track = new Pen(Color.FromArgb(55, accent), 4);
                g.DrawEllipse(track, ringRect);
                using var arc = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, ringRect, _angle, 110);
            }
            else
            {
                using var ring = new Pen(_state == ConnectState.Off ? Theme.Blend(Theme.Grid, accent, 0.35f + 0.65f * h) : accent, 4);
                g.DrawEllipse(ring, ringRect);
            }
            float r2 = R - 13;
            using (var inner = new SolidBrush(Theme.Blend(Theme.Bg, accent, 0.05f + 0.07f * h))) g.FillEllipse(inner, cx - r2, cy - r2, r2 * 2, r2 * 2);

            // значок питания — линиями (чётче, чем символ шрифта)
            var iconColor = _state == ConnectState.Off ? Theme.Blend(Theme.Muted, accent, 0.4f + 0.6f * h) : accent;
            using (var p = new Pen(iconColor, 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                const float ir = 20;
                g.DrawArc(p, cx - ir, cy - ir + 3, ir * 2, ir * 2, -58, 296);
                g.DrawLine(p, cx, cy - 25, cx, cy - 3);
            }

            // подписи под кругом
            var title = _state switch { ConnectState.On => L("Подключено"), ConnectState.Busy => L("Подключение…"), _ => L("Отключено") };
            TextRenderer.DrawText(g, title, _titleFont, new Rectangle(0, TitleY, Width, 30),
                _state == ConnectState.Off ? Theme.Text : stateColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Detail, _detailFont, new Rectangle(0, TitleY + 30, Width, 22), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    /// <summary>Карточка текущего сервера: вся — кнопка; стрелка справа поворачивается, пока открыт список.</summary>
    sealed class ServerCard : Control
    {
        public Bitmap? Badge;
        public string Title = "", Sub = "";
        readonly Theme.Anim _hover, _open;
        readonly Font _bold = new("Segoe UI", 10.5f, FontStyle.Bold);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Open { get => _open.Target > 0.5f; set => _open.Target = value ? 1 : 0; }

        public ServerCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5f);
            _hover = new Theme.Anim(this, 140);
            _open = new Theme.Anim(this, 180);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Target = 0; base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Math.Max(_hover.Eased, _open.Eased);
            g.Clear(Theme.Blend(Theme.Panel, Theme.Header, h));
            using (var border = new Pen(Theme.Blend(Theme.Panel, Theme.Accent, h * 0.8f))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            if (Badge != null) g.DrawImage(Badge, 12, (Height - Badge.Height) / 2);
            int x = 68, w = Width - x - 48;
            TextRenderer.DrawText(g, Title, _bold, new Rectangle(x, 9, w, 24), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Sub, Font, new Rectangle(x, 33, w, 20), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // стрелка: вниз → вверх, пока список открыт
            float t = _open.Eased, cx = Width - 26, cy = Height / 2f, s = 5;
            float dy = s * 0.6f * (1 - 2 * t);
            using var pen = new Pen(Theme.Blend(Theme.Muted, Theme.Text, h), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(cx - s, cy - dy), new PointF(cx, cy + dy), new PointF(cx + s, cy - dy)]);
        }
    }

    enum TileKind { Toggle, Menu, Link }

    /// <summary>Плитка быстрого действия: значок, подпись и переключатель (с плавным ходом) / стрелка.</summary>
    sealed class Tile : Control
    {
        char _icon; TileKind _kind;
        bool _checked;
        public string Title = "";
        public event Action? Activated;
        readonly Theme.Anim _hover, _knob;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (value == _checked) return;
                _checked = value;
                if (IsHandleCreated && Visible) _knob.Target = value ? 1 : 0; else _knob.Snap(value ? 1 : 0);
            }
        }

        public Tile()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5f);
            _hover = new Theme.Anim(this, 140);
            _knob = new Theme.Anim(this, 170);
        }

        public void Setup(char icon, string title, TileKind kind) { _icon = icon; Title = title; _kind = kind; }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Activated?.Invoke(); }
        protected override void OnMouseEnter(EventArgs e) { _hover.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Target = 0; base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = _hover.Eased;
            g.Clear(Theme.Blend(Theme.Panel, Theme.Header, h));
            using (var pen = new Pen(Theme.Blend(Theme.Grid, Theme.Muted, h * 0.6f))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            var on = _kind == TileKind.Toggle && _knob.Value > 0.5f;
            g.DrawImage(Icons.Get(_icon, on ? Theme.Accent : Theme.Text, 18), 12, (Height - 18) / 2);
            TextRenderer.DrawText(g, Title, Font, new Rectangle(38, 0, Width - 38 - 50, Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (_kind == TileKind.Toggle)
            {
                float k = _knob.Eased;
                var sw = new Rectangle(Width - 46, (Height - 18) / 2, 34, 18);
                using var track = new SolidBrush(Theme.Blend(Theme.Grid, Theme.Accent, k));
                using var path = Pill(sw);
                g.FillPath(track, path);
                float knob = sw.Left + 2 + (sw.Width - 18) * k;
                using var kb = new SolidBrush(Theme.Blend(Theme.Muted, Color.White, k));
                g.FillEllipse(kb, knob, sw.Top + 2, 14, 14);
            }
            else
            {
                using var pen = new Pen(Theme.Blend(Theme.Muted, Theme.Text, h), 1.6f);
                float cx = Width - 22 + (_kind == TileKind.Link ? 2 * h : 0), cy = Height / 2f;   // стрелка «→» чуть сдвигается при наведении
                if (_kind == TileKind.Menu) g.DrawLines(pen, [new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2)]);
                else g.DrawLines(pen, [new PointF(cx - 2, cy - 4), new PointF(cx + 2, cy), new PointF(cx - 2, cy + 4)]);
            }
        }

        static GraphicsPath Pill(Rectangle r)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
            p.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Вкладка «Сессия»: карточки «подпись — значение», длинные значения переносятся в своей колонке.</summary>
    sealed class SessionView : Control
    {
        public sealed record Item(string Key, string Value, Color? Color = null);
        public sealed record Card(string Title, List<Item> Items);
        List<Card> _cards = [];
        readonly Font _title = new("Segoe UI", 10f, FontStyle.Bold), _text = new("Segoe UI", 9.5f);
        const int Pad = 14, Gap = 12;

        public SessionView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetData(List<Card> cards) { _cards = cards; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            if (_cards.Count == 0) return;
            int cols = Width >= 860 ? 2 : 1;
            int cardW = (Width - Pad * 2 - Gap * (cols - 1)) / cols;
            var colY = new int[cols];
            for (int c = 0; c < cols; c++) colY[c] = Pad;
            const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;

            foreach (var card in _cards)
            {
                // в колонку, где сейчас меньше всего занято
                int col = 0;
                for (int c = 1; c < cols; c++) if (colY[c] < colY[col]) col = c;
                int x = Pad + col * (cardW + Gap), y = colY[col];
                // колонка подписей — по самой длинной подписи в карточке (но не шире 45% карточки)
                int keyW = 0;
                foreach (var it in card.Items) keyW = Math.Max(keyW, TextRenderer.MeasureText(g, it.Key, _text).Width);
                keyW = Math.Clamp(keyW + 18, 110, (int)(cardW * 0.45));
                int valW = Math.Max(80, cardW - Pad * 2 - keyW);

                // высота карточки по содержимому
                int h = Pad + _title.Height + 10;
                var rowH = new List<int>();
                foreach (var it in card.Items)
                {
                    var sz = TextRenderer.MeasureText(g, it.Value.Length > 0 ? it.Value : "—", _text, new Size(valW, 0), wrap);
                    var kz = TextRenderer.MeasureText(g, it.Key, _text, new Size(keyW - 10, 0), wrap);
                    int rh = Math.Max(_text.Height, Math.Max(sz.Height, kz.Height)) + 6;
                    rowH.Add(rh);
                    h += rh;
                }
                h += Pad - 6;

                using (var bg = new SolidBrush(Theme.Panel)) g.FillRectangle(bg, x, y, cardW, h);
                using (var pen = new Pen(Theme.Grid)) g.DrawRectangle(pen, x, y, cardW - 1, h - 1);
                TextRenderer.DrawText(g, card.Title, _title, new Point(x + Pad, y + Pad - 2), Theme.Text, TextFormatFlags.NoPrefix);

                int ry = y + Pad + _title.Height + 10;
                for (int i = 0; i < card.Items.Count; i++)
                {
                    var it = card.Items[i];
                    TextRenderer.DrawText(g, it.Key, _text, new Rectangle(x + Pad, ry, keyW - 10, rowH[i]), Theme.Muted, wrap);
                    TextRenderer.DrawText(g, it.Value.Length > 0 ? it.Value : "—", _text, new Rectangle(x + Pad + keyW, ry, valW, rowH[i]),
                        it.Color ?? Theme.Text, wrap);
                    ry += rowH[i];
                }
                colY[col] = y + h + Gap;
            }
        }
    }

    /// <summary>Тонкая полоска расхода трафика подписки.</summary>
    sealed class ProgressLine : Control
    {
        public double Fraction;
        public bool Unlimited;
        public ProgressLine() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Grid);
            var c = Unlimited ? Theme.Accent : Fraction > 0.9 ? Theme.Danger : Fraction > 0.7 ? Theme.Warn : Theme.Accent;
            using var b = new SolidBrush(c);
            e.Graphics.FillRectangle(b, 0, 0, (int)(Width * Fraction), Height);
        }
    }

    /// <summary>Пункт бокового меню: значок над подписью; у выбранного зелёная полоска плавно «выезжает».</summary>
    sealed class NavButton : Control
    {
        readonly char _icon;
        readonly Theme.Anim _hover, _sel;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _sel.Target > 0.5f; set => _sel.Target = value ? 1 : 0; }

        public NavButton(string text, char icon)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = text; _icon = icon;
            Height = 64; Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 8.5f);
            _hover = new Theme.Anim(this, 140);
            _sel = new Theme.Anim(this, 200);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover.Target = 1; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Target = 0; base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = _sel.Eased, h = _hover.Eased;
            g.Clear(Theme.Blend(Theme.Blend(Theme.Panel, Theme.PanelAlt, h), Theme.Header, s));
            var color = Theme.Blend(Theme.Blend(Theme.Muted, Theme.Text, h), Theme.Accent, s);
            if (s > 0.01f)
            {
                int bar = (int)((Height - 16) * s);
                using var b = new SolidBrush(Theme.Accent);
                g.FillRectangle(b, 0, (Height - bar) / 2, 3, bar);
            }
            // значок — дискретным цветом (кэш битмапов), подпись — плавным
            var iconColor = s > 0.5f ? Theme.Accent : h > 0.5f ? Theme.Text : Theme.Muted;
            g.DrawImage(Icons.Get(_icon, iconColor, 22), (Width - 22) / 2, 10);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 36, Width, 20), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}
