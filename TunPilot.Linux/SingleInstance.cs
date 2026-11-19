using System.Net.Sockets;

namespace TunPilot.LinuxApp;

/// <summary>
/// Одна копия программы на пользователя. Первая слушает Unix-сокет; вторая подключается к нему,
/// просит показать окно и выходит (именованные события Windows на Linux недоступны).
/// </summary>
static class SingleInstance
{
    static Socket? _listener;
    public static event Action? ShowRequested;

    static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : Path.GetTempPath(),
        $"tunpilot-{Environment.UserName}.sock");

    /// <summary>true — мы первая копия; false — копия уже работает (ей отправлен сигнал «показать окно»).</summary>
    public static bool TryAcquire(bool quiet)
    {
        var ep = new UnixDomainSocketEndPoint(SocketPath);
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(ep);
            if (!quiet) probe.Send("show"u8.ToArray());
            return false;
        }
        catch (SocketException) { /* никто не слушает — мы первые (или остался старый файл сокета) */ }

        try { File.Delete(SocketPath); } catch { }
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(ep);
        _listener.Listen(4);
        _ = Task.Run(AcceptLoop);
        return true;
    }

    static async Task AcceptLoop()
    {
        while (_listener != null)
        {
            try
            {
                using var s = await _listener.AcceptAsync();
                var buf = new byte[16];
                int n = await s.ReceiveAsync(buf);
                if (n > 0) ShowRequested?.Invoke();
            }
            catch { await Task.Delay(200); }
        }
    }

    public static void Release()
    {
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { File.Delete(SocketPath); } catch { }
    }
}
