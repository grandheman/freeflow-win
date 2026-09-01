using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeFlow.Core.Models;
using FreeFlow.Core.Net;
using FreeFlow.Core.Text;

namespace FreeFlow.Core.PostProcessing;

public enum PostProcessingFailure
{
    RequestFailed,
    RateLimited,
    InvalidResponse,
    InvalidInput,
    EmptyOutput,
    RequestTimedOut,
    SuspectedInstructionExecution,
}

public sealed class PostProcessingException : Exception
{
    public PostProcessingFailure Failure { get; }
    public int StatusCode { get; }
    public string? Model { get; }
    public double RetryAfterSeconds { get; }

    public PostProcessingException(
        PostProcessingFailure failure,
        string message,
        int statusCode = 0,
        string? model = null,
        double retryAfterSeconds = 0)
        : base(message)
    {
        Failure = failure;
        StatusCode = statusCode;
        Model = model;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public static PostProcessingException RequestFailed(int statusCode, string details)
        => new(PostProcessingFailure.RequestFailed,
            $"Post-processing failed with status {statusCode}: {details}", statusCode);

    public static PostProcessingException RateLimited(string model, double retryAfter)
        => new(PostProcessingFailure.RateLimited,
            $"Model {model} rate-limited, retry in {(int)retryAfter}s",
            model: model, retryAfterSeconds: retryAfter);

    public static PostProcessingException InvalidResponse(string details)
        => new(PostProcessingFailure.InvalidResponse, $"Invalid post-processing response: {details}");

    public static PostProcessingException InvalidInput(string details)
        => new(PostProcessingFailure.InvalidInput, $"Invalid post-processing input: {details}");

    public static PostProcessingException EmptyOutput()
        => new(PostProcessingFailure.EmptyOutput, "Post-processing returned empty output");

    public static PostProcessingException RequestTimedOut(double seconds)
        => new(PostProcessingFailure.RequestTimedOut, $"Post-processing timed out after {(int)seconds}s");

    public static PostProcessingException SuspectedInstructionExecution()
        => new(PostProcessingFailure.SuspectedInstructionExecution,
            "Post-processing output looked like it answered the transcript instead of cleaning it");
}

public sealed record PostProcessingResult(string Transcript, string Prompt);

public sealed record PostProcessingOptions
{
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.groq.com/openai/v1";
    public string PreferredModel { get; init; } = "";
    public string PreferredFallbackModel { get; init; } = "";
    public bool InstructionExecutionGuardEnabled { get; init; } = true;
    public double TimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;
}

/// <summary>
/// Transcript cleanup, Edit Mode transforms, and verbatim translation against any
/// OpenAI-compatible chat-completions endpoint.
/// </summary>
/// <remarks>Ported from <c>Sources/PostProcessingService.swift</c>.</remarks>
public sealed class PostProcessingService
{
    private const string DefaultModel = "openai/gpt-oss-20b";
    private const string DefaultFallbackModel = "qwen/qwen3.6-27b";
    private const string DefaultModelReasoningEffort = "low";
    private const int PostProcessingMaxCompletionTokens = 4096;

    private readonly PostProcessingOptions _options;
    private readonly LlmCooldownManager _cooldowns;
    private readonly HttpClient _client;

    private readonly string _preferredModel;
    private readonly string _preferredFallbackModel;

    public PostProcessingService(
        PostProcessingOptions options,
        LlmCooldownManager cooldowns,
        HttpClient? client = null)
    {
        _options = options;
        _cooldowns = cooldowns;
        _client = client ?? LlmApiTransport.Client;
        _preferredModel = options.PreferredModel.Trim();
        _preferredFallbackModel = options.PreferredFallbackModel.Trim();
    }

    private double TimeoutSeconds => TimeoutSettings.Normalize(_options.TimeoutSeconds);

    // MARK: Public entry points

    /// <summary>Cleans a raw transcript for pasting.</summary>
    public Task<PostProcessingResult> PostProcessAsync(
        string transcript,
        string contextSummary,
        string customVocabulary,
        string customSystemPrompt = "",
        string outputLanguage = "",
        CancellationToken cancellationToken = default)
    {
        var vocabularyTerms = MergedVocabularyTerms(customVocabulary);

        return WithTimeoutAsync(
            token => ProcessWithFallbackAsync(
                transcript, contextSummary, vocabularyTerms, customSystemPrompt, outputLanguage, token),
            cancellationToken);
    }

