using System.Text;
using System.Text.Json;
using ShareBridge.Core.Models;

namespace ShareBridge.Core.Persistence;

/// <summary>
/// Persists conversations under <c>%APPDATA%\ShareBridge\history</c>: one
/// newline-delimited JSON file per peer, plus a small index so peers with history
/// show up at startup. All access is serialised by a single lock — writes are tiny
/// and infrequent, so simplicity wins over per-file locking.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _folder;
    private readonly string _indexPath;
    private readonly int _maxEntriesPerPeer;
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerRecord> _index;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public HistoryStore(string folder, int maxEntriesPerPeer = 2000)
    {
        _folder = folder;
        _indexPath = Path.Combine(folder, "peers.json");
        _maxEntriesPerPeer = Math.Max(50, maxEntriesPerPeer);
        _index = LoadIndex();
    }

    /// <summary>When false, every write is a no-op; reads still work for what already exists.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Peers known from previous sessions, most-recently-seen first.</summary>
    public IReadOnlyList<PeerRecord> KnownPeers
    {
        get
        {
            lock (_gate)
                return _index.Values.OrderByDescending(p => p.LastSeen).ToList();
        }
    }

    public bool HasHistory(string peerId)
    {
        var path = FileFor(peerId);
        if (path is null)
            return false;

        lock (_gate)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
    }

    public IReadOnlyList<HistoryEntry> Load(string peerId)
    {
        var path = FileFor(peerId);
        if (path is null)
            return [];

        lock (_gate)
        {
            if (!File.Exists(path))
                return [];

            var entries = new List<HistoryEntry>();
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                        if (entry is not null)
                            entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // Skip a single corrupt line rather than losing the whole transcript.
                    }
                }
            }
            catch (IOException)
            {
                return entries;
            }

            entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // Trim runaway files back to the cap, keeping the most recent.
            if (entries.Count > _maxEntriesPerPeer)
            {
                entries = entries.Skip(entries.Count - _maxEntriesPerPeer).ToList();
                if (Enabled)
                    Rewrite(path, entries);
            }

            return Reconcile(entries);
        }
    }

    /// <summary>
    /// Folds edit and delete control records into the message/transfer entries they target,
    /// then drops the control records. The result is the conversation as it stands now.
    /// </summary>
    private static List<HistoryEntry> Reconcile(List<HistoryEntry> entries)
    {
        var result = new List<HistoryEntry>();
        var byId = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case HistoryEntry.KindEdit:
                    if (!string.IsNullOrEmpty(entry.Id) && byId.TryGetValue(entry.Id, out var edited)
                        && edited.Kind == HistoryEntry.KindMessage)
                    {
                        edited.Text = entry.Text;
                        edited.Edited = true;
                    }
                    break;

                case HistoryEntry.KindDelete:
                    // Deleted messages and transfers are removed outright — no placeholder.
                    if (!string.IsNullOrEmpty(entry.Id) && byId.Remove(entry.Id, out var removed))
                        result.Remove(removed);
                    break;

                case HistoryEntry.KindReaction:
                    if (!string.IsNullOrEmpty(entry.Id) && byId.TryGetValue(entry.Id, out var reacted))
                    {
                        // An empty emoji is how a reaction is taken back.
                        var emoji = string.IsNullOrEmpty(entry.Text) ? null : entry.Text;
                        if (entry.ReactionIsMine)
                            reacted.MyReaction = emoji;
                        else
                            reacted.PeerReaction = emoji;
                    }
                    break;

                default:
                    // A later record for the same id supersedes the earlier one in place
                    // (e.g. a message re-sent on retry moves from Failed to Delivered),
                    // keeping its original position and timestamp.
                    if (!string.IsNullOrEmpty(entry.Id) && byId.TryGetValue(entry.Id, out var prior)
                        && prior.Kind == entry.Kind)
                    {
                        prior.Text = entry.Text;
                        prior.Status = entry.Status;
                        prior.Edited = entry.Edited;
                        prior.Deleted = entry.Deleted;
                        prior.TransferStatus = entry.TransferStatus;
                        prior.ImagePath = entry.ImagePath;
                        prior.ImageName = entry.ImageName;
                        prior.ImageWidth = entry.ImageWidth;
                        prior.ImageHeight = entry.ImageHeight;
                        prior.ImageBytes = entry.ImageBytes;
                        prior.MyReaction = entry.MyReaction;
                        prior.PeerReaction = entry.PeerReaction;
                    }
                    else
                    {
                        result.Add(entry);
                        if (!string.IsNullOrEmpty(entry.Id))
                            byId[entry.Id] = entry;
                    }
                    break;
            }
        }

        return result;
    }

    public void AppendMessage(string peerId, ChatMessage message)
    {
        if (!Enabled)
            return;

        Append(peerId, new HistoryEntry
        {
            Kind = HistoryEntry.KindMessage,
            Id = message.Id,
            Direction = message.Direction.ToString(),
            Timestamp = message.Timestamp,
            Text = message.Text,
            Status = message.Status.ToString(),
            Edited = message.Edited,
            Deleted = message.Deleted,
            ImagePath = message.ImagePath,
            ImageName = message.ImageName,
            ImageWidth = message.ImageWidth,
            ImageHeight = message.ImageHeight,
            ImageBytes = message.ImageBytes,
            ReplyToId = message.ReplyToId,
            ReplyToPreview = message.ReplyToPreview,
            ReplyToFromSelf = message.ReplyToFromSelf,
            MyReaction = message.MyReaction,
            PeerReaction = message.PeerReaction,
        });
    }

    /// <summary>Records one side's emoji on a message; applied on the next load.</summary>
    public void AppendReaction(string peerId, string messageId, string? emoji, bool mine)
    {
        if (!Enabled)
            return;

        Append(peerId, new HistoryEntry
        {
            Kind = HistoryEntry.KindReaction,
            Id = messageId,
            Text = emoji ?? string.Empty,
            ReactionIsMine = mine,
            Timestamp = DateTimeOffset.Now,
        });
    }

    /// <summary>Records that a message's text changed; applied on the next load.</summary>
    public void AppendEdit(string peerId, string messageId, string newText)
    {
        if (!Enabled)
            return;

        Append(peerId, new HistoryEntry
        {
            Kind = HistoryEntry.KindEdit,
            Id = messageId,
            Text = newText,
            Timestamp = DateTimeOffset.Now,
        });
    }

    /// <summary>Records that a message was deleted; applied on the next load.</summary>
    public void AppendDelete(string peerId, string messageId)
    {
        if (!Enabled)
            return;

        Append(peerId, new HistoryEntry
        {
            Kind = HistoryEntry.KindDelete,
            Id = messageId,
            Timestamp = DateTimeOffset.Now,
        });
    }

    public void AppendTransfer(string peerId, Transfer transfer)
    {
        if (!Enabled)
            return;

        Append(peerId, new HistoryEntry
        {
            Kind = HistoryEntry.KindTransfer,
            Id = transfer.Id,
            Direction = transfer.Direction.ToString(),
            Timestamp = transfer.StartedAt,
            Title = transfer.Title,
            ItemCount = transfer.Items.Count,
            TotalBytes = transfer.TotalBytes,
            DestinationFolder = transfer.DestinationFolder,
            TransferStatus = transfer.Status.ToString(),
        });
    }

    /// <summary>Removes a peer's transcript and its index entry.</summary>
    public void Clear(string peerId)
    {
        var path = FileFor(peerId);

        lock (_gate)
        {
            if (path is not null && File.Exists(path))
                TryDelete(path);

            if (_index.Remove(peerId))
                SaveIndexLocked();
        }
    }

    /// <summary>Deletes every transcript and empties the index.</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_folder))
                {
                    foreach (var file in Directory.EnumerateFiles(_folder, "*.jsonl"))
                        TryDelete(file);
                }
            }
            catch (IOException)
            {
            }

            _index.Clear();
            SaveIndexLocked();
        }
    }

    /// <summary>Records or refreshes a peer in the index. The index is only rewritten
    /// when identifying details change, not on every heartbeat.</summary>
    public void UpsertPeer(string id, string name, string host, DateTimeOffset lastSeen)
    {
        if (!Enabled || string.IsNullOrEmpty(id))
            return;

        lock (_gate)
        {
            if (_index.TryGetValue(id, out var existing))
            {
                var changed = existing.Name != name || existing.Host != host;
                existing.Name = name;
                existing.Host = host;
                existing.LastSeen = lastSeen;
                if (changed)
                    SaveIndexLocked();
            }
            else
            {
                _index[id] = new PeerRecord { Id = id, Name = name, Host = host, LastSeen = lastSeen };
                SaveIndexLocked();
            }
        }
    }

    /// <summary>Writes the current index to disk, e.g. on shutdown to capture LastSeen.</summary>
    public void Flush()
    {
        if (!Enabled)
            return;

        lock (_gate)
            SaveIndexLocked();
    }

    public void OpenFolderSafe()
    {
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (IOException)
        {
        }
    }

    public string Folder => _folder;

    // -----------------------------------------------------------------------

    private void Append(string peerId, HistoryEntry entry)
    {
        var path = FileFor(peerId);
        if (path is null)
            return;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var line = JsonSerializer.Serialize(entry, Json) + "\n";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing history is preferable to interrupting the conversation.
            }
        }
    }

    private static void Rewrite(string path, IReadOnlyList<HistoryEntry> entries)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
                sb.Append(JsonSerializer.Serialize(entry, Json)).Append('\n');

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Dictionary<string, PeerRecord> LoadIndex()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                var records = JsonSerializer.Deserialize<List<PeerRecord>>(File.ReadAllText(_indexPath), Json);
                if (records is not null)
                    return records.Where(r => !string.IsNullOrEmpty(r.Id)).ToDictionary(r => r.Id, r => r);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Dictionary<string, PeerRecord>(StringComparer.Ordinal);
    }

    private void SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var json = JsonSerializer.Serialize(_index.Values.ToList(), Json);
            File.WriteAllText(_indexPath, json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Maps a peer id to its transcript path, rejecting anything unsafe as a file name.</summary>
    private string? FileFor(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return null;

        var safe = new string(peerId.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

        if (safe.Length == 0)
            return null;

        if (safe.Length > 64)
            safe = safe[..64];

        return Path.Combine(_folder, safe + ".jsonl");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
