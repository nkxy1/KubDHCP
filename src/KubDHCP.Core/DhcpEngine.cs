namespace KubDHCP.Core;

/// <summary>Serialized lease transitions, independent of the network and the UI.</summary>
public sealed class DhcpEngine
{
    private readonly object gate = new();
    private readonly DataStore store;
    private readonly ServerConfiguration configuration;
    private readonly uint start, end, server, network, mask;
    private readonly string scope;
    private List<LeaseRecord> leases;
    private readonly Dictionary<string, (uint Ip, DateTimeOffset Until, string Hostname)> offers = [];
    public event Action? Changed;

    public DhcpEngine(ServerConfiguration configuration, DataStore store)
    {
        this.configuration = configuration;
        this.store = store;
        start = Ipv4.Parse(configuration.StartAddress);
        end = Ipv4.Parse(configuration.EndAddress);
        server = Ipv4.Parse(configuration.ServerAddress);
        mask = Ipv4.Parse(configuration.Mask);
        network = server & mask;
        scope = configuration.Scope;
        leases = store.LoadLeases();

        var now = DateTimeOffset.UtcNow;
        foreach (var lease in leases)
        {
            if (lease.Scope != scope || lease.State != "Active" || lease.ExpiresUtc <= now) continue;
            var address = Ipv4.Parse(lease.Address);
            if (address < start || address > end)
                throw new InvalidOperationException(Localizer.Text("Es bestehen noch gültige Leases außerhalb des neuen Pools. Pool wieder erweitern oder deren Ablauf abwarten."));
        }
    }

    public IReadOnlyList<LeaseRecord> Snapshot()
    {
        lock (gate)
            return leases.Where(l => l.Scope == scope && l.LastConfirmedUtc != default)
                .OrderByDescending(l => l.LastConfirmedUtc).ToArray();
    }

    public bool RemoveLease(LeaseRecord lease)
    {
        lock (gate)
        {
            if (lease.Scope != scope) return false;
            var index = leases.IndexOf(lease);
            if (index < 0) return false;
            var updated = new List<LeaseRecord>(leases);
            updated.RemoveAt(index);
            Commit(updated);
            if (offers.TryGetValue(lease.ClientKey, out var offer) && offer.Ip == Ipv4.Parse(lease.Address))
                offers.Remove(lease.ClientKey);
        }
        Changed?.Invoke();
        return true;
    }

    public DhcpReply? Handle(DhcpPacket packet, DateTimeOffset now)
    {
        DhcpReply? reply;
        lock (gate)
        {
            if (packet.RelayAddress != 0) return null;
            // Dictionary.Remove does not invalidate enumeration on our .NET target.
            foreach (var offer in offers)
                if (offer.Value.Until <= now) offers.Remove(offer.Key);

            var clientKey = packet.ClientKey;
            reply = packet.MessageType switch
            {
                DhcpMessageType.Discover => Discover(packet, clientKey, now),
                DhcpMessageType.Request => Request(packet, clientKey, now),
                DhcpMessageType.Decline => Decline(packet, clientKey, now),
                DhcpMessageType.Release => Release(packet, clientKey, now),
                DhcpMessageType.Inform when packet.ClientAddress != 0 && (packet.ClientAddress & mask) == network => new(DhcpMessageType.Ack, 0),
                _ => null
            };
        }
        Changed?.Invoke();
        return reply;
    }

    private DhcpReply? Discover(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        var known = leases.FirstOrDefault(l => l.Scope == scope && l.ClientKey == clientKey && l.State == "Active" && l.ExpiresUtc > now);
        uint? chosen = null;
        if (known is not null)
        {
            var address = Ipv4.Parse(known.Address);
            if (Available(address, clientKey, now)) chosen = address;
        }
        if (chosen is null && offers.TryGetValue(clientKey, out var previous) && Available(previous.Ip, clientKey, now))
            chosen = previous.Ip;
        if (chosen is null && packet.IpOption(50) is { } requested && Available(requested, clientKey, now))
            chosen = requested;
        chosen ??= FindAvailableAddress(clientKey, now);
        if (chosen is null) return null;

        var hostname = packet.Hostname;
        if (hostname.Length == 0)
            hostname = known?.Hostname ?? (offers.TryGetValue(clientKey, out var prior) ? prior.Hostname : "");
        offers[clientKey] = (chosen.Value, now.AddSeconds(30), hostname);
        return new(DhcpMessageType.Offer, chosen.Value, LeaseTime(packet));
    }

    private uint? FindAvailableAddress(string clientKey, DateTimeOffset now)
    {
        // Build once per pool search instead of scanning all state for each candidate.
        // Keep ordinal address-text comparisons, matching Available and persisted leases.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lease in leases)
            if (BlocksAddress(lease, clientKey, now)) blocked.Add(lease.Address);
        foreach (var offer in offers)
            if (offer.Key != clientKey && offer.Value.Until > now) blocked.Add(Ipv4.Format(offer.Value.Ip));

        // ulong prevents wraparound when the pool ends at uint.MaxValue.
        for (ulong address = start; address <= end; address++)
            if (!blocked.Contains(Ipv4.Format((uint)address))) return (uint)address;
        return null;
    }

