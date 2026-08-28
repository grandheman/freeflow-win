using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FreeFlow.App.Platform.Audio;
using FreeFlow.App.Platform.Context;
using FreeFlow.App.Platform.Input;
using FreeFlow.App.Platform.Host;
using FreeFlow.App.Platform.Text;
using FreeFlow.Core.Context;
using FreeFlow.Core.History;
using FreeFlow.Core.Models;
using FreeFlow.Core.PostProcessing;
using FreeFlow.Core.Settings;
using FreeFlow.Core.Shortcuts;
using FreeFlow.Core.Storage;
using FreeFlow.Core.Transcription;

namespace FreeFlow.App;

public enum PipelineStatus
{
    Idle,
    Recording,
    Transcribing,
    Error,
}

/// <summary>
/// Owns the dictation pipeline and every piece of shared state the UI binds to.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to <c>Sources/AppState.swift</c>. The flow is:
/// capture the current selection, start recording, collect app context concurrently,
/// then on stop transcribe, clean up (or transform the selection in Edit Mode), and
/// paste.
/// </para>
/// <para>
/// Context collection runs while the user is still speaking. That is not an
/// optimization detail; it is what keeps the pause between releasing the key and the
/// text appearing short, because the context round trip overlaps the recording
/// instead of adding to the wait.
/// </para>
/// </remarks>
public sealed class AppState : INotifyPropertyChanged, IDisposable
{
    private readonly JsonKeyValueStore _store;
    private readonly DpapiSecretStore _secrets;
    private readonly PipelineHistoryStore _history;
    private readonly LlmCooldownManager _cooldowns;

    private readonly WasapiAudioRecorder _recorder = new();
    private readonly WindowsShortcutBackend _shortcutBackend = new();
    private readonly ForegroundAppInspector _inspector = new();
    private readonly DictationShortcutSessionController _sessionController = new();

    private ShortcutInputState _shortcutState = new();
    private AppSettings _settings;

    private CancellationTokenSource? _pipelineCancellation;
    private Task<DictationContext>? _contextTask;
    private AppSelectionSnapshot _pendingSelection = AppSelectionSnapshot.Empty;
    private string? _activeRecordingPath;
    private string _lastPastedTranscript = "";

    private const string ApiKeySecretName = "provider_api_key";
    private const string SettingsKey = "app_settings";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the pipeline fails, for surfacing in the tray UI.</summary>
    public event Action<string>? ErrorRaised;

    public AppState()
    {
        AppPaths.EnsureCreated();

        _store = new JsonKeyValueStore();
        _secrets = new DpapiSecretStore();
        _history = new PipelineHistoryStore(AppPaths.HistoryFile);
        _cooldowns = new LlmCooldownManager(_store);

        _settings = (_store.GetObject<AppSettings>(SettingsKey) ?? new AppSettings()).Migrated();

        _recorder.LevelChanged += level => AudioLevel = level;
        _recorder.Failed += error =>
        {
            DiagnosticLog.WriteError("recorder.failed", error);
            Fail(error.Message);
        };

        DiagnosticLog.Start();
        DiagnosticLog.Write("app.start", $"hasKey={HasApiKey} contextAware={_settings.ContextAwarenessEnabled} cleanup={_settings.PostProcessingEnabled}");
    }

    // MARK: Observable state

    private PipelineStatus _status = PipelineStatus.Idle;
    public PipelineStatus Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    private float _audioLevel;
    public float AudioLevel
    {
        get => _audioLevel;
        private set => Set(ref _audioLevel, value);
    }

    private RecordingTriggerMode _triggerMode = RecordingTriggerMode.Hold;
    public RecordingTriggerMode TriggerMode
    {
        get => _triggerMode;
        private set => Set(ref _triggerMode, value);
    }

    public bool IsRecording => Status == PipelineStatus.Recording;

    public AppSettings Settings
    {
        get => _settings;
        private set => Set(ref _settings, value);
    }

    public PipelineHistoryStore History => _history;

    public string ApiKey => _secrets.Get(ApiKeySecretName) ?? "";

    public bool HasApiKey => ApiKey.Trim().Length > 0;

    // MARK: Lifecycle

    /// <summary>Installs the keyboard hook. Throws when the hook cannot be installed.</summary>
    public void Start()
    {
        _shortcutBackend.OnInputEvent = HandleInputEvent;
        _shortcutBackend.OnEscapePressed = HandleEscapePressed;
        _shortcutBackend.Start();
    }

