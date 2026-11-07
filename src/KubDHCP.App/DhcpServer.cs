using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using KubDHCP.Core;

namespace KubDHCP.App;

/// <summary>Windows transport: checks the arrival interface and pins every reply's source/interface.</summary>
internal sealed class DhcpServer : IAsyncDisposable
{
    private Socket? socket;
    private CancellationTokenSource? cancellation;
    private Task? receiveTask, monitorTask;
    private DhcpEngine? engine;
    private NativeUdpSender? sender;
    private readonly DataStore store;
    public event Action? LeasesChanged;
    public event Action<string>? Status;
    public event Action<string>? NetworkActivity;
    public event Action<string>? Failed;
    public bool IsRunning => socket is not null && cancellation?.IsCancellationRequested == false;
    public IReadOnlyList<LeaseRecord> Leases => engine?.Snapshot() ?? [];
    public DhcpServer(DataStore store) => this.store = store;
    public bool RemoveLease(LeaseRecord lease)
    {
        // A receive task may still be winding down after a transport fault. Use its lock until StopAsync has joined it.
        var removed = receiveTask is not null && engine is not null ? engine.RemoveLease(lease) : store.RemoveLease(lease);
        if (removed)
        {
            Log((Localizer.IsGerman ? $"LEASE ENTFERNT · Client {lease.Mac} · IP {lease.Address} · Bereich {lease.Scope}" : $"LEASE REMOVED · Client {lease.Mac} · IP {lease.Address} · range {lease.Scope}"));
            LeasesChanged?.Invoke();
        }
        return removed;
    }

