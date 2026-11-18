namespace TunPilot;

/// <summary>
/// Перевод интерфейса. Строки в коде остаются русскими; L("…") и Lf($"…{x}…") при английском языке
/// берут перевод из Strings.En (у шаблонов с подстановками ключ — текст с {0}, {1}…).
/// Нет перевода — показывается русский текст, программа не ломается.
/// </summary>
public static class Loc
{
    /// <summary>Английский интерфейс (выставляется при запуске из настроек).</summary>
    public static bool En { get; set; }

    public static string L(string ru) => En && Strings.En.TryGetValue(ru, out var en) ? en : ru;

    public static string Lf(FormattableString fs)
    {
        var format = fs.Format;
        if (En && Strings.En.TryGetValue(format, out var en)) format = en;
        try { return string.Format(format, fs.GetArguments()); }
        catch (FormatException) { return fs.ToString(); }   // испорченный перевод не должен ронять программу
    }

    /// <summary>Язык из настройки: "ru", "en" или "auto" (по языку Windows).</summary>
    public static void Apply(string setting) =>
        En = setting switch
        {
            "en" => true,
            "ru" => false,
            _ => !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase),
        };
}
