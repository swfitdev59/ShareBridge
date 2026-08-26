using System.Globalization;

namespace ShareBridge.Wpf.Infrastructure;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        if (value < 1024)
            return $"{value} B";

        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{size:0.#} {Units[unit]}");
    }

    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? string.Empty : $"{Bytes((long)bytesPerSecond)}/s";

    /// <summary>"14:32" for today, "Mon 14:32" this week, otherwise a short date.</summary>
    public static string Timestamp(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        if (local.Date == today)
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);

        return local.Date > today.AddDays(-6)
            ? local.ToString("ddd HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    public static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";

        var parts = name.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]))
            : name.Trim()[..1].ToUpperInvariant();
    }

    public static string Eta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || remainingBytes <= 0)
            return string.Empty;

        var seconds = remainingBytes / bytesPerSecond;
        if (seconds < 60)
            return $"{seconds:0} s left";

        return seconds < 3600
            ? $"{seconds / 60:0} min left"
            : $"{seconds / 3600:0.#} h left";
    }
}
