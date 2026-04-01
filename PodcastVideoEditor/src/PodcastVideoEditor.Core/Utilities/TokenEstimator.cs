namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Lightweight token-count estimator.
/// Uses the widely-accepted heuristic of ~4 characters per token for English/Vietnamese mixed text.
/// Adds a safety margin so callers can budget conservatively.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Average characters per token (conservative for multilingual text).</summary>
    private const double CharsPerToken = 3.5;

    /// <summary>Safety multiplier applied on top of the raw estimate (10 %).</summary>
    private const double SafetyMargin = 1.10;

    /// <summary>
    /// Estimate the number of tokens a string will consume.
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / CharsPerToken * SafetyMargin);
    }

    /// <summary>
    /// Estimate combined token count of a system prompt + user prompt.
    /// Adds a small overhead for message framing tokens (≈8 per message).
    /// </summary>
    public static int EstimateChat(string systemPrompt, string userPrompt)
        => Estimate(systemPrompt) + Estimate(userPrompt) + 16;
}
