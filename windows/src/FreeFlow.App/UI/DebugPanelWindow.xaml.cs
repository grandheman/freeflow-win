using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreeFlow.Core.History;

namespace FreeFlow.App.UI;

/// <summary>
/// Shows what each stage of the pipeline actually produced.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/PipelineDebugPanelView.swift</c>.
/// </para>
/// <para>
/// Everything shown here is the user's own dictated text and screen context, read
/// from the local history file. It is never transmitted anywhere, and Clear removes
/// it from disk.
/// </para>
/// </remarks>
public partial class DebugPanelWindow : Window
{
    private readonly PipelineHistoryStore _history;
    private IReadOnlyList<PipelineHistoryItem> _items = Array.Empty<PipelineHistoryItem>();

    public DebugPanelWindow(PipelineHistoryStore history)
    {
        _history = history;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        _items = _history.LoadAll();

        RunList.Items.Clear();
        foreach (var item in _items)
        {
            RunList.Items.Add(new ListBoxItem
            {
                Content = BuildRunSummary(item),
                Padding = new Thickness(10, 8, 10, 8),
                Foreground = (Brush)FindResource("Ink"),
            });
        }

        EmptyLabel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailHost.Children.Clear();

        if (_items.Count > 0) RunList.SelectedIndex = 0;
    }

    private object BuildRunSummary(PipelineHistoryItem item)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = item.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            Style = (Style)FindResource("MonoText"),
            FontSize = 11,
            Foreground = (Brush)FindResource("InkFaint"),
        });

        panel.Children.Add(new TextBlock
        {
            // The cleaned transcript is what the user actually saw pasted, so it is
            // the most recognizable label for a run.
            Text = Truncate(item.PostProcessedTranscript, 60),
            Style = (Style)FindResource("BodyText"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return panel;
    }

    private void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        DetailHost.Children.Clear();

        var index = RunList.SelectedIndex;
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];

        AddField("Recorded", item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AddField("Intent", item.Intent.ToString());
        AddField("Application", item.ContextAppName ?? "Unknown");
        AddField("Window", item.ContextWindowTitle ?? "Unknown");

        AddBlock("Raw transcript", item.RawTranscript);
        AddBlock("After cleanup", item.PostProcessedTranscript);

        if (item.SelectedText is { Length: > 0 })
        {
            AddBlock("Selected text", item.SelectedText);
        }

        AddBlock("Context summary", item.ContextSummary);
        AddField("Screenshot", item.ContextScreenshotStatus);

        if (item.CustomVocabulary.Length > 0)
        {
            AddBlock("Custom vocabulary", item.CustomVocabulary);
        }

        if (item.PostProcessingPrompt is { Length: > 0 })
        {
            AddBlock("Cleanup prompt", item.PostProcessingPrompt, monospace: true);
        }

        if (item.ContextPrompt is { Length: > 0 })
        {
            AddBlock("Context prompt", item.ContextPrompt, monospace: true);
        }
    }

    private void AddField(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("EyebrowText"),
            Width = 140,
            Margin = new Thickness(0),
        });

        row.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("BodyText"),
        });

        DetailHost.Children.Add(row);
    }

    private void AddBlock(string label, string value, bool monospace = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Style = (Style)FindResource("EyebrowText"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new Border
        {
            Background = (Brush)FindResource("SurfaceSunken"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBox
            {
                Text = value,
                // Read-only rather than a TextBlock, so prompts can be copied out
                // into a bug report.
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = monospace
                    ? (FontFamily)FindResource("MonoFont")
                    : (FontFamily)FindResource("BodyFont"),
                FontSize = monospace ? 11.5 : 13,
                Padding = new Thickness(0),
            },
        });

        DetailHost.Children.Add(panel);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            "Delete every recorded dictation, prompt, and screenshot from this machine?",
            "Clear pipeline history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.OK) return;

        _history.Clear();
        Reload();
    }

    private static string Truncate(string value, int length)
    {
        var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (single.Length == 0) return "(empty)";
        return single.Length <= length ? single : single[..length] + "…";
    }
}
