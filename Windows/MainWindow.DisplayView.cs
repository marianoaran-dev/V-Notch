using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch;

public partial class MainWindow
{
    private const double _displayViewWidth = 660;
    private const double _displayViewMinHeight = 220;
    private double _displayViewHeight = 280;

    private bool _isDisplayView
    {
        get => _notchState.IsDisplayView;
        set
        {
            _notchState.IsDisplayView = value;
            if (value)
            {
                _viewModel.SetView(Models.NotchView.DisplayMonitors);
            }
            else if (_viewModel.CurrentView == Models.NotchView.DisplayMonitors)
            {
                _viewModel.SetView(Models.NotchView.Media);
            }
        }
    }

    private void DisplayIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isPiggyBankView && !_isAnimating)
        {
            SwitchFromPiggyBankToDisplayView();
            return;
        }
        if (_isDisplayView || _isAnimating) return;
        SwitchToDisplayView();
    }

    private void SwitchToDisplayView()
    {
        if (_isDisplayView || _isAnimating) return;

        EnsureDisplayViewBuilt();
        EnsureDisplayPresetBar();
        RebuildDisplayMonitorSections();
        RecalculateDisplayFitHeight(animate: false);

        CancelTimerEditingInstant();
        var fromPrimary = !_isSecondaryView && !_isTimerView && !_isAudioView;
        var fromSecondary = _isSecondaryView;
        FrameworkElement outgoing = _isAudioView
            ? AudioContent
            : _isTimerView
                ? TimerContent
                : _isSecondaryView
                    ? SecondaryContent
                    : ExpandedContent;

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        if (_isAudioView)
        {
            StopAudioPoll();
            _audioMixerServiceCached?.ReleaseSessionCache();
        }
        if (fromSecondary)
        {
            StopCameraPreviewForViewExit();
            DisableKeyboardInput();
        }

        _isDisplayView = true;
        _isAudioView = false;
        _isTimerView = false;
        _isSecondaryView = false;
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
        PrepareDisplayContentLayout();
        _ = _displayViewModel.RefreshAsync();

        var openWindowHeight = Math.Max(fromHeight, _displayViewHeight);
        ResizeHostWindowHeight(openWindowHeight);
        AnimateAudioViewSwap(
            outgoing,
            DisplayContent,
            fromWidth,
            fromHeight,
            _displayViewWidth,
            _displayViewHeight,
            prepIncoming: PrepareDisplayContentLayout,
            onComplete: () =>
            {
                if (openWindowHeight > _displayViewHeight)
                    ResizeHostWindowHeight(_displayViewHeight);
                SettleDisplayNotchToFit();
            });
    }

    private void SwitchFromDisplayToPrimaryView()
    {
        if (!_isDisplayView || _isAnimating) return;
        CommitDisplayWrites();
        _isDisplayView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        // Match the established Audio/Timer -> Home behaviour: keep the nav icons
        // visible while removing only the utility background treatment.
        UpdateNavIconsActiveState();
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _displayViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _displayViewHeight;
        AnimateAudioViewSwap(
            DisplayContent,
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

    private void SwitchFromDisplayToSecondaryView()
    {
        if (!_isDisplayView || _isAnimating) return;
        CommitDisplayWrites();
        _isDisplayView = false;
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        EnableKeyboardInput();
        UpdateShelfCapacityIndicator();
        UpdateNavIconsActiveState();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _displayViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _displayViewHeight;
        AnimateAudioViewSwap(
            DisplayContent,
            SecondaryContent,
            fromWidth,
            fromHeight,
            _expandedWidth,
            _expandedHeight,
            prepIncoming: () =>
            {
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
                EnableKeyboardInput();
            },
            onComplete: () =>
            {
                SecondaryContent.Width = double.NaN;
                SecondaryContent.UpdateLayout();
                RestoreExpandedWindowSize();
                ResetCameraSectionLayoutInstant();
            });
    }

    private void SwitchFromDisplayToTimerView()
    {
        if (!_isDisplayView || _isAnimating) return;
        CommitDisplayWrites();
        _isDisplayView = false;
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        UpdateNavIconsActiveState();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _displayViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _displayViewHeight;
        AnimateAudioViewSwap(
            DisplayContent,
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

    private void SwitchFromDisplayToAudioView()
    {
        if (!_isDisplayView || _isAnimating) return;
        CommitDisplayWrites();
        _isDisplayView = false;
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

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _displayViewWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _displayViewHeight;
        var openWindowHeight = Math.Max(fromHeight, _audioViewHeight);
        ResizeHostWindowHeight(openWindowHeight);
        AnimateAudioViewSwap(
            DisplayContent,
            AudioContent,
            fromWidth,
            fromHeight,
            _audioViewWidth,
            _audioViewHeight,
            prepIncoming: null,
            onComplete: () =>
            {
                if (openWindowHeight > _audioViewHeight)
                    ResizeHostWindowHeight(_audioViewHeight);
                if (!ApplyPendingAudioSnapshot())
                    SettleAudioNotchToFit();
                StartAudioPoll();
            });

        RefreshAudioData(SettleAudioNotchToFit);
    }

    private void PrepareDisplayContentLayout()
    {
        DisplayContent.Width = Math.Max(0, _displayViewWidth - DisplayContent.Margin.Left - DisplayContent.Margin.Right);
        DisplayContent.Height = Math.Max(0, _displayViewHeight - DisplayContent.Margin.Top - DisplayContent.Margin.Bottom);
        DisplayContent.UpdateLayout();
    }

    private void ShowUtilityNavigation()
    {
        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Visibility = Visibility.Visible;
        NavIconsBackground.Opacity = 1;
    }

    private void HideUtilityNavigation()
    {
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
    }

    private void HideLyricsBlurForUtility()
    {
        if (LyricsBlurBackground == null || LyricsBlurBackground.Visibility != Visibility.Visible) return;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    // Legacy XAML handlers remain because the original declarative Display tree is
    // still compiled for compatibility; EnsureDisplayViewBuilt replaces that tree
    // with the product-native dynamic view at runtime.
    private void DisplayRefresh_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_isAnimating) _ = _displayViewModel.RefreshAsync();
    }

    private void DisplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.DataContext is not DisplayMonitorRowViewModel row)
            return;

        var control = string.Equals(slider.Tag as string, "Contrast", StringComparison.Ordinal)
            ? MonitorControlKind.Contrast
            : MonitorControlKind.Brightness;
        _displayViewModel.ApplyUserChange(row, control, e.OldValue, e.NewValue);
    }

    private void DisplaySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => CommitDisplayWrites();

    private void DisplaySlider_LostMouseCapture(object sender, MouseEventArgs e)
        => CommitDisplayWrites();

    private void CommitDisplayWrites()
    {
        _ = CommitDisplayWritesAsync();
    }

    private async Task CommitDisplayWritesAsync()
    {
        try
        {
            await _displayViewModel.CommitPendingWritesAsync().ConfigureAwait(false);
        }
        catch
        {
            // The view model keeps unsupported/failed controls local to their row.
        }
    }
}
