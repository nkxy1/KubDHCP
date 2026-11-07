using KubDHCP.Core;

namespace KubDHCP.App.Tui;

internal static class TuiSelfTest
{
    public static int Run(string reportPath)
    {
        try
        {
            var navigation = new TuiSurface(); var left = new TuiField("links"); var right = new TuiField("rechts");
            navigation.Begin(80, 25); navigation.Field("left", 2, 2, 20, left); navigation.Field("right", 30, 2, 20, right);
            navigation.Button("below", 30, 8, 10, "OK", () => { }); navigation.EnsureFocus();
            navigation.Handle(new(Character: 'x')); Check(left.Text == "links", "Selected field accepted input before Enter");
            navigation.Handle(new(Key: 39)); Check(navigation.FocusId == "right", "Right arrow did not navigate");
            navigation.Handle(new(Key: 40)); Check(navigation.FocusId == "below", "Down arrow did not navigate");
            navigation.Handle(new(Key: 38)); Check(navigation.FocusId == "right", "Up arrow did not navigate");
            navigation.Handle(new(Key: 13)); navigation.Handle(new(Key: 65, Control: true)); navigation.Handle(new(Character: 'X'));
            navigation.Handle(new(Key: 37)); Check(navigation.FocusId == "right" && navigation.Editing && right.Cursor == 0, "Editing arrow left the field");
            navigation.Handle(new(Key: 13)); Check(!navigation.Editing && right.Text == "X", "Enter did not commit editing");
            navigation.Handle(new(Key: 13)); navigation.Handle(new(Character: 'Y')); navigation.Handle(new(Key: 27));
            Check(!navigation.Editing && right.Text == "X", "Escape did not revert editing");
            var decoder = new TerminalSequences();
            static TerminalInput[] Sequence(string text) => text.Select(c => new TerminalInput(Character: c)).ToArray();
            Check(decoder.Decode(Sequence("\u001b[Z")).Single() is { Key: 9, Shift: true }, "SSH Shift+Tab failed");
            Check(decoder.Decode(Sequence("\u001b[A")).Single().Key == 38, "SSH arrow failed");
            Check(decoder.Decode(Sequence("\u001b[21~")).Single().Key == 121, "SSH F10 failed");
            Check(decoder.Decode(Sequence("\u0011")).Single() is { Key: 81, Control: true }, "SSH Ctrl+Q failed");
            Check(decoder.Decode(Sequence("\u001b[<0;20;")).Count == 0, "Split mouse sequence was not buffered");
            Check(decoder.Decode(Sequence("8M")).Single() is { Mouse: true, X: 19, Y: 7, Buttons: 1 }, "SSH mouse click failed");
            Check(decoder.Decode(Sequence("\u001b[<0;20;8m")).Single() is { Mouse: true, Buttons: 0 }, "SSH mouse release failed");
            Check(decoder.Decode(Sequence("\u001b[<64;20;8M")).Single().Wheel == 1, "SSH mouse wheel failed");
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "tui-test-" + Guid.NewGuid().ToString("N"));
            var store = new DataStore(directory);
            var adapter = new AdapterInfo("tui-test", "Test LAN", "Isolated test adapter", 1, "192.168.50.1", "255.255.255.0", "192.168.50.254", ["1.1.1.1"], ["192.168.50.1"], false);
            var configuration = ServerConfiguration.ForAdapter(adapter) with { Options = [new() { Code = 15, Name = "Domain Name", Format = "Text (ASCII)", Value = "lab.example" }] };
            store.SaveConfiguration(configuration); store.SaveRange("Labor", configuration);
            var now = DateTimeOffset.UtcNow;
            store.SaveLeases([new() { Scope = configuration.Scope, ClientKey = "ID:01020304050607", Address = "192.168.50.100", Mac = "B8:27:EB:01:02:03", Hostname = "printer-lab", LastConfirmedUtc = now, ExpiresUtc = now.AddHours(1) },
                new() { Scope = configuration.Scope, ClientKey = "HW:1:001B1B010203", Address = "192.168.50.101", Mac = "00:1B:1B:01:02:03", LastConfirmedUtc = now, ExpiresUtc = now.AddHours(1) }]);
            using var app = new TuiApplication(null, store, [adapter]);
            var canvas = app.Render(176, 62); var text = canvas.PlainText();
            Check(text.Contains("DHCP-Konfiguration") && text.Contains("Leases") && text.Contains("Client-ID") && text.Contains("Hostname"), "Layout missing sections");
            Check(text.Contains("printer-lab") && text.Contains("01020304050607"), "Lease information missing");
            Check(!text.Contains("Netzwerk-Monitor"), "Monitor visible at startup");
            Snapshot(reportPath + ".main", canvas);
            Click(app, "adapter-settings"); app.Render(176, 62);
            Check(app.Surface.Controls.Any(c => c.Id == "adapter-address") && app.Surface.Controls.Any(c => c.Id == "adapter-dns"), "Adapter settings opened outside terminal");
            app.Handle(new(Key: 27)); app.Render(176, 62);
            var first = app.Surface.FocusId; app.Handle(new(Key: 9)); Check(first != app.Surface.FocusId, "Tab did not change focus");
            app.Handle(new(Key: 9, Shift: true)); Check(first == app.Surface.FocusId, "Shift+Tab did not reverse focus");
            app.Surface.FocusId = "start"; app.Render(176, 62); app.Handle(new(Key: 13)); app.Handle(new(Key: 65, Control: true));
            foreach (var c in "192.168.50.110") app.Handle(new(Character: c));
            Check(app.Field("start").Text == "192.168.50.110", "Text editing failed");
            app.Handle(new(Key: 9)); Check(store.LoadConfiguration()!.StartAddress == "192.168.50.110", "Draft was not saved");
            app.Render(176, 62); Click(app, "monitor"); canvas = app.Render(176, 62); Check(app.MonitorVisible && canvas.PlainText().Contains("Netzwerk-Monitor"), "Mouse monitor toggle failed");
            Check(TransportSelfTest.Run(reportPath + ".network.txt", app.QueueNetwork) == 0, "Loopback monitor transport failed");
            app.Poll(); canvas = app.Render(176, 62); Snapshot(reportPath + ".monitor", canvas);
            Check(canvas.PlainText().Contains("ACK"), "Real loopback response missing from TUI monitor");
            var splitter = app.Surface.Controls.Single(c => c.Id == "monitor-resize").Rect;
            app.Handle(new(X: splitter.X, Y: splitter.Y + 5, Buttons: 1, Mouse: true));
            app.Handle(new(X: splitter.X - 8, Y: splitter.Y + 5, Buttons: 1, Mouse: true, Move: true));
            app.Handle(new(X: splitter.X - 8, Y: splitter.Y + 5, Mouse: true)); app.Render(176, 62);
            Check(app.Surface.Controls.Single(c => c.Id == "monitor-resize").Rect.X == splitter.X - 8, "Monitor resize by mouse failed");
            Click(app, "monitor-state"); app.Render(176, 62); Check(!app.Following, "Pause click failed");
            app.QueueNetwork("SELFTEST · buffered-message"); app.Poll(); Check(!app.Render(176, 62).PlainText().Contains("buffered-message"), "Pause did not buffer");
            Click(app, "monitor-state"); app.Render(176, 62); Check(app.Following && app.Render(176, 62).PlainText().Contains("buffered-message"), "Resume click failed");
            for (var i = 0; i < 100; i++) app.QueueNetwork($"SELFTEST · scroll line {i}"); app.Poll(); app.Render(176, 62);
            var output = app.Surface.Controls.Single(c => c.Id == "monitor-output").Rect;
            app.Handle(new(X: output.X + 1, Y: output.Y + 1, Wheel: 1, Mouse: true)); app.Render(176, 62); Check(!app.Following, "Wheel scrolling did not pause");
            app.QueueNetwork("SELFTEST · after-scroll"); app.Poll();
            app.Handle(new(X: output.X + 1, Y: output.Y + 1, Wheel: -1000, Mouse: true)); app.Poll();
            Check(app.Following && app.Render(176, 62).PlainText().Contains("after-scroll"), "Scrolling to end did not resume");
            Click(app, "monitor"); Check(!app.Render(176, 62).PlainText().Contains("Netzwerk-Monitor"), "Monitor did not collapse");
            app.Render(176, 62); Click(app, "ranges"); Check(app.Render(176, 62).PlainText().Contains("Bereich laden"), "Range picker did not open");
            app.Surface.FocusId = "popup-list"; app.Handle(new(Key: 13)); app.Render(176, 62);
            Check(app.Field("start").Text == configuration.StartAddress, "Named range did not restore configuration");
            Click(app, "save-range"); app.Render(176, 62); Type(app, "popup-name", "Zweitbereich");
            app.Surface.FocusId = "popup-save"; app.Handle(new(Key: 13)); app.Render(176, 62);
            Check(store.LoadRanges().Count == 2, "Named range save failed");
            Click(app, "remove-range"); app.Render(176, 62); Check(store.LoadRanges().Single().Name == "Labor" && app.Leases.Count == 2, "Range deletion damaged leases");
            Click(app, "add-option"); Check(app.Render(176, 62).PlainText().Contains("IANA-Optionskatalog"), "Option editor missing");
            Type(app, "popup-code", "66"); Type(app, "popup-value", "boot.example"); app.Render(176, 62); Click(app, "popup-always"); app.Render(176, 62);
            app.Surface.FocusId = "popup-save"; app.Handle(new(Key: 13)); app.Render(176, 62);
            Check(store.LoadConfiguration()!.Options.Single(o => o.Code == 66).AlwaysSend, "Option editor did not persist option");
            Type(app, "option:66", "updated.example"); app.Handle(new(Key: 9)); app.Render(176, 62);
            Check(store.LoadConfiguration()!.Options.Single(o => o.Code == 66).Value == "updated.example", "Inline option edit failed");
            Click(app, "remove-option:66"); app.Render(176, 62); Check(store.LoadConfiguration()!.Options.Single().Code == 15, "Inline option removal failed");
            Click(app, "add-option"); app.Render(80, 25);
            for (var i = 0; i < 9; i++) { app.Handle(new(Key: 9)); app.Render(80, 25); }
            Check(app.Surface.Focused is { } focused && focused.Rect.Y >= 2 && focused.Rect.Bottom <= 23, "Small option dialog does not scroll its focus into view");
            app.Handle(new(Key: 27)); app.Render(176, 62);
            var before = app.Leases.First(); Click(app, "delete-lease:0"); app.Render(176, 62);
            Check(app.Leases.Count == 1 && store.LoadLeases().Count == 1 && app.Leases.Single() != before, "Single lease delete removed the wrong rows");
            app.Render(176, 62); File.WriteAllText(Path.ChangeExtension(reportPath, ".screen.txt"), app.Render(176, 62).PlainText());
            // Exercise actual console input/output separately from the headless event tests.
            using (var terminal = new ConsoleTerminal(hidden: true))
            {
                var size = terminal.Size; var screen = app.Render(size.Width, size.Height); terminal.Present(screen);
                File.WriteAllText(reportPath + ".native.txt", $"{size}\n" + terminal.ReadBack());
                Check(terminal.ReadBack().Contains("DHCP-Konfiguration") && terminal.ReadBack().Contains("Primärer DNS-Server"), "Native Unicode console output failed");
                terminal.Inject(new ConsoleTerminal.Record { Type = 1, Down = 1, Repeat = 1, Key = 9, Character = '\t' });
                Check(terminal.Read(1000).Any(i => i.Key == 9), "Native keyboard input failed");
                terminal.Inject(new ConsoleTerminal.Record { Type = 2, Position = new ConsoleTerminal.Coord(4, 4), Buttons = 1 });
                Check(terminal.Read(1000).Any(i => i.Mouse && i.Buttons == 1), "Native mouse input failed");
            }
            app.Render(176, 62); Click(app, "reset-leases"); Check(app.Leases.Count == 0 && store.LoadLeases().Count == 0, "Lease reset failed");
            app.Handle(new(Key: 81, Control: true)); Check(app.ExitRequested, "Keyboard quit failed");
            File.WriteAllText(reportPath, "PASS: TUI layout; spatial arrow navigation; Enter edit/commit; Escape revert; no typing before activation; Tab/Shift+Tab; editing and durable drafts; clickable buttons; named range save/load/delete; option add/edit/delete; responsive dialogs; monitor drag, pause/resume and scroll; real loopback traffic; exact single lease delete and reset; native Unicode console output, keyboard and mouse input."); return 0;
        }
        catch (Exception ex) { File.WriteAllText(reportPath, "FAIL: " + ex); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Type(TuiApplication app, string id, string text)
    {
        app.Surface.EndInteraction(); app.Surface.FocusId = id; app.Handle(new(Key: 13)); app.Handle(new(Key: 65, Control: true));
        foreach (var c in text) app.Handle(new(Character: c));
    }
    private static void Snapshot(string path, TuiCanvas canvas)
    {
        File.WriteAllText(path + ".txt", canvas.PlainText());
        using var writer = new BinaryWriter(File.Create(path + ".cells")); writer.Write(canvas.Width); writer.Write(canvas.Height);
        foreach (var cell in canvas.Cells) { writer.Write((ushort)cell.Character); writer.Write(cell.Attributes); }
    }
    private static void Click(TuiApplication app, string id)
    {
        var control = app.Surface.Controls.Single(c => c.Id == id);
        app.Handle(new(X: control.Rect.X + Math.Min(1, control.Rect.Width - 1), Y: control.Rect.Y + Math.Min(1, control.Rect.Height - 1), Buttons: 1, Mouse: true));
        app.Handle(new(X: control.Rect.X + Math.Min(1, control.Rect.Width - 1), Y: control.Rect.Y + Math.Min(1, control.Rect.Height - 1), Mouse: true));
    }
}
