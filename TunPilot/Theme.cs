using Microsoft.Win32;

namespace TunPilot;

/// <summary>Цветовая тема (тёмная/светлая/как в системе) и стилизация стандартных контролов.</summary>
static class Theme
{
    public static Color Bg, Panel, PanelAlt, Header, Grid, Select, SelRow, Text, Muted;
    public static Color Accent = Color.FromArgb(87, 199, 120), Direct = Color.FromArgb(110, 170, 240),
        Warn = Color.FromArgb(222, 186, 70), Orange = Color.FromArgb(230, 140, 60), Danger = Color.FromArgb(235, 90, 90);
    public static bool IsDark { get; private set; } = true;

    static Theme() => Apply("dark");

    public static void Apply(string mode)
    {
        IsDark = mode switch { "light" => false, "system" => SystemIsDark(), _ => true };
        if (IsDark)
        {
            Bg = Color.FromArgb(24, 25, 28); Panel = Color.FromArgb(32, 34, 38); PanelAlt = Color.FromArgb(37, 39, 44);
            Header = Color.FromArgb(42, 44, 50); Grid = Color.FromArgb(48, 50, 56); Select = Color.FromArgb(0, 102, 204);
            SelRow = Color.FromArgb(30, 58, 46); Text = Color.FromArgb(222, 224, 228); Muted = Color.FromArgb(140, 145, 155);
            Accent = Color.FromArgb(87, 199, 120); Direct = Color.FromArgb(110, 170, 240);
        }
        else
        {
            Bg = Color.FromArgb(240, 242, 245); Panel = Color.White; PanelAlt = Color.FromArgb(247, 248, 250);
            Header = Color.FromArgb(232, 235, 240); Grid = Color.FromArgb(222, 225, 230); Select = Color.FromArgb(0, 102, 204);
            SelRow = Color.FromArgb(222, 244, 229); Text = Color.FromArgb(28, 30, 34); Muted = Color.FromArgb(110, 115, 125);
            Accent = Color.FromArgb(30, 150, 70); Direct = Color.FromArgb(30, 110, 200);
        }
    }

    static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    /// <summary>Плавный переход между двумя цветами (t от 0 до 1).</summary>
    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Анимированное значение 0…1 для контрола: плавно идёт к цели (наведение, переключатель),
    /// на каждом шаге перерисовывает контрол. Таймер работает только пока значение меняется.
    /// </summary>
    public sealed class Anim
    {
        readonly Control _c;
        readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };
        readonly float _speed;
        float _target;
        public float Value { get; private set; }

        public Anim(Control c, float durationMs = 160)
        {
            _c = c; _speed = 15f / durationMs;
            _t.Tick += (_, _) =>
            {
                var d = _target - Value;
                if (Math.Abs(d) <= _speed) { Value = _target; _t.Stop(); }
                else Value += Math.Sign(d) * _speed;
                if (!_c.IsDisposed) _c.Invalidate();
            };
            c.Disposed += (_, _) => _t.Dispose();
        }

        /// <summary>Сглаженное значение (ease-out) — для отрисовки.</summary>
        public float Eased => 1 - (1 - Value) * (1 - Value);

        public float Target
        {
            get => _target;
            set { _target = value; if (Math.Abs(Value - value) > 0.001f) _t.Start(); }
        }

