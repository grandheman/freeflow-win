using System;
using System.IO;
using System.Text;

namespace FreeFlow.App.Platform.Host;

/// <summary>
/// Records pipeline stage transitions to a local file for troubleshooting.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately records only stage names, timings, sizes, and error types. It never
/// records transcripts, selected text, window titles, clipboard contents, prompts, or
/// API keys, because a log a user might paste into a bug report must not carry their
/// dictation or their credentials.
/// </para>
/// <para>
/// The file is truncated at startup and capped, so it cannot grow without bound.
/// </para>
/// </remarks>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AppPaths.DataDirectory, "diagnostic.log");
    private const long MaxBytes = 512 * 1024;

    public static bool Enabled { get; set; } = true;

    public static void Start()
    {
        if (!Enabled) return;

        try
        {
            AppPaths.EnsureCreated();
            lock (Gate)
            {
                File.WriteAllText(Path,
                    $"FreeFlow diagnostic log{Environment.NewLine}" +
                    $"started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                    $"no transcripts, prompts, or keys are recorded here{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Write(string stage, string? detail = null)
    {
        if (!Enabled) return;

        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff"))
                .Append("  ")
                .Append(stage);

            if (detail is not null) line.Append("  ").Append(detail);
            line.AppendLine();

            lock (Gate)
            {
                if (new FileInfo(Path) is { Exists: true, Length: > MaxBytes }) return;
                File.AppendAllText(Path, line.ToString());
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Records an exception by type and message, which never contain user content.</summary>
    public static void WriteError(string stage, Exception error)
        => Write(stage, $"{error.GetType().Name}: {error.Message}");
}
