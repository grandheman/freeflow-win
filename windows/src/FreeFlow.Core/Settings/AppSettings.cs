using FreeFlow.Core.Context;
using FreeFlow.Core.Net;
using FreeFlow.Core.PostProcessing;
using FreeFlow.Core.Shortcuts;
using FreeFlow.Core.Transcription;

namespace FreeFlow.Core.Settings;

/// <summary>How Edit Mode decides whether a dictation is an edit instruction.</summary>
public enum EditModeTrigger
{
    /// <summary>Edit Mode is off; dictation always inserts text.</summary>
    Disabled,
    /// <summary>Any dictation made while text is selected is treated as an edit instruction.</summary>
    Automatic,
    /// <summary>An edit requires the extra modifier as well as a selection.</summary>
    Manual,
}

/// <summary>
/// Everything the user can configure.
/// </summary>
/// <remarks>
/// Persisted as JSON. The API key is deliberately absent: it lives in the encrypted
/// credential store instead, so a settings file that gets synced, backed up, or
/// attached to a bug report never carries a secret.
/// </remarks>
public sealed record AppSettings
{
    // MARK: Provider

    public string BaseUrl { get; init; } = "https://api.groq.com/openai/v1";

    /// <summary>Separate endpoint for transcription, when the provider differs from the LLM.</summary>
    public string TranscriptionBaseUrl { get; init; } = "";

    public string TranscriptionModel { get; init; } = TranscriptionOptions.DefaultTranscriptionModel;
    public string PostProcessingModel { get; init; } = "";
    public string PostProcessingFallbackModel { get; init; } = "";
    public string ContextModel { get; init; } = AppContextOptions.DefaultContextModel;

    /// <summary>Spoken-language hint for transcription. Empty means auto-detect.</summary>
    public string TranscriptionLanguage { get; init; } = "";

    /// <summary>Target language for output. Empty means keep the spoken language.</summary>
    public string OutputLanguage { get; init; } = "";

    // MARK: Behavior

    public bool PostProcessingEnabled { get; init; } = true;

    /// <summary>Skips cleanup entirely and pastes the raw transcript.</summary>
    public bool PreserveExactWording { get; init; }

    public bool ContextAwarenessEnabled { get; init; } = true;

    /// <summary>Include a screenshot with the context request. Requires a vision-capable context model.</summary>
    public bool ContextScreenshotsEnabled { get; init; }

    public bool InstructionExecutionGuardEnabled { get; init; } = true;

    public string CustomVocabulary { get; init; } = "";
    public string CustomSystemPrompt { get; init; } = "";
    public string CustomContextPrompt { get; init; } = "";

    public EditModeTrigger EditMode { get; init; } = EditModeTrigger.Disabled;

    /// <summary>Extra modifier that marks a dictation as an edit instruction in manual Edit Mode.</summary>
    public ShortcutModifiers EditModeModifier { get; init; } = ShortcutModifiers.Alt;

    // MARK: Clipboard

    public bool PreserveClipboard { get; init; } = true;
    public bool KeepDictationInClipboardHistory { get; init; }

    // MARK: Audio

    /// <summary>Saved capture endpoint id. Empty means follow the system default.</summary>
    public string InputDeviceId { get; init; } = "";

    public bool PlaySounds { get; init; } = true;

    // MARK: Shortcuts

    public ShortcutBinding HoldShortcut { get; init; } = ShortcutBinding.DefaultHold;
    public ShortcutBinding ToggleShortcut { get; init; } = ShortcutBinding.DefaultToggle;
    public ShortcutBinding PasteAgainShortcut { get; init; } = ShortcutBinding.Disabled;

    // MARK: Application

    public bool LaunchAtLogin { get; init; }
    public bool ShowRecordingOverlay { get; init; } = true;
    public bool PipelineDebugPanelEnabled { get; init; }

    // MARK: Timeouts

    public double TranscriptionTimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;
    public double PostProcessingTimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;
    public double ContextRequestTimeoutSeconds { get; init; } = TimeoutSettings.DefaultSeconds;

    // MARK: Setup state

    /// <summary>False until the setup flow has been completed at least once.</summary>
    public bool HasCompletedSetup { get; init; }

    /// <summary>
    /// Version stamp of the built-in cleanup prompt the user last saw, so an updated
    /// default prompt can be offered without silently overwriting a customized one.
    /// </summary>
    public string SeenSystemPromptDate { get; init; } = "";

    public string SeenContextPromptDate { get; init; } = "";

    /// <summary>Endpoint actually used for transcription.</summary>
    public string EffectiveTranscriptionBaseUrl
        => TranscriptionBaseUrl.Trim().Length == 0 ? BaseUrl : TranscriptionBaseUrl;

    public ShortcutConfiguration ShortcutConfiguration => new()
    {
        Hold = HoldShortcut,
        Toggle = ToggleShortcut,
        PasteAgain = PasteAgainShortcut,
    };

    /// <summary>
    /// Repairs bindings written by an older version.
    /// </summary>
    /// <remarks>
    /// Bindings are the one part of settings where a stale shape silently breaks the
    /// hotkey rather than showing a visible default, so they are normalized on load.
    /// </remarks>
    public AppSettings Migrated() => this with
    {
        HoldShortcut = HoldShortcut.NormalizedForStorageMigration(),
        ToggleShortcut = ToggleShortcut.NormalizedForStorageMigration(),
        PasteAgainShortcut = PasteAgainShortcut.NormalizedForStorageMigration(),
    };

    public TranscriptionOptions ToTranscriptionOptions(string apiKey) => new()
    {
        ApiKey = apiKey,
        BaseUrl = EffectiveTranscriptionBaseUrl,
        TranscriptionModel = TranscriptionModel,
        Language = TranscriptionLanguage.Trim().Length == 0 ? null : TranscriptionLanguage.Trim(),
        TimeoutSeconds = TranscriptionTimeoutSeconds,
    };

    public PostProcessingOptions ToPostProcessingOptions(string apiKey) => new()
    {
        ApiKey = apiKey,
        BaseUrl = BaseUrl,
        PreferredModel = PostProcessingModel,
        PreferredFallbackModel = PostProcessingFallbackModel,
        InstructionExecutionGuardEnabled = InstructionExecutionGuardEnabled,
        TimeoutSeconds = PostProcessingTimeoutSeconds,
    };

    public AppContextOptions ToContextOptions(string apiKey) => new()
    {
        ApiKey = apiKey,
        BaseUrl = BaseUrl,
        CustomContextPrompt = CustomContextPrompt,
        ContextModel = ContextModel,
        TimeoutSeconds = ContextRequestTimeoutSeconds,
    };
}
