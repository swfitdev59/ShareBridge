using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShareBridge.Core.Persistence;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.ViewModels;

/// <summary>
/// A picture staged in the composer, waiting for the user to hit Send. Pasted screenshots
/// are held as encoded bytes and picked files as a path — neither touches the media store
/// until the message actually goes out, so removing an attachment leaves nothing behind.
/// </summary>
public sealed class PendingImageViewModel : ObservableObject
{
    private readonly string? _sourcePath;
    private readonly byte[]? _bytes;

    private PendingImageViewModel(
        string name, long sizeBytes, int width, int height, BitmapSource thumbnail, string? sourcePath, byte[]? bytes)
    {
        Name = name;
        SizeBytes = sizeBytes;
        Width = width;
        Height = height;
        Thumbnail = thumbnail;
        _sourcePath = sourcePath;
        _bytes = bytes;

        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke());
    }

    /// <summary>Raised when the user clicks the attachment's ✕.</summary>
    public event Action? RemoveRequested;

    public string Name { get; }

    public long SizeBytes { get; }

    public int Width { get; }

    public int Height { get; }

    public BitmapSource Thumbnail { get; }

    public ICommand RemoveCommand { get; }

    public string DetailText => Width > 0 && Height > 0
        ? $"{Width} × {Height} · {Format.Bytes(SizeBytes)}"
        : Format.Bytes(SizeBytes);

    public string ToolTipText => $"{Name}\n{DetailText}";

    /// <summary>Stages a picture the user picked or dropped. Null if it will not decode.</summary>
    public static PendingImageViewModel? FromFile(string path)
    {
        var thumbnail = ImageTools.Load(path, ImageTools.PreviewWidth);
        if (thumbnail is null)
            return null;

        var (width, height) = ImageTools.Measure(path);
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new PendingImageViewModel(Path.GetFileName(path), size, width, height, thumbnail, path, null);
    }

    /// <summary>
    /// Stages a bitmap off the clipboard. It is encoded to PNG once, here, so the bytes that
    /// get sent are exactly the ones behind the preview the user is looking at.
    /// </summary>
    public static PendingImageViewModel? FromBitmap(BitmapSource image)
    {
        byte[] bytes;
        try
        {
            bytes = ImageTools.EncodePng(image);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }

        var name = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        return new PendingImageViewModel(name, bytes.Length, image.PixelWidth, image.PixelHeight, image, null, bytes);
    }

    /// <summary>
    /// Puts this attachment into the media store and returns the stored path. Called once,
    /// on send; throws only for real disk trouble, which the caller reports on the message.
    /// </summary>
    public string Store(MediaStore media, string peerLabel) =>
        _bytes is not null
            ? media.Write(peerLabel, Name, _bytes)
            : media.Import(_sourcePath!, peerLabel);
}
