using System.Drawing.Drawing2D;

namespace TunPilot;

/// <summary>Статистика трафика за 24 часа / 7 / 30 дней: график по часам или дням, таблицы по серверам и приложениям.</summary>
public sealed class StatsForm : Form
{
    readonly TrayApp _app;
    readonly ComboBox _period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly Label _totals = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(16, 8, 0, 0) };
    readonly Bars _chart = new() { Dock = DockStyle.Top, Height = 240 };
    readonly DataGridView _grid = MainForm.MakeGrid();
    readonly ToolStrip _tabs = new();
    readonly ToolStripButton _bySrv = new(L("По серверу")) { Checked = true }, _byApp = new(L("По приложению"));

    public StatsForm(TrayApp app)
    {
        _app = app;
        Text = L("TunPilot — статистика трафика");
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(700, 500);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg; ForeColor = Theme.Text; Theme.DarkTitle(this);

        _period.Items.AddRange([L("Последние 24 часа"), L("Последние 7 дней"), L("Последние 30 дней")]);
        _period.SelectedIndex = 0;
        _period.SelectedIndexChanged += (_, _) => Reload();
        var refresh = new Button { Text = L("  Обновить"), Image = Icons.Get(Icons.Refresh), TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true };
        refresh.Click += (_, _) => { _app.Monitor.Stats.Save(); Reload(); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0) };
        top.Controls.AddRange([new Label { Text = L("Период:"), AutoSize = true, Margin = new Padding(0, 8, 6, 0) }, _period, _totals, refresh]);

        _tabs.Renderer = new Theme.Renderer();
        _tabs.GripStyle = ToolStripGripStyle.Hidden;
        _tabs.BackColor = Theme.Bg;
        _bySrv.Image = Icons.Get(Icons.Server); _byApp.Image = Icons.Get(Icons.Window);
        foreach (var b in new[] { _bySrv, _byApp })
        {
            b.Click += (_, _) => { _bySrv.Checked = b == _bySrv; _byApp.Checked = b == _byApp; Reload(); };
            _tabs.Items.Add(b);
        }

        _grid.Columns.Add(MainForm.Col("name", L("Профиль"), 0));
        _grid.Columns["name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns.Add(MainForm.Col("group", L("Группа"), 180));
        _grid.Columns.Add(MainForm.Col("in", L("Входящий"), 130, DataGridViewContentAlignment.MiddleRight));
        _grid.Columns.Add(MainForm.Col("out", L("Исходящий"), 130, DataGridViewContentAlignment.MiddleRight));
        _grid.Columns.Add(MainForm.Col("total", L("Всего"), 130, DataGridViewContentAlignment.MiddleRight));

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 10) };
        body.Controls.Add(_grid);
        body.Controls.Add(_tabs);
        body.Controls.Add(_chart);
        Controls.Add(body);
        Controls.Add(top);
        Theme.StyleTree(top);
        Reload();
    }

    void Reload()
    {
        bool hourly = _period.SelectedIndex == 0;
        int days = _period.SelectedIndex switch { 0 => 1, 1 => 7, _ => 30 };
        var now = DateTime.Now;
        var from = hourly ? now.AddHours(-23) : now.Date.AddDays(-(days - 1));
        var fromUtc = (hourly ? new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0) : from).ToUniversalTime();
        var rows = _app.Monitor.Stats.Since(fromUtc);
        var srv = rows.Where(r => r.Kind == "srv").ToList();

        long tin = srv.Sum(r => r.In), tout = srv.Sum(r => r.Out);
        _totals.Text = Lf($"Входящий: {MainForm.Fmt(tin)}    Исходящий: {MainForm.Fmt(tout)}    Всего: {MainForm.Fmt(tin + tout)}");

        // столбцы: 24 часа или N дней
        var buckets = new List<(string Label, long In, long Out)>();
        int n = hourly ? 24 : days;
        for (int i = 0; i < n; i++)
        {
            var start = hourly ? new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0).AddHours(i) : from.AddDays(i);
            var end = hourly ? start.AddHours(1) : start.AddDays(1);
            var part = srv.Where(r => r.HourLocal >= start && r.HourLocal < end).ToList();
            var label = hourly ? (i % 4 == 0 ? start.ToString("HH:00") : "") : (days <= 7 || i % 5 == 0 ? start.ToString("dd.MM") : "");
            buckets.Add((label, part.Sum(r => r.In), part.Sum(r => r.Out)));
        }
        _chart.SetData(buckets);

        // таблица
        bool bySrv = _bySrv.Checked;
        _grid.Columns["name"]!.HeaderText = bySrv ? L("Сервер") : L("Приложение");
        _grid.Columns["group"]!.Visible = bySrv;
        _grid.Rows.Clear();
        foreach (var g in rows.Where(r => r.Kind == (bySrv ? "srv" : "app")).GroupBy(r => r.Key)
                     .Select(g => (Key: g.Key, In: g.Sum(r => r.In), Out: g.Sum(r => r.Out)))
                     .OrderByDescending(x => x.In + x.Out))
        {
            var parts = g.Key.Split('|', 2);
            string name = bySrv && parts.Length == 2 ? parts[1] : g.Key, group = bySrv && parts.Length == 2 ? parts[0] : "";
            _grid.Rows.Add(name, group, MainForm.Fmt(g.In), MainForm.Fmt(g.Out), MainForm.Fmt(g.In + g.Out));
        }
    }

    /// <summary>Столбчатая диаграмма: входящий (синий) + исходящий (зелёный) стопкой.</summary>
    sealed class Bars : Control
    {
        List<(string Label, long In, long Out)> _data = [];

        public Bars()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Font = new Font("Segoe UI", 8.5f);
        }

        public void SetData(List<(string, long, long)> d) { _data = d; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            const int left = 80, right = 16, top = 30, bottom = 24;
            var plot = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            long max = Math.Max(1024 * 1024, _data.Count == 0 ? 0 : _data.Max(d => d.In + d.Out));
            using var grid = new Pen(Theme.Grid);
            using var muted = new SolidBrush(Theme.Muted);
            var far = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            for (int i = 0; i <= 4; i++)
            {
                int y = plot.Bottom - plot.Height * i / 4;
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                g.DrawString(MainForm.Fmt(max * i / 4), Font, muted, new RectangleF(0, y - 9, left - 8, 18), far);
            }
            // легенда
            var inC = Color.FromArgb(80, 140, 240); var outC = Theme.Accent;
            using (var b1 = new SolidBrush(inC)) g.FillRectangle(b1, plot.Right - 190, 8, 12, 12);
            g.DrawString(L("Входящий"), Font, muted, plot.Right - 174, 6);
            using (var b2 = new SolidBrush(outC)) g.FillRectangle(b2, plot.Right - 90, 8, 12, 12);
            g.DrawString(L("Исходящий"), Font, muted, plot.Right - 74, 6);

            if (_data.Count == 0) return;
            float slot = plot.Width / (float)_data.Count, bw = Math.Max(2, slot * 0.6f);
            var center = new StringFormat { Alignment = StringAlignment.Center };
            using var inBr = new SolidBrush(inC);
            using var outBr = new SolidBrush(outC);
            for (int i = 0; i < _data.Count; i++)
            {
                var (label, vin, vout) = _data[i];
                float x = plot.Left + slot * i + (slot - bw) / 2;
                float hIn = plot.Height * vin / (float)max, hOut = plot.Height * vout / (float)max;
                if (hIn > 0) g.FillRectangle(inBr, x, plot.Bottom - hIn, bw, hIn);
                if (hOut > 0) g.FillRectangle(outBr, x, plot.Bottom - hIn - hOut, bw, hOut);
                if (label.Length > 0) g.DrawString(label, Font, muted, x + bw / 2, plot.Bottom + 4, center);
            }
        }
    }
}
