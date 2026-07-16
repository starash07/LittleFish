using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfCursor = System.Windows.Input.Cursor;
using WpfCursors = System.Windows.Input.Cursors;

namespace LittleFish.App;

public partial class MainWindow : Window
{
    private const double MinBackgroundOpacity = 0.01;
    private const double MinLineHeightPercent = 100;
    private const double MaxLineHeightPercent = 250;
    private const int BossHotkeyId = 0x4610;
    private const int ForceShowHotkeyId = 0x4611;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private static readonly Regex ChapterRegex = new(
        @"^\s*(第[一二三四五六七八九十百千万零〇\d]+[章节卷回部集篇].{0,60}|Chapter\s+\d+.{0,60}|\d{1,4}[\.、]\s*\S.{0,60})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly string _settingsPath;
    private readonly List<ChapterEntry> _chapters = [];
    private readonly List<BookmarkEntry> _bookmarks = [];
    private readonly DispatcherTimer _autoPageTimer = new();
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private readonly bool _isSeaSHosted;
    private readonly string? _seaSHostPipeName;
    private CancellationTokenSource? _seaSHostCommandCancellation;
    private HwndSource? _hwndSource;
    private SettingsWindow? _settingsWindow;
    private string _filePath = "";
    private string _documentText = "";
    private int _readerOffset;
    private int _visibleEndOffset;
    private bool _isRenderingPage;
    private int _lastFindOffset;
    private WpfPoint? _resizeStart;
    private WpfSize _resizeStartSize;
    private WpfPoint _resizeStartScreen;
    private WpfPoint _resizeStartWindow;
    private Rect _resizeTargetRect;
    private ResizeDirection _resizeDirection = ResizeDirection.None;
    private int? _resizeReadingAnchor;
    private bool _isMoving;
    private bool _isUpdatingProgressControls;
    private bool _isHiddenByBossKey;
    private bool _settingsWindowWasVisibleBeforeBossKey;
    private bool _isRestoringReadingPosition;
    private WpfPoint _moveStartScreen;
    private WpfPoint _moveStartWindow;
    private const double ResizeBorderThickness = 8;
    private const double VisibleMinWidth = 140;
    private const double VisibleMinHeight = 90;
    private const double HiddenMinWidth = 36;
    private const double HiddenMinHeight = 24;
    private double _lastToolbarHeight;

    public MainWindow()
    {
        _isSeaSHosted = HasCommandLineFlag("--seas-hosted");
        _seaSHostPipeName = GetCommandLineOption("--seas-host-pipe");
        _settingsPath = ResolveSettingsPath(_isSeaSHosted);
        MigrateInstalledSettings(_settingsPath);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
        ReaderText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ReaderText_ScrollChanged));
        _autoPageTimer.Tick += AutoPageTimer_Tick;
        LoadSettings();
        ApplySettings();
        UpdateStatus();

        var startupFilePath = GetStartupFilePath();
        if (startupFilePath is not null)
        {
            Dispatcher.BeginInvoke(
                () => OpenStartupFile(startupFilePath),
                DispatcherPriority.Loaded);
        }

