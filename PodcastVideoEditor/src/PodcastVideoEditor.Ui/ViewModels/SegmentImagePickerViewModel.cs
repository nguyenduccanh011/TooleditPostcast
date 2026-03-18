#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastVideoEditor.Core.Models;
using PodcastVideoEditor.Core.Services.AI;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PodcastVideoEditor.Ui.ViewModels;

/// <summary>
/// ViewModel for the "Ảnh" tab in SegmentEditorPanel.
/// Manages fetching, displaying, and applying image candidates for a Visual segment.
/// </summary>
public partial class SegmentImagePickerViewModel : ObservableObject, IDisposable
{
    private readonly IAIImageSelectionService _imageSelection;
    private readonly ProjectViewModel _projectViewModel;
    private CancellationTokenSource? _fetchCts;
    private bool _disposed;

    public Segment Segment { get; }

    public SegmentImagePickerViewModel(
        Segment segment,
        IAIImageSelectionService imageSelection,
        ProjectViewModel projectViewModel)
    {
        Segment         = segment;
        _imageSelection = imageSelection;
        _projectViewModel = projectViewModel;

        // Build initial search query from keywords
        var keywords = ParseKeywords(segment.Keywords);
        Keywords        = new ObservableCollection<string>(keywords);
        KeywordsDisplay = string.Join(", ", keywords);
        SearchQuery     = string.Join(" ", keywords.Take(3));
    }

    // ── Properties ────────────────────────────────────────────────────────

    [ObservableProperty]
    private string keywordsDisplay = string.Empty;

    public ObservableCollection<string> Keywords { get; } = new();

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ImageCandidate> candidates = new();

    [ObservableProperty]
    private ImageCandidate? selectedCandidate;

    [ObservableProperty]
    private bool isFetching;

    [ObservableProperty]
    private bool isApplying;

    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>True when the segment already has a background image set.</summary>
    public bool HasCurrentImage => !string.IsNullOrEmpty(Segment.BackgroundAssetId);

    // ── Commands ──────────────────────────────────────────────────────────

    /// <summary>Click on a keyword chip → set it as SearchQuery and fetch immediately.</summary>
    [RelayCommand]
    private async Task SearchKeywordAsync(string keyword)
    {
        SearchQuery = keyword;
        await FetchCandidatesAsync();
    }

    [RelayCommand]
    private async Task FetchCandidatesAsync()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        IsFetching  = true;
        StatusText  = "Đang tải ảnh…";
        Candidates.Clear();

        try
        {
            var aiSeg = BuildAISegment();
            var (fetched, _) = await Task.Run(
                () => _imageSelection.FetchCandidatesForSegmentAsync(aiSeg, ct), ct);

            Candidates = new ObservableCollection<ImageCandidate>(fetched);
            StatusText = $"{fetched.Length} ảnh";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã huỷ";
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi: {ex.Message}";
            Log.Warning(ex, "FetchCandidates failed for segment {Id}", Segment.Id);
        }
        finally
        {
            IsFetching = false;
        }
    }

    // Accepts an optional candidate parameter: called from tile click (passes candidate)
    // or from the explicit "Apply" button (passes null → falls back to SelectedCandidate).
    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private async Task ApplySelectedAsync(ImageCandidate? candidate)
    {
        var target = candidate ?? SelectedCandidate;
        if (target == null) return;

        SelectedCandidate = target;
        IsApplying = true;
        StatusText = "Đang tải ảnh về…";

        try
        {
            var assetId = await _projectViewModel.AddImageFromUrlAsync(
                target.Url, target.Id);

            if (assetId != null)
            {
                Segment.BackgroundAssetId = assetId;
                await _projectViewModel.SaveProjectAsync();
                OnPropertyChanged(nameof(HasCurrentImage));
                StatusText = $"✓ Đã áp dụng: {target.Semantic}";
            }
            else
            {
                StatusText = "Lỗi tải ảnh";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi: {ex.Message}";
            Log.Warning(ex, "ApplySelected failed for candidate {Id}", target.Id);
        }
        finally
        {
            IsApplying = false;
        }
    }

    private bool CanApplySelected(ImageCandidate? candidate)
        => (candidate != null || SelectedCandidate != null) && !IsApplying;

    partial void OnSelectedCandidateChanged(ImageCandidate? value)
        => ApplySelectedCommand.NotifyCanExecuteChanged();

    // ── Helpers ───────────────────────────────────────────────────────────

    private AISegment BuildAISegment()
    {
        // Prefer the user-typed SearchQuery over the stored JSON keywords.
        string[] keywords;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            keywords = SearchQuery.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            keywords = ParseKeywords(Segment.Keywords);
        }

        return new AISegment(
            Segment.StartTime,
            Segment.EndTime,
            Segment.Text ?? string.Empty,
            keywords,
            null);
    }

    private static string[] ParseKeywords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = null;
    }
}
