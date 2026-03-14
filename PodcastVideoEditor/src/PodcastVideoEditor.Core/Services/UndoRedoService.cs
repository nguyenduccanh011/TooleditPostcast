#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Command-pattern action that can be undone and redone.
/// </summary>
public interface IUndoableAction
{
    /// <summary>Human-readable description shown in status bar.</summary>
    string Description { get; }
    void Undo();
    void Redo();
}

/// <summary>
/// Stack-based undo/redo manager. Record actions after user-driven mutations.
/// All calls must be on the UI thread (no locking).
/// </summary>
public sealed class UndoRedoService
{
    private readonly Stack<IUndoableAction> _undoStack = new();
    private readonly Stack<IUndoableAction> _redoStack = new();
    private const int MaxHistory = 50;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public string? UndoDescription => _undoStack.TryPeek(out var a) ? $"Undo: {a.Description}" : null;
    public string? RedoDescription => _redoStack.TryPeek(out var a) ? $"Redo: {a.Description}" : null;

    /// <summary>Raised whenever either stack changes (drives CanExecute refresh).</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Record an already-executed action so it can later be undone.
    /// Clears the redo stack.
    /// </summary>
    public void Record(IUndoableAction action)
    {
        _undoStack.Push(action);
        _redoStack.Clear();
        Trim();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var action = _redoStack.Pop();
        action.Redo();
        _undoStack.Push(action);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clear all history (call when a new project is opened).</summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Keep only the MaxHistory most recent actions.
    // Stack.ToArray(): [0]=most recently pushed (top), [n-1]=oldest (bottom).
    private void Trim()
    {
        if (_undoStack.Count <= MaxHistory) return;
        var buf = _undoStack.ToArray();
        _undoStack.Clear();
        // Re-push oldest-first so newest ends up on top.
        for (int i = MaxHistory - 1; i >= 0; i--)
            _undoStack.Push(buf[i]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Concrete action types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Segment move or resize (start/end time change).</summary>
public sealed class SegmentTimingChangedAction : IUndoableAction
{
    private readonly Segment _seg;
    private readonly double _oldStart, _oldEnd, _newStart, _newEnd;
    private readonly Action _invalidateCache;

    public SegmentTimingChangedAction(
        Segment seg,
        double oldStart, double oldEnd,
        double newStart, double newEnd,
        Action invalidateCache)
    {
        _seg = seg;
        _oldStart = oldStart; _oldEnd = oldEnd;
        _newStart = newStart; _newEnd = newEnd;
        _invalidateCache = invalidateCache;
    }

    public string Description =>
        _oldStart != _newStart && _oldEnd != _newEnd ? "Move segment" : "Resize segment";

    public void Undo() { _seg.StartTime = _oldStart; _seg.EndTime = _oldEnd; _invalidateCache(); }
    public void Redo() { _seg.StartTime = _newStart; _seg.EndTime = _newEnd; _invalidateCache(); }
}

/// <summary>A segment was added to a track.</summary>
public sealed class SegmentAddedAction : IUndoableAction
{
    private readonly ObservableCollection<Segment> _segments;
    private readonly Segment _seg;
    private readonly Action _invalidateCache;
    private readonly Action<Segment?> _selectSegment;

    public SegmentAddedAction(
        ObservableCollection<Segment> segments, Segment seg,
        Action invalidateCache, Action<Segment?> selectSegment)
    {
        _segments = segments; _seg = seg;
        _invalidateCache = invalidateCache; _selectSegment = selectSegment;
    }

    public string Description => "Add segment";
    public void Undo() { _segments.Remove(_seg); _invalidateCache(); _selectSegment(null); }
    public void Redo() { if (!_segments.Contains(_seg)) _segments.Add(_seg); _invalidateCache(); _selectSegment(_seg); }
}

/// <summary>A segment was deleted from a track.</summary>
public sealed class SegmentDeletedAction : IUndoableAction
{
    private readonly ObservableCollection<Segment> _segments;
    private readonly Segment _seg;
    private readonly int _index;
    private readonly Action _invalidateCache;
    private readonly Action<Segment?> _selectSegment;

    public SegmentDeletedAction(
        ObservableCollection<Segment> segments, Segment seg, int index,
        Action invalidateCache, Action<Segment?> selectSegment)
    {
        _segments = segments; _seg = seg; _index = index;
        _invalidateCache = invalidateCache; _selectSegment = selectSegment;
    }

    public string Description => "Delete segment";
    public void Undo() { _segments.Insert(Math.Min(_index, _segments.Count), _seg); _invalidateCache(); _selectSegment(_seg); }
    public void Redo() { _segments.Remove(_seg); _invalidateCache(); _selectSegment(null); }
}

/// <summary>
/// A segment was split: original trimmed at splitPoint, right half added as new segment.
/// </summary>
public sealed class SegmentSplitAction : IUndoableAction
{
    private readonly ObservableCollection<Segment> _segments;
    private readonly Segment _original;
    private readonly double _originalEndTime;
    private readonly Segment _rightHalf;
    private readonly Action _invalidateCache;
    private readonly Action<Segment?> _selectSegment;

    public SegmentSplitAction(
        ObservableCollection<Segment> segments,
        Segment original, double originalEndTime, Segment rightHalf,
        Action invalidateCache, Action<Segment?> selectSegment)
    {
        _segments = segments; _original = original;
        _originalEndTime = originalEndTime; _rightHalf = rightHalf;
        _invalidateCache = invalidateCache; _selectSegment = selectSegment;
    }

    public string Description => "Split segment";

    public void Undo()
    {
        _segments.Remove(_rightHalf);
        _original.EndTime = _originalEndTime;
        _invalidateCache();
        _selectSegment(_original);
    }

    public void Redo()
    {
        _original.EndTime = _rightHalf.StartTime;
        if (!_segments.Contains(_rightHalf))
            _segments.Add(_rightHalf);
        _invalidateCache();
        _selectSegment(_rightHalf);
    }
}

/// <summary>A canvas element was moved on the canvas.</summary>
public sealed class ElementMovedAction : IUndoableAction
{
    private readonly CanvasElement _el;
    private readonly double _ox, _oy, _nx, _ny;

    public ElementMovedAction(CanvasElement el, double oldX, double oldY, double newX, double newY)
    { _el = el; _ox = oldX; _oy = oldY; _nx = newX; _ny = newY; }

    public string Description => $"Move '{_el.Name}'";
    public void Undo() { _el.X = _ox; _el.Y = _oy; }
    public void Redo() { _el.X = _nx; _el.Y = _ny; }
}

/// <summary>A canvas element was added.</summary>
public sealed class ElementAddedAction : IUndoableAction
{
    private readonly ObservableCollection<CanvasElement> _elements;
    private readonly CanvasElement _el;

    public ElementAddedAction(ObservableCollection<CanvasElement> elements, CanvasElement el)
    { _elements = elements; _el = el; }

    public string Description => $"Add '{_el.Name}'";
    public void Undo() => _elements.Remove(_el);
    public void Redo() { if (!_elements.Contains(_el)) _elements.Add(_el); }
}

/// <summary>A canvas element was deleted.</summary>
public sealed class ElementDeletedAction : IUndoableAction
{
    private readonly ObservableCollection<CanvasElement> _elements;
    private readonly CanvasElement _el;

    public ElementDeletedAction(ObservableCollection<CanvasElement> elements, CanvasElement el)
    { _elements = elements; _el = el; }

    public string Description => $"Delete '{_el.Name}'";
    public void Undo() { if (!_elements.Contains(_el)) _elements.Add(_el); }
    public void Redo() => _elements.Remove(_el);
}
