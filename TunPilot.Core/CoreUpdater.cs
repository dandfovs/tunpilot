using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TunPilot;

/// <summary>
/// Обновление ядер (sing-box, Xray) из их официальных выпусков на GitHub.
/// Порядок: скачать → проверить (контрольная сумма, если есть; новое ядро запускается) → заменить файлы,
/// оставив копию старых → TrayApp проверяет конфиг новым ядром и при ошибке откатывает.
/// </summary>
public static class CoreUpdater
{
    public sealed record Core(string Name, string Repo, string Dir, string Exe, Func<string, string, bool> IsAsset);

    public static readonly Core[] Cores =
    [
        new("sing-box", "SagerNet/sing-box", Path.GetDirectoryName(AppPaths.SingBoxExe)!, AppPaths.SingBoxExe,
            (name, ver) => name == $"sing-box-{ver}-windows-amd64.zip"),
        new("Xray", "XTLS/Xray-core", AppPaths.XrayDir, AppPaths.XrayExe,
            (name, _) => name == "Xray-windows-64.zip"),
    ];

    /// <summary>Что найдено на GitHub для одного ядра.</summary>
    public sealed record Info(Core Core, string Current, string Latest, string AssetUrl, string? DigestUrl)
    {
        public bool HasUpdate => System.Version.TryParse(Latest, out var l) && System.Version.TryParse(Current, out var c) ? l > c : Latest != Current;
    }

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("TunPilot/" + Updater.VersionText);
        return h;
    }

    static string BackupDir(Core c) => c.Dir + ".old";

    /// <summary>Узнать текущие и последние версии обоих ядер.</summary>
    public static async Task<List<Info>> CheckAsync()
    {
        var res = new List<Info>();
        foreach (var core in Cores)
        {
            var current = await Updater.CoreVersionAsync(core.Exe);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{core.Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var latest = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            string? asset = null, digest = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (core.IsAsset(name, latest)) asset = url;
                else if (name.EndsWith(".dgst") && core.IsAsset(name[..^5], latest)) digest = url;
            }
            if (asset == null) throw new InvalidOperationException(Lf($"В выпуске {core.Name} {latest} нет сборки для Windows x64."));
            res.Add(new Info(core, current, latest, asset, digest));
        }
        return res;
    }

    /// <summary>
    /// Скачать и подготовить новую версию во временной папке (ядра в это время могут работать).
    /// Возвращает папку с готовыми файлами (Root) и временную папку целиком (Temp) — её потом удаляет Cleanup.
    /// </summary>
    public static async Task<(string Root, string Temp)> DownloadAsync(Info info, Action<string> progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "TunPilot-core-" + info.Core.Name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var zipPath = Path.Combine(tmp, "core.zip");

        progress(Lf($"{info.Core.Name}: скачиваю {info.Latest}…"));
        using (var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            int n; int lastPct = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                int pct = total > 0 ? (int)(100 * done / total) : -1;
                if (pct != lastPct && pct % 10 == 0) { lastPct = pct; progress(Lf($"{info.Core.Name}: скачиваю {info.Latest}… {pct}%")); }
            }
        }

        // контрольная сумма (у Xray публикуется файл .dgst со строкой «SHA2-256= …»)
        if (info.DigestUrl != null)
        {
            progress(Lf($"{info.Core.Name}: проверяю контрольную сумму…"));
            var dgst = await Http.GetStringAsync(info.DigestUrl);
            var expected = dgst.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("SHA2-256", StringComparison.OrdinalIgnoreCase) || l.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase))
                ?.Split('=').Last().Trim();
            if (!string.IsNullOrEmpty(expected))
            {
                await using var fs = File.OpenRead(zipPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Lf($"{info.Core.Name}: контрольная сумма не совпала — файл повреждён или подменён. Обновление отменено."));
            }
        }

        progress(Lf($"{info.Core.Name}: распаковываю…"));
        var extract = Path.Combine(tmp, "x");
        ZipFile.ExtractToDirectory(zipPath, extract);
        // sing-box лежит во вложенной папке «sing-box-<версия>-windows-amd64» — берём папку, где exe
        var exeName = Path.GetFileName(info.Core.Exe);
        var exe = Directory.GetFiles(extract, exeName, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException(Lf($"В архиве {info.Core.Name} нет {exeName}."));
        var root = Path.GetDirectoryName(exe)!;

        // новое ядро должно запускаться и сообщать ожидаемую версию
        var v = await Updater.CoreVersionAsync(exe);
        if (v == "?") throw new InvalidOperationException(Lf($"Новый {info.Core.Name} не запускается на этом компьютере."));
        return (root, tmp);
    }

    /// <summary>Заменить файлы ядра новыми (ядро должно быть остановлено). Старые файлы — в папку «.old».</summary>
    public static void Replace(Info info, string newRoot)
    {
        var dir = info.Core.Dir;
        var backup = BackupDir(info.Core);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        CopyDir(dir, backup);
        foreach (var f in Directory.GetFiles(newRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(newRoot, f);
            var target = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyWithRetry(f, target);
        }
    }

    /// <summary>Удалить временную папку загрузки (только нашу: имя начинается с «TunPilot-core-»).</summary>
    public static void Cleanup(string temp)
    {
        try
        {
            if (Path.GetFileName(temp).StartsWith("TunPilot-core-", StringComparison.Ordinal) && Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
        catch { }
    }

    /// <summary>Вернуть прежнюю версию из «.old».</summary>
    public static bool Rollback(Core core)
    {
        var backup = BackupDir(core);
        if (!Directory.Exists(backup)) return false;
        foreach (var f in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            CopyWithRetry(f, Path.Combine(core.Dir, Path.GetRelativePath(backup, f)));
        return true;
    }

    /// <summary>Убрать копию старой версии после успешной проверки.</summary>
    public static void Commit(Core core) { try { Directory.Delete(BackupDir(core), true); } catch { } }

    static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, true); return; }
            catch (IOException) when (i < 15) { Thread.Sleep(300); }   // процесс ядра ещё освобождает файл
        }
    }

    /// <summary>Остановить все процессы ядер из папки программы (рабочее ядро уже остановлено, это — тестовые).</summary>
    public static void KillStrayCores()
    {
        foreach (var core in Cores)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(core.Exe)))
                try
                {
                    if (string.Equals(p.MainModule?.FileName, core.Exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
    }
}
