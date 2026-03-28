using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PodcastVideoEditor.Ui.Helpers;

/// <summary>
/// Timer-based auto-scroll helper for drag operations near viewport edges.
/// Matches commercial editor behavior (CapCut / Premiere Pro / DaVinci Resolve):
/// when the cursor is within the edge margin during a drag, the timeline scrolls
/// continuously at a speed proportional to how close the cursor is to the edge.
/// </summary>
internal sealed class DragAutoScrollHelper
{
    private const double EdgeMarginPx = 50.0;
    private const double MaxScrollSpeedPxPerSec = 400.0;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(30);

    private readonly DispatcherTimer _timer;
    private ScrollViewer? _scroller;
    private Func<Point>? _getCursorPosition;
    private Action<double>? _expandTimeline;
    private double _scrollDelta;

    public DragAutoScrollHelper()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TickInterval
        };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Start auto-scroll monitoring.
    /// </summary>
    /// <param name="scroller">The horizontal ScrollViewer to scroll.</param>
    /// <param name="getCursorPosition">Returns cursor position relative to <paramref name="scroller"/>.</param>
    /// <param name="expandTimeline">Called with the right-edge time when scrolling past the timeline end.
    /// May be null if timeline expansion is not needed.</param>
    public void Start(ScrollViewer scroller, Func<Point> getCursorPosition, Action<double>? expandTimeline = null)
    {
        _scroller = scroller;
        _getCursorPosition = getCursorPosition;
        _expandTimeline = expandTimeline;
        _scrollDelta = 0;
        _timer.Start();
    }

    /// <summary>
    /// Stop auto-scroll monitoring and release references.
    /// </summary>
    public void Stop()
    {
        _timer.Stop();
        _scroller = null;
        _getCursorPosition = null;
        _expandTimeline = null;
        _scrollDelta = 0;
    }

    /// <summary>Whether auto-scroll is currently active.</summary>
    public bool IsActive => _timer.IsEnabled;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_scroller == null || _getCursorPosition == null)
            return;

        Point cursor;
        try
        {
            cursor = _getCursorPosition();
        }
        catch
        {
            return;
        }

        double viewportWidth = _scroller.ViewportWidth;
        if (viewportWidth <= 0)
            return;

        double speed = 0;

        // Cursor near right edge → scroll right
        if (cursor.X > viewportWidth - EdgeMarginPx)
        {
            double proximity = Math.Min(1.0, (cursor.X - (viewportWidth - EdgeMarginPx)) / EdgeMarginPx);
            speed = proximity * MaxScrollSpeedPxPerSec;
        }
        // Cursor near left edge → scroll left
        else if (cursor.X < EdgeMarginPx)
        {
            double proximity = Math.Min(1.0, (EdgeMarginPx - cursor.X) / EdgeMarginPx);
            speed = -proximity * MaxScrollSpeedPxPerSec;
        }

        if (Math.Abs(speed) < 1.0)
        {
            _scrollDelta = 0;
            return;
        }

        double deltaPixels = speed * TickInterval.TotalSeconds;
        double currentOffset = _scroller.HorizontalOffset;
        double maxOffset = _scroller.ScrollableWidth;

        if (double.IsNaN(maxOffset) || maxOffset < 0)
            maxOffset = 0;

        double newOffset = currentOffset + deltaPixels;
        newOffset = Math.Max(0, Math.Min(newOffset, maxOffset));

        // If scrolling past the right edge and we have a timeline expander, expand it
        if (deltaPixels > 0 && newOffset >= maxOffset - 1 && _expandTimeline != null)
        {
            _expandTimeline(0); // Signal: expand by a small amount
        }

        if (Math.Abs(newOffset - currentOffset) > 0.5)
        {
            _scroller.ScrollToHorizontalOffset(newOffset);
            _scrollDelta = newOffset - currentOffset;
        }
        else
        {
            _scrollDelta = 0;
        }
    }

    /// <summary>
    /// The pixel delta applied in the most recent tick (positive = rightward).
    /// Callers can use this to adjust the drag accumulator so the segment follows the scroll.
    /// </summary>
    public double LastScrollDelta => _scrollDelta;
}
