using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KubDHCP.App;

internal static class AdapterSettings
{
    public static void Open(nint owner, string adapterId)
    {
        var id = Guid.Parse(adapterId);
        var info = new ShellExecuteInfo
        {
            Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x0000000C | 0x00000400,
            Window = owner, Verb = "properties",
            File = $"::{{7007ACC7-3202-11D1-AAD2-00805FC1270E}}\\::{id:B}", Show = 1
        };
        if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    // Invoke the native properties verb for the adapter's Shell namespace item.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint Size, Mask;
        public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public nint Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor, Process;
    }
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}
