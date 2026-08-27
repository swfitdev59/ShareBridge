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

    /// <summary>
    /// Largest picture that may travel as an inline chat message. Anything bigger belongs
    /// in a file transfer, which is resumable-ish, cancellable and asks before writing.
    /// </summary>
    public const long MaxImageBytes = 32L * 1024 * 1024;

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
    public const string Image = "IMG";
    public const string MessageAck = "MSG_ACK";
    public const string MessageEdit = "MSG_EDIT";
    public const string MessageDelete = "MSG_DELETE";
    public const string Reaction = "REACT";
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

    /// <summary>Set when this message replies to another. Older peers ignore these fields
    /// and simply show it as an ordinary message.</summary>
    public string? ReplyToId { get; set; }

    public string? ReplyToPreview { get; set; }

    /// <summary>True when the quoted message was written by the sender of this frame.</summary>
    public bool ReplyToFromSelf { get; set; }
}

/// <summary>
/// Puts, changes or clears the sender's emoji on an earlier message. One reaction per
/// person per message: a new emoji replaces the old, and an empty one removes it.
/// </summary>
public sealed class ReactionFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.Reaction;
    public PeerIdentity From { get; set; } = new();

    /// <summary>Id of the message being reacted to.</summary>
    public string Id { get; set; } = string.Empty;

    public string Emoji { get; set; } = string.Empty;
}

/// <summary>
/// Header for a picture sent inline in the conversation. The receiver answers with an
/// OFFER_RESULT; on an accept, exactly <see cref="Size"/> raw bytes follow on the same
/// connection and the receiver finishes with a MSG_ACK.
/// </summary>
public sealed class ImageFrame
{
    public int V { get; set; } = Wire.ProtocolVersion;
    public string Kind { get; set; } = FrameKind.Image;
    public PeerIdentity From { get; set; } = new();
    public string Id { get; set; } = string.Empty;

    /// <summary>File name only — no path. The receiver sanitizes it before use.</summary>
    public string FileName { get; set; } = "image.png";

    public long Size { get; set; }

    /// <summary>Pixel size, so the far end can label the picture without decoding it.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Optional text shown under the picture; edits later replace it.</summary>
    public string Caption { get; set; } = string.Empty;

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Set when the picture is a reply. See <see cref="MessageFrame.ReplyToId"/>.</summary>
    public string? ReplyToId { get; set; }

    public string? ReplyToPreview { get; set; }

    public bool ReplyToFromSelf { get; set; }
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
