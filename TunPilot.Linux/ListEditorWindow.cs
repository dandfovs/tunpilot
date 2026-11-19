using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TunPilot.LinuxApp;

/// <summary>Редактор трёх списков: напрямую / через VPN / мимо TUN.</summary>
static class ListEditorWindow
{
    public static async Task ShowAsync(Window owner, AppController app)
    {
        (string Path, string Title, string Hint)[] defs =
        [
            (RuleLists.DirectTxt, L("Напрямую"), L("Мимо VPN. Применяется сразу, без перезапуска.")),
            (RuleLists.ProxyTxt, L("Через VPN"), L("Всегда через VPN. Применяется сразу, без перезапуска.")),
            (RuleLists.BypassTxt, L("Мимо TUN"), L("Только IP/подсети IPv4. Трафик вообще не попадает в TUN — перезапуск VPN его не касается.")),
        ];
        var tabs = new TabControl { Margin = new Thickness(10, 6) };
        var boxes = new List<TextBox>();
        foreach (var d in defs)
        {
            var box = new TextBox
            {
                Text = File.Exists(d.Path) ? File.ReadAllText(d.Path).ReplaceLineEndings("\n") : "",
                AcceptsReturn = true, FontFamily = new FontFamily("monospace"), FontSize = 13, TextWrapping = TextWrapping.NoWrap,
            };
            boxes.Add(box);
            var page = new DockPanel();
            var hint = Ui.Hint(d.Hint); hint.Margin = new Thickness(0, 4, 0, 6);
            DockPanel.SetDock(hint, Dock.Top);
            page.Children.Add(hint); page.Children.Add(box);
            tabs.Items.Add(new TabItem { Header = d.Title, Content = page });
        }
        var bypassOriginal = boxes[2].Text ?? "";

        var w = new Window
        {
            Title = Lf($"TunPilot — списки профиля «{Profiles.Current.Name}»"), Width = 700, Height = 600, MinWidth = 500, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.B(Ui.Bg), Icon = LinuxShell.AppIcon,
        };
        bool saved = false;
        var help = Ui.Hint(RuleLists.Help); help.Margin = new Thickness(12, 4);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8),
            Children =
            {
                Ui.Btn(L("Сохранить"), () =>
                {
                    for (int i = 0; i < defs.Length; i++) File.WriteAllText(defs[i].Path, (boxes[i].Text ?? "").ReplaceLineEndings("\n"));
                    saved = true; w.Close();
                }, primary: true),
                Ui.Btn(L("Отмена"), () => w.Close()),
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom); DockPanel.SetDock(help, Dock.Bottom);
        root.Children.Add(buttons); root.Children.Add(help); root.Children.Add(tabs);
        w.Content = root;
        await w.ShowDialog(owner);
        if (saved) await app.ListsChangedAsync((boxes[2].Text ?? "") != bypassOriginal);
    }
}
