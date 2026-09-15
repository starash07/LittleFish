using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Control = System.Windows.Controls.Control;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace LittleFish.App;

internal sealed class ReaderPagination
{
    private static readonly DependencyProperty[] LayoutProperties =
    [
        Control.TemplateProperty, Control.FontFamilyProperty, Control.FontSizeProperty,
        Control.FontStyleProperty, Control.FontWeightProperty, Control.FontStretchProperty,
        Control.PaddingProperty, Control.BorderThicknessProperty,
        Control.HorizontalContentAlignmentProperty, Control.VerticalContentAlignmentProperty,
        FrameworkElement.LanguageProperty, FrameworkElement.FlowDirectionProperty,
        FrameworkElement.UseLayoutRoundingProperty, UIElement.SnapsToDevicePixelsProperty,
        TextBox.TextWrappingProperty, TextBox.TextAlignmentProperty,
        TextBlock.LineHeightProperty, TextBlock.LineStackingStrategyProperty,
        TextOptions.TextFormattingModeProperty
    ];

    private readonly TextBox _measureBox = new()
    {
        IsReadOnly = true,
        IsUndoEnabled = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Dictionary<int, int> _pageEnds = [];
    private readonly Dictionary<int, int> _previousPages = [];
    private string _text = "";
    private Size _size;

    public void Configure(TextBox reader, string text)
    {
        reader.UpdateLayout();
        var size = new Size(Math.Max(1, reader.ActualWidth), Math.Max(1, reader.ActualHeight));
        var changed = !ReferenceEquals(_text, text) || _size != size;
        foreach (var property in LayoutProperties)
        {
            var value = reader.GetValue(property);
            if (!Equals(_measureBox.GetValue(property), value))
            {
                _measureBox.SetValue(property, value);
                changed = true;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(reader);
        if (!VisualTreeHelper.GetDpi(_measureBox).Equals(dpi))
        {
            VisualTreeHelper.SetRootDpi(_measureBox, dpi);
            changed = true;
        }

        _text = text;
        _size = size;
        if (changed)
        {
            _pageEnds.Clear();
            _previousPages.Clear();
        }
    }

    public int GetPageEnd(int start)
    {
        start = NormalizeOffset(start);
        if (start >= _text.Length)
        {
            return _text.Length;
        }
        if (_pageEnds.TryGetValue(start, out var cachedEnd))
        {
            return cachedEnd;
        }

        var remaining = _text.Length - start;
        var probe = Math.Min(remaining, EstimateProbeLength());
        while (probe < remaining && RangeFits(start, NormalizeOffset(start + probe)))
        {
            probe = (int)Math.Min(remaining, (long)probe * 2);
        }

        var low = start + 1;
        var high = start + probe;
        var best = NextOffset(start);
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var end = NormalizeOffset(mid);
            if (RangeFits(start, end))
            {
                best = Math.Max(best, end);
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        RememberPage(start, best);
        return best;
    }

    public int GetPreviousPageStart(int end)
    {
        end = NormalizeOffset(end);
        if (_previousPages.TryGetValue(end, out var start))
        {
            return start;
        }

        start = FindPreviousStart(end, singleLine: false);
        if (start < end)
        {
            // Keep the exact boundary when returning from a page found backwards.
            RememberPage(start, end);
        }
        return start;
    }

    public int GetPreviousLineStart(int end) => FindPreviousStart(NormalizeOffset(end), singleLine: true);

    public int NormalizeOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        if (offset > 0 && offset < _text.Length
            && ((_text[offset - 1] == '\r' && _text[offset] == '\n')
                || (char.IsHighSurrogate(_text[offset - 1]) && char.IsLowSurrogate(_text[offset]))))
        {
            offset--;
        }
        return offset;
    }

    private int NextOffset(int start)
    {
        var next = Math.Min(start + 1, _text.Length);
        return NormalizeOffset(next) < next ? next + 1 : next;
    }

    private int FindPreviousStart(int end, bool singleLine)
    {
        if (end <= 0)
        {
            return 0;
        }

        var probe = Math.Min(end, EstimateProbeLength());
        while (probe < end && RangeFits(NormalizeOffset(end - probe), end, singleLine))
        {
            probe = (int)Math.Min(end, (long)probe * 2);
        }

        var low = end - probe;
        var high = end - 1;
        var best = NormalizeOffset(high);
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var start = NormalizeOffset(mid);
            if (RangeFits(start, end, singleLine))
            {
                best = start;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return best;
    }

    private bool RangeFits(int start, int end, bool singleLine = false)
    {
        if (end <= start)
        {
            return true;
        }

        // Use the reader's TextBox template and layout, including its internal text inset.
        _measureBox.Text = _text.Substring(start, end - start);
        _measureBox.Measure(_size);
        _measureBox.Arrange(new Rect(_size));
        _measureBox.ScrollToHome();
        _measureBox.UpdateLayout();

        var lines = _measureBox.LineCount;
        var lineHeight = TextBlock.GetLineHeight(_measureBox);
        var height = _measureBox.ExtentHeight;
        var lastCharacter = _measureBox.Text.Length - 1;
        if (_text[end - 1] is '\r' or '\n')
        {
            // A trailing newline adds a synthetic empty row, not more unread content.
            lines--;
            height -= lineHeight;
            lastCharacter -= _text[end - 1] == '\n' && end - start > 1 && _text[end - 2] == '\r' ? 2 : 1;
        }

        if (singleLine)
        {
            return lines == 1;
        }
        if (lines < 1 || height > _measureBox.ViewportHeight + 0.01)
        {
            return false;
        }

        // At tight line spacing, glyphs can extend below the nominal last line box.
        var lastRect = _measureBox.GetRectFromCharacterIndex(Math.Max(0, lastCharacter), trailingEdge: true);
        var bottom = _size.Height - _measureBox.Padding.Bottom - _measureBox.BorderThickness.Bottom;
        return !lastRect.IsEmpty && lastRect.Bottom <= bottom + 0.01;
    }

    private int EstimateProbeLength()
    {
        var lineHeight = Math.Max(1, TextBlock.GetLineHeight(_measureBox));
        var charWidth = Math.Max(1, _measureBox.FontSize * 0.55);
        return (int)Math.Clamp((_size.Height / lineHeight + 2) * (_size.Width / charWidth + 1) * 4 + 500, 500, 50000);
    }

    private void RememberPage(int start, int end)
    {
        if (_pageEnds.TryGetValue(start, out var oldEnd)
            && _previousPages.GetValueOrDefault(oldEnd, -1) == start)
        {
            _previousPages.Remove(oldEnd);
        }
        if (_previousPages.TryGetValue(end, out var oldStart)
            && _pageEnds.GetValueOrDefault(oldStart, -1) == end)
        {
            _pageEnds.Remove(oldStart);
        }
        _pageEnds[start] = end;
        _previousPages[end] = start;
    }
}
