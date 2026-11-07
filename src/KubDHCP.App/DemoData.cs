using KubDHCP.Core;

namespace KubDHCP.App;

/// <summary>Synthetic documentation data. Never binds a socket or changes an adapter.</summary>
internal static class DemoData
{
    internal static readonly AdapterInfo Adapter = new("documentation", "Documentation LAN", "Synthetic example — no network traffic", 0,
        "192.0.2.1", "255.255.255.0", "192.0.2.254", ["192.0.2.254"], ["192.0.2.1"], false);
    internal static DataStore Create()
    {
        var store = new DataStore(Path.Combine(Path.GetTempPath(), "KubDHCP-demo", Guid.NewGuid().ToString("N")));
        var config = ServerConfiguration.ForAdapter(Adapter) with
        {
            Options = [new() { Code = 15, Name = "Domain Name", Format = "Text (ASCII)", Value = "lab.example" },
                new() { Code = 66, Name = "TFTP Server Name", Format = "Text (ASCII)", Value = "boot.lab.example", AlwaysSend = true }]
        };
        store.SaveConfiguration(config);
        store.SaveRange("Commissioning Lab", config);
        store.SaveRange("Workshop", config with { StartAddress = "192.0.2.120", EndAddress = "192.0.2.140", LeaseSeconds = 7200 });
        var now = DateTimeOffset.UtcNow;
        store.SaveLeases([
            Lease("192.0.2.100", "B8:27:EB:01:02:03", "printer-lab", 1, "Active", now.AddMinutes(-5), now.AddMinutes(55)),
            Lease("192.0.2.101", "00:1B:1B:01:02:03", "controller-01", 2, "Active", now.AddMinutes(-20), now.AddMinutes(40)),
            Lease("192.0.2.102", "02:11:22:33:44:55", "sensor-bench", 3, "Active", now.AddMinutes(-45), now.AddMinutes(15)),
            Lease("192.0.2.103", "00:1B:1B:01:02:04", "test-device", 4, "Released", now.AddHours(-1), now.AddMinutes(-10))
        ]);
        return store;
        LeaseRecord Lease(string address, string mac, string hostname, int client, string state, DateTimeOffset confirmed, DateTimeOffset expires) => new()
        {
            Scope = config.Scope, Address = address, Mac = mac, ClientKey = "ID:01" + client.ToString("X12"),
            Hostname = hostname, State = state, LastConfirmedUtc = confirmed, ExpiresUtc = expires
        };
    }
    internal static readonly string[] MonitorExamples =
    [
        "DEMO · Synthetic examples · No sockets opened · Server start disabled",
        "DISCOVER · client B8:27:EB:01:02:03 · hostname printer-lab · requested IP 192.0.2.100",
        "ARP · 192.0.2.100 · no responding owner found (example)",
        "OFFER · 192.0.2.100 · lease 3600 s · mask 255.255.255.0",
        "REQUEST · client B8:27:EB:01:02:03 · server 192.0.2.1",
        "ACK · 192.0.2.100 · DNS 192.0.2.254 · domain lab.example",
        "RELEASE · test-device · 192.0.2.103 · historical row retained"
    ];
}