        StartSeaSHostCommandServer();
    }

    private static bool HasCommandLineFlag(string name)
    {
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetCommandLineOption(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static string? GetStartupFilePath()
    {
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            try
            {
                var path = Path.GetFullPath(argument.Trim('"'));
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return null;
    }

    private void OpenStartupFile(string path)
    {
        try
        {
            LoadTextFile(path);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"打开文件失败：{exception.Message}",
                "LittleFish",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            EnsureDefaultShortcuts();
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            EnsureDefaultShortcuts();
            _bookmarks.AddRange(_settings.Bookmarks);
        }
        catch
        {
            _settings = new AppSettings();
            EnsureDefaultShortcuts();
        }
    }

    private void EnsureDefaultShortcuts()
    {
        _settings.LastOffsets ??= [];
        _settings.LastTopOffsets ??= [];
        _settings.LastScrollOffsets ??= [];
        _settings.Bookmarks ??= [];
        _settings.Shortcuts ??= AppSettings.DefaultShortcuts();

        foreach (var (commandId, shortcut) in AppSettings.DefaultShortcuts())
        {
            if (!_settings.Shortcuts.ContainsKey(commandId))
            {
                _settings.Shortcuts[commandId] = shortcut;
            }
        }

        foreach (var commandId in _settings.Shortcuts.Keys.ToList())
        {
            _settings.Shortcuts[commandId] = KeyGestureText.NormalizeDisplayText(_settings.Shortcuts[commandId]);
        }
    }

    private void SaveSettings()
    {
        _settings.Bookmarks = _bookmarks;
        _settings.ToolbarVisible = Toolbar.Visibility == Visibility.Visible;
        SaveCurrentReadingPosition();
        WriteSettings();
    }

    private void WriteSettings()
    {
        var settingsDirectory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }

        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    private static string ResolveSettingsPath(bool isSeaSHosted)
    {
        var portableSettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var installManifestPath = Path.Combine(AppContext.BaseDirectory, ".littlefish-manifest.txt");
        if (isSeaSHosted || !File.Exists(installManifestPath))
        {
            return portableSettingsPath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleFish",
            "settings.json");
    }

    private static void MigrateInstalledSettings(string settingsPath)
    {
        var legacySettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (string.Equals(settingsPath, legacySettingsPath, StringComparison.OrdinalIgnoreCase)
            || File.Exists(settingsPath)
            || !File.Exists(legacySettingsPath))
        {
            return;
        }

        try
        {
            var settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            File.Copy(legacySettingsPath, settingsPath, overwrite: false);
            File.Delete(legacySettingsPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveCurrentReadingPosition()
    {
        if (string.IsNullOrWhiteSpace(_filePath) || string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        _settings.LastFile = _filePath;
        var offset = Math.Clamp(_readerOffset, 0, _documentText.Length);
        _settings.LastTopOffsets[_filePath] = offset;
        _settings.LastOffsets[_filePath] = offset;
    }

    private void ApplySettings()
    {
        _settings.BackgroundOpacity = Math.Clamp(_settings.BackgroundOpacity, MinBackgroundOpacity, 1);
        _settings.LineHeightPercent = Math.Clamp(_settings.LineHeightPercent, MinLineHeightPercent, MaxLineHeightPercent);
        Topmost = _settings.Topmost;
        Opacity = _settings.WindowOpacity;
        ReaderText.Foreground = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.Black));
        ReaderText.FontFamily = new MediaFontFamily(_settings.FontFamily);
        ReaderText.FontSize = _settings.FontSize;
        ApplyReaderLineHeight();
        SetToolbarVisible(_settings.ToolbarVisible, preserveReaderSize: IsLoaded);
        ConfigureAutoPageTimer();
        ApplyBackgroundBrush();
        RefreshBookmarks();
        EnsureTrayIcon();
        ApplyTaskbarVisibility();
        RegisterGlobalHotkeys();
        if (!string.IsNullOrEmpty(_documentText))
        {
            Dispatcher.BeginInvoke(() => RenderCurrentPage(savePosition: true), DispatcherPriority.ContextIdle);
        }
    }

    private void ApplyBackgroundBrush()
    {
        var color = ParseColor(_settings.BackgroundColor, Colors.White);
        color.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, MinBackgroundOpacity, 1) * 255);
        ReaderBackground.Background = new SolidColorBrush(color);
    }

    private void ApplyToolbarChrome()
    {
        var toolbarVisible = Toolbar.Visibility == Visibility.Visible;
        WindowFrame.Margin = new Thickness(0);
        WindowFrame.BorderThickness = toolbarVisible ? new Thickness(1) : new Thickness(0);
        ReaderBackground.CornerRadius = toolbarVisible ? new CornerRadius(12) : new CornerRadius(0);
        ReaderText.Padding = toolbarVisible ? new Thickness(20, 6, 20, 2) : new Thickness(8, 4, 8, 0);
    }

    private void SetToolbarVisible(bool visible, bool preserveReaderSize)
    {
        var oldReaderWidth = ReaderText.ActualWidth;
        var oldReaderHeight = ReaderText.ActualHeight;
        var canPreserve = preserveReaderSize && oldReaderWidth > 0 && oldReaderHeight > 0 && WindowState == WindowState.Normal;

        Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyToolbarChrome();
        ApplyWindowMinimums();

        if (!canPreserve)
        {
            if (!string.IsNullOrEmpty(_documentText))
            {
                Dispatcher.BeginInvoke(() => RenderCurrentPage(savePosition: true), DispatcherPriority.ContextIdle);
            }
            return;
        }

        UpdateLayout();
        var widthDelta = ReaderText.ActualWidth - oldReaderWidth;
        var heightDelta = ReaderText.ActualHeight - oldReaderHeight;
        Width = Math.Max(MinWidth, Width - widthDelta);
        Height = Math.Max(MinHeight, Height - heightDelta);
        if (!string.IsNullOrEmpty(_documentText))
        {
            Dispatcher.BeginInvoke(() => RenderCurrentPage(savePosition: true), DispatcherPriority.ContextIdle);
        }
    }

    private static MediaColor ParseColor(string value, MediaColor fallback)
    {
        try
        {
            return (MediaColor)MediaColorConverter.ConvertFromString(value);
        }
        catch
        {
            return fallback;
        }
    }

    private double GetReaderLineHeight()
    {
        var percent = Math.Clamp(_settings.LineHeightPercent, MinLineHeightPercent, MaxLineHeightPercent);
        return Math.Max(1, ReaderText.FontSize * percent / 100);
    }

    private void ApplyReaderLineHeight()
    {
        TextBlock.SetLineHeight(ReaderText, GetReaderLineHeight());
        TextBlock.SetLineStackingStrategy(ReaderText, LineStackingStrategy.BlockLineHeight);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenFile();

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var hasLastFile = !string.IsNullOrWhiteSpace(_settings.LastFile);
        LastFileButton.IsEnabled = hasLastFile;
        LastFileButton.Content = hasLastFile
            ? $"打开上次文件：{Path.GetFileName(_settings.LastFile)}"
            : "打开上次文件";
        MenuPopup.IsOpen = true;
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenFile();
    }

    private void MenuLastFile_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenLastFile();
    }

    private void MenuFind_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        Find_Click(sender, e);
    }

    private void MenuProgress_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ProgressPopup.IsOpen = true;
        UpdateProgressControls(GetReadingProgressPercent());
        ProgressBox.Focus();
        ProgressBox.SelectAll();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenSettingsWindow();
    }

    private void OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 TXT",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadTextFile(dialog.FileName);
        }
    }

    private void OpenLastFile()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastFile))
        {
            System.Windows.MessageBox.Show(this, "还没有上次打开的文件。", "打开上次文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(_settings.LastFile))
        {
            System.Windows.MessageBox.Show(this, "上次打开的文件不存在或已移动。", "打开上次文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadTextFile(_settings.LastFile);
    }

    private void LoadTextFile(string path)
    {
        var content = ReadTextFile(path);
        _filePath = path;
        _documentText = content;
        _isRestoringReadingPosition = true;
        var topOffset = _settings.LastTopOffsets.TryGetValue(path, out var savedTopOffset)
            ? Math.Clamp(savedTopOffset, 0, content.Length)
            : (int?)null;
        var charOffset = topOffset
            ?? (_settings.LastOffsets.TryGetValue(path, out var offset)
            ? Math.Clamp(offset, 0, content.Length)
            : 0);

        _readerOffset = charOffset;
        _visibleEndOffset = charOffset;
        _lastFindOffset = charOffset;
        RenderCurrentPage(savePosition: false);
        ParseChapters(content);
        RefreshBookmarks();
        UpdateStatus();
        _settings.LastFile = path;
        WriteSettings();
        Dispatcher.BeginInvoke(
            () => RestoreReadingPosition(path, charOffset),
            DispatcherPriority.Loaded);
    }

    private void RestoreReadingPosition(string path, int charOffset)
    {
        try
        {
            if (!string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _readerOffset = Math.Clamp(charOffset, 0, _documentText.Length);
            RenderCurrentPage(savePosition: true);

            UpdateStatus();
            SaveCurrentReadingPosition();
            WriteSettings();
        }
        finally
        {
            _isRestoringReadingPosition = false;
        }
    }

    private static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        foreach (var encoding in new[]
        {
            new UTF8Encoding(true, true),
            new UTF8Encoding(false, true),
            Encoding.GetEncoding("GB18030"),
            Encoding.GetEncoding("Big5"),
            Encoding.Default
        })
        {
            try
            {
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private void RenderCurrentPage(bool savePosition)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            _readerOffset = 0;
            _visibleEndOffset = 0;
            SetReaderText("");
            UpdateStatus();
            return;
        }

        _readerOffset = Math.Clamp(_readerOffset, 0, _documentText.Length);
        if (_readerOffset >= _documentText.Length)
        {
            _readerOffset = FindPreviousPageOffset(_documentText.Length);
        }

        var pageText = BuildPageText(_readerOffset, out var endOffset);
        _visibleEndOffset = Math.Clamp(endOffset, _readerOffset, _documentText.Length);
        SetReaderText(pageText);
        UpdateStatus();

        if (savePosition)
        {
            SaveCurrentReadingPosition();
            WriteSettings();
        }
    }

    private void SetReaderText(string text)
    {
        _isRenderingPage = true;
        try
        {
            ReaderText.Text = text;
            ReaderText.CaretIndex = 0;
            ReaderText.ScrollToHome();
        }
        finally
        {
            _isRenderingPage = false;
        }
    }

    private string BuildPageText(int startOffset, out int endOffset)
    {
        if (string.IsNullOrEmpty(_documentText) || startOffset >= _documentText.Length)
        {
            endOffset = Math.Clamp(startOffset, 0, _documentText.Length);
            return "";
        }

        var length = FindPageLength(startOffset);
        endOffset = Math.Clamp(startOffset + length, startOffset, _documentText.Length);
        return _documentText.Substring(startOffset, endOffset - startOffset);
    }

    private int FindPageLength(int startOffset)
    {
        var remaining = _documentText.Length - startOffset;
        if (remaining <= 0)
        {
            return 0;
        }

        var probe = Math.Min(remaining, EstimatePageProbeLength());
        while (probe < remaining && TextRangeFits(startOffset, probe))
        {
            var nextProbe = Math.Min(remaining, probe * 2);
            if (nextProbe == probe)
            {
                break;
            }
            probe = nextProbe;
        }

        var low = 1;
        var high = probe;
        var best = 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (TextRangeFits(startOffset, mid))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Math.Clamp(best, 1, remaining);
    }

    private int EstimatePageProbeLength()
    {
        var size = GetReaderContentSize();
        var lineHeight = GetReaderLineHeight();
        var charWidth = Math.Max(1, ReaderText.FontSize * 0.55);
        var lines = Math.Max(1, (int)Math.Ceiling(size.Height / lineHeight));
        var charsPerLine = Math.Max(1, (int)Math.Ceiling(size.Width / charWidth));
        return Math.Clamp((lines + 2) * charsPerLine * 4 + 500, 500, 50000);
    }

    private bool TextRangeFits(int startOffset, int length)
    {
        if (length <= 0)
        {
            return true;
        }

        var size = GetReaderContentSize();
        if (size.Width <= 1 || size.Height <= 1)
        {
            return length <= 1;
        }

        var text = _documentText.Substring(startOffset, Math.Min(length, _documentText.Length - startOffset));
        var formatted = CreateFormattedText(text, size.Width);
        return formatted.Height <= size.Height + 0.5;
    }

    private bool TextRangeFitsSingleLine(int startOffset, int length)
    {
        if (length <= 0)
        {
            return true;
        }

        var count = Math.Min(length, _documentText.Length - startOffset);
        var text = _documentText.Substring(startOffset, count);
        if (text.Contains('\n') || text.Contains('\r'))
        {
            return false;
        }

        var size = GetReaderContentSize();
        if (size.Width <= 1)
        {
            return count <= 1;
        }

        var formatted = CreateFormattedText(text, double.PositiveInfinity);
        return formatted.WidthIncludingTrailingWhitespace <= size.Width + 0.5;
    }

    private FormattedText CreateFormattedText(string text, double maxTextWidth)
    {
        var typeface = new Typeface(
            ReaderText.FontFamily,
            ReaderText.FontStyle,
            ReaderText.FontWeight,
            ReaderText.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            ReaderText.FontSize,
            System.Windows.Media.Brushes.Black,
            dpi)
        {
            MaxTextWidth = double.IsInfinity(maxTextWidth) ? 100000 : Math.Max(1, maxTextWidth),
            LineHeight = GetReaderLineHeight(),
            TextAlignment = TextAlignment.Left
        };
        return formatted;
    }

    private WpfSize GetReaderContentSize()
    {
        var width = ReaderText.ActualWidth > 0 ? ReaderText.ActualWidth : ReaderText.RenderSize.Width;
        var height = ReaderText.ActualHeight > 0 ? ReaderText.ActualHeight : ReaderText.RenderSize.Height;
        width = Math.Max(1, width - ReaderText.Padding.Left - ReaderText.Padding.Right);
        height = Math.Max(1, height - ReaderText.Padding.Top - ReaderText.Padding.Bottom);
        return new WpfSize(width, height);
    }

    private void SetReaderOffset(int offset, bool savePosition = true)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        _readerOffset = Math.Clamp(offset, 0, _documentText.Length);
        RenderCurrentPage(savePosition);
    }

    private int GetNextLineOffset()
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return 0;
        }

        ReaderText.UpdateLayout();
        if (ReaderText.LineCount > 1)
        {
            var localOffset = ReaderText.GetCharacterIndexFromLineIndex(1);
            if (localOffset > 0)
            {
                return Math.Clamp(_readerOffset + localOffset, 0, _documentText.Length);
            }
        }

        return Math.Min(_documentText.Length, _readerOffset + 1);
    }

    private int FindPreviousLineOffset(int endOffset)
    {
        endOffset = Math.Clamp(endOffset, 0, _documentText.Length);
        if (endOffset <= 0)
        {
            return 0;
        }

        var minOffset = Math.Max(0, endOffset - 20000);
        var low = minOffset;
        var high = endOffset - 1;
        var best = high;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (TextRangeFitsSingleLine(mid, endOffset - mid))
            {
                best = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return Math.Clamp(best, 0, endOffset - 1);
    }

    private int GetVisibleLineCount()
    {
        ReaderText.UpdateLayout();
        if (ReaderText.LineCount > 0)
        {
            return Math.Max(1, ReaderText.LineCount);
        }

        var size = GetReaderContentSize();
        return Math.Max(1, (int)Math.Floor(size.Height / GetReaderLineHeight()));
    }

    private int FindPreviousPageOffset(int endOffset)
    {
        var target = Math.Clamp(endOffset, 0, _documentText.Length);
        var lines = GetVisibleLineCount();
        for (var i = 0; i < lines && target > 0; i++)
        {
            target = FindPreviousLineOffset(target);
        }

        return target;
    }

    private string GetDocumentLineTitle(int offset)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return "";
        }

        offset = Math.Clamp(offset, 0, Math.Max(0, _documentText.Length - 1));
        var lineStart = _documentText.LastIndexOf('\n', offset);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = _documentText.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = _documentText.Length;
        }

        return _documentText[lineStart..lineEnd].Trim();
    }

    private void Window_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = TryGetDroppedFilePath(e, out _) ? WpfDragDropEffects.Copy : WpfDragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetDroppedFilePath(e, out var path))
        {
            return;
        }

        try
        {
            LoadTextFile(path);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"打开拖入文件失败：{exception.Message}",
                "LittleFish",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool TryGetDroppedFilePath(WpfDragEventArgs e, out string path)
    {
        path = "";
        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop) ||
            e.Data.GetData(WpfDataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        path = files.FirstOrDefault(file =>
                File.Exists(file) &&
                string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(File.Exists)
            ?? "";

        return !string.IsNullOrWhiteSpace(path);
    }

    private void ParseChapters(string content)
    {
        _chapters.Clear();
        foreach (Match match in ChapterRegex.Matches(content))
        {
            var title = match.Value.Trim();
            var entry = new ChapterEntry(title.Length > 80 ? title[..80] : title, match.Index);
            _chapters.Add(entry);
        }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e) => AddBookmark();

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e) => DeleteSelectedBookmark();

    private void AddBookmark()
    {
        if (string.IsNullOrWhiteSpace(_filePath) || string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        var offset = Math.Clamp(_readerOffset, 0, _documentText.Length);
        var title = GetDocumentLineTitle(offset);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "当前位置";
        }

        _bookmarks.Add(new BookmarkEntry(_filePath, offset, $"{offset}: {title}"));
        RefreshBookmarks();
        SaveSettings();
    }

    private void RefreshBookmarks()
    {
        BookmarkList.Items.Clear();
        foreach (var bookmark in _bookmarks.Where(b => b.FilePath == _filePath))
        {
            BookmarkList.Items.Add(bookmark.Title);
        }
    }

    private void DeleteSelectedBookmark()
    {
        var current = _bookmarks.Where(b => b.FilePath == _filePath).ToList();
        var selectedIndex = BookmarkList.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= current.Count)
        {
            return;
        }

        _bookmarks.Remove(current[selectedIndex]);
        RefreshBookmarks();
        if (BookmarkList.Items.Count > 0)
        {
            BookmarkList.SelectedIndex = Math.Min(selectedIndex, BookmarkList.Items.Count - 1);
        }
        SaveSettings();
    }

    private void BookmarkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var current = _bookmarks.Where(b => b.FilePath == _filePath).ToList();
        if (BookmarkList.SelectedIndex >= 0 && BookmarkList.SelectedIndex < current.Count)
        {
            JumpToOffset(current[BookmarkList.SelectedIndex].Offset);
            BookmarkPopup.IsOpen = false;
        }
    }

    private void JumpToOffset(int offset)
    {
        ReaderText.Focus();
        SetReaderOffset(offset);
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        SearchPopup.IsOpen = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void SearchBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        var term = SearchBox.Text;
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        var start = Math.Min(Math.Max(_lastFindOffset, _readerOffset) + 1, _documentText.Length);
        var index = _documentText.IndexOf(term, start, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            index = _documentText.IndexOf(term, 0, StringComparison.CurrentCultureIgnoreCase);
        }

        if (index >= 0)
        {
            _lastFindOffset = index;
            ReaderText.Focus();
            SetReaderOffset(index);
            ReaderText.Select(0, Math.Min(term.Length, ReaderText.Text.Length));
            SearchPopup.IsOpen = false;
        }
    }

    private void ProgressJump_Click(object sender, RoutedEventArgs e) => JumpToProgress();

    private void ProgressBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpToProgress();
            e.Handled = true;
        }
    }

    private void ProgressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingProgressControls || ProgressSlider is null)
        {
            return;
        }

        if (TryGetProgressPercent(out var percent))
        {
            _isUpdatingProgressControls = true;
            ProgressSlider.Value = Math.Clamp(percent, 0, 100);
            _isUpdatingProgressControls = false;
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingProgressControls || ProgressBox is null)
        {
            return;
        }

        _isUpdatingProgressControls = true;
        ProgressBox.Text = e.NewValue.ToString("0.##");
        ProgressBox.CaretIndex = ProgressBox.Text.Length;
        _isUpdatingProgressControls = false;
    }

    private void JumpToProgress()
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        if (!TryGetProgressPercent(out var percent))
        {
            System.Windows.MessageBox.Show(this, "请输入 0 到 100 之间的数字。", "进度跳转", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        percent = Math.Clamp(percent, 0, 100);
        UpdateProgressControls(percent);
        var targetOffset = (int)Math.Round(_documentText.Length * percent / 100);
        SetReaderOffset(targetOffset);

        ProgressPopup.IsOpen = false;
        UpdateStatus();
    }

    private bool TryGetProgressPercent(out double percent)
    {
        var text = ProgressBox.Text.Trim().TrimEnd('%');
        return double.TryParse(text, out percent);
    }

    private void UpdateProgressControls(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _isUpdatingProgressControls = true;
        ProgressSlider.Value = percent;
        ProgressBox.Text = percent.ToString("0.##");
        _isUpdatingProgressControls = false;
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        OpenChapterWindow();
    }

    private void Chapters_Click(object sender, RoutedEventArgs e)
    {
        BookmarkPopup.IsOpen = false;
        OpenChapterWindow();
    }

    private void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        BookmarkPopup.IsOpen = !BookmarkPopup.IsOpen;
    }

    private void OpenChapterWindow()
    {
        CloseTransientPopups(includeHelp: true);

        var window = new ChapterPickerWindow(_chapters, _readerOffset)
        {
            Owner = this
        };

        if (window.ShowDialog() == true && window.SelectedOffset is int offset)
        {
            JumpToOffset(offset);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        BookmarkPopup.IsOpen = false;
        SearchPopup.IsOpen = false;
        ProgressPopup.IsOpen = false;
        HelpPopup.IsOpen = !HelpPopup.IsOpen;
    }

    private void CloseTransientPopups(bool includeHelp)
    {
        MenuPopup.IsOpen = false;
        SearchPopup.IsOpen = false;
        ProgressPopup.IsOpen = false;
        BookmarkPopup.IsOpen = false;
        if (includeHelp)
        {
            HelpPopup.IsOpen = false;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings);
        _settingsWindow = window;
        window.Owner = this;
        var committedSettings = _settings.Clone();
        var hasTypographyPreview = false;
        window.SettingsPreviewChanged += (_, args) =>
        {
            hasTypographyPreview = true;
            ApplyReaderTypographyPreview(args.Settings);
        };
        window.SettingsApplied += (_, args) =>
        {
            _settings = args.Settings;
            ApplySettings();
            SaveSettings();
            UpdateStatus();
            committedSettings = _settings.Clone();
            hasTypographyPreview = false;
        };
        window.Closed += (_, _) =>
        {
            if (hasTypographyPreview)
            {
                ApplyReaderTypographyPreview(committedSettings);
            }

            _settingsWindow = null;
        };
        window.ShowDialog();
    }

    private void ApplyReaderTypographyPreview(AppSettings settings)
    {
        _settings.FontSize = Math.Clamp(settings.FontSize, 6, 96);
        _settings.LineHeightPercent = Math.Clamp(settings.LineHeightPercent, MinLineHeightPercent, MaxLineHeightPercent);
        ReaderText.FontSize = _settings.FontSize;
        ApplyReaderLineHeight();

        if (!string.IsNullOrEmpty(_documentText))
        {
            RenderCurrentPage(savePosition: false);
        }
    }

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e) => ToggleTopmost();

    private void ToggleTopmost()
    {
        _settings.Topmost = !_settings.Topmost;
        Topmost = _settings.Topmost;
        SaveSettings();
        UpdateStatus();
    }

    private void ReaderText_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isRenderingPage)
        {
            return;
        }

        UpdateStatus();
        if (!_isRestoringReadingPosition)
        {
            SaveCurrentReadingPosition();
        }
    }

    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        var current = ParseColor(_settings.TextColor, Colors.Black);
        using var dialog = new Forms.ColorDialog { Color = DrawingColor.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _settings.TextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            ReaderText.Foreground = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.Black));
            SaveSettings();
        }
    }

    private void BackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        var current = ParseColor(_settings.BackgroundColor, Colors.White);
        using var dialog = new Forms.ColorDialog { Color = DrawingColor.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _settings.BackgroundColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            ApplyBackgroundBrush();
            SaveSettings();
        }
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.BackgroundOpacity = Math.Clamp(e.NewValue, MinBackgroundOpacity, 1);
        ApplyBackgroundBrush();
        SaveSettings();
        UpdateStatus();
    }

    private void WindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.WindowOpacity = Math.Clamp(e.NewValue, 0.01, 1);
        Opacity = _settings.WindowOpacity;
        SaveSettings();
        UpdateStatus();
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            AdjustBackgroundOpacity(e.Delta > 0 ? 0.04 : -0.04);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            AdjustWindowOpacity(e.Delta > 0 ? 0.04 : -0.04);
            e.Handled = true;
        }
        else
        {
            TurnPage(e.Delta < 0);
            e.Handled = true;
        }
    }

    private void BookmarkList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollListByWheel(BookmarkList, e);
    }

    private static void ScrollListByWheel(WpfListBox listBox, MouseWheelEventArgs e)
    {
        e.Handled = true;

        if (listBox.Items.Count == 0)
        {
            return;
        }

        var step = Math.Max(1, Math.Min(SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3, 6));
        var currentIndex = listBox.SelectedIndex >= 0
            ? listBox.SelectedIndex
            : GetFirstVisibleListBoxIndex(listBox);
        var nextIndex = Math.Clamp(
            currentIndex + (e.Delta < 0 ? step : -step),
            0,
            listBox.Items.Count - 1);

        listBox.SelectedIndex = nextIndex;
        listBox.ScrollIntoView(listBox.Items[nextIndex]);
    }

    private static int GetFirstVisibleListBoxIndex(WpfListBox listBox)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is null)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(scrollViewer.VerticalOffset), 0, Math.Max(0, listBox.Items.Count - 1));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null)
            {
                return nestedChild;
            }
        }

        return null;
    }

    private void TurnPage(bool forward)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        if (_settings.PageTurnByPage)
        {
            if (forward)
            {
                SetReaderOffset(_visibleEndOffset);
            }
            else
            {
                SetReaderOffset(FindPreviousPageOffset(_readerOffset));
            }
        }
        else
        {
            if (forward)
            {
                SetReaderOffset(GetNextLineOffset());
            }
            else
            {
                SetReaderOffset(FindPreviousLineOffset(_readerOffset));
            }
        }
    }

    private void AdjustBackgroundOpacity(double delta)
    {
        _settings.BackgroundOpacity = Math.Clamp(_settings.BackgroundOpacity + delta, MinBackgroundOpacity, 1);
        ApplyBackgroundBrush();
        SaveSettings();
        UpdateStatus();
    }

    private void AdjustWindowOpacity(double delta)
    {
        _settings.WindowOpacity = Math.Clamp(_settings.WindowOpacity + delta, 0.01, 1);
        Opacity = _settings.WindowOpacity;
        SaveSettings();
        UpdateStatus();
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (TryRunFixedShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (TryRunShortcut(KeyGestureText.FromKeyEvent(e)))
        {
            e.Handled = true;
            return;
        }

        if (HandleReaderNavigationKey(e))
        {
            e.Handled = true;
        }
    }

    private void ReaderText_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (TryRunFixedShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (TryRunShortcut(KeyGestureText.FromKeyEvent(e)))
        {
            e.Handled = true;
            return;
        }

        if (HandleReaderNavigationKey(e))
        {
            e.Handled = true;
        }
    }

    private bool HandleReaderNavigationKey(WpfKeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        if (Keyboard.FocusedElement is WpfTextBox focusedTextBox && !ReferenceEquals(focusedTextBox, ReaderText))
        {
            return false;
        }

        if (string.IsNullOrEmpty(_documentText))
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Home:
                SetReaderOffset(0);
                return true;
            case Key.End:
                SetReaderOffset(FindPreviousPageOffset(_documentText.Length));
                return true;
            case Key.PageUp:
                SetReaderOffset(FindPreviousPageOffset(_readerOffset));
                return true;
            case Key.PageDown:
                SetReaderOffset(_visibleEndOffset);
                return true;
            default:
                return false;
        }
    }

    private bool TryRunFixedShortcut(WpfKeyEventArgs e)
    {
        if (KeyGestureText.FromKeyEvent(e) == "Ctrl+W")
        {
            Close();
            return true;
        }

        return false;
    }

    private bool TryRunShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var command = _settings.Shortcuts.FirstOrDefault(pair => pair.Value == shortcut).Key;
        if (string.IsNullOrEmpty(command)) return false;

        switch (command)
        {
            case CommandIds.OpenFile: OpenFile(); return true;
            case CommandIds.OpenSettings: OpenSettingsWindow(); return true;
            case CommandIds.Find: Find_Click(this, new RoutedEventArgs()); return true;
            case CommandIds.FindNext: FindNext(); return true;
            case CommandIds.AddBookmark: AddBookmark(); return true;
            case CommandIds.ToggleSidebar: ToggleSidebar_Click(this, new RoutedEventArgs()); return true;
            case CommandIds.ToggleToolbar: ToggleToolbar(); return true;
            case CommandIds.ToggleTopmost: ToggleTopmost(); return true;
            case CommandIds.ToggleHoverHide: ToggleHoverHide(); return true;
            case CommandIds.ToggleAutoPage: ToggleAutoPage(); return true;
            case CommandIds.ToggleBossKey: ToggleBossKeyHide(); return true;
            case CommandIds.TurnPrevious: TurnPage(false); SaveSettings(); return true;
            case CommandIds.TurnNext: TurnPage(true); SaveSettings(); return true;
            case CommandIds.IncreaseFont: ChangeFontSize(1); return true;
            case CommandIds.DecreaseFont: ChangeFontSize(-1); return true;
            default: return false;
        }
    }

    private void ToggleToolbar()
    {
        SetToolbarVisible(Toolbar.Visibility != Visibility.Visible, preserveReaderSize: true);
        SaveSettings();
    }

    private double GetToolbarHeight()
    {
        if (Toolbar.ActualHeight > 0)
        {
            _lastToolbarHeight = Toolbar.ActualHeight;
            return _lastToolbarHeight;
        }

        return _lastToolbarHeight > 0 ? _lastToolbarHeight : 46;
    }

    private void ApplyWindowMinimums()
    {
        if (Toolbar.Visibility == Visibility.Visible)
        {
            MinWidth = VisibleMinWidth;
            MinHeight = VisibleMinHeight;
        }
        else
        {
            MinWidth = HiddenMinWidth;
            MinHeight = HiddenMinHeight;
        }
    }

    private void ToggleHoverHide()
    {
        _settings.HoverHide = !_settings.HoverHide;
        SaveSettings();
        UpdateStatus();
    }

    private void AutoPage_Click(object sender, RoutedEventArgs e) => ToggleAutoPage();

    private void ToggleAutoPage()
    {
        _settings.AutoPageEnabled = !_settings.AutoPageEnabled;
        ConfigureAutoPageTimer();
        SaveSettings();
        UpdateStatus();
    }

    private void ConfigureAutoPageTimer()
    {
        _autoPageTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.AutoPageIntervalSeconds, 1, 3600));
        if (_settings.AutoPageEnabled)
        {
            _autoPageTimer.Start();
        }
        else
        {
            _autoPageTimer.Stop();
        }
        if (AutoPageIcon is not null && AutoPageLabel is not null)
        {
            AutoPageIcon.Data = Geometry.Parse(_settings.AutoPageEnabled
                ? "M3,1 H5 V11 H3 Z M8,1 H10 V11 H8 Z"
                : "M2,1 L11,6 L2,11 Z");
            AutoPageLabel.Text = _settings.AutoPageEnabled ? "暂停" : "自动";
            AutoPageButton.ToolTip = _settings.AutoPageEnabled ? "暂停自动翻页" : "开启自动翻页";
        }
    }

    private void AutoPageTimer_Tick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return;
        }
        TurnPage(forward: true);
    }

    private void ChangeFontSize(double delta)
    {
        ReaderText.FontSize = Math.Clamp(ReaderText.FontSize + delta, 6, 96);
        _settings.FontSize = ReaderText.FontSize;
        ApplyReaderLineHeight();
        RenderCurrentPage(savePosition: true);
        SaveSettings();
    }

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var direction = GetResizeDirection(e.GetPosition(this));
        if (direction != ResizeDirection.None && e.OriginalSource is not WpfButton and not WpfTextBox and not Slider)
        {
            StartWindowResize(direction, e);
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not WpfButton and not WpfTextBox and not Slider)
        {
            StartWindowMove(e);
        }
    }

    private void ReaderText_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var direction = GetResizeDirection(e.GetPosition(this));
        if (direction != ResizeDirection.None)
        {
            StartWindowResize(direction, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || ShouldDragFromBody(e))
        {
            StartWindowMove(e);
            e.Handled = true;
        }
    }

    private bool ShouldDragFromBody(MouseButtonEventArgs e)
    {
        return Toolbar.Visibility != Visibility.Visible
            && e.ChangedButton == MouseButton.Left
            && GetResizeDirection(e.GetPosition(this)) == ResizeDirection.None;
    }

    private void StartWindowMove(MouseButtonEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        _isMoving = true;
        _moveStartScreen = PointToScreen(e.GetPosition(this));
        _moveStartWindow = new WpfPoint(Left, Top);
        CaptureMouse();
        e.Handled = true;
    }

    private void MoveWindow(WpfMouseEventArgs e)
    {
        var current = PointToScreen(e.GetPosition(this));
        var nextLeft = _moveStartWindow.X + current.X - _moveStartScreen.X;
        var nextTop = _moveStartWindow.Y + current.Y - _moveStartScreen.Y;
        var clamped = ClampWindowPosition(nextLeft, nextTop);
        Left = clamped.X;
        Top = clamped.Y;
    }

    private WpfPoint ClampWindowPosition(double left, double top)
    {
        var bounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        const double keepVisible = 64;
        var minLeft = bounds.Left - ActualWidth + keepVisible;
        var maxLeft = bounds.Right - keepVisible;
        var minTop = bounds.Top;
        var maxTop = bounds.Bottom - keepVisible;
        return new WpfPoint(Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    private void Window_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        if (_settings.HoverHide)
        {
            Opacity = _settings.WindowOpacity;
        }
    }

    private void Window_MouseLeave(object sender, WpfMouseEventArgs e)
    {
        if (_isMoving || _resizeDirection != ResizeDirection.None)
        {
            return;
        }

        if (_settings.HoverHide && !IsMouseOver)
        {
            Opacity = Math.Clamp(_settings.HiddenOpacity, 0.01, 1);
        }
    }

    private void Window_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_isMoving)
        {
            MoveWindow(e);
            return;
        }

        if (_resizeDirection != ResizeDirection.None && _resizeStart is not null)
        {
            ResizeWindow(e);
            return;
        }

        Cursor = CursorForDirection(GetResizeDirection(e.GetPosition(this)));
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        var direction = GetResizeDirection(e.GetPosition(this));
        if (direction == ResizeDirection.None)
        {
            if (Toolbar.Visibility != Visibility.Visible && e.ChangedButton == MouseButton.Left)
            {
                StartWindowMove(e);
            }
            return;
        }

        StartWindowResize(direction, e);
    }

    private void StartWindowResize(ResizeDirection direction, MouseButtonEventArgs e)
    {
        _resizeDirection = direction;
        _resizeReadingAnchor = _readerOffset;
        _resizeStart = e.GetPosition(this);
        _resizeStartScreen = PointToScreenDip(_resizeStart.Value);
        _resizeStartSize = new WpfSize(ActualWidth, ActualHeight);
        _resizeStartWindow = new WpfPoint(Left, Top);
        _resizeTargetRect = new Rect(Left, Top, ActualWidth, ActualHeight);
        LockReaderLayout();
        CaptureMouse();
        e.Handled = true;
    }

    private void FinishResize()
    {
        var anchor = _resizeReadingAnchor;
        _resizeDirection = ResizeDirection.None;
        _resizeStart = null;
        _resizeReadingAnchor = null;
        UnlockReaderLayout();
        ResizeHint.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
        RestoreReadingAnchorAfterLayout(anchor);
    }

    private void LockReaderLayout()
    {
        ReaderText.Width = Math.Max(1, ReaderText.ActualWidth);
        ReaderText.Height = Math.Max(1, ReaderText.ActualHeight);
        ReaderText.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        ReaderText.VerticalAlignment = VerticalAlignment.Top;
        ResizeHint.Visibility = Visibility.Collapsed;
    }

    private void UnlockReaderLayout()
    {
        ReaderText.ClearValue(WidthProperty);
        ReaderText.ClearValue(HeightProperty);
        ReaderText.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ReaderText.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private int? CaptureTopVisibleCharacterIndex()
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return null;
        }

        return Math.Clamp(_readerOffset, 0, _documentText.Length);
    }

    private void RestoreReadingAnchorAfterLayout(int? anchor)
    {
        if (anchor is null || string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => RestoreReadingAnchor(anchor.Value),
            DispatcherPriority.ContextIdle);
    }

    private void RestoreReadingAnchor(int anchor)
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return;
        }

        _readerOffset = Math.Clamp(anchor, 0, _documentText.Length);
        RenderCurrentPage(savePosition: true);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMoving)
        {
            _isMoving = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_resizeDirection == ResizeDirection.None)
        {
            return;
        }

        FinishResize();
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimizeToTray)
        {
            HideWindowToTray();
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Dispatcher.BeginInvoke(HideWindowToTray);
            return;
        }

        if (!string.IsNullOrEmpty(_documentText))
        {
            Dispatcher.BeginInvoke(() => RenderCurrentPage(savePosition: true), DispatcherPriority.ContextIdle);
        }
    }

    private ResizeDirection GetResizeDirection(WpfPoint point)
    {
        var left = point.X <= ResizeBorderThickness;
        var right = point.X >= ActualWidth - ResizeBorderThickness;
        var top = point.Y <= ResizeBorderThickness;
        var bottom = point.Y >= ActualHeight - ResizeBorderThickness;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => ResizeDirection.TopLeft,
            (_, true, true, _) => ResizeDirection.TopRight,
            (true, _, _, true) => ResizeDirection.BottomLeft,
            (_, true, _, true) => ResizeDirection.BottomRight,
            (true, _, _, _) => ResizeDirection.Left,
            (_, true, _, _) => ResizeDirection.Right,
            (_, _, true, _) => ResizeDirection.Top,
            (_, _, _, true) => ResizeDirection.Bottom,
            _ => ResizeDirection.None
        };
    }

    private static WpfCursor CursorForDirection(ResizeDirection direction) => direction switch
    {
        ResizeDirection.Left or ResizeDirection.Right => WpfCursors.SizeWE,
        ResizeDirection.Top or ResizeDirection.Bottom => WpfCursors.SizeNS,
        ResizeDirection.TopLeft or ResizeDirection.BottomRight => WpfCursors.SizeNWSE,
        ResizeDirection.TopRight or ResizeDirection.BottomLeft => WpfCursors.SizeNESW,
        _ => WpfCursors.Arrow
    };

    private void ResizeWindow(WpfMouseEventArgs e)
    {
        var current = PointToScreenDip(e.GetPosition(this));
        var dx = current.X - _resizeStartScreen.X;
        var dy = current.Y - _resizeStartScreen.Y;

        var newLeft = _resizeStartWindow.X;
        var newTop = _resizeStartWindow.Y;
        var newWidth = _resizeStartSize.Width;
        var newHeight = _resizeStartSize.Height;

        if (_resizeDirection is ResizeDirection.Right or ResizeDirection.TopRight or ResizeDirection.BottomRight)
        {
            newWidth = Math.Max(MinWidth, _resizeStartSize.Width + dx);
        }
        if (_resizeDirection is ResizeDirection.Bottom or ResizeDirection.BottomLeft or ResizeDirection.BottomRight)
        {
            newHeight = Math.Max(MinHeight, _resizeStartSize.Height + dy);
        }
        if (_resizeDirection is ResizeDirection.Left or ResizeDirection.TopLeft or ResizeDirection.BottomLeft)
        {
            newWidth = Math.Max(MinWidth, _resizeStartSize.Width - dx);
            newLeft = _resizeStartWindow.X + (_resizeStartSize.Width - newWidth);
        }
        if (_resizeDirection is ResizeDirection.Top or ResizeDirection.TopLeft or ResizeDirection.TopRight)
        {
            newHeight = Math.Max(MinHeight, _resizeStartSize.Height - dy);
            newTop = _resizeStartWindow.Y + (_resizeStartSize.Height - newHeight);
        }

        _resizeTargetRect = new Rect(newLeft, newTop, newWidth, newHeight);
        Left = _resizeTargetRect.Left;
        Top = _resizeTargetRect.Top;
        Width = _resizeTargetRect.Width;
        Height = _resizeTargetRect.Height;
    }

    private WpfPoint PointToScreenDip(WpfPoint point)
    {
        var screenPoint = PointToScreen(point);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return screenPoint;
        }

        return source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _resizeStart = e.GetPosition(this);
        _resizeStartSize = new WpfSize(ActualWidth, ActualHeight);
        ((UIElement)sender).CaptureMouse();
    }

    private void ResizeGrip_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_resizeStart is null || e.LeftButton != MouseButtonState.Pressed || WindowState == WindowState.Maximized)
        {
            return;
        }

        var current = e.GetPosition(this);
        Width = Math.Max(MinWidth, _resizeStartSize.Width + current.X - _resizeStart.Value.X);
        Height = Math.Max(MinHeight, _resizeStartSize.Height + current.Y - _resizeStart.Value.Y);
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _resizeStart = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateStatus()
    {
        var name = string.IsNullOrWhiteSpace(_filePath) ? "未打开文件" : Path.GetFileName(_filePath);
        FileStatusText.Text = name;
        ProgressStatusText.Text = $"{GetReadingProgressPercent()}%";
        AutoStatusText.Text = _settings.AutoPageEnabled ? $"自动 {_settings.AutoPageIntervalSeconds:0.#}s" : "自动 关";
    }

    private int GetReadingProgressPercent()
    {
        if (string.IsNullOrEmpty(_documentText))
        {
            return 0;
        }

        var visibleEnd = Math.Clamp(_visibleEndOffset, _readerOffset, _documentText.Length);
        var progress = (double)visibleEnd / _documentText.Length;
        return (int)Math.Clamp(Math.Round(progress * 100), 0, 100);
    }

    private void EnsureTrayIcon()
    {
        if (_isSeaSHosted)
        {
            DisposeTrayIcon();
            return;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = ShouldShowTrayIcon();
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示/隐藏", null, (_, _) => Dispatcher.Invoke(ToggleTrayWindowVisibility));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));

        var iconPath = Environment.ProcessPath;
        var icon = !string.IsNullOrWhiteSpace(iconPath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(iconPath)
            : null;

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "LittleFish",
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = ShouldShowTrayIcon()
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindowFromTray);
    }

    private bool ShouldShowTrayIcon() => !_isSeaSHosted && (_settings.MinimizeToTray || _isHiddenByBossKey || (IsLoaded && !IsVisible));

    private void ApplyTaskbarVisibility()
    {
        ShowInTaskbar = !_settings.MinimizeToTray && !_isHiddenByBossKey;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = ShouldShowTrayIcon();
        }
    }

    private void ToggleTrayWindowVisibility()
    {
        if (IsVisible && !_isHiddenByBossKey)
        {
            HideWindowToTray();
        }
        else
        {
            ShowWindowFromTray();
        }
    }

    private void HideWindowToTray()
    {
        EnsureTrayIcon();
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
        ApplyTaskbarVisibility();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
        }
    }

    private void ShowWindowFromTray()
    {
        _isHiddenByBossKey = false;
        ShowInTaskbar = !_settings.MinimizeToTray;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ApplyTaskbarVisibility();
    }

    private void StartSeaSHostCommandServer()
    {
        if (!_isSeaSHosted || string.IsNullOrWhiteSpace(_seaSHostPipeName))
        {
            return;
        }

        _seaSHostCommandCancellation = new CancellationTokenSource();
        _ = ListenForSeaSHostCommandsAsync(_seaSHostCommandCancellation.Token);
    }

    private async Task ListenForSeaSHostCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _seaSHostPipeName!,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync();

                if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(ShowWindowFromTray);
                }
                else if (string.Equals(command, "CLOSE", StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(Close);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await DelaySeaSHostCommandLoop(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static async Task DelaySeaSHostCommandLoop(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void ToggleBossKeyHide()
    {
        if (_isHiddenByBossKey || !IsVisible)
        {
            ShowWindowFromBossKey();
            return;
        }

        CloseTransientPopups(includeHelp: true);

        _settingsWindowWasVisibleBeforeBossKey = _settingsWindow?.IsVisible == true;
        _settingsWindow?.Hide();
        _isHiddenByBossKey = true;
        HideWindowToTray();
    }

    private void ShowWindowFromBossKey()
    {
        ShowWindowFromTray();
        if (_settingsWindowWasVisibleBeforeBossKey && _settingsWindow is not null)
        {
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        _settingsWindowWasVisibleBeforeBossKey = false;
    }

    private void ForceShowWindow()
    {
        _isHiddenByBossKey = false;
        _settingsWindowWasVisibleBeforeBossKey = false;
        _settings.BackgroundOpacity = 1;
        _settings.WindowOpacity = 1;
        _settings.ToolbarVisible = true;
        _settings.HoverHide = false;

        ShowWindowFromTray();
        SetToolbarVisible(true, preserveReaderSize: false);
        ApplyBackgroundBrush();
        Opacity = 1;
        ApplyTaskbarVisibility();
        SaveSettings();
        UpdateStatus();

        var shouldRestoreTopmost = !_settings.Topmost;
        Topmost = true;
        Activate();
        Focus();

        if (shouldRestoreTopmost)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Topmost = false;
                Activate();
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterGlobalHotkeys();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == BossHotkeyId)
        {
            ToggleBossKeyHide();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == ForceShowHotkeyId)
        {
            ForceShowWindow();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RegisterGlobalHotkeys()
    {
        RegisterBossHotkey();
        RegisterForceShowHotkey();
    }

    private void RegisterBossHotkey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        var handle = _hwndSource.Handle;
        UnregisterHotKey(handle, BossHotkeyId);

        if (!_settings.Shortcuts.TryGetValue(CommandIds.ToggleBossKey, out var shortcut) ||
            string.IsNullOrWhiteSpace(shortcut) ||
            !TryParseGlobalHotkey(shortcut, out var modifiers, out var virtualKey))
        {
            return;
        }

        RegisterHotKey(handle, BossHotkeyId, modifiers, virtualKey);
    }

    private void RegisterForceShowHotkey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        var handle = _hwndSource.Handle;
        UnregisterHotKey(handle, ForceShowHotkeyId);
        RegisterHotKey(handle, ForceShowHotkeyId, ModControl | ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.F12));
    }

    private static bool TryParseGlobalHotkey(string shortcut, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var keyText = "";
        foreach (var part in parts)
        {
            switch (part)
            {
                case "Ctrl":
                    modifiers |= ModControl;
                    break;
                case "Alt":
                    modifiers |= ModAlt;
                    break;
                case "Shift":
                    modifiers |= ModShift;
                    break;
                case "Win":
                    modifiers |= ModWin;
                    break;
                default:
                    keyText = part;
                    break;
            }
        }

        var key = keyText switch
        {
            "Plus" => Key.OemPlus,
            "Minus" => Key.OemMinus,
            "↑" => Key.Up,
            "↓" => Key.Down,
            "," => Key.OemComma,
            "." => Key.OemPeriod,
            _ => Enum.TryParse<Key>(keyText, out var parsedKey) ? parsedKey : Key.None
        };

        if (key == Key.None)
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        _seaSHostCommandCancellation?.Cancel();
        _seaSHostCommandCancellation?.Dispose();
        _seaSHostCommandCancellation = null;
        if (_hwndSource is not null)
        {
            UnregisterHotKey(_hwndSource.Handle, BossHotkeyId);
            UnregisterHotKey(_hwndSource.Handle, ForceShowHotkeyId);
            _hwndSource.RemoveHook(WndProc);
        }

        DisposeTrayIcon();

        base.OnClosing(e);
    }
}

public static class CommandIds
{
    public const string OpenFile = "open_file";
    public const string OpenSettings = "open_settings";
    public const string Find = "find";
    public const string FindNext = "find_next";
    public const string AddBookmark = "add_bookmark";
    public const string ToggleSidebar = "toggle_sidebar";
    public const string ToggleToolbar = "toggle_toolbar";
    public const string ToggleTopmost = "toggle_topmost";
    public const string ToggleHoverHide = "toggle_hover_hide";
    public const string ToggleAutoPage = "toggle_auto_page";
    public const string ToggleBossKey = "toggle_boss_key";
    public const string TurnPrevious = "turn_previous";
    public const string TurnNext = "turn_next";
    public const string IncreaseFont = "increase_font";
    public const string DecreaseFont = "decrease_font";

    public static readonly Dictionary<string, string> Labels = new()
    {
        [OpenFile] = "打开 TXT",
        [OpenSettings] = "打开设置",
        [Find] = "查找",
        [FindNext] = "查找下一个",
        [AddBookmark] = "添加书签",
        [ToggleSidebar] = "打开章节弹窗",
        [ToggleToolbar] = "显示/隐藏功能栏",
        [ToggleTopmost] = "开启/关闭置顶",
        [ToggleHoverHide] = "开启/关闭悬隐",
        [ToggleAutoPage] = "开启/关闭自动翻页",
        [ToggleBossKey] = "老板键 / 快捷隐藏",
        [TurnPrevious] = "向上翻动",
        [TurnNext] = "向下翻动",
        [IncreaseFont] = "增大字号",
        [DecreaseFont] = "减小字号"
    };
}

public sealed class AppSettings
{
    public string TextColor { get; set; } = "#111111";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public double BackgroundOpacity { get; set; } = 1;
    public double WindowOpacity { get; set; } = 0.96;
    public double HiddenOpacity { get; set; } = 0.08;
    public bool Topmost { get; set; }
    public bool HoverHide { get; set; }
    public bool AutoPageEnabled { get; set; }
    public double AutoPageIntervalSeconds { get; set; } = 5;
    public bool PageTurnByPage { get; set; }
    public bool ToolbarVisible { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 16;
    public double LineHeightPercent { get; set; } = 135;
    public string LastFile { get; set; } = "";
    public Dictionary<string, int> LastOffsets { get; set; } = [];
    public Dictionary<string, int> LastTopOffsets { get; set; } = [];
    public Dictionary<string, double> LastScrollOffsets { get; set; } = [];
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
    public Dictionary<string, string> Shortcuts { get; set; } = DefaultShortcuts();

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public static Dictionary<string, string> DefaultShortcuts() => new()
    {
        [CommandIds.OpenFile] = "Ctrl+O",
        [CommandIds.OpenSettings] = "Ctrl+,",
        [CommandIds.Find] = "Ctrl+F",
        [CommandIds.FindNext] = "F3",
        [CommandIds.AddBookmark] = "Ctrl+B",
        [CommandIds.ToggleSidebar] = "F8",
        [CommandIds.ToggleToolbar] = "F7",
        [CommandIds.ToggleTopmost] = "Ctrl+T",
        [CommandIds.ToggleHoverHide] = "F9",
        [CommandIds.ToggleAutoPage] = "F6",
        [CommandIds.ToggleBossKey] = "Ctrl+Shift+Z",
        [CommandIds.TurnPrevious] = "↑",
        [CommandIds.TurnNext] = "↓",
        [CommandIds.IncreaseFont] = "Ctrl+Plus",
        [CommandIds.DecreaseFont] = "Ctrl+Minus"
    };
}

public sealed record ChapterEntry(string Title, int Offset);

public sealed record BookmarkEntry(string FilePath, int Offset, string Title);

public enum ResizeDirection
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public static class KeyGestureText
{
    public static string FromKeyEvent(WpfKeyEventArgs e)
    {
        var key = GetGestureKey(e);
        if (IsModifierKey(key) || key == Key.None)
        {
            return "";
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string NormalizeDisplayText(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return "";
        }

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i] switch
            {
                "Up" => "↑",
                "Down" => "↓",
                _ => parts[i]
            };
        }

        return string.Join("+", parts);
    }

    private static Key GetGestureKey(WpfKeyEventArgs e)
    {
        if (e.Key == Key.System)
        {
            return e.SystemKey;
        }

        if (e.Key == Key.ImeProcessed)
        {
            return e.ImeProcessedKey;
        }

        if (e.Key == Key.DeadCharProcessed)
        {
            return e.DeadCharProcessedKey;
        }

        return e.Key;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string KeyName(Key key) => key switch
    {
        Key.OemPlus or Key.Add => "Plus",
        Key.OemMinus or Key.Subtract => "Minus",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Space => "Space",
        Key.Up => "↑",
        Key.Down => "↓",
        _ => key.ToString()
    };
}