    public void Stop()
    {
        _shortcutBackend.Stop();
        CancelPipeline();
    }

    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings.Migrated();
        _store.SetObject(SettingsKey, Settings);
        StartupManager.SetEnabled(Settings.LaunchAtLogin);
    }

    public void SetApiKey(string apiKey)
    {
        _secrets.Set(ApiKeySecretName, apiKey.Trim());
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(HasApiKey));
    }

    // MARK: Shortcut handling

    /// <summary>
    /// Reduces one raw key event and acts on whatever the matcher emits.
    /// </summary>
    /// <remarks>
    /// Runs on the keyboard-hook thread, so it must return quickly. Pipeline work is
    /// dispatched rather than awaited here.
    /// </remarks>
    private ShortcutConsumeDecision HandleInputEvent(ShortcutInputEvent inputEvent)
    {
        var configuration = Settings.ShortcutConfiguration with
        {
            // While a session is live, tolerate the extra modifiers that promote the
            // hold shortcut to the toggle shortcut, so latching does not read as a stop.
            PermittedAdditionalExactMatchModifiers = _sessionController.ActiveMode is null
                ? ShortcutModifiers.None
                : Settings.ToggleShortcut.Modifiers,
        };

        var result = ShortcutMatcher.Reduce(_shortcutState, inputEvent, configuration);
        _shortcutState = result.State;

        foreach (var shortcutEvent in result.EmittedEvents)
        {
            if (shortcutEvent == ShortcutEvent.PasteAgainTriggered)
            {
                _ = Task.Run(PasteAgainAsync);
                continue;
            }

            var action = _sessionController.Handle(shortcutEvent, Status == PipelineStatus.Transcribing);
            DiagnosticLog.Write("shortcut",
                $"{shortcutEvent} -> {action?.GetType().Name ?? "none"} (mode={_sessionController.ActiveMode?.ToString() ?? "idle"}, status={Status})");
            DispatchAction(action);
        }

        return result.ConsumeDecision;
    }

    private void DispatchAction(DictationShortcutAction? action)
    {
        switch (action)
        {
            case DictationShortcutAction.Start start:
                TriggerMode = start.Mode;
                _ = Task.Run(() => BeginRecording(start.Mode));
                break;

            case DictationShortcutAction.Stop:
                _ = Task.Run(FinishRecordingAsync);
                break;

            case DictationShortcutAction.SwitchedToToggle:
                TriggerMode = RecordingTriggerMode.Toggle;
                break;
        }
    }

    /// <summary>Escape cancels an in-flight dictation and is swallowed only then.</summary>
    private bool HandleEscapePressed()
    {
        if (Status is not (PipelineStatus.Recording or PipelineStatus.Transcribing)) return false;

        CancelPipeline();
        return true;
    }

    // MARK: Pipeline

    private void BeginRecording(RecordingTriggerMode mode)
    {
        try
        {
            // Clear any leftover work without ending the shortcut session that the
            // hook thread just started for this recording.
            DiscardPipelineWork();
            _pipelineCancellation = new CancellationTokenSource();

            // Capture the selection before recording starts. Once the overlay appears
            // and focus shifts, the original selection may no longer be readable.
            _pendingSelection = _inspector.CollectSelectionSnapshot();

            var path = Path.Combine(
                AppPaths.RecordingsDirectory,
                $"dictation-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.wav");

            _recorder.Start(path, NullIfEmpty(Settings.InputDeviceId));
            _activeRecordingPath = path;
            DiagnosticLog.Write("record.start", $"mode={mode}");

            Status = PipelineStatus.Recording;
            StatusMessage = mode == RecordingTriggerMode.Hold ? "Listening" : "Listening (tap to stop)";

            if (Settings.PlaySounds) FeedbackSounds.PlayStart();

            // Overlaps the context round trip with the user still speaking.
            _contextTask = Settings.ContextAwarenessEnabled
                ? CollectContextAsync(_pipelineCancellation.Token)
                : Task.FromResult(DictationContext.Empty);
        }
        catch (Exception error)
        {
            DiagnosticLog.WriteError("record.start.failed", error);
            Fail(error.Message);
            _sessionController.Reset();
        }
    }

    private async Task<DictationContext> CollectContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var screenshot = Settings.ContextScreenshotsEnabled
                ? CaptureScreenshot()
                : ScreenshotCapture.None;

            var service = new AppContextService(Settings.ToContextOptions(ApiKey));
            return await service.CollectContextAsync(_pendingSelection, screenshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Context is optional; never let it break a dictation.
            return DictationContext.Empty;
        }
    }

    private static ScreenshotCapture CaptureScreenshot()
    {
        var result = ScreenCapture.CaptureForegroundWindow();
        return new ScreenshotCapture(result.DataUrl, result.MimeType, result.Error);
    }

    private async Task FinishRecordingAsync()
    {
        var cancellation = _pipelineCancellation;
        if (cancellation is null)
        {
            DiagnosticLog.Write("finish.skipped", $"no active pipeline, status={Status}");
            // No pipeline to finish, but the UI may still be showing a recording
            // state. Returning silently here would leave the overlay stuck with no
            // way for the user to dismiss it.
            if (Status != PipelineStatus.Idle) Reset("Ready");
            _sessionController.Reset();
            return;
        }

        try
        {
            var recordingPath = _recorder.Stop();
            _activeRecordingPath = null;
            DiagnosticLog.Write("record.stop",
                recordingPath is null ? "no audio" : $"bytes={new FileInfo(recordingPath).Length}");

            if (Settings.PlaySounds) FeedbackSounds.PlayStop();

            if (recordingPath is null)
            {
                Reset("No audio captured");
                return;
            }

            Status = PipelineStatus.Transcribing;
            StatusMessage = "Transcribing";

            var token = cancellation.Token;

            var transcriptionService = new TranscriptionService(Settings.ToTranscriptionOptions(ApiKey));
            DiagnosticLog.Write("transcribe.begin");
            var rawTranscript = await transcriptionService.TranscribeAsync(recordingPath, token)
                .ConfigureAwait(false);
            DiagnosticLog.Write("transcribe.done", $"chars={rawTranscript.Trim().Length}");

            TryDeleteRecording(recordingPath);

            if (rawTranscript.Trim().Length == 0)
            {
                Reset("Nothing was said");
                return;
            }

            var context = _contextTask is null
                ? DictationContext.Empty
                : await _contextTask.ConfigureAwait(false);

            DiagnosticLog.Write("context.ready", $"chars={context.ContextSummary.Length}");

            var result = await RunTextStageAsync(rawTranscript, context, token).ConfigureAwait(false);
            DiagnosticLog.Write("cleanup.done", $"chars={result.Transcript.Trim().Length}");

            if (result.Transcript.Trim().Length == 0)
            {
                Reset("Nothing to paste");
                return;
            }

            DiagnosticLog.Write("paste.begin", $"chars={result.Transcript.Length}");
            await PasteAsync(result.Transcript, token).ConfigureAwait(false);
            DiagnosticLog.Write("paste.done");

            RecordHistory(rawTranscript, result, context);
            Reset("Ready");
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("pipeline.cancelled");
            Reset("Cancelled");
        }
        catch (Exception error)
        {
            DiagnosticLog.WriteError("pipeline.failed", error);
            Fail(error.Message);
        }
        finally
        {
            _sessionController.Reset();
        }
    }

    /// <summary>
    /// Chooses between Edit Mode, verbatim translation, plain cleanup, and no
    /// processing at all.
    /// </summary>
    private async Task<PostProcessingResult> RunTextStageAsync(
        string rawTranscript,
        DictationContext context,
        CancellationToken cancellationToken)
    {
        var service = new PostProcessingService(Settings.ToPostProcessingOptions(ApiKey), _cooldowns);

        if (ShouldTransformSelection())
        {
            StatusMessage = "Editing selection";
            return await service.TransformSelectionAsync(
                _pendingSelection.SelectedText!,
                rawTranscript,
                context.ContextSummary,
                Settings.CustomVocabulary,
                Settings.OutputLanguage,
                cancellationToken).ConfigureAwait(false);
        }

        if (Settings.PreserveExactWording)
        {
            // Skipping cleanup entirely would silently drop a configured output
            // language, so route through the translate-only prompt instead.
            if (Settings.OutputLanguage.Trim().Length > 0)
            {
                StatusMessage = "Translating";
                return await service.TranslateVerbatimAsync(
                    rawTranscript, Settings.OutputLanguage, cancellationToken).ConfigureAwait(false);
            }

            return new PostProcessingResult(rawTranscript.Trim(), "");
        }

        if (!Settings.PostProcessingEnabled)
        {
            return new PostProcessingResult(rawTranscript.Trim(), "");
        }

        StatusMessage = "Cleaning up";
        return await service.PostProcessAsync(
            rawTranscript,
            context.ContextSummary,
            Settings.CustomVocabulary,
            Settings.CustomSystemPrompt,
            Settings.OutputLanguage,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when this dictation is an edit instruction rather than text to insert.
    /// </summary>
    /// <remarks>
    /// Requires a real selection in both modes. Manual mode additionally requires the
    /// configured modifier, so a user who dictates with text selected does not have
    /// their selection unexpectedly rewritten.
    /// </remarks>
    private bool ShouldTransformSelection()
    {
        if (Settings.EditMode == EditModeTrigger.Disabled) return false;
        if (!_pendingSelection.HasSelectedText) return false;
        if (Settings.EditMode == EditModeTrigger.Automatic) return true;

        return _shortcutState.CurrentModifiers.Contains(Settings.EditModeModifier);
    }

    private async Task PasteAsync(string transcript, CancellationToken cancellationToken)
    {
        var paster = new ClipboardPaster
        {
            PreserveClipboard = Settings.PreserveClipboard,
            KeepDictationInClipboardHistory = Settings.KeepDictationInClipboardHistory,
        };

        await paster.PasteAsync(transcript, cancellationToken).ConfigureAwait(false);
        _lastPastedTranscript = transcript;
    }

    /// <summary>Re-pastes the last transcript without re-running the pipeline.</summary>
    public async Task PasteAgainAsync()
    {
        if (_lastPastedTranscript.Length == 0) return;

        try
        {
            await PasteAsync(_lastPastedTranscript, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail(error.Message);
        }
    }

    private void RecordHistory(string rawTranscript, PostProcessingResult result, DictationContext context)
    {
        if (!Settings.PipelineDebugPanelEnabled) return;

        _history.Append(new PipelineHistoryItem
        {
            Intent = ShouldTransformSelection()
                ? Settings.EditMode == EditModeTrigger.Manual
                    ? PipelineHistoryIntent.CommandManual
                    : PipelineHistoryIntent.CommandAutomatic
                : PipelineHistoryIntent.Dictation,
            SelectedText = _pendingSelection.SelectedText,
            RawTranscript = rawTranscript,
            PostProcessedTranscript = result.Transcript,
            PostProcessingPrompt = result.Prompt,
            SystemPrompt = Settings.CustomSystemPrompt,
            ContextSummary = context.ContextSummary,
            ContextSystemPrompt = context.ContextSystemPrompt,
            ContextPrompt = context.ContextPrompt,
            ContextScreenshotDataUrl = context.ScreenshotDataUrl,
            ContextScreenshotStatus = context.ScreenshotError ?? "available",
            PostProcessingStatus = "completed",
            DebugStatus = "ok",
            CustomVocabulary = Settings.CustomVocabulary,
            ContextAppName = context.AppName,
            ContextApplicationId = context.ApplicationId,
            ContextWindowTitle = context.WindowTitle,
        });
    }

    // MARK: Helpers

    /// <summary>
    /// Abandons any in-flight pipeline work and discards its recording.
    /// </summary>
    /// <remarks>
    /// Deliberately does not touch <see cref="_sessionController"/>. Starting a new
    /// recording calls this to clear leftovers, and the session it is starting was
    /// already registered by the shortcut layer moments earlier on the hook thread.
    /// Resetting the controller here would discard that session, so the later
    /// key-release would find no active mode, emit no stop action, and strand the
    /// recording with the overlay stuck on screen.
    /// </remarks>
    private void DiscardPipelineWork()
    {
        _pipelineCancellation?.Cancel();
        _pipelineCancellation?.Dispose();
        _pipelineCancellation = null;
        _contextTask = null;

        if (_recorder.IsRecording)
        {
            var path = _recorder.Stop();
            if (path is not null) TryDeleteRecording(path);
        }

        _activeRecordingPath = null;
    }

    /// <summary>
    /// Cancels the current dictation outright, as Escape does.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DiscardPipelineWork"/> this also ends the shortcut session,
    /// because the user is abandoning the dictation rather than starting another.
    /// </remarks>
    public void CancelPipeline()
    {
        DiscardPipelineWork();
        _sessionController.Reset();

        if (Status != PipelineStatus.Idle) Reset("Ready");
    }

    private static void TryDeleteRecording(string path)
    {
        try
        {
            // Recorded audio is deleted as soon as it has been transcribed; it is
            // never retained on disk beyond the request that needs it.
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A file still held by the upload will be cleaned up by the temp directory.
        }
    }

    private void Reset(string message)
    {
        Status = PipelineStatus.Idle;
        StatusMessage = message;
        AudioLevel = 0;
    }

    private void Fail(string message)
    {
        Status = PipelineStatus.Error;
        StatusMessage = message;
        AudioLevel = 0;
        ErrorRaised?.Invoke(message);
    }

    private static string? NullIfEmpty(string value) => value.Trim().Length == 0 ? null : value;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        Stop();
        _shortcutBackend.Dispose();
        _recorder.Dispose();
    }
}
