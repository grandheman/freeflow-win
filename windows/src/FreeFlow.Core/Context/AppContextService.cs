using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeFlow.Core.Models;
using FreeFlow.Core.Net;
using FreeFlow.Core.PostProcessing;

namespace FreeFlow.Core.Context;

/// <summary>Screenshot payload handed to the context model.</summary>
public sealed record ScreenshotCapture(string? DataUrl, string? MimeType, string? Error)
{
    public static readonly ScreenshotCapture None = new(null, null, null);
}

public sealed record AppContextOptions
{
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.groq.com/openai/v1";
    public string CustomContextPrompt { get; init; } = "";
    public string ContextModel { get; init; } = DefaultContextModel;
    public double TimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;

    public const string DefaultContextModel = "qwen/qwen3.6-27b";
}

/// <summary>
/// Turns foreground-app metadata and an optional screenshot into a two-sentence
/// description of what the user is doing.
/// </summary>
/// <remarks>
/// <para>
/// The result is used purely as a spelling and formatting hint for the cleanup
/// prompt. Every failure path degrades to a neutral fallback sentence rather than
/// throwing, because bad context must never block a dictation.
/// </para>
/// <para>
/// Ported from <c>Sources/AppContextService.swift</c>. The platform-specific parts
/// (reading the foreground window, grabbing the screenshot) are supplied by the
/// caller, which keeps this class testable and OS-independent.
/// </para>
/// </remarks>
public sealed class AppContextService
{
    public const string DefaultContextPrompt = Prompts.DefaultContextPrompt;
    public const string DefaultContextPromptDate = Prompts.DefaultContextPromptDate;
    public const int DefaultScreenshotMaxDimension = 1024;

    private readonly AppContextOptions _options;
    private readonly HttpClient _client;
    private readonly string _contextModel;

    public AppContextService(AppContextOptions options, HttpClient? client = null)
    {
        _options = options;
        _client = client ?? LlmApiTransport.Client;

        var trimmedModel = options.ContextModel.Trim();
        _contextModel = trimmedModel.Length == 0 ? AppContextOptions.DefaultContextModel : trimmedModel;
    }

    private string ResolveContextPrompt()
    {
        var trimmed = _options.CustomContextPrompt.Trim();
        return trimmed.Length == 0 ? DefaultContextPrompt : trimmed;
    }

    /// <summary>
    /// Builds the context for a dictation.
    /// </summary>
    /// <param name="snapshot">Foreground-app metadata gathered by the platform layer.</param>
    /// <param name="screenshot">Optional screenshot, or <see cref="ScreenshotCapture.None"/>.</param>
    public async Task<DictationContext> CollectContextAsync(
        AppSelectionSnapshot snapshot,
        ScreenshotCapture screenshot,
        CancellationToken cancellationToken = default)
    {
        var contextSystemPrompt = ResolveContextPrompt();

        if (snapshot.AppName is null && snapshot.WindowTitle is null)
        {
            return new DictationContext(
                null, null, null, null,
                "You are dictating in an unrecognized context.",
                contextSystemPrompt, null, null, null,
                "No foreground application");
        }

        var windowTitle = snapshot.WindowTitle ?? snapshot.AppName;

        string currentActivity;
        string? contextPrompt = null;

        if (_options.ApiKey.Trim().Length > 0)
        {
            var inferred = await InferActivityAsync(
                snapshot.AppName, snapshot.ApplicationId, windowTitle, snapshot.SelectedText,
                screenshot.DataUrl, contextSystemPrompt, cancellationToken).ConfigureAwait(false);

            if (inferred is not null)
            {
                currentActivity = inferred.Value.Activity;
                contextPrompt = inferred.Value.Prompt;
            }
            else
            {
                currentActivity = FallbackCurrentActivity(snapshot.AppName, screenshot.DataUrl is not null);
            }
        }
        else
        {
            currentActivity = FallbackCurrentActivity(snapshot.AppName, screenshot.DataUrl is not null);
        }

        return new DictationContext(
            snapshot.AppName,
            snapshot.ApplicationId,
            windowTitle,
            snapshot.SelectedText,
            currentActivity,
            contextSystemPrompt,
            contextPrompt,
            screenshot.DataUrl,
            screenshot.MimeType,
            screenshot.Error);
    }

