using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ShareBridge.Core.Models;
using ShareBridge.Core.Protocol;

namespace ShareBridge.Core.Net;

public sealed class PeerEventArgs(Peer peer) : EventArgs
{
    public Peer Peer { get; } = peer;
}

/// <summary>
/// IP-Msg style presence over UDP broadcast: HELLO on startup, ANNOUNCE as a
/// heartbeat, BYE on exit. Peers that stop announcing are reaped after a timeout.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private readonly AppSettings _settings;
    private readonly PeerIdentity _self;
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly Random _jitter = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private Task? _reapLoop;

    public DiscoveryService(AppSettings settings, PeerIdentity self)
    {
        _settings = settings;
        _self = self;
    }

    public event EventHandler<PeerEventArgs>? PeerDiscovered;
    public event EventHandler<PeerEventArgs>? PeerUpdated;
    public event EventHandler<PeerEventArgs>? PeerLost;
    public event EventHandler<string>? Log;

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToList();

    public Peer? Find(string id) => _peers.GetValueOrDefault(id);

    /// <summary>
    /// Records a peer learned outside of discovery — typically the sender of an inbound
    /// TCP exchange whose broadcast we missed. Makes them selectable right away.
    /// </summary>
    public void TrackExternal(PeerIdentity identity, IPAddress address)
    {
        if (string.IsNullOrEmpty(identity.Id) || identity.Id == _self.Id)
            return;

        Track(
            new DiscoveryPacket
            {
                Verb = DiscoveryVerb.Announce,
                Id = identity.Id,
                Name = identity.Name,
                Host = identity.Host,
                Port = identity.Port,
            },
            address);
    }

    public void Start()
    {
        if (_udp is not null)
            return;

        _udp = new UdpClient(AddressFamily.InterNetwork)
        {
            ExclusiveAddressUse = false,
        };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.DiscoveryPort));
        _udp.EnableBroadcast = true;

        // Without this, a single ICMP port-unreachable makes every later
        // ReceiveAsync throw ConnectionReset on Windows.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(ct), ct);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(ct), ct);
        _reapLoop = Task.Run(() => ReapLoopAsync(ct), ct);

        Log?.Invoke(this, $"Discovery listening on UDP {_settings.DiscoveryPort}");
    }

    /// <summary>Broadcasts HELLO so every peer answers immediately instead of waiting for a heartbeat.</summary>
    public Task AnnounceArrivalAsync() => BroadcastAsync(DiscoveryVerb.Hello, CancellationToken.None);

    public Task AnnounceAsync() => BroadcastAsync(DiscoveryVerb.Announce, CancellationToken.None);

    public Task AnnounceDepartureAsync() => BroadcastAsync(DiscoveryVerb.Bye, CancellationToken.None);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            try
            {
                HandlePacket(result);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Someone else's traffic on our port; ignore it.
            }
        }
    }

    private void HandlePacket(UdpReceiveResult result)
    {
        var packet = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer, Wire.Json);
        if (packet is null || packet.V != Wire.ProtocolVersion || string.IsNullOrEmpty(packet.Id))
            return;

        if (packet.Id == _self.Id)
            return; // our own broadcast, echoed back to us

        switch (packet.Verb)
        {
            case DiscoveryVerb.Bye:
                if (_peers.TryRemove(packet.Id, out var gone))
                    PeerLost?.Invoke(this, new PeerEventArgs(gone));
                break;

            case DiscoveryVerb.Hello:
                Track(packet, result.RemoteEndPoint.Address);
                // Answer so the newcomer learns about us right away. Jitter keeps
                // a busy LAN from replying in one burst.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_jitter.Next(50, 400)).ConfigureAwait(false);
                    await AnnounceAsync().ConfigureAwait(false);
                });
                break;

            case DiscoveryVerb.Announce:
                Track(packet, result.RemoteEndPoint.Address);
                break;
        }
    }

    private void Track(DiscoveryPacket packet, IPAddress address)
    {
        if (packet.Port is < 1 or > 65535)
            return;

        var isNew = false;
        var changed = false;

        var peer = _peers.AddOrUpdate(
            packet.Id,
            _ =>
            {
                isNew = true;
                return new Peer
                {
                    Id = packet.Id,
                    Address = address,
                    Port = packet.Port,
                    Name = packet.Name,
                    Host = packet.Host,
                    LastSeen = DateTimeOffset.Now,
                };
            },
            (_, existing) =>
            {
                changed = !existing.Address.Equals(address)
                          || existing.Port != packet.Port
                          || existing.Name != packet.Name
                          || existing.Host != packet.Host;

                existing.Address = address;
                existing.Port = packet.Port;
                existing.Name = packet.Name;
                existing.Host = packet.Host;
                existing.LastSeen = DateTimeOffset.Now;
                return existing;
            });

        if (isNew)
            PeerDiscovered?.Invoke(this, new PeerEventArgs(peer));
        else if (changed)
            PeerUpdated?.Invoke(this, new PeerEventArgs(peer));
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        await BroadcastAsync(DiscoveryVerb.Hello, ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await BroadcastAsync(DiscoveryVerb.Announce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReapLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var cutoff = DateTimeOffset.Now.AddSeconds(-_settings.PeerTimeoutSeconds);
                foreach (var peer in _peers.Values)
                {
                    if (peer.LastSeen < cutoff && _peers.TryRemove(peer.Id, out var lost))
                        PeerLost?.Invoke(this, new PeerEventArgs(lost));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BroadcastAsync(string verb, CancellationToken ct)
    {
        var udp = _udp;
        if (udp is null)
            return;

        var packet = new DiscoveryPacket
        {
            Verb = verb,
            Id = _self.Id,
            Name = _self.Name,
            Host = _self.Host,
            Port = _self.Port,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Wire.Json);

        foreach (var target in BroadcastTargets())
        {
            try
            {
                await udp.SendAsync(bytes, new IPEndPoint(target, _settings.DiscoveryPort), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A single dead adapter must not stop the others.
            }
        }
    }

    /// <summary>
    /// 255.255.255.255 only reaches the segment behind the default route, so we also
    /// send the directed broadcast of every up IPv4 interface to cover multi-NIC hosts.
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask is null)
                        continue;

                    var addr = info.Address.GetAddressBytes();
                    var mask = info.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4)
                        continue;

                    var directed = new byte[4];
                    for (var i = 0; i < 4; i++)
                        directed[i] = (byte)(addr[i] | ~mask[i]);

                    targets.Add(new IPAddress(directed));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the global broadcast address alone.
        }

        return targets;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        try
        {
            await AnnounceDepartureAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _udp?.Dispose();
        _udp = null;

        foreach (var task in new[] { _receiveLoop, _heartbeatLoop, _reapLoop })
        {
            if (task is null)
                continue;

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
    }
}
