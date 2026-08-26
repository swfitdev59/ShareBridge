using System.Net;

namespace ShareBridge.Core.Models;

/// <summary>
/// A node discovered on the LAN. Identity is per-process, so a peer that restarts
/// shows up as a new entry and the stale one ages out.
/// </summary>
public sealed class Peer
{
    public required string Id { get; init; }
    public required IPAddress Address { get; set; }
    public required int Port { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;

    public IPEndPoint EndPoint => new(Address, Port);

    public string DisplayLabel => string.IsNullOrWhiteSpace(Host) ? Name : $"{Name} ({Host})";

    public override string ToString() => $"{DisplayLabel} @ {Address}:{Port}";
}
