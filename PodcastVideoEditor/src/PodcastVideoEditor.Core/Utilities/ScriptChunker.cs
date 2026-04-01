using System.Text;
using System.Text.RegularExpressions;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Splits a timestamped script into chunks that each fit within a token budget.
/// Each chunk contains complete [start → end] segments — never splits mid-segment.
/// </summary>
public static class ScriptChunker
{
    /// <summary>
    /// Regex that matches a single timestamp line: [0.00 → 6.04] text
    /// </summary>
    private static readonly Regex TimestampLine = new(
        @"^\s*\[\s*\d+\.?\d*\s*→\s*\d+\.?\d*\]\s*",
        RegexOptions.Compiled);

    /// <summary>
    /// Known context-window sizes for popular models (input tokens).
    /// Falls back to a conservative 16 000 for unknown models.
    /// </summary>
    private static readonly Dictionary<string, int> ModelContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"]       = 128_000,
        ["gpt-4o-mini"]  = 128_000,
        ["gpt-4-turbo"]  =  128_000,
        ["gpt-4"]        =   8_192,
        ["gpt-3.5-turbo"] = 16_385,
        ["claude-3-opus"] = 200_000,
        ["claude-3-sonnet"] = 200_000,
        ["claude-3-haiku"]  = 200_000,
        ["claude-3.5-sonnet"] = 200_000,
        ["claude-4-sonnet"] = 200_000,
        ["gemini-1.5-pro"]  = 1_000_000,
        ["gemini-1.5-flash"] = 1_000_000,
        ["deepseek-chat"]    = 64_000,
        ["deepseek-v3"]      = 64_000,
    };

    /// <summary>
    /// Default context window for unknown models.
    /// </summary>
    private const int DefaultContextWindow = 16_000;

    /// <summary>
    /// Lookup the context window size for a model name.
    /// Performs prefix match so "gpt-4o-2024-05-13" matches "gpt-4o".
    /// </summary>
    public static int GetContextWindow(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return DefaultContextWindow;

        // Exact match first
        if (ModelContextWindows.TryGetValue(model, out var exact))
            return exact;

        // Prefix match: "gpt-4o-2024-05-13" starts with "gpt-4o"
        foreach (var (key, value) in ModelContextWindows)
        {
            if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return DefaultContextWindow;
    }

    /// <summary>
    /// Determines if a script needs chunking by comparing estimated prompt tokens
    /// against the model context window, reserving space for system prompt and max output tokens.
    /// </summary>
    /// <param name="script">Raw timestamped script.</param>
    /// <param name="systemPromptTokens">Estimated tokens for the system prompt.</param>
    /// <param name="maxOutputTokens">max_tokens reserved for the response.</param>
    /// <param name="model">Model identifier for context window lookup.</param>
    /// <returns>True when the script should be chunked.</returns>
    public static bool NeedsChunking(string script, int systemPromptTokens, int maxOutputTokens, string? model)
    {
        var budget = GetAvailableInputTokens(systemPromptTokens, maxOutputTokens, model);
        var estimated = TokenEstimator.Estimate(script);
        return estimated > budget;
    }

    /// <summary>
    /// Split a timestamped script into chunks, each fitting within the token budget.
    /// Returns a single-element list if no chunking is needed.
    /// </summary>
    /// <param name="script">Full timestamped script text.</param>
    /// <param name="systemPromptTokens">Tokens consumed by the system prompt.</param>
    /// <param name="maxOutputTokens">Tokens reserved for AI output.</param>
    /// <param name="model">Model name for context window lookup.</param>
    /// <param name="overlapSegments">Number of segments to overlap between chunks for context continuity (default 1).</param>
    /// <returns>List of script chunks. Each chunk is a complete set of timestamp lines.</returns>
    public static List<string> ChunkScript(
        string script,
        int systemPromptTokens,
        int maxOutputTokens,
        string? model,
        int overlapSegments = 1)
    {
        var budget = GetAvailableInputTokens(systemPromptTokens, maxOutputTokens, model);

        // Parse into individual segment lines
        var segmentLines = SplitIntoSegmentLines(script);

        if (segmentLines.Count == 0)
            return [script];

        // If it all fits, return as-is
        var totalTokens = TokenEstimator.Estimate(script);
        if (totalTokens <= budget)
            return [script];

        // Per-chunk overhead (user prompt wrapper text around the script body)
        const int chunkOverhead = 80; // ~80 tokens for prompt wrapper text
        var effectiveBudget = budget - chunkOverhead;
        if (effectiveBudget < 200)
            effectiveBudget = 200; // minimum viable chunk

        var chunks = new List<string>();
        var currentChunk = new StringBuilder();
        var currentTokens = 0;
        var chunkStartIdx = 0;

        for (int i = 0; i < segmentLines.Count; i++)
        {
            var lineTokens = TokenEstimator.Estimate(segmentLines[i]);

            if (currentTokens + lineTokens > effectiveBudget && currentTokens > 0)
            {
                // Flush current chunk
                chunks.Add(currentChunk.ToString().TrimEnd());
                currentChunk.Clear();
                currentTokens = 0;

                // Add overlap: re-include last N segments for context continuity
                var overlapStart = Math.Max(chunkStartIdx, i - overlapSegments);
                for (int j = overlapStart; j < i; j++)
                {
                    currentChunk.AppendLine(segmentLines[j]);
                    currentTokens += TokenEstimator.Estimate(segmentLines[j]);
                }
                chunkStartIdx = i;
            }

            currentChunk.AppendLine(segmentLines[i]);
            currentTokens += lineTokens;
        }

        // Flush remaining
        if (currentChunk.Length > 0)
            chunks.Add(currentChunk.ToString().TrimEnd());

        return chunks;
    }

    /// <summary>
    /// Split script text into individual segment lines.
    /// Lines that don't start with a timestamp are appended to the previous segment.
    /// </summary>
    internal static List<string> SplitIntoSegmentLines(string script)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(script)) return result;

        var lines = script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        StringBuilder? current = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (TimestampLine.IsMatch(trimmed))
            {
                if (current != null)
                    result.Add(current.ToString());
                current = new StringBuilder(trimmed);
            }
            else if (current != null)
            {
                // Continuation line — append to current segment
                current.Append(' ').Append(trimmed);
            }
            // else: text before first timestamp — discard
        }

        if (current != null)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>
    /// Calculate how many tokens are available for the script input in a single chunk.
    /// </summary>
    private static int GetAvailableInputTokens(int systemPromptTokens, int maxOutputTokens, string? model)
    {
        var contextWindow = GetContextWindow(model);
        // Reserve: system prompt + max output + framing overhead (32 tokens)
        var available = contextWindow - systemPromptTokens - maxOutputTokens - 32;
        return Math.Max(available, 500); // never go below 500 tokens
    }
}
