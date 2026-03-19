#nullable enable
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PodcastVideoEditor.Ui.ViewModels
{
    public partial class TimelineViewModel
    {
        // ── BGM private state ────────────────────────────────────────────────
        private CancellationTokenSource? _bgmSyncDebounce;
        private bool _isLoadingBgm;

        // ── BGM property-change handlers ────────────────────────────────────
        partial void OnBgmVolumeChanged(double value)     { if (!_isLoadingBgm) DebouncedSyncBgm(); }
        partial void OnBgmFadeInChanged(double value)     { if (!_isLoadingBgm) DebouncedSyncBgm(); }
        partial void OnBgmFadeOutChanged(double value)    { if (!_isLoadingBgm) DebouncedSyncBgm(); }
        partial void OnBgmIsLoopingChanged(bool value)    { if (!_isLoadingBgm) DebouncedSyncBgm(); }
        partial void OnBgmIsEnabledChanged(bool value)    { if (!_isLoadingBgm) DebouncedSyncBgm(); }

        partial void OnBgmFilePathChanged(string value)
        {
            if (!_isLoadingBgm) DebouncedSyncBgm();
        }

        // ── BGM commands ─────────────────────────────────────────────────────

        /// <summary>Open a file picker and set the BGM file path.</summary>
        [RelayCommand]
        public void BrowseBgmFile()
        {
            // If playing, the modal dialog blocks the UI thread while the mixer keeps running.
            // All readers advance in lockstep via the mixer, so no desync from the dialog itself.
            // But stale sync-loop dispatches may queue up. Force resync after dialog closes.
            bool wasPlaying = _audioService.IsPlaying;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select BGM audio file",
                Filter = "Audio files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                BgmFilePath = dlg.FileName;
                SyncBgmToProject();
                _ = _projectViewModel.SaveProjectAsync();
                StatusMessage = $"BGM: {System.IO.Path.GetFileName(BgmFilePath)}";
            }

            // Force resync to undo any stale position updates that queued during the dialog
            if (wasPlaying)
            {
                var pos = _audioService.GetCurrentPosition();
                LastSyncedPlayhead = pos;
                _audioPreviewService.SyncPreviewAudio(pos, forceResync: true);
            }
        }

        /// <summary>Remove the BGM track from the current project.</summary>
        [RelayCommand]
        public void ClearBgm()
        {
            BgmFilePath = string.Empty;
            SyncBgmToProject();
            _audioService.StopBgmAudio();
            _ = _projectViewModel.SaveProjectAsync();
            StatusMessage = "BGM cleared";
        }

        // ── BGM helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Load BGM settings from the project's first BgmTrack (if any).
        /// Called whenever the project changes.
        /// </summary>
        private void LoadBgmFromProject()
        {
            _isLoadingBgm = true;
            try
            {
                var bgm = _projectViewModel.CurrentProject?.BgmTracks?.FirstOrDefault();
                if (bgm != null)
                {
                    BgmFilePath   = bgm.AudioPath;
                    BgmVolume     = bgm.Volume;
                    BgmFadeIn     = bgm.FadeInSeconds;
                    BgmFadeOut    = bgm.FadeOutSeconds;
                    BgmIsLooping  = bgm.IsLooping;
                    BgmIsEnabled  = bgm.IsEnabled;
                }
                else
                {
                    BgmFilePath   = string.Empty;
                    BgmVolume     = 0.3;
                    BgmFadeIn     = 2.0;
                    BgmFadeOut    = 2.0;
                    BgmIsLooping  = false;
                    BgmIsEnabled  = true;
                }
            }
            finally
            {
                _isLoadingBgm = false;
            }
        }

        /// <summary>
        /// Write current BGM UI state back to the project model (first BgmTrack, or create one).
        /// </summary>
        private void SyncBgmToProject()
        {
            var project = _projectViewModel.CurrentProject;
            if (project == null) return;

            project.BgmTracks ??= [];
            var bgm = project.BgmTracks.FirstOrDefault();
            if (bgm == null)
            {
                bgm = new BgmTrack { ProjectId = project.Id };
                project.BgmTracks.Add(bgm);
            }
            bgm.AudioPath      = BgmFilePath;
            bgm.Volume         = BgmVolume;
            bgm.FadeInSeconds  = BgmFadeIn;
            bgm.FadeOutSeconds = BgmFadeOut;
            bgm.IsLooping      = BgmIsLooping;
            bgm.IsEnabled      = BgmIsEnabled;
        }

        /// <summary>Debounce: coalesce rapid BGM property changes into a single sync after idle period.</summary>
        private void DebouncedSyncBgm()
        {
            _bgmSyncDebounce?.Cancel();
            _bgmSyncDebounce?.Dispose();
            _bgmSyncDebounce = new CancellationTokenSource();
            var token = _bgmSyncDebounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(BgmSyncDebounceMs, token);
                    if (!token.IsCancellationRequested)
                    {
                        var app = Application.Current;
                        app?.Dispatcher.Invoke(() => SyncBgmToProject());
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        /// <summary>
        /// Get the first active BGM track for render pipeline consumption.
        /// Returns null if no BGM is configured or disabled.
        /// </summary>
        public BgmTrack? GetActiveBgmTrack()
        {
            if (!BgmIsEnabled || string.IsNullOrWhiteSpace(BgmFilePath)) return null;
            return new BgmTrack
            {
                AudioPath      = BgmFilePath,
                Volume         = BgmVolume,
                FadeInSeconds  = BgmFadeIn,
                FadeOutSeconds = BgmFadeOut,
                IsLooping      = BgmIsLooping,
                IsEnabled      = BgmIsEnabled
            };
        }
    }
}
