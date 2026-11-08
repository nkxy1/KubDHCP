using System.Buffers.Binary;
using System.Collections.Concurrent;
using KubDHCP.Core;

args = Localizer.Configure(args);
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var adapter = new AdapterInfo("test", "Test LAN", "Isolated test adapter", 10, "192.168.50.1", "255.255.255.0", "192.168.50.254", ["1.1.1.1", "1.0.0.1"], ["192.168.50.1"], false);
var config = ServerConfiguration.ForAdapter(adapter);
var now = DateTimeOffset.UtcNow;
DataStore Store() => new(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
DhcpPacket Packet(byte type, int client = 1, uint ciaddr = 0, params (byte Code, byte[] Value)[] extra)
{
    var b = new byte[240]; b[0] = 1; b[1] = 1; b[2] = 6; b[7] = (byte)client; b[10] = 0x80;
    b[28] = 0; b[29] = 0x1b; b[30] = 0x1b; b[31] = (byte)(client >> 16); b[32] = (byte)(client >> 8); b[33] = (byte)client;
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), ciaddr); new byte[] { 99, 130, 83, 99 }.CopyTo(b, 236);
    var bytes = b.ToList(); bytes.AddRange([53, 1, type]);
    foreach (var (code, value) in extra) { bytes.Add(code); bytes.Add((byte)value.Length); bytes.AddRange(value); }
    bytes.Add(255); Check(DhcpPacket.TryParse(bytes.ToArray(), out var packet)); return packet!;
}
DhcpPacket ReadReply(byte[] bytes) { bytes = bytes.ToArray(); bytes[0] = 1; Check(DhcpPacket.TryParse(bytes, out var p)); return p!; }
DhcpPacket Request(DhcpReply offer, int client = 1) => Packet(3, client, 0, (50, OptionCodec.UInt32Bytes(offer.Address)), (54, OptionCodec.IpBytes(config.ServerAddress)));

