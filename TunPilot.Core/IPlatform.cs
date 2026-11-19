namespace TunPilot;

public enum NotifyKind { Info, Warning, Error }

/// <summary>
/// Всё, что у каждой ОС своё. Логика приложения (<see cref="AppController"/>) пользуется только этим интерфейсом,
/// оболочка платформы (Windows — WinForms, Linux — Avalonia) его реализует.
/// </summary>
public interface IPlatform
{
    // ── разговор с пользователем (вызывается в UI-потоке) ──
    void Notify(string title, string text, NotifyKind kind);
    void ShowError(string text);
    void ShowInfo(string title, string text);
    /// <summary>Вопрос «да/нет». Асинхронный: в Avalonia диалоги не блокируют поток.</summary>
    Task<bool> ConfirmAsync(string text);

    // ── системное ──
    IKillSwitch KillSwitch { get; }
    IBypassRoutes Bypass { get; }
    IAutostart Autostart { get; }
}

/// <summary>Блокировка трафика мимо VPN (Windows — WFP, Linux — nftables).</summary>
public interface IKillSwitch
{
    bool IsActive { get; }
    void Enable(IEnumerable<string> bypassCidrs);
    void UpdateBypass(IEnumerable<string> bypassCidrs);
    void Disable();
    /// <summary>Подсказка, если снять блокировку не удалось (например, «перезагрузка снимет её»).</summary>
    string RecoveryHint { get; }
}

/// <summary>Маршруты «мимо TUN» через физический шлюз (Windows — route.exe, Linux — ip route).</summary>
public interface IBypassRoutes
{
    Task<string> ApplyAsync(IEnumerable<string> cidrs);
    Task ClearAsync();
    Task CleanupLeftoversAsync();
    void ResetGateway();
}

/// <summary>Запуск вместе с системой (Windows — Планировщик, Linux — XDG autostart).</summary>
public interface IAutostart
{
    bool IsEnabled();
    /// <summary>null — успешно, иначе текст ошибки.</summary>
    string? Enable();
    void Disable();
    /// <summary>Автозапуск указывает на другую копию программы (её перенесли) — пора обновить путь.</summary>
    bool PointsElsewhere();
}
