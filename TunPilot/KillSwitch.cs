using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace TunPilot;

/// <summary>
/// Kill-switch на Windows Filtering Platform (как у WireGuard/Mullvad).
/// Блокирует весь трафик, кроме: трафика через TUN, самих ядер (sing-box, xray) и TunPilot,
/// локальной сети и исключений «мимо TUN». Не зависит от того, включён ли брандмауэр Windows.
///
/// Фильтры постоянные: блокировка остаётся и после падения программы, и после перезагрузки Windows
/// (действует с самого старта системы, ещё до запуска TunPilot). Снимается только «Отключить», выходом,
/// пунктом «Снять блокировку» в трее или командой TunPilot.exe --killswitch-off.
/// Исключения меняются на лету, без перезапуска TUN.
/// </summary>
public static class KillSwitch
{
    // Фиксированные ключи: по ним можно снять фильтры даже после падения/перезапуска программы.
    static readonly Guid SubLayerKey = new("6b3f1e52-7d0a-4c1e-9a55-2f1d7c0e9b11");
    const string FilterKeyPrefix = "6b3f1e52-7d0a-4c1e-9a55-2f1d7c0e";   // + 4 hex: номер фильтра
    const int BaseSlots = 0x00, BypassSlots = 0x40, MaxSlots = 0x80;   // база: 16 фильтров; исключения: до 64 пачек по 200 подсетей

    static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    static readonly Guid LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    static readonly Guid LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    static readonly Guid CondRemoteAddr = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    static readonly Guid CondLocalAddr = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");

    static readonly (Guid Layer, bool V6)[] Layers =
        [(LayerConnectV4, false), (LayerConnectV6, true), (LayerAcceptV4, false), (LayerAcceptV6, true)];

