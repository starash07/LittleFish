using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfListBox = System.Windows.Controls.ListBox;

namespace LittleFish.App;

public sealed class ChapterPickerWindow : Window
{
    private readonly WpfListBox _chapterList = new();

    public int? SelectedOffset { get; private set; }

    public ChapterPickerWindow(IReadOnlyList<ChapterEntry> chapters, int currentOffset)
    {
        Title = "章节";
        Width = 420;
        Height = 560;
        MinWidth = 320;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        Resources.MergedDictionaries.Add(CreateStyleResources());
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        _chapterList.ItemsSource = chapters;
        _chapterList.DisplayMemberPath = nameof(ChapterEntry.Title);
        _chapterList.BorderThickness = new Thickness(0);
        _chapterList.Background = WpfBrushes.Transparent;
        _chapterList.Padding = new Thickness(0);
        _chapterList.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        _chapterList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _chapterList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _chapterList.MouseDoubleClick += (_, _) => AcceptSelection();
        _chapterList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
        };

        Content = BuildContent();

        Loaded += (_, _) =>
        {
            if (chapters.Count == 0)
            {
                return;
            }

            var selectedIndex = 0;
            for (var i = 0; i < chapters.Count; i++)
            {
                if (chapters[i].Offset <= currentOffset)
                {
                    selectedIndex = i;
                }
                else
                {
                    break;
                }
            }

            _chapterList.SelectedIndex = selectedIndex;
            _chapterList.ScrollIntoView(chapters[selectedIndex]);
            _chapterList.Focus();
        };
    }

    private Border BuildContent()
    {
        var title = new TextBlock
        {
            Text = "章节",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(16, 24, 40)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var chapterCount = new TextBlock
        {
            Text = $"{_chapterList.Items.Count} 个章节",
            Foreground = new SolidColorBrush(WpfColor.FromRgb(102, 112, 133)),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new WpfButton
        {
            Content = "×",
            Width = 34,
            Height = 34,
            MinWidth = 34,
            Padding = new Thickness(0),
            Style = (Style)Resources["ChapterWindowButton"]
        };
        closeButton.Click += (_, _) => Close();

        var titlePanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titlePanel.Children.Add(title);
        titlePanel.Children.Add(chapterCount);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (closeButton.IsMouseOver)
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        };
        DockPanel.SetDock(closeButton, Dock.Right);
        header.Children.Add(closeButton);
        header.Children.Add(titlePanel);

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(_chapterList);

        return new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(WpfColor.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(214, 236, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.16,
                Color = WpfColor.FromRgb(102, 112, 133)
            },
            Child = panel
        };
    }

    private static ResourceDictionary CreateStyleResources()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="ChapterWindowButton" TargetType="{x:Type Button}">
        <Setter Property="Background" Value="#FFFFFF" />
        <Setter Property="BorderBrush" Value="#D7EEFF" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Foreground" Value="#1D2939" />
        <Setter Property="FontSize" Value="15" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type Button}">
                    <Border x:Name="Chrome"
                            CornerRadius="17"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}">
                        <ContentPresenter HorizontalAlignment="Center"
                                          VerticalAlignment="Center"
                                          Margin="{TemplateBinding Padding}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Chrome" Property="Background" Value="#F2FAFF" />
                            <Setter TargetName="Chrome" Property="BorderBrush" Value="#9DDBFF" />
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Chrome" Property="Background" Value="#EAF7FF" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="{x:Type ScrollBar}">
        <Setter Property="Width" Value="10" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ScrollBar}">
                    <Grid Background="Transparent">
                        <Track x:Name="PART_Track"
                               IsDirectionReversed="True"
                               Focusable="False">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageUpCommand"
                                              Opacity="0"
                                              Focusable="False" />
                            </Track.DecreaseRepeatButton>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageDownCommand"
                                              Opacity="0"
                                              Focusable="False" />
                            </Track.IncreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb MinHeight="34" Background="#9DDBFF">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="{x:Type Thumb}">
                                            <Border x:Name="ThumbChrome"
                                                    Width="6"
                                                    HorizontalAlignment="Center"
                                                    CornerRadius="3"
                                                    Background="{TemplateBinding Background}" />
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="ThumbChrome" Property="Background" Value="#74C8FF" />
                                                </Trigger>
                                                <Trigger Property="IsDragging" Value="True">
                                                    <Setter TargetName="ThumbChrome" Property="Background" Value="#2EA8FF" />
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="{x:Type ListBoxItem}">
        <Setter Property="Padding" Value="12,8" />
        <Setter Property="Margin" Value="0,0,4,5" />
        <Setter Property="Foreground" Value="#344054" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ListBoxItem}">
                    <Border x:Name="Chrome"
                            CornerRadius="10"
                            Background="Transparent"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Chrome" Property="Background" Value="#F2FAFF" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Chrome" Property="Background" Value="#EAF7FF" />
                            <Setter Property="Foreground" Value="#1570EF" />
                            <Setter Property="FontWeight" Value="SemiBold" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
""";

        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    private void AcceptSelection()
    {
        if (_chapterList.SelectedItem is not ChapterEntry entry)
        {
            return;
        }

        SelectedOffset = entry.Offset;
        Close();
    }
}
