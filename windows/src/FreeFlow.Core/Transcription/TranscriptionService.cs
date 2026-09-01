using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FreeFlow.Core.Net;
using FreeFlow.Core.Text;

namespace FreeFlow.Core.Transcription;

public enum TranscriptionFailure
{
    InvalidBaseUrl,
    UploadFailed,
    SubmissionFailed,
    TranscriptionFailed,
    TranscriptionTimedOut,
    PollFailed,
    AudioPreparationFailed,
}

public sealed class TranscriptionException : Exception
{
    public TranscriptionFailure Failure { get; }

    public TranscriptionException(TranscriptionFailure failure, string message) : base(message)
        => Failure = failure;

    public static TranscriptionException InvalidBaseUrl(string message)
        => new(TranscriptionFailure.InvalidBaseUrl, $"Invalid provider URL: {message}");

    public static TranscriptionException SubmissionFailed(string message)
        => new(TranscriptionFailure.SubmissionFailed, $"Submission failed: {message}");

    public static TranscriptionException TimedOut(double seconds)
        => new(TranscriptionFailure.TranscriptionTimedOut, $"Transcription timed out after {(int)seconds}s");

    public static TranscriptionException PollFailed(string message)
        => new(TranscriptionFailure.PollFailed, $"Polling failed: {message}");
}

public sealed record TranscriptionOptions
{
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.groq.com/openai/v1";
    public string TranscriptionModel { get; init; } = DefaultTranscriptionModel;
    /// <summary>ISO language hint, or null to let the provider detect it.</summary>
    public string? Language { get; init; }
    public double TimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;

    public const string DefaultTranscriptionModel = "whisper-large-v3";
}

/// <summary>
/// Uploads recorded audio to an OpenAI-compatible transcription endpoint.
/// </summary>
/// <remarks>Ported from <c>Sources/TranscriptionService.swift</c>.</remarks>
public sealed class TranscriptionService
{
    /// <summary>
    /// Models that support segment metadata.
    /// </summary>
    /// <remarks>
    /// Only these can return <c>verbose_json</c>, which carries the
    /// <c>no_speech_prob</c> field the hallucination filter depends on. The newer
    /// gpt-4o-transcribe family supports plain JSON only.
    /// </remarks>
    private static readonly HashSet<string> ModelsSupportingVerboseJson = new(StringComparer.Ordinal)
    {
        "whisper-1",
        "whisper-large-v3",
        "whisper-large-v3-turbo",
    };

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _baseUrlHost;
    private readonly string _transcriptionModel;
    private readonly string? _language;
    private readonly double _timeoutSeconds;
    private readonly HttpClient _client;

    public TranscriptionService(TranscriptionOptions options, HttpClient? client = null)
    {
        _apiKey = options.ApiKey;
        _baseUrl = NormalizedBaseUrl(options.BaseUrl);
        _baseUrlHost = new Uri(_baseUrl).Host;

        var trimmedModel = options.TranscriptionModel.Trim();
        _transcriptionModel = trimmedModel.Length == 0
            ? TranscriptionOptions.DefaultTranscriptionModel
            : trimmedModel;

        var trimmedLanguage = options.Language?.Trim();
        _language = string.IsNullOrEmpty(trimmedLanguage) ? null : trimmedLanguage;

        _timeoutSeconds = TimeoutSettings.Normalize(options.TimeoutSeconds);
        _client = client ?? LlmApiTransport.Client;
    }

    private string ResponseFormat => ResponseFormatFor(_transcriptionModel);

    public static string ResponseFormatFor(string model)
        => ModelsSupportingVerboseJson.Contains(model.Trim().ToLowerInvariant())
            ? "verbose_json"
            : "json";

    /// <summary>Checks a key against the provider's models endpoint.</summary>
    public static async Task<bool> ValidateApiKeyAsync(
        string key,
        string baseUrl = "https://api.groq.com/openai/v1",
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return false;

        string normalized;
        try
        {
            normalized = NormalizedBaseUrl(baseUrl);
        }
        catch (TranscriptionException)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{normalized}/models");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {trimmed}");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await (client ?? LlmApiTransport.Client)
                .SendAsync(request, timeoutSource.Token).ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Transcribes a recorded audio file.</summary>
    public async Task<string> TranscribeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            return await TranscribeOnceAsync(filePath, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TranscriptionException.TimedOut(_timeoutSeconds);
        }
    }

    private async Task<string> TranscribeOnceAsync(string filePath, CancellationToken cancellationToken)
    {
        var audioBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(filePath);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_transcriptionModel), "model");
        content.Add(new StringContent(ResponseFormat), "response_format");
        if (_language is not null) content.Add(new StringContent(_language), "language");

        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(AudioContentType(fileName));
        content.Add(audioContent, "file", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/audio/transcriptions") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw TranscriptionException.SubmissionFailed(
                FriendlyHttpMessage((int)response.StatusCode, _baseUrlHost));
        }

        try
        {
            return TranscriptionResponseParser.Parse(body);
        }
        catch (TranscriptionResponseParsingException)
        {
            throw TranscriptionException.PollFailed("Invalid response");
        }
    }

    private static string AudioContentType(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".wav", StringComparison.Ordinal)) return "audio/wav";
        if (lower.EndsWith(".mp3", StringComparison.Ordinal)) return "audio/mpeg";
        return "audio/mp4";
    }

    /// <summary>
    /// Maps a non-200 status into a one-line, user-readable message.
    /// </summary>
    /// <remarks>
    /// Keeps the tray menu showing "Invalid API key for api.openai.com" rather than raw JSON.
    /// </remarks>
    public static string FriendlyHttpMessage(int status, string? host)
    {
        var provider = host ?? "the provider";
        return status switch
        {
            401 => $"Invalid API key for {provider}. Open Settings to fix it.",
            403 => $"Key lacks permission for this endpoint at {provider} (HTTP 403). Check the key's scopes.",
            404 => $"Endpoint not found at {provider} (HTTP 404). Base URL is likely wrong for this provider.",
            413 => $"Audio file too large for {provider} (HTTP 413). Try a shorter recording.",
            400 => "Provider rejected the request (HTTP 400). Check your model name and Base URL in Settings.",
            429 => $"Rate limit reached at {provider} (HTTP 429). Wait a moment and try again.",
            >= 500 and < 600 => $"Provider error at {provider} (HTTP {status}). Try again in a moment.",
            _ => $"Request failed at {provider} (HTTP {status}).",
        };
    }

    /// <summary>
    /// Validates and canonicalizes a provider base URL, stripping trailing slashes so
    /// path joining stays predictable.
    /// </summary>
    public static string NormalizedBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.Length == 0) throw TranscriptionException.InvalidBaseUrl("Provider URL is empty.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw TranscriptionException.InvalidBaseUrl("Provider URL is malformed.");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https")
        {
            throw TranscriptionException.InvalidBaseUrl("Provider URL must use http or https.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw TranscriptionException.InvalidBaseUrl("Provider URL must include a host.");
        }

        // Rebuild from parts rather than using UriBuilder, which reintroduces a
        // trailing slash for host-only URLs and can surface the default port.
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');

        return $"{scheme}://{authority}{path}";
    }
}
