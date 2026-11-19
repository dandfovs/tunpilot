using System.Text.Json;

namespace TunPilot;

/// <summary>
/// Профиль маршрутизации: свои списки (напрямую / через VPN / мимо TUN), geo-наборы
/// и «всё остальное». Переключение списков — без перезапуска; geo и «всё остальное» меняют конфиг ядра.
/// </summary>
public sealed class RoutingProfile
{
    public string Name { get; set; } = "";
    /// <summary>"proxy" или "direct".</summary>
    public string DefaultOutbound { get; set; } = "proxy";
    public List<string> GeoDirect { get; set; } = [];
    public List<string> GeoProxy { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public string Dir { get; set; } = "";
    public string DirectTxt => Path.Combine(Dir, "direct.txt");
    public string ProxyTxt => Path.Combine(Dir, "proxy.txt");
    public string BypassTxt => Path.Combine(Dir, "bypass.txt");
    string MetaPath => Path.Combine(Dir, "profile.json");

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Отличается ли от другого профиля тем, что требует перезапуска ядра.</summary>
    /// <summary>Наборы geoip/geosite меняются на лету; перезапуск нужен только при смене «всего остального».</summary>
    public bool NeedsRestartComparedTo(RoutingProfile other) => DefaultOutbound != other.DefaultOutbound;

    public static RoutingProfile? LoadFrom(string dir)
    {
        try
        {
            var meta = Path.Combine(dir, "profile.json");
            if (!File.Exists(meta)) return null;
            var p = JsonSerializer.Deserialize<RoutingProfile>(File.ReadAllText(meta))!;
            p.Dir = dir;
            return p;
        }
        catch { return null; }
    }
}

public static class Profiles
{
    /// <summary>Активный профиль — с ним работают RuleLists и ConfigBuilder.</summary>
    public static RoutingProfile Current { get; private set; } = null!;

    public static List<RoutingProfile> All() =>
        Directory.GetDirectories(AppPaths.Profiles)
            .Select(RoutingProfile.LoadFrom).Where(p => p != null).Select(p => p!)
            .OrderBy(p => p.Name == "Default" ? 0 : 1).ThenBy(p => p.Name).ToList();

    public static RoutingProfile? Find(string name) => All().FirstOrDefault(p => p.Name == name);

    /// <summary>Создаёт профиль (или перезаписывает метаданные существующего с тем же именем).</summary>
    public static RoutingProfile Create(string name)
    {
        var existing = Find(name);
        if (existing != null) return existing;
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dir = Path.Combine(AppPaths.Profiles, safe.Length > 0 ? safe : "profile");
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(AppPaths.Profiles, $"{safe} ({i})");
        var p = new RoutingProfile { Name = name, Dir = dir };
        p.Save();
        RuleLists.EnsureDefaults(p);
        return p;
    }

    public static RoutingProfile Clone(RoutingProfile src, string name)
    {
        var p = Create(name);
        p.DefaultOutbound = src.DefaultOutbound;
        p.GeoDirect = [.. src.GeoDirect];
        p.GeoProxy = [.. src.GeoProxy];
        p.Save();
        foreach (var f in new[] { "direct.txt", "proxy.txt", "bypass.txt" })
            if (File.Exists(Path.Combine(src.Dir, f))) File.Copy(Path.Combine(src.Dir, f), Path.Combine(p.Dir, f), true);
        return p;
    }

    public static void Delete(RoutingProfile p)
    {
        try { Directory.Delete(p.Dir, true); } catch { }
    }

    /// <summary>Загружает активный профиль; при первом запуске переносит старые списки.</summary>
    public static void Init(Settings s)
    {
        if (All().Count == 0) Migrate(s);
        Current = Find(s.ActiveProfile) ?? All().First();
        if (s.ActiveProfile != Current.Name) { s.ActiveProfile = Current.Name; s.Save(); }
        RuleLists.EnsureDefaults(Current);
    }

    public static void SetCurrent(RoutingProfile p, Settings s)
    {
        Current = p;
        s.ActiveProfile = p.Name;
        s.Save();
        RuleLists.EnsureDefaults(p);
    }

    static void Migrate(Settings s)
    {
        // «Default»: всё через VPN, без исключений.
        var def = Create("Default");
        File.WriteAllText(def.DirectTxt, L("# Напрямую, без VPN\n"));

        // Старые списки (папка lists) — в отдельный профиль.
        var legacyDirect = Path.Combine(AppPaths.Lists, "direct.txt");
        if (File.Exists(legacyDirect))
        {
            bool bypassRussia = s.GeoDirect is { Count: > 0 };
            var p = Create(bypassRussia ? "Bypass Russia" : L("Мой профиль"));
            p.DefaultOutbound = s.DefaultOutbound ?? "proxy";
            p.GeoDirect = s.GeoDirect ?? [];
            p.GeoProxy = s.GeoProxy ?? [];
            p.Save();
            foreach (var f in new[] { "direct.txt", "proxy.txt", "bypass.txt" })
            {
                var src = Path.Combine(AppPaths.Lists, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(p.Dir, f), true);
            }
            s.ActiveProfile = p.Name;
        }
        else s.ActiveProfile = def.Name;
        s.DefaultOutbound = null; s.GeoDirect = null; s.GeoProxy = null;
        s.Save();
    }
}
