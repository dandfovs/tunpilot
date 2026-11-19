namespace TunPilot;

/// <summary>Управление группами: подписки и ручные ключи (как «Группы» в Throne).</summary>
public sealed class GroupsForm : Form
{
    readonly TrayApp _app;
    readonly FlowLayoutPanel _list = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(10),
    };

    public GroupsForm(TrayApp app)
    {
        _app = app;
        Text = L("TunPilot — группы и подписки");
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1040, 520);
        MinimumSize = new Size(700, 360);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg; ForeColor = Theme.Text; Theme.DarkTitle(this);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10, 8, 10, 8), FlowDirection = FlowDirection.LeftToRight };
        bottom.Controls.AddRange([
            Btn(L("Новая подписка"), Icons.Cloud, async () => await EditAsync(null, "subscription")),
            Btn(L("Группа ключей"), Icons.Key, async () => await EditAsync(null, "manual")),
            Btn(L("Обновить все"), Icons.Refresh, async () => { await _app.UpdateAllAsync(manual: true); Rebuild(); }),
            Btn(L("Добавить Warp"), Icons.Cloud, async () => await AddWarpAsync()),
        ]);
        Theme.DarkScroll(_list);
        Controls.Add(_list);
        Controls.Add(bottom);
        _list.Resize += (_, _) => { foreach (Control c in _list.Controls) c.Width = _list.ClientSize.Width - 24; };
        Rebuild();
    }

    Button Btn(string text, char icon, Func<Task> onClick)
    {
        var b = new Button
        {
            Text = "  " + text, Image = Icons.Get(icon), ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText, AutoSize = true, Padding = new Padding(6, 0, 8, 0),
        };
        Theme.StyleButton(b);
        b.Click += async (_, _) => { if (!Theme.SetBusy(b, true)) return; try { await onClick(); } finally { Theme.SetBusy(b, false); } };
        return b;
    }

    void Rebuild()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        if (_app.Groups.Groups.Count == 0)
            _list.Controls.Add(new Label
            {
                Text = L("Групп пока нет. Нажмите «Новая подписка» и вставьте ссылку от провайдера VPN,\n") +
                       L("или «Группа ключей», чтобы добавить отдельные ссылки vless://, hy2://…"),
                AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(4, 10, 0, 0),
            });
        foreach (var g in _app.Groups.Groups) _list.Controls.Add(Card(g));
        _list.ResumeLayout();
    }

    Control Card(Group g)
    {
        int count = _app.ServersOf(g).Count;
        var card = new Panel { Height = g.IsSubscription ? 84 : 60, Width = _list.ClientSize.Width - 24, BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, 8) };
        var type = new Label
        {
            Text = (g.IsSubscription ? L("Подписка") : L("Ключи")) + $" ({count})", AutoSize = true, Left = 12, Top = 10,
            ForeColor = g.IsSubscription ? Theme.Danger : Theme.Warn,
        };
        var name = new Label { Text = g.Name, AutoSize = true, Top = 10, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text };
        card.Controls.Add(type);
        card.Controls.Add(name);
        card.Layout += (_, _) => name.Left = type.Right + 8;

        if (g.IsSubscription)
        {
            card.Controls.Add(new Label { Text = g.Url, AutoSize = true, Left = 12, Top = 34, ForeColor = Theme.Muted, MaximumSize = new Size(560, 0) });
            var i = g.Info;
            long used = (i?.Upload ?? 0) + (i?.Download ?? 0);
            var left = i is { Total: > 0 } ? MainForm.Fmt(Math.Max(0, i.Total - used)) : "∞";
            var exp = i is { Expire: > 0 } ? DateTimeOffset.FromUnixTimeSeconds(i.Expire).LocalDateTime : (DateTime?)null;
            var mins = g.UpdateIntervalMinutes(_app.Settings);
            card.Controls.Add(new Label
            {
                AutoSize = true, Left = 12, Top = 56, ForeColor = exp < DateTime.Now ? Theme.Danger : Theme.Text,
                Text = Lf($"Обновлено: {(g.UpdatedUtc == default ? "—" : g.UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))}") +
                       Lf($"  |  Использовано: {MainForm.Fmt(used)}, осталось: {left}") +
                       Lf($"  |  Истекает: {(exp is { } e ? e.ToString("dd.MM.yyyy") + (e < DateTime.Now ? L(" (истекла)") : "") : L("нет"))}") +
                       Lf($"  |  Автообновление: {(mins > 0 ? (mins % 60 == 0 ? Lf($"каждые {mins / 60} ч") : Lf($"каждые {mins} мин")) : L("выкл."))}"),
            });
        }
        else
            card.Controls.Add(new Label { Text = L("Отдельные ключи. Добавляйте ссылки через «Изменить» или Ctrl+V в главном окне."), AutoSize = true, Left = 12, Top = 34, ForeColor = Theme.Muted });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Top = 8, BackColor = Theme.Panel };
        buttons.Controls.Add(Btn(L("Удалить"), Icons.Delete, async () =>
        {
            if (await DeleteGroupAsync(_app, g)) Rebuild();
        }));
        buttons.Controls.Add(Btn(L("Изменить"), Icons.Edit, async () => await EditAsync(g, g.Type)));
        if (g.IsSubscription)
            buttons.Controls.Add(Btn(L("Обновить"), Icons.Refresh, async () => { await _app.UpdateGroupAsync(g, manual: true, force: true); Rebuild(); }));
        card.Controls.Add(buttons);
        card.Layout += (_, _) => buttons.Left = card.Width - buttons.Width - 8;
        return card;
    }

    async Task EditAsync(Group? g, string type)
    {
        if (await EditGroupAsync(_app, this, g, type)) Rebuild();
    }

    /// <summary>Удалить группу с подтверждением (общая логика для окна групп и меню вкладок).</summary>
    internal static async Task<bool> DeleteGroupAsync(TrayApp app, Group g)
    {
        if (!TrayApp.Confirm(Lf($"Удалить группу «{g.Name}»?"))) return false;
        if (app.Settings.SelectedServer.StartsWith(g.Id) || app.Settings.SelectedServer == g.AutoKey) app.Settings.SelectedServer = "";
        app.Groups.Remove(g);
        await app.GroupsChangedAsync();
        return true;
    }

    /// <summary>Диалог создания/изменения группы и применение результата. false — если отменили.</summary>
    internal static async Task<bool> EditGroupAsync(TrayApp _app, IWin32Window owner, Group? g, string type)
    {
        using var f = new GroupEditForm(g, type, g != null ? _app.ServersOf(g) : []);
        if (f.ShowDialog(owner) != DialogResult.OK) return false;
        bool isNew = g == null;
        g ??= _app.Groups.Add(f.GroupName, type, f.Url);
        bool urlChanged = g.Url != f.Url;
        g.Name = f.GroupName;
        g.Url = f.Url;
        g.SkipAutoUpdate = f.SkipAutoUpdate;
        if (!g.IsSubscription) g.Body = f.Links;
        if (urlChanged && !isNew) { g.Body = ""; g.Info = null; }
        _app.Groups.Save();
        if (isNew) _app.SetActiveGroup(g);
        if (g.IsSubscription && (isNew || urlChanged)) await _app.UpdateGroupAsync(g, manual: false, force: true);
        await _app.GroupsChangedAsync();
        return true;
    }

    async Task AddWarpAsync()
    {
        if (!TrayApp.Confirm(L("Создать бесплатный аккаунт Cloudflare WARP и добавить его как сервер?\n") +
                             L("Программа зарегистрирует новое устройство на серверах Cloudflare (без логина и почты)."))) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            var link = await Warp.RegisterAsync(_app.Core, _app.SelectedTag);
            var n = await _app.AddLinksAsync(link);
            _app.Balloon(n > 0 ? L("WARP добавлен в группу ключей.") : L("WARP уже есть в списке."), ToolTipIcon.Info);
            Rebuild();
        }
        catch (Exception ex) { TrayApp.ShowError(L("Не удалось создать WARP:\n") + ex.Message + L("\n\nЕсли Cloudflare заблокирован, подключитесь к VPN и попробуйте снова.")); }
        finally { Cursor = Cursors.Default; }
    }
}

