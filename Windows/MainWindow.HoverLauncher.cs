using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    internal enum HoverLauncherDestination
    {
        Home,
        FileShelf,
        Timer,
        Audio,
        Display,
        PiggyBank
    }

    private const int HoverLauncherHideDelayMs = 150;
    private const double HoverLauncherShellGap = 38;
    private const double QuotaRingDiameter = 40;
    private const double QuotaRingStrokeThickness = 6;
    internal static readonly TimeSpan PiggyShellAutoRefreshInterval = TimeSpan.FromMinutes(5);
    internal static HoverLauncherDestination DefaultIdleClickDestination => HoverLauncherDestination.PiggyBank;

    private DispatcherTimer? _hoverLauncherHideTimer;
    private DispatcherTimer? _hoverExpandTimer;
    private HoverLauncherDestination? _pendingHoverLauncherDestination;
    private bool _hoverLauncherVisible;
    private bool _hoverExpandArmed;
    private bool _hoverLauncherButtonAnimationsWired;
    private DateTime _piggyLastRefreshAttemptUtc = DateTime.MinValue;

    internal static bool ShouldRevealHoverLauncher(
        bool isExpanded,
        bool isMusicExpanded,
        bool isAnimating,
        bool isMusicAnimating,
        bool isGreetingActive,
        bool spotlightOwnsNotch,
        bool shellVisible) =>
        !isExpanded && !isMusicExpanded && !isAnimating && !isMusicAnimating
        && !isGreetingActive && !spotlightOwnsNotch && shellVisible;

    internal static bool ShouldKeepHoverLauncherEngaged(bool notchHovered, bool launcherHovered)
        => notchHovered || launcherHovered;

    internal static double ResolveHoverCollapseTimerDelayMs(
        double configuredDelayMs,
        DateTime suppressUntilUtc,
        DateTime nowUtc)
    {
        double graceRemaining = Math.Max(0, (suppressUntilUtc - nowUtc).TotalMilliseconds);
        return Math.Max(40, Math.Max(Math.Max(0, configuredDelayMs), graceRemaining));
    }

    internal static bool IsPiggyShellRefreshDue(DateTime lastAttemptUtc, DateTime utcNow)
        => lastAttemptUtc == DateTime.MinValue || utcNow - lastAttemptUtc >= PiggyShellAutoRefreshInterval;

    internal static Rect CalculatePhysicalHoverBounds(
        int fixedX,
        int fixedY,
        int windowWidth,
        double dpiScale,
        double notchWidthDip,
        double notchHeightDip,
        double containerTopDip,
        double containerTranslateYDip)
    {
        var dpi = dpiScale > 0 ? dpiScale : 1.0;
        var width = Math.Max(1, notchWidthDip * dpi);
        var height = Math.Max(1, notchHeightDip * dpi);
        var left = fixedX + (windowWidth - width) / 2.0;
        var top = fixedY + (containerTopDip + containerTranslateYDip) * dpi;
        return new Rect(left, top, width, height);
    }

    private void SyncHoverDetectionBoundsToPhysicalNotch()
    {
        if (_hwnd == IntPtr.Zero || _windowWidth <= 0) return;

        var bounds = CalculatePhysicalHoverBounds(
            _fixedX,
            _fixedY,
            _windowWidth,
            _overlayWindow.DpiScale,
            _collapsedWidth,
            _collapsedHeight,
            NotchContainer?.Margin.Top ?? 0,
            NotchContainerTranslate?.Y ?? 0);

        _notchManager.HoverService.UpdateNotchBounds(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height);
    }

    private void InitialiseHoverLauncherShell()
    {
        UpdateHoverLauncherPlacement();
        UpdateHoverLauncherActiveState();
        WireHoverLauncherButtonAnimations();
        ApplyPiggyShellSnapshot(_piggyBankSnapshot);
        EnforceQuotaOnlyIdleChrome();
        RefreshPiggyBankIfStale();
    }

    private HoverLauncherDestination ResolveIdleExpandDestination()
        => ResolveIdleExpandDestination(
            _settings.ReopenLastViewOnExpand,
            _lastExpandedViewBeforeCollapse);

    internal static HoverLauncherDestination ResolveIdleExpandDestination(
        bool reopenLastViewOnExpand,
        HoverLauncherDestination lastExpandedDestination)
        => reopenLastViewOnExpand
            ? lastExpandedDestination
            : DefaultIdleClickDestination;

    internal static double ResolveDirectExpandSettledHostHeight(
        HoverLauncherDestination destination,
        double primaryHeight,
        double timerHeight,
        double audioHeight,
        double displayHeight,
        double piggyBankHeight)
        => destination switch
        {
            HoverLauncherDestination.FileShelf => CalculateSecondaryViewHeight(primaryHeight),
            HoverLauncherDestination.Timer => timerHeight,
            HoverLauncherDestination.Audio => audioHeight,
            HoverLauncherDestination.Display => displayHeight,
            HoverLauncherDestination.PiggyBank => piggyBankHeight,
            _ => primaryHeight
        };

    internal static HoverLauncherDestination ParsePersistedHoverLauncherDestination(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out HoverLauncherDestination destination)
            && Enum.IsDefined(destination))
        {
            return destination;
        }

        return HoverLauncherDestination.Home;
    }

    private void RememberHoverLauncherDestination(HoverLauncherDestination destination)
    {
        _lastExpandedViewBeforeCollapse = destination;
        string persisted = destination.ToString();
        if (string.Equals(
                _settings.LastExpandedLauncherDestination,
                persisted,
                StringComparison.Ordinal))
        {
            return;
        }

        _settings.LastExpandedLauncherDestination = persisted;
        _settingsService.Save(_settings);
    }

    private void RememberActiveHoverLauncherDestination()
    {
        if (!_isExpanded) return;

        RememberHoverLauncherDestination(ResolveActiveHoverLauncherDestination(
            _isSecondaryView,
            _isTimerView,
            _isAudioView,
            _isDisplayView,
            _isPiggyBankView));
    }

    private void ArmHoverExpand()
    {
        if (!_settings.EnableHoverExpand || _isExpanded || _isAnimating || _isMusicExpanded || _isMusicAnimating)
            return;

        _hoverExpandTimer ??= new DispatcherTimer(DispatcherPriority.Input);
        _hoverExpandTimer.Tick -= HoverExpandTimer_Tick;
        _hoverExpandTimer.Tick += HoverExpandTimer_Tick;
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoverExpandDelay));
        _hoverExpandArmed = true;
        _hoverExpandTimer.Stop();
        _hoverExpandTimer.Start();
    }

    private void CancelHoverExpand()
    {
        _hoverExpandArmed = false;
        _hoverExpandTimer?.Stop();
    }

    private void HoverExpandTimer_Tick(object? sender, EventArgs e)
    {
        _hoverExpandTimer?.Stop();
        RuntimeLog.Log("HOVER-EXPAND",
            $"Timer armed={_hoverExpandArmed} setting={_settings.EnableHoverExpand} expanded={_isExpanded} animating={_isAnimating}");
        if (!_hoverExpandArmed || !_settings.EnableHoverExpand ||
            _isExpanded || _isAnimating || _isMusicExpanded || _isMusicAnimating ||
            _isGreetingActive || !IsEffectivelyNotchVisible)
        {
            return;
        }

        _hoverExpandArmed = false;
        _pendingHoverLauncherDestination = ResolveIdleExpandDestination();
        UpdateHoverLauncherActiveState();
        ExpandNotch();
    }

    private void WireHoverLauncherButtonAnimations()
    {
        if (_hoverLauncherButtonAnimationsWired) return;
        _hoverLauncherButtonAnimationsWired = true;

        (Button Button, ScaleTransform Scale)[] buttons =
        [
            (HoverLauncherHomeButton, HoverLauncherHomeScale),
            (HoverLauncherShelfButton, HoverLauncherShelfScale),
            (HoverLauncherTimerButton, HoverLauncherTimerScale),
            (HoverLauncherAudioButton, HoverLauncherAudioScale),
            (HoverLauncherDisplayButton, HoverLauncherDisplayScale),
            (HoverLauncherPiggyButton, HoverLauncherPiggyScale),
            (HoverLauncherSettingsButton, HoverLauncherSettingsScale),
            (HoverLauncherExitButton, HoverLauncherExitScale)
        ];

        foreach (var (button, scale) in buttons)
        {
            button.MouseEnter += (_, _) => AnimateHoverLauncherButton(scale, true);
            button.MouseLeave += (_, _) => AnimateHoverLauncherButton(scale, false);
        }
    }

    private static void AnimateHoverLauncherButton(ScaleTransform scale, bool hovered)
    {
        if (AnimationConfig.ReduceMotion)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = hovered ? 1.055 : 1.0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(hovered ? 190 : 260);
        var easing = new ExponentialEase
        {
            Exponent = hovered ? 5 : 4,
            EasingMode = EasingMode.EaseOut
        };
        var target = hovered ? 1.07 : 1.0;
        var x = new DoubleAnimation(target, duration) { EasingFunction = easing };
        var y = new DoubleAnimation(target, duration) { EasingFunction = easing };
        Timeline.SetDesiredFrameRate(x, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(y, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, x, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, y, HandoffBehavior.SnapshotAndReplace);
    }

    private void EnforceQuotaOnlyIdleChrome()
    {
        if (_isExpanded || _isAnimating) return;

        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        StopUpdatePulseAnimation();
        UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
        UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateNotificationButton.Opacity = 0;
        UpdateNotificationButton.Visibility = Visibility.Collapsed;
        UpdateNotificationButton.IsHitTestVisible = false;
        UpdateNotificationTranslate.Y = -4;
    }

    private void UpdateHoverLauncherPlacement()
    {
        if (HoverLauncherDock is null) return;
        var containerTop = NotchContainer?.Margin.Top ?? 0;
        var shellHeight = NotchBorder?.ActualHeight > 0
            ? NotchBorder.ActualHeight
            : (!double.IsNaN(NotchBorder?.Height ?? double.NaN) && (NotchBorder?.Height ?? 0) > 0
                ? NotchBorder!.Height
                : _collapsedHeight);
        var top = Math.Max(24, containerTop + shellHeight + HoverLauncherShellGap);
        HoverLauncherDock.Margin = new Thickness(0, top, 0, 0);
    }

    private void NotchBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateHoverLauncherPlacement();

    private void ShowHoverLauncher()
    {
        if (!EnsureStableNotchStateForLauncherInput()) return;

        if (!ShouldRevealHoverLauncher(
                _isExpanded,
                _isMusicExpanded,
                _isAnimating,
                _isMusicAnimating,
                _isGreetingActive,
                _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility,
                IsEffectivelyNotchVisible))
        {
            return;
        }

        RevealHoverLauncherCore();
    }

    private void RevealHoverLauncherCore()
    {
        var alreadyVisible = _hoverLauncherVisible
            && HoverLauncherDock.Visibility == Visibility.Visible
            && HoverLauncherDock.Opacity >= 0.99;

        _hoverLauncherHideTimer?.Stop();
        UpdateHoverLauncherPlacement();
        UpdateHoverLauncherActiveState();
        _hoverLauncherVisible = true;

        HoverLauncherDock.Visibility = Visibility.Visible;
        HoverLauncherDock.IsHitTestVisible = true;

        if (alreadyVisible)
            return;

        if (AnimationConfig.ReduceMotion)
        {
            ResetHoverLauncherAnimations(visible: true);
            return;
        }

        var currentOpacity = HoverLauncherDock.Opacity;
        HoverLauncherDock.BeginAnimation(OpacityProperty, null);
        HoverLauncherDock.Opacity = currentOpacity;

        var currentY = HoverLauncherTranslate.Y;
        HoverLauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        HoverLauncherTranslate.Y = currentOpacity <= 0.01 ? -8 : currentY;

        var fade = new DoubleAnimation(HoverLauncherDock.Opacity, 1, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        HoverLauncherDock.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        var slide = new DoubleAnimation(HoverLauncherTranslate.Y, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        HoverLauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);

        var scales = GetHoverLauncherScales();
        for (var i = 0; i < scales.Length; i++)
        {
            var scale = scales[i];
            var currentScale = scale.ScaleX;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = currentOpacity <= 0.01 ? 0.96 : currentScale;
            scale.ScaleY = currentOpacity <= 0.01 ? 0.96 : currentScale;

            var begin = TimeSpan.FromMilliseconds(i * 16);
            var springX = CreateLauncherSpring(scale.ScaleX, begin);
            var springY = CreateLauncherSpring(scale.ScaleY, begin);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, springX, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, springY, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static DoubleAnimationUsingKeyFrames CreateLauncherSpring(double from, TimeSpan beginTime)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = beginTime };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.012, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(175)))
        {
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(255)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    private ScaleTransform[] GetHoverLauncherScales() =>
    [
        HoverLauncherHomeScale,
        HoverLauncherShelfScale,
        HoverLauncherTimerScale,
        HoverLauncherAudioScale,
        HoverLauncherDisplayScale,
        HoverLauncherPiggyScale,
        HoverLauncherSettingsScale,
        HoverLauncherExitScale
    ];

    private void ScheduleHoverLauncherHide()
    {
        if (_isExpanded || _isAnimating || !_hoverLauncherVisible) return;

        _hoverLauncherHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HoverLauncherHideDelayMs)
        };

        _hoverLauncherHideTimer.Tick -= HoverLauncherHideTimer_Tick;
        _hoverLauncherHideTimer.Tick += HoverLauncherHideTimer_Tick;
        _hoverLauncherHideTimer.Stop();
        _hoverLauncherHideTimer.Start();
    }

    private void HoverLauncherHideTimer_Tick(object? sender, EventArgs e)
    {
        _hoverLauncherHideTimer?.Stop();
        if (ShouldKeepHoverLauncherEngaged(NotchWrapper.IsMouseOver, HoverLauncherDock.IsMouseOver)) return;

        CancelHoverExpand();
        HideHoverLauncher(immediate: false);
        AnimateNotchHover(false);
    }

    private void HideHoverLauncher(bool immediate)
    {
        _hoverLauncherHideTimer?.Stop();
        _hoverLauncherVisible = false;
        HoverLauncherDock.IsHitTestVisible = false;

        if (immediate || AnimationConfig.ReduceMotion || HoverLauncherDock.Visibility != Visibility.Visible)
        {
            ResetHoverLauncherAnimations(visible: false);
            return;
        }

        var currentOpacity = HoverLauncherDock.Opacity;
        var currentY = HoverLauncherTranslate.Y;
        HoverLauncherDock.BeginAnimation(OpacityProperty, null);
        HoverLauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        HoverLauncherDock.Opacity = currentOpacity;
        HoverLauncherTranslate.Y = currentY;

        var fade = new DoubleAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(155))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (!_hoverLauncherVisible &&
                !ShouldKeepHoverLauncherEngaged(NotchWrapper.IsMouseOver, HoverLauncherDock.IsMouseOver))
            {
                ResetHoverLauncherAnimations(visible: false);
            }
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        HoverLauncherDock.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        var slide = new DoubleAnimation(currentY, -5, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        HoverLauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetHoverLauncherAnimations(bool visible)
    {
        HoverLauncherDock.BeginAnimation(OpacityProperty, null);
        HoverLauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        HoverLauncherDock.Opacity = visible ? 1 : 0;
        HoverLauncherTranslate.Y = visible ? 0 : -8;
        HoverLauncherDock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HoverLauncherDock.IsHitTestVisible = visible;

        foreach (var scale in GetHoverLauncherScales())
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    private void HoverLauncherDock_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer.Stop();
        _hoverLauncherHideTimer?.Stop();
        AnimateNotchHover(true);
        ShowHoverLauncher();
        ArmHoverExpand();
    }

    private void HoverLauncherDock_MouseLeave(object sender, MouseEventArgs e)
    {
        CancelHoverExpand();
        if (_isExpanded)
            RequestHoverCollapseAfterPointerExit("HoverLauncherDock_MouseLeave");
        else
            ScheduleHoverLauncherHide();
    }

    private void HoverLauncherHomeButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.Home);

    private void HoverLauncherShelfButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.FileShelf);

    private void HoverLauncherTimerButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.Timer);

    private void HoverLauncherAudioButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.Audio);

    private void HoverLauncherDisplayButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.Display);

    private void HoverLauncherPiggyButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.PiggyBank);

    private void HoverLauncherSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideHoverLauncher(immediate: true);
        OpenAppSettings();
    }

    private void HoverLauncherExitButton_Click(object sender, RoutedEventArgs e)
        => ShutdownApplication();

    private void QuotaGlanceButton_Click(object sender, RoutedEventArgs e)
        => BeginHoverLauncherNavigation(HoverLauncherDestination.PiggyBank);

    private void BeginHoverLauncherNavigation(HoverLauncherDestination destination)
    {
        if (!EnsureStableNotchStateForLauncherInput() ||
            _isAnimating || _isMusicAnimating || _isMusicExpanded || _isGreetingActive || !IsEffectivelyNotchVisible)
            return;

        RuntimeLog.Log("HOVER-LAUNCHER", $"Navigate {destination}");
        CancelHoverExpand();

        _pendingHoverLauncherDestination = destination;
        UpdateHoverLauncherActiveState();

        if (_isExpanded)
        {
            CompletePendingHoverLauncherNavigation();
            return;
        }

        ExpandNotch();
    }

    private bool EnsureStableNotchStateForLauncherInput()
    {
        if (_notchState.IsTransitioning &&
            !_isAnimating &&
            _notchState.TimeSinceLastTransition >= TimeSpan.FromMilliseconds(950))
        {
            _notchState.RecoverFromStuckTransition();
        }

        return !_notchState.IsTransitioning;
    }

    private bool CompletePendingHoverLauncherNavigation()
    {
        if (_pendingHoverLauncherDestination is not { } destination) return false;
        _pendingHoverLauncherDestination = null;

        NavigateExpandedHoverLauncherDestination(destination);
        UpdateHoverLauncherActiveState();
        if (!_isAnimating && ResolveActiveHoverLauncherDestination(
                _isSecondaryView,
                _isTimerView,
                _isAudioView,
                _isDisplayView,
                _isPiggyBankView) == destination)
        {
            RememberHoverLauncherDestination(destination);
        }
        return true;
    }

    private void NavigateExpandedHoverLauncherDestination(HoverLauncherDestination destination)
    {
        switch (destination)
        {
            case HoverLauncherDestination.Home:
                if (_isPiggyBankView)
                    SwitchFromPiggyBankToPrimaryView();
                else if (_isDisplayView)
                    SwitchFromDisplayToPrimaryView();
                else if (_isAudioView)
                    SwitchFromAudioToPrimaryView();
                else if (_isTimerView)
                    SwitchFromTimerToPrimaryView();
                else if (_isSecondaryView)
                {
                    StopCameraPreviewForViewExit();
                    SwitchToPrimaryView();
                }
                break;
            case HoverLauncherDestination.FileShelf:
                if (_isPiggyBankView)
                    SwitchFromPiggyBankToSecondaryView();
                else if (_isDisplayView)
                    SwitchFromDisplayToSecondaryView();
                else if (_isAudioView)
                    SwitchFromAudioToSecondaryView();
                else if (_isTimerView)
                    SwitchFromTimerToSecondaryView();
                else if (!_isSecondaryView)
                    SwitchToSecondaryView();
                break;
            case HoverLauncherDestination.Timer:
                if (_isPiggyBankView)
                    SwitchFromPiggyBankToTimerView();
                else if (_isDisplayView)
                    SwitchFromDisplayToTimerView();
                else if (_isAudioView)
                    SwitchFromAudioToTimerView();
                else if (_isSecondaryView)
                    SwitchFromSecondaryToTimerView();
                else if (!_isTimerView)
                    SwitchToTimerView();
                break;
            case HoverLauncherDestination.Audio:
                if (_isPiggyBankView)
                    SwitchFromPiggyBankToAudioView();
                else if (_isDisplayView)
                    SwitchFromDisplayToAudioView();
                else if (!_isAudioView)
                    SwitchToAudioView();
                break;
            case HoverLauncherDestination.Display:
                if (_isPiggyBankView)
                    SwitchFromPiggyBankToDisplayView();
                else if (!_isDisplayView)
                    SwitchToDisplayView();
                break;
            case HoverLauncherDestination.PiggyBank:
                if (!_isPiggyBankView)
                    SwitchToPiggyBankView();
                break;
        }
    }

    internal static HoverLauncherDestination ResolveActiveHoverLauncherDestination(
        bool isSecondaryView,
        bool isTimerView,
        bool isAudioView,
        bool isDisplayView,
        bool isPiggyBankView)
    {
        if (isPiggyBankView) return HoverLauncherDestination.PiggyBank;
        if (isDisplayView) return HoverLauncherDestination.Display;
        if (isAudioView) return HoverLauncherDestination.Audio;
        if (isTimerView) return HoverLauncherDestination.Timer;
        if (isSecondaryView) return HoverLauncherDestination.FileShelf;
        return HoverLauncherDestination.Home;
    }

    private void UpdateHoverLauncherActiveState()
    {
        var active = _pendingHoverLauncherDestination ?? ResolveActiveHoverLauncherDestination(
            _isSecondaryView,
            _isTimerView,
            _isAudioView,
            _isDisplayView,
            _isPiggyBankView);

        HoverLauncherHomeButton.Tag = active == HoverLauncherDestination.Home ? "True" : null;
        HoverLauncherShelfButton.Tag = active == HoverLauncherDestination.FileShelf ? "True" : null;
        HoverLauncherTimerButton.Tag = active == HoverLauncherDestination.Timer ? "True" : null;
        HoverLauncherAudioButton.Tag = active == HoverLauncherDestination.Audio ? "True" : null;
        HoverLauncherDisplayButton.Tag = active == HoverLauncherDestination.Display ? "True" : null;
        HoverLauncherPiggyButton.Tag = active == HoverLauncherDestination.PiggyBank ? "True" : null;
    }

    private void HideIdleShellChromeForExpansion()
    {
        RevealHoverLauncherCore();
        QuotaGlancePanel.BeginAnimation(OpacityProperty, null);
        QuotaGlancePanel.Opacity = 0;
        QuotaGlancePanel.Visibility = Visibility.Collapsed;
        QuotaGlancePanel.IsHitTestVisible = false;
    }

    private void RestoreIdleShellChromeAfterCollapse()
    {
        UpdateHoverLauncherActiveState();
        QuotaGlancePanel.Visibility = Visibility.Visible;
        QuotaGlancePanel.IsHitTestVisible = true;
        EnforceQuotaOnlyIdleChrome();

        if (AnimationConfig.ReduceMotion)
        {
            QuotaGlancePanel.BeginAnimation(OpacityProperty, null);
            QuotaGlancePanel.Opacity = 1;
        }
        else
        {
            QuotaGlancePanel.BeginAnimation(OpacityProperty, null);
            QuotaGlancePanel.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
            QuotaGlancePanel.BeginAnimation(OpacityProperty, fade);
        }

        if (ShouldKeepHoverLauncherEngaged(NotchWrapper.IsMouseOver, HoverLauncherDock.IsMouseOver))
            ShowHoverLauncher();
        else
            HideHoverLauncher(immediate: AnimationConfig.ReduceMotion);
    }

    private void ApplyPiggyShellSnapshot(PiggyBankSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            SetQuotaGlanceUnavailable(FiveHourQuotaGlanceButton, FiveHourQuotaGlanceDot, FiveHourQuotaGlanceText, "5-hour Codex quota");
            SetQuotaGlanceUnavailable(WeeklyQuotaGlanceButton, WeeklyQuotaGlanceDot, WeeklyQuotaGlanceText, "Weekly Codex quota");
            return;
        }

        UpdateQuotaGlance(
            FiveHourQuotaGlanceButton,
            FiveHourQuotaGlanceDot,
            FiveHourQuotaGlanceText,
            snapshot.FiveHour,
            weekly: false);
        UpdateQuotaGlance(
            WeeklyQuotaGlanceButton,
            WeeklyQuotaGlanceDot,
            WeeklyQuotaGlanceText,
            snapshot.Weekly,
            weekly: true);
    }

    private static void SetQuotaGlanceUnavailable(Button button, Ellipse dot, TextBlock valueText, string quotaName)
    {
        valueText.Text = "--";
        dot.Stroke = Brushes.Transparent;
        dot.StrokeDashArray = null;
        button.ToolTip = $"{quotaName}\nQuota unavailable";
    }

    private static void UpdateQuotaGlance(
        Button button,
        Ellipse dot,
        TextBlock valueText,
        PiggyQuotaWindow? quota,
        bool weekly)
    {
        if (quota is null)
        {
            SetQuotaGlanceUnavailable(button, dot, valueText, weekly ? "Weekly Codex quota" : "5-hour Codex quota");
            return;
        }

        var remaining = quota.RemainingPercent;
        valueText.Text = $"{remaining}%";
        dot.Stroke = new SolidColorBrush(PiggyBankFormatting.QuotaColour(remaining));
        SetQuotaRingProgress(dot, remaining);

        var now = DateTimeOffset.UtcNow;
        button.ToolTip = weekly
            ? $"Weekly Codex quota: {remaining}% remaining\n{PiggyBankFormatting.WeeklyRemaining(quota.ResetsAt, now)}\n{PiggyBankFormatting.WeeklyReset(quota.ResetsAt)}"
            : $"5-hour Codex quota: {remaining}% remaining\n{PiggyBankFormatting.FiveHourReset(quota.ResetsAt, now)}";
    }

    private static void SetQuotaRingProgress(Ellipse ring, int remainingPercent)
    {
        var clamped = Math.Clamp(remainingPercent, 0, 100);
        var circumferenceUnits = Math.PI * (QuotaRingDiameter - QuotaRingStrokeThickness) / QuotaRingStrokeThickness;
        var activeUnits = circumferenceUnits * clamped / 100d;
        var inactiveUnits = circumferenceUnits - activeUnits;

        // One dash + one gap forms a single continuous progress arc around the ring.
        // Tiny non-zero values avoid WPF treating 0-length dash segments as special cases.
        ring.StrokeDashArray = new DoubleCollection
        {
            Math.Max(0.001, activeUnits),
            Math.Max(0.001, inactiveUnits)
        };
    }

    private void RefreshPiggyShellTooltips()
    {
        if (_piggyBankSnapshot is { } snapshot)
            ApplyPiggyShellSnapshot(snapshot);
    }

    private void RefreshPiggyBankIfStale()
    {
        var now = DateTime.UtcNow;
        if (!IsPiggyShellRefreshDue(_piggyLastRefreshAttemptUtc, now)) return;
        _ = RefreshPiggyBankAsync();
    }
}
