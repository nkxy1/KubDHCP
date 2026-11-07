using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KubDHCP.Core;

public sealed record OptionDefinition
{
    public int Code { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Reference { get; init; } = "";
    public string Display => $"{Code} · {Name}";
    public bool Managed => OptionCodec.ManagedCodes.Contains((byte)Code);
    public string Format => OptionCodec.DefaultFormat((byte)Code);
    public static List<OptionDefinition> Load()
    {
        using var stream = typeof(OptionDefinition).Assembly.GetManifestResourceStream("KubDHCP.Core.Data.options.json")!;
        return (JsonSerializer.Deserialize(stream, DataJsonContext.Default.ListOptionDefinition) ?? []).GroupBy(o => o.Code).Select(g => g.First()).OrderBy(o => o.Code).ToList();
    }
}

public static class OptionCodec
{
    public static readonly HashSet<byte> ManagedCodes = [0, 1, 3, 6, 50, 51, 52, 53, 54, 55, 57, 58, 59, 60, 61, 77, 80, 81, 82, 90, 93, 94, 97, 118, 124, 255];
    public static readonly string[] Formats = ["Hex", "Text (ASCII)", "IPv4", "IPv4-Liste", "UInt8", "UInt16", "UInt32", "Boolean", "Domainliste", "Routen"];
    public static string DefaultFormat(byte code) => code switch
    {
        12 or 14 or 15 or 17 or 18 or 40 or 56 or 64 or 66 or 67 or 86 or 87 or 100 or 101 or 114 => "Text (ASCII)",
        4 or 5 or 7 or 8 or 9 or 10 or 11 or 41 or 42 or 44 or 45 or 48 or 49 or 65 or 68 or 69 or 70 or 71 or 72 or 73 or 74 or 75 or 76 => "IPv4-Liste",
        16 or 28 or 32 or 118 => "IPv4",
        19 or 20 or 27 or 29 or 30 or 31 or 34 or 36 or 39 => "Boolean",
        13 or 22 or 26 => "UInt16", 23 or 37 or 46 => "UInt8", 24 or 35 or 38 => "UInt32",
        119 => "Domainliste", 121 or 249 => "Routen", _ => "Hex"
    };
    public static byte[] Encode(ExtraOption option)
    {
        if (ManagedCodes.Contains(option.Code)) throw new ArgumentException((Localizer.IsGerman ? $"Option {option.Code} wird vom Protokoll oder Hauptformular verwaltet und kann nicht zusätzlich gesendet werden." : $"Option {option.Code} is managed by the protocol or main form and cannot be sent as an additional option."));
        var value = option.Value.Trim();
        if (value.Length == 0) throw new ArgumentException(Localizer.Text("Der Optionswert darf nicht leer sein."));
        byte[] bytes;
        try
        {
            bytes = option.Format switch
            {
                "Text (ASCII)" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(value),
                "IPv4" => IpBytes(value),
                "IPv4-Liste" => value.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).SelectMany(IpBytes).ToArray(),
                "UInt8" => [byte.Parse(value, CultureInfo.InvariantCulture)],
                "UInt16" => UInt16Bytes(ushort.Parse(value, CultureInfo.InvariantCulture)),
                "UInt32" => UInt32Bytes(uint.Parse(value, CultureInfo.InvariantCulture)),
                "Boolean" => [value.ToLowerInvariant() switch { "true" or "1" or "ja" => (byte)1, "false" or "0" or "nein" => (byte)0, _ => throw new FormatException(Localizer.Text("Boolean erwartet true/false oder 1/0.")) }],
                "Domainliste" => Domains(value), "Routen" => Routes(value),
                "Hex" => Convert.FromHexString(string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-'))),
                _ => throw new FormatException(Localizer.Text("Unbekanntes Optionsformat."))
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or EncoderFallbackException) { throw new ArgumentException($"Option {option.Code}: {ex.Message}", ex); }
        if (bytes.Length == 0 || bytes.Length > 3500) throw new ArgumentException(Localizer.Text("Ein zusätzlicher Optionswert muss 1 bis 3500 Bytes enthalten."));
        return bytes;
    }
    public static byte[] IpBytes(string text) => UInt32Bytes(Ipv4.Parse(text));
    public static byte[] UInt16Bytes(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }
    public static byte[] UInt32Bytes(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] Domains(string text)
    {
        var result = new List<byte>(); var idn = new IdnMapping();
        foreach (var domain in text.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var name = idn.GetAscii(domain.TrimEnd('.'));
            if (name.Length > 253) throw new FormatException(Localizer.Text("Domainname ist zu lang."));
            foreach (var label in name.Split('.'))
            {
                if (label.Length is < 1 or > 63 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new FormatException(Localizer.Text("Ungültiges DNS-Label."));
                result.Add((byte)label.Length); result.AddRange(Encoding.ASCII.GetBytes(label));
            }
            result.Add(0);
        }
        return result.ToArray();
    }
    private static byte[] Routes(string text)
    {
        var result = new List<byte>();
        foreach (var item in text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var route = item.Split('=', StringSplitOptions.TrimEntries); var destination = route[0].Split('/');
            if (route.Length != 2 || destination.Length != 2 || !byte.TryParse(destination[1], out var prefix) || prefix > 32) throw new FormatException(Localizer.Text("Route erwartet Ziel/Präfix=Gateway, z. B. 10.20.0.0/16=192.168.50.254."));
            var ip = Ipv4.Parse(destination[0]); var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            if ((ip & mask) != ip) throw new FormatException(Localizer.Text("Das Routenziel muss eine Netzadresse sein."));
            result.Add(prefix); result.AddRange(UInt32Bytes(ip).Take((prefix + 7) / 8)); result.AddRange(IpBytes(route[1]));
        }
        return result.ToArray();
    }
}
