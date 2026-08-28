using System;
using System.Threading;
using System.Windows;
using FreeFlow.App.UI;

namespace FreeFlow.App;

public partial class App : Application
{
    /// <summary>
    /// Guarantees a single instance.
    /// </summary>
    /// <remarks>
    /// Two copies would install two keyboard hooks and both would react to the same
    /// keypress, producing a doubled transcript. The mutex is the cheapest way to
    /// make that impossible.
    /// </remarks>
    private static Mutex? _singleInstanceMutex;

    private AppState? _state;
    private TrayIcon? _tray;
    private RecordingOverlay? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "FreeFlow.SingleInstance", out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "FreeFlow is already running. Look for it in the notification area.",
                "FreeFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ThemeManager.ApplySystemTheme(this);

        _state = new AppState();

        // BeginInvoke, not Invoke. These fire from the audio capture thread and the
        // keyboard hook thread. A blocking Invoke from either would stall that thread
        // whenever the UI is busy, and stalling the hook thread freezes keyboard input
        // for every application on the machine.
        _state.ErrorRaised += message =>
            Dispatcher.BeginInvoke(() => _tray?.ShowError(message));
        _state.PropertyChanged += (_, args) =>
            Dispatcher.BeginInvoke(() => OnStateChanged(args.PropertyName));

        _overlay = new RecordingOverlay();
        _tray = new TrayIcon(_state);
        _tray.OpenSettingsRequested += ShowSettings;
        _tray.OpenDebugPanelRequested += ShowDebugPanel;
        _tray.QuitRequested += Shutdown;

        try
        {
            _state.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"FreeFlow could not start listening for its shortcut.\n\n{error.Message}",
                "FreeFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // First run has no API key, so setup is the only useful thing to show.
        if (!_state.Settings.HasCompletedSetup || !_state.HasApiKey)
        {
            ShowSetup();
        }
    }

    private void OnStateChanged(string? propertyName)
    {
        if (_state is null || _overlay is null) return;

        switch (propertyName)
        {
            case nameof(AppState.Status):
                UpdateOverlayForStatus();
                _tray?.UpdateStatus();
                break;

            case nameof(AppState.AudioLevel):
                _overlay.SetLevel(_state.AudioLevel);
                break;

            case nameof(AppState.TriggerMode):
                _overlay.SetMode(_state.TriggerMode);
                break;

            case nameof(AppState.StatusMessage):
                if (_state.Status == PipelineStatus.Transcribing)
                {
                    _overlay.SetWorking(_state.StatusMessage);
                }
                _tray?.UpdateStatus();
                break;
        }
    }

    private void UpdateOverlayForStatus()
    {
        if (_state is null || _overlay is null) return;

        if (!_state.Settings.ShowRecordingOverlay)
        {
            _overlay.HideOverlay();
            return;
        }

        switch (_state.Status)
        {
            case PipelineStatus.Recording:
                _overlay.ShowForSession(_state.TriggerMode);
                break;

            case PipelineStatus.Transcribing:
                _overlay.SetWorking(_state.StatusMessage);
                break;

            default:
                _overlay.HideOverlay();
                break;
        }
    }

    private void ShowSettings()
    {
        if (_state is null) return;

        var window = new SettingsWindow(_state);
        window.Show();
        window.Activate();
    }

    private void ShowDebugPanel()
    {
        if (_state is null) return;

        var window = new DebugPanelWindow(_state.History);
        window.Show();
        window.Activate();
    }

    private void ShowSetup()
    {
        if (_state is null) return;

        var window = new SetupWindow(_state);
        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _state?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            // Only the instance that actually acquired the mutex may release it.
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
