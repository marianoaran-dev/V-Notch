using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.ViewModels;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private StackPanel? _displayRoot;
    private StackPanel? _displayMonitorSections;
    private ScrollViewer? _displayOverflowScroll;
    private TextBlock? _displayStatusText;
    private Action<bool>? _displayAllLinkVisual;
    private bool _displayUiBuilt;
    private bool _displayEventsAttached;
    private int _displayHeightGeneration;
    private bool _displayFitPending;

    private readonly Dictionary<DisplayMonitorRowViewModel, PropertyChangedEventHandler> _displayRowHandlers = new();

    private static readonly Geometry DisplayHeaderIconGeometry = MakeFrozenGeometry(
        "M3,4 H21 C22.1,4 23,4.9 23,6 V16 C23,17.1 22.1,18 21,18 H13 V20 H17 V22 H7 V20 H11 V18 H3 C1.9,18 1,17.1 1,16 V6 C1,4.9 1.9,4 3,4 Z M3,6 V16 H21 V6 Z");

    private void EnsureDisplayViewBuilt()
    {
        if (_displayUiBuilt) return;
        _displayUiBuilt = true;

        DisplayContent.Children.Clear();
        DisplayContent.RowDefinitions.Clear();
        DisplayContent.ColumnDefinitions.Clear();
        DisplayContent.Margin = new Thickness(24, 38, 24, 20);

        _displayRoot = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        _displayRoot.Children.Add(BuildDisplayHeader());

        _displayStatusText = new TextBlock
        {
            Foreground = AudioMuted,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = AudioFont,
            MinHeight = 14,
            Margin = new Thickness(30, 2, 0, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Hidden
        };
        _displayRoot.Children.Add(_displayStatusText);

        _displayMonitorSections = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 7, 0, 0)
        };
        _displayRoot.Children.Add(_displayMonitorSections);

        _displayOverflowScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = _displayRoot
        };
        DisplayContent.Children.Add(_displayOverflowScroll);

        AttachDisplayEvents();
        RebuildDisplayMonitorSections();
        UpdateDisplayStatusVisual();
        UpdateDisplayAllLinkVisual();
        RecalculateDisplayFitHeight(animate: false);
    }

    private FrameworkElement BuildDisplayHeader()
    {
        var grid = new Grid { Height = 46 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Viewbox
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new Path
            {
                Data = DisplayHeaderIconGeometry,
                Fill = Brushes.White,
                Stretch = Stretch.Uniform
            }
        };
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(new TextBlock
        {
            Text = "Displays",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont
        });
        title.Children.Add(new TextBlock
        {
            Text = "Brightness and contrast",
            Foreground = AudioMuted,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = AudioFont,
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var allLink = CreateDisplayToggle(
            "Link monitors",
            _displayViewModel.IsAllMonitorsLinked,
            enabled => _displayViewModel.IsAllMonitorsLinked = enabled,
            out _displayAllLinkVisual);
        allLink.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(allLink, 2);
        grid.Children.Add(allLink);

        var refresh = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            BorderBrush = AudioComboBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "↻",
                Foreground = AudioMuted,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                FontFamily = AudioFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            },
            ToolTip = "Refresh monitors"
        };
        refresh.MouseEnter += (_, _) => refresh.Background = AudioComboHover;
        refresh.MouseLeave += (_, _) => refresh.Background = Brushes.Transparent;
        refresh.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!_isAnimating) _ = _displayViewModel.RefreshAsync();
        };
        Grid.SetColumn(refresh, 3);
        grid.Children.Add(refresh);

        return grid;
    }

    private void AttachDisplayEvents()
    {
        if (_displayEventsAttached) return;
        _displayEventsAttached = true;

        _displayViewModel.Monitors.CollectionChanged += DisplayMonitors_CollectionChanged;
        _displayViewModel.PropertyChanged += DisplayViewModel_PropertyChanged;
    }

    private void DisplayMonitors_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => DisplayMonitors_CollectionChanged(sender, e)));
            return;
        }

        RebuildDisplayMonitorSections();
        RequestDisplayFitRecalculation();
    }

    private void DisplayViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => DisplayViewModel_PropertyChanged(sender, e)));
            return;
        }

        if (e.PropertyName == nameof(DisplayMonitorsViewModel.StatusText) ||
            e.PropertyName == nameof(DisplayMonitorsViewModel.IsLoading))
        {
            UpdateDisplayStatusVisual();
            RequestDisplayFitRecalculation();
        }
        else if (e.PropertyName == nameof(DisplayMonitorsViewModel.IsAllMonitorsLinked))
        {
            UpdateDisplayAllLinkVisual();
        }
    }

    private void RebuildDisplayMonitorSections()
    {
        if (_displayMonitorSections == null) return;

        foreach (var (row, handler) in _displayRowHandlers)
            row.PropertyChanged -= handler;
        _displayRowHandlers.Clear();
        _displayMonitorSections.Children.Clear();

        if (_displayViewModel.Monitors.Count == 0)
        {
            if (!_displayViewModel.IsLoading)
            {
                _displayMonitorSections.Children.Add(new TextBlock
                {
                    Text = "No compatible external displays detected.",
                    Foreground = AudioMuted,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = AudioFont,
                    Margin = new Thickness(30, 18, 0, 14)
                });
            }
            return;
        }

        for (var index = 0; index < _displayViewModel.Monitors.Count; index++)
        {
            var row = _displayViewModel.Monitors[index];
            _displayMonitorSections.Children.Add(BuildDisplayMonitorSection(row, index));

            if (index < _displayViewModel.Monitors.Count - 1)
            {
                _displayMonitorSections.Children.Add(new Border
                {
                    Height = 1,
                    Background = Frozen("#16FFFFFF"),
                    Margin = new Thickness(0, 14, 0, 12)
                });
            }
        }
    }

    private FrameworkElement BuildDisplayMonitorSection(DisplayMonitorRowViewModel row, int index)
    {
        var section = new StackPanel
        {
            Orientation = Orientation.Vertical,
            ToolTip = row.Monitor.DisplayName
        };

        var header = new Grid { Height = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titlePanel.Children.Add(new TextBlock
        {
            Text = FriendlyDisplayTitle(row, index),
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (ShouldShowMonitorDescription(row.Description))
        {
            titlePanel.Children.Add(new TextBlock
            {
                Text = row.Description,
                Foreground = AudioMuted,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = AudioFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 230
            });
        }

        Grid.SetColumn(titlePanel, 0);
        header.Children.Add(titlePanel);

        var localLink = CreateDisplayToggle(
            "Link",
            row.IsLinkEnabled,
            enabled => row.IsLinkEnabled = enabled,
            out var setLinkVisual);
        Grid.SetColumn(localLink, 1);
        header.Children.Add(localLink);
        section.Children.Add(header);

        var brightness = BuildDisplayControlRow(
            "Brightness",
            row.IsBrightnessSupported,
            row.Brightness,
            value => ApplyDisplayControlChange(row, MonitorControlKind.Brightness, value),
            out var setBrightnessVisual);
        brightness.Margin = new Thickness(0, 4, 0, 0);
        section.Children.Add(brightness);

        var contrast = BuildDisplayControlRow(
            "Contrast",
            row.IsContrastSupported,
            row.Contrast,
            value => ApplyDisplayControlChange(row, MonitorControlKind.Contrast, value),
            out var setContrastVisual);
        contrast.Margin = new Thickness(0, 3, 0, 0);
        section.Children.Add(contrast);

        var error = new TextBlock
        {
            Foreground = Frozen("#FFFFA36C"),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = AudioFont,
            Margin = new Thickness(132, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        section.Children.Add(error);

        void UpdateRowState()
        {
            setLinkVisual(row.IsLinkEnabled);
            setBrightnessVisual(row.Brightness);
            setContrastVisual(row.Contrast);

            var showError = !string.Equals(row.StatusText, "Ready", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(row.StatusText);
            error.Text = showError ? row.StatusText : string.Empty;
            error.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateRowState));
                return;
            }

            if (e.PropertyName is nameof(DisplayMonitorRowViewModel.Brightness)
                or nameof(DisplayMonitorRowViewModel.Contrast)
                or nameof(DisplayMonitorRowViewModel.IsLinkEnabled)
                or nameof(DisplayMonitorRowViewModel.StatusText))
            {
                UpdateRowState();
            }
        };
        row.PropertyChanged += handler;
        _displayRowHandlers[row] = handler;
        UpdateRowState();

        return section;
    }

    private FrameworkElement BuildDisplayControlRow(
        string label,
        bool supported,
        double initialPercentage,
        Action<double> onChanged,
        out Action<double> setVisual)
    {
        var grid = new Grid
        {
            Height = 40,
            Opacity = supported ? 1 : 0.42
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        var percent = new TextBlock
        {
            Foreground = AudioMuted,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Text = supported ? $"{Math.Round(initialPercentage):0}%" : "—"
        };
        Grid.SetColumn(percent, 2);
        grid.Children.Add(percent);

        var slider = CreateDisplaySlider(
            initialPercentage,
            supported,
            onChanged,
            percent,
            out setVisual);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        return grid;
    }

    private FrameworkElement CreateDisplaySlider(
        double initialPercentage,
        bool enabled,
        Action<double> onChanged,
        TextBlock percentLabel,
        out Action<double> setVisual)
    {
        var current = Math.Clamp(initialPercentage / 100.0, 0, 1);
        const double thumbSize = 15;

        var area = new Grid
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            IsHitTestVisible = enabled
        };

        var trackScale = new ScaleTransform(1, 1);
        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(4),
            Background = AudioTrack,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = trackScale,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        area.Children.Add(track);

        var fillScale = new ScaleTransform(current, 1);
        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(4),
            Background = enabled ? AudioGreen : AudioMuted,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = fillScale,
            RenderTransformOrigin = new Point(0, 0.5)
        };
        area.Children.Add(fill);

        var thumbScale = new ScaleTransform(1, 1);
        var thumbTranslate = new TranslateTransform();
        var thumbGroup = new TransformGroup();
        thumbGroup.Children.Add(thumbScale);
        thumbGroup.Children.Add(thumbTranslate);

        var thumb = new Ellipse
        {
            Width = thumbSize,
            Height = thumbSize,
            Fill = enabled ? Brushes.White : AudioMuted,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransform = thumbGroup,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        area.Children.Add(thumb);

        void UpdateVisual(double ratio)
        {
            current = Math.Clamp(ratio, 0, 1);
            var width = area.ActualWidth;
            if (width > 0)
            {
                var usable = Math.Max(0, width - thumbSize);
                fillScale.ScaleX = current;
                thumbTranslate.X = current * usable;
            }

            if (enabled)
                percentLabel.Text = $"{Math.Round(current * 100):0}%";
        }

        area.Loaded += (_, _) => UpdateVisual(current);
        area.SizeChanged += (_, _) => UpdateVisual(current);

        bool dragging = false;
        void SetFromPointer(double x)
        {
            if (!enabled || area.ActualWidth <= 0) return;
            var ratio = Math.Clamp(x / area.ActualWidth, 0, 1);
            UpdateVisual(ratio);
            onChanged(ratio * 100.0);
        }

        area.MouseLeftButtonDown += (_, e) =>
        {
            if (!enabled) return;
            dragging = true;
            area.CaptureMouse();
            SetFromPointer(e.GetPosition(area).X);
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (dragging) SetFromPointer(e.GetPosition(area).X);
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            area.ReleaseMouseCapture();
            SetFromPointer(e.GetPosition(area).X);
            CommitDisplayWrites();
            e.Handled = true;
        };
        area.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            CommitDisplayWrites();
        };

        void AnimateHover(bool hover)
        {
            if (!enabled) return;
            var duration = TimeSpan.FromMilliseconds(hover ? 300 : 220);
            IEasingFunction easing = hover
                ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var barScale = new DoubleAnimation
            {
                To = hover ? 1.7 : 1,
                Duration = duration,
                EasingFunction = easing
            };
            var thumbAnim = new DoubleAnimation
            {
                To = hover ? 1.16 : 1,
                Duration = duration,
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(barScale, AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(thumbAnim, AnimationConfig.TargetFps);
            trackScale.BeginAnimation(ScaleTransform.ScaleYProperty, barScale);
            fillScale.BeginAnimation(ScaleTransform.ScaleYProperty, barScale);
            thumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbAnim);
            thumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbAnim);
        }

        area.MouseEnter += (_, _) => AnimateHover(true);
        area.MouseLeave += (_, _) =>
        {
            if (!dragging) AnimateHover(false);
        };

        setVisual = percentage =>
        {
            if (dragging) return;
            UpdateVisual(Math.Clamp(percentage / 100.0, 0, 1));
        };

        return area;
    }

    private void ApplyDisplayControlChange(
        DisplayMonitorRowViewModel row,
        MonitorControlKind control,
        double requestedPercentage)
    {
        var requested = MonitorLinkEngine.ClampPercentage(requestedPercentage);
        var oldValue = control == MonitorControlKind.Brightness ? row.Brightness : row.Contrast;

        if (control == MonitorControlKind.Brightness)
            row.Brightness = requested;
        else
            row.Contrast = requested;

        _displayViewModel.ApplyUserChange(row, control, oldValue, requested);
    }

    private Border CreateDisplayToggle(
        string label,
        bool initialValue,
        Action<bool> onChanged,
        out Action<bool> setVisual)
    {
        var state = initialValue;
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var border = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };

        void Apply(bool enabled)
        {
            state = enabled;
            border.Background = enabled ? Frozen("#263FD15B") : AudioComboBg;
            border.BorderBrush = enabled ? Frozen("#663FD15B") : AudioComboBorder;
            text.Foreground = enabled ? AudioGreen : AudioMuted;
        }

        border.MouseEnter += (_, _) =>
        {
            if (!state) border.Background = AudioComboHover;
        };
        border.MouseLeave += (_, _) => Apply(state);
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            Apply(state);
            onChanged(state);
        };

        setVisual = Apply;
        Apply(initialValue);
        return border;
    }

    private void UpdateDisplayAllLinkVisual()
        => _displayAllLinkVisual?.Invoke(_displayViewModel.IsAllMonitorsLinked);

    private void UpdateDisplayStatusVisual()
    {
        if (_displayStatusText == null) return;

        var text = _displayViewModel.StatusText ?? string.Empty;
        var show = _displayViewModel.IsLoading ||
                   text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("No ", StringComparison.OrdinalIgnoreCase);

        _displayStatusText.Text = text;
        _displayStatusText.Foreground = text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                        text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            ? Frozen("#FFFFA36C")
            : AudioMuted;
        _displayStatusText.Visibility = show ? Visibility.Visible : Visibility.Hidden;
    }

    private static string FriendlyDisplayTitle(DisplayMonitorRowViewModel row, int index)
        => $"Display {index + 1}";

    private static bool ShouldShowMonitorDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        return !description.Contains("Generic", StringComparison.OrdinalIgnoreCase) &&
               !description.Contains("Physical monitor", StringComparison.OrdinalIgnoreCase);
    }

    private void RequestDisplayFitRecalculation()
    {
        if (_isDisplayView && _isAnimating)
        {
            _displayFitPending = true;
            return;
        }

        _displayFitPending = false;
        RecalculateDisplayFitHeight(animate: _isDisplayView);
    }

    private void RecalculateDisplayFitHeight(bool animate)
    {
        if (_displayRoot == null || _displayOverflowScroll == null) return;

        var contentWidth = Math.Max(360, _displayViewWidth - DisplayContent.Margin.Left - DisplayContent.Margin.Right);
        _displayRoot.Measure(new Size(contentWidth, double.PositiveInfinity));

        var desiredContent = _displayRoot.DesiredSize.Height;
        var chrome = DisplayContent.Margin.Top + DisplayContent.Margin.Bottom + 8;
        var naturalHeight = Math.Max(_displayViewMinHeight, desiredContent + chrome);
        var maxHeight = Math.Max(_displayViewMinHeight, SystemParameters.WorkArea.Height - 72);
        var target = Math.Min(naturalHeight, maxHeight);
        var overflow = naturalHeight > maxHeight + 0.5;

        _displayOverflowScroll.VerticalScrollBarVisibility = overflow
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;

        _displayViewHeight = target;
        PrepareDisplayContentLayout();

        if (animate && _isDisplayView && !_isAnimating)
            AnimateDisplayNotchHeight(target);
    }

    private void SettleDisplayNotchToFit()
    {
        _displayFitPending = false;
        RecalculateDisplayFitHeight(animate: false);
        AnimateDisplayNotchHeight(_displayViewHeight);
    }

    private void AnimateDisplayNotchHeight(double target)
    {
        var generation = ++_displayHeightGeneration;
        if (!_isDisplayView || _isAnimating) return;

        var current = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        if (Math.Abs(current - target) < 0.5)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            PrepareDisplayContentLayout();
            ResizeHostWindowHeight(target);
            return;
        }

        var growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = current;
        _isAnimating = true;

        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var animation = MakeAnim(current, target, duration, _easeExpOut6);
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        animation.Completed += (_, _) =>
        {
            _isAnimating = false;
            if (generation != _displayHeightGeneration || !_isDisplayView) return;

            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            PrepareDisplayContentLayout();
            if (!growing) ResizeHostWindowHeight(target);

            if (_displayFitPending)
            {
                _displayFitPending = false;
                Dispatcher.BeginInvoke(new Action(() => RecalculateDisplayFitHeight(animate: true)));
            }
        };
        NotchBorder.BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
