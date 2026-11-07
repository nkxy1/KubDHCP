using System.Buffers.Binary;
using System.Net;

namespace KubDHCP.Core;

public enum DhcpMessageType : byte { Discover = 1, Offer = 2, Request = 3, Decline = 4, Ack = 5, Nak = 6, Release = 7, Inform = 8 }

public sealed class DhcpPacket
{
    public required byte[] Header { get; init; }
    public required Dictionary<byte, byte[]> Options { get; init; }
    public DhcpMessageType MessageType => (DhcpMessageType)Options[53][0];
    public uint ClientAddress => BinaryPrimitives.ReadUInt32BigEndian(Header.AsSpan(12));
    public uint RelayAddress => BinaryPrimitives.ReadUInt32BigEndian(Header.AsSpan(24));
    public bool Broadcast => (Header[10] & 0x80) != 0;
    public byte[] HardwareAddress => Header.AsSpan(28, Header[2]).ToArray();
    public string Mac => string.Join(":", HardwareAddress.Select(b => b.ToString("X2")));
    public string ClientKey => Options.TryGetValue(61, out var id) ? "ID:" + Convert.ToHexString(id) : $"HW:{Header[1]}:" + Convert.ToHexString(HardwareAddress);
    public uint? IpOption(byte code) => Options.TryGetValue(code, out var b) && b.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(b) : null;
    public string Hostname
    {
        get
        {
            // RFC 4702: prefer Client FQDN (81), supporting DNS labels and legacy text.
            if (Options.TryGetValue(81, out var fqdn) && fqdn.Length > 3)
            {
                var name = (fqdn[0] & 4) == 0 ? ReadName(fqdn.AsSpan(3)) : ReadDnsName(fqdn.AsSpan(3));
                if (name.Length != 0) return name;
            }
            return Options.TryGetValue(12, out var host) ? ReadName(host) : "";
        }
    }
    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var name = System.Text.Encoding.UTF8.GetString(bytes).Trim().TrimEnd('\0', '.');
        return name.Length > 253 || name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? "" : name;
    }
    private static string ReadDnsName(ReadOnlySpan<byte> bytes)
    {
        var labels = new List<string>(); var offset = 0;
        while (offset < bytes.Length)
        {
            var length = bytes[offset++];
            if (length == 0) return offset == bytes.Length ? ReadName(System.Text.Encoding.UTF8.GetBytes(string.Join(".", labels))) : "";
            if (length > 63 || offset + length > bytes.Length) return "";
            var label = ReadName(bytes.Slice(offset, length));
            if (label.Length == 0 || label.Contains('.')) return "";
            labels.Add(label); offset += length;
        }
        return ReadName(System.Text.Encoding.UTF8.GetBytes(string.Join(".", labels)));
    }
    public static bool TryParse(ReadOnlySpan<byte> data, out DhcpPacket? packet)
    {
        packet = null;
        if (data.Length is < 240 or > 4096 || data[0] != 1 || data[1] != 1 || data[2] != 6 || !data.Slice(236, 4).SequenceEqual(new byte[] { 99, 130, 83, 99 })) return false;
        var options = new Dictionary<byte, byte[]>();
        if (!ReadOptions(data[240..], options)) return false;
        if (options.TryGetValue(52, out var overload))
        {
            if (overload.Length != 1 || overload[0] is < 1 or > 3) return false;
            if ((overload[0] & 1) != 0 && !ReadOptions(data.Slice(108, 128), options)) return false;
            if ((overload[0] & 2) != 0 && !ReadOptions(data.Slice(44, 64), options)) return false;
        }
        if (!options.TryGetValue(53, out var type) || type.Length != 1 || type[0] is < 1 or > 8) return false;
        foreach (var code in new byte[] { 50, 51, 54 }) if (options.TryGetValue(code, out var v) && v.Length != 4) return false;
        if (options.TryGetValue(57, out var max) && max.Length != 2) return false;
        if (options.TryGetValue(61, out var clientId) && clientId.Length < 2) return false;
        packet = new() { Header = data[..236].ToArray(), Options = options }; return true;
    }
    private static bool ReadOptions(ReadOnlySpan<byte> data, Dictionary<byte, byte[]> options)
    {
        var index = 0;
        while (index < data.Length)
        {
            var code = data[index++]; if (code == 255) return true; if (code == 0) continue;
            if (index >= data.Length) return false; var length = data[index++]; if (index + length > data.Length) return false;
            var value = data.Slice(index, length).ToArray(); index += length;
            if (options.TryGetValue(code, out var previous)) options[code] = [.. previous, .. value]; else options.Add(code, value);
        }
        return false;
    }
}

