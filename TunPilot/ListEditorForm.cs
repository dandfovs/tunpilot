namespace TunPilot;

/// <summary>Редактор трёх списков: напрямую / через VPN / мимо TUN.</summary>
public sealed class ListEditorForm : Form
{
    readonly (string Path, string Title, char Icon, string Hint, TextBox Box)[] _tabs;
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0) };
    readonly ToolStrip _strip = new();
    readonly Label _hint = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
    string _bypassOriginal = "";
    public bool BypassChanged { get; private set; }

    public ListEditorForm()
    {
        Text = Lf($"TunPilot — списки профиля «{Profiles.Current.Name}»");
        BackColor = Theme.Bg; ForeColor = Theme.Text; Theme.DarkTitle(this);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(500, 400);
        Font = new Font("Segoe UI", 9.5f);

        _tabs =
        [
            (RuleLists.DirectTxt, L("Напрямую"), Icons.Plug, L("Мимо VPN. Применяется сразу, без перезапуска."), MakeBox()),
            (RuleLists.ProxyTxt, L("Через VPN"), Icons.Shield, L("Всегда через VPN. Применяется сразу, без перезапуска."), MakeBox()),
            (RuleLists.BypassTxt, L("Мимо TUN"), Icons.Steam, L("Только IP/подсети IPv4. Трафик вообще не попадает в TUN — перезапуск VPN его не касается."), MakeBox()),
        ];
        foreach (var t in _tabs) t.Box.Text = File.Exists(t.Path) ? File.ReadAllText(t.Path).ReplaceLineEndings("\r\n") : "";
        _bypassOriginal = _tabs[2].Box.Text;

        // вкладки — как в окне настроек (TabControl Windows не перекрашивается)
        _strip.Renderer = new Theme.Renderer();
        _strip.GripStyle = ToolStripGripStyle.Hidden;
        _strip.BackColor = Theme.Bg;
        _strip.Padding = new Padding(10, 6, 10, 0);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int idx = i;
            var b = new ToolStripButton(_tabs[i].Title, Icons.Get(_tabs[i].Icon)) { Padding = new Padding(4, 2, 4, 4) };
            b.Click += (_, _) => Select(idx);
            _strip.Items.Add(b);
        }

        _hint.ForeColor = Theme.Muted;
        _host.Resize += (_, _) => _hint.MaximumSize = new Size(Math.Max(100, _host.ClientSize.Width - _host.Padding.Horizontal), 0);

        var help = new Label { Text = RuleLists.Help, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 6, 8, 6), ForeColor = Theme.Muted };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var cancel = new Button { Text = L("Отмена"), DialogResult = DialogResult.Cancel, Width = 100, Height = 30 };
        var save = new Button { Text = L("Сохранить"), Width = 110, Height = 30 };
        save.Click += (_, _) => Save();
        buttons.Controls.AddRange([cancel, save]);
        Theme.StyleButton(cancel); Theme.StyleButton(save);

        Controls.Add(_host);
        Controls.Add(_strip);
        Controls.Add(help);
        Controls.Add(buttons);
        AcceptButton = null;   // Enter должен переносить строку в редакторе
        CancelButton = cancel;
        Select(0);
    }

    void Select(int i)
    {
        for (int k = 0; k < _strip.Items.Count; k++) ((ToolStripButton)_strip.Items[k]).Checked = k == i;
        _host.SuspendLayout();
        _host.Controls.Clear();
        _hint.Text = _tabs[i].Hint;
        // рамка поля — тонкая, в цвет сетки (вместо белой у стандартного поля)
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Grid };
        frame.Controls.Add(_tabs[i].Box);
        _host.Controls.Add(frame);
        _host.Controls.Add(_hint);
        _host.ResumeLayout();
        _tabs[i].Box.Focus();
        _tabs[i].Box.Select(0, 0);
    }

    static TextBox MakeBox()
    {
        var b = new TextBox
        {
            Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            Font = new Font("Consolas", 10f), WordWrap = false, BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel, ForeColor = Theme.Text,
        };
        Theme.DarkScroll(b);
        return b;
    }

    void Save()
    {
        foreach (var t in _tabs) File.WriteAllText(t.Path, t.Box.Text.ReplaceLineEndings("\n"));
        BypassChanged = _tabs[2].Box.Text != _bypassOriginal;
        DialogResult = DialogResult.OK;
    }
}
