using System.Text.Json;
using System.Text.Json.Serialization;
using ShareBridge.Core.Models;

namespace ShareBridge.Core.Protocol;

/// <summary>Shared JSON settings so both ends encode identically.</summary>
public static class Wire
{
    public const int ProtocolVersion = 1;

    /// <summary>Upper bound on a single JSON frame, to stop a hostile or broken peer
    /// from making us allocate arbitrarily.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---------------------------------------------------------------------------
// UDP discovery
// ---------------------------------------------------------------------------

public static class DiscoveryVerb
{
    /// <summary>"Who is out there?" — sent at startup; every peer answers with ANNOUNCE.</summary>
    public const string Hello = "HELLO";

    /// <summary>Periodic heartbeat, and the reply to HELLO.</summary>
    public const string Announce = "ANNOUNCE";

    /// <summary>Graceful shutdown notice.</summary>
    public const string Bye = "BYE";
}

public sealed class DiscoveryPacket
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Verb { get; set; } = DiscoveryVerb.Announce;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}

// ---------------------------------------------------------------------------
// TCP envelopes. Every exchange opens its own connection and starts with one
// length-prefixed JSON frame whose "kind" selects the shape below.
// ---------------------------------------------------------------------------

public static class FrameKind
{
    public const string Message = "MSG";
    public const string MessageAck = "MSG_ACK";
    public const string MessageEdit = "MSG_EDIT";
    public const string MessageDelete = "MSG_DELETE";
    public const string Offer = "OFFER";
    public const string OfferResult = "OFFER_RESULT";
    public const string Complete = "COMPLETE";
}

public sealed class MessageFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.Message;
    public PeerIdentity From { get; set; } = new();
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.Now;
}

public sealed class MessageAckFrame
{
    public string Kind { get; set; } = FrameKind.MessageAck;
    public string Id { get; set; } = string.Empty;
}

/// <summary>Edits the text of a message the sender previously delivered.</summary>
public sealed class MessageEditFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.MessageEdit;
    public PeerIdentity From { get; set; } = new();
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset EditedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Deletes a message the sender previously delivered.</summary>
public sealed class MessageDeleteFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.MessageDelete;
    public PeerIdentity From { get; set; } = new();
    public string Id { get; set; } = string.Empty;
}

public sealed class OfferItem
{
    /// <summary>Relative path with '/' separators. Validated by the receiver before use.</summary>
    public string Rel { get; set; } = string.Empty;

    public long Size { get; set; }
}

public sealed class OfferFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.Offer;
    public PeerIdentity From { get; set; } = new();
    public string TransferId { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public List<OfferItem> Items { get; set; } = [];
}

public sealed class OfferResultFrame
{
    public string Kind { get; set; } = FrameKind.OfferResult;
    public bool Accept { get; set; }
    public string? Reason { get; set; }
}

public sealed class CompleteFrame
{
    public string Kind { get; set; } = FrameKind.Complete;
    public bool Ok { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Minimal shape used to peek at "kind" before deserializing fully.</summary>
public sealed class FrameHeader
{
    public string Kind { get; set; } = string.Empty;
    public int V { get; set; } = Wire.ProtocolVersion;
}
