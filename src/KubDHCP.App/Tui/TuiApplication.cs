using System.Collections.Concurrent;
using System.Globalization;
using KubDHCP.Core;

namespace KubDHCP.App.Tui;

internal sealed class TuiApplication : IDisposable
{
    internal readonly TuiSurface Surface = new();
    private readonly ConsoleTerminal? terminal;
    internal readonly DataStore Store;
    private readonly DhcpServer server;
    private readonly OuiLookup oui = new();
    private readonly List<OptionDefinition> catalog = OptionDefinition.Load();
    private readonly Dictionary<string, TuiField> fields = [];
    private readonly List<ExtraOption> options = [];
    private readonly ConcurrentQueue<string> network = new();
    private readonly List<string> history = [];
    private readonly Stack<Popup> popups = [];
    private AdapterInfo[] adapters = [];
    private List<SavedRange> ranges = [];
    internal IReadOnlyList<LeaseRecord> Leases = [];
    private readonly AdapterInfo[]? testAdapters;
    private AdapterInfo? adapter;
    private string? selectedRange, startupError, pendingStatus, pendingFailure;
    internal string Status = "Bereit";
    private bool error, dirty, exit, monitorVisible, following = true, manualPause;
    private int unit = 2, mainOffset, mainHeight, mainWidth, monitorWidth = 48, monitorOffset, monitorLines;
    private int leaseOffset, leaseHorizontal, selectedLease, selectedColumn, queued, dropped, leaseDirty;
    private readonly int[] columnWidths = [18, 16, 19, 22, 19, 22, 28];
    private int dragColumn = -1, dragX;
    private bool dragMonitor, leftHeld;
    private TuiRect tableRect, monitorRect;
    private TuiRect popupBounds;
    private int width = 176, height = 62;
    internal bool MonitorVisible => monitorVisible;
    internal bool Following => following;
    internal bool ExitRequested => exit;
    internal TuiField Field(string key, string initial = "")
    {
        if (!fields.TryGetValue(key, out var result)) { result = new(initial); fields[key] = result; result.Changed = _ => dirty = true; }
        return result;
    }
    public TuiApplication(ConsoleTerminal? terminal, DataStore? store = null, AdapterInfo[]? testAdapters = null)
    {
        this.terminal = terminal; this.testAdapters = testAdapters; Store = store ?? OpenStore(); server = new(Store);
        server.Status += text => Volatile.Write(ref pendingStatus, text);
        server.NetworkActivity += QueueNetwork;
        server.LeasesChanged += () => Interlocked.Exchange(ref leaseDirty, 1);
        server.Failed += text => Volatile.Write(ref pendingFailure, text);
        ServerConfiguration? saved = null;
        try { saved = Store.LoadConfiguration(); Store.LoadLeases(); }
        catch (Exception ex) when (IsDataError(ex)) { startupError = ex.Message; Notify(ex.Message, true); }
        RefreshAdapters(saved?.AdapterId, saved); RefreshRanges();
        QueueNetwork("Monitor bereit · DHCP/ARP-Kommunikation dieses Servers. Server gestoppt.");
        using var notices = typeof(TuiApplication).Assembly.GetManifestResourceStream("KubDHCP.App.ThirdPartyNotices.txt");
        if (notices is not null) { using var reader = new StreamReader(notices); try { File.WriteAllText(Path.Combine(Store.DirectoryPath, "Lizenzen.txt"), reader.ReadToEnd()); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static DataStore OpenStore()
    {
        try
        {
            var store = new DataStore(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "KubDHCP.data"));
            var check = Path.Combine(store.DirectoryPath, ".tui-write-check"); File.WriteAllText(check, ""); File.Delete(check); return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new DataStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KubDHCP")); }
    }
    public static int Run()
    {
        using var terminal = new ConsoleTerminal(); using var app = new TuiApplication(terminal);
        while (!app.exit && !terminal.QuitRequested)
        {
            app.Poll(); var size = terminal.Size; terminal.Present(app.Render(size.Width, size.Height));
            foreach (var input in terminal.Read()) { app.Handle(input); app.Render(size.Width, size.Height); }
        }
        return 0;
    }
    public static int ShowError(string message)
    {
        ConsoleTerminal terminal;
        try { terminal = new ConsoleTerminal(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Console.Error.WriteLine(message + "\n" + ex.Message); return 1; }
        using var terminalLifetime = terminal; var size = terminal.Size; var canvas = new TuiCanvas(size.Width, size.Height);
        canvas.Box(new(1, 1, size.Width - 2, size.Height - 2), "KubDHCP · TUI", TuiCanvas.Red);
        var lines = TuiCanvas.Wrap(message, size.Width - 6);
        for (var i = 0; i < Math.Min(lines.Count, size.Height - 7); i++) canvas.Text(3, i + 3, lines[i], size.Width - 6, TuiCanvas.Red);
        canvas.Text(3, size.Height - 4, "Beliebige Taste zum Schließen.", size.Width - 6); terminal.Present(canvas);
        while (!terminal.QuitRequested) if (terminal.Read().Any(i => !i.Mouse && i.Key != 0)) break;
        return 1;
    }
    internal void Poll()
    {
        var failure = Interlocked.Exchange(ref pendingFailure, null);
        if (failure is not null) { server.StopAsync().GetAwaiter().GetResult(); Notify(failure, true); ReloadLeases(); }
        var text = Interlocked.Exchange(ref pendingStatus, null); if (text is not null && failure is null) Notify(text);
        if (Interlocked.Exchange(ref leaseDirty, 0) != 0) ReloadLeases();
        if (!following && monitorVisible) return;
        var lost = Interlocked.Exchange(ref dropped, 0);
        if (lost > 0) history.Add($"[{DateTime.Now:HH:mm:ss.fff}] MONITOR · {lost} Meldungen bei hoher Last ausgelassen.");
        for (var i = 0; i < 250 && network.TryDequeue(out var line); i++) { Interlocked.Decrement(ref queued); history.Add(line); }
        if (history.Count > 2000) history.RemoveRange(0, history.Count - 2000);
    }
    internal void QueueNetwork(string line)
    {
        if (Interlocked.Increment(ref queued) > 4000) { Interlocked.Decrement(ref queued); Interlocked.Increment(ref dropped); return; }
        network.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
    }
    private void Notify(string message, bool failure = false) { Status = message.Replace('\r', ' ').Replace('\n', ' '); error = failure; }
    private static bool IsDataError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
    private void RefreshAdapters(string? preferred = null, ServerConfiguration? saved = null)
    {
        if (server.IsRunning) return;
        adapters = testAdapters ?? AdapterInfo.Enumerate().ToArray();
        adapter = adapters.FirstOrDefault(a => saved is not null && a.Id == saved.AdapterId && a.Address == saved.ServerAddress && a.Mask == saved.Mask)
            ?? adapters.FirstOrDefault(a => a.Id == preferred) ?? adapters.FirstOrDefault();
        if (adapter is not null)
            Apply(saved is not null && adapter.Id == saved.AdapterId && adapter.Address == saved.ServerAddress && adapter.Mask == saved.Mask ? saved : ServerConfiguration.ForAdapter(adapter) with { Options = options.ToList() });
        else Notify("Keine aktive IPv4-LAN-Karte. Netzwerk verbinden und aktualisieren.", true);
        ReloadLeases();
    }
    private void RefreshRanges()
    {
        try { ranges = Store.LoadRanges(); }
        catch (Exception ex) when (IsDataError(ex)) { Notify("Bereiche: " + ex.Message, true); }
    }
    private void Apply(ServerConfiguration c)
    {
        foreach (var (key, value) in new[] { ("start", c.StartAddress), ("end", c.EndAddress), ("mask", c.Mask), ("gateway", c.Gateway), ("dns1", c.PrimaryDns), ("dns2", c.SecondaryDns) }) Field(key).Set(value);
        var seconds = c.LeaseSeconds; unit = seconds % 86400 == 0 && seconds > 0 ? 3 : seconds % 3600 == 0 && seconds > 0 ? 2 : seconds % 60 == 0 && seconds > 0 ? 1 : 0;
        Field("duration").Set((seconds / new uint[] { 1, 60, 3600, 86400 }[unit]).ToString(CultureInfo.InvariantCulture));
        options.Clear(); options.AddRange(c.Options); foreach (var key in fields.Keys.Where(k => k.StartsWith("option:", StringComparison.Ordinal)).ToArray()) fields.Remove(key);
        dirty = false;
    }
    private ServerConfiguration ReadConfiguration()
    {
        if (adapter is null) throw new ArgumentException("Bitte eine LAN-Karte auswählen.");
        if (!ulong.TryParse(Field("duration").Text, out var value) || value == 0 || value > uint.MaxValue) throw new ArgumentException("Lease-Dauer muss eine positive ganze Zahl sein.");
        var seconds = value * new uint[] { 1, 60, 3600, 86400 }[unit];
        if (seconds > 4294967294) throw new ArgumentException("Lease-Dauer ist zu groß.");
        foreach (var option in options) OptionCodec.Encode(option);
        return new() { AdapterId = adapter.Id, ServerAddress = adapter.Address, StartAddress = Field("start").Text.Trim(), EndAddress = Field("end").Text.Trim(), Mask = Field("mask").Text.Trim(),
            Gateway = Field("gateway").Text.Trim(), PrimaryDns = Field("dns1").Text.Trim(), SecondaryDns = Field("dns2").Text.Trim(), LeaseSeconds = (uint)seconds, Options = options.ToList() };
    }
    private void SaveDraft()
    {
        if (!dirty || adapter is null || server.IsRunning) return;
        try { var c = ReadConfiguration(); c.Validate(adapter); Store.SaveConfiguration(c); dirty = false; }
        catch (ArgumentException) { }
        catch (Exception ex) when (IsDataError(ex)) { Notify(ex.Message, true); }
    }
    private void ToggleServer()
    {
        if (server.IsRunning) { server.StopAsync().GetAwaiter().GetResult(); Notify("Server gestoppt. Bereits vergebene Leases bleiben bis zum Ablauf gültig."); }
        else
        {
            if (startupError is not null) throw new InvalidOperationException(startupError);
            var c = ReadConfiguration(); c.Validate(adapter!); server.Start(adapter!, c); dirty = false;
        }
        ReloadLeases();
    }
    private void ReloadLeases()
    {
        try { Leases = server.IsRunning ? server.Leases : Store.LoadLeases().Where(l => adapter is not null && l.Scope == $"{Ipv4.Network(adapter.Address, adapter.Mask)}/{adapter.Mask}" && l.LastConfirmedUtc != default).OrderByDescending(l => l.LastConfirmedUtc).ToArray(); }
        catch (Exception ex) when (IsDataError(ex)) { Notify(ex.Message, true); }
        selectedLease = Math.Clamp(selectedLease, 0, Math.Max(0, Leases.Count - 1));
    }
    internal void DeleteLease(LeaseRecord lease)
    {
        var removed = server.RemoveLease(lease); ReloadLeases(); Notify(removed ? $"Lease {lease.Address} · {lease.Mac} gelöscht." : "Lease inzwischen geändert. Tabelle wurde aktualisiert.");
    }
    private void ResetLeases()
    {
        if (server.IsRunning) return; Store.SaveLeases([]); Store.LoadConfiguration(); startupError = null; ReloadLeases();
        Notify("Leases gelöscht. Clients behalten ihre lokale Adresse bis zur Erneuerung oder zum Ablauf.");
    }
    private void LoadRange(SavedRange range)
    {
        var c = range.Configuration; var match = adapters.FirstOrDefault(a => a.Id == c.AdapterId && a.Address == c.ServerAddress && a.Mask == c.Mask);
        if (match is null) throw new ArgumentException("Netzwerkschnittstelle dieses Bereichs ist nicht verfügbar oder wurde umkonfiguriert.");
        c.Validate(match); adapter = match; Apply(c); Store.SaveConfiguration(c); selectedRange = range.Name; ReloadLeases(); Notify($"Bereich „{range.Name}“ geladen.");
    }
    private void RemoveRange()
    {
        if (selectedRange is null) return; Store.RemoveRange(selectedRange); RefreshRanges(); Notify($"Bereich „{selectedRange}“ entfernt; aktuelle Einstellungen bleiben erhalten."); selectedRange = null;
    }
    private void AdapterSettings()
    {
        if (adapter is null || server.IsRunning) return;
        Push(new("Netzwerkeinstellungen · " + adapter.Name) { Kind = "adapter", Address = new(adapter.Address), Mask = new(adapter.Mask), Gateway = new(adapter.Gateway ?? ""), Dns = new(string.Join(", ", adapter.Dns)), Dhcp = adapter.DhcpEnabled });
    }
    private void SaveAdapter(Popup popup)
    {
        if (adapter is null || server.IsRunning) return;
        var address = popup.Address.Text.Trim(); var mask = popup.Mask.Text.Trim(); var gateway = popup.Gateway.Text.Trim();
        var dns = popup.Dns.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!popup.Dhcp)
        {
            Ipv4.Parse(address); if (!Ipv4.IsMask(Ipv4.Parse(mask))) throw new ArgumentException("Ungültige Subnetzmaske.");
            if (gateway.Length > 0) Ipv4.Parse(gateway);
        }
        foreach (var ip in dns) Ipv4.Parse(ip);
        var index = adapter.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        static void Netsh(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Netzwerkeinstellungen: Zeitüberschreitung. Adapterzustand prüfen."); }
            if (process.ExitCode != 0) throw new IOException((output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).Trim());
        }
        if (popup.Dhcp) Netsh("interface", "ipv4", "set", "address", "name=" + index, "source=dhcp");
        else Netsh("interface", "ipv4", "set", "address", "name=" + index, "source=static", "address=" + address, "mask=" + mask, "gateway=" + (gateway.Length == 0 ? "none" : gateway));
        if (dns.Length == 0) Netsh("interface", "ipv4", "set", "dnsservers", "name=" + index, "source=dhcp");
        else
        {
            Netsh("interface", "ipv4", "set", "dnsservers", "name=" + index, "source=static", "address=" + dns[0], "validate=no");
            for (var i = 1; i < dns.Length; i++) Netsh("interface", "ipv4", "add", "dnsservers", "name=" + index, "address=" + dns[i], "index=" + (i + 1).ToString(CultureInfo.InvariantCulture), "validate=no");
        }
        var id = adapter.Id; ClosePopup(); RefreshAdapters(id); Notify("Adaptereinstellungen übernommen.");
    }
    internal void ToggleMonitor() { monitorVisible = !monitorVisible; following = true; manualPause = false; }
    internal void TogglePlayback()
    {
        if (following) { following = false; manualPause = true; }
        else { following = true; manualPause = false; Poll(); }
    }
    private void ScrollMonitor(int delta)
    {
        monitorOffset = Math.Clamp(monitorOffset + delta, 0, Math.Max(0, monitorLines - monitorRect.Height + 5));
        if (!manualPause) following = monitorOffset >= Math.Max(0, monitorLines - monitorRect.Height + 5);
    }
    internal TuiCanvas Render(int columns, int rows)
    {
        width = Math.Max(40, columns); height = Math.Max(16, rows); Surface.Begin(width, height);
        if (monitorVisible) monitorWidth = Math.Clamp(monitorWidth, 30, Math.Max(30, width - 65));
        mainWidth = width - (monitorVisible ? monitorWidth + 1 : 0);
        Surface.MainViewport = new(0, 0, mainWidth, height - 3); Surface.Canvas.Clip = Surface.MainViewport; Surface.OffsetY = mainOffset;
        DrawMain(); Surface.OffsetY = 0; Surface.Canvas.Clip = new(0, 0, width, height);
        if (monitorVisible) DrawMonitor();
        var footer = TuiCanvas.Wrap(Status, Math.Max(1, width - 14));
        Surface.Canvas.Fill(new(0, height - 3, width, 3)); Surface.Label(1, height - 3, footer.FirstOrDefault() ?? "", width - 15, error ? TuiCanvas.Red : TuiCanvas.Normal);
        var version = typeof(TuiApplication).Assembly.GetName().Version!; Surface.Label(width - 13, height - 3, $"v{version.Major}.{version.Minor}.{version.Build}", 12, TuiCanvas.Muted);
        Surface.Label(1, height - 1, Surface.Editing ? "EINGABE · Enter: übernehmen · Esc: verwerfen · Mehrzeilig: Ctrl+Enter übernimmt" : "Pfeile: Auswahl · Enter: Eingabe/Aktion · Esc: zurück · F1: Hilfe · F10/Ctrl+Q: Ende", width - 2, TuiCanvas.Muted);
        if (popups.TryPeek(out var popup)) DrawPopup(popup);
        Surface.EnsureFocus(); return Surface.Canvas;
    }
    private void DrawMain()
    {
        var right = mainWidth - 3; var usable = Math.Max(24, mainWidth - 6); var countColumns = mainWidth >= 96 ? 3 : mainWidth >= 65 ? 2 : 1;
        var cellWidth = (usable - (countColumns - 1) * 2) / countColumns;
        var leaseY = 15 + (int)Math.Ceiling(6d / countColumns) * 5;
        var optionsY = leaseY + 5 + (countColumns == 1 ? 3 : 0); var cardBottom = optionsY + options.Count * 5 + 1;
        Surface.Box(1, 1, mainWidth - 2, cardBottom + 1, color: TuiCanvas.Card);
        Surface.Label(3, 3, "DHCP-Konfiguration", Math.Max(1, mainWidth - 40), TuiCanvas.Card);
        Surface.Label(right - 25, 3, server.IsRunning ? "● Läuft" : "● Gestoppt", 12, server.IsRunning ? (ushort)0x8A : (ushort)0x8C);
        Surface.Button("server", right - 12, 2, 5, server.IsRunning ? "■" : "▶", ToggleServer, adapter is not null && (server.IsRunning || startupError is null), true);
        Surface.Button("monitor", right - 5, 2, 5, ">_", ToggleMonitor, accent: monitorVisible);
        Surface.Label(3, 6, "Gespeicherte Bereiche", color: TuiCanvas.Card);
        Surface.Choice("ranges", 3, 7, usable - 14, selectedRange ?? "Gespeicherten Bereich laden", () => Pick("Bereich laden", ranges.Select(r => r.Name).ToArray(), i => LoadRange(ranges[i])), !server.IsRunning);
        Surface.Button("save-range", right - 12, 7, 5, "S", OpenSaveRange, adapter is not null); Surface.Button("remove-range", right - 5, 7, 5, "X", RemoveRange, selectedRange is not null);
        Surface.Label(3, 10, "Netzwerkschnittstelle", color: TuiCanvas.Card);
        Surface.Choice("adapters", 3, 11, usable - 14, adapter?.DisplayName ?? "Keine Schnittstelle", () => Pick("Netzwerkschnittstelle", adapters.Select(a => a.DisplayName).ToArray(), i => { adapter = adapters[i]; Apply(ServerConfiguration.ForAdapter(adapter) with { Options = options.ToList() }); selectedRange = null; ReloadLeases(); }), !server.IsRunning);
        Surface.Button("adapter-settings", right - 12, 11, 5, "⚙", AdapterSettings, adapter is not null && !server.IsRunning);
        Surface.Button("refresh", right - 5, 11, 5, "↻", () => { ServerConfiguration? draft = null; try { draft = ReadConfiguration(); } catch (ArgumentException) { } RefreshAdapters(adapter?.Id, draft); }, !server.IsRunning);
        Surface.Label(3, 14, adapter is null ? "" : $"{adapter.Address} · {adapter.Mask} · {(adapter.DhcpEnabled ? "Adapteradresse per DHCP" : "Statische Adapteradresse")}", usable, TuiCanvas.Card);
        var names = new[] { ("start", "Start-IP"), ("end", "Stop-IP"), ("mask", "Subnetzmaske"), ("gateway", "Gateway"), ("dns1", "Primärer DNS-Server"), ("dns2", "Sekundärer DNS-Server") };
        for (var i = 0; i < names.Length; i++)
        {
            var x = 3 + i % countColumns * (cellWidth + 2); var y = 15 + i / countColumns * 5;
            Surface.Label(x, y, names[i].Item2, cellWidth, TuiCanvas.Card); Surface.Field(names[i].Item1, x, y + 1, cellWidth, Field(names[i].Item1), !server.IsRunning);
        }
        var half = Math.Max(8, (cellWidth - 1) / 2); Surface.Label(3, leaseY, "Lease-Dauer", color: TuiCanvas.Card);
        Surface.Field("duration", 3, leaseY + 1, half, Field("duration", "1"), !server.IsRunning);
        Surface.Choice("unit", 4 + half, leaseY + 1, Math.Max(8, cellWidth - half - 1), Units[unit], () => Pick("Lease-Einheit", Units, i => { unit = i; dirty = true; }), !server.IsRunning);
        Surface.Button("add-option", countColumns > 1 ? 3 + cellWidth + 2 : 3, leaseY + (countColumns > 1 ? 1 : 4), 22, "+ Option hinzufügen", () => OpenOption(null), true);
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i]; var y = optionsY + i * 5;
            var title = $"{option.Code} · {option.Name}" + (option.Enabled ? "" : " · deaktiviert");
            var titleControl = Surface.Custom("edit-option:" + option.Code, Surface.Rect(3, y, usable - 7, 1)); titleControl.Activate = () => OpenOption(option);
            Surface.Label(3, y, title, usable - 7, Surface.FocusId == titleControl.Id ? TuiCanvas.Focus : TuiCanvas.Card);
            var field = Field("option:" + option.Code, option.Value); field.Multiline = false;
            field.Changed = text => { var index = options.FindIndex(o => o.Code == option.Code); if (index >= 0) options[index] = options[index] with { Value = text.Trim() }; dirty = true; };
            var valid = true; try { OptionCodec.Encode(option); } catch (ArgumentException) { valid = false; }
            Surface.Field("option:" + option.Code, 3, y + 1, usable - 7, field, !server.IsRunning, invalid: !valid);
            Surface.Button("remove-option:" + option.Code, right - 5, y + 1, 5, "X", () => { options.RemoveAll(o => o.Code == option.Code); fields.Remove("option:" + option.Code); dirty = true; SaveDraft(); }, !server.IsRunning);
        }
        // A bottom rule is redrawn after the responsive one-column option row.
        Surface.Label(1, cardBottom + 1, "└" + new string('─', Math.Max(0, mainWidth - 4)) + "┘", mainWidth - 2, TuiCanvas.Card);
        var listY = cardBottom + 2; Surface.Label(2, listY + 1, "Leases", color: TuiCanvas.White);
        var active = Leases.Count(l => l.State == "Active" && l.ExpiresUtc > DateTimeOffset.UtcNow);
        var count = active == 1 ? "1 aktiver Lease" : $"{active} aktive Leases"; Surface.Label(Math.Max(9, right - count.Length - 17), listY + 1, count, count.Length);
        Surface.Button("reset-leases", right - 14, listY, 14, "Lease Reset", ResetLeases, !server.IsRunning);
        var tableY = listY + 4; var tableHeight = Math.Max(9, height - 4 - tableY); mainHeight = tableY + tableHeight;
        tableRect = Surface.Rect(1, tableY, mainWidth - 2, tableHeight); DrawLeases(tableY, tableHeight);
    }
    private static readonly string[] Units = ["Sekunden", "Minuten", "Stunden", "Tage"];
    private static string Remaining(LeaseRecord l)
    {
        var span = l.ExpiresUtc - DateTimeOffset.UtcNow;
        return l.State == "Released" ? "Freigegeben" : l.State == "Declined" ? "Adresskonflikt" : span <= TimeSpan.Zero ? "Abgelaufen" : (span.Days > 0 ? span.Days + "d " : "") + span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
    private string[] LeaseValues(LeaseRecord l) => [l.LastConfirmedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture), l.Address, l.Mac,
        l.ClientId.Length == 0 ? "—" : l.ClientId, string.IsNullOrWhiteSpace(l.Hostname) ? "—" : l.Hostname, oui.Find(l.Mac), Remaining(l)];
    private void DrawLeases(int y, int h)
    {
        Surface.Box(1, y, mainWidth - 2, h); var headings = new[] { "Zeitstempel", "IP", "MAC-Adresse", "Client-ID", "Hostname", "Gerätehersteller", "Rest-Leasezeit" };
        var visible = Math.Max(1, (h - 4) / 3); leaseOffset = Math.Clamp(leaseOffset, 0, Math.Max(0, Leases.Count - visible));
        var oldClip = Surface.Canvas.Clip; Surface.Canvas.Clip = new(2, tableRect.Y + 1, mainWidth - 4, h - 2);
        var tableControl = Surface.Custom("lease-table", tableRect, TableKey, TableMouse);
        var widths = TableWidths(); var x = 3 - leaseHorizontal;
        for (var col = 0; col < headings.Length; col++) { Surface.Label(x, y + 1, headings[col], widths[col] - 1, TuiCanvas.White); x += widths[col]; }
        for (var row = 0; row < visible && leaseOffset + row < Leases.Count; row++)
        {
            var index = leaseOffset + row; var lease = Leases[index]; var values = LeaseValues(lease); x = 3 - leaseHorizontal;
            var rowY = y + 3 + row * 3;
            for (var col = 0; col < values.Length; col++)
            {
                var color = Surface.FocusId == tableControl.Id && index == selectedLease && col == selectedColumn ? TuiCanvas.Focus : TuiCanvas.Normal;
                if (col == 0) { Surface.Label(x, rowY, values[col][..10], widths[col] - 1, color); Surface.Label(x, rowY + 1, values[col][11..], widths[col] - 1, color); }
                else if (col == 6)
                {
                    Surface.Label(x, rowY, values[col], Math.Max(1, widths[col] - 15), color);
                    var total = (lease.ExpiresUtc - lease.LastConfirmedUtc).TotalSeconds;
                    var fraction = total <= 0 || lease.State != "Active" ? 0 : Math.Clamp((lease.ExpiresUtc - DateTimeOffset.UtcNow).TotalSeconds / total, 0, 1);
                    var filled = (int)Math.Round(fraction * 6); Surface.Label(x + widths[col] - 14, rowY, new string('█', filled) + new string('░', 6 - filled), 6, TuiCanvas.Cyan);
                    Surface.Button("delete-lease:" + index, x + widths[col] - 7, rowY - 1, 5, "X", () => DeleteLease(lease));
                }
                else { var lines = TuiCanvas.Wrap(values[col], widths[col] - 1); for (var line = 0; line < Math.Min(2, lines.Count); line++) Surface.Label(x, rowY + line, lines[line], widths[col] - 1, color); }
                x += widths[col];
            }
            Surface.Label(2, rowY + 2, new string('─', Math.Max(0, mainWidth - 4)), mainWidth - 4, TuiCanvas.Muted);
        }
        if (Leases.Count == 0) Surface.Label(3, y + 3, "Noch keine bestätigten Adressvergaben.", mainWidth - 6, TuiCanvas.Muted);
        Surface.Label(3, y + h - 1, " ←/→: Spalten  PgUp/PgDn: Zeilen  Kopf ziehen: Breite ", mainWidth - 6, TuiCanvas.Muted);
        Surface.Canvas.Clip = oldClip;
    }
    private int ColumnAt(int screenX)
    {
        var widths = TableWidths(); var x = 3 - leaseHorizontal; for (var i = 0; i < widths.Length; i++) { if (screenX < x + widths[i]) return i; x += widths[i]; } return 6;
    }
    private int[] TableWidths() { var widths = columnWidths.ToArray(); widths[5] += Math.Max(0, mainWidth - 6 - widths.Sum()); return widths; }
    private void TableMouse(TerminalInput input)
    {
        if (input.Wheel != 0) { leaseOffset = Math.Max(0, leaseOffset - input.Wheel * 3); return; }
        if (input.Y == tableRect.Y + 1 && (input.Buttons & 1) != 0)
        {
            var edge = 3 - leaseHorizontal;
            var widths = TableWidths();
            for (var i = 0; i < widths.Length; i++) { edge += widths[i]; if (Math.Abs(input.X - edge) <= 1) { dragColumn = i; dragX = input.X; return; } }
        }
        var row = leaseOffset + (input.Y - tableRect.Y - 3) / 3;
        if (input.Y < tableRect.Y + 3 || row < 0 || row >= Leases.Count) return;
        selectedLease = row; selectedColumn = ColumnAt(input.X);
        if ((input.Buttons & 2) != 0) { var value = LeaseValues(Leases[row])[selectedColumn]; ConsoleTerminal.Copy(value); Notify("Kopiert: " + value); }
    }
    private void TableKey(TerminalInput input)
    {
        var visible = Math.Max(1, (tableRect.Height - 4) / 3);
        switch (input.Key)
        {
            case 38: selectedLease--; break; case 40: selectedLease++; break;
            case 33: selectedLease -= visible; break; case 34: selectedLease += visible; break;
            case 36: selectedLease = 0; break; case 35: selectedLease = Leases.Count - 1; break;
            case 37: selectedColumn = Math.Max(0, selectedColumn - 1); leaseHorizontal = Math.Max(0, leaseHorizontal - 12); break;
            case 39: selectedColumn = Math.Min(6, selectedColumn + 1); leaseHorizontal = Math.Min(Math.Max(0, columnWidths.Sum() - mainWidth + 8), leaseHorizontal + 12); break;
            case 46 when Leases.Count > 0: DeleteLease(Leases[selectedLease]); return;
            case 67 when input.Control && Leases.Count > 0: ConsoleTerminal.Copy(LeaseValues(Leases[selectedLease])[selectedColumn]); Notify("Tabellenwert kopiert."); break;
        }
        selectedLease = Math.Clamp(selectedLease, 0, Math.Max(0, Leases.Count - 1));
        if (selectedLease < leaseOffset) leaseOffset = selectedLease;
        if (selectedLease >= leaseOffset + visible) leaseOffset = selectedLease - visible + 1;
    }
    private void DrawMonitor()
    {
        var x = mainWidth + 1; monitorRect = new(x, 1, width - x - 1, height - 4);
        Surface.Canvas.Box(monitorRect, color: TuiCanvas.Monitor);
        Surface.Label(x + 2, 2, "Netzwerk-Monitor", monitorWidth - 18, TuiCanvas.Green);
        var statusColor = following ? TuiCanvas.Green : DateTime.UtcNow.Millisecond < 500 ? TuiCanvas.Red : (ushort)0x04;
        var stateRect = new TuiRect(width - 14, 2, 11, 1);
        var state = Surface.Custom("monitor-state", stateRect); state.Activate = TogglePlayback;
        Surface.Canvas.Text(stateRect.X, stateRect.Y, following ? "[▶ Live]" : "[Ⅱ PAUSE]", 11, Surface.FocusId == state.Id ? TuiCanvas.Focus : statusColor);
        Surface.Label(x + 2, 4, "DHCP & ARP · Hochscrollen pausiert", monitorWidth - 5, TuiCanvas.Muted);
        var lines = history.SelectMany(line => TuiCanvas.Wrap(line, Math.Max(1, monitorRect.Width - 4))).ToList(); monitorLines = lines.Count;
        var available = Math.Max(1, monitorRect.Height - 5); var maximum = Math.Max(0, lines.Count - available);
        monitorOffset = following ? maximum : Math.Clamp(monitorOffset, 0, maximum);
        var monitorContent = new TuiRect(x + 1, 5, monitorRect.Width - 2, available);
        Surface.Custom("monitor-output", monitorContent, input =>
        {
            if (input.Key == 38) ScrollMonitor(-1); if (input.Key == 40) ScrollMonitor(1);
            if (input.Key == 33) ScrollMonitor(-available); if (input.Key == 34) ScrollMonitor(available);
            if (input.Key == 36) ScrollMonitor(-monitorLines); if (input.Key == 35) ScrollMonitor(monitorLines);
            if (input.Key is 13 or 32) TogglePlayback();
        }, input => { if (input.Wheel != 0) ScrollMonitor(-input.Wheel * 3); });
        for (var i = 0; i < available && i + monitorOffset < lines.Count; i++)
        {
            var line = lines[i + monitorOffset]; Surface.Label(x + 2, 5 + i, line, monitorRect.Width - 4, line.Contains("FEHLER", StringComparison.OrdinalIgnoreCase) || line.Contains("IGNORIERT", StringComparison.Ordinal) ? TuiCanvas.Yellow : TuiCanvas.Green);
        }
        var splitter = new TuiRect(mainWidth, 0, 1, height - 3); Surface.Canvas.Fill(splitter, '│', TuiCanvas.Muted);
        Surface.Custom("monitor-resize", splitter, input => { if (input.Key == 37) monitorWidth += 2; if (input.Key == 39) monitorWidth -= 2; }, input => { if ((input.Buttons & 1) != 0) dragMonitor = true; });
    }
    internal void Handle(TerminalInput input)
    {
        try
        {
            var oldFocus = Surface.FocusId; var wasEditing = Surface.Editing;
            if (input.Mouse)
            {
                if (input.Buttons == 0 && input.Wheel == 0) { leftHeld = false; dragMonitor = false; dragColumn = -1; return; }
                if (popups.Count == 0 && input.Y >= height - 3) return;
                if (popups.TryPeek(out var popup) && (input.Y <= popupBounds.Y || input.Y >= popupBounds.Bottom - 1 || !popupBounds.Contains(input.X, input.Y))) return;
                if (input.Move)
                {
                    if (dragMonitor && (input.Buttons & 1) != 0) monitorWidth = Math.Clamp(width - input.X - 1, 30, Math.Max(30, width - 65));
                    if (dragColumn >= 0 && (input.Buttons & 1) != 0) { columnWidths[dragColumn] = Math.Clamp(columnWidths[dragColumn] + input.X - dragX, 8, 100); dragX = input.X; }
                    return;
                }
                if ((input.Buttons & 1) != 0 && leftHeld) return; leftHeld = (input.Buttons & 1) != 0;
                if (popups.TryPeek(out var activePopup) && activePopup.Kind is "option" or "adapter" && input.Wheel != 0) activePopup.ViewOffset = Math.Clamp(activePopup.ViewOffset - input.Wheel * 3, 0, Math.Max(0, 34 - popupBounds.Height));
                else if (popups.Count == 0 && input.Wheel != 0 && input.X < mainWidth && !tableRect.Contains(input.X, input.Y)) mainOffset = Math.Clamp(mainOffset - input.Wheel * 3, 0, Math.Max(0, mainHeight - height + 3));
                else Surface.Handle(input);
            }
            else if (input.Key == 121 || input.Control && input.Key == 81) exit = true;
            else if (input.Key == 112) Message("Bedienung", "Pfeiltasten: Felder und Buttons auswählen\nEnter: Eingabefeld aktivieren, Eingabe übernehmen\nEscape: Eingabe verwerfen\nMehrzeilige Eingabe: Ctrl+Enter übernimmt\nEnter / Leertaste: Button oder Dropdown öffnen\nCtrl+A / Ctrl+V: Auswählen / Einfügen\nRechtsklick auf Tabellenwert: Kopieren\nPfeiltasten / Bild auf, ab: Tabelle und Monitor\nSpaltenkopf / Monitor-Trennlinie ziehen: Breite ändern\nOptionstitel anklicken: Code, Format, Aktivierung bearbeiten\nEscape: Dialog schließen · F10 / Ctrl+Q: Programm beenden");
            else if (input.Key == 27 && Surface.EndInteraction(true)) SaveDraft();
            else if (input.Key == 27 && popups.Count > 0) ClosePopup();
            else if (input.Key == 27) Notify("Zum Beenden F10 oder Ctrl+Q drücken.");
            else Surface.Handle(input);
            if (wasEditing && !Surface.Editing) SaveDraft();
            if (Surface.FocusId != oldFocus)
            {
                SaveDraft();
                if (popups.Count == 0 && Surface.Focused is { } focus && focus.Rect.X < mainWidth)
                {
                    if (focus.Rect.Y < 0) mainOffset = Math.Max(0, mainOffset + focus.Rect.Y - 1);
                    if (focus.Rect.Bottom > height - 3) mainOffset += focus.Rect.Bottom - height + 4;
                }
                else if (popups.TryPeek(out var activePopup) && activePopup.Kind is "option" or "adapter" && Surface.Focused is { } popupFocus)
                {
                    if (popupFocus.Rect.Y <= popupBounds.Y) activePopup.ViewOffset = Math.Max(0, activePopup.ViewOffset + popupFocus.Rect.Y - popupBounds.Y - 1);
                    if (popupFocus.Rect.Bottom >= popupBounds.Bottom) activePopup.ViewOffset = Math.Min(34 - popupBounds.Height, activePopup.ViewOffset + popupFocus.Rect.Bottom - popupBounds.Bottom + 1);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
        { Notify(ex.Message, true); if (popups.TryPeek(out var popup)) popup.Error = ex.Message; }
    }
    public void Dispose() { SaveDraft(); server.StopAsync().GetAwaiter().GetResult(); }

    private sealed class Popup(string title)
    {
        public string Title = title, Kind = "message", Message = "", Error = "";
        public string? ReturnFocus;
        public TuiField Text = new(), Code = new("15"), Value = new();
        public TuiField Address = new(), Mask = new(), Gateway = new(), Dns = new();
        public bool Dhcp;
        public string[] Items = [];
        public int Selected, Scroll, ViewOffset;
        public Action<int>? Pick;
        public Action<string>? Save;
        public ExtraOption? Original;
        public string Format = "Text (ASCII)";
        public bool Always, Enabled = true;
    }
    private void Push(Popup popup) { Surface.EndInteraction(); popup.ReturnFocus = Surface.FocusId; popups.Push(popup); Surface.FocusId = null; }
    private void ClosePopup() { Surface.EndInteraction(); if (popups.TryPop(out var popup)) Surface.FocusId = popup.ReturnFocus; }
    private void Message(string title, string text) => Push(new(title) { Message = text });
    private void Pick(string title, string[] items, Action<int> pick) => Push(new(title) { Kind = "picker", Items = items, Pick = pick });
    private void OpenSaveRange()
    {
        var c = ReadConfiguration(); c.Validate(adapter!);
        Push(new("Bereich speichern") { Kind = "name", Text = new(selectedRange ?? $"{c.StartAddress} – {c.EndAddress}"), Save = name =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Bitte einen Namen eingeben.");
            Store.SaveRange(name, c); Store.SaveConfiguration(c); selectedRange = name.Trim(); RefreshRanges(); Notify($"Bereich „{selectedRange}“ gespeichert."); ClosePopup();
        }});
    }
    private void OpenOption(ExtraOption? option)
    {
        var popup = new Popup(option is null ? "Option hinzufügen" : "Option bearbeiten") { Kind = "option", Original = option, Code = new((option?.Code ?? 15).ToString(CultureInfo.InvariantCulture)), Value = new(option?.Value ?? ""), Format = option?.Format ?? "Text (ASCII)", Always = option?.AlwaysSend ?? false, Enabled = option?.Enabled ?? true };
        popup.Code.Changed = text => { if (byte.TryParse(text, out var code)) popup.Format = OptionCodec.DefaultFormat(code); };
        Push(popup);
    }
    private void SaveOption(Popup popup)
    {
        if (server.IsRunning) return;
        if (!byte.TryParse(popup.Code.Text, out var code) || code is 0 or 255) throw new ArgumentException("Optionscode muss zwischen 1 und 254 liegen.");
        if (options.Any(o => o.Code == code && o.Code != popup.Original?.Code)) throw new ArgumentException("Optionscode existiert bereits.");
        var option = new ExtraOption { Code = code, Name = catalog.FirstOrDefault(c => c.Code == code)?.Name ?? $"Option {code}", Format = popup.Format, Value = popup.Value.Text.Trim(), AlwaysSend = popup.Always, Enabled = popup.Enabled };
        OptionCodec.Encode(option);
        var index = popup.Original is null ? -1 : options.FindIndex(o => o.Code == popup.Original.Code);
        if (index >= 0) { options[index] = option; fields.Remove("option:" + popup.Original!.Code); } else options.Add(option);
        fields.Remove("option:" + code); dirty = true; SaveDraft(); ClosePopup();
    }
    private void DrawPopup(Popup popup)
    {
        Surface.Controls.Clear(); Surface.OffsetY = 0; Surface.Canvas.Clip = new(0, 0, width, height);
        var w = Math.Min(width - 4, 88); var h = popup.Kind == "picker" ? Math.Min(height - 4, 26) : popup.Kind is "option" or "adapter" ? 34 : Math.Min(height - 4, popup.Kind == "name" ? 14 : 20);
        var visibleHeight = Math.Min(height - 4, h);
        var x = (width - w) / 2; var y = (height - visibleHeight) / 2; popupBounds = new(x, y, w, visibleHeight);
        Surface.Box(x, y, w, visibleHeight, popup.Title, TuiCanvas.Normal);
        Surface.Canvas.Clip = new(x + 1, y + 1, w - 2, visibleHeight - 2); Surface.OffsetY = popup.ViewOffset;
        if (popup.Kind == "picker")
        {
            Surface.Label(x + 2, y + 2, "Suchen:"); Surface.Field("popup-filter", x + 10, y + 1, w - 12, popup.Text);
            var matches = popup.Items.Select((value, index) => (value, index)).Where(v => v.value.Contains(popup.Text.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            popup.Selected = Math.Clamp(popup.Selected, 0, Math.Max(0, matches.Length - 1)); var visible = h - 9;
            popup.Scroll = Math.Clamp(popup.Scroll, Math.Max(0, popup.Selected - visible + 1), popup.Selected);
            void Select() { if (matches.Length == 0) return; var index = matches[popup.Selected].index; var action = popup.Pick; ClosePopup(); action?.Invoke(index); }
            var rect = new TuiRect(x + 2, y + 5, w - 4, visible);
            var list = Surface.Custom("popup-list", rect, input =>
            {
                if (input.Key == 38) popup.Selected--; if (input.Key == 40) popup.Selected++;
                if (input.Key == 33) popup.Selected -= visible; if (input.Key == 34) popup.Selected += visible;
                if (input.Key == 36) popup.Selected = 0; if (input.Key == 35) popup.Selected = matches.Length - 1;
                if (input.Key == 13) Select();
            }, input => { if (input.Wheel != 0) popup.Selected -= input.Wheel * 3; else if ((input.Buttons & 1) != 0) { popup.Selected = popup.Scroll + input.Y - rect.Y; if (popup.Selected < matches.Length) Select(); } });
            for (var i = 0; i < visible && popup.Scroll + i < matches.Length; i++) Surface.Label(rect.X, rect.Y + i, matches[popup.Scroll + i].value, rect.Width, popup.Selected == popup.Scroll + i ? TuiCanvas.Focus : TuiCanvas.Normal);
            Surface.Button("popup-ok", x + w - 30, y + h - 4, 13, "Übernehmen", Select, matches.Length != 0);
            Surface.Button("popup-cancel", x + w - 16, y + h - 4, 13, "Abbrechen", ClosePopup);
        }
        else if (popup.Kind == "name")
        {
            Surface.Label(x + 2, y + 2, "Name des Bereichs"); Surface.Field("popup-name", x + 2, y + 3, w - 4, popup.Text);
            Surface.Label(x + 2, y + 7, "Vorhandene Namen werden aktualisiert.", w - 4);
            Surface.Button("popup-save", x + w - 30, y + h - 4, 13, "Speichern", () => popup.Save?.Invoke(popup.Text.Text));
            Surface.Button("popup-cancel", x + w - 16, y + h - 4, 13, "Abbrechen", ClosePopup);
        }
        else if (popup.Kind == "adapter")
        {
            Surface.CheckBox("adapter-dhcp", x + 2, y + 2, "IPv4 automatisch per DHCP beziehen", popup.Dhcp, () => popup.Dhcp = !popup.Dhcp);
            Surface.Label(x + 2, y + 4, "IPv4-Adresse"); Surface.Field("adapter-address", x + 2, y + 5, w - 4, popup.Address, !popup.Dhcp);
            Surface.Label(x + 2, y + 9, "Subnetzmaske"); Surface.Field("adapter-mask", x + 2, y + 10, w - 4, popup.Mask, !popup.Dhcp);
            Surface.Label(x + 2, y + 14, "Gateway (optional)"); Surface.Field("adapter-gateway", x + 2, y + 15, w - 4, popup.Gateway, !popup.Dhcp);
            Surface.Label(x + 2, y + 19, "DNS-Adressen, kommagetrennt · leer = automatisch"); Surface.Field("adapter-dns", x + 2, y + 20, w - 4, popup.Dns);
            Surface.Label(x + 2, y + 25, "Benötigt Administratorrechte in der aktuellen Sitzung.", w - 4, TuiCanvas.Yellow);
            Surface.Label(x + 2, y + 26, "Eine IP-Änderung kann die SSH-Verbindung unterbrechen.", w - 4, TuiCanvas.Yellow);
            Surface.Button("popup-save", x + w - 30, y + h - 4, 13, "Übernehmen", () => SaveAdapter(popup));
            Surface.Button("popup-cancel", x + w - 16, y + h - 4, 13, "Abbrechen", ClosePopup);
        }
        else if (popup.Kind == "option")
        {
            var readOnly = server.IsRunning;
            Surface.Label(x + 2, y + 2, "IANA-Optionskatalog");
            var current = byte.TryParse(popup.Code.Text, out var code) ? catalog.FirstOrDefault(c => c.Code == code) : null;
            Surface.Choice("popup-catalog", x + 2, y + 3, w - 4, current?.Display ?? "Eigener Optionscode", () => Pick("IANA-Optionskatalog", catalog.Select(d => d.Display + (d.Managed ? " · automatisch / clientseitig" : "")).ToArray(), i => { popup.Code.Set(catalog[i].Code.ToString(CultureInfo.InvariantCulture)); popup.Format = catalog[i].Format; }), !readOnly);
            Surface.Label(x + 2, y + 7, "Optionscode"); Surface.Field("popup-code", x + 2, y + 8, 14, popup.Code, !readOnly);
            Surface.Label(x + 18, y + 7, "Datenformat"); Surface.Choice("popup-format", x + 18, y + 8, w - 20, popup.Format, () => Pick("Datenformat", OptionCodec.Formats, i => popup.Format = OptionCodec.Formats[i]), !readOnly);
            Surface.Label(x + 2, y + 12, "Wert" + (popup.Format == "Routen" ? " · eine Route je Zeile" : "")); popup.Value.Multiline = popup.Format == "Routen";
            Surface.Field("popup-value", x + 2, y + 13, w - 4, popup.Value, !readOnly, Math.Max(3, Math.Min(7, h - 23)));
            var helpY = y + h - 12;
            var description = current?.Description ?? "Eigener Optionscode. Format und Kodierung anhand der Gerätespezifikation prüfen.";
            var lines = TuiCanvas.Wrap(description, w - 4); for (var i = 0; i < Math.Min(2, lines.Count); i++) Surface.Label(x + 2, helpY + i, lines[i], w - 4, TuiCanvas.Muted);
            Surface.CheckBox("popup-always", x + 2, y + h - 9, "Auch ohne Client-Anfrage senden", popup.Always, () => popup.Always = !popup.Always, !readOnly);
            Surface.CheckBox("popup-enabled", x + 2, y + h - 7, "Option aktiv", popup.Enabled, () => popup.Enabled = !popup.Enabled, !readOnly);
            Surface.Button("popup-help", x + 2, y + h - 4, 5, "?", () => Message("Option und Kodierung", description + "\nDatenformat: " + popup.Format + "\n" + (current?.Reference ?? "Herstellerspezifikation") + "\n\nIPv4-Liste: Adressen durch Komma trennen.\nRouten: eine Route je Zeile, z. B. 10.20.0.0/16=192.168.50.254\nDomainliste: Namen durch Komma trennen.\nHex: Bytes, z. B. 01 04 C0 A8 32 FE"));
            Surface.Button("popup-save", x + w - 30, y + h - 4, 13, "Übernehmen", () => SaveOption(popup), !readOnly);
            Surface.Button("popup-cancel", x + w - 16, y + h - 4, 13, "Schließen", ClosePopup);
        }
        else
        {
            var lines = TuiCanvas.Wrap(popup.Message, w - 4); for (var i = 0; i < Math.Min(h - 6, lines.Count); i++) Surface.Label(x + 2, y + 2 + i, lines[i], w - 4);
            Surface.Button("popup-close", x + w - 16, y + h - 4, 13, "Schließen", ClosePopup);
        }
        if (popup.Error.Length > 0) Surface.Label(x + 2, y + h - 5, popup.Error, w - 4, TuiCanvas.Red);
        Surface.OffsetY = 0; Surface.Canvas.Clip = new(0, 0, width, height);
    }
}