    /// <summary>Transforms selected text according to a spoken instruction (Edit Mode).</summary>
    public Task<PostProcessingResult> TransformSelectionAsync(
        string selectedText,
        string voiceCommand,
        string contextSummary,
        string customVocabulary,
        string outputLanguage = "",
        CancellationToken cancellationToken = default)
    {
        var vocabularyTerms = MergedVocabularyTerms(customVocabulary);

        return WithTimeoutAsync(
            token => ProcessCommandTransformWithFallbackAsync(
                selectedText, voiceCommand, contextSummary, vocabularyTerms, outputLanguage, token),
            cancellationToken);
    }

    /// <summary>
    /// Translates a transcript without any of the polishing the cleanup pipeline applies.
    /// </summary>
    /// <remarks>
    /// Used by the "preserve exact wording" path when an output language is also set:
    /// skipping the LLM entirely there would silently drop the translation, so requests
    /// route through a minimal translate-only prompt instead.
    /// </remarks>
    public Task<PostProcessingResult> TranslateVerbatimAsync(
        string transcript,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var trimmedTranscript = transcript.Trim();
        if (trimmedTranscript.Length == 0)
        {
            throw PostProcessingException.InvalidInput("Transcript must not be empty");
        }

        var trimmedLanguage = targetLanguage.Trim();
        if (trimmedLanguage.Length == 0)
        {
            throw PostProcessingException.InvalidInput("Target language must not be empty");
        }

        return WithTimeoutAsync(
            token => TranslateVerbatimWithFallbackAsync(trimmedTranscript, trimmedLanguage, token),
            cancellationToken);
    }

    // MARK: Model selection and fallback

    private string ResolvedPrimaryModel()
        => _preferredModel.Length == 0 ? DefaultModel : _preferredModel;

    /// <summary>The other model to try once, or null when there is no sensible second choice.</summary>
    private string? ResolvedRetryModel(string primaryModel)
    {
        if (_preferredFallbackModel.Length > 0)
        {
            return _preferredFallbackModel == primaryModel ? null : _preferredFallbackModel;
        }
        if (primaryModel == DefaultModel) return DefaultFallbackModel;
        if (primaryModel == DefaultFallbackModel) return DefaultModel;
        return null;
    }

    /// <summary>
    /// Cleanup path. Retries on the other model for rate limits, empty output, and a
    /// tripped instruction guard; when the guard trips again there is nothing safe to
    /// paste but the raw transcript, so that is what comes back.
    /// </summary>
    private async Task<PostProcessingResult> ProcessWithFallbackAsync(
        string transcript,
        string contextSummary,
        IReadOnlyList<string> vocabularyTerms,
        string customSystemPrompt,
        string outputLanguage,
        CancellationToken cancellationToken)
    {
        var primaryModel = ResolvedPrimaryModel();
        var retryModel = ResolvedRetryModel(primaryModel);

        // Circuit breaker: skip a request that is certain to fail.
        var availableModel = _cooldowns.EffectivePrimary(primaryModel, retryModel);
        if (availableModel is null) throw PostProcessingException.RateLimited(primaryModel, 0);
        primaryModel = availableModel;

        Task<PostProcessingResult> Attempt(string model, CancellationToken token)
            => ProcessAsync(
                transcript, contextSummary, model, vocabularyTerms,
                customSystemPrompt, outputLanguage, token);

        PostProcessingResult RawTranscriptSafeExit()
            => new(transcript.Trim(), "");

        try
        {
            return await Attempt(primaryModel, cancellationToken).ConfigureAwait(false);
        }
        catch (PostProcessingException error)
        {
            var shouldFallback = error.Failure switch
            {
                PostProcessingFailure.RateLimited => true,
                PostProcessingFailure.RequestFailed => error.StatusCode == 429,
                PostProcessingFailure.EmptyOutput => true,
                PostProcessingFailure.SuspectedInstructionExecution => true,
                _ => false,
            };

            if (!shouldFallback) throw;
            if (retryModel is null) throw;

            if (primaryModel == retryModel)
            {
                // No distinct model left. Still honor the safe exit so an up-front
                // cooldown swap does not lose it.
                if (error.Failure == PostProcessingFailure.SuspectedInstructionExecution)
                {
                    return RawTranscriptSafeExit();
                }
                throw;
            }

            try
            {
                return await Attempt(retryModel, cancellationToken).ConfigureAwait(false);
            }
            catch (PostProcessingException retryError)
                when (retryError.Failure == PostProcessingFailure.SuspectedInstructionExecution)
            {
                return RawTranscriptSafeExit();
            }
        }
    }

