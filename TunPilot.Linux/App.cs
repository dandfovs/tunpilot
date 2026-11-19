using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TunPilot.LinuxApp;

/// <summary>Приложение Avalonia: тема и запуск оболочки (трей + главное окно + общий контроллер).</summary>
sealed class App : Application
{
    public override void Initialize()
    {
        // тема — до стилей: стили берут цвета из палитры Ui
        var s = Settings.Load();
        bool light = s.Theme switch
        {
            "light" => true,
            "system" => PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light,
            _ => false,
        };
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        Ui.ApplyPalette(light);

        // акцент Fluent (переключатели, флажки, вкладки) — зелёный, как в версии для Windows
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Ui.Accent };
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Avalonia.Media.Color.FromRgb(46, 160, 82) };
        Styles.Add(fluent);
        Styles.Add(Ui.AppStyles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            new LinuxShell(desktop, desktop.Args ?? []).Start();
        base.OnFrameworkInitializationCompleted();
    }
}
