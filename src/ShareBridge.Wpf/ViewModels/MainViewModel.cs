using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ShareBridge.Core;
using ShareBridge.Core.Models;
using ShareBridge.Core.Net;
using ShareBridge.Core.Persistence;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HistoryStore _history;
    private readonly Dictionary<string, PeerViewModel> _peersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferViewModel> _transfersById = new(StringComparer.Ordinal);

    private readonly List<MessageViewModel> _conversationMatches = [];

    private ShareBridgeNode? _node;
    private PeerViewModel? _selectedPeer;
    private SearchResultViewModel? _selectedSearchResult;
    private int _currentMatchIndex = -1;
    private bool _windowActive = true;
    private string _draft = string.Empty;
    private string _statusText = "Starting…";
    private string _searchQuery = string.Empty;

    public MainViewModel(AppSettings settings, Dispatcher dispatcher)
    {
        Settings = settings;
        _dispatcher = dispatcher;
        _history = new HistoryStore(AppSettings.HistoryFolder) { Enabled = settings.SaveHistory };

        PeersView = (ListCollectionView)CollectionViewSource.GetDefaultView(Peers);
        PeersView.Filter = FilterPeer;
        PeersView.SortDescriptions.Add(new SortDescription(nameof(PeerViewModel.IsOnline), ListSortDirection.Descending));
        PeersView.SortDescriptions.Add(new SortDescription(nameof(PeerViewModel.Name), ListSortDirection.Ascending));
        PeersView.IsLiveSorting = true;
        PeersView.LiveSortingProperties.Add(nameof(PeerViewModel.IsOnline));
        PeersView.LiveSortingProperties.Add(nameof(PeerViewModel.Name));

        SendMessageCommand = new AsyncRelayCommand(_ => SendDraftAsync(), _ => CanSendMessage);
        SendFilesCommand = new RelayCommand(_ => RequestPickFiles?.Invoke(), _ => SelectedPeer is not null);
        SendFolderCommand = new RelayCommand(_ => RequestPickFolder?.Invoke(), _ => SelectedPeer is not null);
        OpenSettingsCommand = new RelayCommand(_ => RequestSettings?.Invoke());
        OpenDownloadsCommand = new RelayCommand(_ => OpenDownloadsFolder());
        ClearConversationCommand = new RelayCommand(_ => ClearConversation(), _ => SelectedPeer is not null);
        ExportConversationCommand = new RelayCommand(
            _ => RequestExport?.Invoke(),
            _ => SelectedPeer is { Timeline.Count: > 0 });
        NextMatchCommand = new RelayCommand(_ => NavigateMatch(1), _ => HasConversationMatches);
        PreviousMatchCommand = new RelayCommand(_ => NavigateMatch(-1), _ => HasConversationMatches);
    }

    public AppSettings Settings { get; }

    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    public ListCollectionView PeersView { get; }

    /// <summary>Transfers still in flight, shown in the bar under the conversation.</summary>
    public ObservableCollection<TransferViewModel> ActiveTransfers { get; } = [];

    /// <summary>Messages across all conversations that match the current search query.</summary>
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public ICommand SendMessageCommand { get; }
    public ICommand SendFilesCommand { get; }
    public ICommand SendFolderCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenDownloadsCommand { get; }
    public ICommand ClearConversationCommand { get; }
    public ICommand ExportConversationCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PreviousMatchCommand { get; }

    /// <summary>Set by the view: opens the file picker.</summary>
    public Action? RequestPickFiles { get; set; }

    /// <summary>Set by the view: opens the export save dialog.</summary>
    public Action? RequestExport { get; set; }

    /// <summary>Set by the view: opens the folder picker.</summary>
    public Action? RequestPickFolder { get; set; }

    /// <summary>Set by the view: opens the settings window.</summary>
    public Action? RequestSettings { get; set; }

    /// <summary>Set by the view: asks the user to accept an inbound transfer.</summary>
    public Func<TransferViewModel, Task<bool>>? RequestOfferDecision { get; set; }

    /// <summary>Set by the view: called when something arrives while unfocused.</summary>
    public Action? RequestAttention { get; set; }

    /// <summary>Set by the view: shows a desktop notification for a message (peer, text).</summary>
    public Action<PeerViewModel, string>? RequestNotification { get; set; }

    /// <summary>Raised when a search hit is opened, so the view can scroll to and flash it.</summary>
    public event Action<MessageViewModel>? ScrollToMessageRequested;

    /// <summary>Raised with the new total unread count, for the tray badge.</summary>
    public event Action<int>? UnreadTotalChanged;

    /// <summary>Unread messages across every conversation.</summary>
    public int TotalUnread => _peersById.Values.Sum(p => p.UnreadCount);

    /// <summary>
    /// Whether the window is currently in the foreground. The view keeps this in sync.
    /// A message only counts as "read" while you are actually looking at its conversation,
    /// so messages that arrive while the window is minimized, hidden in the tray, or in the
    /// background raise the unread count (and the tray badge) even for the open conversation.
    /// </summary>
    public bool WindowActive
    {
        get => _windowActive;
        set
        {
            if (_windowActive == value)
                return;

            _windowActive = value;

            // Coming back to the foreground marks the open conversation as read.
            if (value && SelectedPeer is { UnreadCount: > 0 } peer)
            {
                peer.UnreadCount = 0;
                RaiseUnread();
            }
        }
    }

    public PeerViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (!Set(ref _selectedPeer, value))
                return;

            if (value is not null)
                value.UnreadCount = 0;

            RaiseUnread();
            RecomputeConversationMatches();
            RaiseAll(nameof(HasSelection), nameof(CanSendMessage), nameof(ShowFindBar));
        }
    }

    public bool HasSelection => SelectedPeer is not null;

    public string Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value))
                Raise(nameof(CanSendMessage));
        }
    }

    public bool CanSendMessage => SelectedPeer is not null && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>Drives both the peer-list filter and the cross-conversation message search.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!Set(ref _searchQuery, value))
                return;

            PeersView.Refresh();
            RebuildSearchResults();
            RecomputeConversationMatches();
            RaiseAll(nameof(IsSearching), nameof(HasSearchResults), nameof(SearchTerm), nameof(ShowFindBar));
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchQuery);

    public bool HasSearchResults => SearchResults.Count > 0;

    /// <summary>The trimmed query, used to highlight matches inside message bubbles.</summary>
    public string SearchTerm => _searchQuery.Trim();

    public SearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (Set(ref _selectedSearchResult, value) && value is not null)
                OpenSearchResult(value);
        }
    }

    /// <summary>The find bar shows while searching with a conversation open.</summary>
    public bool ShowFindBar => IsSearching && SelectedPeer is not null;

    public bool HasConversationMatches => _conversationMatches.Count > 0;

    /// <summary>"3 of 12", "12 matches" before navigating, or "No matches".</summary>
    public string MatchPositionText
    {
        get
        {
            if (_conversationMatches.Count == 0)
                return "No matches";

            return _currentMatchIndex < 0
                ? $"{_conversationMatches.Count} match{(_conversationMatches.Count == 1 ? "" : "es")}"
                : $"{_currentMatchIndex + 1} of {_conversationMatches.Count}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string IdentityText => _node is null
        ? Settings.DisplayName
        : $"{_node.Self.Name} · {_node.Self.Host} · TCP {_node.ListeningPort}";

    public async Task StartAsync()
    {
        // Bring back peers and conversations from previous sessions before the network
        // comes up, so history is on screen immediately and discovery just marks who is online.
        LoadSavedConversations();

        _node = new ShareBridgeNode(Settings);

        _node.PeerDiscovered += (_, e) => Post(() => OnPeerDiscovered(e.Peer));
        _node.PeerUpdated += (_, e) => Post(() => OnPeerUpdated(e.Peer));
        _node.PeerLost += (_, e) => Post(() => OnPeerLost(e.Peer));
        _node.MessageReceived += (_, e) => Post(() => OnMessageReceived(e));
        _node.MessageEdited += (_, e) => Post(() => OnMessageEdited(e));
        _node.MessageDeleted += (_, e) => Post(() => OnMessageDeleted(e));
        _node.TransferStarted += (_, e) => Post(() => OnTransferStarted(e.Transfer));
        _node.TransferChanged += (_, e) => Post(() => OnTransferChanged(e.Transfer));
        _node.TransferFinished += (_, e) => Post(() => OnTransferFinished(e.Transfer));
        _node.OfferDecision = DecideOfferAsync;

        await _node.StartAsync();

        Raise(nameof(IdentityText));
        StatusText = $"Listening on UDP {Settings.DiscoveryPort} (discovery) and TCP {_node.ListeningPort}";
    }

    /// <summary>Applies edited settings; a changed name is re-broadcast immediately.</summary>
    public async Task ApplySettingsAsync()
    {
        Settings.Save();
        _history.Enabled = Settings.SaveHistory;

        if (_node is not null)
            await _node.RefreshIdentityAsync();

        Raise(nameof(IdentityText));
    }

    public async Task SendPathsAsync(IReadOnlyList<string> paths)
    {
        var peer = SelectedPeer;
        if (_node is null || peer is null || paths.Count == 0)
            return;

        try
        {
            await _node.SendFilesAsync(peer.Model, paths);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not send: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    // Core event handlers — all of these already run on the dispatcher.
    // -----------------------------------------------------------------------

    private void OnPeerDiscovered(Peer peer)
    {
        var vm = GetOrCreatePeer(peer);
        vm.ApplyModel(peer);
        vm.IsOnline = true;
        _history.UpsertPeer(vm.Id, vm.Name, vm.Host, DateTimeOffset.Now);
    }

    private void OnPeerUpdated(Peer peer)
    {
        if (_peersById.TryGetValue(peer.Id, out var vm))
        {
            vm.ApplyModel(peer);
            vm.IsOnline = true;
            _history.UpsertPeer(vm.Id, vm.Name, vm.Host, DateTimeOffset.Now);
        }
        else
        {
            OnPeerDiscovered(peer);
        }
    }

    private void OnPeerLost(Peer peer)
    {
        if (!_peersById.TryGetValue(peer.Id, out var vm))
            return;

        // Keep peers we have history with so the conversation stays readable;
        // drop the ones that never said anything.
        if (vm.Timeline.Count > 0 || _history.HasHistory(vm.Id))
        {
            vm.IsOnline = false;
        }
        else
        {
            var hadUnread = vm.UnreadCount > 0;
            _peersById.Remove(peer.Id);
            Peers.Remove(vm);
            if (ReferenceEquals(SelectedPeer, vm))
                SelectedPeer = null;
            if (hadUnread)
                RaiseUnread();
        }
    }

    private void OnMessageReceived(IncomingMessageEventArgs e)
    {
        var peer = _node?.FindPeer(e.From.Id);
        var vm = peer is not null
            ? GetOrCreatePeer(peer)
            : GetOrCreatePlaceholder(e.From.Id, e.From.Name, e.From.Host);

        vm.IsOnline = true;
        vm.Touch();
        vm.Timeline.Add(NewMessage(vm, e.Message));

        _history.UpsertPeer(vm.Id, vm.Name, vm.Host, DateTimeOffset.Now);
        _history.AppendMessage(vm.Id, e.Message);

        // "Read" only if the window is in the foreground AND this is the open conversation;
        // otherwise it counts as unread (drives the sidebar and tray badges).
        var beingViewed = _windowActive && ReferenceEquals(SelectedPeer, vm);
        if (!beingViewed)
        {
            vm.UnreadCount++;
            RaiseUnread();
        }

        RefreshSearchIfActive();
        RequestAttention?.Invoke();

        // The view suppresses this when the user is already looking at this conversation.
        if (Settings.ShowNotifications)
            RequestNotification?.Invoke(vm, e.Message.Text);

        StatusText = $"Message from {vm.Name}";
    }

    private void OnTransferStarted(Transfer transfer)
    {
        var vm = GetOrCreateTransfer(transfer);

        if (!ActiveTransfers.Contains(vm))
            ActiveTransfers.Add(vm);

        var peer = ResolvePeer(transfer);
        if (peer is not null && !peer.Timeline.Contains(vm))
        {
            peer.Touch();
            peer.Timeline.Add(vm);
        }

        if (transfer.Direction == TransferDirection.Incoming)
            RequestAttention?.Invoke();
    }

    private void OnTransferChanged(Transfer transfer) => GetOrCreateTransfer(transfer).Refresh();

    private void OnTransferFinished(Transfer transfer)
    {
        var vm = GetOrCreateTransfer(transfer);
        vm.Refresh();
        ActiveTransfers.Remove(vm);

        // A transfer that got as far as running (or beyond) is worth remembering;
        // a bare rejection before any bytes moved is noise.
        if (transfer.Status != TransferStatus.Rejected || transfer.TransferredBytes > 0)
        {
            var peer = ResolvePeer(transfer);
            if (peer is not null)
                _history.UpsertPeer(peer.Id, peer.Name, peer.Host, DateTimeOffset.Now);

            _history.AppendTransfer(transfer.PeerId, transfer);
        }

        StatusText = transfer.Status switch
        {
            TransferStatus.Completed when transfer.Direction == TransferDirection.Incoming =>
                $"Received {transfer.Title} from {transfer.PeerName}",
            TransferStatus.Completed => $"Sent {transfer.Title} to {transfer.PeerName}",
            TransferStatus.Rejected => $"{transfer.PeerName} declined {transfer.Title}",
            TransferStatus.Cancelled => $"Cancelled {transfer.Title}",
            TransferStatus.Failed => $"Transfer failed: {transfer.Error}",
            _ => StatusText,
        };
    }

    private Task<bool> DecideOfferAsync(Transfer transfer)
    {
        var handler = RequestOfferDecision;
        if (handler is null)
            return Task.FromResult(false);

        // Hop to the UI thread; the returned task completes when the user answers.
        return _dispatcher.InvokeAsync(() => handler(GetOrCreateTransfer(transfer))).Task.Unwrap();
    }

    // -----------------------------------------------------------------------

    private async Task SendDraftAsync()
    {
        var peer = SelectedPeer;
        var text = Draft.Trim();
        if (_node is null || peer is null || text.Length == 0)
            return;

        Draft = string.Empty;

        var pending = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            PeerId = peer.Id,
            Text = text,
            Direction = MessageDirection.Outgoing,
            Status = MessageStatus.Sending,
        };

        var bubble = NewMessage(peer, pending);
        peer.Timeline.Add(bubble);
        peer.Touch();

        var sent = await _node.SendMessageAsync(peer.Model, text);

        pending.Status = sent.Status;
        bubble.RefreshStatus();

        // Persist with the settled status so a reload shows delivered/failed, not "sending".
        _history.UpsertPeer(peer.Id, peer.Name, peer.Host, DateTimeOffset.Now);
        _history.AppendMessage(peer.Id, pending);
        RefreshSearchIfActive();

        if (sent.Status == MessageStatus.Failed)
            StatusText = $"{peer.Name} did not receive that message.";
    }

    private PeerViewModel? ResolvePeer(Transfer transfer)
    {
        if (_peersById.TryGetValue(transfer.PeerId, out var existing))
            return existing;

        var peer = _node?.FindPeer(transfer.PeerId);
        return peer is not null
            ? GetOrCreatePeer(peer)
            : GetOrCreatePlaceholder(transfer.PeerId, transfer.PeerName, string.Empty);
    }

    // -----------------------------------------------------------------------
    // History
    // -----------------------------------------------------------------------

    /// <summary>Recreates offline peer entries and their timelines from saved history.</summary>
    private void LoadSavedConversations()
    {
        foreach (var record in _history.KnownPeers)
        {
            if (!_history.HasHistory(record.Id))
                continue;

            var vm = GetOrCreatePlaceholder(record.Id, record.Name, record.Host);
            vm.IsOnline = false;
            LoadTimeline(vm);
        }
    }

    /// <summary>Reads one peer's transcript into its timeline. Runs once per peer.</summary>
    private void LoadTimeline(PeerViewModel vm)
    {
        if (vm.HistoryLoaded)
            return;

        vm.HistoryLoaded = true;

        foreach (var entry in _history.Load(vm.Id))
        {
            object item = entry.Kind == HistoryEntry.KindTransfer
                ? new TransferViewModel(BuildHistoricalTransfer(vm, entry))
                : NewMessage(vm, BuildHistoricalMessage(vm, entry));

            vm.Timeline.Add(item);
            vm.TouchTo(entry.Timestamp);
        }
    }

    private static ChatMessage BuildHistoricalMessage(PeerViewModel vm, HistoryEntry entry) => new()
    {
        Id = entry.Id,
        PeerId = vm.Id,
        Text = entry.Text ?? string.Empty,
        Direction = ParseEnum(entry.Direction, MessageDirection.Incoming),
        Timestamp = entry.Timestamp,
        Status = ParseEnum(entry.Status, MessageStatus.Delivered),
        Edited = entry.Edited,
        Deleted = entry.Deleted,
    };

    /// <summary>
    /// Opens the most recent editable message in the open conversation for editing — the
    /// Arrow-Up shortcut from an empty composer. Returns false if there is nothing to edit.
    /// </summary>
    public bool BeginEditLastMessage()
    {
        var last = SelectedPeer?.Timeline
            .OfType<MessageViewModel>()
            .LastOrDefault(m => m.CanModify);

        if (last is null)
            return false;

        last.BeginEditCommand.Execute(null);
        return true;
    }

    /// <summary>Creates a message view model wired to persist and sync its own edits/deletes.</summary>
    private MessageViewModel NewMessage(PeerViewModel peer, ChatMessage model)
    {
        var message = new MessageViewModel(model);
        message.EditCommitted += text => EditOwnMessage(peer, message, text);
        message.DeleteRequested += () => DeleteOwnMessage(peer, message);
        return message;
    }

    private void EditOwnMessage(PeerViewModel peer, MessageViewModel message, string newText)
    {
        message.Model.Text = newText;
        message.Model.Edited = true;
        message.RefreshText();

        _history.AppendEdit(peer.Id, message.Model.Id, newText);
        RefreshSearchIfActive();

        _ = SyncEditAsync(peer, message.Model.Id, newText);
    }

    private void DeleteOwnMessage(PeerViewModel peer, MessageViewModel message)
    {
        MarkDeleted(message);
        _history.AppendDelete(peer.Id, message.Model.Id);
        RefreshSearchIfActive();

        _ = SyncDeleteAsync(peer, message.Model.Id);
    }

    /// <summary>Turns a message into a "deleted" placeholder, dropping its text.</summary>
    private static void MarkDeleted(MessageViewModel message)
    {
        message.Model.Deleted = true;
        message.Model.Edited = false;
        message.Model.Text = string.Empty;
        message.RefreshText();
    }

    private async Task SyncEditAsync(PeerViewModel peer, string messageId, string newText)
    {
        if (_node is null)
            return;

        var delivered = await _node.EditMessageAsync(peer.Model, messageId, newText);
        if (!delivered)
            StatusText = $"Edit saved here, but {peer.Name} may not have received it.";
    }

    private async Task SyncDeleteAsync(PeerViewModel peer, string messageId)
    {
        if (_node is null)
            return;

        var delivered = await _node.DeleteMessageAsync(peer.Model, messageId);
        if (!delivered)
            StatusText = $"Deleted here, but {peer.Name} may still see it.";
    }

    /// <summary>Applies an edit the peer made to one of their own messages.</summary>
    private void OnMessageEdited(MessageEditEventArgs e)
    {
        if (!_peersById.TryGetValue(e.From.Id, out var peer))
            return;

        var message = FindMessage(peer, e.MessageId);
        if (message is null)
            return;

        message.Model.Text = e.Text;
        message.Model.Edited = true;
        message.RefreshText();

        _history.AppendEdit(peer.Id, e.MessageId, e.Text);
        RefreshSearchIfActive();
    }

    /// <summary>Applies a delete the peer made to one of their own messages.</summary>
    private void OnMessageDeleted(MessageDeleteEventArgs e)
    {
        if (!_peersById.TryGetValue(e.From.Id, out var peer))
            return;

        var message = FindMessage(peer, e.MessageId);
        if (message is null)
            return;

        MarkDeleted(message);
        _history.AppendDelete(peer.Id, e.MessageId);
        RefreshSearchIfActive();
    }

    private static MessageViewModel? FindMessage(PeerViewModel peer, string messageId) =>
        peer.Timeline.OfType<MessageViewModel>().FirstOrDefault(m => m.Model.Id == messageId);

    private static Transfer BuildHistoricalTransfer(PeerViewModel vm, HistoryEntry entry) =>
        Transfer.CreateHistorical(
            entry.Id,
            ParseEnum(entry.Direction, TransferDirection.Incoming),
            vm.Id,
            vm.Name,
            entry.Title ?? "files",
            entry.ItemCount,
            entry.TotalBytes,
            ParseEnum(entry.TransferStatus, TransferStatus.Completed),
            entry.DestinationFolder,
            entry.Timestamp);

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(value, out var parsed) ? parsed : fallback;

    private void ClearConversation()
    {
        var peer = SelectedPeer;
        if (peer is null)
            return;

        peer.Timeline.Clear();
        _history.Clear(peer.Id);

        // With nothing left to show, drop an offline peer from the list entirely.
        if (!peer.IsOnline)
        {
            _peersById.Remove(peer.Id);
            Peers.Remove(peer);
            SelectedPeer = null;
        }

        RefreshSearchIfActive();
    }

    /// <summary>Wipes every saved conversation. Online peers stay listed but emptied.</summary>
    public void ClearAllHistory()
    {
        _history.ClearAll();

        foreach (var peer in Peers.ToList())
        {
            peer.Timeline.Clear();
            peer.HistoryLoaded = true;

            if (!peer.IsOnline)
            {
                _peersById.Remove(peer.Id);
                Peers.Remove(peer);
            }
        }

        SelectedPeer = null;
        RaiseUnread();
        RefreshSearchIfActive();
        StatusText = "Chat history cleared.";
    }

    /// <summary>Default file name the export dialog should offer for the open conversation.</summary>
    public string SuggestedExportName =>
        SelectedPeer is null ? "conversation" : $"{SafeFileStem(SelectedPeer.Name)}-chat";

    /// <summary>Writes the open conversation to a plain-text or Markdown transcript.</summary>
    public void ExportConversationTo(string path)
    {
        var peer = SelectedPeer;
        if (peer is null)
            return;

        try
        {
            var markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, BuildTranscript(peer, markdown));
            StatusText = $"Exported conversation with {peer.Name} to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not export the conversation: {ex.Message}";
        }
    }

    private static string BuildTranscript(PeerViewModel peer, bool markdown)
    {
        var sb = new System.Text.StringBuilder();
        var rule = markdown ? "---" : new string('-', 48);

        sb.AppendLine($"Conversation with {peer.Name}");
        if (!string.IsNullOrWhiteSpace(peer.Subtitle))
            sb.AppendLine(peer.Subtitle);
        sb.AppendLine($"Exported {DateTimeOffset.Now.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine(rule);
        sb.AppendLine();

        foreach (var item in peer.Timeline)
        {
            switch (item)
            {
                case MessageViewModel m:
                    var stamp = m.Model.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    var who = m.Model.Direction == MessageDirection.Outgoing ? "You" : peer.Name;
                    var failed = m.Model.Status == MessageStatus.Failed ? "  (not delivered)" : string.Empty;
                    sb.AppendLine($"[{stamp}] {who}: {m.Model.Text}{failed}");
                    break;

                case TransferViewModel t:
                    var ts = t.Model.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    var verb = t.IsOutgoing ? "Sent" : "Received";
                    sb.AppendLine($"[{ts}] {verb} \"{t.Model.Title}\" " +
                                  $"({Format.Bytes(t.Model.TotalBytes)}) — {t.Model.Status}");
                    break;
            }
        }

        return sb.ToString();
    }

    private static string SafeFileStem(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "conversation" : cleaned;
    }

    public void OpenHistoryFolder()
    {
        try
        {
            Directory.CreateDirectory(_history.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_history.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the history folder: {ex.Message}";
        }
    }

    private PeerViewModel GetOrCreatePeer(Peer peer)
    {
        if (_peersById.TryGetValue(peer.Id, out var existing))
            return existing;

        var vm = new PeerViewModel(peer);
        _peersById[peer.Id] = vm;
        Peers.Add(vm);
        return vm;
    }

    /// <summary>
    /// Stand-in for a peer we have heard from but not discovered. Real discovery data
    /// replaces the fields as soon as their next broadcast lands.
    /// </summary>
    private PeerViewModel GetOrCreatePlaceholder(string id, string name, string host)
    {
        if (_peersById.TryGetValue(id, out var existing))
            return existing;

        var peer = new Peer
        {
            Id = id,
            Address = System.Net.IPAddress.None,
            Port = 0,
            Name = string.IsNullOrWhiteSpace(name) ? "Unknown" : name,
            Host = host,
        };

        return GetOrCreatePeer(peer);
    }

    private TransferViewModel GetOrCreateTransfer(Transfer transfer)
    {
        if (_transfersById.TryGetValue(transfer.Id, out var existing))
            return existing;

        var vm = new TransferViewModel(transfer);
        _transfersById[transfer.Id] = vm;
        return vm;
    }

    private bool FilterPeer(object item)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
            return true;

        if (item is not PeerViewModel peer)
            return false;

        var q = _searchQuery.Trim();
        return peer.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || peer.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
               || peer.AddressText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scans every loaded conversation for messages containing the query.</summary>
    private void RebuildSearchResults()
    {
        SearchResults.Clear();

        var q = _searchQuery.Trim();
        if (q.Length == 0)
            return;

        const int maxResults = 300;
        var matches = new List<SearchResultViewModel>();

        foreach (var peer in _peersById.Values)
        {
            foreach (var item in peer.Timeline)
            {
                if (item is MessageViewModel message
                    && message.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new SearchResultViewModel(peer, message.Model));
                }
            }
        }

        matches.Sort((a, b) => b.Message.Timestamp.CompareTo(a.Message.Timestamp));

        foreach (var result in matches.Take(maxResults))
            SearchResults.Add(result);
    }

    /// <summary>Opens the conversation for a search hit and asks the view to reveal the message.</summary>
    private void OpenSearchResult(SearchResultViewModel result)
    {
        SelectedPeer = result.Peer; // recomputes the conversation matches for the find bar

        // Find the timeline item wrapping this exact message so the view can scroll to it.
        var target = result.Peer.Timeline
            .OfType<MessageViewModel>()
            .FirstOrDefault(m => ReferenceEquals(m.Model, result.Message));

        if (target is null)
            return;

        // Line the find bar up with the clicked message so Next/Prev continue from here.
        var index = _conversationMatches.IndexOf(target);
        if (index >= 0)
        {
            _currentMatchIndex = index;
            Raise(nameof(MatchPositionText));
        }

        ScrollToMessageRequested?.Invoke(target);
    }

    /// <summary>Rebuilds the list of messages in the open conversation that match the query.</summary>
    private void RecomputeConversationMatches()
    {
        _conversationMatches.Clear();
        _currentMatchIndex = -1;

        var term = SearchTerm;
        if (SelectedPeer is not null && term.Length > 0)
        {
            foreach (var item in SelectedPeer.Timeline)
            {
                if (item is MessageViewModel message
                    && message.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    _conversationMatches.Add(message);
                }
            }
        }

        RaiseAll(nameof(HasConversationMatches), nameof(MatchPositionText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Moves to the next or previous match in the open conversation, wrapping around.</summary>
    private void NavigateMatch(int delta)
    {
        var count = _conversationMatches.Count;
        if (count == 0)
            return;

        _currentMatchIndex = _currentMatchIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : ((_currentMatchIndex + delta) % count + count) % count;

        Raise(nameof(MatchPositionText));
        ScrollToMessageRequested?.Invoke(_conversationMatches[_currentMatchIndex]);
    }

    private void RaiseUnread() => UnreadTotalChanged?.Invoke(TotalUnread);

    /// <summary>Keeps an open search current when the conversations behind it change.</summary>
    private void RefreshSearchIfActive()
    {
        if (!IsSearching)
            return;

        RebuildSearchResults();
        Raise(nameof(HasSearchResults));

        // Preserve the caret position in the find bar when a new message shifts the list.
        var current = _currentMatchIndex >= 0 && _currentMatchIndex < _conversationMatches.Count
            ? _conversationMatches[_currentMatchIndex]
            : null;

        RecomputeConversationMatches();

        if (current is not null)
        {
            var index = _conversationMatches.IndexOf(current);
            if (index >= 0)
            {
                _currentMatchIndex = index;
                Raise(nameof(MatchPositionText));
            }
        }
    }

    private void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.DownloadFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.DownloadFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the downloads folder: {ex.Message}";
        }
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.InvokeAsync(action);
    }

    public async ValueTask DisposeAsync()
    {
        // Persist the peer index one last time to capture the latest LastSeen values.
        _history.Flush();

        if (_node is not null)
            await _node.DisposeAsync();
    }
}
