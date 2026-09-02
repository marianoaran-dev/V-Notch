using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isAudioView || _isDisplayView) return;

        if (!_isExpanded && !_isAnimating)
        {
            if (_settings.EnableHoverExpand) return;

            if (_isGestureActive)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            if (TryGetCompactVolumeWheelDelta(e.Delta, out int volumeDelta))
            {
                AdjustVolumeByScroll(volumeDelta);
            }
            return;
        }

        if (!_isExpanded || _isAnimating) return;
        if (e.Handled) return;

        e.Handled = true;

        ResetScrollSessionTimer();
        if (_isScrollSessionLocked) return;

        if ((DateTime.UtcNow - _lastViewSwitchUtc) < ViewSwitchCooldown) return;

        if (e.Delta < 0)
        {
            if (!_isSecondaryView && !_isTimerView)
            {
                SwitchToSecondaryView();
            }
            else if (_isSecondaryView && !_isTimerView)
            {
                StopCameraPreviewForViewExit();
                SwitchFromSecondaryToTimerView();
            }
        }
        else if (e.Delta > 0)
        {
            if (_isTimerView)
            {
                SwitchFromTimerToSecondaryView();
            }
            else if (_isSecondaryView)
            {
                StopCameraPreviewForViewExit();
                SwitchToPrimaryView();
            }
        }
    }

    private void ResetScrollSessionTimer()
    {
        if (_scrollSessionResetTimer == null)
        {
            _scrollSessionResetTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _scrollSessionResetTimer.Tick += (s, e) =>
            {
                _scrollSessionResetTimer.Stop();
                _isScrollSessionLocked = false;
            };
        }
        _scrollSessionResetTimer.Stop();
        _scrollSessionResetTimer.Start();
    }

    private void SecondaryContent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void NavIconsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void HomeIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isDisplayView && !_isAnimating)
        {
            SwitchFromDisplayToPrimaryView();
        }
        else if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToPrimaryView();
        }
        else if (_isTimerView && !_isAnimating)
        {
            SwitchFromTimerToPrimaryView();
        }
        else if (_isSecondaryView && !_isAnimating)
        {
            StopCameraPreviewForViewExit();
            SwitchToPrimaryView();
        }
    }

    private void FileShelfIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isDisplayView && !_isAnimating)
        {
            SwitchFromDisplayToSecondaryView();
        }
        else if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToSecondaryView();
        }
        else if (_isTimerView && !_isAnimating)
        {
            SwitchFromTimerToSecondaryView();
        }
        else if (!_isSecondaryView && !_isAnimating)
        {
            SwitchToSecondaryView();
        }
    }

    private void SwitchToSecondaryView()
    {
        if (_isSecondaryView || _isAnimating) return;
        _isSecondaryView = true;
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        HideMediaBackground();
        if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }

        UpdateShelfCapacityIndicator();
        ShowUtilityNavigation();

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        AnimateAudioViewSwap(
            ExpandedContent,
            SecondaryContent,
            fromWidth,
            fromHeight,
            _expandedWidth,
            _expandedHeight,
            prepIncoming: () =>
            {
                EnableKeyboardInput();
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
                SecondaryContent.UpdateLayout();
            },
            onComplete: () =>
            {
                SecondaryContent.Width = double.NaN;
                SecondaryContent.UpdateLayout();

                if (_pendingFlipThumbnail != null)
                {
                    var thumb = _pendingFlipThumbnail;
                    _pendingFlipThumbnail = null;
                    AnimateThumbnailSwitchOnly(thumb, force: true);
                }

                if (IsCameraPreviewLifecycleActive)
                    StopCameraPreviewForViewExit();
                ResetCameraSectionLayoutInstant();
            });
    }

    private void SwitchToPrimaryView()
    {
        if (!_isSecondaryView || _isAnimating) return;
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (IsCameraPreviewLifecycleActive)
            StopCameraPreviewForViewExit();
        else
            ResetCameraSectionLayoutInstant();

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        var fromWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        var fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        AnimateAudioViewSwap(
            SecondaryContent,
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
                DisableKeyboardInput();
                RestoreExpandedWindowSize();
                ResumeSpotifyCanvasLifecycle();
                ShowMediaBackground();
                UpdateProgressSectionLayout();
                RefreshMediaMarquee();

                if (_settings.EnableBlurEffects && !IsLiquidGlassEnabled && _isLyricsActive &&
                    !_isSpotifyCanvasMediaOpen && LyricsBlurBackground != null)
                {
                    LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImage.Opacity = 1;
                    LyricsBlurBackground.Visibility = Visibility.Visible;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                    var lyricsBlurFadeIn = new DoubleAnimation(
                        0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                    {
                        EasingFunction = new ExponentialEase
                        {
                            Exponent = 4,
                            EasingMode = EasingMode.EaseOut
                        }
                    };
                    Timeline.SetDesiredFrameRate(lyricsBlurFadeIn, VNotch.Services.AnimationConfig.TargetFps);
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, lyricsBlurFadeIn);
                }

                if (_pendingFlipThumbnail != null)
                {
                    var thumb = _pendingFlipThumbnail;
                    _pendingFlipThumbnail = null;
                    AnimateThumbnailSwitchOnly(thumb, force: true);
                }
            });
    }

    private void UpdateNavIconsActiveState()
    {
        var showShelfCountBadge = false;
        var utilityMode = _isDisplayView || _isAudioView || _isTimerView || _isSecondaryView;
        ApplySharedStatusBarMode(utilityMode);

        if (_isDisplayView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 0.4;
            DisplayIconButton.Opacity = 1.0;
        }
        else if (_isAudioView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 1.0;
            DisplayIconButton.Opacity = 0.4;
        }
        else if (_isTimerView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 1.0;
            AudioIconButton.Opacity = 0.4;
            DisplayIconButton.Opacity = 0.4;
        }
        else if (_isSecondaryView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 1.0;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 0.4;
            DisplayIconButton.Opacity = 0.4;
            showShelfCountBadge = ShelfUnlockBanner.Visibility != Visibility.Visible;
        }
        else
        {
            HomeIconButton.Opacity = 1.0;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 0.4;
            DisplayIconButton.Opacity = 0.4;
        }

        if (!_isAnimating)
        {
            ShelfCountBadge.Visibility = showShelfCountBadge
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplySharedStatusBarMode(bool utilityMode)
    {
        if (utilityMode)
        {
            BatterySection.BeginAnimation(OpacityProperty, null);
            BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            BatterySection.Opacity = 0;
            BatterySection.Visibility = Visibility.Collapsed;
            BatterySection.IsHitTestVisible = false;

            StopUpdatePulseAnimation();
            UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
            UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            UpdateNotificationButton.Opacity = 0;
            UpdateNotificationButton.Visibility = Visibility.Collapsed;
            UpdateNotificationButton.IsHitTestVisible = false;
            return;
        }

        if (!_isExpanded)
            return;

        var showBattery = _settings.ShowBatteryIndicator;
        BatterySection.BeginAnimation(OpacityProperty, null);
        BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BatterySection.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
        BatterySection.IsHitTestVisible = showBattery;
        BatterySection.Opacity = showBattery ? 1 : 0;
        BatteryTranslate.Y = _settings.EnableDynamicIslandMode ? 5 : 0;

        if (_isUpdateAvailable)
            ShowUpdateNotification();
    }

}
