using System.Reflection;
using System.Text.Json;

namespace TunPilot;

/// <summary>Сведения о программе и проверка новой версии на GitHub (по кнопке, без фоновых запросов).</summary>
public static class Updater
{
    public const string Repo = "dandfovs/tunpilot";
    public static string PageUrl => $"https://github.com/{Repo}";
    public static string ReleasesUrl => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("TunPilot/" + VersionText);   // GitHub API требует User-Agent
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Version
    {
        get { var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0); return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
    }

    public static string VersionText => Version.ToString(3);

    /// <summary>Последний выпуск на GitHub: (версия, ссылка) или null, если выпусков ещё нет.</summary>
    public static async Task<(string Tag, string Url)?> LatestAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("tag_name").GetString() ?? "", doc.RootElement.GetProperty("html_url").GetString() ?? ReleasesUrl);
    }

    public static bool IsNewer(string tag) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var v) && new Version(v.Major, v.Minor, Math.Max(0, v.Build)) > Version;

    /// <summary>Первая строка «core version» (sing-box / Xray) — для раздела «О программе».</summary>
    public static async Task<string> CoreVersionAsync(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var line = (await p.StandardOutput.ReadLineAsync()) ?? "";
            await p.WaitForExitAsync();
            // «sing-box version 1.14.4» / «Xray 26.11.9 (Xray, Penetrates Everything.) …»
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.FirstOrDefault(x => x.Length > 0 && char.IsDigit(x[0])) ?? "?";
        }
        catch { return "?"; }
    }
}
