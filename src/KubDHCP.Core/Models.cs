using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KubDHCP.Core;

public sealed record AdapterInfo(string Id, string Name, string Description, int InterfaceIndex,
    string Address, string Mask, string? Gateway, string[] Dns, string[] LocalAddresses, bool DhcpEnabled)
{
    public string DisplayName => $"{Name} · {Address} · {Description}";
    public string Key => $"{Id}|{Address}";
    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var props = nic.GetIPProperties();
                var ipProps = props.GetIPv4Properties();
                var local = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToArray();
                foreach (var ip in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var mask = Ipv4.Parse(ip.IPv4Mask.ToString()); var network = Ipv4.Parse(ip.Address.ToString()) & mask;
                    var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && (Ipv4.Parse(a.ToString()) & mask) == network);
                    var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2).Select(a => a.ToString()).ToArray();
                    result.Add(new(nic.Id, nic.Name, nic.Description, ipProps.Index, ip.Address.ToString(), ip.IPv4Mask.ToString(), gateway?.ToString(), dns, local, OperatingSystem.IsWindows() && ipProps.IsDhcpEnabled));
                }
            }
            catch (NetworkInformationException) { /* An adapter may disappear during enumeration. */ }
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

public sealed record ExtraOption
{
    public byte Code { get; init; }
    public string Name { get; init; } = "";
    public string Format { get; init; } = "Hex";
    public string Value { get; init; } = "";
    public bool AlwaysSend { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record ServerConfiguration
{
    public string AdapterId { get; init; } = "";
    public string ServerAddress { get; init; } = "";
    public string StartAddress { get; init; } = "";
    public string EndAddress { get; init; } = "";
    public string Mask { get; init; } = "";
    public string Gateway { get; init; } = "";
    public string PrimaryDns { get; init; } = "";
    public string SecondaryDns { get; init; } = "";
    public uint LeaseSeconds { get; init; } = 3600;
    public List<ExtraOption> Options { get; init; } = [];
    public string Scope => $"{Ipv4.Network(ServerAddress, Mask)}/{Mask}";

    public static ServerConfiguration ForAdapter(AdapterInfo adapter)
    {
        var server = Ipv4.Parse(adapter.Address);
        var mask = Ipv4.Parse(adapter.Mask);
        var network = server & mask;
        var broadcast = network | ~mask;
        var excluded = adapter.LocalAddresses.Concat(adapter.Dns).Append(adapter.Gateway ?? "").Where(s => Ipv4.TryParse(s, out _)).Select(Ipv4.Parse).ToHashSet();
        uint start = 0, end = 0;
        if (broadcast > network + 2)
        {
            // Prefer a familiar .100-.150 block in the server's /24, while respecting the actual subnet.
            var preferred = (server & 0xffffff00) + 100;
            var candidates = new[] { preferred, network + 1 };
            foreach (var first in candidates)
            {
                if (first <= network || first >= broadcast) continue;
                var last = (uint)Math.Min((ulong)first + 50, (ulong)broadcast - 1);
                if (!excluded.Any(ip => ip >= first && ip <= last)) { start = first; end = last; break; }
            }
            if (start == 0)
            {
                var boundaries = excluded.Where(ip => ip > network && ip < broadcast).Order().Prepend(network).Append(broadcast).ToArray();
                var best = boundaries.Zip(boundaries.Skip(1), (a, b) => (First: a + 1, Last: b - 1, Count: (long)b - a - 1)).OrderByDescending(b => b.Count).First();
                if (best.Count > 0) { start = best.First; end = (uint)Math.Min((ulong)best.Last, (ulong)start + 50); }
            }
        }
        return new() { AdapterId = adapter.Id, ServerAddress = adapter.Address, Mask = adapter.Mask,
            StartAddress = start == 0 ? "" : Ipv4.Format(start), EndAddress = end == 0 ? "" : Ipv4.Format(end),
            Gateway = adapter.Gateway ?? "", PrimaryDns = adapter.Dns.ElementAtOrDefault(0) ?? "", SecondaryDns = adapter.Dns.ElementAtOrDefault(1) ?? "" };
    }

    public void Validate(AdapterInfo adapter)
    {
        var server = Ipv4.Parse(ServerAddress); var start = Ipv4.Parse(StartAddress); var end = Ipv4.Parse(EndAddress); var mask = Ipv4.Parse(Mask);
        if (!Ipv4.IsMask(mask) || mask is 0 or 0xffffffff or 0xfffffffe) throw new ArgumentException("Die Subnetzmaske muss zusammenhängend sein und einen Pool von Hostadressen zulassen (/1 bis /30).");
        if (AdapterId != adapter.Id || ServerAddress != adapter.Address || Mask != adapter.Mask) throw new ArgumentException("Serveradresse und Subnetzmaske müssen zur ausgewählten LAN-Karte passen.");
        if (!Ipv4.IsHost(server) || server >> 16 == 0xa9fe) throw new ArgumentException("Die LAN-Karte benötigt eine reguläre IPv4-Adresse. Bitte die Windows-IP-Konfiguration prüfen.");
        var net = server & mask; var broadcast = net | ~mask;
        if (start > end || (start & mask) != net || (end & mask) != net || start == net || end == broadcast) throw new ArgumentException("Start- und Stop-IP müssen einen gültigen Hostbereich im Subnetz der LAN-Karte bilden.");
        if (LeaseSeconds is 0 or uint.MaxValue) throw new ArgumentException("Bitte eine endliche Lease-Dauer zwischen 1 und 4.294.967.294 Sekunden eingeben.");
        var excluded = new List<uint> { server };
        excluded.AddRange(adapter.LocalAddresses.Select(Ipv4.Parse));
        foreach (var text in new[] { Gateway, PrimaryDns, SecondaryDns }.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var ip = Ipv4.Parse(text); if (!Ipv4.IsHost(ip) || ip == broadcast || ip == net) throw new ArgumentException("Gateway und DNS müssen gültige Unicast-Hostadressen sein.");
            excluded.Add(ip);
        }
        if (!string.IsNullOrWhiteSpace(Gateway) && (Ipv4.Parse(Gateway) & mask) != net) throw new ArgumentException("Der Gateway muss im lokalen Subnetz liegen.");
        if (string.IsNullOrWhiteSpace(PrimaryDns) && !string.IsNullOrWhiteSpace(SecondaryDns)) throw new ArgumentException("Bitte zuerst den primären DNS-Server angeben.");
        if (excluded.Any(ip => ip >= start && ip <= end)) throw new ArgumentException("Der Adresspool darf keine lokale Adapteradresse, Gateway- oder DNS-Adresse enthalten.");
        if (Options.Where(o => o.Enabled).GroupBy(o => o.Code).Any(g => g.Count() > 1)) throw new ArgumentException("Ein zusätzlicher Optionscode darf nur einmal vorkommen.");
        foreach (var option in Options.Where(o => o.Enabled)) OptionCodec.Encode(option);
    }
}

public static class Ipv4
{
    public static bool TryParse(string? text, out uint address)
    {
        address = 0;
        var parts = text?.Trim().Split('.');
        if (parts?.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || part.Any(c => c is < '0' or > '9') || (part.Length > 1 && part[0] == '0') || !byte.TryParse(part, out var b)) return false;
            address = (address << 8) | b;
        }
        return true;
    }
    public static uint Parse(string text) => TryParse(text, out var ip) ? ip : throw new ArgumentException($"Ungültige IPv4-Adresse: {text}");
    public static string Format(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";
    public static IPAddress ToAddress(uint ip) => new(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });
    public static bool IsMask(uint mask) { var inv = ~mask; return (inv & (inv + 1)) == 0; }
    public static bool IsHost(uint ip) => ip >> 24 is > 0 and < 224 && ip >> 24 != 127;
    public static string Network(string ip, string mask) => Format(Parse(ip) & Parse(mask));
}

public sealed record LeaseRecord
{
    public string Scope { get; init; } = "";
    public string ClientKey { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ClientId => ClientKey.StartsWith("ID:", StringComparison.Ordinal) ? ClientKey[3..] : "";
    public string Mac { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Address { get; init; } = "";
    public DateTimeOffset LastConfirmedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string State { get; init; } = "Active";
}

public sealed record SavedRange
{
    public string Name { get; init; } = "";
    public ServerConfiguration Configuration { get; init; } = new();
    public override string ToString() => Name;
}