    private DhcpReply? Request(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        var selected = packet.IpOption(54);
        if (selected is not null && selected != server)
        {
            offers.Remove(clientKey);
            return null;
        }
        var address = packet.ClientAddress != 0 ? packet.ClientAddress : packet.IpOption(50);
        if (address is null || (address.Value & mask) != network) return null; // Never NAK a scope we do not own.
        var addressText = Ipv4.Format(address.Value);
        var known = leases.FirstOrDefault(l => l.Scope == scope && l.ClientKey == clientKey && l.Address == addressText && l.State == "Active");
        var offered = offers.TryGetValue(clientKey, out var offer) && offer.Ip == address;
        if (selected == server && packet.ClientAddress != 0) return null;
        if (selected is null && known is null) return null; // Unknown INIT-REBOOT / renewal: stay silent.
        if (selected == server && !offered && known is null) return null;
        if (!Available(address.Value, clientKey, now)) return new(DhcpMessageType.Nak, 0);

        var seconds = LeaseTime(packet);
        var hostname = packet.Hostname;
        if (hostname.Length == 0 && offered) hostname = offer.Hostname ?? "";
        if (hostname.Length == 0)
            hostname = leases.Where(l => l.Scope == scope && l.ClientKey == clientKey)
                .MaxBy(l => l.LastConfirmedUtc)?.Hostname ?? "";
        var record = new LeaseRecord
        {
            Scope = scope, ClientKey = clientKey, Mac = packet.Mac, Address = addressText,
            Hostname = hostname, LastConfirmedUtc = now, ExpiresUtc = now.AddSeconds(seconds)
        };
        var updated = leases.Where(l => !(l.Scope == scope && l.ClientKey == clientKey && l.Address == addressText)).ToList();
        updated.Add(record);
        Commit(updated);
        offers.Remove(clientKey);
        return new(DhcpMessageType.Ack, address.Value, seconds);
    }

    private DhcpReply? Decline(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        if (packet.IpOption(54) != server || packet.IpOption(50) is not { } address) return null;
        var addressText = Ipv4.Format(address);
        var owned = offers.TryGetValue(clientKey, out var offered) && offered.Ip == address
            || leases.Any(l => l.ClientKey == clientKey && l.Address == addressText && l.State == "Active" && l.ExpiresUtc > now);
        if (owned)
        {
            QuarantineLocked(address, now, clientKey);
            offers.Remove(clientKey);
        }
        return null;
    }

    private DhcpReply? Release(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        if (packet.IpOption(54) != server || packet.ClientAddress == 0) return null;
        var addressText = Ipv4.Format(packet.ClientAddress);
        var updated = leases.Select(l => l.Scope == scope && l.ClientKey == clientKey && l.Address == addressText && l.State == "Active"
            ? l with { State = "Released", ExpiresUtc = now } : l).ToList();
        Commit(updated);
        offers.Remove(clientKey);
        return null;
    }

    private uint LeaseTime(DhcpPacket packet)
    {
        var requested = packet.IpOption(51);
        return requested is > 0 and < uint.MaxValue ? Math.Min(requested.Value, configuration.LeaseSeconds) : configuration.LeaseSeconds;
    }

    private bool BlocksAddress(LeaseRecord lease, string clientKey, DateTimeOffset now) =>
        lease.ExpiresUtc > now && (lease.State == "Declined"
            || lease.State == "Active" && (lease.ClientKey != clientKey || lease.Scope != scope));

    private bool Available(uint address, string clientKey, DateTimeOffset now)
    {
        if (address < start || address > end) return false;
        var addressText = Ipv4.Format(address);
        return !leases.Any(l => l.Address == addressText && BlocksAddress(l, clientKey, now))
            && !offers.Any(o => o.Key != clientKey && o.Value.Ip == address && o.Value.Until > now);
    }

    private void Commit(List<LeaseRecord> updated)
    {
        store.SaveLeases(updated);
        leases = updated;
    }

    public void Quarantine(uint address, DateTimeOffset now)
    {
        lock (gate)
        {
            QuarantineLocked(address, now, "CONFLICT");
            foreach (var offer in offers)
                if (offer.Value.Ip == address) offers.Remove(offer.Key);
        }
        Changed?.Invoke();
    }

    private void QuarantineLocked(uint address, DateTimeOffset now, string clientKey)
    {
        var addressText = Ipv4.Format(address);
        var previous = leases.FirstOrDefault(l => l.Scope == scope && l.Address == addressText && l.ClientKey == clientKey);
        var blocked = new LeaseRecord
        {
            Scope = scope, Address = addressText, ClientKey = clientKey, Mac = previous?.Mac ?? "",
            Hostname = previous?.Hostname ?? "", LastConfirmedUtc = previous?.LastConfirmedUtc ?? default,
            ExpiresUtc = now.AddMinutes(10), State = "Declined"
        };
        var updated = leases.Where(l => !(l.Scope == scope && l.Address == addressText)).ToList();
        updated.Add(blocked);
        Commit(updated);
    }
}
