using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShareBridge.Wpf.Infrastructure;

/// <summary>
/// Fills a read-only <see cref="RichTextBox"/> with a message's text, wrapping every
/// case-insensitive occurrence of a search term in a highlighted run. A RichTextBox
/// (unlike a TextBlock) supports both text selection/copy and inline formatting, so the
/// bubble stays selectable while search matches still stand out.
/// </summary>
public static class MessageDocument
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MessageDocument), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(MessageDocument), new PropertyMetadata(string.Empty, OnChanged));

    public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

    public static void SetQuery(DependencyObject o, string v) => o.SetValue(QueryProperty, v);
    public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty);

    private static readonly Brush HighlightBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF)));

    /// <summary>Widest a bubble's text may get before it wraps (Border max 560 minus padding).</summary>
    private const double MaxTextWidth = 536;

    /// <summary>
    /// A RichTextBox's text area is inset ~12px from its Width by internal chrome, so the
    /// box needs that much more than the raw text or it word-breaks. Measured empirically.
    /// </summary>
    private const double Slack = 14;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
            return;

        var text = GetText(box) ?? string.Empty;
        box.Document = Build(text, GetQuery(box));

        // Size the box to the text so the bubble hugs its content. Measuring with
        // FormattedText is deterministic — it does not depend on a layout pass having
        // run, which is what made the old ActualWidth-binding approach wrap on reload.
        box.Width = MeasureWidth(box, text);
    }

    private static double MeasureWidth(RichTextBox box, string text)
    {
        if (string.IsNullOrEmpty(text))
            return double.NaN;

        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            box.FontSize,
            Brushes.Black,
            pixelsPerDip: 1.0)
        {
            // Wrap long text at the bubble's max so the widest line is bounded.
            MaxTextWidth = MaxTextWidth,
        };

        return Math.Min(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + Slack, MaxTextWidth);
    }

    /// <summary>Builds a tight, single-paragraph document with the matched term highlighted.</summary>
    public static FlowDocument Build(string? text, string? query)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        AppendRuns(paragraph, text ?? string.Empty, (query ?? string.Empty).Trim());

        return new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
        };
    }

    private static void AppendRuns(Paragraph paragraph, string text, string query)
    {
        if (query.Length == 0 || text.Length == 0)
        {
            paragraph.Inlines.Add(new Run(text));
            return;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var match = text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                paragraph.Inlines.Add(new Run(text[cursor..]));
                break;
            }

            if (match > cursor)
                paragraph.Inlines.Add(new Run(text[cursor..match]));

            paragraph.Inlines.Add(new Run(text.Substring(match, query.Length))
            {
                Background = HighlightBrush,
                FontWeight = FontWeights.SemiBold,
            });

            cursor = match + query.Length;
        }
    }

    private static Brush Freeze(Brush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }
}
