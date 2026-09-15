using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LittleFish.App;

internal static class Program
{
    private static int _pagesChecked;
    private const string Paragraph = "\u7b2c{0}\u6bb5 \u8fd9\u662f\u4e00\u6bb5\u7528\u4e8e\u6d4b\u91cf\u7684\u6587\u672c\uff0c\u5305\u542b\u4e2d\u6587\u6807\u70b9\u548c English words, 12345 and \U0001F30A. ";
    private static readonly string Text = string.Join("\r\n", Enumerable.Range(1, 12)
        .Select(i => string.Format(CultureInfo.InvariantCulture, Paragraph, i)
            + (i % 3 == 0 ? "\r\n\r\n" : "") + (i % 2 == 0 ? new string('W', 45) : "short\nline")));

    [STAThread]
    private static int Main()
    {
        _ = new Application();
        try
        {
            var timer = Stopwatch.StartNew();
            ReproduceOldClipping();
            foreach (var dpi in new[] { 1.0, 1.5 })
            foreach (var size in new[] { new Size(230, 180), new Size(440, 250) })
            foreach (var fontSize in new[] { 12.0, 20.0 })
            foreach (var percent in new[] { 100.0, 135.0, 175.0, 200.0, 250.0 })
            {
                var reader = CreateReader(size, fontSize, percent, dpi);
                CheckForwardAndBackward(reader, Text, checkMaximal: true);
            }
            CheckSettingsChanges();
            CheckUnvisitedPreviousPage();
            CheckDocumentEdges();
            CheckOverlappingHistory();
            CheckMainWindow();
            Console.WriteLine($"PASS: {_pagesChecked} rendered pages; old bug reproduced; forward/backward, typography, resize, DPI, CRLF and Unicode checks ({timer.Elapsed.TotalSeconds:0.0}s).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static TextBox CreateReader(Size size, double fontSize = 16, double percent = 135, double dpi = 1)
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ReaderWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var element = new XElement(xaml.Descendants(ns + "TextBox").Single(e => (string?)e.Attribute(x + "Name") == "ReaderText"));
        foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "Name" or "Grid.Row" || a.Name.LocalName.StartsWith("Preview", StringComparison.Ordinal)).ToList())
        {
            attribute.Remove();
        }
        var reader = (TextBox)XamlReader.Parse(element.ToString());
        reader.IsUndoEnabled = false;
        reader.Padding = new Thickness(20, 6, 20, 2);
        reader.FontSize = fontSize;
        TextBlock.SetLineHeight(reader, fontSize * percent / 100);
        TextBlock.SetLineStackingStrategy(reader, LineStackingStrategy.BlockLineHeight);
        VisualTreeHelper.SetRootDpi(reader, new DpiScale(dpi, dpi));
        Layout(reader, size);
        Require(reader.Template is not null, "Reader template must be available for real layout tests.");
        return reader;
    }

    private static void Layout(TextBox reader, Size size)
    {
        reader.Measure(size);
        reader.Arrange(new Rect(size));
        reader.ScrollToHome();
        reader.UpdateLayout();
    }

    private static bool FullyVisible(TextBox reader, string page)
    {
        var size = reader.RenderSize;
        reader.Text = page;
        Layout(reader, size);
        var bottom = size.Height - reader.Padding.Bottom - reader.BorderThickness.Bottom;
        var contentEnd = page.EndsWith("\r\n", StringComparison.Ordinal) ? page.Length - 2
            : page.EndsWith('\n') || page.EndsWith('\r') ? page.Length - 1 : page.Length;
        for (var line = 0; line < reader.LineCount; line++)
        {
            var offset = reader.GetCharacterIndexFromLineIndex(line);
            if (offset >= contentEnd)
            {
                break;
            }
            var rect = reader.GetRectFromCharacterIndex(offset, trailingEdge: true);
            if (rect.IsEmpty || rect.Bottom > bottom + 0.01)
            {
                return false;
            }
        }
        return true;
    }