    /// <summary>
    /// Edit Mode path. Falls back only on rate limits and empty output, and returns the
    /// selection untouched rather than destroying it when both models are cooling down.
    /// </summary>
    private async Task<PostProcessingResult> ProcessCommandTransformWithFallbackAsync(
        string selectedText,
        string voiceCommand,
        string contextSummary,
        IReadOnlyList<string> vocabularyTerms,
        string outputLanguage,
        CancellationToken cancellationToken)
    {
        var primaryModel = ResolvedPrimaryModel();
        var retryModel = ResolvedRetryModel(primaryModel);

        var availableModel = _cooldowns.EffectivePrimary(primaryModel, retryModel);
        if (availableModel is null) return new PostProcessingResult(selectedText, "");
        primaryModel = availableModel;

        Task<PostProcessingResult> Attempt(string model, CancellationToken token)
            => ProcessCommandTransformAsync(
                selectedText, voiceCommand, contextSummary, model, vocabularyTerms, outputLanguage, token);

        try
        {
            return await Attempt(primaryModel, cancellationToken).ConfigureAwait(false);
        }
        catch (PostProcessingException error)
        {
            var shouldFallback = error.Failure is
                PostProcessingFailure.RateLimited or PostProcessingFailure.EmptyOutput;

            if (!shouldFallback) throw;
            if (retryModel is null || primaryModel == retryModel) throw;

            return await Attempt(retryModel, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verbatim translation path. Deliberately does not consult the circuit breaker,
    /// and falls back only on an explicit 429 status or empty output.
    /// </summary>
    private async Task<PostProcessingResult> TranslateVerbatimWithFallbackAsync(
        string transcript,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var primaryModel = ResolvedPrimaryModel();
        var retryModel = ResolvedRetryModel(primaryModel);

        try
        {
            return await TranslateVerbatimOnceAsync(
                transcript, targetLanguage, primaryModel, cancellationToken).ConfigureAwait(false);
        }
        catch (PostProcessingException error)
        {
            var shouldFallback = error.Failure switch
            {
                PostProcessingFailure.RequestFailed => error.StatusCode == 429,
                PostProcessingFailure.EmptyOutput => true,
                _ => false,
            };

            if (!shouldFallback || retryModel is null) throw;

            return await TranslateVerbatimOnceAsync(
                transcript, targetLanguage, retryModel, cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: Individual requests

    private async Task<PostProcessingResult> ProcessAsync(
        string transcript,
        string contextSummary,
        string model,
        IReadOnlyList<string> vocabularyTerms,
        string customSystemPrompt,
        string outputLanguage,
        CancellationToken cancellationToken)
    {
        var systemPrompt = customSystemPrompt.Trim().Length == 0
            ? Prompts.DefaultSystemPrompt
            : customSystemPrompt;

        var trimmedOutputLanguage = outputLanguage.Trim();
        if (trimmedOutputLanguage.Length > 0)
        {
            systemPrompt = Prompts.ApplyOutputLanguage(systemPrompt, trimmedOutputLanguage);
        }
        systemPrompt = AppendVocabulary(systemPrompt, vocabularyTerms);

        var userMessage = Prompts.CleanupUserMessage(contextSummary, transcript);
        var promptForDisplay = Prompts.ForDisplay(model, systemPrompt, userMessage);

        var content = await SendChatCompletionAsync(
            model, systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);

        var sanitized = TranscriptOutputSanitizer.PostProcessedTranscript(content);

        if (_options.InstructionExecutionGuardEnabled &&
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                transcript, sanitized, outputLanguage))
        {
            throw PostProcessingException.SuspectedInstructionExecution();
        }

        return new PostProcessingResult(sanitized, promptForDisplay);
    }

    private async Task<PostProcessingResult> ProcessCommandTransformAsync(
        string selectedText,
        string voiceCommand,
        string contextSummary,
        string model,
        IReadOnlyList<string> vocabularyTerms,
        string outputLanguage,
        CancellationToken cancellationToken)
    {
        var systemPrompt = Prompts.CommandModeSystemPrompt;

        var trimmedOutputLanguage = outputLanguage.Trim();
        if (trimmedOutputLanguage.Length > 0)
        {
            systemPrompt = systemPrompt.Replace(
                Prompts.CommandModeLanguageLine,
                $"- Output the result in {trimmedOutputLanguage}.",
                StringComparison.Ordinal);
        }
        systemPrompt = AppendVocabulary(systemPrompt, vocabularyTerms);

        var userMessage = Prompts.CommandUserMessage(contextSummary, voiceCommand, selectedText);
        var promptForDisplay = Prompts.ForDisplay(model, systemPrompt, userMessage);

        var content = await SendChatCompletionAsync(
            model, systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);

        // Edit Mode keeps surrounding quotes, since they may be part of the replacement.
        return new PostProcessingResult(
            TranscriptOutputSanitizer.CommandModeTranscript(content), promptForDisplay);
    }

    private async Task<PostProcessingResult> TranslateVerbatimOnceAsync(
        string transcript,
        string targetLanguage,
        string model,
        CancellationToken cancellationToken)
    {
        var systemPrompt = Prompts.VerbatimTranslationSystemPrompt(targetLanguage);
        var userMessage = Prompts.VerbatimTranslationUserMessage(targetLanguage, transcript);
        var promptForDisplay = Prompts.ForDisplay(model, systemPrompt, userMessage);

        var content = await SendChatCompletionAsync(
            model, systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);

        return new PostProcessingResult(
            TranscriptOutputSanitizer.VerbatimTranslation(content), promptForDisplay);
    }

    /// <summary>
    /// Sends one chat-completion request and returns the assistant content.
    /// </summary>
    /// <remarks>
    /// Registers a cooldown on 429 before throwing, so both the primary and the
    /// fallback attempt feed the circuit breaker.
    /// </remarks>
    private async Task<string> SendChatCompletionAsync(
        string model,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var config = ModelConfiguration.Config(model);
        var payload = BuildPayload(model, systemPrompt, userMessage, config);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.BaseUrl}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var cooldown = LlmCooldownManager.RateLimitCooldown(response.Headers);
            _cooldowns.SetCooldown(model, cooldown.Seconds, cooldown.IsDaily);
            throw PostProcessingException.RateLimited(model, cooldown.Seconds);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw PostProcessingException.RequestFailed((int)response.StatusCode, body);
        }

        var content = ExtractMessageContent(body);

        if (config.ShouldStripThinkTags)
        {
            content = ModelConfiguration.StripThinkTags(content);
        }

        if (content.Trim().Length == 0) throw PostProcessingException.EmptyOutput();

        return content;
    }

    private static string ExtractMessageContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            throw PostProcessingException.InvalidResponse("Response body was not JSON");
        }

        throw PostProcessingException.InvalidResponse("Missing choices[0].message.content");
    }

    private string BuildPayload(string model, string systemPrompt, string userMessage, ModelConfig config)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("temperature", 0.0);

            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", systemPrompt);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", userMessage);
            writer.WriteEndObject();
            writer.WriteEndArray();