Test("IPv4 decimal parsing and boundaries", () => { Check(Ipv4.Parse("192.168.50.1") == 0xc0a83201); Check(Ipv4.Format(0xffffffff) == "255.255.255.255"); foreach (var ip in new[] { "127.1", "010.0.0.1", "1.2.3.256", "1.2.3.-1", "1.2.3.4.5" }) Check(!Ipv4.TryParse(ip, out _)); });
Test("Adapter-based default pool and DNS", () => { config.Validate(adapter); Check(config.StartAddress == "192.168.50.100" && config.EndAddress == "192.168.50.150"); Check(config.PrimaryDns == "1.1.1.1"); });
Test("Small subnet avoids infrastructure", () => { var a = adapter with { Address = "10.0.0.2", Mask = "255.255.255.248", Gateway = "10.0.0.1", Dns = ["10.0.0.1"], LocalAddresses = ["10.0.0.2"] }; var c = ServerConfiguration.ForAdapter(a); c.Validate(a); Check(c.StartAddress == "10.0.0.3" && c.EndAddress == "10.0.0.6"); });
Test("Reject invalid pool, mask and infrastructure overlap", () => { Throws<ArgumentException>(() => (config with { EndAddress = "192.168.51.1" }).Validate(adapter)); Throws<ArgumentException>(() => (config with { Mask = "255.0.255.0" }).Validate(adapter)); Throws<ArgumentException>(() => (config with { StartAddress = "192.168.50.1" }).Validate(adapter)); });
Test("Secondary DNS needs primary", () => Throws<ArgumentException>(() => (config with { PrimaryDns = "" }).Validate(adapter)));
Test("Reject multicast DNS and APIPA server", () => { Throws<ArgumentException>(() => (config with { PrimaryDns = "224.0.0.1" }).Validate(adapter)); var a = adapter with { Address = "169.254.1.2", Gateway = null, Dns = [], LocalAddresses = ["169.254.1.2"] }; Throws<ArgumentException>(() => ServerConfiguration.ForAdapter(a).Validate(a)); });
Test("DHCP parser rejects malformed TLV and cookie", () => { Check(!DhcpPacket.TryParse(new byte[239], out _)); var raw = Packet(1).Header.Concat(new byte[] { 99, 130, 83, 99, 53, 1, 1, 15, 10, 1 }).ToArray(); Check(!DhcpPacket.TryParse(raw, out _)); raw[236] = 0; Check(!DhcpPacket.TryParse(raw, out _)); });
Test("Big-endian integer option encoders", () => { Check(OptionCodec.Encode(new() { Code = 224, Format = "UInt16", Value = "4660" }).SequenceEqual(new byte[] { 0x12, 0x34 })); Check(OptionCodec.Encode(new() { Code = 224, Format = "UInt32", Value = "305419896" }).SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 })); });
Test("Managed protocol options cannot be overridden", () => Throws<ArgumentException>(() => OptionCodec.Encode(new() { Code = 53, Format = "Hex", Value = "02" })));
Test("RFC 3397 domain-list encoder", () => { var bytes = OptionCodec.Encode(new() { Code = 119, Format = "Domainliste", Value = "example.com" }); Check(bytes.SequenceEqual(new byte[] { 7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0 })); });
Test("RFC 3442 route encoder", () => { var bytes = OptionCodec.Encode(new() { Code = 121, Format = "Routen", Value = "0.0.0.0/0=192.168.50.254;10.20.0.0/16=192.168.50.254" }); Check(bytes.SequenceEqual(new byte[] { 0, 192, 168, 50, 254, 16, 10, 20, 192, 168, 50, 254 })); Throws<ArgumentException>(() => OptionCodec.Encode(new() { Code = 121, Format = "Routen", Value = "10.20.1.0/16=192.168.50.254" })); });
Test("Full embedded IANA catalog", () => { var options = OptionDefinition.Load(); Check(options.Count >= 160); Check(options.Any(o => o.Code == 125)); Check(options.First(o => o.Code == 53).Managed); });
Test("IEEE longest-prefix database and local MAC handling", () => { var oui = new OuiLookup(); Check(oui.Count > 50000); Check(oui.Find("B8:27:EB:01:02:03").Contains("Raspberry", StringComparison.OrdinalIgnoreCase)); Check(oui.Find("02:11:22:33:44:55") == Localizer.Text("Lokal administriert")); });
Test("Offer does not create a confirmed lease", () => { var e = new DhcpEngine(config, Store()); Check(e.Handle(Packet(1), now)?.Type == DhcpMessageType.Offer); Check(e.Snapshot().Count == 0); });
Test("DORA commits lease, updates renewal and survives restart", () => { var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; var ack = e.Handle(Request(offer), now); Check(ack?.Type == DhcpMessageType.Ack); Check(e.Snapshot().Count == 1); e.Handle(Packet(3, 1, offer.Address), now.AddSeconds(30)); Check(e.Snapshot().Count == 1 && e.Snapshot()[0].LastConfirmedUtc == now.AddSeconds(30)); var restart = new DhcpEngine(config, store); Check(restart.Handle(Packet(1, 2), now.AddSeconds(31))?.Address != offer.Address); });
Test("Simultaneous clients receive unique reservations", () => { var e = new DhcpEngine(config, Store()); var ips = new ConcurrentBag<uint>(); Parallel.For(1, 41, client => ips.Add(e.Handle(Packet(1, client), now)!.Address)); Check(ips.Count == 40 && ips.Distinct().Count() == 40); });
Test("Pool exhaustion returns no offer", () => { var c = config with { EndAddress = config.StartAddress }; var e = new DhcpEngine(c, Store()); Check(e.Handle(Packet(1), now) is not null); Check(e.Handle(Packet(1, 2), now) is null); });
Test("Reservation expires after 30 seconds", () => { var c = config with { EndAddress = config.StartAddress }; var e = new DhcpEngine(c, Store()); e.Handle(Packet(1), now); Check(e.Handle(Packet(1, 2), now.AddSeconds(31)) is not null); });
Test("Foreign server requests are ignored", () => { var e = new DhcpEngine(config, Store()); var offer = e.Handle(Packet(1), now)!; Check(e.Handle(Packet(3, 1, 0, (50, OptionCodec.UInt32Bytes(offer.Address)), (54, OptionCodec.IpBytes("192.168.50.2"))), now) is null); Check(e.Snapshot().Count == 0); });
Test("Unknown reboot and foreign subnet do not receive ACK/NAK", () => { var e = new DhcpEngine(config, Store()); Check(e.Handle(Packet(3, 1, 0, (50, OptionCodec.IpBytes("192.168.50.100"))), now) is null); Check(e.Handle(Packet(3, 1, Ipv4.Parse("10.0.0.1")), now) is null); });
Test("Release frees address and retains historical row", () => { var c = config with { EndAddress = config.StartAddress }; var e = new DhcpEngine(c, Store()); var offer = e.Handle(Packet(1), now)!; e.Handle(Request(offer), now); e.Handle(Packet(7, 1, offer.Address, (54, OptionCodec.IpBytes(config.ServerAddress))), now.AddSeconds(1)); Check(e.Snapshot()[0].State == "Released"); Check(e.Handle(Packet(1, 2), now.AddSeconds(2))?.Address == offer.Address); });
Test("Decline quarantines address across restart", () => { var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; e.Handle(Packet(4, 1, 0, (50, OptionCodec.UInt32Bytes(offer.Address)), (54, OptionCodec.IpBytes(config.ServerAddress))), now); var restart = new DhcpEngine(config, store); Check(restart.Handle(Packet(1, 2), now.AddSeconds(1))?.Address != offer.Address); });
Test("Shorter requested lease is honored", () => { var e = new DhcpEngine(config, Store()); var offer = e.Handle(Packet(1, 1, 0, (51, OptionCodec.UInt32Bytes(120))), now); Check(offer?.LeaseSeconds == 120); });
Test("Client identifier has priority over MAC", () => { var p1 = Packet(1, 1, 0, (61, new byte[] { 0, 1 })); var p2 = Packet(1, 1, 0, (61, new byte[] { 0, 2 })); var e = new DhcpEngine(config, Store()); Check(e.Handle(p1, now)!.Address != e.Handle(p2, now)!.Address); });
Test("Response echoes client ID and respects parameter request list", () => { var c = config with { Options = [new() { Code = 15, Format = "Text (ASCII)", Value = "example.com" }] }; var request = Packet(1, 1, 0, (61, new byte[] { 1, 2, 3 }), (55, new byte[] { 15 })); var response = ReadReply(DhcpPacketWriter.Build(request, new(DhcpMessageType.Offer, Ipv4.Parse(c.StartAddress), 3600), c)); Check(response.Options[61].SequenceEqual(new byte[] { 1, 2, 3 })); Check(response.Options.ContainsKey(15)); var noRequest = ReadReply(DhcpPacketWriter.Build(Packet(1), new(DhcpMessageType.Offer, Ipv4.Parse(c.StartAddress), 3600), c)); Check(!noRequest.Options.ContainsKey(15)); });
Test("RFC 3396 concatenates long raw options", () => { var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(); var c = config with { Options = [new() { Code = 224, Format = "Hex", Value = Convert.ToHexString(data), AlwaysSend = true }] }; var request = Packet(1, 1, 0, (57, OptionCodec.UInt16Bytes(1500))); var response = ReadReply(DhcpPacketWriter.Build(request, new(DhcpMessageType.Offer, Ipv4.Parse(c.StartAddress), 3600), c)); Check(response.Options[224].SequenceEqual(data)); });
Test("Oversized options are not silently truncated", () => { var c = config with { Options = [new() { Code = 224, Format = "Hex", Value = new string('A', 1400), AlwaysSend = true }] }; Throws<InvalidOperationException>(() => DhcpPacketWriter.Build(Packet(1), new(DhcpMessageType.Offer, Ipv4.Parse(c.StartAddress), 3600), c)); });
Test("DHCPINFORM returns configuration without a lease", () => { var e = new DhcpEngine(config, Store()); var request = Packet(8, 1, Ipv4.Parse("192.168.50.20")); var reply = e.Handle(request, now)!; var response = ReadReply(DhcpPacketWriter.Build(request, reply, config)); Check(reply.Type == DhcpMessageType.Ack && reply.Address == 0 && !response.Options.ContainsKey(51)); Check(e.Snapshot().Count == 0); });
Test("Corrupt lease state blocks start", () => { var store = Store(); File.WriteAllText(Path.Combine(store.DirectoryPath, "leases.json"), "{broken"); Throws<InvalidDataException>(() => new DhcpEngine(config, store)); });
Test("Shrinking pool with outstanding leases is blocked", () => { var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; e.Handle(Request(offer), now); Throws<InvalidOperationException>(() => new DhcpEngine(config with { StartAddress = "192.168.50.110" }, store)); });
Test("Configuration JSON roundtrip includes additional options", () => { var store = Store(); var c = config with { Options = [new() { Code = 66, Name = "TFTP", Format = "Text (ASCII)", Value = "boot.example" }] }; store.SaveConfiguration(c); Check(store.LoadConfiguration()!.Options[0].Value == "boot.example"); });
Test("Missing DHCP end option is rejected", () => { var raw = Packet(1).Header.Concat(new byte[] { 99, 130, 83, 99, 53, 1, 1 }).ToArray(); Check(!DhcpPacket.TryParse(raw, out _)); });
Test("Null entry in lease JSON blocks start", () => { var store = Store(); File.WriteAllText(Path.Combine(store.DirectoryPath, "leases.json"), "[null]"); Throws<InvalidDataException>(() => new DhcpEngine(config, store)); });

