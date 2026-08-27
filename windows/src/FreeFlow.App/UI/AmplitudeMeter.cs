using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
/// Drawn with <see cref="OnRender"/> rather than composed from elements, because it
/// repaints many times a second and creating visuals per frame would be wasteful for
/// something this simple.
/// </para>
/// </remarks>
public sealed class AmplitudeMeter : Control
{
    private const int BarCount = 34;
    private const double BarWidth = 2.5;
    private const double BarGap = 2.5;
    private const double MinimumBarHeight = 2.5;

    private readonly float[] _history = new float[BarCount];
    private int _writeIndex;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(AmplitudeMeter),
        new PropertyMetadata(0.0, OnLevelChanged));

    /// <summary>Latest normalized level, 0 to 1.</summary>
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

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var meter = (AmplitudeMeter)sender;
        meter._history[meter._writeIndex] = (float)Math.Clamp((double)args.NewValue, 0, 1);
        meter._writeIndex = (meter._writeIndex + 1) % BarCount;
        meter.InvalidateVisual();
    }

    /// <summary>Clears the trace so a new session does not inherit the previous one.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _writeIndex = 0;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
        => new(BarCount * (BarWidth + BarGap) - BarGap, double.IsInfinity(constraint.Height) ? 22 : constraint.Height);

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
            brush.Opacity = 0.35 + 0.65 * (position / (double)(BarCount - 1));

            drawingContext.DrawRoundedRectangle(
                brush, null, new Rect(x, y, BarWidth, barHeight), radius, radius);
        }

        brush.Opacity = 1;
    }
}
