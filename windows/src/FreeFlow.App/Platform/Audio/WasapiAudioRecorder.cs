using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FreeFlow.App.Platform.Audio;

public sealed class AudioRecorderException : Exception
{
    public AudioRecorderException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Captures the microphone and writes a normalized WAV file for upload.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for <c>Sources/AudioRecorder.swift</c>. It keeps the same
/// output contract the macOS build settled on:
/// </para>
/// <list type="bullet">
/// <item>a 16 kHz mono 16-bit PCM WAV file for upload-based transcription;</item>
/// <item>an optional 24 kHz mono PCM16 stream for the realtime socket;</item>
/// <item>a smoothed 0-1 level for the recording overlay.</item>
/// </list>
/// <para>
/// The device is opened in shared mode at whatever format it natively runs at, then
/// resampled, because forcing a format on a shared endpoint fails on plenty of
/// hardware. A watchdog reports a failure if no audio arrives shortly after start,
/// which is how a muted or hijacked device surfaces instead of producing silence.
/// </para>
/// </remarks>
public sealed class WasapiAudioRecorder : IDisposable
{
    private const int RecordingSampleRate = 16_000;
    private const int RealtimeSampleRate = 24_000;
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private MediaFoundationResampler? _uploadResampler;
    private BufferedWaveProvider? _sourceBuffer;
    private Timer? _watchdog;

    private string? _activeFilePath;
    private bool _receivedAudio;
    private bool _failureReported;

    /// <summary>
    /// Raw RMS of the latest capture buffer, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Deliberately unsmoothed. Shared-mode WASAPI delivers only about 16 buffers a
    /// second, and the level normalizer's attack and release constants are per-sample,
    /// so running it here would make the meter take seconds to adapt. The UI runs it
    /// at frame rate instead.
    /// </remarks>
    public event Action<float>? LevelChanged;

    /// <summary>
    /// Raised with 24 kHz mono PCM16 frames when set before <see cref="Start"/>.
    /// Used to feed the realtime transcription socket.
    /// </summary>
    public event Action<byte[]>? RealtimeSamplesAvailable;

    /// <summary>Raised when capture fails after it has already started.</summary>
    public event Action<AudioRecorderException>? Failed;

    public bool IsRecording
    {
        get { lock (_gate) return _capture is not null; }
    }

    /// <summary>Total bytes written so far, used to detect an empty recording.</summary>
    public long RecordedByteCount
    {
        get { lock (_gate) return _writer?.Length ?? 0; }
    }

    /// <summary>
    /// Starts capture, writing to <paramref name="filePath"/>.
    /// </summary>
    /// <param name="deviceId">Saved endpoint id, or null for the system default.</param>
    public void Start(string filePath, string? deviceId = null)
    {
        Stop();

        var device = AudioDevices.Resolve(deviceId)
            ?? throw new AudioRecorderException("No microphone is available.");

        lock (_gate)
        {
            try
            {
                _activeFilePath = filePath;
                _receivedAudio = false;
                _failureReported = false;

                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                _capture = new WasapiCapture(device)
                {
                    // Shorter than the default, so stopping a hold-to-talk session
                    // does not lose the tail of the last word.
                    ShareMode = AudioClientShareMode.Shared,
                };

                var sourceFormat = _capture.WaveFormat;
                _sourceBuffer = new BufferedWaveProvider(sourceFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(10),

                    // Critical. The default is true, which pads every read with
                    // silence to fill the requested count. The resampler would then
                    // never report end-of-data, so the drain loop below would spin
                    // forever manufacturing silence, wedging the capture thread on
                    // its first callback and writing gigabytes of nothing.
                    ReadFully = false,
                };

                var uploadFormat = new WaveFormat(RecordingSampleRate, 16, 1);
                _uploadResampler = new MediaFoundationResampler(_sourceBuffer, uploadFormat)
                {
                    ResamplerQuality = 60,
                };

                _writer = new WaveFileWriter(filePath, uploadFormat);

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                // Surface a device that accepts StartRecording but never delivers audio.
                _watchdog = new Timer(OnWatchdogElapsed, null, WatchdogTimeout, Timeout.InfiniteTimeSpan);
            }
            catch (Exception error)
            {
                CleanupLocked();
                throw new AudioRecorderException("Could not start the microphone.", error);
            }
            finally
            {
                device.Dispose();
            }
        }
    }

    /// <summary>
    /// Stops capture and finalizes the WAV file.
    /// </summary>
    /// <returns>The written file path, or null when nothing was recorded.</returns>
    public string? Stop()
    {
        WasapiCapture? capture;

        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _watchdog?.Dispose();
            _watchdog = null;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                // A device removed mid-recording throws here; the audio already
                // written is still worth keeping.
            }

            capture.Dispose();
        }

