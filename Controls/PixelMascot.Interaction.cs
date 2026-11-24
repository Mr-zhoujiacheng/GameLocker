using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace GameLocker.Controls;

public sealed partial class PixelMascot
{
    private const long SleepAfterMs = 60_000;
    private const long ReturnMs = 320;
    private const long TapGapMs = 650, TapHoldMs = 550;
    private const int AngryTapCount = 5;
    private const double DragThreshold = 5;

    private bool _pageActive, _deepSleeping, _dragging;
    private bool _tapCandidate, _pressWokeMascot;
    private long _lastActivityAt, _pressAt, _lastTapAt;
    private int _tapCount;
    private uint? _pointerId;
    private double _pressX, _pressY, _pressOffset, _anchorX, _trackWidth;
    private long? _returnAt;
    private double _returnFrom;

    internal bool IsPointerCaptured => _pointerId.HasValue;
    internal double HorizontalDragOffset => DragOffset.X;

    internal void SetPageActive(bool active)
    {
        _pageActive = active;
        if (active) TryStartPlayback();
        else SuspendPlayback();
    }

    private void SuspendPlayback()
    {
        _timer.Stop();
        _deepSleeping = false;
        CancelMovement();
    }

    internal void ReportActivity(bool wake = true)
    {
        if (!_timer.IsEnabled) return;
        long now = Environment.TickCount64;
        _lastActivityAt = now;
        if (!wake || _row != SleepRow) return;

        _deepSleeping = false;
        StartPose(WakeRow, now);
    }

    internal void SetDragBounds(double anchorX, double trackWidth)
    {
        _anchorX = anchorX;
        _trackWidth = trackWidth;
        if (_dragging || _returnAt.HasValue) DragOffset.X = ClampOffset(DragOffset.X);
    }

    private double ClampOffset(double offset)
    {
        double maxLeft = Math.Max(0, _trackWidth - Width);
        return Math.Round(Math.Clamp(_anchorX + offset, 0, maxLeft) - _anchorX);
    }

    private void UpdateInteraction(long now)
    {
        if (_returnAt is long start)
        {
            var t = Math.Clamp((double)(now - start) / ReturnMs, 0, 1);
            if (t >= 1)
            {
                StopReturning();
                DragOffset.X = 0;
                if (_row == DragRow) StartIdle(now);
            }
            else
            {
                DragOffset.X = ClampOffset(_returnFrom * Math.Pow(1 - t, 3));
            }
        }

        if (!_musicPlaying && _row != HeadphonesRow && !_deepSleeping && !IsPointerCaptured && !_returnAt.HasValue
            && now - _lastActivityAt >= SleepAfterMs)
        {
            _deepSleeping = true;
            StartPose(SleepRow, now);
        }
    }

    private void OnMascotPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_timer.IsEnabled || IsPointerCaptured) return;
        var point = e.GetCurrentPoint(null);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        if (!MascotHitSurface.CapturePointer(e.Pointer)) return;

        _pointerId = e.Pointer.PointerId;
        StopReturning();
        _pressX = point.Position.X;
        _pressY = point.Position.Y;
        _pressOffset = DragOffset.X;
        _pressAt = Environment.TickCount64;
        _tapCandidate = true;
        _pressWokeMascot = _row == SleepRow;
        ReportActivity();
        e.Handled = true;
    }

    private void OnMascotMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        var point = e.GetCurrentPoint(null).Position;
        double dx = point.X - _pressX;
        _tapCandidate &= Math.Abs(dx) < DragThreshold && Math.Abs(point.Y - _pressY) < DragThreshold;
        if (!_dragging && Math.Abs(dx) < DragThreshold) return;

        long now = Environment.TickCount64;
        _lastActivityAt = now;
        if (!_dragging)
        {
            _dragging = true;
            if (_row != HeadphonesRow) StartPose(DragRow, now);
        }
        DragOffset.X = ClampOffset(_pressOffset + dx);
    }

    private void OnMascotReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(null).Position;
        long now = Environment.TickCount64;
        bool tapped = _tapCandidate && !_dragging && now - _pressAt <= TapHoldMs
            && Math.Abs(point.X - _pressX) < DragThreshold && Math.Abs(point.Y - _pressY) < DragThreshold;
        FinishPointer();
        if (tapped) PlayTapReaction(now);
        else _tapCount = 0;
        MascotHitSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnMascotCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _tapCount = 0;
        FinishPointer();
        e.Handled = true;
    }

    private void PlayTapReaction(long now)
    {
        if (_row is AngryRow or HeadphonesRow) return;
        _tapCount = now - _lastTapAt <= TapGapMs ? _tapCount + 1 : 1;
        _lastTapAt = now;
        if (_tapCount == AngryTapCount)
        {
            _tapCount = 0;
            StartPose(AngryRow, now);
        }
        else if (!_pressWokeMascot)
        {
            StartPose(HappyRow, now);
        }
    }

    private void FinishPointer()
    {
        long now = Environment.TickCount64;
        _lastActivityAt = now;
        _pointerId = null;
        _dragging = false;
        if (DragOffset.X != 0)
        {
            _returnAt = now;
            _returnFrom = DragOffset.X;
            _timer.Interval = TimeSpan.FromMilliseconds(16);
        }
        else if (_row == DragRow)
        {
            StartIdle(now);
        }
    }

    private void StopReturning()
    {
        if (!_returnAt.HasValue) return;
        _returnAt = null;
        _timer.Interval = TimeSpan.FromMilliseconds(45);
    }

    internal void CancelMovement()
    {
        var hadPointer = IsPointerCaptured;
        _pointerId = null;
        _dragging = false;
        _tapCount = 0;
        StopReturning();
        DragOffset.X = 0;
        if (hadPointer) MascotHitSurface.ReleasePointerCaptures();
        if (_timer.IsEnabled && _row == DragRow) StartIdle(Environment.TickCount64);
    }
}
