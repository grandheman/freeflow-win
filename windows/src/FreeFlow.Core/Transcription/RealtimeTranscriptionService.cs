using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlow.Core.Transcription;

public enum RealtimeFailure
{
    InvalidBaseUrl,
    NotConnected,
    ServerError,
    ClosedBeforeFinal,
}

public sealed class RealtimeTranscriptionException : Exception
{
    public RealtimeFailure Failure { get; }

    public RealtimeTranscriptionException(RealtimeFailure failure, string message) : base(message)
        => Failure = failure;
}

public sealed record RealtimeConfiguration(
    string BaseUrl,
    string ApiKey,
    string Model,
    string? Language);

/// <summary>
/// Streams microphone audio to a realtime transcription socket while the user speaks.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/RealtimeTranscriptionService.swift</c>, using
/// <see cref="ClientWebSocket"/> in place of <c>URLSessionWebSocketTask</c>.
/// </para>
/// <para>
/// This is an alternative to the upload path, not a replacement: it trades a little
/// accuracy for the transcript being nearly ready the moment the user stops talking.
/// Audio is sent as 24 kHz mono PCM16, which is what the recorder's realtime tap
/// produces.
/// </para>
/// <para>
/// Server turn detection is disabled. The app decides when a dictation ends, based on
/// the shortcut being released, so letting the server guess at silence would cut
/// people off mid-thought.
/// </para>
/// </remarks>
public sealed class RealtimeTranscriptionService : IDisposable
{
    private const int SampleRate = 24_000;

    private readonly RealtimeConfiguration _config;
    private readonly object _gate = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveLoop;

    private readonly StringBuilder _finalText = new();
    private string _partialText = "";
    private bool _commitSent;
    private bool _postCommitCompleted;
    private TaskCompletionSource<string>? _completion;
    private Exception? _terminalError;

    /// <summary>Raised as the transcript grows, for a live readout in the overlay.</summary>
    public event Action<string>? PartialUpdated;

    public RealtimeTranscriptionService(RealtimeConfiguration config) => _config = config;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var socketUri = DeriveWebSocketUri(_config.BaseUrl)
            ?? throw new RealtimeTranscriptionException(
                RealtimeFailure.InvalidBaseUrl,
                $"Cannot derive a WebSocket URL from {_config.BaseUrl}");

