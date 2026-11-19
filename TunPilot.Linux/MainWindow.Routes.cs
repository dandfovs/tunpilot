using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TunPilot.LinuxApp;

sealed partial class MainWindow
{
    Control BuildRoutes()
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(24, 18), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        string sig = "";

        void Build()
        {
            var p = Profiles.Current;
            body.Children.Clear();

            body.Children.Add(Ui.H(L("Профиль")));
            body.Children.Add(Ui.Hint(L("Профиль — набор правил: какие сайты идут через VPN, а какие напрямую. Переключение обычно проходит без переподключения.")));
            foreach (var prof in Profiles.All())
            {
                var rb = new RadioButton { Content = prof.Name, GroupName = "profile", IsChecked = prof.Name == p.Name };
                rb.IsCheckedChanged += async (_, _) => { if (rb.IsChecked == true && prof.Name != Profiles.Current.Name) await App.SwitchProfileAsync(prof); };
                body.Children.Add(rb);
            }
            var row = new WrapPanel();
            void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(c); }
            Add(Ui.BtnAsync(L("Новый"), async () =>
            {
                if (await Dialogs.PromptAsync(L("Название нового профиля:"), "") is { Length: > 0 } n) await App.SwitchProfileAsync(Profiles.Create(n.Trim()));
            }));
            Add(Ui.BtnAsync(L("Копия текущего"), async () =>
            {
                if (await Dialogs.PromptAsync(L("Название копии:"), p.Name + L(" (копия)")) is { Length: > 0 } n) await App.SwitchProfileAsync(Profiles.Clone(p, n.Trim()));
            }));
            Add(Ui.BtnAsync(L("Переименовать"), async () =>
            {
                if (await Dialogs.PromptAsync(L("Новое название:"), p.Name) is { Length: > 0 } n && Profiles.Find(n.Trim()) == null)
                { p.Name = n.Trim(); p.Save(); App.Settings.ActiveProfile = p.Name; App.Settings.Save(); sig = ""; Refresh(); }
            }));
            Add(Ui.BtnAsync(L("Удалить"), async () =>
            {
                var others = Profiles.All().Where(x => x.Name != p.Name).ToList();
                if (others.Count == 0) { await Dialogs.MessageAsync("TunPilot", L("Нельзя удалить единственный профиль.")); return; }
                if (!await Dialogs.ConfirmAsync(Lf($"Удалить профиль «{p.Name}» со всеми его списками?"))) return;
                await App.SwitchProfileAsync(others[0]);
                Profiles.Delete(p);
                sig = ""; Refresh();
            }));
            Add(Ui.BtnAsync(L("Импорт из другого клиента…"), async () =>
            {
                var clip = Clipboard != null ? (await Clipboard.TryGetTextAsync() ?? "").Trim() : "";
                var text = await Dialogs.PromptAsync(L("Вставьте ссылку, текст настроек или ссылку на файл — или нажмите «Файл…».\n") + RouteImport.SupportedText,
                    RouteImport.LooksImportable(clip) ? clip : "", multiline: true, withFile: true);
                if (text != null) await App.ImportRoutesAsync(text);
                sig = ""; Refresh();
            }));
            body.Children.Add(row);

            body.Children.Add(Ui.H(L("Готовые наборы (geoip / geosite)")));
            body.Children.Add(Ui.Hint(L("Большие готовые списки: российские сайты и IP, YouTube, Telegram и другие. ") +
                                      L("Обновляются автоматически раз в сутки. Добавить или убрать можно без переподключения.")));
            body.Children.Add(GeoRow(L("Напрямую"), p.GeoDirect, p.GeoProxy, Ui.Direct));
            body.Children.Add(GeoRow(L("Через VPN"), p.GeoProxy, p.GeoDirect, Ui.Accent));

