using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace TunPilot.LinuxApp;

/// <summary>Простые диалоги в стиле программы: сообщение, вопрос «да/нет», ввод текста.</summary>
static class Dialogs
{
    static Window? Owner => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
        ?.Windows.FirstOrDefault(w => w.IsActive) ?? LinuxShell.Instance?.Window;

    internal static Window Make(string title, Control content, double width = 520) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.B(Ui.Bg),
        Icon = LinuxShell.AppIcon, Content = new Border { Padding = new Thickness(18), Child = content },
    };

    internal static async Task ShowAsync(Window w)
    {
        if (Owner is { IsVisible: true } o) await w.ShowDialog(o);
        else
        {
            var done = new TaskCompletionSource();
            w.Closed += (_, _) => done.TrySetResult();
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            await done.Task;
        }
    }

    public static async Task MessageAsync(string title, string text, bool error = false)
    {
        var ok = Ui.Btn("OK", () => { }, primary: true);
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B(error ? Ui.Danger : Ui.Text) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok);
        body.Children.Add(row);
        var w = Make(title, body);
        ok.Click += (_, _) => w.Close();
        await ShowAsync(w);
    }

    public static async Task<bool> ConfirmAsync(string text, string title = "TunPilot")
    {
        bool result = false;
        var yes = Ui.Btn(L("Да"), () => { }, primary: true);
        var no = Ui.Btn(L("Нет"), () => { });
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B(Ui.Text) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(yes); row.Children.Add(no);
        body.Children.Add(row);
        var w = Make(title, body);
        yes.Click += (_, _) => { result = true; w.Close(); };
        no.Click += (_, _) => w.Close();
        await ShowAsync(w);
        return result;
    }

    /// <summary>Ввод текста; null — отмена. withFile — кнопка «Файл…» подставляет содержимое файла.</summary>
    public static async Task<string?> PromptAsync(string label, string value, bool multiline = false, bool withFile = false)
    {
        string? result = null;
        var box = new TextBox
        {
            Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.NoWrap,
            Height = multiline ? 220 : double.NaN, FontFamily = multiline ? new FontFamily("monospace") : FontFamily.Default,
        };
        var ok = Ui.Btn("OK", () => { }, primary: true);
        var cancel = Ui.Btn(L("Отмена"), () => { });
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B(Ui.Text) });
        body.Children.Add(box);
        var row = new DockPanel { LastChildFill = false };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(ok); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var w = Make("TunPilot", body, multiline ? 640 : 520);
        if (withFile)
        {
            var file = Ui.BtnAsync(L("Файл…"), async () =>
            {
                var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) box.Text = await File.ReadAllTextAsync(path);
            });
            DockPanel.SetDock(file, Dock.Left);
            row.Children.Add(file);
        }
        body.Children.Add(row);
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => box.Focus();
        await ShowAsync(w);
        return result;
    }
}
