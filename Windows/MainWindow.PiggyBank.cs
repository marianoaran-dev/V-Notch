using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private const double PiggyBankViewWidth = 650;
    private const double PiggyBankBaseHeight = 372;
    private const double PiggyLiquidMaxHeight = 158;

    private double _piggyBankViewHeight = PiggyBankBaseHeight;
    private bool _piggyBankBuilt;
    private bool _piggySettingPreviewValues;
    private PiggyBankSnapshot? _piggyBankSnapshot;
    private PiggyBankQuotaService? _piggyBankQuotaServiceCached;
    private CancellationTokenSource? _piggyRefreshCancellation;
    private DispatcherTimer? _piggyClockTimer;
    private ImageSource? _piggyPanelIconSource;

    private TextBlock? _piggyStatusText;
    private TextBlock? _piggyFivePercentText;
    private TextBlock? _piggyFiveResetText;
    private TextBlock? _piggyWeekPercentText;
    private TextBlock? _piggyWeekRemainingText;
    private TextBlock? _piggyWeekResetText;
    private StackPanel? _piggyWeekDayBlocks;
    private WrapPanel? _piggyBankedResetPanel;
    private Slider? _piggyFivePreviewSlider;
    private Slider? _piggyWeekPreviewSlider;
    private TextBlock? _piggyFivePreviewValue;
    private TextBlock? _piggyWeekPreviewValue;
    private PiggyQuotaVisual? _piggyFiveVisual;
    private PiggyQuotaVisual? _piggyWeekVisual;

    private PiggyBankQuotaService PiggyBankQuotaService =>
        _piggyBankQuotaServiceCached ??= (PiggyBankQuotaService)App.Services.GetService(typeof(PiggyBankQuotaService))!;

    private bool _isPiggyBankView
    {
        get => _notchState.IsPiggyBankView;
        set
        {
            _notchState.IsPiggyBankView = value;
            if (value)
                _viewModel.SetView(NotchView.PiggyBank);
            else if (_viewModel.CurrentView == NotchView.PiggyBank)
                _viewModel.SetView(NotchView.Media);
        }
    }

    private void PiggyBankIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isPiggyBankView || _isAnimating) return;
        SwitchToPiggyBankView();
    }

    private void SwitchToPiggyBankView()
    {
        if (_isPiggyBankView || _isAnimating) return;

        EnsurePiggyBankViewBuilt();

        var fromDisplay = _isDisplayView;
        var fromAudio = _isAudioView;
        var fromTimer = _isTimerView;
        var fromSecondary = _isSecondaryView;
        var fromPrimary = !fromDisplay && !fromAudio && !fromTimer && !fromSecondary;
        FrameworkElement outgoing = fromDisplay
            ? DisplayContent
            : fromAudio
                ? AudioContent
                : fromTimer
                    ? TimerContent
                    : fromSecondary
                        ? SecondaryContent
                        : ExpandedContent;

        if (fromDisplay) CommitDisplayWrites();
        if (fromAudio)
        {
            StopAudioPoll();
            _audioMixerServiceCached?.ReleaseSessionCache();
        }
        if (fromTimer) CancelTimerEditingInstant();
        if (fromSecondary)
        {
            StopCameraPreviewForViewExit();
            DisableKeyboardInput();
        }

        _isDisplayView = false;
        _isAudioView = false;
        _isTimerView = false;
        _isSecondaryView = false;
        _isPiggyBankView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        SuspendSpotifyCanvasLifecycle();

        if (fromPrimary)
        {
            HideMediaBackground();
            HideLyricsBlurForUtility();
        }

        ShowUtilityNavigation();
        RefreshPiggyClockText();
        PreparePiggyBankContentLayout();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        var openWindowHeight = Math.Max(fromHeight, _piggyBankViewHeight);
        ResizeHostWindowHeight(openWindowHeight);

        AnimateAudioViewSwap(
            outgoing,
            PiggyBankContent,
            fromWidth,
            fromHeight,
            PiggyBankViewWidth,
            _piggyBankViewHeight,
            prepIncoming: PreparePiggyBankContentLayout,
            onComplete: () =>
            {
                if (openWindowHeight > _piggyBankViewHeight)
                    ResizeHostWindowHeight(_piggyBankViewHeight);
                StartPiggyClock();
                _ = RefreshPiggyBankAsync();
            });
    }

    private void SwitchFromPiggyBankToPrimaryView()
    {
        if (!_isPiggyBankView || _isAnimating) return;
        StopPiggyClock();
        _isPiggyBankView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _piggyBankViewHeight;
        AnimateAudioViewSwap(
            PiggyBankContent,
            ExpandedContent,
            fromWidth,
            fromHeight,
            _expandedWidth,
            _expandedHeight,
            prepIncoming: () =>
            {
                ExpandedContent.Effect = null;
                ExpandedContent.Width = _expandedWidth - 24;
                ExpandedContent.Height = _expandedHeight - 18;
            },
            onComplete: () =>
            {
                RestoreExpandedWindowSize();
                ResumeSpotifyCanvasLifecycle();
                ShowMediaBackground();
                UpdateProgressSectionLayout();
                RefreshMediaMarquee();
            });
    }

    private void SwitchFromPiggyBankToSecondaryView()
    {
        if (!_isPiggyBankView || _isAnimating) return;
        StopPiggyClock();
        _isPiggyBankView = false;
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        UpdateNavIconsActiveState();
        UpdateShelfCapacityIndicator();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _piggyBankViewHeight;
        AnimateAudioViewSwap(
            PiggyBankContent,
            SecondaryContent,
            fromWidth,
            fromHeight,
            _expandedWidth,
            _expandedHeight,
            prepIncoming: () =>
            {
                EnableKeyboardInput();
                SecondaryContent.Width = _expandedWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            },
            onComplete: () =>
            {
                SecondaryContent.Width = double.NaN;
                SecondaryContent.UpdateLayout();
                RestoreExpandedWindowSize();
                ResetCameraSectionLayoutInstant();
            });
    }

    private void SwitchFromPiggyBankToTimerView()
    {
        if (!_isPiggyBankView || _isAnimating) return;
        StopPiggyClock();
        _isPiggyBankView = false;
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        UpdateTimerNavIconsState();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _piggyBankViewHeight;
        AnimateAudioViewSwap(
            PiggyBankContent,
            TimerContent,
            fromWidth,
            fromHeight,
            _clockViewWidth,
            _clockViewHeight,
            prepIncoming: () =>
            {
                ApplyClockViewWindowSize();
                PrepareClockViewContentSize();
                RefreshClockView();
                RestoreTimerContentOpacity();
            },
            onComplete: UpdateTimerDisplay);
    }

    private void SwitchFromPiggyBankToAudioView()
    {
        if (!_isPiggyBankView || _isAnimating) return;
        StopPiggyClock();
        _isPiggyBankView = false;
        _isAudioView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        UpdateNavIconsActiveState();

        if (_lastAudioSnapshot != null)
        {
            SetAudioLoadingState(false);
            EnsureAudioUIBuilt(_lastAudioSnapshot);
        }
        else
        {
            SetAudioLoadingState(true);
            _audioViewHeight = _audioViewMaxHeight;
        }

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _piggyBankViewHeight;
        var openWindowHeight = Math.Max(fromHeight, _audioViewHeight);
        ResizeHostWindowHeight(openWindowHeight);
        AnimateAudioViewSwap(
            PiggyBankContent,
            AudioContent,
            fromWidth,
            fromHeight,
            _audioViewWidth,
            _audioViewHeight,
            prepIncoming: null,
            onComplete: () =>
            {
                if (openWindowHeight > _audioViewHeight) ResizeHostWindowHeight(_audioViewHeight);
                if (!ApplyPendingAudioSnapshot()) SettleAudioNotchToFit();
                StartAudioPoll();
            });
        RefreshAudioData(SettleAudioNotchToFit);
    }

    private void SwitchFromPiggyBankToDisplayView()
    {
        if (!_isPiggyBankView || _isAnimating) return;
        EnsureDisplayViewBuilt();
        EnsureDisplayPresetBar();
        RebuildDisplayMonitorSections();
        RecalculateDisplayFitHeight(animate: false);

        StopPiggyClock();
        _isPiggyBankView = false;
        _isDisplayView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        UpdateNavIconsActiveState();
        PrepareDisplayContentLayout();
        _ = _displayViewModel.RefreshAsync();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _piggyBankViewHeight;
        var openWindowHeight = Math.Max(fromHeight, _displayViewHeight);
        ResizeHostWindowHeight(openWindowHeight);
        AnimateAudioViewSwap(
            PiggyBankContent,
            DisplayContent,
            fromWidth,
            fromHeight,
            _displayViewWidth,
            _displayViewHeight,
            prepIncoming: PrepareDisplayContentLayout,
            onComplete: () =>
            {
                if (openWindowHeight > _displayViewHeight) ResizeHostWindowHeight(_displayViewHeight);
                SettleDisplayNotchToFit();
            });
    }

    private void EnsurePiggyBankViewBuilt()
    {
        if (_piggyBankBuilt) return;
        _piggyBankBuilt = true;
        _piggyPanelIconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/piggy-panel-pink.png", UriKind.Absolute));
        PiggyBankContent.Children.Clear();
        var root = new Grid { Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        PiggyBankContent.Children.Add(root);

        root.Children.Add(BuildPiggyHeader());

        var quotaGrid = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(quotaGrid, 1);
        root.Children.Add(quotaGrid);

        var five = BuildQuotaSection("5H QUOTA", showWeeklyDayBlocks: false, out _piggyFiveVisual, out _piggyFivePercentText,
            out _piggyFiveResetText, out _);
        Grid.SetColumn(five, 0);
        quotaGrid.Children.Add(five);

        var week = BuildQuotaSection("WEEKLY QUOTA", showWeeklyDayBlocks: true, out _piggyWeekVisual, out _piggyWeekPercentText,
            out _piggyWeekRemainingText, out _piggyWeekResetText);
        _piggyWeekRemainingText.FontSize = 14;
        _piggyWeekResetText.FontSize = 12.5;
        Grid.SetColumn(week, 2);
        quotaGrid.Children.Add(week);

        var banked = BuildBankedResetSection();
        Grid.SetRow(banked, 2);
        root.Children.Add(banked);

        var preview = BuildPiggyPreviewSection();
        Grid.SetRow(preview, 3);
        root.Children.Add(preview);

        SetPiggyEmptyState();
    }

    private FrameworkElement BuildPiggyHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(CreatePiggyPanelImage(26));
        title.Children.Add(new TextBlock
        {
            Text = "PIGGY BANK",
            Foreground = Brushes.White,
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        });
        grid.Children.Add(title);

        _piggyStatusText = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(_piggyStatusText, 1);
        grid.Children.Add(_piggyStatusText);

        var refresh = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "\uE72C",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        refresh.MouseLeftButtonDown += PiggyRefresh_MouseLeftButtonDown;
        refresh.MouseEnter += (_, _) => refresh.Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
        refresh.MouseLeave += (_, _) => refresh.Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        Grid.SetColumn(refresh, 2);
        grid.Children.Add(refresh);
        return grid;
    }

    private Grid BuildQuotaSection(
        string label,
        bool showWeeklyDayBlocks,
        out PiggyQuotaVisual visual,
        out TextBlock percentText,
        out TextBlock secondaryText,
        out TextBlock tertiaryText)
    {
        var section = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var capsule = BuildLiquidCapsule(out visual);
        capsule.HorizontalAlignment = HorizontalAlignment.Center;
        capsule.VerticalAlignment = VerticalAlignment.Center;
        section.Children.Add(capsule);

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(text, 1);
        section.Children.Add(text);

        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        percentText = new TextBlock
        {
            Text = "—",
            Foreground = Brushes.White,
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 39,
            FontWeight = FontWeights.Bold,
            LineHeight = 41,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular, NumeralStyle = FontNumeralStyle.Lining }
        };
        text.Children.Add(percentText);

        secondaryText = new TextBlock
        {
            Text = "Quota unavailable",
            Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        text.Children.Add(secondaryText);

        if (showWeeklyDayBlocks)
        {
            _piggyWeekDayBlocks = BuildWeeklyDayBlocks();
            text.Children.Add(_piggyWeekDayBlocks);
        }
        else
        {
            text.Children.Add(new Border
            {
                Height = 12,
                Margin = new Thickness(0, 5, 0, 0),
                Visibility = Visibility.Hidden
            });
        }

        tertiaryText = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, showWeeklyDayBlocks ? 4 : 3, 0, 0),
            MinHeight = 15,
            TextWrapping = TextWrapping.Wrap
        };
        text.Children.Add(tertiaryText);

        return section;
    }

    private static StackPanel BuildWeeklyDayBlocks()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0),
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (var i = 0; i < 7; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 6,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(70, 154, 163, 173)),
                Margin = new Thickness(0, 0, i == 6 ? 0 : 4, 0)
            });
        }

        return panel;
    }

    private void UpdateWeeklyDayBlocks(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (_piggyWeekDayBlocks is null) return;
        var activeCount = PiggyBankFormatting.WeeklyRemainingDays(resetAt, now);
        var active = Color.FromRgb(112, 214, 139);
        var inactive = Color.FromArgb(70, 154, 163, 173);

        for (var i = 0; i < _piggyWeekDayBlocks.Children.Count; i++)
        {
            if (_piggyWeekDayBlocks.Children[i] is Border block)
                block.Background = new SolidColorBrush(i < activeCount ? active : inactive);
        }
    }

    private Border BuildLiquidCapsule(out PiggyQuotaVisual visual)
    {
        var shell = new Border
        {
            Width = 60,
            Height = 170,
            CornerRadius = new CornerRadius(30),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(145, 150, 158, 168)),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(48, 255, 255, 255), 0),
                    new(Color.FromArgb(8, 255, 255, 255), 0.22),
                    new(Color.FromArgb(4, 255, 255, 255), 0.65),
                    new(Color.FromArgb(28, 255, 255, 255), 1)
                },
                new Point(0, 0.5),
                new Point(1, 0.5)),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                RenderingBias = RenderingBias.Performance
            },
            ClipToBounds = true
        };

        var innerClip = new RectangleGeometry { RadiusX = 24, RadiusY = 24 };
        var shellGrid = new Grid
        {
            Margin = new Thickness(5),
            Clip = innerClip
        };
        shellGrid.SizeChanged += (_, _) =>
        {
            var width = Math.Max(0, shellGrid.ActualWidth);
            var height = Math.Max(0, shellGrid.ActualHeight);
            innerClip.Rect = new Rect(0, 0, width, height);
            innerClip.RadiusX = Math.Min(24, width / 2d);
            innerClip.RadiusY = Math.Min(24, height / 2d);
        };
        shell.Child = shellGrid;

        var liquidBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        var lightStop = new GradientStop(Color.FromRgb(88, 224, 122), 0.05);
        var coreStop = new GradientStop(Color.FromRgb(48, 209, 88), 0.34);
        var bodyStop = new GradientStop(Color.FromRgb(48, 209, 88), 0.68);
        var darkStop = new GradientStop(Color.FromRgb(23, 132, 54), 1);
        liquidBrush.GradientStops.Add(lightStop);
        liquidBrush.GradientStops.Add(coreStop);
        liquidBrush.GradientStops.Add(bodyStop);
        liquidBrush.GradientStops.Add(darkStop);

        var glow = new DropShadowEffect
        {
            Color = Color.FromRgb(48, 209, 88),
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.44,
            RenderingBias = RenderingBias.Performance
        };
        var fill = new Border
        {
            Height = 0,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(7, 7, 25, 25),
            Background = liquidBrush,
            Effect = glow,
            ClipToBounds = true
        };
        shellGrid.Children.Add(fill);

        var liquidHighlight = new Border
        {
            Height = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(82, 255, 255, 255), 0),
                    new(Color.FromArgb(20, 255, 255, 255), 0.45),
                    new(Colors.Transparent, 1)
                },
                new Point(0.5, 0),
                new Point(0.5, 1))
        };
        fill.Child = liquidHighlight;

        var glassHighlight = new Border
        {
            Width = 7,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(9, 10, 0, 17),
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(100, 255, 255, 255), 0),
                    new(Color.FromArgb(22, 255, 255, 255), 0.56),
                    new(Colors.Transparent, 1)
                },
                new Point(0.5, 0),
                new Point(0.5, 1)),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(glassHighlight, 5);
        shellGrid.Children.Add(glassHighlight);

        var topReflection = new Path
        {
            Data = Geometry.Parse("M 10,18 C 17,7 34,5 44,15"),
            Stroke = new SolidColorBrush(Color.FromArgb(78, 255, 255, 255)),
            StrokeThickness = 1.2,
            Stretch = Stretch.None,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(topReflection, 5);
        shellGrid.Children.Add(topReflection);

        visual = new PiggyQuotaVisual(fill, lightStop, coreStop, bodyStop, darkStop, glow);
        return shell;
    }

    private FrameworkElement BuildBankedResetSection()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) });

        var content = new Grid { Margin = new Thickness(8, 9, 8, 8) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        content.Children.Add(new TextBlock
        {
            Text = "BANKED RESETS",
            Foreground = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _piggyBankedResetPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_piggyBankedResetPanel, 1);
        content.Children.Add(_piggyBankedResetPanel);
        return grid;
    }

    private FrameworkElement BuildPiggyPreviewSection()
    {
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
        outer.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(17, 255, 255, 255)) });

        var row = new Grid { Margin = new Thickness(8, 7, 8, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(row, 1);
        outer.Children.Add(row);

        row.Children.Add(new TextBlock
        {
            Text = "PREVIEW",
            Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var five = BuildPreviewControl("5H", out _piggyFivePreviewSlider, out _piggyFivePreviewValue);
        Grid.SetColumn(five, 1);
        row.Children.Add(five);

        var week = BuildPreviewControl("WEEK", out _piggyWeekPreviewSlider, out _piggyWeekPreviewValue);
        Grid.SetColumn(week, 3);
        row.Children.Add(week);

        _piggyFivePreviewSlider.ValueChanged += PiggyPreviewSlider_ValueChanged;
        _piggyWeekPreviewSlider.ValueChanged += PiggyPreviewSlider_ValueChanged;
        return outer;
    }

    private FrameworkElement BuildPreviewControl(string label, out Slider slider, out TextBlock valueText)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Style = (Style)FindResource("PiggyPreviewSlider"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 7, 0)
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        valueText = new TextBlock
        {
            Text = "50%",
            Foreground = new SolidColorBrush(Color.FromArgb(195, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);
        return grid;
    }

    private Image CreatePiggyPanelImage(double size)
        => new()
        {
            Source = _piggyPanelIconSource,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };

    private void PiggyRefresh_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isAnimating) _ = RefreshPiggyBankAsync();
    }

    private async Task RefreshPiggyBankAsync()
    {
        _piggyRefreshCancellation?.Cancel();
        _piggyRefreshCancellation?.Dispose();
        _piggyRefreshCancellation = new CancellationTokenSource();
        var token = _piggyRefreshCancellation.Token;

        SetPiggyStatus("Refreshing…");
        try
        {
            var snapshot = await PiggyBankQuotaService.ReadAsync(token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            _piggyBankSnapshot = snapshot;
            ApplyPiggySnapshot(snapshot, animateLiquids: true);
            SetPiggyStatus("Updated just now");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer refresh superseded this one.
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("PIGGY", $"Quota refresh unavailable: {ex.GetType().Name}: {ex.Message}");
            if (_piggyBankSnapshot is null) SetPiggyEmptyState();
            SetPiggyStatus(_piggyBankSnapshot is null ? "Codex quota unavailable" : "Refresh failed · showing previous data");
        }
    }

    private void ApplyPiggySnapshot(PiggyBankSnapshot snapshot, bool animateLiquids)
    {
        var now = DateTimeOffset.UtcNow;
        var five = snapshot.FiveHour;
        var week = snapshot.Weekly;

        if (five is not null)
        {
            _piggyFivePercentText!.Text = $"{five.RemainingPercent}%";
            _piggyFiveResetText!.Text = PiggyBankFormatting.FiveHourReset(five.ResetsAt, now);
            UpdateLiquidVisual(_piggyFiveVisual!, five.RemainingPercent, animateLiquids);
        }
        else
        {
            _piggyFivePercentText!.Text = "—";
            _piggyFiveResetText!.Text = "Quota unavailable";
            UpdateLiquidVisual(_piggyFiveVisual!, 0, animateLiquids);
        }

        if (week is not null)
        {
            _piggyWeekPercentText!.Text = $"{week.RemainingPercent}%";
            _piggyWeekRemainingText!.Text = PiggyBankFormatting.WeeklyRemaining(week.ResetsAt, now);
            _piggyWeekResetText!.Text = PiggyBankFormatting.WeeklyReset(week.ResetsAt);
            UpdateWeeklyDayBlocks(week.ResetsAt, now);
            UpdateLiquidVisual(_piggyWeekVisual!, week.RemainingPercent, animateLiquids);
        }
        else
        {
            _piggyWeekPercentText!.Text = "—";
            _piggyWeekRemainingText!.Text = "Quota unavailable";
            _piggyWeekResetText!.Text = "";
            UpdateWeeklyDayBlocks(null, now);
            UpdateLiquidVisual(_piggyWeekVisual!, 0, animateLiquids);
        }

        SetPreviewValuesFromSnapshot(snapshot);
        RebuildBankedResets(snapshot);
        UpdatePiggyTargetHeight(snapshot.BankedResetCount);
    }

    private void SetPiggyEmptyState()
    {
        if (!_piggyBankBuilt) return;
        _piggyFivePercentText!.Text = "—";
        _piggyFiveResetText!.Text = "Quota unavailable";
        _piggyWeekPercentText!.Text = "—";
        _piggyWeekRemainingText!.Text = "Quota unavailable";
        _piggyWeekResetText!.Text = "";
        UpdateWeeklyDayBlocks(null, DateTimeOffset.UtcNow);
        UpdateLiquidVisual(_piggyFiveVisual!, 0, animate: false);
        UpdateLiquidVisual(_piggyWeekVisual!, 0, animate: false);
        _piggyBankedResetPanel!.Children.Clear();
        _piggyBankedResetPanel.Children.Add(CreateBankedResetEmptyText("Banked resets unavailable"));
    }

    private void SetPreviewValuesFromSnapshot(PiggyBankSnapshot snapshot)
    {
        if (_piggyFivePreviewSlider is null || _piggyWeekPreviewSlider is null) return;
        _piggySettingPreviewValues = true;
        try
        {
            var five = snapshot.FiveHour?.RemainingPercent ?? 0;
            var week = snapshot.Weekly?.RemainingPercent ?? 0;
            _piggyFivePreviewSlider.Value = five;
            _piggyWeekPreviewSlider.Value = week;
            _piggyFivePreviewValue!.Text = $"{five}%";
            _piggyWeekPreviewValue!.Text = $"{week}%";
        }
        finally
        {
            _piggySettingPreviewValues = false;
        }
    }

    private void PiggyPreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_piggySettingPreviewValues || !_piggyBankBuilt ||
            _piggyFivePreviewSlider is null || _piggyWeekPreviewSlider is null) return;

        var five = PiggyBankFormatting.ClampRemaining(_piggyFivePreviewSlider.Value);
        var week = PiggyBankFormatting.ClampRemaining(_piggyWeekPreviewSlider.Value);
        _piggyFivePreviewValue!.Text = $"{five}%";
        _piggyWeekPreviewValue!.Text = $"{week}%";
        _piggyFivePercentText!.Text = $"{five}%";
        _piggyWeekPercentText!.Text = $"{week}%";
        UpdateLiquidVisual(_piggyFiveVisual!, five, animate: true);
        UpdateLiquidVisual(_piggyWeekVisual!, week, animate: true);
        SetPiggyStatus("Preview only · refresh restores live data");
    }

    private void RebuildBankedResets(PiggyBankSnapshot snapshot)
    {
        if (_piggyBankedResetPanel is null) return;
        _piggyBankedResetPanel.Children.Clear();

        if (snapshot.BankedResetCount == 0)
        {
            _piggyBankedResetPanel.Children.Add(CreateBankedResetEmptyText("No banked resets"));
            return;
        }

        foreach (var reset in snapshot.BankedResets)
            _piggyBankedResetPanel.Children.Add(CreateBankedResetChip(reset.ExpiresAt));

        for (var i = 0; i < snapshot.MissingResetDetailCount; i++)
            _piggyBankedResetPanel.Children.Add(CreateBankedResetChip(null));
    }

    private TextBlock CreateBankedResetEmptyText(string text)
        => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };

    private Border CreateBankedResetChip(DateTimeOffset? expiresAt)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(CreatePiggyPanelImage(28));

        var text = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        text.Children.Add(new TextBlock
        {
            Text = "EXPIRES",
            Foreground = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold
        });
        text.Children.Add(new TextBlock
        {
            Text = PiggyBankFormatting.ResetExpiryDate(expiresAt),
            Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            FontFamily = (FontFamily)FindResource("MainSystemFont"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold
        });
        var timeText = PiggyBankFormatting.ResetExpiryTime(expiresAt);
        if (!string.IsNullOrEmpty(timeText))
        {
            text.Children.Add(new TextBlock
            {
                Text = timeText,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                FontFamily = (FontFamily)FindResource("MainSystemFont"),
                FontSize = 10.5,
                FontWeight = FontWeights.Medium
            });
        }
        content.Children.Add(text);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 10, 6),
            Margin = new Thickness(0, 0, 7, 4),
            Child = content
        };
    }

    private void RefreshPiggyClockText()
    {
        if (_piggyBankSnapshot is not { } snapshot) return;
        var now = DateTimeOffset.UtcNow;
        if (snapshot.FiveHour is { } five)
            _piggyFiveResetText!.Text = PiggyBankFormatting.FiveHourReset(five.ResetsAt, now);
        if (snapshot.Weekly is { } week)
        {
            _piggyWeekRemainingText!.Text = PiggyBankFormatting.WeeklyRemaining(week.ResetsAt, now);
            _piggyWeekResetText!.Text = PiggyBankFormatting.WeeklyReset(week.ResetsAt);
            UpdateWeeklyDayBlocks(week.ResetsAt, now);
        }
    }

    private void StartPiggyClock()
    {
        _piggyClockTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _piggyClockTimer.Tick -= PiggyClockTimer_Tick;
        _piggyClockTimer.Tick += PiggyClockTimer_Tick;
        _piggyClockTimer.Start();
    }

    private void StopPiggyClock() => _piggyClockTimer?.Stop();

    private void PiggyClockTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPiggyBankView) return;
        RefreshPiggyClockText();
    }

    private void UpdatePiggyTargetHeight(int bankedResetCount)
    {
        var rows = Math.Max(1, (int)Math.Ceiling(bankedResetCount / 2d));
        var target = PiggyBankBaseHeight + Math.Min(3, rows - 1) * 30;
        if (Math.Abs(target - _piggyBankViewHeight) < 0.5) return;
        _piggyBankViewHeight = target;
        PreparePiggyBankContentLayout();

        if (_isPiggyBankView && !_isAnimating)
        {
            var currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : PiggyBankViewWidth;
            var currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
            ResizeHostWindowHeight(Math.Max(currentHeight, target));
            AnimateClockViewNotchResize(
                currentWidth,
                currentHeight,
                PiggyBankViewWidth,
                target,
                new Duration(TimeSpan.FromMilliseconds(260)),
                TimeSpan.Zero,
                () => ResizeHostWindowHeight(target));
        }
    }

    private void PreparePiggyBankContentLayout()
    {
        PiggyBankContent.Width = Math.Max(0, PiggyBankViewWidth - PiggyBankContent.Margin.Left - PiggyBankContent.Margin.Right);
        PiggyBankContent.Height = Math.Max(0, _piggyBankViewHeight - PiggyBankContent.Margin.Top - PiggyBankContent.Margin.Bottom);
        PiggyBankContent.UpdateLayout();
    }

    private void SetPiggyStatus(string text)
    {
        if (_piggyStatusText is not null) _piggyStatusText.Text = text;
    }

    private static void UpdateLiquidVisual(PiggyQuotaVisual visual, int remainingPercent, bool animate)
    {
        var percent = Math.Clamp(remainingPercent, 0, 100);
        var targetHeight = PiggyLiquidMaxHeight * percent / 100d;
        var colour = PiggyBankFormatting.QuotaColour(percent);
        var light = Blend(colour, Colors.White, 0.26);
        var dark = Blend(colour, Colors.Black, 0.30);

        var generation = ++visual.AnimationGeneration;
        visual.Fill.BeginAnimation(FrameworkElement.HeightProperty, null);
        var currentHeight = double.IsNaN(visual.Fill.Height) ? visual.Fill.ActualHeight : visual.Fill.Height;
        currentHeight = Math.Clamp(currentHeight, 0, PiggyLiquidMaxHeight);
        visual.Fill.Height = currentHeight;

        if (!animate || Math.Abs(currentHeight - targetHeight) < 0.25)
        {
            visual.Fill.Height = targetHeight;
            SetLiquidColours(visual, light, colour, dark);
            return;
        }

        var heightAnimation = new DoubleAnimation(
            currentHeight,
            targetHeight,
            new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(heightAnimation, AnimationConfig.TargetFps);
        heightAnimation.Completed += (_, _) =>
        {
            if (generation != visual.AnimationGeneration) return;
            visual.Fill.BeginAnimation(FrameworkElement.HeightProperty, null);
            visual.Fill.Height = targetHeight;
        };
        visual.Fill.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);

        AnimateColour(visual.LightStop, light);
        AnimateColour(visual.CoreStop, colour);
        AnimateColour(visual.BodyStop, colour);
        AnimateColour(visual.DarkStop, dark);
        var glowAnimation = new ColorAnimation(colour, new Duration(TimeSpan.FromMilliseconds(220)));
        Timeline.SetDesiredFrameRate(glowAnimation, AnimationConfig.TargetFps);
        visual.Glow.BeginAnimation(DropShadowEffect.ColorProperty, glowAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetLiquidColours(PiggyQuotaVisual visual, Color light, Color colour, Color dark)
    {
        visual.LightStop.BeginAnimation(GradientStop.ColorProperty, null);
        visual.CoreStop.BeginAnimation(GradientStop.ColorProperty, null);
        visual.BodyStop.BeginAnimation(GradientStop.ColorProperty, null);
        visual.DarkStop.BeginAnimation(GradientStop.ColorProperty, null);
        visual.Glow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        visual.LightStop.Color = light;
        visual.CoreStop.Color = colour;
        visual.BodyStop.Color = colour;
        visual.DarkStop.Color = dark;
        visual.Glow.Color = colour;
    }

    private static void AnimateColour(GradientStop stop, Color target)
    {
        var animation = new ColorAnimation(target, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private sealed class PiggyQuotaVisual(
        Border fill,
        GradientStop lightStop,
        GradientStop coreStop,
        GradientStop bodyStop,
        GradientStop darkStop,
        DropShadowEffect glow)
    {
        public Border Fill { get; } = fill;
        public GradientStop LightStop { get; } = lightStop;
        public GradientStop CoreStop { get; } = coreStop;
        public GradientStop BodyStop { get; } = bodyStop;
        public GradientStop DarkStop { get; } = darkStop;
        public DropShadowEffect Glow { get; } = glow;
        public int AnimationGeneration { get; set; }
    }
}
