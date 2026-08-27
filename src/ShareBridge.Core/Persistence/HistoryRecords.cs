namespace ShareBridge.Core.Persistence;

/// <summary>
/// One line in a per-peer transcript. A single shape carries both chat messages and
/// transfer summaries, distinguished by <see cref="Kind"/>, so the whole conversation
/// serialises as newline-delimited JSON and appends cheaply.
/// </summary>
public sealed class HistoryEntry
{
    public const string KindMessage = "message";
    public const string KindTransfer = "transfer";

    /// <summary>Control record: change the text of an earlier message (<see cref="Id"/>).</summary>
    public const string KindEdit = "edit";

    /// <summary>Control record: remove an earlier message (<see cref="Id"/>).</summary>
    public const string KindDelete = "delete";

    /// <summary>Control record: set one side's emoji on an earlier message (<see cref="Id"/>).</summary>
    public const string KindReaction = "reaction";

    public string Kind { get; set; } = KindMessage;
    public string Id { get; set; } = string.Empty;

    /// <summary>"Incoming" or "Outgoing".</summary>
    public string Direction { get; set; } = "Incoming";

    public DateTimeOffset Timestamp { get; set; }

    // --- message fields ---
    public string? Text { get; set; }
    public string? Status { get; set; }
    public bool Edited { get; set; }
    public bool Deleted { get; set; }

    // --- reply fields, set when a message quotes another ---
    public string? ReplyToId { get; set; }
    public string? ReplyToPreview { get; set; }
    public bool ReplyToFromSelf { get; set; }

    // --- reactions: each side's emoji on a message, from this machine's point of view ---
    public string? MyReaction { get; set; }
    public string? PeerReaction { get; set; }

    /// <summary>On a <see cref="KindReaction"/> record: whose emoji it is.</summary>
    public bool ReactionIsMine { get; set; }

    // --- picture fields, set when a message carries one ---
    public string? ImagePath { get; set; }
    public string? ImageName { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public long ImageBytes { get; set; }

    // --- transfer fields ---
    public string? Title { get; set; }
    public int ItemCount { get; set; }
    public long TotalBytes { get; set; }
    public string? DestinationFolder { get; set; }
    public string? TransferStatus { get; set; }
}

/// <summary>
/// Index entry letting a peer with saved history appear on startup — before, or even
/// without, being rediscovered on the network.
/// </summary>
public sealed class PeerRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public DateTimeOffset LastSeen { get; set; }
}
