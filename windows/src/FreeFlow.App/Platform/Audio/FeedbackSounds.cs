using System;
using System.IO;
using System.Media;
using System.Threading;

namespace FreeFlow.App.Platform.Audio;

/// <summary>
/// Short tones marking the start and end of a recording.
/// </summary>
/// <remarks>
/// <para>
/// The tones are synthesized rather than shipped as .wav resources so the app stays
/// a single file, and so the start and stop cues can be a matched pair by
/// construction: the same envelope, rising to begin and falling to end.
/// </para>
/// <para>
/// The Windows system sounds were not used here. They carry meanings the user already
/// has (asterisk means notice, exclamation means problem), and borrowing one to mean
/// "microphone live" would be misleading.
/// </para>
/// </remarks>
public static class FeedbackSounds
{
    private const int SampleRate = 44_100;
    private const double DurationSeconds = 0.085;
    private const double Amplitude = 0.16;

    private static readonly Lazy<byte[]> StartTone = new(() => BuildTone(660, 880));
    private static readonly Lazy<byte[]> StopTone = new(() => BuildTone(880, 620));

    public static void PlayStart() => Play(StartTone.Value);

    public static void PlayStop() => Play(StopTone.Value);

    private static void Play(byte[] wav)
    {
        // Off the calling thread: this runs on the shortcut path, and blocking it
        // would delay the recording itself.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using var stream = new MemoryStream(wav, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception)
            {
                // Audio feedback is never worth an error path.
            }
        });
    }

    /// <summary>
    /// Renders a short sine sweep as an in-memory 16-bit mono WAV.
    /// </summary>
    /// <remarks>
    /// The sweep is enveloped at both ends; a raw sine that starts and stops at
    /// nonzero amplitude produces an audible click.
    /// </remarks>
    private static byte[] BuildTone(double startFrequency, double endFrequency)
    {
        var sampleCount = (int)(SampleRate * DurationSeconds);
        var samples = new short[sampleCount];

        double phase = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var position = index / (double)sampleCount;
            var frequency = startFrequency + (endFrequency - startFrequency) * position;

            // Accumulate phase rather than recomputing from time, so the sweep stays
            // continuous and does not glitch as the frequency changes.
            phase += 2 * Math.PI * frequency / SampleRate;

            // Raised-cosine envelope: silent at both edges.
            var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * position));

            samples[index] = (short)(Math.Sin(phase) * envelope * Amplitude * short.MaxValue);
        }

        return WrapAsWav(samples);
    }

    private static byte[] WrapAsWav(short[] samples)
    {
        var dataBytes = samples.Length * 2;

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM header size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample

        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);

        writer.Flush();
        return stream.ToArray();
    }
}