    public void Start(AdapterInfo adapter, ServerConfiguration configuration)
    {
        if (Program.DemoMode) throw new InvalidOperationException("DEMO: server start is disabled.");
        if (IsRunning) throw new InvalidOperationException(Localizer.Text("Der Server läuft bereits."));
        var current = AdapterInfo.Enumerate().FirstOrDefault(a => a.Key == adapter.Key) ?? throw new InvalidOperationException(Localizer.Text("Die gewählte LAN-Karte ist nicht mehr verfügbar."));
        configuration.Validate(current);
        var candidate = new DhcpEngine(configuration, store);
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true, EnableBroadcast = true, ReceiveBufferSize = 65536 };
        try
        {
            udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            udp.Bind(new IPEndPoint(IPAddress.Any, 67));
            sender = new NativeUdpSender(udp, current.InterfaceIndex, current.Address);
            store.SaveConfiguration(configuration);
        }
        catch { udp.Dispose(); throw; }
        engine = candidate; engine.Changed += () => LeasesChanged?.Invoke(); socket = udp; cancellation = new();
        var token = cancellation.Token;
        receiveTask = Task.Run(() => ReceiveAsync(current, configuration, udp, token));
        monitorTask = Task.Run(() => MonitorAsync(current, token));
        Log((Localizer.IsGerman ? $"Gestartet auf {current.Name} ({current.Address}), UDP 67. Initiale Antworten verwenden den RFC-2131-Broadcast-Fallback; Renewals sind Unicast." : $"Started on {current.Name} ({current.Address}), UDP 67. Initial replies use the RFC 2131 broadcast fallback; renewals use unicast."));
        Log((Localizer.IsGerman ? $"Pool {configuration.StartAddress} – {configuration.EndAddress} · Maske {configuration.Mask} · Lease {configuration.LeaseSeconds} s · Gateway {configuration.Gateway} · DNS {configuration.PrimaryDns} {configuration.SecondaryDns}" : $"Pool {configuration.StartAddress} – {configuration.EndAddress} · mask {configuration.Mask} · Lease {configuration.LeaseSeconds} s · Gateway {configuration.Gateway} · DNS {configuration.PrimaryDns} {configuration.SecondaryDns}"));
        LeasesChanged?.Invoke();
    }

    private async Task ReceiveAsync(AdapterInfo adapter, ServerConfiguration configuration, Socket udp, CancellationToken token)
    {
        var buffer = new byte[4096]; var client = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var received = await udp.ReceiveMessageFromAsync(buffer, SocketFlags.None, client, token);
                var source = received.RemoteEndPoint.ToString();
                Activity((Localizer.IsGerman ? $"EMPFANG UDP {source} → {configuration.ServerAddress}:67 · {received.ReceivedBytes} Bytes · Schnittstelle {received.PacketInformation.Interface}" : $"RECEIVE UDP {source} → {configuration.ServerAddress}:67 · {received.ReceivedBytes} bytes · interface {received.PacketInformation.Interface}"));
                // Log ignored packets as well; the acceptance rules remain unchanged.
                if (received.PacketInformation.Interface != adapter.InterfaceIndex || received.ReceivedBytes == 0 || received.RemoteEndPoint is not IPEndPoint remote || remote.Port != 68)
                { Activity((Localizer.IsGerman ? $"IGNORIERT · {source} · andere/unbekannte Schnittstelle, leerer Inhalt oder Quellport ungleich 68." : $"IGNORED · {source} · different/unknown interface, empty payload or source port other than 68.")); continue; }
                if (!DhcpPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var request) || request is null)
                { Activity((Localizer.IsGerman ? $"IGNORIERT · {source} · ungültiges oder unvollständiges DHCP-Paket." : $"IGNORED · {source} · invalid or incomplete DHCP packet.")); continue; }
                Activity(Localizer.Text("ANFRAGE · ") + NetworkMonitorText.Request(request));
                if (request.RelayAddress != 0)
                { Activity((Localizer.IsGerman ? $"IGNORIERT · {request.Mac} · DHCP-Relay {Ipv4.Format(request.RelayAddress)} wird nicht unterstützt." : $"IGNORED · {request.Mac} · DHCP-Relay {Ipv4.Format(request.RelayAddress)} is not supported.")); continue; }
                try
                {
                    var reply = engine!.Handle(request, DateTimeOffset.UtcNow);
                    if (reply?.Type == DhcpMessageType.Offer)
                    {
                        // A responding ARP owner is a conflict; silence is only a best-effort signal.
                        for (var tries = 0; reply is not null && tries < 16; tries++)
                        {
                            token.ThrowIfCancellationRequested();
                            Activity((Localizer.IsGerman ? $"ARP PRÜFUNG · {NetworkMonitorText.Client(request)} · Ist {Ipv4.Format(reply.Address)} bereits belegt? Quelle {configuration.ServerAddress}" : $"ARP CHECK · {NetworkMonitorText.Client(request)} · Is {Ipv4.Format(reply.Address)} already occupied? Source {configuration.ServerAddress}"));
                            var owner = NativeUdpSender.FindMac(reply.Address, configuration.ServerAddress);
                            Activity(owner is null ? (Localizer.IsGerman ? $"ARP ERGEBNIS · {Ipv4.Format(reply.Address)} · kein antwortender Besitzer ermittelt." : $"ARP RESULT · {Ipv4.Format(reply.Address)} · no responding owner found.")
                                : (Localizer.IsGerman ? $"ARP ERGEBNIS · {Ipv4.Format(reply.Address)} · Besitzer {string.Join(":", owner.Select(b => b.ToString("X2")))}" : $"ARP RESULT · {Ipv4.Format(reply.Address)} · owner {string.Join(":", owner.Select(b => b.ToString("X2")))}"));
                            if (owner is null || owner.SequenceEqual(request.HardwareAddress)) break;
                            engine.Quarantine(reply.Address, DateTimeOffset.UtcNow); Log((Localizer.IsGerman ? $"Adresskonflikt: {Ipv4.Format(reply.Address)} für 10 Minuten gesperrt." : $"Address conflict: {Ipv4.Format(reply.Address)} quarantined for 10 minutes."));
                            reply = tries == 15 ? null : engine.Handle(request, DateTimeOffset.UtcNow);
                        }
                    }
                    if (reply is null) { Activity((Localizer.IsGerman ? $"KEINE ANTWORT · {NetworkMonitorText.Client(request)} · {NetworkMonitorText.NoReply(request, configuration.ServerAddress)}" : $"NO REPLY · {NetworkMonitorText.Client(request)} · {NetworkMonitorText.NoReply(request, configuration.ServerAddress)}")); continue; }
                    var bytes = DhcpPacketWriter.Build(request, reply, configuration);
                    var destination = reply.Type == DhcpMessageType.Nak || request.ClientAddress == 0 || request.Broadcast ? IPAddress.Broadcast : Ipv4.ToAddress(request.ClientAddress);
                    try { sender!.Send(bytes, destination, 68); }
                    catch (Exception ex) when (ex is SocketException or IOException)
                    { Activity((Localizer.IsGerman ? $"SENDEN FEHLGESCHLAGEN · {NetworkMonitorText.Client(request)} · {destination}:68 · {ex.Message}" : $"SEND FAILED · {NetworkMonitorText.Client(request)} · {destination}:68 · {ex.Message}")); throw; }
                    Log((Localizer.IsGerman ? $"ANTWORT UDP {configuration.ServerAddress}:67 → {destination}:68 · {bytes.Length} Bytes · {(destination.Equals(IPAddress.Broadcast) ? "Broadcast" : "Unicast")} · {NetworkMonitorText.Response(request, reply, bytes)}" : $"REPLY UDP {configuration.ServerAddress}:67 → {destination}:68 · {bytes.Length} Bytes · {(destination.Equals(IPAddress.Broadcast) ? "Broadcast" : "Unicast")} · {NetworkMonitorText.Response(request, reply, bytes)}"), $"{reply.Type.ToString().ToUpperInvariant()} · {request.Mac}" + (reply.Address == 0 ? "" : $" · {Ipv4.Format(reply.Address)}"));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Log((Localizer.IsGerman ? $"VERARBEITUNGSFEHLER · {NetworkMonitorText.Client(request)} · {ex.Message}" : $"PROCESSING ERROR · {NetworkMonitorText.Client(request)} · {ex.Message}")); }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception ex) { Fault((Localizer.IsGerman ? $"DHCP-Empfang beendet: {ex.Message}" : $"DHCP receive loop ended: {ex.Message}")); }
    }

    private async Task MonitorAsync(AdapterInfo adapter, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(token))
                if (!AdapterInfo.Enumerate().Any(a => a.Key == adapter.Key && a.InterfaceIndex == adapter.InterfaceIndex && a.Mask == adapter.Mask))
                { Fault(Localizer.Text("LAN-Karte getrennt oder IPv4-Konfiguration geändert. Server wurde gestoppt.")); return; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fault((Localizer.IsGerman ? $"Adapterüberwachung fehlgeschlagen: {ex.Message}" : $"Adapter monitoring failed: {ex.Message}")); }
    }
    private void Fault(string message)
    {
        cancellation?.Cancel(); socket?.Dispose(); Log(message); Failed?.Invoke(message);
    }
    private void Activity(string text) => NetworkActivity?.Invoke(text);
    private void Log(string text, string? status = null)
    {
        Activity(text);
        Status?.Invoke(status ?? text);
        try { File.AppendAllText(Path.Combine(store.DirectoryPath, "KubDHCP.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}"); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public async Task StopAsync()
    {
        var wasStarted = socket is not null;
        cancellation?.Cancel(); socket?.Dispose();
        if (receiveTask is not null) await receiveTask;
        if (monitorTask is not null) await monitorTask;
        socket = null; sender = null; cancellation?.Dispose(); cancellation = null; receiveTask = monitorTask = null;
        if (wasStarted) Log(Localizer.Text("Server gestoppt · UDP-Empfang beendet."));
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}

internal sealed class NativeUdpSender
{
    private readonly Socket socket;
    private readonly int interfaceIndex;
    private readonly uint sourceAddress;
    private readonly SendMsg send;
    [StructLayout(LayoutKind.Sequential)] private struct Buffer { public uint Length; public nint Pointer; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Name; public int NameLength; public nint Buffers; public uint BufferCount; public Buffer Control; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ControlHeader { public nuint Length; public int Level; public int Type; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)] private delegate int SendMsg(nint socket, ref Message message, uint flags, out uint sent, nint overlapped, nint completion);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int WSAIoctl(nint socket, uint code, ref Guid input, uint inputLength, out nint output, uint outputLength, out uint returned, nint overlapped, nint completion);
    [DllImport("iphlpapi.dll")] private static extern uint SendARP(uint destination, uint source, byte[] mac, ref uint length);

    public NativeUdpSender(Socket socket, int interfaceIndex, string source)
    {
        this.socket = socket; this.interfaceIndex = interfaceIndex; sourceAddress = BitConverter.ToUInt32(IPAddress.Parse(source).GetAddressBytes());
        var id = new Guid("a441e712-754f-43ca-84a7-0dee44cf606d");
        if (WSAIoctl(socket.Handle, 0xC8000006, ref id, 16, out var pointer, (uint)nint.Size, out _, 0, 0) != 0) throw new SocketException(Marshal.GetLastPInvokeError());
        send = Marshal.GetDelegateForFunctionPointer<SendMsg>(pointer);
    }
    public static byte[]? FindMac(uint ip, string source)
    {
        var bytes = new byte[8]; uint length = 8;
        return SendARP(BitConverter.ToUInt32(Ipv4.ToAddress(ip).GetAddressBytes()), BitConverter.ToUInt32(IPAddress.Parse(source).GetAddressBytes()), bytes, ref length) == 0 && length == 6 ? bytes[..6] : null;
    }
    public unsafe void Send(byte[] data, IPAddress destination, ushort port)
    {
        // Windows IN_PKTINFO: source IPv4 in network byte order, interface index in host byte order.
        Span<byte> address = stackalloc byte[16]; address.Clear(); address[0] = 2;
        address[2] = (byte)(port >> 8); address[3] = (byte)port; destination.GetAddressBytes().CopyTo(address[4..]);
        Span<byte> control = stackalloc byte[24]; control.Clear();
        var controlHeader = new ControlHeader { Length = 24, Level = 0, Type = 19 };
        MemoryMarshal.Write(control, in controlHeader); BitConverter.TryWriteBytes(control[16..], sourceAddress); BitConverter.TryWriteBytes(control[20..], interfaceIndex);
        fixed (byte* payload = data, target = address, ancillary = control)
        {
            var buffer = new Buffer { Length = (uint)data.Length, Pointer = (nint)payload };
            var message = new Message { Name = (nint)target, NameLength = 16, Buffers = (nint)(&buffer), BufferCount = 1, Control = new() { Length = 24, Pointer = (nint)ancillary } };
            if (send(socket.Handle, ref message, 0, out var sent, 0, 0) != 0) throw new SocketException(Marshal.GetLastPInvokeError());
            if (sent != data.Length) throw new IOException(Localizer.Text("DHCP-Antwort wurde nicht vollständig gesendet."));
        }
    }
}