Test("Named ranges survive restart including options and lease duration", () =>
{
    var store = Store(); var c = config with { LeaseSeconds = 7200, Options = [new() { Code = 66, Name = "TFTP", Format = "Text (ASCII)", Value = "boot.example" }] };
    store.SaveRange("Lab", c); store.SaveRange("Office", config);
    var ranges = new DataStore(store.DirectoryPath).LoadRanges();
    Check(ranges.Count == 2); var saved = ranges.Single(r => r.Name == "Lab").Configuration;
    Check(saved.AdapterId == c.AdapterId && saved.StartAddress == c.StartAddress && saved.LeaseSeconds == 7200 && saved.Options.Single().Value == "boot.example");
});
Test("Range names are trimmed and replaced without case duplicates", () =>
{
    var store = Store(); store.SaveRange(" Lab ", config); store.SaveRange("LAB", config with { LeaseSeconds = 60 });
    Check(store.LoadRanges().Count == 1 && store.LoadRanges()[0].Configuration.LeaseSeconds == 60);
    Throws<ArgumentException>(() => store.SaveRange(" ", config));
});
Test("Corrupt range file is preserved instead of overwritten", () =>
{
    var store = Store(); var path = Path.Combine(store.DirectoryPath, "ranges.json"); File.WriteAllText(path, "[null]");
    Throws<InvalidDataException>(() => store.SaveRange("Lab", config)); Check(File.ReadAllText(path) == "[null]");
});
Test("Lease reset persists across engine restart and leaves ranges intact", () =>
{
    var store = Store(); store.SaveRange("Lab", config); var engine = new DhcpEngine(config, store);
    var offer = engine.Handle(Packet(1), now)!; engine.Handle(Request(offer), now); Check(engine.Snapshot().Count == 1);
    store.SaveLeases([]); var restarted = new DhcpEngine(config, new DataStore(store.DirectoryPath));
    Check(restarted.Snapshot().Count == 0 && store.LoadRanges().Count == 1);
    Check(restarted.Handle(Packet(1, 2), now)!.Address == offer.Address);
});

