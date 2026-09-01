namespace FreeFlow.Core.Shortcuts;

/// <summary>What the app should do in response to a shortcut transition.</summary>
public abstract record DictationShortcutAction
{
    public sealed record Start(RecordingTriggerMode Mode) : DictationShortcutAction;
    public sealed record Stop : DictationShortcutAction;
    /// <summary>An in-flight hold session latched into toggle mode; keep recording.</summary>
    public sealed record SwitchedToToggle : DictationShortcutAction;
}

/// <summary>
/// Tracks whether a dictation session is running and in which trigger mode.
/// </summary>
/// <remarks>
/// Ported from <c>Sources/ShortcutCore/DictationShortcutSessionController.swift</c>.
/// The interesting case is latching: while holding the hold shortcut, pressing the
/// extra modifiers that form the toggle shortcut converts the session to toggle mode
/// so the user can let go and keep talking.
/// </remarks>
public sealed class DictationShortcutSessionController
{
    public RecordingTriggerMode? ActiveMode { get; private set; }

    /// <summary>
    /// In toggle mode the shortcut must be fully released once before a second press
    /// can stop the session, otherwise the press that started it would also end it.
    /// </summary>
    public bool ToggleStopArmed { get; private set; }

    public DictationShortcutAction? Handle(ShortcutEvent shortcutEvent, bool isTranscribing)
    {
        // Paste Again is handled before this controller runs; if it ever reaches
        // here, treat it as a no-op so dictation state is unaffected.
        if (shortcutEvent == ShortcutEvent.PasteAgainTriggered) return null;

        if (ActiveMode is null)
        {
            if (isTranscribing) return null;

            return shortcutEvent switch
            {
                ShortcutEvent.ToggleActivated => StartToggle(),
                ShortcutEvent.HoldActivated => StartHold(),
                _ => null,
            };
        }

        return ActiveMode switch
        {
            RecordingTriggerMode.Hold => HandleWhileHolding(shortcutEvent),
            RecordingTriggerMode.Toggle => HandleWhileToggled(shortcutEvent),
            _ => null,
        };
    }

    private DictationShortcutAction StartToggle()
    {
        ActiveMode = RecordingTriggerMode.Toggle;
        ToggleStopArmed = false;
        return new DictationShortcutAction.Start(RecordingTriggerMode.Toggle);
    }

    private DictationShortcutAction StartHold()
    {
        ActiveMode = RecordingTriggerMode.Hold;
        ToggleStopArmed = false;
        return new DictationShortcutAction.Start(RecordingTriggerMode.Hold);
    }

    private DictationShortcutAction? HandleWhileHolding(ShortcutEvent shortcutEvent)
    {
        switch (shortcutEvent)
        {
            case ShortcutEvent.ToggleActivated:
                ActiveMode = RecordingTriggerMode.Toggle;
                ToggleStopArmed = false;
                return new DictationShortcutAction.SwitchedToToggle();

            case ShortcutEvent.HoldDeactivated:
                Reset();
                return new DictationShortcutAction.Stop();

            default:
                return null;
        }
    }

    private DictationShortcutAction? HandleWhileToggled(ShortcutEvent shortcutEvent)
    {
        switch (shortcutEvent)
        {
            case ShortcutEvent.ToggleDeactivated:
                ToggleStopArmed = true;
                return null;

            case ShortcutEvent.ToggleActivated:
                if (!ToggleStopArmed) return null;
                Reset();
                return new DictationShortcutAction.Stop();

            default:
                return null;
        }
    }

    public void BeginManual(RecordingTriggerMode mode)
    {
        ActiveMode = mode;
        ToggleStopArmed = false;
    }

    public void ForceToggleMode()
    {
        ActiveMode = RecordingTriggerMode.Toggle;
        ToggleStopArmed = false;
    }

    public void Reset()
    {
        ActiveMode = null;
        ToggleStopArmed = false;
    }
}
