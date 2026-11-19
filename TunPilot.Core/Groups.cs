using System.Text.Json;

namespace TunPilot;

/// <summary>Группа серверов: подписка (по ссылке) или «ручная» (вставленные ключи).</summary>
public sealed class Group
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"subscription" или "manual".</summary>
    public string Type { get; set; } = "subscription";
    public string Url { get; set; } = "";
    public bool SkipAutoUpdate { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public SubscriptionInfo? Info { get; set; }
    /// <summary>Скрытые пользователем серверы подписки (до следующего обновления).</summary>
    public List<string> Excluded { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public bool IsSubscription => Type == "subscription";
    /// <summary>Тег «Авто» этой группы в ядре.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string AutoTag => "auto-" + Id;
    [System.Text.Json.Serialization.JsonIgnore] public string AutoKey => "auto:" + Id;

    string BodyPath => Path.Combine(AppPaths.GroupsDir, Id + ".txt");

    /// <summary>Тело подписки (кэш) или список ключей ручной группы. Хранится в отдельном файле, не в groups.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Body
    {
        get => File.Exists(BodyPath) ? File.ReadAllText(BodyPath) : "";
        set => File.WriteAllText(BodyPath, value);
    }

    public void DeleteFiles() { try { File.Delete(BodyPath); } catch { } }

    public List<ServerInfo> ParseServers() =>
        Subscription.Parse(Body, Id).Where(s => !Excluded.Contains(s.Name)).ToList();

    /// <summary>Интервал автообновления в минутах (0 — не обновлять).</summary>
    public int UpdateIntervalMinutes(Settings s)
    {
        if (!IsSubscription || SkipAutoUpdate) return 0;
        if (s.SubIntervalMode == "provider" && Info?.UpdateIntervalHours is int h and > 0) return h * 60;
        return Math.Max(30, s.SubIntervalMinutes);
    }
}

public sealed class GroupStore
{
    public List<Group> Groups { get; set; } = [];

    static string FilePath => Path.Combine(AppPaths.Data, "groups.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static GroupStore Load(Settings settings)
    {
        GroupStore store;
        try
        {
            store = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<GroupStore>(File.ReadAllText(FilePath)) ?? new GroupStore()
                : MigrateLegacy(settings);
        }
        catch { store = new GroupStore(); }
        store.Save();
        return store;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));

    public Group? Find(string id) => Groups.FirstOrDefault(g => g.Id == id);

    public Group Add(string name, string type, string url = "")
    {
        var g = new Group { Id = NewId(), Name = name, Type = type, Url = url };
        Groups.Add(g);
        Save();
        return g;
    }

    public void Remove(Group g) { Groups.Remove(g); g.DeleteFiles(); Save(); }

    /// <summary>Короткий ASCII-идентификатор: он же префикс тегов серверов в ядрах.</summary>
    string NewId()
    {
        string id;
        do id = "g" + Random.Shared.Next(0x1000, 0xFFFF).ToString("x");
        while (Groups.Any(g => g.Id == id));
        return id;
    }

    /// <summary>Перенос из версии с одной подпиской (settings.SubscriptionUrl + subscription.txt).</summary>
    static GroupStore MigrateLegacy(Settings s)
    {
        var store = new GroupStore();
        if (!string.IsNullOrWhiteSpace(s.SubscriptionUrl))
        {
            var info = SubscriptionInfo.Load();
            var g = new Group
            {
                Id = "g1000", Name = L("Подписка"), Type = "subscription", Url = s.SubscriptionUrl!,
                UpdatedUtc = s.SubscriptionUpdatedUtc, Info = info,
            };
            var legacy = Path.Combine(AppPaths.Data, "subscription.txt");
            if (File.Exists(legacy)) g.Body = File.ReadAllText(legacy);
            store.Groups.Add(g);
            s.ActiveGroupId = g.Id;
            s.SelectedServer = s.SelectedServer is "" or "auto" ? g.AutoKey : $"{g.Id}/{s.SelectedServer}";
            s.SubscriptionUrl = null;
            s.Save();
        }
        return store;
    }
}