Test("Removing one named range preserves other ranges, draft and leases", () =>
{
    var store = Store(); store.SaveConfiguration(config); store.SaveRange("Lab", config); store.SaveRange("Other", config with { LeaseSeconds = 60 });
    var engine = new DhcpEngine(config, store); var offer = engine.Handle(Packet(1), now)!; engine.Handle(Request(offer), now);
    var settings = File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")); var leases = File.ReadAllText(Path.Combine(store.DirectoryPath, "leases.json"));
    Check(store.RemoveRange(" lab ")); Check(new DataStore(store.DirectoryPath).LoadRanges().Single().Name == "Other");
    Check(File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")) == settings && File.ReadAllText(Path.Combine(store.DirectoryPath, "leases.json")) == leases);
    Check(!store.RemoveRange("absent")); Check(store.RemoveRange("Other") && store.LoadRanges().Count == 0);
});
Test("Removing a range never overwrites corrupt storage", () =>
{
    var store = Store(); var path = Path.Combine(store.DirectoryPath, "ranges.json"); File.WriteAllText(path, "[null]");
    Throws<InvalidDataException>(() => store.RemoveRange("Lab")); Check(File.ReadAllText(path) == "[null]");
});
Test("Monitor shows request identity, address, lease and requested options", () =>
{
    var packet = Packet(3, 1, Ipv4.Parse("192.168.50.100"), (50, OptionCodec.IpBytes("192.168.50.101")), (51, OptionCodec.UInt32Bytes(120)), (55, new byte[] { 1, 3, 6 }));
    var text = NetworkMonitorText.Request(packet);
    Check(text.Contains("REQUEST") && text.Contains(packet.Mac) && text.Contains("192.168.50.100") && text.Contains(Localizer.Text("gewünschte IP") + "=192.168.50.101") && text.Contains("120 s") && text.Contains("1,3,6"));
});
Test("Monitor response reflects options actually sent on the wire", () =>
{
    var c = config with { Options = [new() { Code = 66, Format = "Text (ASCII)", Value = "boot.example" }, new() { Code = 67, Format = "Text (ASCII)", Value = "not-requested.bin" }] };
    var packet = Packet(1, 1, 0, (55, new byte[] { 66 })); var reply = new DhcpReply(DhcpMessageType.Offer, Ipv4.Parse(c.StartAddress), 3600);
    var text = NetworkMonitorText.Response(packet, reply, DhcpPacketWriter.Build(packet, reply, c));
    Check(text.Contains("OFFER") && text.Contains(packet.Mac) && text.Contains(c.StartAddress) && text.Contains("boot.example") && text.Contains("3600 s") && text.Contains("DNS="));
    Check(!text.Contains("not-requested.bin"));
});
Test("Monitor sanitizes untrusted names and bounds binary option output", () =>
{
    var packet = Packet(1, 1, 0, (12, new byte[] { 65, 10, 27, 13, 66 }), (224, new byte[200]), (224, new byte[100]));
    var text = NetworkMonitorText.Request(packet);
    Check(text.Contains("Hostname=A???B") && !text.Contains('\n') && !text.Contains('\r') && !text.Contains((char)27));
    Check(text.Contains("300 Bytes") && text.Length < 600);
});
Test("Monitor distinguishes foreign DHCP servers and no-reply messages", () =>
{
    var foreign = Packet(3, 1, 0, (54, OptionCodec.IpBytes("192.168.50.2")));
    Check(NetworkMonitorText.NoReply(foreign, config.ServerAddress).Contains("192.168.50.2"));
    Check(NetworkMonitorText.NoReply(Packet(7), config.ServerAddress).Contains(Localizer.IsGerman ? "keine Antwort vorgesehen" : "no reply is expected"));
});

Test("Hostname survives DISCOVER-only advertisement, renewal and restart", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store);
    var offer = e.Handle(Packet(1, 1, 0, (12, System.Text.Encoding.UTF8.GetBytes("device-lab"))), now)!;
    e.Handle(Request(offer), now); Check(e.Snapshot().Single().Hostname == "device-lab");
    e.Handle(Packet(3, 1, offer.Address), now.AddSeconds(10));
    Check(new DhcpEngine(config, store).Snapshot().Single().Hostname == "device-lab");
    e.Handle(Packet(3, 1, offer.Address, (12, System.Text.Encoding.UTF8.GetBytes("renamed-device"))), now.AddSeconds(20));
    Check(e.Snapshot().Single().Hostname == "renamed-device");
});
Test("Hostname decodes FQDN options and rejects malformed DNS label input", () =>
{
    var dns = OptionCodec.Encode(new ExtraOption { Code = 119, Format = "Domainliste", Value = "printer.lab.example" });
    Check(Packet(1, 1, 0, (81, new byte[] { 4, 0, 0 }.Concat(dns).ToArray()), (12, System.Text.Encoding.UTF8.GetBytes("fallback"))).Hostname == "printer.lab.example");
    Check(Packet(1, 1, 0, (81, new byte[] { 0, 0, 0 }.Concat(System.Text.Encoding.UTF8.GetBytes("legacy.example")).ToArray())).Hostname == "legacy.example");
    Check(Packet(1, 1, 0, (81, new byte[] { 4, 0, 0, 63, 1 }), (12, System.Text.Encoding.UTF8.GetBytes("fallback"))).Hostname == "fallback");
    Check(Packet(1, 1, 0, (81, new byte[] { 4, 0, 0, 192, 0 })).Hostname == "");
    Check(Packet(1, 1, 0, (12, new byte[] { 65, 10, 27, 66 })).Hostname == "");
    Check(Packet(1).Hostname == "");
});
Test("Old lease files without hostname remain readable", () =>
{
    var store = Store(); File.WriteAllText(Path.Combine(store.DirectoryPath, "leases.json"), "[{\"Scope\":\"test\",\"ClientKey\":\"old\",\"Mac\":\"00:11:22:33:44:55\",\"Address\":\"192.168.50.100\",\"State\":\"Released\"}]");
    Check(store.LoadLeases().Single().Hostname == "");
});
Test("Single active lease deletion preserves other clients and durable state", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store);
    var first = e.Handle(Packet(1, 1), now)!; e.Handle(Request(first, 1), now);
    var second = e.Handle(Packet(1, 2), now)!; e.Handle(Request(second, 2), now);
    var lease = e.Snapshot().Single(l => l.ClientKey == Packet(1, 1).ClientKey);
    Check(e.RemoveLease(lease)); Check(!e.RemoveLease(lease));
    Check(e.Snapshot().Single().Address == Ipv4.Format(second.Address));
    Check(new DhcpEngine(config, store).Snapshot().Single().Address == Ipv4.Format(second.Address));
    Check(e.Handle(Packet(1, 3), now)!.Address == first.Address);
});
Test("Stale lease deletion cannot remove a renewed or different-scope lease", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; e.Handle(Request(offer), now);
    var old = e.Snapshot().Single(); e.Handle(Packet(3, 1, offer.Address), now.AddSeconds(15));
    Check(!e.RemoveLease(old)); var current = e.Snapshot().Single();
    Check(!e.RemoveLease(current with { Scope = "other" })); Check(e.Snapshot().Count == 1);
    Check(!store.RemoveLease(old)); Check(store.LoadLeases().Single().LastConfirmedUtc == now.AddSeconds(15));
});
Test("Stopped single deletion removes only the selected historical row", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; e.Handle(Request(offer), now);
    e.Handle(Packet(7, 1, offer.Address, (54, OptionCodec.IpBytes(config.ServerAddress))), now.AddSeconds(1));
    var released = store.LoadLeases().Single(); var next = e.Handle(Packet(1, 2), now.AddSeconds(2))!; e.Handle(Request(next, 2), now.AddSeconds(2));
    var remaining = store.LoadLeases().Single(l => l.State == "Active");
    Check(store.RemoveLease(released)); Check(store.LoadLeases().Single() == remaining);
});