        var socket = new ClientWebSocket();
        if (_config.ApiKey.Length > 0)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
        }

        await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _socket = socket;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await SendSessionUpdateAsync().ConfigureAwait(false);

        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Appends a chunk of 24 kHz mono PCM16 audio.</summary>
    public async Task AppendAudioAsync(byte[] pcm16)
    {
        if (pcm16.Length == 0) return;

        ClientWebSocket? socket;
        lock (_gate) socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16),
        });

        await SendAsync(socket, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals the end of the utterance and waits for the final transcript.
    /// </summary>
    public async Task<string> CommitAndAwaitFinalAsync(TimeSpan timeout)
    {
        ClientWebSocket? socket;
        TaskCompletionSource<string>? completion;

        lock (_gate)
        {
            socket = _socket;
            completion = _completion;
            _commitSent = true;
        }

        if (socket is null || completion is null)
        {
            throw new RealtimeTranscriptionException(
                RealtimeFailure.NotConnected, "Realtime transcription socket is not connected");
        }

        await SendAsync(socket, """{"type":"input_audio_buffer.commit"}""").ConfigureAwait(false);

        // The socket can stall or die silently, so the wait is always bounded and
        // the caller can fall back to the upload path.
        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != completion.Task)
        {
            lock (_gate)
            {
                // Whatever arrived before the timeout is better than nothing.
                return _finalText.ToString();
            }
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task SendSessionUpdateAsync()
    {
        ClientWebSocket? socket;
        lock (_gate) socket = _socket;
        if (socket is null) return;

        var model = _config.Model.Trim();
        var language = _config.Language?.Trim();

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session.update");

            writer.WriteStartObject("session");
            writer.WriteString("type", "transcription");

            writer.WriteStartObject("audio");
            writer.WriteStartObject("input");

            writer.WriteStartObject("format");
            writer.WriteString("type", "audio/pcm");
            writer.WriteNumber("rate", SampleRate);
            writer.WriteEndObject();

            writer.WriteStartObject("transcription");
            if (model.Length > 0) writer.WriteString("model", model);
            if (!string.IsNullOrEmpty(language)) writer.WriteString("language", language);
            writer.WriteEndObject();

            // Null disables server-side turn detection; the shortcut decides when
            // the utterance ends.
            writer.WriteNull("turn_detection");

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await SendAsync(socket, Encoding.UTF8.GetString(stream.ToArray())).ConfigureAwait(false);
    }

    private static async Task SendAsync(ClientWebSocket socket, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // A dead socket surfaces through the receive loop, which owns failure.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            socket = _socket;
            cancellation = _cancellation;
        }

        if (socket is null || cancellation is null) return;

        var buffer = new byte[16 * 1024];
        var message = new StringBuilder();

        try
        {
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                var result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    FailIfIncomplete();
                    return;
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                HandleServerEvent(message.ToString());
                message.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException error)
        {
            lock (_gate) _terminalError = error;
            FailIfIncomplete();
        }
    }

    private void HandleServerEvent(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var eventType = typeElement.GetString();
            var delta = ReadString(document.RootElement, "delta");
            var transcript = ReadString(document.RootElement, "transcript");

            switch (eventType)
            {
                case "conversation.item.input_audio_transcription.delta":
                    if (delta is not null)
                    {
                        lock (_gate) _partialText += delta;
                        PublishPartial();
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    lock (_gate)
                    {
                        // The completed event carries the authoritative text for the
                        // item, so accumulated deltas are replaced rather than appended.
                        _finalText.Append(transcript ?? _partialText);
                        _partialText = "";
                        if (_commitSent) _postCommitCompleted = true;
                    }
                    PublishPartial();
                    ResumeIfReadyAfterCommit();
                    break;

                case "error":
                    var code = ReadString(document.RootElement, "code") ?? "unknown";
                    var errorMessage = ReadString(document.RootElement, "message") ?? "Realtime error";
                    lock (_gate)
                    {
                        _terminalError = new RealtimeTranscriptionException(
                            RealtimeFailure.ServerError, $"Realtime server error [{code}]: {errorMessage}");
                    }
                    FailIfIncomplete();
                    break;
            }
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private void PublishPartial()
    {
        string combined;
        lock (_gate) combined = _finalText + _partialText;
        PartialUpdated?.Invoke(combined);
    }

    /// <summary>
    /// Completes the pending wait once the commit has been acknowledged and no
    /// partial text is still streaming.
    /// </summary>
    private void ResumeIfReadyAfterCommit()
    {
        TaskCompletionSource<string>? completion = null;
        string text = "";

        lock (_gate)
        {
            if (_completion is null || !_commitSent || _partialText.Length > 0 || !_postCommitCompleted)
            {
                return;
            }

            completion = _completion;
            _completion = null;
            text = _finalText.ToString();
        }

        completion?.TrySetResult(text);
    }

    private void FailIfIncomplete()
    {
        TaskCompletionSource<string>? completion;
        Exception? error;

        lock (_gate)
        {
            completion = _completion;
            _completion = null;
            error = _terminalError;
        }

        completion?.TrySetException(error ?? new RealtimeTranscriptionException(
            RealtimeFailure.ClosedBeforeFinal,
            "Realtime socket closed before emitting the final transcript"));
    }

    /// <summary>
    /// Turns an HTTP base URL into the realtime WebSocket URL.
    /// </summary>
    /// <remarks>
    /// Reuses a trailing <c>/v1</c> when the configured base already has one, so
    /// both <c>https://host</c> and <c>https://host/v1</c> resolve correctly.
    /// </remarks>
    public static Uri? DeriveWebSocketUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => null,
        };

        if (scheme is null) return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        path += path.EndsWith("/v1", StringComparison.Ordinal) ? "/realtime" : "/v1/realtime";

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var query = uri.Query.TrimStart('?');
        if (!query.Contains("intent=", StringComparison.Ordinal))
        {
            query = query.Length == 0 ? "intent=transcription" : $"{query}&intent=transcription";
        }

        return new Uri($"{scheme}://{authority}{path}?{query}");
    }

    public void Cancel()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            socket = _socket;
            cancellation = _cancellation;
            _socket = null;
            _cancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        try
        {
            socket?.Abort();
            socket?.Dispose();
        }
        catch (Exception)
        {
        }

        FailIfIncomplete();
    }

    public void Dispose() => Cancel();
}
