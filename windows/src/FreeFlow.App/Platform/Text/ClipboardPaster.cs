using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FreeFlow.App.Platform.Input;

namespace FreeFlow.App.Platform.Text;

/// <summary>
/// A saved clipboard, held so dictation can put it back afterwards.
/// </summary>
/// <remarks>
/// Only formats that round-trip safely are captured. Images and file drops are
/// deliberately skipped: copying them out of the clipboard and back in can be very
/// expensive and, for delay-rendered data, lossy.
/// </remarks>
public sealed record ClipboardSnapshot(IReadOnlyDictionary<string, object> Formats)
{
    private static readonly string[] PreservedFormats =
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.Rtf,
        DataFormats.Html,
    };

    public static ClipboardSnapshot Capture()
    {
        var formats = new Dictionary<string, object>(StringComparer.Ordinal);

        try
        {
            var data = Clipboard.GetDataObject();
            if (data is not null)
            {
                foreach (var format in PreservedFormats)
                {
                    if (!data.GetDataPresent(format)) continue;
                    var value = data.GetData(format);
                    if (value is not null) formats[format] = value;
                }
            }
        }
        catch (Exception)
        {
            // Another process can hold the clipboard open. An empty snapshot simply
            // means there is nothing to restore.
        }

        return new ClipboardSnapshot(formats);
    }

    public void Restore()
    {
        try
        {
            if (Formats.Count == 0)
            {
                Clipboard.Clear();
                return;
            }

            var data = new DataObject();
            foreach (var (format, value) in Formats) data.SetData(format, value);
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception)
        {
            // Losing the restore is preferable to crashing the app.
        }
    }
}

/// <summary>Bookkeeping for a clipboard restore that is pending after a paste.</summary>
public sealed record PendingClipboardRestore(
    ClipboardSnapshot Snapshot,
    string WrittenTranscript);

/// <summary>
/// Writes the transcript to the clipboard and pastes it into the focused control.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for the pasteboard and synthetic Cmd-V path in
/// <c>Sources/AppState.swift</c>. Clipboard-and-paste is used rather than typing the
/// text character by character because it is far faster for long transcripts and
/// preserves Unicode reliably.
/// </para>
/// <para>
/// All clipboard access happens on an STA thread, which Win32 requires.
/// </para>
/// </remarks>
public sealed class ClipboardPaster
{
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    /// <summary>
    /// How long to wait before restoring the previous clipboard.
    /// </summary>
    /// <remarks>
    /// Some applications consume Ctrl+V asynchronously, so restoring too quickly
    /// pastes the pre-dictation clipboard instead of the transcript.
    /// </remarks>
    public TimeSpan RestoreDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>When false, the previous clipboard contents are not preserved.</summary>
    public bool PreserveClipboard { get; init; } = true;

    /// <summary>
    /// When true the transcript is written as an ordinary copy so clipboard managers
    /// record it in history. When false it is marked so well-behaved managers skip it.
    /// </summary>
    public bool KeepDictationInClipboardHistory { get; init; }

    /// <summary>
    /// Writes the transcript, sends Ctrl+V, then restores the previous clipboard.
    /// </summary>
    public async Task PasteAsync(string transcript, CancellationToken cancellationToken = default)
    {
        if (transcript.Length == 0) return;

        var textToWrite = AppendTrailingSpaceIfNeeded(transcript);
        var pending = await RunOnStaThreadAsync(() => WriteToClipboard(textToWrite)).ConfigureAwait(false);

        SendPasteKeystroke();

        if (pending is null) return;

        try
        {
            await Task.Delay(RestoreDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunOnStaThreadAsync(() => RestoreClipboard(pending)).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a space after sentence-ending punctuation so the next dictation does
    /// not jam against the previous period.
    /// </summary>
    internal static string AppendTrailingSpaceIfNeeded(string transcript)
    {
        if (transcript.Length == 0) return transcript;
        var last = transcript[^1];
        return last is '.' or '!' or '?' ? transcript + " " : transcript;
    }

    private PendingClipboardRestore? WriteToClipboard(string textToWrite)
    {
        var snapshot = PreserveClipboard ? ClipboardSnapshot.Capture() : null;

        try
        {
            if (KeepDictationInClipboardHistory)
            {
                Clipboard.SetText(textToWrite);
            }
            else
            {
                // Windows clipboard history and most third-party managers honor
                // these two markers, which keeps dictated text out of history while
                // still pasting normally. This is the Windows counterpart to the
                // org.nspasteboard.TransientType markers the macOS build sets.
                var data = new DataObject();
                data.SetText(textToWrite, TextDataFormat.UnicodeText);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(1));
                data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(new byte[] { 0, 0, 0, 0 }));
                data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(new byte[] { 0, 0, 0, 0 }));
                Clipboard.SetDataObject(data, copy: true);
            }
        }
        catch (Exception)
        {
            // A clipboard held open by another process means the paste will fail,
            // but the pipeline result is still shown in the tray menu.
            return null;
        }

        return snapshot is null ? null : new PendingClipboardRestore(snapshot, textToWrite);
    }

    private static object? RestoreClipboard(PendingClipboardRestore pending)
    {
        try
        {
            // Restore only when the clipboard still holds exactly what was written.
            // If the user copied something else in the meantime, leave it alone.
            var current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (current is not null && current != pending.WrittenTranscript) return null;
        }
        catch (Exception)
        {
            return null;
        }

        pending.Snapshot.Restore();
        return null;
    }

    /// <summary>
    /// Sends Ctrl+V to the focused window, tagged so the app's own keyboard hook
    /// ignores it.
    /// </summary>
    private static void SendPasteKeystroke()
    {
        var marker = WindowsShortcutBackend.InjectedMarker;

        var inputs = new[]
        {
            KeyInput(VkControl, isDown: true, marker),
            KeyInput(VkV, isDown: true, marker),
            KeyInput(VkV, isDown: false, marker),
            KeyInput(VkControl, isDown: false, marker),
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT KeyInput(ushort virtualKey, bool isDown, IntPtr marker) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = isDown ? 0 : NativeMethods.KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = marker,
            },
        },
    };

    /// <summary>Runs clipboard work on a dedicated STA thread, which Win32 requires.</summary>
    private static Task<T?> RunOnStaThreadAsync<T>(Func<T?> work) where T : class
    {
        var completion = new TaskCompletionSource<T?>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return completion.Task;
    }
}
