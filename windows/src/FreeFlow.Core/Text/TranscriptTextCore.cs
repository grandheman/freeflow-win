using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeFlow.Core.Text;

public class TranscriptionResponseParsingException : Exception
{
    public TranscriptionResponseParsingException(string message) : base(message) { }
}

/// <summary>
/// Parses provider transcription responses and suppresses Whisper's stock
/// silence hallucinations.
/// </summary>
/// <remarks>Ported from <c>Sources/TranscriptTextCore.swift</c>.</remarks>
public static class TranscriptionResponseParser
{
    /// <summary>
    /// Whisper emits these stock phrases for silence or background noise. They are
    /// only suppressed when segment metadata independently reports a high probability
    /// of no speech, which protects genuine short dictations.
    /// </summary>
    private static readonly HashSet<string> HallucinationPhrases = new(StringComparer.Ordinal)
    {
        "thank you",
        "thank you for watching",
        "thank you very much",
        "thank you so much",
        "thanks for watching",
        "please subscribe",
        "like and subscribe",
        "subtitles by",
        "subtitles by the amara.org community",
        "you",
    };

    /// <summary>
    /// Tuned conservatively upstream against roughly 500 quiet, noisy, and real-speech
    /// samples to minimize the chance of filtering genuine short dictations.
    /// </summary>
    private const double HallucinationNoSpeechThreshold = 0.1;

    /// <summary>
    /// Parses a raw response body. Takes bytes rather than a string so that
    /// undecodable payloads are rejected the same way the macOS build rejects them.
    /// </summary>
    public static string Parse(byte[] data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            // A body that is not JSON at all is not a transcript. This also covers
            // empty, whitespace-only, and undecodable payloads.
            throw new TranscriptionResponseParsingException("Invalid response");
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString() ?? string.Empty;
                return IsHallucination(text, document.RootElement) ? string.Empty : text;
            }
        }

        // Valid JSON that carries no "text" field. Some OpenAI-compatible providers
        // return the transcript as a bare string body, so fall back to the raw text.
        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new TranscriptionResponseParsingException("Invalid response");
        }

        // Lines are joined without individually trimming them, matching the
        // upstream behavior that the parser tests pin down.
        var plainText = string.Join(" ", decoded.Split('\n', '\r')).Trim();

        if (plainText.Length == 0)
        {
            throw new TranscriptionResponseParsingException("Invalid response");
        }

        return plainText;
    }

    private static bool IsHallucination(string text, JsonElement root)
    {
        var normalized = TrimPunctuationAndWhitespace(text.ToLowerInvariant());
        if (!HallucinationPhrases.Contains(normalized)) return false;

        // Without segment metadata there is no independent signal, so keep the text.
        if (!root.TryGetProperty("segments", out var segments) ||
            segments.ValueKind != JsonValueKind.Array ||
            segments.GetArrayLength() == 0)
        {
            return false;
        }

        var firstSegment = segments[0];
        if (firstSegment.ValueKind != JsonValueKind.Object ||
            !firstSegment.TryGetProperty("no_speech_prob", out var noSpeechElement) ||
            noSpeechElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return noSpeechElement.GetDouble() >= HallucinationNoSpeechThreshold;
    }

    private static string TrimPunctuationAndWhitespace(string value)
    {
        static bool IsTrimmable(char c)
            => char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) switch
            {
                UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation => true,
                _ => false,
            };

        var start = 0;
        var end = value.Length;
        while (start < end && IsTrimmable(value[start])) start++;
        while (end > start && IsTrimmable(value[end - 1])) end--;
        return value.Substring(start, end - start);
    }
}

/// <summary>
/// Normalizes model output before it reaches the clipboard.
/// </summary>
/// <remarks>Ported from <c>Sources/TranscriptTextCore.swift</c>.</remarks>
public static class TranscriptOutputSanitizer
{
    /// <summary>
    /// Verbatim translation deliberately preserves the cleanup prompt's EMPTY
    /// sentinel, because "empty" can be legitimate translated speech here.
    /// </summary>
    public static string VerbatimTranslation(string value)
    {
        var result = value.Trim();
        return result.Length == 0 ? string.Empty : StripSurroundingQuotes(result);
    }

    public static string PostProcessedTranscript(string value)
    {
        var result = value.Trim();
        if (result.Length == 0) return string.Empty;

        result = StripSurroundingQuotes(result);

        // The cleanup prompt returns this sentinel when the transcript was only filler.
        return result == "EMPTY" ? string.Empty : result;
    }

    public static string CommandModeTranscript(string value) => value.Trim();

    private static string StripSurroundingQuotes(string value)
    {
        if (value.Length > 1 && value.StartsWith("\"", StringComparison.Ordinal) &&
            value.EndsWith("\"", StringComparison.Ordinal))
        {
            return value.Substring(1, value.Length - 2).Trim();
        }
        return value;
    }

    private static readonly Regex AssistantPreamblePattern = new(
        @"^\s*(sure|certainly|absolutely|here(?:'s| is)|i(?:'d| would) be happy to|i can)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> InstructionMarkers = new(StringComparer.Ordinal)
    {
        "ask", "answer", "compose", "create", "draft", "email", "generate", "make",
        "message", "prompt", "reply", "respond", "response", "summarize", "tell",
        "translate", "write", "claude", "chatgpt", "ai", "llm",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "could",
        "for", "from", "had", "has", "have", "he", "her", "him", "his", "i", "if",
        "in", "into", "is", "it", "its", "just", "me", "my", "of", "on", "or", "our",
        "please", "she", "so", "that", "the", "their", "them", "then", "there", "this",
        "to", "um", "uh", "was", "we", "were", "what", "when", "where", "who", "with",
        "would", "you", "your",
    };

    /// <summary>
    /// Heuristic guard against a model answering the transcript instead of cleaning it.
    /// </summary>
    /// <remarks>
    /// Only applies when no output language is configured, because translation
    /// legitimately destroys token overlap with the source.
    /// </remarks>
    public static bool AppearsToHaveExecutedInstruction(
        string rawTranscript,
        string cleanedTranscript,
        string outputLanguage)
    {
        if (outputLanguage.Trim().Length != 0) return false;

        var rawTokens = SignificantTokens(rawTranscript);
        var cleanedTokens = SignificantTokens(cleanedTranscript);
        if (rawTokens.Count == 0 || cleanedTokens.Count == 0) return false;

        var rawMarkers = rawTokens.Intersect(InstructionMarkers).ToHashSet();
        if (rawMarkers.Count == 0) return false;

        var preservedMarkers = rawMarkers.Intersect(cleanedTokens).ToHashSet();
        var overlap = rawTokens.Intersect(cleanedTokens).Count();
        var overlapRatio = (double)overlap / Math.Max(rawTokens.Count, 1);

        var cleanedHasAssistantPreamble = AssistantPreamblePattern.IsMatch(cleanedTranscript);
        var rawHasSamePreamble = AssistantPreamblePattern.IsMatch(rawTranscript);

        return (cleanedHasAssistantPreamble && !rawHasSamePreamble)
            || (preservedMarkers.Count == 0 && overlapRatio < 0.35);
    }

    private static HashSet<string> SignificantTokens(string text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetter(character) || char.IsDigit(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());

        return parts.Where(token => token.Length > 1 && !StopWords.Contains(token)).ToHashSet();
    }
}