/// <summary>Диалог «Изменить группу».</summary>
sealed class GroupEditForm : Form
{
    readonly TextBox _name = new() { Width = 520 };
    readonly TextBox _url = new() { Width = 520 };
    readonly TextBox _links = new() { Width = 520, Height = 200, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9.5f) };
    readonly CheckBox _skip = new() { Text = L("Не обновлять автоматически"), AutoSize = true };
    readonly string _type;

    public string GroupName => _name.Text.Trim();
    public string Url => _url.Text.Trim();
    public string Links => _links.Text.Replace("\r", "").Trim();
    public bool SkipAutoUpdate => _skip.Checked;

    public GroupEditForm(Group? g, string type, List<ServerInfo> servers)
    {
        _type = type;
        Text = g == null ? (type == "subscription" ? L("Новая подписка") : L("Новая группа ключей")) : Lf($"Изменить «{g.Name}»");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg; ForeColor = Theme.Text; Theme.DarkTitle(this);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _name.Text = g?.Name ?? (type == "subscription" ? "" : L("Ключи"));
        _url.Text = g?.Url ?? "";
        _links.Text = (g != null && type == "manual" ? g.Body : "").Replace("\n", "\r\n");
        _skip.Checked = g?.SkipAutoUpdate ?? false;

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c) { t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }); t.Controls.Add(c); }
        Row(L("Название"), _name);
        if (type == "subscription")
        {
            Row(L("Ссылка"), _url);
            Row("", _skip);
            if (g == null) Row("", new Label { Text = L("Название можно не заполнять — возьмётся из подписки."), AutoSize = true, ForeColor = Theme.Muted });
        }
        else
        {
            Row(L("Ключи"), _links);
            Row("", new Label { Text = L("По одной ссылке на строку: vless://…, hy2://…"), AutoSize = true, ForeColor = Theme.Muted });
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = "OK", Width = 90 };
        var cancel = new Button { Text = L("Отмена"), Width = 90, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_type == "subscription" && !Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { TrayApp.ShowError(L("Ссылка на подписку должна начинаться с http:// или https://")); return; }
            if (_name.Text.Trim().Length == 0) _name.Text = _type == "subscription" ? L("Подписка") : L("Ключи");
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.AddRange([cancel, ok]);
        if (servers.Count > 0)
        {
            var copy = new Button { Text = L("Копировать ссылки серверов"), AutoSize = true };
            copy.Click += (_, _) => { Clipboard.SetText(string.Join("\n", servers.Select(s => s.Raw))); copy.Text = L("Скопировано"); };
            buttons.Controls.Add(copy);
        }
        t.Controls.Add(new Label());
        t.Controls.Add(buttons);
        Controls.Add(t);
        AcceptButton = type == "subscription" ? ok : null;
        CancelButton = cancel;
        Theme.StyleTree(this);
    }
}
