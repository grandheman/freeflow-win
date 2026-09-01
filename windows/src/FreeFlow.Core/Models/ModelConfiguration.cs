using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FreeFlow.Core.Models;

/// <summary>Per-model request tuning. Null members are omitted from the request payload.</summary>
public sealed record ModelConfig(
    int? MaxCompletionTokens,
    string? ReasoningEffort,
    bool? IncludeReasoning,
    bool ShouldStripThinkTags)
{
    /// <summary>Defaults for any model not explicitly listed.</summary>
    public static readonly ModelConfig Generic = new(null, null, null, false);
}

/// <summary>
/// Known models and their request quirks.
/// </summary>
/// <remarks>Ported from <c>Sources/ModelConfiguration.swift</c>.</remarks>
public static class ModelConfiguration
{
    public static readonly IReadOnlyList<string> LlmModels = new[]
    {
        "openai/gpt-oss-20b",
        "openai/gpt-oss-120b",
        "openai/gpt-oss-safeguard-20b",
        "qwen/qwen3.6-27b",
        "groq/compound",
        "groq/compound-mini",
    };

    /// <summary>
    /// Models that accept image input. The context model must support vision for
    /// screenshot analysis to work.
    /// </summary>
    public static readonly IReadOnlyList<string> VisionModels = new[]
    {
        "qwen/qwen3.6-27b",
    };

    public static readonly IReadOnlyList<string> TranscriptionModels = new[]
    {
        "whisper-large-v3",
        "whisper-large-v3-turbo",
    };

    /// <summary>Providerless aliases accepted in settings, mapped to their canonical ids.</summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["qwen3-32b"] = "qwen/qwen3-32b",
        ["qwen3.6-27b"] = "qwen/qwen3.6-27b",
        ["gpt-oss-20b"] = "openai/gpt-oss-20b",
        ["gpt-oss-120b"] = "openai/gpt-oss-120b",
        ["gpt-oss-safeguard-20b"] = "openai/gpt-oss-safeguard-20b",
    };

    /// <summary>
    /// Only models needing non-default handling appear here. Everything else,
    /// listed or not, uses <see cref="ModelConfig.Generic"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ModelConfig> Configs =
        new Dictionary<string, ModelConfig>
        {
            ["openai/gpt-oss-20b"] = new(MaxCompletionTokens: 4096, ReasoningEffort: "low",
                IncludeReasoning: false, ShouldStripThinkTags: false),
            // Emits reasoning inside think tags that must be stripped before pasting.
            ["qwen/qwen3-32b"] = new(null, null, null, ShouldStripThinkTags: true),
            ["qwen/qwen3.6-27b"] = new(null, ReasoningEffort: "none",
                IncludeReasoning: false, ShouldStripThinkTags: true),
        };

    public static ModelConfig Config(string model)
    {
        var cleanModel = model.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(cleanModel, out var canonical)) cleanModel = canonical;
        return Configs.TryGetValue(cleanModel, out var config) ? config : ModelConfig.Generic;
    }

    // Multiple consecutive closed think blocks at the start of the output.
    private static readonly Regex ClosedThinkTags = new(
        @"^(?:\s*<think>[\s\S]*?</think>)+", RegexOptions.Compiled);

    // An unclosed think tag, which means the model was truncated mid-reasoning.
    private static readonly Regex UnclosedThinkTag = new(
        @"^\s*<think>[\s\S]*$", RegexOptions.Compiled);

    /// <summary>
    /// Removes leading think blocks, tolerating an unclosed tag left behind when
    /// the model runs out of completion tokens.
    /// </summary>
    public static string StripThinkTags(string text)
    {
        var cleaned = ClosedThinkTags.Replace(text, string.Empty);
        cleaned = UnclosedThinkTag.Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }
}
