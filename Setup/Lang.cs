using System.Globalization;

namespace TunPilotSetup
{
    /// <summary>Язык установщика: русский, если Windows на русском, иначе английский.</summary>
    static class Lang
    {
        public static readonly bool En =
            !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", System.StringComparison.OrdinalIgnoreCase);

        public static string T(string ru, string en) => En ? en : ru;
    }
}
