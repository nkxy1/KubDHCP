namespace KubDHCP.Core;

/// <summary>One data location per Windows user, shared by WinUI and the terminal.</summary>
public static class UserData
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KubDHCP");

    public static DataStore Open(string? profileRoot = null, string? executableDirectory = null)
    {
        var destination = profileRoot is null ? DirectoryPath : Path.Combine(Path.GetFullPath(profileRoot), "KubDHCP");
        if (Directory.Exists(destination)) return new DataStore(destination);
        var legacy = Path.Combine(executableDirectory ?? Path.GetDirectoryName(Environment.ProcessPath!)!, "KubDHCP.data");
        if (!Directory.Exists(legacy)) return new DataStore(destination);

        // Stage the complete legacy state before exposing the new directory. Never
        // merge into an existing user profile or delete the original lease state.
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        foreach (var name in new[] { "settings.json", "leases.json", "ranges.json", "settings.json.bak", "leases.json.bak", "ranges.json.bak", "KubDHCP.log", "Lizenzen.txt" })
        {
            var source = Path.Combine(legacy, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name));
        }
        Directory.Move(staging, destination);
        return new DataStore(destination);
    }
}
