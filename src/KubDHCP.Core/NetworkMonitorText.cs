using System.Buffers.Binary;

namespace KubDHCP.Core;

/// <summary>Readable summaries of the packets handled by this DHCP server.</summary>
public static class NetworkMonitorText
{
    public static string Client(DhcpPacket packet) => $"Client {packet.Mac} · Transaktion {Convert.ToHexString(packet.Header.AsSpan(4, 4))}" + (packet.Hostname.Length == 0 ? "" : $" · Hostname {packet.Hostname}");
    public static string Request(DhcpPacket packet) => $"{packet.MessageType.ToString().ToUpperInvariant()} · {Client(packet)} · {Meaning(packet.MessageType)}"
        + (packet.ClientAddress != 0 ? $" · aktuelle IP {Ipv4.Format(packet.ClientAddress)}" : "")
        + $" · Optionen: {Options(packet.Options)}";

    public static string Response(DhcpPacket request, DhcpReply reply, byte[] payload)
    {
        // The parser's BOOTREQUEST restriction is only adapted on our own outgoing packet copy.
        var copy = payload.ToArray(); copy[0] = 1;
        var options = DhcpPacket.TryParse(copy, out var parsed) ? Options(parsed!.Options) : "nicht dekodierbar";
        return $"{reply.Type.ToString().ToUpperInvariant()} · {Client(request)} · {Meaning(reply.Type)}"
            + (reply.Address != 0 ? $" · IP {Ipv4.Format(reply.Address)}" : "") + $" · Optionen: {options}";
    }
    public static string NoReply(DhcpPacket packet, string serverAddress) => packet.MessageType switch
    {
        DhcpMessageType.Release => "RELEASE verarbeitet, falls ein passender Lease besteht; keine Antwort vorgesehen.",
        DhcpMessageType.Decline => "DECLINE geprüft; passende angebotene/aktive Adresse wird gesperrt. Keine Antwort vorgesehen.",
        DhcpMessageType.Discover => "Kein Angebot möglich: kein freier oder konfliktfreier Eintrag im Adresspool.",
        DhcpMessageType.Request when packet.IpOption(54) is { } server && server != Ipv4.Parse(serverAddress)
            => $"Anfrage richtet sich an DHCP-Server {Ipv4.Format(server)}; keine Antwort von diesem Server.",
        _ => "Keine Antwort: Anfrage passt nicht zu unserem Subnetz, Nachrichtentyp oder einer gültigen Reservierung/einem bekannten Lease."
    };
    private static string Meaning(DhcpMessageType type) => type switch
    {
        DhcpMessageType.Discover => "sucht eine Adresse", DhcpMessageType.Request => "fordert/bestätigt/erneuert eine Adresse",
        DhcpMessageType.Release => "gibt seine Adresse frei", DhcpMessageType.Decline => "meldet einen Adresskonflikt",
        DhcpMessageType.Inform => "fragt Konfiguration ohne neue Adresse an", DhcpMessageType.Offer => "Adresse angeboten",
        DhcpMessageType.Ack => "Anfrage bestätigt", DhcpMessageType.Nak => "Adressanfrage abgelehnt", _ => "DHCP-Nachricht"
    };
    public static string Options(IReadOnlyDictionary<byte, byte[]> options) => string.Join("; ", options.OrderBy(o => o.Key).Where(o => o.Key != 53).Select(o => $"{OptionName(o.Key)}={Value(o.Key, o.Value)}"));
    private static string OptionName(byte code) => code switch
    {
        1 => "Subnetzmaske", 3 => "Gateway", 6 => "DNS", 12 => "Hostname", 15 => "Domäne", 50 => "gewünschte IP",
        51 => "Lease-Dauer", 54 => "DHCP-Server", 55 => "angefragte Optionen", 57 => "max. Nachrichtengröße",
        58 => "Erneuerung nach", 59 => "Neubindung nach", 61 => "Client-ID", 66 => "Bootserver", 67 => "Bootdatei", _ => $"Option {code}"
    };
    private static string Value(byte code, byte[] bytes)
    {
        if (code is 1 or 3 or 6 or 50 or 54 && bytes.Length > 0 && bytes.Length % 4 == 0)
            return string.Join(", ", Enumerable.Range(0, bytes.Length / 4).Take(16).Select(i => Ipv4.Format(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4, 4)))));
        if (code is 51 or 58 or 59 && bytes.Length == 4) return BinaryPrimitives.ReadUInt32BigEndian(bytes) + " s";
        if (code == 57 && bytes.Length == 2) return BinaryPrimitives.ReadUInt16BigEndian(bytes) + " Bytes";
        if (code == 55) return string.Join(",", bytes);
        if (code is 12 or 15 or 66 or 67)
            return new string(bytes.Take(160).Select(b => b is >= 32 and <= 126 ? (char)b : '?').ToArray()) + (bytes.Length > 160 ? "…" : "");
        return Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length))) + (bytes.Length > 32 ? $"… ({bytes.Length} Bytes)" : "");
    }
}
