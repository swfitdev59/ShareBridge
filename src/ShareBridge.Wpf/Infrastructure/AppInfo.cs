using System.Reflection;

namespace ShareBridge.Wpf;

/// <summary>Version and product details, read once from the running assembly.</summary>
public static class AppInfo
{
    /// <summary>Short marketing version, e.g. "1.0". Taken from the assembly's informational version.</summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Assembly attributes are embedded in the app, so this works in a single-file build
        // where Assembly.Location is empty. InformationalVersion carries the csproj <Version>.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var raw = !string.IsNullOrWhiteSpace(informational)
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString() ?? "1.0.0";

        // Present "1.0.0" as "1.0"; keep a patch component when it is non-zero.
        var parts = raw.Split('.');
        return parts.Length >= 3 && parts[2] == "0"
            ? $"{parts[0]}.{parts[1]}"
            : raw;
    }
}
