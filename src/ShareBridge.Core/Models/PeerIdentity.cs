namespace ShareBridge.Core.Models;

/// <summary>
/// The self-description a node puts on the wire. Sent in discovery packets and
/// again at the head of every TCP exchange so the receiver can attribute it.
/// </summary>
public sealed class PeerIdentity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port the node is actually listening on (may differ from the configured one).</summary>
    public int Port { get; set; }
}
