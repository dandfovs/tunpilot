using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace TunPilot;

/// <summary>
/// «Личность» программы для Windows: ID приложения (AppUserModelID) и ярлык в меню «Пуск» с тем же ID.
/// Без этого Windows не знает, чья иконка нужна в шапке уведомлений, и рисует стандартную серую галочку.
/// Заодно TunPilot находится поиском в «Пуске».
/// </summary>
static class AppIdentity
{
    public const string AppId = "TunPilot.VpnClient";

    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TunPilot.lnk");

    /// <summary>Вызывать в самом начале Main — до создания окон и иконки в трее.</summary>
    public static void Apply()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }
        try { EnsureShortcut(Environment.ProcessPath!); } catch { /* без ярлыка просто останется стандартный значок */ }
    }

    static void EnsureShortcut(string exe)
    {
        var link = (IShellLinkW)new CShellLink();
        // уже есть и указывает сюда же — ничего не делаем
        if (File.Exists(ShortcutPath))
        {
            ((IPersistFile)link).Load(ShortcutPath, 0);
            var sb = new System.Text.StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            if (string.Equals(sb.ToString(), exe, StringComparison.OrdinalIgnoreCase) && ReadAppId(link) == AppId) return;
            link = (IShellLinkW)new CShellLink();
        }

        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetIconLocation(exe, 0);
        link.SetDescription(L("TunPilot — VPN-клиент"));

        var store = (IPropertyStore)link;
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);   // System.AppUserModel.ID
        var pv = new PropVariant { vt = 31 /*VT_LPWSTR*/, p = Marshal.StringToCoTaskMemUni(AppId) };
        try
        {
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(pv.p); }

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        ((IPersistFile)link).Save(ShortcutPath, true);
    }

    /// <summary>Для самопроверки: куда указывает ярлык и какой у него ID.</summary>
    internal static string Describe()
    {
        if (!File.Exists(ShortcutPath)) return "shortcut=MISSING";
        var link = (IShellLinkW)new CShellLink();
        ((IPersistFile)link).Load(ShortcutPath, 0);
        var sb = new System.Text.StringBuilder(1024);
        link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
        return $"shortcut={ShortcutPath} target={sb} appid={ReadAppId(link)}";
    }

    static string? ReadAppId(IShellLinkW link)
    {
        try
        {
            var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            ((IPropertyStore)link).GetValue(ref key, out var pv);
            return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
        }
        catch { return null; }
    }

    // ───────────────────────── COM ─────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }
}
