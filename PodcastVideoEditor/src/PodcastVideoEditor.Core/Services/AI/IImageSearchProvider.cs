namespace PodcastVideoEditor.Core.Services.AI;

/// <summary>
/// Provides image candidates from a single stock photo service.
/// </summary>
public interface IImageSearchProvider
{
    string ProviderName { get; }

    /// <summary>
    /// Search for images matching the query.
    /// Returns up to <paramref name="count"/> candidates.
    /// Never throws — returns empty array on any error.
    /// </summary>
    Task<ImageCandidate[]> SearchAsync(string query, int count = 20, CancellationToken ct = default);
}