    private static void CheckForwardAndBackward(TextBox reader, string text, bool checkMaximal)
    {
        var paginator = new ReaderPagination();
        paginator.Configure(reader, text);
        var pages = new List<(int Start, int End)>();
        var reconstructed = new StringBuilder();
        for (var start = 0; start < text.Length;)
        {
            paginator.Configure(reader, text);
            var end = paginator.GetPageEnd(start);
            Require(end > start && end <= text.Length, "Forward paging must advance within the document.");
            Require(paginator.NormalizeOffset(end) == end, "Page boundary split CRLF or a surrogate pair.");
            var page = text[start..end];
            Require(FullyVisible(reader, page), $"Clipped page: font={reader.FontSize}, lineHeight={TextBlock.GetLineHeight(reader)}, size={reader.RenderSize}, range={start}..{end}.");
            if (checkMaximal && end < text.Length)
            {
                var next = end + 1;
                if (paginator.NormalizeOffset(next) != next) next++;
                // A page may reserve the last line's spacing even if its glyphs fit.
                var expanded = text[start..next];
                var visible = FullyVisible(reader, expanded);
                var effectiveHeight = reader.ExtentHeight - (expanded.EndsWith('\n') || expanded.EndsWith('\r') ? TextBlock.GetLineHeight(reader) : 0);
                Require(!visible || effectiveHeight > reader.ViewportHeight + 0.01,
                    $"Underfilled page: lineHeight={TextBlock.GetLineHeight(reader)}, range={start}..{end}.");
            }
            reconstructed.Append(page);
            pages.Add((start, end));
            start = end;
            _pagesChecked++;
        }
        Require(reconstructed.ToString() == text, "Forward pages must preserve every character exactly once.");
        for (var i = pages.Count - 1; i > 0; i--)
        {
            paginator.Configure(reader, text);
            var previous = paginator.GetPreviousPageStart(pages[i].Start);
            Require(previous == pages[i - 1].Start, "Backward paging did not return to the same page.");
            Require(paginator.GetPageEnd(previous) == pages[i].Start, "Returning forward changed the boundary.");
        }
    }

    private static void CheckSettingsChanges()
    {
        var reader = CreateReader(new Size(440, 250));
        var paginator = new ReaderPagination();
        paginator.Configure(reader, Text);
        var anchor = paginator.GetPageEnd(0);
        foreach (var percent in new[] { 250.0, 100.0, 175.0, 135.0 })
        {
            TextBlock.SetLineHeight(reader, reader.FontSize * percent / 100);
            AssertRecomputed();
        }
        reader.FontSize = 24;
        TextBlock.SetLineHeight(reader, 24 * 1.35);
        AssertRecomputed();
        Layout(reader, new Size(270, 190));
        AssertRecomputed();
        reader.Padding = new Thickness(8, 4, 8, 0);
        AssertRecomputed();
        VisualTreeHelper.SetRootDpi(reader, new DpiScale(1.25, 1.25));
        AssertRecomputed();

        void AssertRecomputed()
        {
            paginator.Configure(reader, Text);
            var fresh = new ReaderPagination();
            fresh.Configure(reader, Text);
            Require(paginator.GetPageEnd(anchor) == fresh.GetPageEnd(anchor), "Layout change reused an outdated page boundary.");
            Require(paginator.GetPreviousPageStart(anchor) == fresh.GetPreviousPageStart(anchor), "Layout change reused outdated backward history.");
            Require(FullyVisible(reader, Text[anchor..paginator.GetPageEnd(anchor)]), "Typography preview clipped text.");
        }
    }

    private static void CheckUnvisitedPreviousPage()
    {
        var reader = CreateReader(new Size(300, 230), percent: 200);
        var paginator = new ReaderPagination();
        paginator.Configure(reader, Text);
        var end = paginator.NormalizeOffset(Text.Length * 3 / 4);
        while (end > 0)
        {
            var start = paginator.GetPreviousPageStart(end);
            Require(start < end, "Backward paging must advance towards the start.");
            Require(paginator.GetPageEnd(start) == end, "An unvisited previous page crossed its end boundary.");
            Require(FullyVisible(reader, Text[start..end]), "An unvisited previous page was clipped.");
            end = start;
            _pagesChecked++;
        }
        Require(paginator.GetPreviousPageStart(0) == 0, "Previous at document start must stay at zero.");
    }

    private static void CheckDocumentEdges()
    {
        var reader = CreateReader(new Size(230, 180));
        foreach (var text in new[] { "", "short", "\r\n\r\n\n\r", "\U0001F30A\r\nend", new string('x', 3000) })
        {
            CheckForwardAndBackward(reader, text, checkMaximal: false);
        }
        var paginator = new ReaderPagination();
        paginator.Configure(reader, "first\r\nsecond");
        Require(paginator.GetPreviousLineStart(7) == 0, "Previous line should consume CRLF with the line, not as two separate steps.");
        Layout(reader, new Size(36, 24));
        paginator.Configure(reader, "\U0001F30A\r\nend");
        Require(paginator.GetPageEnd(0) == 2, "Tiny windows must still advance by a complete character.");
    }

