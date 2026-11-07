using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KubDHCP.App.Tui;

internal readonly record struct TerminalInput(ushort Key = 0, char Character = '\0', bool Shift = false, bool Control = false,
    int X = 0, int Y = 0, int Buttons = 0, int Wheel = 0, bool Mouse = false, bool Move = false);

/// <summary>Uses the caller's console, including a remote Windows pseudoconsole.</summary>
internal sealed class ConsoleTerminal : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct Coord { public short X, Y; public Coord(int x, int y) { X = (short)x; Y = (short)y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Region { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public Region Window; public Coord Maximum; }
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 4)] internal struct Cell { [FieldOffset(0)] public char Character; [FieldOffset(2)] public ushort Attributes; }
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)] internal struct Record
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort Key;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint Modifiers;
        [FieldOffset(4)] public Coord Position;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(12)] public uint MouseModifiers;
        [FieldOffset(16)] public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size; public int Visible; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedInfo
    {
        public uint Size; public Coord Buffer, Cursor; public ushort Attributes; public Region Window; public Coord Maximum;
        public ushort Popup; public int Fullscreen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[] Colors;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FontInfo
    {
        public uint Size, Number; public Coord Cell; public uint Family, Weight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Face;
    }
    private readonly nint input, output;
    private readonly uint oldMode;
    private readonly uint oldOutputMode;
    private readonly CursorInfo oldCursor;
    private readonly TerminalSequences sequences = new();
    private TuiCanvas? previous;
    private static ConsoleTerminal? active;
    private readonly ControlHandler handler;
    private bool allocated;
    private int quitRequested;
    public bool QuitRequested => Volatile.Read(ref quitRequested) != 0;
    public nint Window => GetConsoleWindow();
    public ConsoleTerminal(bool hidden = false)
    {
        // AllocConsole is exclusively a self-test fixture, never a --tui fallback.
        if (hidden) { Check(AllocConsole()); allocated = true; ShowWindow(GetConsoleWindow(), 0); }
        else if (!GetConsoleMode(GetStdHandle(-10), out _)) AttachConsole(uint.MaxValue);
        input = CreateFile("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
        output = CreateFile("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
        if (input == -1 || output == -1) throw new InvalidOperationException("--tui benötigt eine interaktive PowerShell-/Terminalsitzung. Bei SSH bitte ein Terminal anfordern (ssh -t). Es wird kein Fenster geöffnet.");
        Check(GetConsoleMode(input, out oldMode)); Check(GetConsoleMode(output, out oldOutputMode));
        Check(SetConsoleMode(input, 0x0080 | 0x0010 | 0x0008));
        Check(SetConsoleMode(output, oldOutputMode | 0x0004 | 0x0008));
        GetConsoleCursorInfo(output, out oldCursor);
        Write("\u001b[?1049h\u001b[?25l\u001b[?1002h\u001b[?1006h");
        active = this;
        var cursor = new CursorInfo { Size = 20, Visible = 0 }; SetConsoleCursorInfo(output, ref cursor);
        if (allocated)
        {
            var palette = new ExtendedInfo { Size = (uint)Marshal.SizeOf<ExtendedInfo>(), Colors = new uint[16] };
            if (GetConsoleScreenBufferInfoEx(output, ref palette))
            {
                var rgb = new uint[] { 0x202020, 0x383838, 0x0D1411, 0x404040, 0x7D2626, 0x774477, 0x857247, 0xDADADA, 0x2B2B2B, 0x39BDF8, 0x8BE3A9, 0x4FD4D0, 0xEF4444, 0xA0A0A0, 0xF2BE6D, 0xFFFFFF };
                palette.Colors = rgb.Select(c => (c & 255) << 16 | (c & 0xFF00) | c >> 16).ToArray();
                SetConsoleScreenBufferInfoEx(output, ref palette);
            }
            var font = new FontInfo { Size = (uint)Marshal.SizeOf<FontInfo>(), Cell = new Coord(0, 16), Family = 54, Weight = 400, Face = "Consolas" };
            SetCurrentConsoleFontEx(output, false, ref font);
            var maximum = GetLargestConsoleWindowSize(output);
            var size = new Coord(Math.Max(60, Math.Min(176, (int)maximum.X)), Math.Max(24, Math.Min(62, (int)maximum.Y)));
            // Growing the buffer before the window avoids ERROR_INVALID_PARAMETER.
            if (GetConsoleScreenBufferInfo(output, out var info))
            {
                SetConsoleScreenBufferSize(output, new Coord(Math.Max(size.X, info.Size.X), Math.Max(size.Y, info.Size.Y)));
                var region = new Region { Right = (short)(size.X - 1), Bottom = (short)(size.Y - 1) };
                SetConsoleWindowInfo(output, true, ref region); SetConsoleScreenBufferSize(output, size);
            }
        }
        handler = type => { if (type is 0 or 2 or 5 or 6) { Interlocked.Exchange(ref quitRequested, 1); return true; } return false; };
        SetConsoleCtrlHandler(handler, true);
    }
    public (int Width, int Height) Size
    {
        get { Check(GetConsoleScreenBufferInfo(output, out var info)); return (info.Window.Right - info.Window.Left + 1, info.Window.Bottom - info.Window.Top + 1); }
    }
    public IReadOnlyList<TerminalInput> Read(int timeout = 80)
    {
        if (WaitForSingleObject(input, (uint)timeout) != 0) return sequences.Decode([]);
        Check(GetNumberOfConsoleInputEvents(input, out var available));
        if (available == 0) return [];
        var records = new Record[Math.Min(64, available)]; Check(ReadConsoleInput(input, records, (uint)records.Length, out var count));
        GetConsoleScreenBufferInfo(output, out var info);
        var result = new List<TerminalInput>();
        foreach (var item in records.Take((int)count))
        {
            if (item.Type == 1 && item.Down != 0)
                for (var i = 0; i < Math.Max(1, (int)item.Repeat); i++) result.Add(new(item.Key, item.Character, (item.Modifiers & 0x10) != 0, (item.Modifiers & 0xC) != 0));
            if (item.Type == 2) result.Add(new(X: item.Position.X - info.Window.Left, Y: item.Position.Y - info.Window.Top,
                Buttons: (int)item.Buttons & 0xFFFF, Wheel: (item.Flags & 4) != 0 ? (short)(item.Buttons >> 16) / 120 : 0,
                Mouse: true, Move: (item.Flags & 1) != 0));
        }
        return sequences.Decode(result);
    }
    public void Present(TuiCanvas canvas)
    {
        var encoded = Encode(canvas, previous); if (encoded.Length > 0) Write(encoded); previous = canvas;
    }
    private static readonly string[] Colors = ["32;32;32", "56;56;56", "13;20;17", "64;64;64", "125;38;38", "119;68;119", "133;114;71", "218;218;218", "43;43;43", "57;189;248", "139;227;169", "79;212;208", "239;68;68", "160;160;160", "242;190;109", "255;255;255"];
    internal static string Encode(TuiCanvas canvas, TuiCanvas? previous = null)
    {
        var text = new System.Text.StringBuilder(canvas.Cells.Length + 2000); int color = -1;
        for (var y = 0; y < canvas.Height; y++)
        {
            if (previous is not null && previous.Width == canvas.Width && previous.Height == canvas.Height)
            {
                var changed = false;
                for (var x = 0; x < canvas.Width; x++) { var i = y * canvas.Width + x; if (canvas.Cells[i].Character != previous.Cells[i].Character || canvas.Cells[i].Attributes != previous.Cells[i].Attributes) { changed = true; break; } }
                if (!changed) continue;
            }
            text.Append("\u001b[").Append(y + 1).Append(";1H");
            for (var x = 0; x < canvas.Width; x++)
            {
                var cell = canvas.Cells[y * canvas.Width + x];
                if (cell.Attributes != color) { color = cell.Attributes; text.Append("\u001b[38;2;").Append(Colors[color & 15]).Append(";48;2;").Append(Colors[(color >> 4) & 15]).Append('m'); }
                text.Append(char.IsControl(cell.Character) ? ' ' : cell.Character);
                // Force the gear's single-cell text glyph. Emoji presentation can
                // occupy two terminal columns and shift it off the button centre.
                if (cell.Character == '⚙') text.Append('\uFE0E');
            }
        }
        return text.ToString();
    }
    private void Write(string text) => Check(WriteConsole(output, text, (uint)text.Length, out _, 0));
    internal void Inject(params Record[] records) => Check(WriteConsoleInput(input, records, (uint)records.Length, out _));
    internal string ReadBack()
    {
        var size = Size; var cells = new Cell[size.Width * size.Height]; GetConsoleScreenBufferInfo(output, out var info); var region = info.Window;
        Check(ReadConsoleOutput(output, cells, new Coord(size.Width, size.Height), new Coord(0, 0), ref region));
        return new string(cells.Select(c => c.Character).ToArray());
    }
    public static void Copy(string value)
    {
        // OSC 52 addresses the terminal user's clipboard over SSH, rather than
        // silently copying to the remote server's desktop clipboard.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")))
        { active?.Write("\u001b]52;c;" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)) + "\u0007"); return; }
        if (!OpenClipboard(0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0;
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0"); memory = GlobalAlloc(0x42, (nuint)bytes.Length);
            if (memory == 0) throw new OutOfMemoryException(); var pointer = GlobalLock(memory);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(memory); }
            Check(EmptyClipboard());
            if (SetClipboardData(13, memory) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); memory = 0;
        }
        finally { if (memory != 0) GlobalFree(memory); CloseClipboard(); }
    }
    public static string Paste()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"))) return "";
        if (!OpenClipboard(0)) return "";
        try { var memory = GetClipboardData(13); if (memory == 0) return ""; var p = GlobalLock(memory); try { return p == 0 ? "" : Marshal.PtrToStringUni(p) ?? ""; } finally { if (p != 0) GlobalUnlock(memory); } }
        finally { CloseClipboard(); }
    }
    public void Dispose()
    {
        active = null;
        Write("\u001b[?1002l\u001b[?1006l\u001b[0m\u001b[?25h\u001b[?1049l");
        SetConsoleCtrlHandler(handler, false); SetConsoleMode(input, oldMode);
        SetConsoleMode(output, oldOutputMode); var cursor = oldCursor; SetConsoleCursorInfo(output, ref cursor);
        CloseHandle(input); CloseHandle(output); if (allocated) FreeConsole();
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private delegate bool ControlHandler(uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint process);
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleCursorInfo(nint handle, out CursorInfo info);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteConsole(nint handle, string text, uint length, out uint written, nint reserved);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFile(string file, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetConsoleTitle(string title);
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(nint handle, out BufferInfo info);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleScreenBufferInfoEx(nint handle, ref ExtendedInfo info);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleScreenBufferInfoEx(nint handle, ref ExtendedInfo info);
    [DllImport("kernel32.dll")] private static extern Coord GetLargestConsoleWindowSize(nint handle);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleScreenBufferSize(nint handle, Coord size);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleWindowInfo(nint handle, bool absolute, ref Region region);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCursorInfo(nint handle, ref CursorInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetCurrentConsoleFontEx(nint handle, bool maximum, ref FontInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] private static extern bool ReadConsoleInput(nint handle, [Out] Record[] records, uint length, out uint read);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)] private static extern bool WriteConsoleInput(nint handle, Record[] records, uint length, out uint written);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleOutputW", SetLastError = true)] private static extern bool WriteConsoleOutput(nint handle, Cell[] cells, Coord size, Coord origin, ref Region region);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputW", SetLastError = true)] private static extern bool ReadConsoleOutput(nint handle, [Out] Cell[] cells, Coord size, Coord origin, ref Region region);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ControlHandler handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int show);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
