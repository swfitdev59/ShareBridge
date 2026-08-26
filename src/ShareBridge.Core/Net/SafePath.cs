namespace ShareBridge.Core.Net;

/// <summary>
/// Relative paths arrive from another machine, so they are treated as hostile until
/// proven otherwise: no roots, no drive letters, no ".." escapes, no reserved names.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Converts a wire-format relative path to a local one, or null if it is unsafe.</summary>
    public static string? SanitizeRelative(string? wirePath)
    {
        if (string.IsNullOrWhiteSpace(wirePath))
            return null;

        var segments = wirePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var clean = new List<string>(segments.Length);
        foreach (var raw in segments)
        {
            var segment = raw.Trim().TrimEnd('.', ' ');
            if (segment.Length == 0 || segment is "." or "..")
                return null;

            if (segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;

            var stem = Path.GetFileNameWithoutExtension(segment);
            if (ReservedNames.Contains(stem))
                segment = "_" + segment;

            clean.Add(segment);
        }

        return Path.Combine([.. clean]);
    }

    /// <summary>Resolves a sanitized relative path under <paramref name="rootFolder"/>,
    /// refusing anything that would land outside it.</summary>
    public static string? ResolveUnder(string rootFolder, string relativePath)
    {
        var root = Path.GetFullPath(rootFolder);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    /// <summary>Turns "report.pdf" into "report (1).pdf" when the name is taken.</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>Strips characters a peer name might contain that are illegal in a folder name.</summary>
    public static string SanitizeFolderName(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        return string.IsNullOrEmpty(cleaned) ? "peer" : cleaned;
    }
}
