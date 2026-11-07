using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubDHCP.Core;

[JsonSerializable(typeof(List<SavedRange>))]
[JsonSerializable(typeof(ServerConfiguration))]
[JsonSerializable(typeof(List<LeaseRecord>))]
[JsonSerializable(typeof(List<OptionDefinition>))]
internal partial class DataJsonContext : JsonSerializerContext;

public sealed class DataStore
{
    public string DirectoryPath { get; }
    public DataStore(string path) { DirectoryPath = Path.GetFullPath(path); Directory.CreateDirectory(DirectoryPath); }
    public ServerConfiguration? LoadConfiguration()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), DataJsonContext.Default.ServerConfiguration) : null;
    }
    public void SaveConfiguration(ServerConfiguration configuration) => AtomicWrite("settings.json", JsonSerializer.Serialize(configuration, DataJsonContext.Default.ServerConfiguration));
    public List<SavedRange> LoadRanges()
    {
        var path = Path.Combine(DirectoryPath, "ranges.json");
        if (!File.Exists(path)) return [];
        var ranges = JsonSerializer.Deserialize(File.ReadAllText(path), DataJsonContext.Default.ListSavedRange)
            ?? throw new InvalidDataException("Die gespeicherten Bereiche sind leer oder beschädigt.");
        if (ranges.Any(r => r is null || string.IsNullOrWhiteSpace(r.Name) || r.Configuration is null)
            || ranges.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Die gespeicherten Bereiche enthalten ungültige oder doppelte Namen.");
        return ranges;
    }
    public void SaveRange(string name, ServerConfiguration configuration)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Bitte einen Namen für den Bereich eingeben.");
        var ranges = LoadRanges();
        ranges.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        ranges.Add(new SavedRange { Name = name, Configuration = configuration });
        AtomicWrite("ranges.json", JsonSerializer.Serialize(ranges.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), DataJsonContext.Default.ListSavedRange));
    }
    public bool RemoveRange(string name)
    {
        var ranges = LoadRanges();
        if (ranges.RemoveAll(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) return false;
        AtomicWrite("ranges.json", JsonSerializer.Serialize(ranges, DataJsonContext.Default.ListSavedRange));
        return true;
    }
    public List<LeaseRecord> LoadLeases()
    {
        var path = Path.Combine(DirectoryPath, "leases.json");
        if (!File.Exists(path)) return [];
        try
        {
            var leases = JsonSerializer.Deserialize(File.ReadAllText(path), DataJsonContext.Default.ListLeaseRecord) ?? throw new InvalidDataException("Leasedatei ist leer.");
            if (leases.Any(l => l is null || !Ipv4.TryParse(l.Address, out _) || string.IsNullOrWhiteSpace(l.ClientKey) || string.IsNullOrWhiteSpace(l.Scope) || l.State is not ("Active" or "Released" or "Declined") || l.State == "Active" && (l.LastConfirmedUtc == default || l.ExpiresUtc < l.LastConfirmedUtc))) throw new InvalidDataException("Leasedatei enthält ungültige Einträge.");
            var now = DateTimeOffset.UtcNow;
            if (leases.Where(l => l.State is "Active" or "Declined" && l.ExpiresUtc > now).GroupBy(l => (l.Scope, l.Address)).Any(g => g.Count() > 1)) throw new InvalidDataException("Leasedatei enthält doppelte aktive IP-Adressen.");
            // Source-generated deserialization may supply null for newly added init properties in older files.
            return leases.Select(l => l.Hostname is null ? l with { Hostname = "" } : l).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new InvalidDataException("Lease-Zustand ist beschädigt. Start gesperrt; leases.json und Sicherung im Datenordner prüfen.", ex); }
    }
    public void SaveLeases(List<LeaseRecord> leases) => AtomicWrite("leases.json", JsonSerializer.Serialize(leases, DataJsonContext.Default.ListLeaseRecord));
    public bool RemoveLease(LeaseRecord lease)
    {
        var leases = LoadLeases();
        if (!leases.Remove(lease)) return false;
        SaveLeases(leases); return true;
    }
    private void AtomicWrite(string name, string text)
    {
        var path = Path.Combine(DirectoryPath, name); var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(text); writer.Flush(); stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class OuiLookup
{
    private readonly Dictionary<string, string> vendors = new(StringComparer.OrdinalIgnoreCase);
    public int Count => vendors.Count;
    public OuiLookup()
    {
        using var data = typeof(OuiLookup).Assembly.GetManifestResourceStream("KubDHCP.Core.Data.oui.tsv.gz")!;
        using var zip = new GZipStream(data, CompressionMode.Decompress); using var reader = new StreamReader(zip);
        while (reader.ReadLine() is { } line)
        {
            var index = line.IndexOf('\t'); if (index > 0) vendors[line[..index]] = line[(index + 1)..];
        }
    }
    public string Find(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var first)) return "Unbekannt";
        if ((first & 2) != 0) return "Lokal administriert";
        foreach (var length in new[] { 9, 7, 6 }) if (vendors.TryGetValue(hex[..length], out var name)) return name;
        return "Unbekannt";
    }
}
