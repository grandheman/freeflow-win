using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using FreeFlow.Core.Shortcuts;

namespace FreeFlow.App.UI;

/// <summary>
/// The floating capsule shown while the microphone is live.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/RecordingOverlay.swift</c>.
/// </para>
/// <para>
/// The overlay must never take focus. The whole product depends on the transcript
/// landing in whatever window the user was already typing in, and a window that
/// activates would move focus away and paste into the wrong place. That is enforced
/// twice: <c>ShowActivated="False"</c> in XAML, and the <c>WS_EX_NOACTIVATE</c>
/// extended style applied below, which also keeps the capsule out of Alt+Tab.
/// </para>
/// </remarks>
public partial class RecordingOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Distance from the bottom of the work area, in device-independent pixels.</summary>
    private const double BottomMargin = 96;

    public RecordingOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, GWL_EXSTYLE);

        // NOACTIVATE keeps focus where it is, TOOLWINDOW hides it from Alt+Tab, and
        // TRANSPARENT lets clicks pass straight through to the app underneath.
        NativeMethods.SetWindowLong(
            handle, GWL_EXSTYLE,
            style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    /// <summary>Shows the capsule for a new session.</summary>
    public void ShowForSession(RecordingTriggerMode mode)
    {
        Meter.Reset();
        SetMode(mode);
        StatusLabel.Text = "Listening";
        LiveDot.Visibility = Visibility.Visible;

        PositionAboveTaskbar();

        if (!IsVisible) Show();

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    public void SetMode(RecordingTriggerMode mode)
        => ModeLabel.Text = mode.BadgeTitle();

    public void SetLevel(double level) => Meter.Level = level;

    /// <summary>
    /// Switches the capsule to the post-recording stage.
    /// </summary>
    /// <remarks>
    /// The live dot is hidden here, so the signal color disappears the moment the
    /// microphone stops. Leaving it on during transcription would misreport the
    /// state to anyone reading the overlay peripherally.
    /// </remarks>
    public void SetWorking(string message)
    {
        StatusLabel.Text = message;
        LiveDot.Visibility = Visibility.Collapsed;
        Meter.Level = 0;
    }

    public void HideOverlay()
    {
        if (!IsVisible) return;

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (Opacity <= 0.01) Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Centers the capsule near the bottom of the work area of the active screen.</summary>
    private void PositionAboveTaskbar()
    {
        // Measure first so the capsule is centered on its real width, which changes
        // with the mode badge and status text.
        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - width) / 2;
        Top = workArea.Bottom - height - BottomMargin;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
