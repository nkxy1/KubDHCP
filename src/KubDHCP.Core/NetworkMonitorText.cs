using System.Buffers.Binary;

namespace KubDHCP.Core;

/// <summary>Readable summaries of the packets handled by this DHCP server.</summary>
public static class NetworkMonitorText
{
    public static string Client(DhcpPacket packet) => (Localizer.IsGerman ? $"Client {packet.Mac} · Transaktion {Convert.ToHexString(packet.Header.AsSpan(4, 4))}" : $"Client {packet.Mac} · transaction {Convert.ToHexString(packet.Header.AsSpan(4, 4))}") + (packet.Hostname.Length == 0 ? "" : $" · Hostname {packet.Hostname}");
    public static string Request(DhcpPacket packet) => $"{packet.MessageType.ToString().ToUpperInvariant()} · {Client(packet)} · {Meaning(packet.MessageType)}"
        + (packet.ClientAddress != 0 ? (Localizer.IsGerman ? $" · aktuelle IP {Ipv4.Format(packet.ClientAddress)}" : $" · current IP {Ipv4.Format(packet.ClientAddress)}") : "")
        + (Localizer.IsGerman ? $" · Optionen: {Options(packet.Options)}" : $" · options: {Options(packet.Options)}");

    public static string Response(DhcpPacket request, DhcpReply reply, byte[] payload)
    {
        // The parser's BOOTREQUEST restriction is only adapted on our own outgoing packet copy.
        var copy = payload.ToArray(); copy[0] = 1;
        var options = DhcpPacket.TryParse(copy, out var parsed) ? Options(parsed!.Options) : Localizer.Text("nicht dekodierbar");
        return $"{reply.Type.ToString().ToUpperInvariant()} · {Client(request)} · {Meaning(reply.Type)}"
            + (reply.Address != 0 ? $" · IP {Ipv4.Format(reply.Address)}" : "") + (Localizer.IsGerman ? $" · Optionen: {options}" : $" · options: {options}");
    }
    public static string NoReply(DhcpPacket packet, string serverAddress) => packet.MessageType switch
    {
        DhcpMessageType.Release => Localizer.Text("RELEASE verarbeitet, falls ein passender Lease besteht; keine Antwort vorgesehen."),
        DhcpMessageType.Decline => Localizer.Text("DECLINE geprüft; passende angebotene/aktive Adresse wird gesperrt. Keine Antwort vorgesehen."),
        DhcpMessageType.Discover => Localizer.Text("Kein Angebot möglich: kein freier oder konfliktfreier Eintrag im Adresspool."),
        DhcpMessageType.Request when packet.IpOption(54) is { } server && server != Ipv4.Parse(serverAddress)
            => (Localizer.IsGerman ? $"Anfrage richtet sich an DHCP-Server {Ipv4.Format(server)}; keine Antwort von diesem Server." : $"Request is addressed to DHCP server {Ipv4.Format(server)}; this server will not reply."),
        _ => Localizer.Text("Keine Antwort: Anfrage passt nicht zu unserem Subnetz, Nachrichtentyp oder einer gültigen Reservierung/einem bekannten Lease.")
    };
    private static string Meaning(DhcpMessageType type) => type switch
    {
        DhcpMessageType.Discover => Localizer.Text("sucht eine Adresse"), DhcpMessageType.Request => Localizer.Text("fordert/bestätigt/erneuert eine Adresse"),
        DhcpMessageType.Release => Localizer.Text("gibt seine Adresse frei"), DhcpMessageType.Decline => Localizer.Text("meldet einen Adresskonflikt"),
        DhcpMessageType.Inform => Localizer.Text("fragt Konfiguration ohne neue Adresse an"), DhcpMessageType.Offer => Localizer.Text("Adresse angeboten"),
        DhcpMessageType.Ack => Localizer.Text("Anfrage bestätigt"), DhcpMessageType.Nak => Localizer.Text("Adressanfrage abgelehnt"), _ => Localizer.Text("DHCP-Nachricht")
    };
    public static string Options(IReadOnlyDictionary<byte, byte[]> options) => string.Join("; ", options.OrderBy(o => o.Key).Where(o => o.Key != 53).Select(o => $"{OptionName(o.Key)}={Value(o.Key, o.Value)}"));
    private static string OptionName(byte code) => code switch
    {
        1 => Localizer.Text("Subnetzmaske"), 3 => "Gateway", 6 => "DNS", 12 => "Hostname", 15 => Localizer.Text("Domäne"), 50 => Localizer.Text("gewünschte IP"),
        51 => Localizer.Text("Lease-Dauer"), 54 => "DHCP-Server", 55 => Localizer.Text("angefragte Optionen"), 57 => Localizer.Text("max. Nachrichtengröße"),
        58 => Localizer.Text("Erneuerung nach"), 59 => Localizer.Text("Neubindung nach"), 61 => Localizer.Text("Client-ID"), 66 => "Bootserver", 67 => Localizer.Text("Bootdatei"), _ => $"Option {code}"
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