            body.Children.Add(Ui.H(L("Списки сайтов и подсетей")));
            body.Children.Add(Ui.Hint(L("Напрямую, через VPN и мимо TUN (для игр). Изменения применяются сразу, без переподключения.")));
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Btn(L("Открыть списки"), () => _ = ListEditorWindow.ShowAsync(this, App)) } });

            body.Children.Add(Ui.H(L("Всё остальное")));
            body.Children.Add(Ui.Hint(L("Куда идут сайты, которых нет в списках. Смена требует переподключения (программа спросит).")));
            var viaVpn = new RadioButton { Content = L("через VPN"), GroupName = "rest", IsChecked = p.DefaultOutbound != "direct" };
            var direct = new RadioButton { Content = L("напрямую"), GroupName = "rest", IsChecked = p.DefaultOutbound == "direct", Margin = new Thickness(16, 0, 0, 0) };
            viaVpn.IsCheckedChanged += async (_, _) => { if (viaVpn.IsChecked == true) await App.SetDefaultRouteAsync("proxy"); };
            direct.IsCheckedChanged += async (_, _) => { if (direct.IsChecked == true) await App.SetDefaultRouteAsync("direct"); };
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { viaVpn, direct } });

            body.Children.Add(Ui.H(L("Игры")));
            var steam = new CheckBox { Content = L("Ретрансляторы Steam (Dota 2) мимо VPN"), IsChecked = App.Settings.SteamSdrBypass };
            steam.IsCheckedChanged += async (_, _) => await App.SetSteamBypassAsync(steam.IsChecked == true);
            body.Children.Add(steam);
            body.Children.Add(Ui.Hint(L("Игровой трафик идёт напрямую — ниже пинг. Включается без переподключения.")));
        }

        Control GeoRow(string title, List<string> list, List<string> other, Color color)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            row.Children.Add(new TextBlock { Text = title + ":", FontWeight = FontWeight.SemiBold, Foreground = Ui.B(color), MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
            foreach (var name in list.ToList())
            {
                var chip = new Button { Content = name + "   ✕", Classes = { "flat" }, Margin = new Thickness(0, 0, 6, 6) };
                var desc = GeoRuleSets.TitleOf(name);
                ToolTip.SetTip(chip, (desc.Length > 0 ? desc + "\n" : "") + L("Нажмите, чтобы убрать"));
                chip.Click += async (_, _) => { list.Remove(name); await App.GeoChangedAsync(); sig = ""; Refresh(); };
                row.Children.Add(chip);
            }
            var add = new Button { Content = L("+ Добавить"), Classes = { "flat" }, Margin = new Thickness(0, 0, 6, 6) };
            var menu = new MenuFlyout();
            async Task AddSet(string n)
            {
                n = n.Trim().ToLowerInvariant().Replace(':', '-');
                if (n.Length == 0 || list.Contains(n)) return;
                if (!n.StartsWith("geoip-") && !n.StartsWith("geosite-")) n = "geosite-" + n;
                other.Remove(n);
                list.Add(n);
                var missing = await App.GeoChangedAsync();
                if (missing.Contains(n))
                {
                    list.Remove(n);
                    await App.GeoChangedAsync();
                    await Dialogs.MessageAsync("TunPilot", Lf($"Набор «{n}» не найден в репозитории наборов.\nПроверьте название (например geosite-youtube, geoip-ru)."), error: true);
                }
                sig = ""; Refresh();
            }
            foreach (var (n, t) in GeoRuleSets.Popular.Where(x => !list.Contains(x.Name)))
            {
                var mi = new MenuItem { Header = $"{t}   ({n})" };
                mi.Click += async (_, _) => await AddSet(n);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var custom = new MenuItem { Header = L("Другой набор по названию…") };
            custom.Click += async (_, _) =>
            {
                if (await Dialogs.PromptAsync(L("Название набора (как в Throne/v2rayN), например geosite-netflix или geoip-ru:"), "") is { Length: > 0 } n) await AddSet(n);
            };
            menu.Items.Add(custom);
            add.Flyout = menu;
            row.Children.Add(add);
            if (list.Count == 0) row.Children.Add(new TextBlock { Text = L("нет"), Foreground = Ui.B(Ui.Muted), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        _refreshPage = () =>
        {
            var p = Profiles.Current;
            var s = string.Join("|", Profiles.All().Select(x => x.Name)) + p.Name + p.DefaultOutbound + string.Join(",", p.GeoDirect) + ";" + string.Join(",", p.GeoProxy) + App.Settings.SteamSdrBypass;
            if (s == sig) return;
            sig = s;
            Build();
        };
        return new ScrollViewer { Content = body };
    }
}
