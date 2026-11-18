using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TunPilot;

/// <summary>
/// Бесплатный Cloudflare WARP: регистрируем устройство через API клиента WARP и получаем
/// параметры WireGuard. Результат — ссылка wireguard://…, работает через Xray.
/// </summary>
public static class Warp
{
    const string Api = "https://api.cloudflareclient.com/v0a2158/reg";

    public static async Task<string> RegisterAsync(TrayApp app)
    {
        var (priv, pub) = await GenerateKeyPairAsync();

        // Если подключены — идём через текущий сервер (API Cloudflare бывает заблокирован напрямую).
        using var http = app.Core.ProxiedClient(app.SelectedTag) ?? new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("okhttp/3.12.1");
        http.DefaultRequestHeaders.Add("CF-Client-Version", "a-6.30-3596");

        var body = new JsonObject
        {
            ["key"] = pub, ["install_id"] = "", ["fcm_token"] = "", ["warp_enabled"] = true,
            ["tos"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["type"] = "Android", ["locale"] = "en_US",
        };
        using var resp = await http.PostAsync(Api, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Lf($"Cloudflare ответил {(int)resp.StatusCode}: {text[..Math.Min(200, text.Length)]}"));

        var cfg = JsonNode.Parse(text)!["config"]!;
        var peer = cfg["peers"]![0]!;
        var peerKey = peer["public_key"]!.GetValue<string>();
        var v4 = cfg["interface"]!["addresses"]!["v4"]!.GetValue<string>();
        var v6 = cfg["interface"]!["addresses"]!["v6"]!.GetValue<string>();
        var clientId = cfg["client_id"]?.GetValue<string>() ?? "";
        var reserved = clientId.Length > 0 ? string.Join(",", Convert.FromBase64String(clientId).Take(3)) : "0,0,0";

        // Стандартный эндпоинт WARP; порт 2408 обычно доступен лучше 443/500.
        return $"wireguard://{Uri.EscapeDataString(priv)}@engage.cloudflareclient.com:2408" +
               $"?publickey={Uri.EscapeDataString(peerKey)}&address={Uri.EscapeDataString($"{v4}/32,{v6}/128")}" +
               $"&reserved={reserved}&mtu=1280#{Uri.EscapeDataString("Cloudflare WARP")}";
    }

    /// <summary>Ключи WireGuard (X25519) генерирует sing-box: в .NET нет встроенного X25519.</summary>
    static async Task<(string Priv, string Pub)> GenerateKeyPairAsync()
    {
        var psi = new ProcessStartInfo(AppPaths.SingBoxExe, "generate wg-keypair")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        string? priv = null, pub = null;
        foreach (var line in output.Split('\n'))
        {
            var kv = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) priv = kv[1];
            if (kv[0].Equals("PublicKey", StringComparison.OrdinalIgnoreCase)) pub = kv[1];
        }
        if (priv == null || pub == null) throw new InvalidOperationException(L("sing-box не сгенерировал ключи WireGuard."));
        return (priv, pub);
    }
}