    private static void ReproduceOldClipping()
    {
        var reader = CreateReader(new Size(440, 250), percent: 100);
        var low = 1;
        var high = Text.Length;
        var best = 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var formatted = new FormattedText(Text[..mid], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(reader.FontFamily, reader.FontStyle, reader.FontWeight, reader.FontStretch),
                reader.FontSize, Brushes.Black, 1)
            {
                MaxTextWidth = reader.ActualWidth - reader.Padding.Left - reader.Padding.Right,
                LineHeight = TextBlock.GetLineHeight(reader)
            };
            if (formatted.Height <= reader.ActualHeight - reader.Padding.Top - reader.Padding.Bottom + 0.5)
            {
                best = mid;
                low = mid + 1;
            }
            else high = mid - 1;
        }
        Require(!FullyVisible(reader, Text[..best]), "Regression fixture must reproduce the old clipping bug.");
        Console.WriteLine($"Old pagination reproduced: {best} characters include clipped text at 100% line height.");
    }

    private static void CheckOverlappingHistory()
    {
        var reader = CreateReader(new Size(440, 250));
        var paginator = new ReaderPagination();
        paginator.Configure(reader, Text);
        var end = paginator.GetPageEnd(0);
        paginator.GetPreviousPageStart(end / 2);
        var previous = paginator.GetPreviousPageStart(end);
        Require(paginator.GetPageEnd(previous) == end, "Overlapping jumps left conflicting forward/backward history.");
    }

    private static void CheckMainWindow()
    {
        var document = string.Concat(Enumerable.Repeat(Text, 4));
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        MainWindow? window = null;
        try
        {
            File.Delete(settingsPath);
            window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(700, 400));
            root.Arrange(new Rect(0, 0, 700, 400));
            root.UpdateLayout();
            var reader = (TextBox)window.FindName("ReaderText");
            SetField("_documentText", document);
            var settings = GetField<AppSettings>("_settings");
            settings.PageTurnByPage = true;
            Invoke("RenderCurrentPage", false);

            foreach (var percent in new[] { 100.0, 250.0, 135.0 })
            {
                var preview = settings.Clone();
                preview.LineHeightPercent = percent;
                Invoke("ApplyReaderTypographyPreview", preview);
                var start = GetField<int>("_readerOffset");
                var end = GetField<int>("_visibleEndOffset");
                Require(reader.Text == document[start..end] && FullyVisible(reader, reader.Text), "MainWindow preview clipped or lost text.");
                Require(end < document.Length, "Integration fixture must contain another page.");
                Invoke("TryRunShortcut", settings.Shortcuts[CommandIds.TurnNext]);
                Require(GetField<int>("_readerOffset") == end, $"MainWindow next-page shortcut skipped text: expected {end}, actual {GetField<int>("_readerOffset")}, size={reader.RenderSize}.");
                Invoke("TryRunShortcut", settings.Shortcuts[CommandIds.TurnPrevious]);
                Require(GetField<int>("_readerOffset") == start && GetField<int>("_visibleEndOffset") == end,
                    "MainWindow backward shortcut changed the page after a line-height preview.");
                Invoke("AutoPageTimer_Tick", null, EventArgs.Empty);
                Require(GetField<int>("_readerOffset") == start, "Auto paging must pause while the window is hidden.");
                Invoke("TurnFullPage", true);
                Require(GetField<int>("_readerOffset") == end, "Full-page navigation did not use the current line height.");

                if (percent is 100 or 250)
                {
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(700, 400, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"reader-{percent:0}.png"));
                    encoder.Save(output);
                }
            }
            Invoke("SetReaderOffset", document.Length, true);
            var lastStart = GetField<int>("_readerOffset");
            Require(GetField<int>("_visibleEndOffset") == document.Length, "End navigation did not reach the last character.");
            Invoke("AutoPageTimer_Tick", null, EventArgs.Empty);
            Require(GetField<int>("_readerOffset") == lastStart, "Paging past EOF jumped backwards or displayed a blank page.");
            Console.WriteLine("PASS: MainWindow settings preview, shortcut navigation, hidden-window auto-page pause, and EOF integration.");
        }
        finally
        {
            window?.Close();
            if (savedSettings is null) File.Delete(settingsPath);
            else File.WriteAllBytes(settingsPath, savedSettings);
        }

        object? Invoke(string name, params object?[] arguments) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
        T GetField<T>(string name) => (T)typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object value) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
