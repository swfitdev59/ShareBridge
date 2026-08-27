using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareBridge.Core;

/// <summary>
/// User-editable configuration, persisted to %APPDATA%\ShareBridge\settings.json.
/// </summary>
public sealed class AppSettings
{
    // Picked from the unassigned 47100-47556 block so nothing well-known collides, and
    // kept below 49152 so a fixed listener never races the Windows ephemeral range.
    public const int DefaultDiscoveryPort = 47425;
    public const int DefaultTransferPort = 47426;

    // Ports shipped before the move off the registered 2425/2426 pair. Settings written by
    // those builds are migrated on load, otherwise upgraded machines would keep announcing
    // on the old discovery port and never see a fresh install.
    private const int LegacyDiscoveryPort = 2425;
    private const int LegacyTransferPort = 2426;

    /// <summary>
    /// Stable identity for this user on this machine, generated once and persisted.
    /// Keeps chat history attributable to the same peer across restarts, unlike the
    /// old per-process id. Empty until <see cref="Load"/> assigns one.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Name shown to other users on the LAN.</summary>
    public string DisplayName { get; set; } = Environment.UserName;

    /// <summary>Persist conversations to disk so they survive restarts.</summary>
    public bool SaveHistory { get; set; } = true;

    /// <summary>Pop a desktop notification when a message arrives and you are not looking.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Minimizing the window hides it to the system tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Closing the window hides it to the tray and keeps the app running.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>UDP port used for broadcast discovery. Must match across all peers.</summary>
    public int DiscoveryPort { get; set; } = DefaultDiscoveryPort;

    /// <summary>Preferred TCP port for messages and file streams. Falls back to an
    /// ephemeral port when already in use, so several instances can share one machine.</summary>
    public int TransferPort { get; set; } = DefaultTransferPort;

    /// <summary>Where received files are written.</summary>
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ShareBridge");

    /// <summary>Accept incoming files without prompting.</summary>
    public bool AutoAcceptFiles { get; set; }

    /// <summary>Seconds between discovery heartbeats.</summary>
    public int HeartbeatSeconds { get; set; } = 5;

    /// <summary>A peer is dropped after this many seconds without a heartbeat.</summary>
    public int PeerTimeoutSeconds { get; set; } = 20;

    /// <summary>Root folder for all app data (settings, history).</summary>
    [JsonIgnore]
    public static string AppDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ShareBridge");

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(AppDataFolder, "settings.json");

    /// <summary>Folder holding per-peer conversation transcripts.</summary>
    [JsonIgnore]
    public static string HistoryFolder { get; } = Path.Combine(AppDataFolder, "history");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings should never stop the app from starting.
            settings = new AppSettings();
        }

        var migrated = settings.Normalize();

        // The first run mints a stable id and writes it back, so it stays fixed from here on.
        if (string.IsNullOrWhiteSpace(settings.NodeId))
        {
            settings.NodeId = Guid.NewGuid().ToString("N");
            settings.Save();
        }
        else if (migrated)
        {
            settings.Save();
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the app keeps running with in-memory settings.
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// Repairs out-of-range values and migrates settings written by older builds.
    /// Returns true when a migration changed something worth writing back to disk.
    /// </summary>
    private bool Normalize()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
            DisplayName = Environment.UserName;

        // Only the untouched legacy defaults move. A port the user typed themselves is
        // their choice and stays put, even if it happens to sit low in the range.
        var migrated = false;
        if (DiscoveryPort == LegacyDiscoveryPort)
        {
            DiscoveryPort = DefaultDiscoveryPort;
            migrated = true;
        }

        if (TransferPort == LegacyTransferPort)
        {
            TransferPort = DefaultTransferPort;
            migrated = true;
        }

        if (DiscoveryPort is < 1 or > 65535)
            DiscoveryPort = DefaultDiscoveryPort;

        if (TransferPort is < 1 or > 65535)
            TransferPort = DefaultTransferPort;

        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            DownloadFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ShareBridge");
        }

        HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, 1, 60);
        PeerTimeoutSeconds = Math.Max(PeerTimeoutSeconds, HeartbeatSeconds * 3);

        return migrated;
    }
}
