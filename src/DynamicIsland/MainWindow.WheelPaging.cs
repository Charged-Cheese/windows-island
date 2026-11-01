using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

public partial class MainWindow
{
    private bool _clientAreaAnimation = SystemParameters.ClientAreaAnimation;
    // Unit-mass spring: k=700, c=52, zeta≈0.983. Exact integration avoids frame-rate drift.
    private const double WheelStiffness = 700;
    private const double WheelHalfDamping = 26;
    private static readonly double WheelFrequency = Math.Sqrt(WheelStiffness - WheelHalfDamping * WheelHalfDamping);

    private void StartWheelGlide(int target)
    {
        long now = Stopwatch.GetTimestamp();
        bool continuing = _wheelGliding;
        bool takingOverClick = _tabAnimating && !continuing;
        if (continuing)
        {
            AdvanceWheelGlide(now);
            RecordPagingSample("retarget_before");
        }
        else if (takingOverClick)
        {
            double t = Math.Clamp(Stopwatch.GetElapsedTime(_tabAnimStart, now).TotalMilliseconds / TabTransitionMs, 0, 1);
            _tabPosition = _tabAnimFrom + (_tabAnimTo - _tabAnimFrom) * EaseOutQuint(t);
            _wheelVelocity = (_tabAnimTo - _tabAnimFrom) * 5 * Math.Pow(1 - t, 4) / (TabTransitionMs / 1000);
            RecordPagingSample("handoff_before");
            RecordPagingSample("step_cancel");
        }
        else
        {
            _wheelVelocity = 0;
        }

        ClearTabOpacityAnimations();
        _tabAnimFrom = _tabPosition;
        _tabAnimTo = target;
        _wheelLastTick = now;
        _wheelGliding = true;
        _tabAnimating = true;
        _lastTabTransition = DateTime.UtcNow;
        SyncTabTarget(target);
        TargetPageSize(target, now);
        if (!continuing) BeginTabTransitionVisual();
        RecordPagingSample(continuing ? "wheel_retarget" : "wheel_start");
        if (takingOverClick) RecordPagingSample("handoff_after");

        if (!_clientAreaAnimation)
        {
            FinishWheelGlide();
            return;
        }
        CompositionTarget.Rendering -= OnTabRendering;
        CompositionTarget.Rendering += OnTabRendering;
        ApplyTabTransition();
    }

    private void AdvanceWheelGlide(long now)
    {
        double dt = Stopwatch.GetElapsedTime(_wheelLastTick, now).TotalSeconds;
        _wheelLastTick = now;
        if (dt <= 0) return;

        double error = _tabPosition - _tabAnimTo;
        double velocity = _wheelVelocity;
        double decay = Math.Exp(-WheelHalfDamping * dt);
        double cosine = Math.Cos(WheelFrequency * dt);
        double sine = Math.Sin(WheelFrequency * dt) / WheelFrequency;
        double position = _tabAnimTo + decay * (error * cosine + (velocity + WheelHalfDamping * error) * sine);
        _wheelVelocity = decay * (velocity * cosine - (WheelHalfDamping * velocity + WheelStiffness * error) * sine);
        _tabPosition = Math.Clamp(position, 0, _tabPages.Length - 1);
        if (_tabPosition != position) _wheelVelocity = 0;
    }

    private void RenderWheelGlide()
    {
        AdvanceWheelGlide(Stopwatch.GetTimestamp());
        // Pixel-based thresholds, not the much looser size-spring thresholds in page units.
        if (Math.Abs(_tabPosition - _tabAnimTo) * TabSlideDistance < 0.15 &&
            Math.Abs(_wheelVelocity) * TabSlideDistance < 4)
        {
            FinishWheelGlide();
            return;
        }
        ApplyTabTransition();
    }

    private void FinishWheelGlide()
    {
        _tabPosition = _tabAnimTo;
        _wheelVelocity = 0;
        _wheelGliding = false;
        _tabAnimating = false;
        ApplyTabTransition();
        CompositionTarget.Rendering -= OnTabRendering;
        RecordPagingSample("wheel_finish");
        EndTabTransitionVisual();
    }

    private void ClearTabOpacityAnimations()
    {
        foreach (FrameworkElement page in _tabPages)
            page.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private void SyncTabTarget(int target)
    {
        if (_viewModel.ActiveTab != target)
        {
            _tabSyncingTarget = true;
            try { _viewModel.ActiveTab = target; }
            finally { _tabSyncingTarget = false; }
        }
        if (target == 4) _viewModel.SyncNotificationHistory();
    }

    private void TargetPageSize(int target, long timestamp)
    {
        // 已折叠/通知态的后续点击队列不得把窗口重新撑开。
        if (_state != IslandState.Expanded) return;
        (double width, double height) = SizeForTab(target);
        if (_clientAreaAnimation)
            _spring.SetTargetTimed(width, height, ExpandedRadius, TabTransitionMs, timestamp);
        else
            _spring.SnapTo(width, height, ExpandedRadius);
        UpdateIslandHitRect();
    }

    private void OnSystemMotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSystemMotionChanged(sender, e)));
            return;
        }
        ApplySystemMotionPreference(SystemParameters.ClientAreaAnimation);
    }

    private void ApplySystemMotionPreference(bool animate)
    {
        _clientAreaAnimation = animate;
        if (!animate)
        {
            _spring.SnapTo(_spring.TargetWidth, _spring.TargetHeight, _spring.TargetRadius);
            if (_tabAnimating) OnTabRendering(this, EventArgs.Empty);
        }
    }
}
