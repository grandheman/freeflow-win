using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using FreeFlow.Core.Transcription;

namespace FreeFlow.App.UI;

/// <summary>
/// First-run setup.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/SetupView.swift</c>, reduced to the two things
/// that actually block a new user: a working API key, and knowing which key to hold.
/// </para>
/// <para>
/// The key is validated against the provider before setup can be finished, because a
/// typo would otherwise surface much later as a failed dictation with no obvious cause.
/// </para>
/// </remarks>
public partial class SetupWindow : Window
{
    private readonly AppState _state;
    private CancellationTokenSource? _validationCancellation;

    public SetupWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        HoldKeys.Binding = _state.Settings.HoldShortcut;
        ToggleKeys.Binding = _state.Settings.ToggleShortcut;

        ApiKeyBox.Password = _state.ApiKey;
        if (_state.HasApiKey) _ = ValidateAsync(_state.ApiKey);
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        _state.SetApiKey(key);

        FinishButton.IsEnabled = false;

        if (key.Length == 0)
        {
            SetValidationMessage("Your key is stored encrypted to your Windows account.", isError: false);
            FinishHint.Text = "Add your API key to continue.";
            return;
        }

        _ = ValidateAsync(key);
    }

    /// <summary>
    /// Checks the key against the provider, debounced so typing does not fire a
    /// request per keystroke.
    /// </summary>
    private async Task ValidateAsync(string key)
    {
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var token = _validationCancellation.Token;

        SetValidationMessage("Checking the key…", isError: false);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(true);

            var isValid = await TranscriptionService
                .ValidateApiKeyAsync(key, _state.Settings.EffectiveTranscriptionBaseUrl, cancellationToken: token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            if (isValid)
            {
                SetValidationMessage("Key accepted.", isError: false);
                FinishButton.IsEnabled = true;
                FinishHint.Text = "You can change any of this later in Settings.";
            }
            else
            {
                SetValidationMessage(
                    "That key was rejected. Check that you copied all of it, and that it belongs to the provider set in Settings.",
                    isError: true);
                FinishHint.Text = "Add a working API key to continue.";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private void SetValidationMessage(string message, bool isError)
    {
        ValidationLabel.Text = message;
        ValidationLabel.Foreground = isError
            ? (Brush)FindResource("Danger")
            : (Brush)FindResource("InkDim");
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        _state.UpdateSettings(_state.Settings with { HasCompletedSetup = true });
        Close();
    }

    private void OnLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        // UseShellExecute is required to hand a URL to the default browser.
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