    /// <summary>
    /// Attempts inference with the screenshot, then retries without it.
    /// </summary>
    /// <remarks>
    /// The retry matters because a non-vision model, or an image the provider rejects,
    /// fails the whole request. Metadata-only context is still useful.
    /// </remarks>
    private async Task<(string Activity, string Prompt)?> InferActivityAsync(
        string? appName,
        string? applicationId,
        string? windowTitle,
        string? selectedText,
        string? screenshotDataUrl,
        string contextSystemPrompt,
        CancellationToken cancellationToken)
    {
        var attempts = screenshotDataUrl is not null
            ? new[] { screenshotDataUrl, null }
            : new string?[] { null };

        foreach (var attempt in attempts)
        {
            var inferred = await InferActivityOnceAsync(
                appName, applicationId, windowTitle, selectedText,
                attempt, contextSystemPrompt, _contextModel, cancellationToken).ConfigureAwait(false);

            if (inferred is not null) return inferred;
        }

        return null;
    }

    private async Task<(string Activity, string Prompt)?> InferActivityOnceAsync(
        string? appName,
        string? applicationId,
        string? windowTitle,
        string? selectedText,
        string? screenshotDataUrl,
        string contextSystemPrompt,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata =
                $"App: {appName ?? "Unknown"}\n" +
                $"Application ID: {applicationId ?? "Unknown"}\n" +
                $"Window: {windowTitle ?? "Unknown"}\n" +
                $"Selected text: {selectedText ?? "None"}";

            var textOnlyPrompt =
                "Analyze the context and infer the user's current activity in exactly two sentences.\n\n" + metadata;

            var userMessageDescription = screenshotDataUrl is not null
                ? "[screenshot attached]\nAnalyze the screenshot plus metadata to infer current activity.\n" + metadata
                : textOnlyPrompt;

            var fullPrompt =
                $"Model: {model}\n\n[System]\n{contextSystemPrompt}\n[User]\n{userMessageDescription}";

            var payload = BuildPayload(
                model, contextSystemPrompt, metadata, textOnlyPrompt, screenshotDataUrl);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl}/chat/completions");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(TimeoutSettings.Normalize(_options.TimeoutSeconds)));

            using var response = await _client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            var content = ExtractContent(body);
            if (content is null) return null;

            var activity = ActivitySummary(content, model);
            return activity is null ? null : (activity, fullPrompt);
        }
        catch (Exception)
        {
            // Context is an optimization. Any failure falls back to a neutral summary.
            return null;
        }
    }

    private static string BuildPayload(
        string model,
        string contextSystemPrompt,
        string metadata,
        string textOnlyPrompt,
        string? screenshotDataUrl)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            // Slightly above zero: this is a summary, not text destined for the clipboard.
            writer.WriteNumber("temperature", 0.2);

            writer.WriteStartArray("messages");

            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", contextSystemPrompt);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("role", "user");

            if (screenshotDataUrl is not null)
            {
                // Multi-part content is the OpenAI vision message shape.
                writer.WriteStartArray("content");

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", "Analyze the screenshot plus metadata to infer current activity.");
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", metadata);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", screenshotDataUrl);
                writer.WriteEndObject();
                writer.WriteEndObject();

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString("content", textOnlyPrompt);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ExtractContent(string body)
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
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Cleans a raw model response into the two-sentence summary the cleanup prompt expects.
    /// </summary>
    public static string? ActivitySummary(string rawContent, string model)
    {
        var content = rawContent;
        if (ModelConfiguration.Config(model).ShouldStripThinkTags)
        {
            content = ModelConfiguration.StripThinkTags(content);
        }

        var cleaned = content.Trim();
        return cleaned.Length == 0 ? null : NormalizedActivitySummary(cleaned);
    }

    /// <summary>
    /// Trims a response back to two sentences.
    /// </summary>
    /// <remarks>
    /// Models overshoot the "exactly two sentences" instruction often enough that the
    /// limit is enforced here rather than trusted. Text already at or under two
    /// sentences is returned untouched, punctuation included.
    /// </remarks>
    private static string NormalizedActivitySummary(string value)
    {
        var sentences = value
            .Split('.', '。', '!', '?')
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        if (sentences.Count <= 2) return value;

        return string.Join(". ", sentences.Take(2)) + ".";
    }

    private static string FallbackCurrentActivity(string? appName, bool screenshotAvailable)
    {
        var activeApp = appName ?? "the active application";
        return screenshotAvailable
            ? $"Could not reliably infer a two-sentence summary for {activeApp} from the screenshot and metadata."
            : $"Could not reliably infer a two-sentence summary for {activeApp} from the visible metadata.";
    }
}