public sealed record DhcpReply(DhcpMessageType Type, uint Address, uint LeaseSeconds = 0);

public static class DhcpPacketWriter
{
    public static byte[] Build(DhcpPacket request, DhcpReply reply, ServerConfiguration configuration)
    {
        var result = new List<byte>(576); var header = new byte[240];
        request.Header.CopyTo(header, 0); header[0] = 2; header[3] = 0; header[8] = header[9] = 0;
        header.AsSpan(44, 192).Clear(); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), reply.Address);
        header.AsSpan(20, 4).Clear(); // No implicit TFTP/boot server is provided by KubDHCP.
        if (reply.Type == DhcpMessageType.Nak) { header.AsSpan(12, 12).Clear(); header[10] = 0x80; header[11] = 0; }
        new byte[] { 99, 130, 83, 99 }.CopyTo(header, 236); result.AddRange(header);
        Add(result, 53, [(byte)reply.Type]); Add(result, 54, OptionCodec.IpBytes(configuration.ServerAddress));
        if (request.Options.TryGetValue(61, out var id)) Add(result, 61, id);
        if (reply.Type != DhcpMessageType.Nak)
        {
            Add(result, 1, OptionCodec.IpBytes(configuration.Mask));
            if (!string.IsNullOrWhiteSpace(configuration.Gateway)) Add(result, 3, OptionCodec.IpBytes(configuration.Gateway));
            var dns = new[] { configuration.PrimaryDns, configuration.SecondaryDns }.Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany(OptionCodec.IpBytes).ToArray();
            if (dns.Length > 0) Add(result, 6, dns);
            if (request.MessageType != DhcpMessageType.Inform)
            {
                Add(result, 51, OptionCodec.UInt32Bytes(reply.LeaseSeconds));
                if (reply.LeaseSeconds >= 4)
                {
                    Add(result, 58, OptionCodec.UInt32Bytes(reply.LeaseSeconds / 2));
                    Add(result, 59, OptionCodec.UInt32Bytes((uint)((ulong)reply.LeaseSeconds * 7 / 8)));
                }
            }
            request.Options.TryGetValue(55, out var requested);
            foreach (var option in configuration.Options.Where(o => o.Enabled))
                if (option.AlwaysSend || requested?.Contains(option.Code) == true) Add(result, option.Code, OptionCodec.Encode(option));
        }
        result.Add(255);
        while (result.Count < 300) result.Add(0);
        var maximum = request.Options.TryGetValue(57, out var size) ? Math.Clamp(BinaryPrimitives.ReadUInt16BigEndian(size) - 28, 548, 4096) : 548;
        if (result.Count > maximum) throw new InvalidOperationException($"DHCP-Antwort ({result.Count} Bytes) überschreitet die vom Client erlaubte Größe ({maximum} Bytes). Zusätzliche Optionen verkleinern.");
        return result.ToArray();
    }
    private static void Add(List<byte> output, byte code, byte[] value)
    {
        for (var offset = 0; offset < value.Length; offset += 255)
        {
            var count = Math.Min(255, value.Length - offset); output.Add(code); output.Add((byte)count); output.AddRange(value.AsSpan(offset, count).ToArray());
        }
    }
}
