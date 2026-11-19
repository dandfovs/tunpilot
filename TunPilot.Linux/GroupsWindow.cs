using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TunPilot.LinuxApp;

/// <summary>Группы: подписки и ручные ключи.</summary>
static class GroupsWindow
{
    public static async Task ShowAsync(Window owner, AppController app)
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        var w = new Window
        {
            Title = L("TunPilot — группы и подписки"), Width = 1000, Height = 540, MinWidth = 640, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.B(Ui.Bg), Icon = LinuxShell.AppIcon,
        };

        void Rebuild()
        {
            list.Children.Clear();
            if (app.Groups.Groups.Count == 0)
                list.Children.Add(Ui.Hint(L("Групп пока нет. Нажмите «Новая подписка» и вставьте ссылку от провайдера VPN,\n") +
                                          L("или «Группа ключей», чтобы добавить отдельные ссылки vless://, hy2://…")));
            foreach (var g in app.Groups.Groups) list.Children.Add(Card(g));
        }

        Control Card(Group g)
        {
            int count = app.ServersOf(g).Count;
            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    Ui.T((g.IsSubscription ? L("Подписка") : L("Ключи")) + $" ({count})", color: g.IsSubscription ? Ui.Danger : Ui.Warn),
                    Ui.T(g.Name, weight: FontWeight.Bold),
                },
            });
            if (g.IsSubscription)
            {
                var url = Ui.T(g.Url, 12.5, color: Ui.Muted); url.TextTrimming = TextTrimming.CharacterEllipsis;
                info.Children.Add(url);
                var i = g.Info;
                long used = (i?.Upload ?? 0) + (i?.Download ?? 0);
                var left = i is { Total: > 0 } ? Ui.Fmt(Math.Max(0, i.Total - used)) : "∞";
                var exp = i is { Expire: > 0 } ? DateTimeOffset.FromUnixTimeSeconds(i.Expire).LocalDateTime : (DateTime?)null;
                var mins = g.UpdateIntervalMinutes(app.Settings);
                info.Children.Add(Ui.T(
                    Lf($"Обновлено: {(g.UpdatedUtc == default ? "—" : g.UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))}") +
                    Lf($"  |  Использовано: {Ui.Fmt(used)}, осталось: {left}") +
                    Lf($"  |  Истекает: {(exp is { } e ? e.ToString("dd.MM.yyyy") + (e < DateTime.Now ? L(" (истекла)") : "") : L("нет"))}") +
                    Lf($"  |  Автообновление: {(mins > 0 ? (mins % 60 == 0 ? Lf($"каждые {mins / 60} ч") : Lf($"каждые {mins} мин")) : L("выкл."))}"),
                    12.5, color: exp < DateTime.Now ? Ui.Danger : Ui.Text, wrap: true));
            }
            else info.Children.Add(Ui.Hint(L("Отдельные ключи. Добавляйте ссылки через «Изменить» или Ctrl+V в главном окне.")));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            if (g.IsSubscription) buttons.Children.Add(Ui.BtnAsync(L("Обновить"), async () => { await app.UpdateGroupAsync(g, manual: true, force: true); Rebuild(); }));
            buttons.Children.Add(Ui.BtnAsync(L("Изменить"), async () => { if (await EditGroupAsync(app, g, g.Type)) Rebuild(); }));
            buttons.Children.Add(Ui.BtnAsync(L("Удалить"), async () => { if (await DeleteGroupAsync(app, g)) Rebuild(); }));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(info); grid.Children.Add(buttons);
            return Ui.Card(grid);
        }

        var bottom = new WrapPanel { Margin = new Thickness(12, 8) };
        foreach (var b in new Control[]
        {
            Ui.BtnAsync(L("Новая подписка"), async () => { if (await EditGroupAsync(app, null, "subscription")) Rebuild(); }, primary: true),
            Ui.BtnAsync(L("Группа ключей"), async () => { if (await EditGroupAsync(app, null, "manual")) Rebuild(); }),
            Ui.BtnAsync(L("Обновить все"), async () => { await app.UpdateAllAsync(manual: true); Rebuild(); }),
            Ui.BtnAsync(L("Добавить Warp"), async () => { await AddWarpAsync(app); Rebuild(); }),
        })
        { b.Margin = new Thickness(0, 0, 8, 0); bottom.Children.Add(b); }

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list });
        w.Content = root;
        Rebuild();
        await w.ShowDialog(owner);
    }

    public static async Task<bool> DeleteGroupAsync(AppController app, Group g)
    {
        if (!await Dialogs.ConfirmAsync(Lf($"Удалить группу «{g.Name}»?"))) return false;
        if (app.Settings.SelectedServer.StartsWith(g.Id) || app.Settings.SelectedServer == g.AutoKey) app.Settings.SelectedServer = "";
        app.Groups.Remove(g);
        await app.GroupsChangedAsync();
        return true;
    }

    /// <summary>Диалог создания/изменения группы и применение результата. false — если отменили.</summary>
    public static async Task<bool> EditGroupAsync(AppController app, Group? g, string type)
    {
        bool sub = type == "subscription";
        var servers = g != null ? app.ServersOf(g) : [];
        var name = new TextBox { Text = g?.Name ?? (sub ? "" : L("Ключи")) };
        var url = new TextBox { Text = g?.Url ?? "" };
        var links = new TextBox { Text = g != null && !sub ? g.Body : "", AcceptsReturn = true, Height = 200, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.NoWrap };
        var skip = new CheckBox { Content = L("Не обновлять автоматически"), IsChecked = g?.SkipAutoUpdate ?? false };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Ui.T(L("Название"))); body.Children.Add(name);
        if (sub)
        {
            body.Children.Add(Ui.T(L("Ссылка"))); body.Children.Add(url); body.Children.Add(skip);
            if (g == null) body.Children.Add(Ui.Hint(L("Название можно не заполнять — возьмётся из подписки.")));
        }
        else
        {
            body.Children.Add(Ui.T(L("Ключи"))); body.Children.Add(links);
            body.Children.Add(Ui.Hint(L("По одной ссылке на строку: vless://…, hy2://…")));
        }
        bool ok = false;
        var w = Dialogs.Make(g == null ? (sub ? L("Новая подписка") : L("Новая группа ключей")) : Lf($"Изменить «{g.Name}»"), body, 600);
        var okBtn = Ui.BtnAsync("OK", async () =>
        {
            if (sub && !(url.Text ?? "").Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await Dialogs.MessageAsync("TunPilot", L("Ссылка на подписку должна начинаться с http:// или https://"), error: true); return; }
            if ((name.Text ?? "").Trim().Length == 0) name.Text = sub ? L("Подписка") : L("Ключи");
            ok = true; w.Close();
        }, primary: true);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (servers.Count > 0)
        {
            Button? copy = null;
            copy = Ui.BtnAsync(L("Копировать ссылки серверов"), async () =>
            {
                if (w.Clipboard != null) await w.Clipboard.SetTextAsync(string.Join("\n", servers.Select(s => s.Raw)));
                copy!.Content = L("Скопировано");
            });
            row.Children.Add(copy);
        }
        row.Children.Add(okBtn);
        row.Children.Add(Ui.Btn(L("Отмена"), () => w.Close()));
        body.Children.Add(row);
        await Dialogs.ShowAsync(w);
        if (!ok) return false;

        bool isNew = g == null;
        var newUrl = (url.Text ?? "").Trim();
        g ??= app.Groups.Add(name.Text!.Trim(), type, newUrl);
        bool urlChanged = g.Url != newUrl;
        g.Name = name.Text!.Trim();
        g.Url = newUrl;
        g.SkipAutoUpdate = skip.IsChecked == true;
        if (!g.IsSubscription) g.Body = (links.Text ?? "").Replace("\r", "").Trim();
        if (urlChanged && !isNew) { g.Body = ""; g.Info = null; }
        app.Groups.Save();
        if (isNew) app.SetActiveGroup(g);
        if (g.IsSubscription && (isNew || urlChanged)) await app.UpdateGroupAsync(g, manual: false, force: true);
        await app.GroupsChangedAsync();
        return true;
    }

    static async Task AddWarpAsync(AppController app)
    {
        if (!await Dialogs.ConfirmAsync(L("Создать бесплатный аккаунт Cloudflare WARP и добавить его как сервер?\n") +
                                        L("Программа зарегистрирует новое устройство на серверах Cloudflare (без логина и почты)."))) return;
        try
        {
            var link = await Warp.RegisterAsync(app.Core, app.SelectedTag);
            var n = await app.AddLinksAsync(link);
            LinuxShell.Instance?.Window?.Toast(n > 0 ? L("WARP добавлен в группу ключей.") : L("WARP уже есть в списке."), NotifyKind.Info);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync("TunPilot", L("Не удалось создать WARP:\n") + ex.Message + L("\n\nЕсли Cloudflare заблокирован, подключитесь к VPN и попробуйте снова."), error: true);
        }
    }
}
