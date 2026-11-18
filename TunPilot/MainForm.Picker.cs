using System.Drawing.Drawing2D;

namespace TunPilot;

public sealed partial class MainForm
{
    /// <summary>
    /// Всплывающий список серверов для карточки на «Главной»: группы, поиск, плашки протоколов, пинг цветом.
    /// Закрывается кликом мимо, Esc или выбором сервера.
    /// </summary>
    sealed class ServerPicker : Form
    {
        readonly MainForm _owner;
        readonly TrayApp _app;
        readonly FlowLayoutPanel _groups = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 8, 8, 0) };
        readonly TextBox _search = new() { BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f) };
        readonly SmoothList _list = new() { Dock = DockStyle.Fill, RowHeight = 38 };
        readonly System.Windows.Forms.Timer _refresh = new() { Interval = 400 };
        record Item(string Key, string Tag, string Name, Bitmap Badge, string Sub);
        List<Item> _items = [];
        Group? _group;

        // шрифты — один раз, а не на каждую строку при отрисовке
        readonly Font _regular = new("Segoe UI", 9.5f), _boldFont = new("Segoe UI", 9.5f, FontStyle.Bold), _small = new("Segoe UI", 8.5f);

        public ServerPicker(MainForm owner, Control anchor)
        {
            _owner = owner; _app = owner._app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; KeyPreview = true;
            BackColor = Theme.Grid;               // 1px рамка — через Padding
            Padding = new Padding(1);
            Font = _regular;
            _group = _app.ActiveGroup;

            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            // поиск
            var searchBox = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 10, 12, 6), BackColor = Theme.Panel };
            var searchFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Header, Padding = new Padding(30, 4, 8, 2) };
            var glass = new PictureBox { Image = Icons.Get(Icons.Filter, Theme.Muted, 16), Size = new Size(16, 16), Location = new Point(8, 5), BackColor = Theme.Header };
            _search.Dock = DockStyle.Fill; _search.BackColor = Theme.Header; _search.ForeColor = Theme.Text;
            _search.PlaceholderText = L("Поиск сервера");
            _search.TextChanged += (_, _) => { Fill(); _list.ScrollToTop(); };
            searchFrame.Controls.Add(_search); searchFrame.Controls.Add(glass);
            searchBox.Controls.Add(searchFrame);

            // низ
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 7, 10, 7), BackColor = Theme.Panel };
            var test = Btn(L("Проверить задержку"), Icons.Timer);
            test.Click += async (_, _) => { _refresh.Start(); await _owner.TestGroupAsync(); _list.Invalidate(); };
            var all = Btn(L("Все серверы…"), Icons.Server);
            all.Click += (_, _) => { Close(); _owner.ShowPage(_owner._navServers); };
            bottom.Controls.AddRange([test, all]);

            _list.DrawRow = DrawRow;
            _list.RowClicked += async i => await Pick(i);

            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 2, 0), BackColor = Theme.Panel };
            listHost.Controls.Add(_list);
            inner.Controls.Add(listHost);
            inner.Controls.Add(searchBox);
            _groups.BackColor = Theme.Panel;
            if (_app.Groups.Groups.Count > 1) inner.Controls.Add(_groups);
            inner.Controls.Add(bottom);
            inner.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Grid });
            Controls.Add(inner);

            BuildGroups();
            Fill();
            // во время теста только перерисовываем (пинги), список не пересобираем
            _refresh.Tick += (_, _) => { _list.Invalidate(); if (_owner._testing.Count == 0) _refresh.Stop(); };

            // размер и место: под карточкой, по её ширине, не выходя за экран
            var screen = Screen.FromControl(anchor).WorkingArea;
            int width = Math.Max(380, anchor.Width);
            int height = Math.Min(460, screen.Height - 40);
            var p = anchor.PointToScreen(new Point(0, anchor.Height + 4));
            if (p.Y + height > screen.Bottom) p.Y = Math.Max(screen.Top, anchor.PointToScreen(Point.Empty).Y - height - 4);
            Bounds = new Rectangle(Math.Min(p.X, screen.Right - width), p.Y, width, height);
        }

        Button Btn(string text, char icon)
        {
            var b = new Button
            {
                Text = "  " + text, Image = Icons.Get(icon), TextImageRelation = TextImageRelation.ImageBeforeText,
                AutoSize = true, Padding = new Padding(4, 0, 6, 0), Margin = new Padding(0, 0, 8, 0),
            };
            Theme.StyleButton(b);
            return b;
        }

        void BuildGroups()
        {
            _groups.Controls.Clear();
            foreach (var g in _app.Groups.Groups)
            {
                var grp = g;
                bool on = g.Id == _group?.Id;
                var chip = new Button
                {
                    Text = $"{g.Name}  {_app.ServersOf(g).Count}", AutoSize = true, Margin = new Padding(0, 0, 6, 4),
                    Padding = new Padding(4, 0, 4, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                    BackColor = on ? Theme.SelRow : Theme.Header, ForeColor = on ? Theme.Accent : Theme.Text,
                };
                chip.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Grid;
                // пересобрать кнопки групп — после клика (сразу удалять нажатую кнопку нельзя)
                chip.Click += (_, _) => { _group = grp; _app.SetActiveGroup(grp); Fill(); _list.ScrollToTop(); BeginInvoke(BuildGroups); };
                _groups.Controls.Add(chip);
            }
        }

        void Fill()
        {
            var g = _group;
            var q = _search.Text.Trim();
            var items = new List<Item>();
            if (g != null)
            {
                var servers = _app.ServersOf(g);
                if (q.Length == 0)
                {
                    var nowTag = _app.Monitor.AutoNow.GetValueOrDefault(g.AutoTag);
                    var now = nowTag != null ? servers.FirstOrDefault(s => s.Tag == nowTag)?.Name : null;
                    items.Add(new Item(g.AutoKey, g.AutoTag, L("Авто — самый быстрый"), Icons.Badge("AUTO", Theme.Warn),
                        now != null ? L("сейчас: ") + now : L("выбирает сервер с наименьшей задержкой")));
                }
                foreach (var s in servers.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    var (t, c) = Icons.ProtocolOf(s);
                    items.Add(new Item(s.Key, s.Tag, s.Name, Icons.Badge(t, c), ""));
                }
            }
            _items = items;
            _list.Count = items.Count;
        }

        void DrawRow(Graphics g, Rectangle r, int index, bool hover, bool focused)
        {
            var it = _items[index];
            bool sel = _app.Settings.SelectedServer == it.Key;
            var bg = sel ? Theme.SelRow : hover || focused ? Theme.Header : Theme.Panel;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            if (sel) using (var a = new SolidBrush(Theme.Accent)) g.FillRectangle(a, r.X, r.Y + 6, 3, r.Height - 12);

            g.DrawImage(it.Badge, r.X + 10, r.Y + (r.Height - it.Badge.Height) / 2);
            int textX = r.X + 64, right = r.Right - 12;
            if (sel)
            {
                using var pen = new Pen(Theme.Accent, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = right - 8, cy = r.Y + r.Height / 2;
                g.DrawLines(pen, [new Point(cx - 6, cy), new Point(cx - 2, cy + 4), new Point(cx + 6, cy - 4)]);
                right -= 24;
            }
            // пинг справа (фиксированная колонка — без измерения текста на каждой строке)
            bool testing = _owner._testing.Contains(it.Tag);
            bool known = Delay.TryGetValue(it.Tag, out var d);
            string delay = testing ? "…" : known ? (d is { } ms ? Lf($"{ms} мс") : L("таймаут")) : "";
            var dColor = testing || !known ? Theme.Muted : d is null ? Theme.Danger : d < 200 ? Theme.Accent : d < 500 ? Theme.Warn : Theme.Orange;
            const int delayW = 64;
            if (delay.Length > 0)
                TextRenderer.DrawText(g, delay, _regular, new Rectangle(right - delayW, r.Y, delayW, r.Height), dColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            int textW = right - (delay.Length > 0 ? delayW + 8 : 0) - textX;
            var font = sel ? _boldFont : _regular;
            if (it.Sub.Length > 0)
            {
                TextRenderer.DrawText(g, it.Name, font, new Rectangle(textX, r.Y + 2, textW, r.Height / 2), Theme.Text,
                    TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Sub, _small, new Rectangle(textX, r.Y + r.Height / 2, textW, r.Height / 2 - 2), Theme.Muted,
                    TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
                TextRenderer.DrawText(g, it.Name, font, new Rectangle(textX, r.Y, textW, r.Height), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        async Task Pick(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            var key = _items[i].Key;
            Close();
            await _app.SelectServerAsync(key);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _search.Focus();
            // плавное появление со сдвигом вниз на несколько пикселей
            Opacity = 0;
            int finalTop = Top, step = 0;
            Top -= 8;
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            FormClosed += (_, _) => { t.Stop(); t.Dispose(); };   // закрыли раньше, чем закончилась анимация
            t.Tick += (_, _) =>
            {
                if (IsDisposed || Disposing) { t.Stop(); return; }
                step++;
                float k = Math.Min(1, step / 9f), ease = 1 - (1 - k) * (1 - k);
                Opacity = ease;
                Top = finalTop - (int)(8 * (1 - ease));
                if (k >= 1) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!IsDisposed) BeginInvoke(Close); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: Close(); return true;
                case Keys.Down: _list.MoveFocus(+1); return true;
                case Keys.Up: _list.MoveFocus(-1); return true;
                case Keys.PageDown: _list.MoveFocus(+8); return true;
                case Keys.PageUp: _list.MoveFocus(-8); return true;
                case Keys.Enter: _ = Pick(_list.FocusIndex >= 0 ? _list.FocusIndex : 0); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _refresh.Dispose(); _regular.Dispose(); _boldFont.Dispose(); _small.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Лёгкий виртуальный список: рисует только видимые строки, прокрутка плавная (по пикселям, с инерцией),
    /// своя тонкая полоса прокрутки, которую можно тащить мышью.
    /// </summary>
    sealed class SmoothList : Control
    {
        public int RowHeight = 36;
        public Action<Graphics, Rectangle, int, bool, bool>? DrawRow;
        public event Action<int>? RowClicked;
        public int FocusIndex { get; private set; } = -1;

        int _count, _hover = -1;
        float _offset, _target;
        readonly System.Windows.Forms.Timer _anim = new() { Interval = 10 };
        bool _dragThumb; int _dragStartY; float _dragStartOffset;
        readonly Theme.Anim _barHover;
        const int BarW = 6;

        public SmoothList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            _barHover = new Theme.Anim(this, 150);
            _anim.Tick += (_, _) =>
            {
                var d = _target - _offset;
                if (Math.Abs(d) < 0.5f) { _offset = _target; _anim.Stop(); }
                else _offset += d * 0.28f;          // ease-out: каждый кадр проходим часть оставшегося пути
                UpdateHover(PointToClient(MousePosition));
                Invalidate();
            };
            Disposed += (_, _) => _anim.Dispose();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Count
        {
            get => _count;
            set { _count = value; FocusIndex = Math.Min(FocusIndex, value - 1); _target = Clamp(_target); _offset = Clamp(_offset); Invalidate(); }
        }

        float MaxOffset => Math.Max(0, _count * RowHeight - Height);
        float Clamp(float v) => Math.Clamp(v, 0, MaxOffset);

        public void ScrollToTop() { _target = _offset = 0; FocusIndex = -1; Invalidate(); }

        void ScrollTo(float target) { _target = Clamp(target); _anim.Start(); }

        public void MoveFocus(int delta)
        {
            if (_count == 0) return;
            FocusIndex = Math.Clamp(FocusIndex + delta, 0, _count - 1);
            float top = FocusIndex * RowHeight, bottom = top + RowHeight;
            if (top < _target) ScrollTo(top);
            else if (bottom > _target + Height) ScrollTo(bottom - Height);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(_target - e.Delta / 120f * RowHeight * 2.2f);
            base.OnMouseWheel(e);
        }

        Rectangle Thumb()
        {
            if (MaxOffset <= 0) return Rectangle.Empty;
            int h = Math.Max(30, (int)(Height * (Height / (float)(_count * RowHeight))));
            int y = (int)((Height - h) * (_offset / MaxOffset));
            return new Rectangle(Width - BarW - 2, y, BarW, h);
        }

        void UpdateHover(Point p)
        {
            int i = p.X >= 0 && p.X < Width - BarW - 4 && p.Y >= 0 && p.Y < Height ? (int)((p.Y + _offset) / RowHeight) : -1;
            if (i >= _count) i = -1;
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragThumb)
            {
                var th = Thumb();
                float track = Height - th.Height;
                if (track > 0) { _offset = _target = Clamp(_dragStartOffset + (e.Y - _dragStartY) / track * MaxOffset); Invalidate(); }
                return;
            }
            bool overBar = e.X >= Width - BarW - 6 && MaxOffset > 0;
            _barHover.Target = overBar ? 1 : 0;
            Cursor = overBar ? Cursors.Default : Cursors.Hand;
            UpdateHover(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; _barHover.Target = 0; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var th = Thumb();
            if (e.Button == MouseButtons.Left && e.X >= Width - BarW - 6 && MaxOffset > 0)
            {
                if (th.Contains(new Point(Width - BarW - 2, e.Y))) { _dragThumb = true; _dragStartY = e.Y; _dragStartOffset = _offset; Capture = true; }
                else ScrollTo(_target + (e.Y < th.Y ? -Height : Height));   // клик по дорожке — на страницу
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragThumb) { _dragThumb = false; Capture = false; return; }
            if (e.Button == MouseButtons.Left && _hover >= 0) RowClicked?.Invoke(_hover);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            if (DrawRow != null && _count > 0)
            {
                int first = Math.Max(0, (int)(_offset / RowHeight));
                int last = Math.Min(_count - 1, (int)((_offset + Height) / RowHeight));
                int w = Width - BarW - 6;
                for (int i = first; i <= last; i++)
                {
                    var r = new Rectangle(0, (int)(i * RowHeight - _offset), w, RowHeight);
                    DrawRow(g, r, i, i == _hover, i == FocusIndex);
                }
            }
            var th = Thumb();
            if (!th.IsEmpty)
            {
                float h = _barHover.Eased;
                int bw = (int)(4 + 2 * h);
                using var br = new SolidBrush(Theme.Blend(Theme.Grid, Theme.Muted, 0.35f + 0.5f * h));
                using var path = Round(new Rectangle(Width - bw - 2, th.Y + 2, bw, th.Height - 4), bw / 2);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(br, path);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
