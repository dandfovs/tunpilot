namespace TunPilot;

/// <summary>
/// То, что ядру программы нужно знать о состоянии приложения. Реализует оболочка конкретной платформы
/// (на Windows — TrayApp), чтобы общая логика не зависела от интерфейса.
/// </summary>
public interface IAppState
{
    CoreManager Core { get; }
    IReadOnlyList<ServerInfo> AllServers { get; }
    GroupStore Groups { get; }
}
