namespace TunPilot;

/// <summary>Kill-switch Windows (WFP) для общей логики.</summary>
sealed class WinKillSwitch : IKillSwitch
{
    public bool IsActive => KillSwitch.IsActive;
    public void Enable(IEnumerable<string> bypassCidrs) => KillSwitch.Enable(bypassCidrs);
    public void UpdateBypass(IEnumerable<string> bypassCidrs) => KillSwitch.UpdateBypass(bypassCidrs);
    public void Disable() => KillSwitch.Disable();
    public string RecoveryHint => L("\n\nПерезагрузка Windows снимет её точно.");
}

/// <summary>Маршруты «мимо TUN» Windows (route.exe) для общей логики.</summary>
sealed class WinBypassRoutes : IBypassRoutes
{
    public Task<string> ApplyAsync(IEnumerable<string> cidrs) => BypassRoutes.ApplyAsync(cidrs);
    public Task ClearAsync() => BypassRoutes.ClearAsync();
    public Task CleanupLeftoversAsync() => BypassRoutes.CleanupLeftoversAsync();
    public void ResetGateway() => BypassRoutes.ResetGateway();
}

/// <summary>Автозапуск Windows (Планировщик заданий) для общей логики.</summary>
sealed class WinAutostart : IAutostart
{
    public bool IsEnabled() => Autostart.IsEnabled();
    public string? Enable() => Autostart.Enable();
    public void Disable() => Autostart.Disable();
    public bool PointsElsewhere() => Autostart.PointsElsewhere();
}
