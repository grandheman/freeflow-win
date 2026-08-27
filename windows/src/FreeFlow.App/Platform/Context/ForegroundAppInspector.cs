using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using FreeFlow.Core.Context;

namespace FreeFlow.App.Platform.Context;

/// <summary>
/// Reads what the user is currently working in: the foreground app, its window title,
/// and any selected text.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for the <c>AXUIElement</c> calls in
/// <c>Sources/AppContextService.swift</c> and <c>Sources/AppState.swift</c>.
/// UI Automation is the closest equivalent to the macOS Accessibility API.
/// </para>
/// <para>
/// Coverage is genuinely narrower than on macOS, and that is a platform limitation
/// rather than a gap in this port. Selected text is readable from controls that
/// implement the UIA Text or Value pattern, which covers most native controls,
/// Office, and Chromium and Firefox with accessibility enabled. It is not readable
/// from applications that render their own text without exposing a UIA tree, notably
/// some terminals, Electron apps with accessibility off, and most games. Every read
/// therefore degrades to null rather than failing, and the caller must treat missing
/// context as normal.
/// </para>
/// <para>
/// Reads are bounded by a timeout because a hung foreground application can otherwise
/// block a UIA call indefinitely, which would stall dictation.
/// </para>
/// </remarks>
public sealed class ForegroundAppInspector
{
    /// <summary>Upper bound on any single inspection, so a hung app cannot stall dictation.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(400);

    public AppSelectionSnapshot CollectSelectionSnapshot()
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == IntPtr.Zero) return AppSelectionSnapshot.Empty;

        var processName = ProcessNameFor(windowHandle);
        var windowTitle = WindowTitleFor(windowHandle);
        var selectedText = RunBounded(ReadSelectedText);

        return new AppSelectionSnapshot(
            AppName: processName,
            ApplicationId: processName,
            WindowTitle: windowTitle,
            SelectedText: selectedText);
    }

    /// <summary>Reads the focused element's selected text, or null when unavailable.</summary>
    public static string? ReadSelectedText()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return null;

            // The Text pattern is the reliable path and exposes a real selection range.
            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var ranges = textPattern.GetSelection();
                if (ranges is { Length: > 0 })
                {
                    var builder = new StringBuilder();
                    foreach (var range in ranges) builder.Append(range.GetText(-1));

                    var text = builder.ToString();
                    if (text.Length > 0) return text;
                }
            }

            // Value pattern has no selection concept, so this only helps for controls
            // whose whole value is effectively the selection.
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }
        catch (ElementNotAvailableException)
        {
            // The focused element vanished mid-read, which happens constantly during
            // normal window switching.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? WindowTitleFor(IntPtr windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0) return null;

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, buffer, buffer.Capacity);

        var title = buffer.ToString();
        return title.Length == 0 ? null : title;
    }

    private static string? ProcessNameFor(IntPtr windowHandle)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process exited between the handle read and the lookup.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a UIA read with a hard time limit.
    /// </summary>
    /// <remarks>
    /// UI Automation calls cross into the target process. A frozen application can
    /// block one indefinitely, so an abandoned worker is preferable to a stalled
    /// dictation pipeline.
    /// </remarks>
    private T? RunBounded<T>(Func<T?> work) where T : class
    {
        T? result = null;
        using var completed = new System.Threading.ManualResetEventSlim(false);

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception)
            {
                result = null;
            }
            finally
            {
                // ReSharper disable once AccessToDisposedClosure
                try { completed.Set(); } catch (ObjectDisposedException) { }
            }
        })
        {
            IsBackground = true,
            Name = "FreeFlow UIA read",
        };

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();

        return completed.Wait(ReadTimeout) ? result : null;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
