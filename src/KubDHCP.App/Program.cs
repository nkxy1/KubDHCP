using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KubDHCP.App;

internal static class Program
{
    private static App? app;
    internal static string? StartupTestReport { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        // Console subsystem makes PowerShell wait. The detached manifest prevents a
        // console on graphical launches; detach on older Windows before WinUI starts.
        if (!args.Contains("--tui", StringComparer.Ordinal)) FreeConsole();
        if (args.Length == 2 && args[0] == "--transport-test") return TransportSelfTest.Run(args[1]);
        if (args.Length == 2 && args[0] == "--tui-test") return Tui.TuiSelfTest.Run(args[1]);
        var tui = args.Contains("--tui", StringComparer.Ordinal);
        if (args.Length == 2 && args[0] == "--startup-test") StartupTestReport = Path.GetFullPath(args[1]);
        // WinUI resolves its compiled XAML resources using the executable's original name.
        if (!tui && !string.Equals(Path.GetFileName(Environment.ProcessPath), "KubDHCP.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox(0, "Bitte die Programmdatei wieder KubDHCP.exe nennen. Die Versionsnummer gehört in den Ordnernamen, nicht in den EXE-Dateinamen.", "KubDHCP", 0x10);
            return 1;
        }
        using var mutex = new Mutex(true, "Local\\KubDHCP-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!)))[..16], out var created);
        if (!created) { if (tui) return Tui.TuiApplication.ShowError("KubDHCP läuft bereits aus diesem Ordner. WinUI und TUI teilen denselben Server und Datenordner."); MessageBox(0, "KubDHCP läuft bereits aus diesem Ordner.", "KubDHCP", 0x40); return 0; }
        try
        {
            if (tui) return Tui.TuiApplication.Run();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                app = new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            var report = Path.Combine(Path.GetTempPath(), "KubDHCP-startup.log");
            File.WriteAllText(report, ex.ToString());
            if (tui) return Tui.TuiApplication.ShowError($"KubDHCP konnte nicht gestartet werden.\n{ex.Message}\nDiagnose: {report}");
            MessageBox(0, $"KubDHCP konnte nicht gestartet werden.\n\n{ex.Message}\n\nDiagnose: {report}", "KubDHCP", 0x10); return 1;
        }
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint window, string text, string title, uint type);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
}

public sealed partial class App : Application
{
    private MainWindow? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "KubDHCP-unhandled.log"), e.Exception.ToString()); } catch (IOException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(); window.Activate(); window.SizeForStartup();
        if (Program.StartupTestReport is { } report)
        {
            var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    await window.VerifyMonitorStartupAsync(report);
                    if (window.Content is not FrameworkElement content || !content.IsLoaded || content.ActualWidth <= 0 || content.ActualHeight <= 0)
                        throw new InvalidOperationException("WinUI-Fenster wurde nicht geladen.");
                    File.WriteAllText(report, $"PASS: WinUI window loaded; normal window state; monitor resize; blinking PAUSE; clickable manual pause/resume; buffered resume; monospace; hide preserves position and height; live loopback DISCOVER/OFFER/REQUEST/ACK; version {typeof(App).Assembly.GetName().Version}; executable {Environment.ProcessPath}");
                }
                catch (Exception ex) { File.WriteAllText(report, "FAIL: " + ex); }
                window.Close();
            };
            timer.Start();
        }
    }
}