        public void Snap(float v) { _target = Value = v; _t.Stop(); }
    }

    // ── стилизация ──

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    static void OnHandle(Control c, Action<IntPtr> apply)
    {
        if (c.IsHandleCreated) apply(c.Handle);
        c.HandleCreated += (_, _) => apply(c.Handle);
    }

    /// <summary>Тёмный заголовок окна (Windows 10 20H1+ / 11).</summary>
    public static void DarkTitle(Form f) => OnHandle(f, h =>
    {
        int on = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
    });

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolTip, object> StyledTips = new();
    static readonly Font TipFont = new("Segoe UI", 9f);
    static readonly Padding TipPad = new(10, 7, 10, 8);

    /// <summary>Подсказка в цветах темы (стандартная у Windows всегда белая).</summary>
    public static ToolTip Tip(ToolTip? t = null)
    {
        t ??= new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
        if (StyledTips.TryGetValue(t, out _)) return t;
        StyledTips.Add(t, true);
        t.OwnerDraw = true;
        t.Popup += (_, e) =>
        {
            var text = t.GetToolTip(e.AssociatedControl!) is { Length: > 0 } s ? s : LastToolStripText(e.AssociatedControl);
            var size = TextRenderer.MeasureText(text, TipFont, new Size(560, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(size.Width + TipPad.Horizontal, size.Height + TipPad.Vertical);
        };
        t.Draw += (_, e) =>
        {
            using var bg = new SolidBrush(Header);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var pen = new Pen(Grid);
            e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var r = new Rectangle(TipPad.Left, TipPad.Top, e.Bounds.Width - TipPad.Horizontal, e.Bounds.Height - TipPad.Vertical);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, r, Text, TextFormatFlags.WordBreak | TextFormatFlags.Left);
        };
        return t;
    }

    // У ToolStrip подсказка показывается через его внутренний ToolTip; текст берём у пункта под мышью.
    static string LastToolStripText(Control? c) =>
        c is ToolStrip ts && ts.GetItemAt(ts.PointToClient(Cursor.Position)) is { } item ? item.ToolTipText ?? item.Text ?? "" : "";

    /// <summary>Тёмные подсказки у пунктов панели/меню.</summary>
    public static void TipsFor(ToolStrip ts)
    {
        if (typeof(ToolStrip).GetProperty("ToolTip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(ts) is ToolTip tip)
            Tip(tip);
    }

    /// <summary>Тёмный выпадающий список: сами рисуем пункты (иначе Windows рисует белый список).</summary>
    public static void StyleCombo(ComboBox cb)
    {
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = Panel; cb.ForeColor = Text;
        if (cb.DrawMode == DrawMode.Normal)
        {
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.ItemHeight = Math.Max(cb.ItemHeight, cb.Font.Height + 4);
            cb.DrawItem += (_, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using var bg = new SolidBrush(sel ? Select : Panel);
                e.Graphics.FillRectangle(bg, e.Bounds);
                TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font,
                    new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height),
                    sel ? Color.White : Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
        }
        if (cb.DropDownStyle == ComboBoxStyle.DropDown)   // редактируемый: без «синего» выделения текста при показе
            cb.VisibleChanged += (_, _) => { if (cb.Visible && cb.IsHandleCreated) cb.BeginInvoke(() => cb.SelectionLength = 0); };
        _ = new ComboPainter(cb);   // NativeWindow живёт, пока жив хэндл комбобокса
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> BusyButtons = new();

    /// <summary>
    /// «Занятая» кнопка вместо Enabled=false: Windows рисует текст выключенной кнопки тёмным, игнорируя тему.
    /// Кнопка выглядит как обычно, повторные нажатия обработчик пропускает. Возвращает false, если кнопка уже занята.
    /// </summary>
    public static bool SetBusy(Button b, bool busy)
    {
        if (b.IsDisposed) return false;
        if (busy)
        {
            if (BusyButtons.TryGetValue(b, out _)) return false;
            // запоминаем цвета подсветки и гасим её: занятая кнопка не реагирует на наведение и нажатие
            BusyButtons.Add(b, (b.FlatAppearance.MouseOverBackColor, b.FlatAppearance.MouseDownBackColor));
            b.FlatAppearance.MouseOverBackColor = b.BackColor;
            b.FlatAppearance.MouseDownBackColor = b.BackColor;
            b.Cursor = Cursors.WaitCursor;   // текст не трогаем: что идёт работа, видно по статусу рядом
            b.Invalidate();                  // мышь уже над кнопкой (её только что нажали) — сразу перерисовать без подсветки
        }
        else
        {
            if (BusyButtons.TryGetValue(b, out var saved) && saved is (Color over, Color down))
            {
                b.FlatAppearance.MouseOverBackColor = over;
                b.FlatAppearance.MouseDownBackColor = down;
            }
            BusyButtons.Remove(b);
            b.Cursor = Cursors.Hand;
        }
        return true;
    }

    /// <summary>Тёмные кнопки ▲▼ у числового поля: дорисовываем поверх стандартных (у них нет своих цветов).</summary>
    public static void StyleNumeric(NumericUpDown n)
    {
        n.BackColor = Panel; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle;
        if (n.Controls.Count == 0) return;
        var btn = n.Controls[0];
        btn.Paint += (_, e) =>
        {
            var r = btn.ClientRectangle;
            using var bg = new SolidBrush(Header);
            e.Graphics.FillRectangle(bg, r);
            using var line = new Pen(Grid);
            e.Graphics.DrawLine(line, 0, 0, 0, r.Height);
            e.Graphics.DrawLine(line, 0, r.Height / 2, r.Width, r.Height / 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var arrow = new SolidBrush(n.Enabled ? Text : Muted);
            int cx = r.Width / 2 + 1, q = r.Height / 4;
            e.Graphics.FillPolygon(arrow, [new Point(cx - 4, q + 2), new Point(cx + 4, q + 2), new Point(cx, q - 2)]);
            e.Graphics.FillPolygon(arrow, [new Point(cx - 4, 3 * q - 1), new Point(cx + 4, 3 * q - 1), new Point(cx, 3 * q + 3)]);
        };
    }

    /// <summary>Перерисовка кнопки-стрелки у редактируемого выпадающего списка (Windows рисует её белой).</summary>
    sealed class ComboPainter : NativeWindow
    {
        readonly ComboBox _cb;
        public ComboPainter(ComboBox cb)
        {
            _cb = cb;
            if (cb.IsHandleCreated) AssignHandle(cb.Handle);
            cb.HandleCreated += (_, _) => AssignHandle(cb.Handle);
            cb.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F /*WM_PAINT*/ ) return;
            using var g = Graphics.FromHwnd(_cb.Handle);
            int w = SystemInformation.VerticalScrollBarWidth + 2;
            var r = new Rectangle(_cb.ClientSize.Width - w, 0, w, _cb.ClientSize.Height);
            using var bg = new SolidBrush(Panel);
            g.FillRectangle(bg, r);
            using var border = new Pen(Grid);
            g.DrawRectangle(border, 0, 0, _cb.ClientSize.Width - 1, _cb.ClientSize.Height - 1);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Muted, 1.6f);
            int cx = r.X + r.Width / 2, cy = r.Height / 2;
            g.DrawLines(pen, [new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2)]);
        }
    }

    /// <summary>Тёмные системные полосы прокрутки у контрола (многострочные поля, списки, таблицы).</summary>
    public static void DarkScroll(Control c)
    {
        if (!IsDark) return;
        OnHandle(c, h => SetWindowTheme(h, "DarkMode_Explorer", null));
        if (c is DataGridView g)   // у таблицы полосы прокрутки — отдельные дочерние контролы
            foreach (Control sb in g.Controls) if (sb is ScrollBar) OnHandle(sb, h => SetWindowTheme(h, "DarkMode_Explorer", null));
    }

    public static void StyleDropDown(ToolStripDropDown dd)
    {
        dd.Renderer = new Renderer();
        TipsFor(dd);
        dd.BackColor = Panel;
        dd.ForeColor = Text;
        foreach (ToolStripItem i in dd.Items)
        {
            i.ForeColor = Text;
            if (i is ToolStripMenuItem mi && mi.HasDropDownItems) StyleDropDown(mi.DropDown);
        }
    }

    public static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Header;
        b.ForeColor = Text;
        b.FlatAppearance.BorderColor = Grid;
        b.FlatAppearance.MouseOverBackColor = IsDark ? Color.FromArgb(52, 56, 64) : Color.FromArgb(220, 225, 232);
        b.Height = Math.Max(b.Height, 28);
        b.Cursor = Cursors.Hand;
    }

    /// <summary>Рекурсивно красит дерево контролов (для окон настроек/групп/статистики).</summary>
    public static void StyleTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b: StyleButton(b); break;
                case NumericUpDown nud: StyleNumeric(nud); break;
                case TextBox or ComboBox:
                    c.BackColor = Panel; c.ForeColor = Text;
                    if (c is TextBox tb) { tb.BorderStyle = BorderStyle.FixedSingle; if (tb.Multiline) DarkScroll(tb); }
                    if (c is ComboBox cb) StyleCombo(cb);
                    break;
                case CheckBox or RadioButton or Label:
                    c.ForeColor = c.ForeColor == SystemColors.GrayText ? Muted : Text;
                    if (c is CheckBox chk) chk.FlatStyle = FlatStyle.Flat;
                    if (c is RadioButton rb) { rb.FlatStyle = FlatStyle.Flat; rb.FlatAppearance.CheckedBackColor = Panel; }
                    break;
                case GroupBox gb: gb.ForeColor = Muted; break;
                case DataGridView or ListBox: DarkScroll(c); break;
                case ScrollableControl { AutoScroll: true }: DarkScroll(c); break;
            }
            if (c is not (DataGridView or ToolStrip)) StyleTree(c);
        }
    }

    public sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Явно заданный цвет (зелёная/красная кнопка) не перекрашиваем.
            if (e.Item.ForeColor == SystemColors.ControlText || e.Item.ForeColor == Control.DefaultForeColor)
                e.TextColor = Text;
            else e.TextColor = e.Item.ForeColor;
            if (!e.Item.Enabled) e.TextColor = Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is ToolStripButton { Checked: true } b && b.Owner is not StatusStrip)
            {
                using var br = new SolidBrush(Header);
                e.Graphics.FillRectangle(br, new Rectangle(Point.Empty, e.Item.Size));
                using var pen = new Pen(Accent, 2);
                e.Graphics.DrawLine(pen, 2, e.Item.Height - 1, e.Item.Width - 2, e.Item.Height - 1);
                return;
            }
            base.OnRenderButtonBackground(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Text; base.OnRenderArrow(e); }

        /// <summary>Кнопка «ещё» (пункты не влезли): своя отрисовка вместо сине-белой подсветки Windows.</summary>
        protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(Point.Empty, e.Item.Size);
            var bg = e.Item.Pressed ? Header : e.Item.Selected ? (IsDark ? Color.FromArgb(48, 52, 60) : Color.FromArgb(225, 230, 238))
                : e.ToolStrip?.BackColor ?? Panel;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item.Selected ? Text : Muted, 1.6f);
            int cx = r.Width / 2, cy = r.Height / 2;
            g.DrawLines(pen, [new Point(cx - 4, cy - 1), new Point(cx, cy + 3), new Point(cx + 4, cy - 1)]);
            g.DrawLine(pen, cx - 4, cy - 5, cx + 4, cy - 5);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Accent, 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new Point(r.Left + 3, r.Top + r.Height / 2), new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4), new Point(r.Right - 3, r.Top + 3)]);
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        static Color Hover => IsDark ? Color.FromArgb(48, 52, 60) : Color.FromArgb(225, 230, 238);
        public override Color ToolStripGradientBegin => Panel;
        public override Color ToolStripGradientMiddle => Panel;
        public override Color ToolStripGradientEnd => Panel;
        public override Color ToolStripBorder => Panel;
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Grid;
        public override Color MenuItemBorder => Select;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Header;
        public override Color MenuItemPressedGradientEnd => Header;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonSelectedBorder => Grid;
        public override Color ButtonPressedGradientBegin => Header;
        public override Color ButtonPressedGradientEnd => Header;
        public override Color ButtonCheckedGradientBegin => Header;
        public override Color ButtonCheckedGradientEnd => Header;
        public override Color SeparatorDark => Grid;
        public override Color SeparatorLight => Panel;
        public override Color StatusStripGradientBegin => Panel;
        public override Color StatusStripGradientEnd => Panel;
        // кнопка «»» у панели, когда пункты не влезают (по умолчанию — светлый градиент)
        public override Color OverflowButtonGradientBegin => Panel;
        public override Color OverflowButtonGradientMiddle => Panel;
        public override Color OverflowButtonGradientEnd => Panel;
        public override Color GripDark => Grid;
        public override Color GripLight => Panel;
        public override Color ToolStripContentPanelGradientBegin => Bg;
        public override Color ToolStripContentPanelGradientEnd => Bg;
        public override Color CheckBackground => Panel;
        public override Color CheckSelectedBackground => Panel;
        public override Color CheckPressedBackground => Panel;
    }
}