Test("Client ID is shown losslessly from persisted DHCP option 61", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store); var id = new byte[] { 1, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
    var offer = e.Handle(Packet(1, 1, 0, (61, id)), now)!;
    e.Handle(Packet(3, 1, 0, (61, id), (50, OptionCodec.UInt32Bytes(offer.Address)), (54, OptionCodec.IpBytes(config.ServerAddress))), now);
    Check(e.Snapshot().Single().ClientId == "01AABBCCDDEEFF");
    Check(new DhcpEngine(config, store).Snapshot().Single().ClientId == "01AABBCCDDEEFF");
    Check(!File.ReadAllText(Path.Combine(store.DirectoryPath, "leases.json")).Contains("\"ClientId\"", StringComparison.Ordinal));
});
Test("MAC-derived internal identity is not displayed as a supplied Client ID", () =>
{
    var store = Store(); var e = new DhcpEngine(config, store); var offer = e.Handle(Packet(1), now)!; e.Handle(Request(offer), now);
    Check(e.Snapshot().Single().ClientId == ""); Check(new LeaseRecord { ClientKey = "ID:000102FF" }.ClientId == "000102FF");
});

Test("Allocation preserves active lease, previous offer, requested IP and lowest-free priority", () =>
{
    var store = Store();
    store.SaveLeases([new LeaseRecord
    {
        Scope = config.Scope, ClientKey = Packet(1).ClientKey, Address = "192.168.50.110",
        LastConfirmedUtc = now, ExpiresUtc = now.AddHours(1), Hostname = "known-device"
    }]);
    var engine = new DhcpEngine(config, store);
    Check(engine.Handle(Packet(1, 1, 0, (50, OptionCodec.IpBytes("192.168.50.111"))), now)!.Address == Ipv4.Parse("192.168.50.110"));
    var requested = engine.Handle(Packet(1, 2, 0, (50, OptionCodec.IpBytes("192.168.50.115"))), now)!;
    Check(requested.Address == Ipv4.Parse("192.168.50.115"));
    Check(engine.Handle(Packet(1, 2, 0, (50, OptionCodec.IpBytes("192.168.50.116"))), now)!.Address == requested.Address);
    Check(engine.Handle(Packet(1, 3), now)!.Address == Ipv4.Parse(config.StartAddress));
});
Test("Pool search respects cross-scope ownership, quarantine, expiry and released history", () =>
{
    var store = Store();
    LeaseRecord Row(string address, string state, string scope, DateTimeOffset expiry) => new()
    {
        Scope = scope, ClientKey = Packet(1).ClientKey, Address = address, State = state,
        LastConfirmedUtc = now.AddHours(-1), ExpiresUtc = expiry
    };
    store.SaveLeases([
        Row("192.168.50.100", "Active", "other-scope", now.AddHours(1)),
        Row("192.168.50.101", "Declined", config.Scope, now.AddMinutes(10)),
        Row("192.168.50.102", "Active", config.Scope, now),
        Row("192.168.50.103", "Released", config.Scope, now.AddHours(1))
    ]);
    var engine = new DhcpEngine(config, store);
    Check(engine.Handle(Packet(1), now)!.Address == Ipv4.Parse("192.168.50.102"));
    Check(engine.Handle(Packet(1, 2), now)!.Address == Ipv4.Parse("192.168.50.103"));
});
Test("Expiry removes multiple offers at the exact boundary and preserves live offers", () =>
{
    var engine = new DhcpEngine(config with { EndAddress = "192.168.50.102" }, Store());
    var first = engine.Handle(Packet(1, 1), now)!;
    var second = engine.Handle(Packet(1, 2), now)!;
    var third = engine.Handle(Packet(1, 3), now.AddSeconds(10))!;
    Check(engine.Handle(Packet(1, 4), now.AddSeconds(30))!.Address == first.Address);
    Check(engine.Handle(Packet(1, 5), now.AddSeconds(30))!.Address == second.Address);
    Check(engine.Handle(Packet(1, 6), now.AddSeconds(30)) is null);
    Check(engine.Handle(Packet(1, 6), now.AddSeconds(40))!.Address == third.Address);
});
Test("Quarantine removes reservations and expires at the exact ten-minute boundary", () =>
{
    var engine = new DhcpEngine(config with { EndAddress = config.StartAddress }, Store());
    var offered = engine.Handle(Packet(1), now)!;
    engine.Quarantine(offered.Address, now);
    Check(engine.Handle(Request(offered), now.AddSeconds(1)) is null);
    Check(engine.Handle(Packet(1, 2), now.AddMinutes(10).AddTicks(-1)) is null);
    Check(engine.Handle(Packet(1, 2), now.AddMinutes(10))!.Address == offered.Address);
});
Test("Unavailable requested addresses fall back to the lowest free address", () =>
{
    var engine = new DhcpEngine(config, Store());
    var first = engine.Handle(Packet(1), now)!;
    Check(engine.Handle(Packet(1, 2, 0, (50, OptionCodec.UInt32Bytes(first.Address))), now)!.Address == first.Address + 1);
    Check(engine.Handle(Packet(1, 3, 0, (50, OptionCodec.IpBytes("10.0.0.1"))), now)!.Address == first.Address + 2);
});
Test("Pool search terminates at the maximum IPv4 value without wrapping", () =>
{
    var engine = new DhcpEngine(config with
    {
        ServerAddress = "255.255.255.254", StartAddress = "255.255.255.255", EndAddress = "255.255.255.255"
    }, Store());
    Check(engine.Handle(Packet(1), now)!.Address == uint.MaxValue);
    Check(engine.Handle(Packet(1, 2), now) is null);
});
Test("Failed durable commit preserves the reservation and in-memory lease state", () =>
{
    var store = Store();
    var engine = new DhcpEngine(config with { EndAddress = config.StartAddress }, store);
    var offer = engine.Handle(Packet(1), now)!;
    var blockedPath = Path.Combine(store.DirectoryPath, "leases.json.tmp");
    Directory.CreateDirectory(blockedPath);
    Throws<UnauthorizedAccessException>(() => engine.Handle(Request(offer), now));
    Check(engine.Snapshot().Count == 0);
    Check(engine.Handle(Packet(1, 2), now) is null);
    Directory.Delete(blockedPath);
    Check(engine.Handle(Request(offer), now)!.Type == DhcpMessageType.Ack);
    Check(new DhcpEngine(config, store).Snapshot().Count == 1);
});
Test("Hostname history keeps the first row when confirmation timestamps tie", () =>
{
    var store = Store();
    var clientKey = Packet(1).ClientKey;
    LeaseRecord History(string address, string hostname) => new()
    {
        Scope = config.Scope, ClientKey = clientKey, Address = address, State = "Released",
        LastConfirmedUtc = now.AddMinutes(-1), ExpiresUtc = now, Hostname = hostname
    };
    store.SaveLeases([History("192.168.50.110", "first"), History("192.168.50.111", "second")]);
    var engine = new DhcpEngine(config, store);
    var offer = engine.Handle(Packet(1), now)!;
    engine.Handle(Request(offer), now);
    Check(engine.Snapshot().Single(l => l.State == "Active").Hostname == "first");
});

