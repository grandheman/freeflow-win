using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreeFlow.Core.Audio;

namespace FreeFlow.App.UI;

/// <summary>
/// A scrolling record of recent microphone amplitude.
/// </summary>
/// <remarks>
/// <para>
/// This shows real measured level, not a decorative animation. Each bar is one
/// sampled moment, newest on the right, scrolling left as you speak. That makes it
/// genuinely diagnostic: a flat line means the microphone is not hearing you, which
/// is exactly the failure a dictation user needs to catch within the first second
/// rather than after the transcript comes back empty.
/// </para>
/// <para>
/// The trace advances on the compositor's frame tick rather than on each audio
/// callback. Shared-mode WASAPI delivers roughly 16 buffers a second, so advancing
/// per callback scrolled the whole width in about two seconds, which reads as
/// laggy and disconnected from the voice. Decoupling the two means the scroll speed
/// is the same on every machine regardless of how the audio device is buffered.
/// </para>
/// <para>
/// Drawn with <see cref="OnRender"/> rather than composed from elements, because it
/// repaints many times a second and creating visuals per frame would be wasteful for
/// something this simple.
/// </para>
/// </remarks>
public sealed class AmplitudeMeter : Control
{
    private const int BarCount = 44;
    private const double BarWidth = 2.5;
    private const double BarGap = 2.0;
    private const double MinimumBarHeight = 2.5;

    private readonly float[] _history = new float[BarCount];
    private int _writeIndex;
    private bool _isRunning;

    /// <summary>
    /// Maps raw RMS onto a usable 0-1 range by tracking a rolling noise floor and peak.
    /// </summary>
    /// <remarks>
    /// This lives here, driven by the frame tick, rather than in the recorder. Its
    /// attack and release constants are per-sample and were tuned against macOS audio
    /// callbacks; running it at the ~16 Hz that shared-mode WASAPI delivers made the
    /// meter take seconds to find the speaker's range, which read as severe lag.
    /// </remarks>
    private LiveAudioLevelNormalizer _normalizer = new();

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(AmplitudeMeter),
        new PropertyMetadata(0.0));

    /// <summary>Latest raw RMS from the microphone. Sampled each frame rather than on change.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(AmplitudeMeter),
        new PropertyMetadata(Brushes.Gray, (d, _) => ((AmplitudeMeter)d).InvalidateVisual()));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Clears the trace and begins advancing it each frame.</summary>
    public void Start()
    {
        Array.Clear(_history);
        _writeIndex = 0;
        _normalizer.Reset();

        if (!_isRunning)
        {
            CompositionTarget.Rendering += OnFrame;
            _isRunning = true;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Stops advancing the trace.
    /// </summary>
    /// <remarks>
    /// Detaching matters: <see cref="CompositionTarget.Rendering"/> is a static event,
    /// so a meter left attached keeps its window alive and repaints forever.
    /// </remarks>
    public void Stop()
    {
        if (!_isRunning) return;

        CompositionTarget.Rendering -= OnFrame;
        _isRunning = false;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        // Normalizing here means the adaptation runs at frame rate, independent of
        // how often the audio device happens to deliver buffers.
        _history[_writeIndex] = _normalizer.NormalizedLevel((float)Math.Clamp(Level, 0, 1));
        _writeIndex = (_writeIndex + 1) % BarCount;

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
        => new(BarCount * (BarWidth + BarGap) - BarGap,
               double.IsInfinity(constraint.Height) ? 22 : constraint.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = BarBrush;
        if (brush is null) return;

        var height = ActualHeight;
        var centerY = height / 2;
        var maximumHalf = Math.Max(height / 2 - 1, MinimumBarHeight / 2);
        var radius = BarWidth / 2;

        for (var position = 0; position < BarCount; position++)
        {
            // Oldest sample on the left, newest on the right.
            var sample = _history[(_writeIndex + position) % BarCount];

            var barHeight = Math.Max(MinimumBarHeight, sample * maximumHalf * 2);
            var x = position * (BarWidth + BarGap);
            var y = centerY - barHeight / 2;

            // Older samples fade, which gives the trace direction without motion.
            brush.Opacity = 0.3 + 0.7 * (position / (double)(BarCount - 1));

            drawingContext.DrawRoundedRectangle(
                brush, null, new Rect(x, y, BarWidth, barHeight), radius, radius);
        }

        brush.Opacity = 1;
    }
}
