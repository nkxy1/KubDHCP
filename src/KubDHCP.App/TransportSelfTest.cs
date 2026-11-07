using System.Net;
using System.Net.Sockets;
using KubDHCP.Core;

namespace KubDHCP.App;

internal static class TransportSelfTest
{
    // Uses only loopback and ephemeral UDP ports; never opens a DHCP server on the LAN.
    public static int Run(string reportPath, Action<string>? networkActivity = null)
    {
        try
        {
            using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0)); receiver.ReceiveTimeout = 3000;
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var loopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(a => a.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback).GetIPProperties().GetIPv4Properties().Index;
            var native = new NativeUdpSender(sender, loopback, "127.0.0.1");
            native.Send([0x4b, 0x75, 0x62], IPAddress.Loopback, (ushort)((IPEndPoint)receiver.LocalEndPoint!).Port);
            var data = new byte[20]; EndPoint remote = new IPEndPoint(IPAddress.Any, 0); var flags = SocketFlags.None;
            var length = receiver.ReceiveMessageFrom(data, 0, data.Length, ref flags, ref remote, out var info);
            if (length != 3 || data[0] != 0x4b || info.Interface != loopback) throw new InvalidOperationException("Payload oder Interface-Index stimmt nicht.");
            // Send actual DHCP payloads through the same native send path, using only ephemeral loopback ports.
            sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true); sender.ReceiveTimeout = 3000;
            var configuration = new ServerConfiguration { ServerAddress = "192.168.50.1", StartAddress = "192.168.50.100", EndAddress = "192.168.50.150", Mask = "255.255.255.0" };
            var store = new DataStore(Path.Combine(Path.GetDirectoryName(reportPath)!, "transport-data-" + Guid.NewGuid().ToString("N")));
            var engine = new DhcpEngine(configuration, store);
            var discover = MakeRequest(1);
            receiver.SendTo(discover, sender.LocalEndPoint!);
            var packetBuffer = new byte[4096]; EndPoint client = new IPEndPoint(IPAddress.Any, 0); flags = SocketFlags.None;
            length = sender.ReceiveMessageFrom(packetBuffer, 0, packetBuffer.Length, ref flags, ref client, out var incoming);
            if (incoming.Interface != loopback || !DhcpPacket.TryParse(packetBuffer.AsSpan(0, length), out var request)) throw new InvalidOperationException("DISCOVER-Empfang fehlgeschlagen.");
            networkActivity?.Invoke("LOOPBACK-TEST · ANFRAGE · " + NetworkMonitorText.Request(request!));
            var offer = engine.Handle(request!, DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("Kein OFFER.");
            var offerBytes = DhcpPacketWriter.Build(request!, offer, configuration);
            native.Send(offerBytes, IPAddress.Loopback, (ushort)((IPEndPoint)receiver.LocalEndPoint!).Port);
            networkActivity?.Invoke("LOOPBACK-TEST · ANTWORT · " + NetworkMonitorText.Response(request!, offer, offerBytes));
            ValidateReply(receiver, packetBuffer, (byte)DhcpMessageType.Offer, loopback);
            receiver.SendTo(MakeRequest(3, offer.Address, configuration.ServerAddress), sender.LocalEndPoint!);
            length = sender.ReceiveMessageFrom(packetBuffer, 0, packetBuffer.Length, ref flags, ref client, out incoming);
            if (incoming.Interface != loopback || !DhcpPacket.TryParse(packetBuffer.AsSpan(0, length), out request)) throw new InvalidOperationException("REQUEST-Empfang fehlgeschlagen.");
            networkActivity?.Invoke("LOOPBACK-TEST · ANFRAGE · " + NetworkMonitorText.Request(request!));
            var ack = engine.Handle(request!, DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("Kein ACK.");
            var ackBytes = DhcpPacketWriter.Build(request!, ack, configuration);
            native.Send(ackBytes, IPAddress.Loopback, (ushort)((IPEndPoint)receiver.LocalEndPoint!).Port);
            networkActivity?.Invoke("LOOPBACK-TEST · ANTWORT · " + NetworkMonitorText.Response(request!, ack, ackBytes));
            ValidateReply(receiver, packetBuffer, (byte)DhcpMessageType.Ack, loopback);
            if (store.LoadLeases().Count != 1) throw new InvalidOperationException("Bestätigter Lease wurde nicht persistiert.");
            File.WriteAllText(reportPath, $"PASS: WSASendMsg/IP_PKTINFO; arrival interface {info.Interface}; complete UDP DISCOVER/OFFER/REQUEST/ACK; durable lease; loopback and ephemeral ports only."); return 0;
        }
        catch (Exception ex) { File.WriteAllText(reportPath, ex.ToString()); return 1; }
    }
    private static byte[] MakeRequest(byte type, uint requested = 0, string? server = null)
    {
        var header = new byte[240]; header[0] = 1; header[1] = 1; header[2] = 6; header[7] = 42; header[10] = 0x80;
        new byte[] { 0xb8, 0x27, 0xeb, 1, 2, 3 }.CopyTo(header, 28); new byte[] { 99, 130, 83, 99 }.CopyTo(header, 236);
        var data = header.ToList(); data.AddRange([53, 1, type]);
        if (requested != 0) { data.AddRange([50, 4]); data.AddRange(OptionCodec.UInt32Bytes(requested)); }
        if (server is not null) { data.AddRange([54, 4]); data.AddRange(OptionCodec.IpBytes(server)); }
        data.Add(255); return data.ToArray();
    }
    private static void ValidateReply(Socket socket, byte[] buffer, byte expected, int loopback)
    {
        EndPoint source = new IPEndPoint(IPAddress.Any, 0); var flags = SocketFlags.None;
        var length = socket.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref source, out var info);
        if (length < 240 || buffer[0] != 2 || info.Interface != loopback) throw new InvalidOperationException("Ungültige Serverantwort oder Eingangsschnittstelle.");
        buffer[0] = 1; // Reuse the strict request parser to inspect the response's TLV options.
        if (!DhcpPacket.TryParse(buffer.AsSpan(0, length), out var reply) || (byte)reply!.MessageType != expected) throw new InvalidOperationException("Unerwarteter DHCP-Nachrichtentyp.");
    }
}