        lock (_gate)
        {
            // Drain whatever the resampler still holds so the final word is not clipped.
            DrainLocked();

            var path = _activeFilePath;
            var hasAudio = (_writer?.Length ?? 0) > 0;

            _writer?.Dispose();
            _writer = null;
            _uploadResampler?.Dispose();
            _uploadResampler = null;
            _sourceBuffer = null;
            _activeFilePath = null;

            LevelChanged?.Invoke(0);

            return hasAudio ? path : null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0) return;

        lock (_gate)
        {
            if (_sourceBuffer is null || _writer is null) return;

            _receivedAudio = true;
            _sourceBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            DrainLocked();
        }

        PublishLevel(args);
    }

    /// <summary>
    /// Pulls everything currently available through the resampler into the file.
    /// </summary>
    /// <remarks>
    /// Terminating this loop depends on the source buffer having
    /// <c>ReadFully = false</c>, so that a drained buffer yields a read of zero.
    /// </remarks>
    private void DrainLocked()
    {
        if (_uploadResampler is null || _writer is null) return;

        var buffer = new byte[8192];
        int read;
        while ((read = _uploadResampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            _writer.Write(buffer, 0, read);
            RealtimeSamplesAvailable?.Invoke(ResampleForRealtime(buffer, read));
        }
    }

    /// <summary>
    /// Upsamples the 16 kHz upload stream to the 24 kHz the realtime socket expects.
    /// </summary>
    /// <remarks>
    /// Linear interpolation is sufficient here: the samples are already band-limited
    /// by the upload resampler, and the realtime path is a transcription hint rather
    /// than the archived audio.
    /// </remarks>
    private static byte[] ResampleForRealtime(byte[] pcm16, int byteCount)
    {
        var sourceSamples = byteCount / 2;
        if (sourceSamples == 0) return Array.Empty<byte>();

        const double ratio = (double)RealtimeSampleRate / RecordingSampleRate;
        var targetSamples = (int)(sourceSamples * ratio);
        var output = new byte[targetSamples * 2];

        for (var index = 0; index < targetSamples; index++)
        {
            var position = index / ratio;
            var lower = (int)position;
            var upper = Math.Min(lower + 1, sourceSamples - 1);
            var fraction = position - lower;

            var a = BitConverter.ToInt16(pcm16, lower * 2);
            var b = BitConverter.ToInt16(pcm16, upper * 2);
            var value = (short)(a + (b - a) * fraction);

            BitConverter.TryWriteBytes(output.AsSpan(index * 2, 2), value);
        }

        return output;
    }

    private void PublishLevel(WaveInEventArgs args)
    {
        var handler = LevelChanged;
        if (handler is null) return;

        WaveFormat? format;
        lock (_gate) format = _capture?.WaveFormat;
        if (format is null) return;

        handler(ComputeRms(args.Buffer, args.BytesRecorded, format));
    }

    /// <summary>
    /// RMS of one capture buffer, handling the two formats shared-mode WASAPI
    /// actually hands back: 32-bit float and 16-bit PCM.
    /// </summary>
    private static float ComputeRms(byte[] buffer, int byteCount, WaveFormat format)
    {
        double sum = 0;
        var count = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= byteCount; offset += 4)
            {
                var sample = BitConverter.ToSingle(buffer, offset);
                sum += sample * (double)sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= byteCount; offset += 2)
            {
                var sample = BitConverter.ToInt16(buffer, offset) / 32768f;
                sum += sample * (double)sample;
                count++;
            }
        }
        else
        {
            return 0;
        }

        return count == 0 ? 0 : (float)Math.Sqrt(sum / count);
    }

    private void OnWatchdogElapsed(object? state)
    {
        bool shouldReport;
        lock (_gate)
        {
            shouldReport = _capture is not null && !_receivedAudio && !_failureReported;
            if (shouldReport) _failureReported = true;
        }

        if (shouldReport)
        {
            Failed?.Invoke(new AudioRecorderException(
                "The microphone started but delivered no audio. Check that it is not muted " +
                "and that FreeFlow has microphone access in Windows privacy settings."));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null) return;

        bool shouldReport;
        lock (_gate)
        {
            shouldReport = !_failureReported;
            if (shouldReport) _failureReported = true;
        }

        if (shouldReport)
        {
            Failed?.Invoke(new AudioRecorderException("Microphone capture stopped unexpectedly.", args.Exception));
        }
    }

    private void CleanupLocked()
    {
        _watchdog?.Dispose();
        _watchdog = null;

        _capture?.Dispose();
        _capture = null;

        _writer?.Dispose();
        _writer = null;

        _uploadResampler?.Dispose();
        _uploadResampler = null;

        _sourceBuffer = null;
        _activeFilePath = null;
    }

    public void Dispose() => Stop();
}
