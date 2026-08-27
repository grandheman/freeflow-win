using System;
using System.Net.Http;

namespace FreeFlow.Core.Net;

/// <summary>
/// Shared <see cref="HttpClient"/> for every provider call.
/// </summary>
/// <remarks>
/// <para>
/// One client is reused process-wide because creating an <see cref="HttpClient"/> per
/// request exhausts sockets under repeated dictation.
/// </para>
/// <para>
/// The client itself carries no timeout; each call passes a
/// <see cref="System.Threading.CancellationToken"/> instead, which is what lets the
/// transcription and post-processing paths apply their own configurable limits.
/// </para>
/// <para>Replaces <c>Sources/LLMAPITransport.swift</c>.</para>
/// </remarks>
public static class LlmApiTransport
{
    private static readonly Lazy<HttpClient> LazyClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    });

    public static HttpClient Client => LazyClient.Value;

    private static class Timeout
    {
        public static readonly TimeSpan InfiniteTimeSpan = System.Threading.Timeout.InfiniteTimeSpan;
    }
}

/// <summary>
/// Per-stage network timeouts.
/// </summary>
/// <remarks>
/// The macOS build read these from <c>defaults write com.zachlatta.freeflow …</c>.
/// On Windows they are ordinary settings, surfaced in the Settings window, which
/// matters for local providers (Ollama, LM Studio) that are slow on cold start.
/// </remarks>
public sealed record TimeoutSettings
{
    public const double DefaultSeconds = 20;

    public double TranscriptionSeconds { get; init; } = DefaultSeconds;
    public double PostProcessingSeconds { get; init; } = DefaultSeconds;
    public double ContextRequestSeconds { get; init; } = DefaultSeconds;

    public static readonly TimeoutSettings Default = new();

    /// <summary>Only positive values override the default, matching upstream behavior.</summary>
    public static double Normalize(double value) => value > 0 ? value : DefaultSeconds;
}
