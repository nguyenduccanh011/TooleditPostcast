#nullable enable
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services;
using PodcastVideoEditor.Core.Services.AI;
using PodcastVideoEditor.Core.Utilities;
using PodcastVideoEditor.Ui.Views;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class TimelineViewModel
    {
        // ── Script Paste ──────────────────────────────────────────────────────

        /// <summary>
        /// Apply pasted script: parse [start → end] text, replace text track segments, persist (ST-3, multi-track).
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanApplyScript))]
        public async Task ApplyScriptAsync()
        {
            try
            {
                if (_projectViewModel.CurrentProject == null)
                {
                    StatusMessage = "No project loaded";
                    return;
                }

                // Find text track (first track with TrackType = "text")
                var textTrack = _projectViewModel.CurrentProject.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Text);
                if (textTrack == null)
                {
                    StatusMessage = "No text track found in project";
                    return;
                }

                var parsed = ScriptParser.Parse(ScriptPasteText);
                if (parsed.Count == 0)
                {
                    StatusMessage = "No valid segments (format: [start → end] text)";
                    return;
                }

                var projectId = _projectViewModel.CurrentProject.Id;

                // Capture old text-track segment IDs so CanvasViewModel can remove orphaned elements
                var oldSegmentIds = (textTrack.Segments ?? Enumerable.Empty<Segment>())
                    .Select(s => s.Id)
                    .ToHashSet();

                var newSegments = new List<Segment>();
                for (int i = 0; i < parsed.Count; i++)
                {
                    var p = parsed[i];
                    newSegments.Add(new Segment
                    {
                        ProjectId = projectId,
                        TrackId = textTrack.Id,
                        StartTime = Math.Round(p.Start, 2),
                        EndTime = Math.Round(p.End, 2),
                        Text = p.Text,
                        Kind = SegmentKinds.Text,
                        TransitionType = "fade",
                        TransitionDuration = 0.5,
                        Order = i
                    });
                }

                // Close gaps: extend each segment's EndTime to next segment's StartTime
                // so there are no black frames between scenes.
                if (AutoCloseGaps)
                    CloseGapsService.CloseGaps(newSegments);

                await _projectViewModel.ReplaceSegmentsAndSaveAsync(newSegments);

                // Reload tracks from project
                LoadTracksFromProject();

                // Notify CanvasViewModel to create/refresh canvas TextElements
                ScriptApplied?.Invoke(this, new ScriptAppliedEventArgs(oldSegmentIds, newSegments.AsReadOnly()));

                StatusMessage = $"Script applied: {newSegments.Count} segment(s) in text track";
                Log.Information("Script applied: {Count} segments in text track {TrackId}", newSegments.Count, textTrack.Id);
            }
            catch (Exception ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                StatusMessage = $"Error applying script: {message}";
                Log.Error(ex, "Error applying script: {Message}", ex.Message);
            }
        }

        private bool CanApplyScript() => _projectViewModel.CurrentProject != null && !string.IsNullOrWhiteSpace(ScriptPasteText);

        /// <summary>
        /// Fired after ApplyScriptAsync successfully replaces the text track.
        /// Carries the IDs of the segments that were removed and the new segments that were created,
        /// so CanvasViewModel can synchronise canvas elements.
        /// </summary>
        public event EventHandler<ScriptAppliedEventArgs>? ScriptApplied;

        /// <summary>
        /// Fired after every committed seek (SeekTo / CommitScrubSeek).
        /// CanvasViewModel subscribes to bypass its 60fps throttle and always render
        /// the correct frame immediately after the user repositions the playhead.
        /// </summary>
        public event EventHandler? ScrubCompleted;

        partial void OnScriptPasteTextChanged(string value)
        {
            ApplyScriptCommand.NotifyCanExecuteChanged();
            AnalyzeWithAICommand.NotifyCanExecuteChanged();
        }

        // ── Analyze With AI ─────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanAnalyzeWithAI))]
        private async Task AnalyzeWithAIAsync()
        {
            var project = _projectViewModel.CurrentProject;
            if (project == null || _aiOrchestrator == null) return;

            _isAnalyzing = true;
            AnalyzeWithAICommand.NotifyCanExecuteChanged();
            AIAnalysisStatus = "Đang khởi động…";

            var cts = new System.Threading.CancellationTokenSource();
            var progressVm = new AIAnalysisProgressViewModel(cts);
            var progressWindow = new Views.AIAnalysisProgressWindow(progressVm);
            progressWindow.Owner = System.Windows.Application.Current.MainWindow;

            // Progress<T> already marshals to the UI thread via SynchronizationContext.
            // No need for an extra Dispatcher.Invoke wrapper.
            var progress = new Progress<AIAnalysisProgressReport>(report =>
            {
                progressVm.Report(report);
                AIAnalysisStatus = report.Message;
            });

            // Capture existing text-track segment IDs before replacing (for ScriptApplied event)
            var textTrack = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Text);
            var oldSegmentIds = textTrack?.Segments
                .Select(s => s.Id)
                .ToHashSet() ?? new HashSet<string>();

            var mainWindow = System.Windows.Application.Current.MainWindow;
            if (mainWindow != null) mainWindow.IsEnabled = false;
            progressWindow.Show();

            try
            {
                var result = await Task.Run(
                    () => _aiOrchestrator.RunAsync(
                        project, ScriptPasteText, TotalDuration, progress, cts.Token, AutoCloseGaps),
                    cts.Token);

                // Persist text track
                if (textTrack != null)
                    await _projectViewModel.ReplaceSegmentsForTrackAsync(textTrack.Id, result.TextSegments);

                // Persist visual track
                var visualTrack = project.Tracks?.FirstOrDefault(t => t.TrackType == TrackTypes.Visual);
                if (visualTrack != null)
                    await _projectViewModel.ReplaceSegmentsForTrackAsync(visualTrack.Id, result.VisualSegments);

                LoadTracksFromProject();
                ScriptApplied?.Invoke(this, new ScriptAppliedEventArgs(
                    oldSegmentIds, result.TextSegments));

                AIAnalysisStatus = $"✓ Xong: {result.TextSegments.Length} segment, {result.RegisteredAssetIds.Length} ảnh";
                StatusMessage    = AIAnalysisStatus;
            }
            catch (OperationCanceledException)
            {
                AIAnalysisStatus = "Đã huỷ";
            }
            catch (Exception ex)
            {
                AIAnalysisStatus = $"Lỗi: {ex.Message}";
                Log.Error(ex, "AnalyzeWithAI failed");
            }
            finally
            {
                progressVm.NotifyComplete();
                cts.Dispose();
                _isAnalyzing = false;
                AnalyzeWithAICommand.NotifyCanExecuteChanged();
                if (mainWindow != null) mainWindow.IsEnabled = true;
            }
        }

        private bool CanAnalyzeWithAI()
            => !_isAnalyzing
            && _projectViewModel.CurrentProject != null
            && !string.IsNullOrWhiteSpace(ScriptPasteText)
            && _aiOrchestrator != null;
    }

    /// <summary>
    /// Event args for the <see cref="TimelineViewModel.ScriptApplied"/> event.
    /// </summary>
    /// <param name="ReplacedSegmentIds">IDs of text-track segments that were removed by the apply.</param>
    /// <param name="NewSegments">The freshly-created segments that replaced them.</param>
    public record ScriptAppliedEventArgs(
        IReadOnlySet<string> ReplacedSegmentIds,
        IReadOnlyList<Segment> NewSegments);
}