Test("UI language follows German locales and falls back to English for other locales", () =>
{
    var previous = System.Globalization.CultureInfo.CurrentUICulture;
    try
    {
        foreach (var name in new[] { "de-DE", "de-CH", "en-US", "fr-FR" })
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(name);
            Check(Localizer.IsGerman == name.StartsWith("de", StringComparison.Ordinal));
            Check(Localizer.Text("Subnetzmaske") == (Localizer.IsGerman ? "Subnetzmaske" : "Subnet mask"));
            Check(Localizer.Text("DHCP-Konfiguration") == (Localizer.IsGerman ? "DHCP-Konfiguration" : "DHCP configuration"));
            Check(OptionCodec.Encode(new() { Code = 119, Format = "Domainliste", Value = "lab.example" }).Length == 13);
        }
    }
    finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
});
Test("Language override consumes only its own argument and preserves test switches", () =>
{
    var previous = System.Globalization.CultureInfo.CurrentUICulture;
    var threadDefault = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;
    try
    {
        Check(Localizer.Configure(["--tui", "--language=en"]).SequenceEqual(new[] { "--tui" }));
        Check(!Localizer.IsGerman && Localizer.Text("Routen") == "Routes");
        Check(Localizer.Configure(["--startup-test", "report", "--language=de"]).SequenceEqual(new[] { "--startup-test", "report" }));
        Check(Localizer.IsGerman);
        Throws<ArgumentException>(() => Localizer.Configure(["--language=xx"]));
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentUICulture = previous;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = threadDefault;
    }
});
Test("User data defaults to the Windows user profile instead of the executable folder", () =>
{
    Check(UserData.DirectoryPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KubDHCP"));
    var root = Path.Combine(Store().DirectoryPath, "profile");
    var exe = Path.Combine(Store().DirectoryPath, "application");
    Check(UserData.Open(root, exe).DirectoryPath == Path.Combine(root, "KubDHCP"));
    Check(!Directory.Exists(exe));
});
Test("First user-profile start copies legacy data and backups without changing their bytes", () =>
{
    var root = Store().DirectoryPath;
    var executable = Path.Combine(root, "application");
    var legacy = new DataStore(Path.Combine(executable, "KubDHCP.data"));
    legacy.SaveConfiguration(config); legacy.SaveRange("Lab", config);
    legacy.SaveLeases([]); legacy.SaveLeases([]);
    var files = Directory.GetFiles(legacy.DirectoryPath).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
    var user = UserData.Open(Path.Combine(root, "profile"), executable);
    Check(user.LoadRanges().Single().Name == "Lab" && user.LoadConfiguration()!.StartAddress == config.StartAddress);
    foreach (var file in files)
    {
        Check(File.ReadAllBytes(Path.Combine(user.DirectoryPath, file.Key!)).SequenceEqual(file.Value));
        Check(File.ReadAllBytes(Path.Combine(legacy.DirectoryPath, file.Key!)).SequenceEqual(file.Value));
    }
});
Test("Existing user data takes priority over legacy executable-side data", () =>
{
    var root = Store().DirectoryPath; var executable = Path.Combine(root, "application");
    var profile = Path.Combine(root, "profile");
    var user = UserData.Open(profile, executable); user.SaveRange("User", config);
    var legacy = new DataStore(Path.Combine(executable, "KubDHCP.data")); legacy.SaveRange("Legacy", config);
    Check(UserData.Open(profile, executable).LoadRanges().Single().Name == "User");
});
Test("Native launcher preserves legacy migration beside the portable executable", () =>
{
    var root = Store().DirectoryPath;
    var executable = Path.Combine(root, "portable", "KubDHCP.exe");
    var legacy = new DataStore(Path.Combine(Path.GetDirectoryName(executable)!, "KubDHCP.data"));
    legacy.SaveRange("Launcher legacy", config);
    var previous = Environment.GetEnvironmentVariable("KUBDHCP_LAUNCHER_PATH");
    try
    {
        Environment.SetEnvironmentVariable("KUBDHCP_LAUNCHER_PATH", executable);
        var user = UserData.Open(Path.Combine(root, "profile"));
        Check(user.LoadRanges().Single().Name == "Launcher legacy");
        Check(legacy.LoadRanges().Single().Name == "Launcher legacy");
    }
    finally { Environment.SetEnvironmentVariable("KUBDHCP_LAUNCHER_PATH", previous); }
});
Test("Legacy corrupt leases remain corrupt after migration and still block server startup", () =>
{
    var root = Store().DirectoryPath; var executable = Path.Combine(root, "application");
    var legacy = new DataStore(Path.Combine(executable, "KubDHCP.data"));
    File.WriteAllText(Path.Combine(legacy.DirectoryPath, "leases.json"), "{broken");
    var user = UserData.Open(Path.Combine(root, "profile"), executable);
    Throws<InvalidDataException>(() => new DhcpEngine(config, user));
    Check(File.ReadAllText(Path.Combine(legacy.DirectoryPath, "leases.json")) == "{broken");
});

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed");
return failures == 0 ? 0 : 1;
