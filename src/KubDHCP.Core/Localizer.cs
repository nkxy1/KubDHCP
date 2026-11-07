using System.Globalization;

namespace KubDHCP.Core;

/// <summary>Shared UI language; persisted identifiers and DHCP values are never translated.</summary>
public static class Localizer
{
    private static readonly Lazy<Dictionary<string, string>> english = new(LoadEnglish);
    public static bool IsGerman => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    public static string Text(string german) => IsGerman ? german : english.Value.GetValueOrDefault(german, german);

    public static string[] Configure(string[] arguments)
    {
        var language = arguments.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal));
        if (language is null) return arguments;
        var name = language[11..];
        if (name is not ("de" or "en")) throw new ArgumentException("--language must be de or en.");
        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return arguments.Where(a => !a.StartsWith("--language=", StringComparison.Ordinal)).ToArray();
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("KubDHCP.Core.Data.English.tsv")
            ?? throw new InvalidOperationException("English language resources are missing.");
        using var reader = new StreamReader(stream);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('\t');
            if (separator < 0 || line.StartsWith('#')) continue;
            result.Add(line[..separator].Replace("\\n", "\n"), line[(separator + 1)..].Replace("\\n", "\n"));
        }
        return result;
    }
}
