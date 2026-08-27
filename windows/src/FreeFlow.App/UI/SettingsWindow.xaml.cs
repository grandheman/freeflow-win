using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeFlow.App.Platform.Audio;
using FreeFlow.Core.Context;
using FreeFlow.Core.Models;
using FreeFlow.Core.PostProcessing;
using FreeFlow.Core.Settings;
using FreeFlow.Core.Shortcuts;
using FreeFlow.Core.Transcription;

namespace FreeFlow.App.UI;

/// <summary>
/// The settings surface.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/SettingsView.swift</c>.
/// </para>
/// <para>
/// Sections are built in code rather than declared as seven XAML panels. The rows are
/// highly repetitive (label, control, explanation) and a small set of builders keeps
/// them consistent, whereas hand-written markup drifts row by row.
/// </para>
/// <para>
/// Every control writes through immediately. A settings window with a Save button
/// invites half-applied state, and this app's settings all take effect the moment
/// they change.
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private bool _isLoading;

    public SettingsWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        HoldPreview.Binding = _state.Settings.HoldShortcut;
        ShowSection(0);
    }

    private AppSettings Settings => _state.Settings;

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (_isLoading) return;

        _state.UpdateSettings(change(_state.Settings));
        HoldPreview.Binding = _state.Settings.HoldShortcut;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionHost is null) return;
        ShowSection(SectionList.SelectedIndex);
    }

    private void OnDoneClicked(object sender, RoutedEventArgs e) => Close();

    private void ShowSection(int index)
    {
        _isLoading = true;
        SectionHost.Children.Clear();

        switch (index)
        {
            case 0: BuildProviderSection(); break;
            case 1: BuildShortcutsSection(); break;
            case 2: BuildDictationSection(); break;
            case 3: BuildEditModeSection(); break;
            case 4: BuildContextSection(); break;
            case 5: BuildAudioSection(); break;
            default: BuildAdvancedSection(); break;
        }

        _isLoading = false;
    }

    // MARK: Sections

    private void BuildProviderSection()
    {
        AddHeading("Provider",
            "FreeFlow talks to any OpenAI-compatible endpoint. Groq is the default because its free tier is fast enough for live dictation.");

        var apiKeyBox = new PasswordBox
        {
            Password = _state.ApiKey,
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
        };
        apiKeyBox.PasswordChanged += (_, _) => _state.SetApiKey(apiKeyBox.Password);
        AddRow("API key", apiKeyBox,
            "Stored encrypted to your Windows account. It never leaves this machine except in requests to your provider.");

        AddTextRow("LLM base URL", Settings.BaseUrl,
            value => Update(s => s with { BaseUrl = value }),
            "Used for cleanup and context. Point this at Ollama or LM Studio to run locally.");

        AddTextRow("Transcription base URL", Settings.TranscriptionBaseUrl,
            value => Update(s => s with { TranscriptionBaseUrl = value }),
            "Leave empty to use the LLM base URL. Set it only when speech and text run on different providers.");

        AddComboRow("Transcription model", ModelConfiguration.TranscriptionModels,
            Settings.TranscriptionModel,
            value => Update(s => s with { TranscriptionModel = value }),
            "Turbo is faster; the full model is more accurate on accents and noise.");

        AddComboRow("Cleanup model", ModelConfiguration.LlmModels,
            Settings.PostProcessingModel,
            value => Update(s => s with { PostProcessingModel = value }),
            "Leave empty to use the default. Cleanup runs on every dictation, so speed matters more than raw capability.");

        AddComboRow("Fallback model", ModelConfiguration.LlmModels,
            Settings.PostProcessingFallbackModel,
            value => Update(s => s with { PostProcessingFallbackModel = value }),
            "Tried once when the cleanup model is rate-limited or returns nothing.");
    }

    private void BuildShortcutsSection()
    {
        AddHeading("Shortcuts",
            "Hold the first shortcut to talk, or add the toggle modifiers to latch recording on so you can let go.");

        AddNote(
            "The Fn key cannot be used on Windows. On nearly all keyboards it is handled in firmware and never reaches the operating system, so no application can detect it. Right Ctrl is the default in its place.");

        AddPresetRow("Hold to talk", Settings.HoldShortcut,
            binding => Update(s => s with { HoldShortcut = binding }),
            "Recording runs while this key is held.");

        AddPresetRow("Tap to toggle", Settings.ToggleShortcut,
            binding => Update(s => s with { ToggleShortcut = binding }),
            "Press once to start, once again to stop. If this extends the hold shortcut, adding its extra keys mid-hold latches recording on.");

        AddPresetRow("Paste again", Settings.PasteAgainShortcut,
            binding => Update(s => s with { PasteAgainShortcut = binding }),
            "Re-pastes the last transcript without recording again.");
    }

    private void BuildDictationSection()
    {
        AddHeading("Dictation",
            "What happens to your words between the microphone and the cursor.");

        AddCheckRow("Clean up transcripts", Settings.PostProcessingEnabled,
            value => Update(s => s with { PostProcessingEnabled = value }),
            "Removes filler, fixes punctuation, and applies your vocabulary. Turning this off pastes the raw transcript.");

        AddCheckRow("Preserve exact wording", Settings.PreserveExactWording,
            value => Update(s => s with { PreserveExactWording = value }),
            "Skips cleanup entirely. Use this when you need every word as spoken, including the ums.");

        AddCheckRow("Guard against answered instructions", Settings.InstructionExecutionGuardEnabled,
            value => Update(s => s with { InstructionExecutionGuardEnabled = value }),
            "If cleanup answers your dictation instead of cleaning it, FreeFlow pastes what you actually said.");

        AddTextRow("Spoken language", Settings.TranscriptionLanguage,
            value => Update(s => s with { TranscriptionLanguage = value }),
            "An ISO code such as en or de. Leave empty to detect it automatically.");

        AddTextRow("Output language", Settings.OutputLanguage,
            value => Update(s => s with { OutputLanguage = value }),
            "Translates the result before pasting. Leave empty to keep the language you spoke.");

        AddMultilineRow("Custom vocabulary", Settings.CustomVocabulary,
            value => Update(s => s with { CustomVocabulary = value }),
            "Names, jargon, and project terms to spell correctly. Separate with commas or new lines.",
            height: 90);

        AddMultilineRow("Custom cleanup prompt", Settings.CustomSystemPrompt,
            value => Update(s => s with { CustomSystemPrompt = value }),
            "Replaces the built-in cleanup instructions. Leave empty to use the default.",
            height: 140);
    }

    private void BuildEditModeSection()
    {
        AddHeading("Edit Mode",
            "Select text, dictate an instruction like \"make this shorter\", and FreeFlow replaces the selection with the result.");

        AddNote(
            "Edit Mode reads your selection through Windows UI Automation. It works in most native apps, Office, and browsers, but not in applications that draw their own text without exposing it, including some terminals.");

        var triggers = new[] { "Off", "Whenever text is selected", "Only with an extra key" };
        var current = triggers[(int)Settings.EditMode];

        AddComboRow("Trigger", triggers, current, value =>
        {
            var mode = Array.IndexOf(triggers, value) switch
            {
                1 => EditModeTrigger.Automatic,
                2 => EditModeTrigger.Manual,
                _ => EditModeTrigger.Disabled,
            };
            Update(s => s with { EditMode = mode });
            ShowSection(3);
        },
        "Requiring an extra key avoids rewriting a selection you did not mean to edit.");

        if (Settings.EditMode == EditModeTrigger.Manual)
        {
            var modifiers = new[] { "Alt", "Shift", "Ctrl", "Win" };
            var currentModifier = Settings.EditModeModifier switch
            {
                ShortcutModifiers.Shift => "Shift",
                ShortcutModifiers.Control => "Ctrl",
                ShortcutModifiers.Windows => "Win",
                _ => "Alt",
            };

            AddComboRow("Extra key", modifiers, currentModifier, value =>
            {
                var modifier = value switch
                {
                    "Shift" => ShortcutModifiers.Shift,
                    "Ctrl" => ShortcutModifiers.Control,
                    "Win" => ShortcutModifiers.Windows,
                    _ => ShortcutModifiers.Alt,
                };
                Update(s => s with { EditModeModifier = modifier });
            },
            "Hold this along with your dictation shortcut to edit the selection.");
        }
    }

    private void BuildContextSection()
    {
        AddHeading("Context",
            "FreeFlow can look at the app you are dictating into so names and terms come out spelled the way they appear on screen.");

        AddCheckRow("Read app context", Settings.ContextAwarenessEnabled,
            value => Update(s => s with { ContextAwarenessEnabled = value }),
            "Sends the app name, window title, and any selected text to your provider alongside the transcript.");

        AddCheckRow("Include a screenshot", Settings.ContextScreenshotsEnabled,
            value => Update(s => s with { ContextScreenshotsEnabled = value }),
            "Captures the focused window and sends it to the context model. This sends a picture of your screen to your provider, so leave it off unless the accuracy is worth that.");

        AddComboRow("Context model", ModelConfiguration.VisionModels,
            Settings.ContextModel,
            value => Update(s => s with { ContextModel = value }),
            "Must support image input for screenshots to work.");

        AddMultilineRow("Custom context prompt", Settings.CustomContextPrompt,
            value => Update(s => s with { CustomContextPrompt = value }),
            "Replaces the built-in context instructions. Leave empty to use the default.",
            height: 120);
    }

    private void BuildAudioSection()
    {
        AddHeading("Audio", "Which microphone FreeFlow listens to, and what you see while it does.");

        var devices = AudioDevices.CaptureDevices();
        var names = new List<string> { "System default" };
        names.AddRange(devices.Select(device => device.Name));

        var currentName = devices.FirstOrDefault(device => device.Id == Settings.InputDeviceId)?.Name
            ?? "System default";

        AddComboRow("Microphone", names, currentName, value =>
        {
            var id = devices.FirstOrDefault(device => device.Name == value)?.Id ?? "";
            Update(s => s with { InputDeviceId = id });
        },
        "System default follows whatever Windows is using, including headsets you plug in later.");

        AddCheckRow("Show the recording overlay", Settings.ShowRecordingOverlay,
            value => Update(s => s with { ShowRecordingOverlay = value }),
            "A small capsule near the bottom of the screen showing live microphone level. It never takes focus.");

        AddCheckRow("Play sounds", Settings.PlaySounds,
            value => Update(s => s with { PlaySounds = value }),
            "A short tone when recording starts and stops.");
    }

    private void BuildAdvancedSection()
    {
        AddHeading("Advanced", "Startup, clipboard behavior, timeouts, and local history.");

        AddCheckRow("Start FreeFlow when I sign in", Settings.LaunchAtLogin,
            value => Update(s => s with { LaunchAtLogin = value }),
            "Adds FreeFlow to your startup apps. You can also turn this off from the Startup tab in Task Manager.");

        AddCheckRow("Restore my clipboard after pasting", Settings.PreserveClipboard,
            value => Update(s => s with { PreserveClipboard = value }),
            "Pasting uses the clipboard. With this on, whatever you had copied comes back afterwards.");

        AddCheckRow("Keep dictations in clipboard history", Settings.KeepDictationInClipboardHistory,
            value => Update(s => s with { KeepDictationInClipboardHistory = value }),
            "Off by default, so dictated text stays out of Windows clipboard history and cloud sync.");

        AddNumberRow("Transcription timeout", Settings.TranscriptionTimeoutSeconds,
            value => Update(s => s with { TranscriptionTimeoutSeconds = value }),
            "Seconds. Raise this for local models, which are slow on the first request after idling.");

        AddNumberRow("Cleanup timeout", Settings.PostProcessingTimeoutSeconds,
            value => Update(s => s with { PostProcessingTimeoutSeconds = value }), "Seconds.");

        AddNumberRow("Context timeout", Settings.ContextRequestTimeoutSeconds,
            value => Update(s => s with { ContextRequestTimeoutSeconds = value }), "Seconds.");

        AddCheckRow("Record pipeline history", Settings.PipelineDebugPanelEnabled,
            value => Update(s => s with { PipelineDebugPanelEnabled = value }),
            "Saves each dictation's transcript, prompts, and context to this machine for debugging. Off by default.");

        var clearButton = new Button
        {
            Content = "Clear pipeline history",
            Style = (Style)FindResource("QuietButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        clearButton.Click += (_, _) =>
        {
            _state.History.Clear();
            clearButton.Content = "History cleared";
            clearButton.IsEnabled = false;
        };
        AddRow("Local history", clearButton,
            "Deletes every recorded dictation, prompt, and screenshot from this machine.");
    }

    // MARK: Row builders

    private void AddHeading(string title, string description)
    {
        SectionHost.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("TitleText"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        SectionHost.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("BodyText"),
            Foreground = (System.Windows.Media.Brush)FindResource("InkDim"),
            Margin = new Thickness(0, 0, 0, 26),
        });
    }

    /// <summary>A standing constraint the user should know about, not a warning.</summary>
    private void AddNote(string text)
    {
        var note = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceSunken"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 22),
            Child = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("BodyText"),
                Foreground = (System.Windows.Media.Brush)FindResource("InkDim"),
                FontSize = 12,
            },
        };

        SectionHost.Children.Add(note);
    }

    private void AddRow(string label, UIElement control, string? caption = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("HeadingText"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 7),
        });

        panel.Children.Add(control);

        if (caption is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                Style = (Style)FindResource("CaptionText"),
            });
        }

        SectionHost.Children.Add(panel);
    }

    private void AddTextRow(string label, string value, Action<string> onChanged, string caption)
    {
        var box = new TextBox { Text = value };
        box.LostFocus += (_, _) => onChanged(box.Text);
        AddRow(label, box, caption);
    }

    private void AddMultilineRow(string label, string value, Action<string> onChanged, string caption, double height)
    {
        var box = new TextBox
        {
            Text = value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = height,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        box.LostFocus += (_, _) => onChanged(box.Text);
        AddRow(label, box, caption);
    }

    private void AddNumberRow(string label, double value, Action<double> onChanged, string caption)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        box.LostFocus += (_, _) =>
        {
            // Ignore unparseable input rather than resetting to zero, which would
            // silently disable the timeout.
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                onChanged(parsed);
            }
            else
            {
                box.Text = value.ToString(CultureInfo.InvariantCulture);
            }
        };
        AddRow(label, box, caption);
    }

    private void AddCheckRow(string label, bool value, Action<bool> onChanged, string caption)
    {
        var check = new CheckBox { Content = label, IsChecked = value };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(check);
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(26, 4, 0, 0),
        });

        SectionHost.Children.Add(panel);
    }

    private void AddComboRow(
        string label,
        IEnumerable<string> options,
        string current,
        Action<string> onChanged,
        string caption)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in options) combo.Items.Add(option);

        if (current.Length > 0 && !combo.Items.Contains(current)) combo.Items.Insert(0, current);
        combo.SelectedItem = current.Length == 0 ? null : current;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected) onChanged(selected);
        };

        AddRow(label, combo, caption);
    }

    /// <summary>
    /// A shortcut chooser: preset list plus a live keycap preview of the result.
    /// </summary>
    private void AddPresetRow(
        string label,
        ShortcutBinding binding,
        Action<ShortcutBinding> onChanged,
        string caption)
    {
        var presets = Enum.GetValues<ShortcutPreset>();
        var options = new List<string> { "Disabled" };
        options.AddRange(presets.Select(preset => preset.Title()));

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedItem = binding.IsDisabled ? "Disabled" : binding.SelectionTitle;

        var preview = new KeycapView { Binding = binding, Margin = new Thickness(0, 10, 0, 0) };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string selected) return;

            var updated = selected == "Disabled"
                ? ShortcutBinding.Disabled
                : presets.First(preset => preset.Title() == selected).Binding();

            // The toggle shortcut has to stay distinguishable from the hold shortcut,
            // so it keeps the extra modifier that separates the two.
            if (label.StartsWith("Tap", StringComparison.Ordinal) && !updated.IsDisabled)
            {
                updated = updated.WithAddedModifiers(ShortcutModifiers.Shift);
            }

            preview.Binding = updated;
            onChanged(updated);
        };

        var panel = new StackPanel();
        panel.Children.Add(combo);
        panel.Children.Add(preview);

        AddRow(label, panel, caption);
    }
}
