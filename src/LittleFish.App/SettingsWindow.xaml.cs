using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;

namespace LittleFish.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, WpfTextBox> _shortcutBoxes = [];
    private string _textColor = "#111111";
    private string _backgroundColor = "#FFFFFF";
    private double _fontSize = 16;
    private double _lineHeightPercent = 135;
    private string? _recordingCommand;

    public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied;
    public event EventHandler<SettingsPreviewChangedEventArgs>? SettingsPreviewChanged;
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();
        LoadSettings();
        BuildShortcutList();
    }

    private void LoadSettings()
    {
        _textColor = _settings.TextColor;
        _backgroundColor = _settings.BackgroundColor;
        _fontSize = _settings.FontSize;
        _lineHeightPercent = Math.Clamp(_settings.LineHeightPercent, 100, 250);
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, 0.01, 1);
        WindowOpacitySlider.Value = Math.Clamp(_settings.WindowOpacity, 0.01, 1);
        HiddenOpacitySlider.Value = Math.Clamp(_settings.HiddenOpacity, 0.01, 1);
        UpdateColorPreviews();
        UpdateFontSizeDisplay();
        UpdateLineHeightDisplay();
        UpdateOpacityValues();
        TopmostBox.IsChecked = _settings.Topmost;
        HoverHideBox.IsChecked = _settings.HoverHide;
        ToolbarVisibleBox.IsChecked = _settings.ToolbarVisible;
        MinimizeToTrayBox.IsChecked = _settings.MinimizeToTray;
        AutoPageEnabledBox.IsChecked = _settings.AutoPageEnabled;
        PageTurnByPageBox.IsChecked = _settings.PageTurnByPage;
        AutoPageIntervalBox.Text = _settings.AutoPageIntervalSeconds.ToString("0.##");
    }

    private void BuildShortcutList()
    {
        ShortcutList.Items.Clear();
        _shortcutBoxes.Clear();

        foreach (var (commandId, label) in CommandIds.Labels)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });

            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.DarkSlateGray
            };
            var box = new WpfTextBox
            {
                Text = _settings.Shortcuts.TryGetValue(commandId, out var shortcut) ? shortcut : "",
                Margin = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                IsReadOnly = true,
                Cursor = System.Windows.Input.Cursors.Arrow
            };
            var recordButton = new WpfButton
            {
                Content = "修改",
                Tag = commandId,
                MinWidth = 74
            };
            recordButton.Click += RecordShortcut_Click;

            var clearButton = new WpfButton
            {
                Content = "清空",
                Tag = commandId,
                MinWidth = 74,
                Margin = new Thickness(8, 0, 0, 0)
            };
            clearButton.Click += ClearShortcut_Click;

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(box, 1);
            Grid.SetColumn(recordButton, 2);
            Grid.SetColumn(clearButton, 3);
            row.Children.Add(labelBlock);
            row.Children.Add(box);
            row.Children.Add(recordButton);
            row.Children.Add(clearButton);
            ShortcutList.Items.Add(row);
            _shortcutBoxes[commandId] = box;
        }
    }

    private void RecordShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string commandId)
        {
            return;
        }

        _recordingCommand = commandId;
        _shortcutBoxes[commandId].Text = "请直接按键...";
        PreviewKeyDown -= SettingsWindow_PreviewKeyDown;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string commandId)
        {
            return;
        }

        if (_recordingCommand is not null)
        {
            PreviewKeyDown -= SettingsWindow_PreviewKeyDown;
            _recordingCommand = null;
        }

        _shortcutBoxes[commandId].Text = "";
        _settings.Shortcuts[commandId] = "";
    }

    private void SettingsWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_recordingCommand is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _shortcutBoxes[_recordingCommand].Text = _settings.Shortcuts.TryGetValue(_recordingCommand, out var existingShortcut)
                ? existingShortcut
                : "";
            _recordingCommand = null;
            PreviewKeyDown -= SettingsWindow_PreviewKeyDown;
            e.Handled = true;
            return;
        }

        var shortcut = KeyGestureText.FromKeyEvent(e);
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            e.Handled = true;
            return;
        }

        _shortcutBoxes[_recordingCommand].Text = shortcut;
        _settings.Shortcuts[_recordingCommand] = shortcut;
        _recordingCommand = null;
        PreviewKeyDown -= SettingsWindow_PreviewKeyDown;
        e.Handled = true;
    }

    private void PickTextColor_Click(object sender, RoutedEventArgs e)
    {
        _textColor = PickColor(_textColor);
        UpdateColorPreviews();
    }

    private void PickBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        _backgroundColor = PickColor(_backgroundColor);
        UpdateColorPreviews();
    }

    private void DecreaseFontSize_Click(object sender, RoutedEventArgs e)
    {
        ChangeFontSize(-1);
    }

    private void IncreaseFontSize_Click(object sender, RoutedEventArgs e)
    {
        ChangeFontSize(1);
    }

    private void DecreaseLineHeight_Click(object sender, RoutedEventArgs e)
    {
        ChangeLineHeight(-5);
    }

    private void IncreaseLineHeight_Click(object sender, RoutedEventArgs e)
    {
        ChangeLineHeight(5);
    }

    private void ChangeFontSize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 6, 96);
        UpdateFontSizeDisplay();
        RaiseTypographyPreviewChanged();
    }

    private void ChangeLineHeight(double delta)
    {
        _lineHeightPercent = Math.Clamp(_lineHeightPercent + delta, 100, 250);
        UpdateLineHeightDisplay();
        RaiseTypographyPreviewChanged();
    }

    private void FontSizeValueBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitFontSizeInput();
        if (sender is WpfTextBox box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    private void FontSizeValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitFontSizeInput();
    }

    private void LineHeightValueBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitLineHeightInput();
        if (sender is WpfTextBox box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    private void LineHeightValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitLineHeightInput();
    }

    private void CommitFontSizeInput()
    {
        if (FontSizeValueBox is null)
        {
            return;
        }

        if (TryParseFlexibleDouble(FontSizeValueBox.Text.Trim(), out var value))
        {
            _fontSize = Math.Clamp(Math.Round(value), 6, 96);
        }

        UpdateFontSizeDisplay();
        RaiseTypographyPreviewChanged();
    }

    private void CommitLineHeightInput()
    {
        if (LineHeightValueBox is null)
        {
            return;
        }

        if (TryParseLineHeightPercent(LineHeightValueBox.Text, out var value))
        {
            _lineHeightPercent = value;
        }

        UpdateLineHeightDisplay();
        RaiseTypographyPreviewChanged();
    }

    private void RaiseTypographyPreviewChanged()
    {
        var previewSettings = _settings.Clone();
        previewSettings.FontSize = _fontSize;
        previewSettings.LineHeightPercent = _lineHeightPercent;
        SettingsPreviewChanged?.Invoke(this, new SettingsPreviewChangedEventArgs(previewSettings));
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityValues();
    }

    private void OpacityValueBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitOpacityInput(sender);
        if (sender is WpfTextBox box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    private void OpacityValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitOpacityInput(sender);
    }

    private void CommitOpacityInput(object? sender)
    {
        if (ReferenceEquals(sender, BackgroundOpacityValueBox))
        {
            CommitOpacityInput(BackgroundOpacityValueBox, BackgroundOpacitySlider);
        }
        else if (ReferenceEquals(sender, WindowOpacityValueBox))
        {
            CommitOpacityInput(WindowOpacityValueBox, WindowOpacitySlider);
        }
        else if (ReferenceEquals(sender, HiddenOpacityValueBox))
        {
            CommitOpacityInput(HiddenOpacityValueBox, HiddenOpacitySlider);
        }
    }

    private void CommitOpacityInput(WpfTextBox? box, Slider? slider)
    {
        if (box is null || slider is null)
        {
            return;
        }

        if (TryParseOpacity(box.Text, out var opacity))
        {
            slider.Value = opacity;
        }

        UpdateOpacityValues(force: true);
    }

    private void UpdateColorPreviews()
    {
        TextColorPreview.Background = CreateColorBrush(_textColor, MediaBrushes.Black);
        BackgroundColorPreview.Background = CreateColorBrush(_backgroundColor, MediaBrushes.White);
    }

    private void UpdateFontSizeDisplay()
    {
        if (FontSizeValueBox is null)
        {
            return;
        }

        FontSizeValueBox.Text = _fontSize.ToString("0", CultureInfo.CurrentCulture);
    }

    private void UpdateLineHeightDisplay()
    {
        if (LineHeightValueBox is null)
        {
            return;
        }

        LineHeightValueBox.Text = $"{Math.Round(_lineHeightPercent):0}%";
    }

    private void UpdateOpacityValues(bool force = false)
    {
        if (BackgroundOpacitySlider is null
            || WindowOpacitySlider is null
            || HiddenOpacitySlider is null
            || BackgroundOpacityValueBox is null
            || WindowOpacityValueBox is null
            || HiddenOpacityValueBox is null)
        {
            return;
        }

        SetOpacityText(BackgroundOpacityValueBox, BackgroundOpacitySlider.Value, force);
        SetOpacityText(WindowOpacityValueBox, WindowOpacitySlider.Value, force);
        SetOpacityText(HiddenOpacityValueBox, HiddenOpacitySlider.Value, force);
    }

    private static string FormatOpacity(double value) => $"{Math.Round(Math.Clamp(value, 0.01, 1) * 100):0}%";

    private static void SetOpacityText(WpfTextBox box, double value, bool force)
    {
        if (!force && box.IsKeyboardFocusWithin)
        {
            return;
        }

        box.Text = FormatOpacity(value);
    }

    private static bool TryParseOpacity(string text, out double opacity)
    {
        opacity = 0;
        var normalized = text.Trim().Replace("％", "%", StringComparison.Ordinal);
        normalized = normalized.Replace("%", "", StringComparison.Ordinal).Trim();

        if (!TryParseFlexibleDouble(normalized, out var value))
        {
            return false;
        }

        opacity = value / 100;
        opacity = Math.Clamp(opacity, 0.01, 1);
        return true;
    }

    private static bool TryParseLineHeightPercent(string text, out double percent)
    {
        percent = 0;
        var normalized = text.Trim().Replace("％", "%", StringComparison.Ordinal);
        var hasPercent = normalized.Contains('%', StringComparison.Ordinal);
        normalized = normalized.Replace("%", "", StringComparison.Ordinal).Trim();

        if (!TryParseFlexibleDouble(normalized, out var value))
        {
            return false;
        }

        percent = !hasPercent && value <= 3
            ? value * 100
            : value;
        percent = Math.Clamp(Math.Round(percent), 100, 250);
        return true;
    }

    private static bool TryParseFlexibleDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static MediaBrush CreateColorBrush(string colorText, MediaBrush fallback)
    {
        try
        {
            if (MediaColorConverter.ConvertFromString(colorText) is MediaColor color)
            {
                return new MediaSolidColorBrush(color);
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string PickColor(string current)
    {
        var color = DrawingColor.White;
        try
        {
            var parsed = System.Drawing.ColorTranslator.FromHtml(current);
            color = DrawingColor.FromArgb(parsed.R, parsed.G, parsed.B);
        }
        catch
        {
        }

        using var dialog = new Forms.ColorDialog { Color = color };
        return dialog.ShowDialog() == Forms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : current;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _settings.Shortcuts = AppSettings.DefaultShortcuts();
        BuildShortcutList();
    }

    private void ForceShowHelp_Click(object sender, RoutedEventArgs e)
    {
        ForceShowHelpPopup.IsOpen = !ForceShowHelpPopup.IsOpen;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings(closeAfterApply: false);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings(closeAfterApply: true);
    }

    private void ApplySettings(bool closeAfterApply)
    {
        CommitFontSizeInput();
        CommitLineHeightInput();
        CommitOpacityInput(BackgroundOpacityValueBox);
        CommitOpacityInput(WindowOpacityValueBox);
        CommitOpacityInput(HiddenOpacityValueBox);

        _settings.TextColor = _textColor;
        _settings.BackgroundColor = _backgroundColor;
        _settings.BackgroundOpacity = Math.Clamp(BackgroundOpacitySlider.Value, 0.01, 1);
        _settings.WindowOpacity = WindowOpacitySlider.Value;
        _settings.HiddenOpacity = HiddenOpacitySlider.Value;
        _settings.FontSize = _fontSize;
        _settings.LineHeightPercent = _lineHeightPercent;
        _settings.Topmost = TopmostBox.IsChecked == true;
        _settings.HoverHide = HoverHideBox.IsChecked == true;
        _settings.ToolbarVisible = ToolbarVisibleBox.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.AutoPageEnabled = AutoPageEnabledBox.IsChecked == true;
        _settings.PageTurnByPage = PageTurnByPageBox.IsChecked == true;
        if (double.TryParse(AutoPageIntervalBox.Text.Trim(), out var interval))
        {
            _settings.AutoPageIntervalSeconds = Math.Clamp(interval, 1, 3600);
        }
        else
        {
            System.Windows.MessageBox.Show(this, "翻页间隔请输入数字，单位为秒。", "翻页设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var (commandId, box) in _shortcutBoxes)
        {
            _settings.Shortcuts[commandId] = box.Text.Trim();
        }

        var duplicate = _settings.Shortcuts
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            System.Windows.MessageBox.Show(this, $"快捷键冲突：{duplicate.Key}", "快捷键", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SettingsApplied?.Invoke(this, new SettingsAppliedEventArgs(_settings.Clone()));
        if (closeAfterApply)
        {
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryDragWindow(e);
    }

    private void WindowSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            TryDragWindow(e);
        }
    }

    private void TryDragWindow(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or Slider
                or System.Windows.Controls.Primitives.Selector
                or TabItem
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            element = GetParent(element);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement && frameworkElement.Parent is DependencyObject frameworkParent)
        {
            return frameworkParent;
        }

        if (element is FrameworkContentElement contentElement && contentElement.Parent is DependencyObject contentParent)
        {
            return contentParent;
        }

        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class SettingsAppliedEventArgs(AppSettings settings) : EventArgs
{
    public AppSettings Settings { get; } = settings;
}

public sealed class SettingsPreviewChangedEventArgs(AppSettings settings) : EventArgs
{
    public AppSettings Settings { get; } = settings;
}