    // Адрес интерфейса TUN (см. ConfigBuilder): трафик с этим исходным адресом идёт через VPN.
    static readonly string[] TunLocal = ["172.19.0.0/30", "fdfe:dcba:9876::/126"];
    static readonly string[] Lan =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "100.64.0.0/10", "224.0.0.0/4", "255.255.255.255/32",
        "::1/128", "fe80::/10", "fc00::/7", "ff00::/8",
    ];

    static readonly object Lock = new();

    /// <summary>Пояснение для подсказок в трее и настройках.</summary>
    public static string Help =>
        L("Kill-switch не пускает трафик в интернет мимо VPN.\n\n") +
        L("• Включается при подключении: интернет идёт только через TUN.\n") +
        L("• Если ядро или TunPilot упали — интернет блокируется.\n") +
        L("• Блокировка сохраняется после перезагрузки Windows (включите автозапуск и «Запоминать подключение», чтобы VPN поднимался сам).\n") +
        L("• Работают: локальная сеть и исключения «мимо TUN» (Dota, Steam).\n\n") +
        L("Снимается только вашим действием: «Отключить», «Выход», «Снять блокировку» в трее\n") +
        L("или командой TunPilot.exe --killswitch-off (от администратора).");

    /// <summary>Включена ли блокировка в системе прямо сейчас (в том числе оставшаяся после падения).</summary>
    public static bool IsActive
    {
        get
        {
            try { return WithEngine(e => FwpmSubLayerGetByKey0(e, SubLayerKey, out var p) == 0 && Free(p)); }
            catch { return false; }
        }
    }

    /// <summary>Включить (или обновить) блокировку. bypassCidrs — подсети «мимо TUN» (Dota/Steam и т. п.).</summary>
    public static void Enable(IEnumerable<string> bypassCidrs)
    {
        var apps = new[] { AppPaths.SingBoxExe, AppPaths.XrayExe, Environment.ProcessPath! }
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (Lock)
            WithEngine(e =>
            {
                Transaction(e, () =>
                {
                    DeleteSlots(e, 0, MaxSlots);
                    var sl = new FWPM_SUBLAYER0
                    {
                        subLayerKey = SubLayerKey,
                        displayData = new FWPM_DISPLAY_DATA0 { name = "TunPilot kill-switch" },
                        weight = 0xFFFF,
                        flags = FWPM_SUBLAYER_FLAG_PERSISTENT,
                    };
                    int r = FwpmSubLayerAdd0(e, ref sl, IntPtr.Zero);
                    if (r != 0 && (uint)r != FWP_E_ALREADY_EXISTS) Throw(r, "sublayer");

                    int slot = BaseSlots;
                    foreach (var (layer, v6) in Layers)
                    {
                        // 1) сами ядра и TunPilot — им можно всё (подключение к VPN-серверам, DNS, обновление подписок)
                        AddFilter(e, slot++, layer, permit: true, weight: 10, apps.Select(AppCond).ToList(), "apps");
                        // 2) трафик через TUN
                        AddFilter(e, slot++, layer, permit: true, weight: 10, AddrConds(CondLocalAddr, TunLocal, v6), "tun");
                        // 3) локальная сеть, loopback, multicast, DHCP
                        AddFilter(e, slot++, layer, permit: true, weight: 10, AddrConds(CondRemoteAddr, Lan, v6), "lan");
                        // 4) всё остальное — блок
                        AddFilter(e, slot++, layer, permit: false, weight: 0, [], "block");
                    }
                    SetBypassLocked(e, bypassCidrs);
                });
                return true;
            });
    }

    /// <summary>Обновить только исключения «мимо TUN» — на лету, если блокировка включена.</summary>
    public static void UpdateBypass(IEnumerable<string> bypassCidrs)
    {
        lock (Lock)
            WithEngine(e =>
            {
                if (FwpmSubLayerGetByKey0(e, SubLayerKey, out var p) != 0) return false;
                Free(p);
                Transaction(e, () => SetBypassLocked(e, bypassCidrs));
                return true;
            });
    }

    /// <summary>Снять блокировку полностью.</summary>
    public static void Disable()
    {
        lock (Lock)
            WithEngine(e =>
            {
                Transaction(e, () =>
                {
                    DeleteSlots(e, 0, MaxSlots);
                    int r = FwpmSubLayerDeleteByKey0(e, SubLayerKey);
                    if (r != 0 && (uint)r != FWP_E_SUBLAYER_NOT_FOUND) Throw(r, "sublayer delete");
                });
                return true;
            });
    }

    static void SetBypassLocked(IntPtr e, IEnumerable<string> cidrs)
    {
        DeleteSlots(e, BypassSlots, MaxSlots);
        var list = cidrs.Where(c => c.Contains('/')).Distinct().ToList();
        if (list.Count == 0) return;
        int slot = BypassSlots;
        foreach (var (layer, v6) in Layers)
        {
            var conds = AddrConds(CondRemoteAddr, list, v6);
            // условия одного поля объединяются через ИЛИ; режем на пачки, чтобы не упереться в лимиты
            foreach (var chunk in conds.Chunk(200))
                if (slot < MaxSlots) AddFilter(e, slot++, layer, permit: true, weight: 10, chunk.ToList(), "bypass");
        }
    }

    // ───────────────────────── условия ─────────────────────────

    /// <summary>Условие фильтра в «управляемом» виде; неуправляемая память выделяется при добавлении.</summary>
    sealed record Cond(Guid Field, uint Type, byte[] Data);

    static Cond AppCond(string path)
    {
        int r = FwpmGetAppIdFromFileName0(path, out var blobPtr);
        if (r != 0) Throw(r, "app id " + Path.GetFileName(path));
        try
        {
            var blob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(blobPtr);
            var data = new byte[blob.size];
            Marshal.Copy(blob.data, data, 0, data.Length);
            return new Cond(CondAppId, FWP_BYTE_BLOB_TYPE, data);
        }
        finally { FwpmFreeMemory0(ref blobPtr); }
    }

    static List<Cond> AddrConds(Guid field, IEnumerable<string> cidrs, bool v6)
    {
        var res = new List<Cond>();
        foreach (var c in cidrs)
        {
            var parts = c.Split('/');
            if (!IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var len)) continue;
            bool isV6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (isV6 != v6) continue;
            if (!v6)
            {
                var b = ip.GetAddressBytes();
                uint mask = len == 0 ? 0 : uint.MaxValue << (32 - len);
                uint addr = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) & mask;   // порядок хоста
                var data = new byte[8];
                BitConverter.GetBytes(addr).CopyTo(data, 0);
                BitConverter.GetBytes(mask).CopyTo(data, 4);
                res.Add(new Cond(field, FWP_V4_ADDR_MASK, data));
            }
            else
            {
                var data = new byte[17];
                ip.GetAddressBytes().CopyTo(data, 0);
                data[16] = (byte)len;
                res.Add(new Cond(field, FWP_V6_ADDR_MASK, data));
            }
        }
        return res;
    }

    static void AddFilter(IntPtr e, int slot, Guid layer, bool permit, byte weight, List<Cond> conds, string name)
    {
        // «Разрешить, если …» без единого условия разрешало бы всё — такой фильтр просто не ставим.
        if (permit && conds.Count == 0) return;
        var allocs = new List<IntPtr>();
        IntPtr Alloc(int size) { var p = Marshal.AllocHGlobal(size); allocs.Add(p); return p; }
        try
        {
            int condSize = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
            var arr = conds.Count > 0 ? Alloc(condSize * conds.Count) : IntPtr.Zero;
            for (int i = 0; i < conds.Count; i++)
            {
                var c = conds[i];
                var dataPtr = Alloc(c.Data.Length);
                Marshal.Copy(c.Data, 0, dataPtr, c.Data.Length);
                IntPtr value = dataPtr;
                if (c.Type == FWP_BYTE_BLOB_TYPE)
                {
                    value = Alloc(Marshal.SizeOf<FWP_BYTE_BLOB>());
                    Marshal.StructureToPtr(new FWP_BYTE_BLOB { size = (uint)c.Data.Length, data = dataPtr }, value, false);
                }
                var fc = new FWPM_FILTER_CONDITION0
                {
                    fieldKey = c.Field, matchType = FWP_MATCH_EQUAL,
                    conditionValue = new FWP_VALUE0 { type = c.Type, value = value },
                };
                Marshal.StructureToPtr(fc, arr + i * condSize, false);
            }
            var namePtr = Marshal.StringToHGlobalUni("TunPilot kill-switch: " + name);
            allocs.Add(namePtr);
            var f = new FWPM_FILTER0
            {
                filterKey = SlotKey(slot),
                flags = FWPM_FILTER_FLAG_PERSISTENT,
                displayData = new FWPM_DISPLAY_DATA0 { namePtr = namePtr },
                layerKey = layer,
                subLayerKey = SubLayerKey,
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = weight },
                numFilterConditions = (uint)conds.Count,
                filterCondition = arr,
                action = new FWPM_ACTION0 { type = permit ? FWP_ACTION_PERMIT : FWP_ACTION_BLOCK },
            };
            int r = FwpmFilterAdd0(e, ref f, IntPtr.Zero, out _);
            if (r != 0) Throw(r, "filter " + name);
        }
        finally { foreach (var p in allocs) Marshal.FreeHGlobal(p); }
    }

    /// <summary>Для самопроверки: размеры структур (x64 SDK: 200 / 40 / 72 / 16 / 16).</summary>
    internal static string Describe() =>
        $"filter={Marshal.SizeOf<FWPM_FILTER0>()} cond={Marshal.SizeOf<FWPM_FILTER_CONDITION0>()} " +
        $"sublayer={Marshal.SizeOf<FWPM_SUBLAYER0>()} value={Marshal.SizeOf<FWP_VALUE0>()} blob={Marshal.SizeOf<FWP_BYTE_BLOB>()} " +
        $"actionOffset={Marshal.OffsetOf<FWPM_FILTER0>(nameof(FWPM_FILTER0.action))} " +
        $"ctxOffset={Marshal.OffsetOf<FWPM_FILTER0>(nameof(FWPM_FILTER0.rawContext))} active={IsActive}";

    static Guid SlotKey(int slot) => new($"{FilterKeyPrefix}{slot:x4}");

    static void DeleteSlots(IntPtr e, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            var k = SlotKey(i);
            FwpmFilterDeleteByKey0(e, ref k);   // «не найден» — нормально
        }
    }

    // ───────────────────────── движок ─────────────────────────

    static T WithEngine<T>(Func<IntPtr, T> body)
    {
        int r = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out var engine);
        if (r != 0) Throw(r, "engine");
        try { return body(engine); }
        finally { FwpmEngineClose0(engine); }
    }

    static void Transaction(IntPtr e, Action body)
    {
        int r = FwpmTransactionBegin0(e, 0);
        if (r != 0) Throw(r, "transaction");
        try { body(); }
        catch { FwpmTransactionAbort0(e); throw; }
        r = FwpmTransactionCommit0(e);
        if (r != 0) Throw(r, "commit");
    }

    static bool Free(IntPtr p) { FwpmFreeMemory0(ref p); return true; }

    static void Throw(int code, string what) =>
        throw new Win32Exception(code, Lf($"Kill-switch ({what}): ошибка WFP 0x{code:X8}"));

    // ───────────────────────── P/Invoke ─────────────────────────

    const uint RPC_C_AUTHN_WINNT = 10;
    const uint FWP_UINT8 = 1, FWP_BYTE_BLOB_TYPE = 12, FWP_V4_ADDR_MASK = 0x100, FWP_V6_ADDR_MASK = 0x101;
    const uint FWP_MATCH_EQUAL = 0;
    const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x1, FWPM_FILTER_FLAG_PERSISTENT = 0x1;
    const uint FWP_ACTION_BLOCK = 0x1001, FWP_ACTION_PERMIT = 0x1002;
    const uint FWP_E_ALREADY_EXISTS = 0x80320009, FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;

    [StructLayout(LayoutKind.Sequential)]
    struct FWP_BYTE_BLOB { public uint size; public IntPtr data; }

    /// <summary>FWP_VALUE0 / FWP_CONDITION_VALUE0: тип + 8-байтовое объединение (число или указатель).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FWP_VALUE0 { public uint type; public IntPtr value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FWPM_DISPLAY_DATA0
    {
        public IntPtr namePtr, description;
        public string name { set => namePtr = Marshal.StringToHGlobalUni(value); }   // только для подслоя (живёт до выхода)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FWPM_FILTER_CONDITION0 { public Guid fieldKey; public uint matchType; public FWP_VALUE0 conditionValue; }

    [StructLayout(LayoutKind.Sequential)]
    struct FWPM_ACTION0 { public uint type; public Guid filterType; }

    [StructLayout(LayoutKind.Sequential)]
    struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FWPM_ACTION0 action;
        public ulong rawContext, rawContextHigh;   // union { UINT64; GUID }
        public IntPtr reserved;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    static extern int FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern int FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern int FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] static extern int FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern int FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern int FwpmSubLayerAdd0(IntPtr engine, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);
    [DllImport("fwpuclnt.dll")] static extern int FwpmSubLayerDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] static extern int FwpmSubLayerGetByKey0(IntPtr engine, in Guid key, out IntPtr subLayer);
    [DllImport("fwpuclnt.dll")] static extern int FwpmFilterAdd0(IntPtr engine, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern int FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern int FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr p);
}
