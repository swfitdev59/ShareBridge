using System.Collections.ObjectModel;
using System.Net;
using ShareBridge.Core.Models;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

/// <summary>
/// A peer plus the conversation held with them. The timeline mixes
/// <see cref="MessageViewModel"/> and <see cref="TransferViewModel"/> items.
/// </summary>
public sealed class PeerViewModel(Peer peer) : ObservableObject
{
    private int _unreadCount;
    private bool _isOnline = true;

    public Peer Model { get; } = peer;

    public string Id => Model.Id;

    /// <summary>True once this peer's saved history has been read into the timeline.</summary>
    public bool HistoryLoaded { get; set; }

    public ObservableCollection<object> Timeline { get; } = [];

    public string Name => Model.Name;

    public string Host => Model.Host;

    public string AddressText => $"{Model.Address}:{Model.Port}";

    public string Initials => Format.Initials(Model.Name);

    public string Subtitle => string.IsNullOrWhiteSpace(Model.Host) ? AddressText : $"{Model.Host} · {Model.Address}";

    public bool IsOnline
    {
        get => _isOnline;
        set => Set(ref _isOnline, value);
    }

    public int UnreadCount
    {
        get => _unreadCount;
        set => Set(ref _unreadCount, value);
    }

    /// <summary>Sorting key: most recent activity first, so busy conversations float up.</summary>
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    public void Touch() => LastActivity = DateTimeOffset.Now;

    public void TouchTo(DateTimeOffset when)
    {
        if (when > LastActivity)
            LastActivity = when;
    }

    /// <summary>
    /// Folds fresh discovery data into the model this view wraps. A preloaded peer
    /// starts with a placeholder address; this is how it gains a real one when the peer
    /// is found on the network. Blank or unset fields never overwrite good values.
    /// </summary>
    public void ApplyModel(Peer source)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(source.Name) && Model.Name != source.Name)
        {
            Model.Name = source.Name;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(source.Host) && Model.Host != source.Host)
        {
            Model.Host = source.Host;
            changed = true;
        }

        if (!Equals(source.Address, IPAddress.None) && !Model.Address.Equals(source.Address))
        {
            Model.Address = source.Address;
            changed = true;
        }

        if (source.Port > 0 && Model.Port != source.Port)
        {
            Model.Port = source.Port;
            changed = true;
        }

        if (changed)
            RefreshFromModel();
    }

    /// <summary>Re-reads the underlying model after a discovery update changed name or address.</summary>
    public void RefreshFromModel() =>
        RaiseAll(nameof(Name), nameof(Host), nameof(AddressText), nameof(Initials), nameof(Subtitle));
}
