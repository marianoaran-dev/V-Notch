using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _isAudioView
    {
        get => _notchState.IsAudioView;
        set
        {
            _notchState.IsAudioView = value;
            if (value) _viewModel.SetView(VNotch.Models.NotchView.AudioMixer);
            else if (_viewModel.CurrentView == VNotch.Models.NotchView.AudioMixer)
                _viewModel.SetView(VNotch.Models.NotchView.Media);
        }
    }
    private const double _audioViewWidth = 720;

    private const double _audioViewMaxHeight = 378;
    private const double _audioViewMinHeight = 150;
    private const double _audioViewChrome = 66;
    private double _audioViewHeight = _audioViewMaxHeight;
    private int _audioNotchHeightGeneration;

    private AudioMixerService? _audioMixerServiceCached;
    private AudioMixerService AudioMixer =>
        _audioMixerServiceCached ??= (AudioMixerService)App.Services.GetService(typeof(AudioMixerService))!;

    private System.Windows.Threading.DispatcherTimer? _audioPollTimer;

    private void StartAudioPoll()
    {
        if (_audioPollTimer == null)
        {
            _audioPollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _audioPollTimer.Tick += (_, _) => PollAudioVolumes();
        }
        _audioPollTimer.Start();
    }

    private void StopAudioPoll() => _audioPollTimer?.Stop();

    private bool _audioTopFadeShown;
    private bool _audioBottomFadeShown;

    private void AudioScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateAudioScrollFades();

    private void UpdateAudioScrollFades()
    {
        if (AudioScrollViewer == null || AudioFadeTopStop == null || AudioFadeBottomStop == null) return;

        double offset = AudioScrollViewer.VerticalOffset;
        double scrollable = AudioScrollViewer.ScrollableHeight;

        bool showTop = offset > 1.0;
        bool showBottom = scrollable > 1.0 && offset < scrollable - 1.0;

        if (showTop != _audioTopFadeShown)
        {
            _audioTopFadeShown = showTop;
            AnimateAudioFadeStop(AudioFadeTopStop, showTop);
        }
        if (showBottom != _audioBottomFadeShown)
        {
            _audioBottomFadeShown = showBottom;
            AnimateAudioFadeStop(AudioFadeBottomStop, showBottom);
        }
    }

    private void AnimateAudioFadeStop(GradientStop stop, bool fade)
    {
        var to = fade ? Colors.Transparent : Colors.Black;
        var anim = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    private void AudioIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isDisplayView && !_isAnimating)
        {
            SwitchFromDisplayToAudioView();
            return;
        }
        if (_isAudioView || _isAnimating) return;
        SwitchToAudioView();
    }

    private void SwitchToAudioView()
    {
        if (_isAudioView || _isAnimating) return;
        CancelTimerEditingInstant();

        FrameworkElement outgoing;
        bool fromPrimary = !_isSecondaryView && !_isTimerView;
        if (_isTimerView) outgoing = TimerContent;
        else if (_isSecondaryView) outgoing = SecondaryContent;
        else outgoing = ExpandedContent;

        bool fromSecondary = _isSecondaryView;

        _isAudioView = true;
        _isSecondaryView = false;
        _isTimerView = false;
        _isAnimating = true;
        SuppressPrivacyDot();
        SuspendSpotifyCanvasLifecycle();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (fromSecondary)
        {
            StopCameraPreviewForViewExit();
        }
        if (fromPrimary)
        {
            HideMediaBackground();
            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                LyricsBlurBackground.Opacity = 0;
                LyricsBlurBackground.Visibility = Visibility.Collapsed;
            }
        }

        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, AnimationConfig.TargetFps);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        bool hadSnapshot = _lastAudioSnapshot != null;
        if (hadSnapshot)
        {
            SetAudioLoadingState(false);
            EnsureAudioUIBuilt(_lastAudioSnapshot!);
        }
        else
        {
            SetAudioLoadingState(true);
            _audioViewHeight = _audioViewMaxHeight;
        }

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        double openWindowHeight = Math.Max(fromH, _audioViewHeight);
        ResizeHostWindowHeight(openWindowHeight);

        AnimateAudioViewSwap(
            outgoing, AudioContent,
            fromW, fromH, _audioViewWidth, _audioViewHeight,
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

    private void SwitchFromAudioToPrimaryView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, ExpandedContent,
            fromW, fromH, _expandedWidth, _expandedHeight,
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
                if (_settings.EnableBlurEffects && _isLyricsActive && !_isSpotifyCanvasMediaOpen && LyricsBlurBackground != null)
                {
                    LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImage.Opacity = 1;
                    LyricsBlurBackground.Visibility = Visibility.Visible;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                    var lyricsBlurFadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                    {
                        EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(lyricsBlurFadeIn, VNotch.Services.AnimationConfig.TargetFps);
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, lyricsBlurFadeIn);
                }
            });
    }

    private void SwitchFromAudioToSecondaryView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        UpdateShelfCapacityIndicator();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, SecondaryContent,
            fromW, fromH, _expandedWidth, _expandedHeight,
            prepIncoming: () =>
            {
                EnableKeyboardInput();
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            },
            onComplete: () =>
            {
                SecondaryContent.Width = double.NaN;
                SecondaryContent.UpdateLayout();
                RestoreExpandedWindowSize();
                ResetCameraSectionLayoutInstant();
            });
    }

    private void SwitchFromAudioToTimerView()
    {
        if (!_isAudioView || _isAnimating) return;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateTimerNavIconsState();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, TimerContent,
            fromW, fromH, _clockViewWidth, _clockViewHeight,
            prepIncoming: () =>
            {
                ApplyClockViewWindowSize();
                PrepareClockViewContentSize();
                RefreshClockView();
                RestoreTimerContentOpacity();
            },
            onComplete: () => UpdateTimerDisplay());
    }

    private void AnimateAudioViewSwap(
        FrameworkElement outgoing, FrameworkElement incoming,
        double notchFromW, double notchFromH, double notchToW, double notchToH,
        Action? prepIncoming, Action? onComplete)
    {
        NotchBorder.IsHitTestVisible = false;

        var durOut = _dur200;
        var durIn = _dur600;
        var inDelay = TimeSpan.Zero;
        int fps = AnimationConfig.TargetFps;

        bool outIsAudio = ReferenceEquals(outgoing, AudioContent);
        bool inIsAudio = ReferenceEquals(incoming, AudioContent);
        bool resetIncomingAutoSize = !inIsAudio
            && !ReferenceEquals(incoming, ExpandedContent)
            && notchFromW > notchToW + 0.5;

        // Keep category roots geometrically stable during the view swap. The notch
        // itself still resizes, but whole-view scale/slide/alignment changes caused
        // visible shake, especially around Audio and Display.
        outgoing.BeginAnimation(OpacityProperty, null);
        outgoing.Opacity = 1;
        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        fadeOut.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.BeginAnimation(OpacityProperty, null);
            outgoing.Opacity = 1;
        };
        outgoing.BeginAnimation(OpacityProperty, fadeOut);

        prepIncoming?.Invoke();

        AnimateClockViewNotchResize(notchFromW, notchFromH, notchToW, notchToH, durIn, inDelay);

        // Do not bitmap-cache or transform transition roots. Several of these views
        // update while hidden, and keeping their layout coordinates fixed prevents
        // a one-frame horizontal/vertical jump as the notch geometry changes.
        incoming.Visibility = Visibility.Visible;
        incoming.BeginAnimation(OpacityProperty, null);
        incoming.Opacity = 0;

        if (ReferenceEquals(incoming, ExpandedContent))
            PrepareExpandedContentLayoutForReveal();
        else
            incoming.UpdateLayout();

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        fadeIn.Completed += (_, _) =>
        {
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            incoming.Opacity = 1;
            incoming.BeginAnimation(OpacityProperty, null);

            if (ReferenceEquals(incoming, ExpandedContent))
            {
                RestoreExpandedContentRestLayout();
            }
            else if (resetIncomingAutoSize)
            {
                // Some shrinking view preparations pin Width/Height temporarily.
                // Preserve the existing end-state behaviour without changing the
                // incoming panel's alignment during the animation.
                incoming.Width = double.NaN;
                incoming.Height = double.NaN;
                incoming.UpdateLayout();
            }

            if (outIsAudio)
                RestorePrivacyDotVisibility();

            onComplete?.Invoke();
        };
        incoming.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void SettleAudioNotchToFit()
        => AnimateAudioNotchHeight(_audioViewHeight, new Duration(TimeSpan.FromMilliseconds(300)), _easeExpOut6);

    private void AnimateAudioNotchHeight(double target, Duration dur, IEasingFunction ease)
    {
        int generation = ++_audioNotchHeightGeneration;
        if (!_isAudioView || _isAnimating || AudioScrollViewer == null) return;

        double current = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        double currentScrollHeight = AudioScrollViewer.ActualHeight > 0
            ? AudioScrollViewer.ActualHeight
            : Math.Max(0.0, current - _audioViewChrome);
        if (Math.Abs(current - target) < 0.5)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            ResizeHostWindowHeight(target);
            return;
        }

        int fps = AnimationConfig.TargetFps;
        bool growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        NotchBorder.BeginAnimation(HeightProperty, null);
        AudioScrollViewer.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = current;
        AudioScrollViewer.Height = currentScrollHeight;

        _isAnimating = true;

        var notchAnim = MakeAnim(current, target, dur, ease);
        var scrollAnim = MakeAnim(currentScrollHeight, Math.Max(0.0, target - _audioViewChrome), dur, ease);
        Timeline.SetDesiredFrameRate(notchAnim, fps);
        Timeline.SetDesiredFrameRate(scrollAnim, fps);

        notchAnim.Completed += (_, _) =>
        {
            _isAnimating = false;
            if (generation != _audioNotchHeightGeneration || !_isAudioView)
                return;

            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            if (!growing) ResizeHostWindowHeight(target);
        };

        NotchBorder.BeginAnimation(HeightProperty, notchAnim, HandoffBehavior.SnapshotAndReplace);
        AudioScrollViewer.BeginAnimation(HeightProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
    }
}
