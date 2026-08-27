using System.Windows;
using System.Windows.Input;
using ShareBridge.Wpf.Infrastructure;

namespace ShareBridge.Wpf.Views;

/// <summary>
/// Shows one picture from a conversation at full size, with the actions that only make
/// sense once you are looking at it: copy, save a copy, reveal the file.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly string _path;
    private readonly string _fileName;

    public ImageViewerWindow(string path, string fileName, string detail)
    {
        InitializeComponent();

        _path = path;
        _fileName = fileName;

        Title = fileName;
        NameText.Text = fileName;
        DetailText.Text = detail;

        // Full resolution here — the bubble already showed the scaled-down copy.
        var image = ImageTools.Load(path, maxWidth: 0);
        Picture.Source = image;

        if (image is not null)
            SizeToPicture(image.PixelWidth, image.PixelHeight);
    }

    /// <summary>
    /// Opens roughly around the picture, never larger than the screen it lands on. The
    /// minimum width is what keeps the file name legible beside the buttons; the title bar
    /// carries the full name either way.
    /// </summary>
    private void SizeToPicture(int pixelWidth, int pixelHeight)
    {
        const double chrome = 28;   // the Image's margins
        const double bottomBar = 110;

        // Max first, then min: on a very small screen the minimum still wins.
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(pixelWidth + chrome, area.Width * 0.9));
        Height = Math.Max(MinHeight, Math.Min(pixelHeight + chrome + bottomBar, area.Height * 0.9));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Esc closes the viewer, the way it would a dialog.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => ImageTools.CopyToClipboard(_path);

    private void OnShowInFolder(object sender, RoutedEventArgs e) => ImageTools.ShowInFolder(_path);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var outcome = PictureActions.SaveCopy(this, _path, _fileName);
        if (outcome is not null && outcome.StartsWith("Could not", StringComparison.Ordinal))
            MessageBox.Show(this, outcome, "ShareBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