            // Model-specific tuning, with the default model's values as a backstop.
            if (config.MaxCompletionTokens is { } maxTokens)
                writer.WriteNumber("max_completion_tokens", maxTokens);
            else if (model == DefaultModel)
                writer.WriteNumber("max_completion_tokens", PostProcessingMaxCompletionTokens);

            if (config.ReasoningEffort is { } effort)
                writer.WriteString("reasoning_effort", effort);
            else if (model == DefaultModel)
                writer.WriteString("reasoning_effort", DefaultModelReasoningEffort);

            if (config.IncludeReasoning is { } include)
                writer.WriteBoolean("include_reasoning", include);
            else if (model == DefaultModel)
                writer.WriteBoolean("include_reasoning", false);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // MARK: Helpers

    private async Task<PostProcessingResult> WithTimeoutAsync(
        Func<CancellationToken, Task<PostProcessingResult>> work,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = TimeoutSeconds;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await work(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostProcessingException.RequestTimedOut(timeoutSeconds);
        }
    }

    private static string AppendVocabulary(string systemPrompt, IReadOnlyList<string> vocabularyTerms)
    {
        var normalized = NormalizedVocabularyText(vocabularyTerms);
        return normalized.Length == 0
            ? systemPrompt
            : systemPrompt + "\n\n" + Prompts.VocabularyPrompt(normalized);
    }

    /// <summary>Splits raw vocabulary input on newlines, commas, and semicolons, de-duplicated case-insensitively.</summary>
    internal static IReadOnlyList<string> MergedVocabularyTerms(string rawVocabulary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return rawVocabulary
            .Split('\n', ',', ';')
            .Select(term => term.Trim())
            .Where(term => term.Length > 0 && seen.Add(term))
            .ToList();
    }

    internal static string NormalizedVocabularyText(IReadOnlyList<string> vocabularyTerms)
        => string.Join(", ", vocabularyTerms.Select(term => term.Trim()).Where(term => term.Length > 0));
}
