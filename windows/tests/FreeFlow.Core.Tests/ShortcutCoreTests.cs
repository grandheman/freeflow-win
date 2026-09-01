using System.Collections.Generic;
using FreeFlow.Core.Shortcuts;
using Xunit;

namespace FreeFlow.Core.Tests;

/// <summary>
/// Ported from <c>Tests/ShortcutCoreTests.swift</c>, retargeted from macOS key codes
/// to Windows virtual-key codes. Every case in the Swift suite has a counterpart here.
/// </summary>
public class ShortcutCoreTests
{
    private static ShortcutMatchResult Modifier(ShortcutInputState state, ushort keyCode, bool isDown,
        ShortcutConfiguration configuration)
        => ShortcutMatcher.Reduce(state, new ShortcutInputEvent.ModifierChanged(keyCode, isDown), configuration);

    private static ShortcutMatchResult Key(ShortcutInputState state, ushort keyCode, bool isDown, bool isRepeat,
        ShortcutConfiguration configuration)
        => ShortcutMatcher.Reduce(state, new ShortcutInputEvent.KeyChanged(keyCode, isDown, isRepeat), configuration);

    [Fact]
    public void BareHoldKeyLifecycle()
    {
        var configuration = new ShortcutConfiguration { Hold = ShortcutBinding.DefaultHold };

        var down = Modifier(new ShortcutInputState(), VirtualKeys.RControl, true, configuration);
        var up = Modifier(down.State, VirtualKeys.RControl, false, configuration);

        Assert.Equal(new[] { ShortcutEvent.HoldActivated }, down.EmittedEvents);
        Assert.Equal(ShortcutConsumeDecision.Consume, down.ConsumeDecision);
        Assert.Equal(new[] { ShortcutEvent.HoldDeactivated }, up.EmittedEvents);
        Assert.Equal(ShortcutConsumeDecision.Consume, up.ConsumeDecision);
    }

    [Fact]
    public void DefaultShortcutSpecificityOrdering()
    {
        // The more specific toggle binding must activate before the hold binding,
        // and deactivate after it, so a hold session can latch into toggle mode.
        var configuration = ShortcutConfiguration.Default;

        var shiftDown = Modifier(new ShortcutInputState(), VirtualKeys.LShift, true, configuration);
        var holdKeyDown = Modifier(shiftDown.State, VirtualKeys.RControl, true, configuration);
        var holdKeyUp = Modifier(holdKeyDown.State, VirtualKeys.RControl, false, configuration);

        Assert.Equal(
            new[] { ShortcutEvent.ToggleActivated, ShortcutEvent.HoldActivated },
            holdKeyDown.EmittedEvents);
        Assert.Equal(
            new[] { ShortcutEvent.HoldDeactivated, ShortcutEvent.ToggleDeactivated },
            holdKeyUp.EmittedEvents);
    }

    [Fact]
    public void RightAltPresetIsSideSpecific()
    {
        var configuration = new ShortcutConfiguration { Hold = ShortcutPreset.RightAlt.Binding() };

        var leftAlt = Modifier(new ShortcutInputState(), VirtualKeys.LMenu, true, configuration);
        var rightAlt = Modifier(new ShortcutInputState(), VirtualKeys.RMenu, true, configuration);

        Assert.Empty(leftAlt.EmittedEvents);
        Assert.Equal(new[] { ShortcutEvent.HoldActivated }, rightAlt.EmittedEvents);
    }

    [Fact]
    public void ExactModifierMatching()
    {
        var bothControls = new HashSet<ushort> { VirtualKeys.LControl, VirtualKeys.RControl };

        Assert.True(
            ShortcutBinding.ExactModifierKeyCodesMatch(
                new HashSet<ushort> { VirtualKeys.RControl }, bothControls),
            "A generic Ctrl binding should accept Right Ctrl");

        Assert.True(
            ShortcutBinding.ExactModifierKeyCodesMatch(
                new HashSet<ushort> { VirtualKeys.LControl }, bothControls),
            "A generic Ctrl binding should accept Left Ctrl");

        Assert.False(
            ShortcutBinding.ExactModifierKeyCodesMatch(
                new HashSet<ushort> { VirtualKeys.LControl, VirtualKeys.LShift },
                new HashSet<ushort> { VirtualKeys.LControl }),
            "Unexpected Shift should invalidate an exact Ctrl binding");

        Assert.True(
            ShortcutBinding.ExactModifierKeyCodesMatch(
                new HashSet<ushort> { VirtualKeys.LControl, VirtualKeys.LShift },
                new HashSet<ushort> { VirtualKeys.LControl },
                ShortcutModifiers.Shift),
            "Explicitly permitted Shift should not invalidate an exact Ctrl binding");
    }

    [Fact]
    public void ReducerHonorsExactModifierMatching()
    {
        var binding = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.Control, ShortcutBindingKind.Key, null,
            new HashSet<ushort> { VirtualKeys.LControl });
        var configuration = new ShortcutConfiguration { Hold = binding };

        var rightControlState = Modifier(new ShortcutInputState(), VirtualKeys.RControl, true, configuration).State;
        var rightControlKey = Key(rightControlState, VirtualKeys.F5, true, false, configuration);
        Assert.Empty(rightControlKey.EmittedEvents);

        var leftControlState = Modifier(new ShortcutInputState(), VirtualKeys.LControl, true, configuration).State;
        var leftControlKey = Key(leftControlState, VirtualKeys.F5, true, false, configuration);
        Assert.Equal(new[] { ShortcutEvent.HoldActivated }, leftControlKey.EmittedEvents);

        var shiftedState = Modifier(leftControlState, VirtualKeys.LShift, true, configuration).State;
        var shiftedKey = Key(shiftedState, VirtualKeys.F5, true, false, configuration);
        Assert.Empty(shiftedKey.EmittedEvents);

        var permittedConfiguration = new ShortcutConfiguration
        {
            Hold = binding,
            PermittedAdditionalExactMatchModifiers = ShortcutModifiers.Shift,
        };
        var permittedKey = Key(shiftedState, VirtualKeys.F5, true, false, permittedConfiguration);
        Assert.Equal(new[] { ShortcutEvent.HoldActivated }, permittedKey.EmittedEvents);
    }

    [Fact]
    public void RepeatedKeyDownDoesNotReactivate()
    {
        var binding = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.None, ShortcutBindingKind.Key, null);
        var configuration = new ShortcutConfiguration { Hold = binding };

        var first = Key(new ShortcutInputState(), VirtualKeys.F5, true, false, configuration);
        var repeated = Key(first.State, VirtualKeys.F5, true, true, configuration);

        Assert.Equal(new[] { ShortcutEvent.HoldActivated }, first.EmittedEvents);
        Assert.Empty(repeated.EmittedEvents);
        Assert.Equal(first.State.PressedKeyCodes, repeated.State.PressedKeyCodes);
        Assert.True(repeated.State.HoldIsActive);
        Assert.Equal(ShortcutConsumeDecision.Consume, repeated.ConsumeDecision);
    }

    [Fact]
    public void PasteAgainFiresOnLeadingEdgeOnly()
    {
        var binding = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.None, ShortcutBindingKind.Key, null);
        var configuration = new ShortcutConfiguration { PasteAgain = binding };

        var firstDown = Key(new ShortcutInputState(), VirtualKeys.F5, true, false, configuration);
        var repeated = Key(firstDown.State, VirtualKeys.F5, true, true, configuration);
        var up = Key(repeated.State, VirtualKeys.F5, false, false, configuration);
        var secondDown = Key(up.State, VirtualKeys.F5, true, false, configuration);

        Assert.Equal(new[] { ShortcutEvent.PasteAgainTriggered }, firstDown.EmittedEvents);
        Assert.Empty(repeated.EmittedEvents);
        Assert.Empty(up.EmittedEvents);
        Assert.Equal(new[] { ShortcutEvent.PasteAgainTriggered }, secondDown.EmittedEvents);
    }

    [Fact]
    public void BackendResetClearsActiveBindings()
    {
        var configuration = ShortcutConfiguration.Default;

        var shiftDown = Modifier(new ShortcutInputState(), VirtualKeys.LShift, true, configuration);
        var holdKeyDown = Modifier(shiftDown.State, VirtualKeys.RControl, true, configuration);
        var reset = ShortcutMatcher.Reduce(
            holdKeyDown.State, new ShortcutInputEvent.BackendReset(), configuration);

        Assert.Equal(
            new[] { ShortcutEvent.HoldDeactivated, ShortcutEvent.ToggleDeactivated },
            reset.EmittedEvents);
        Assert.Equal(ShortcutConsumeDecision.Passthrough, reset.ConsumeDecision);
        Assert.Empty(reset.State.PressedKeyCodes);
        Assert.Empty(reset.State.PressedModifierKeyCodes);
        Assert.False(reset.State.HoldIsActive);
        Assert.False(reset.State.ToggleIsActive);
    }

    [Fact]
    public void BindingMigrationAndIdentity()
    {
        // 999 is not a modifier key code and must be dropped; Right Alt must be
        // reflected back into the logical modifier mask.
        var stored = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.None, ShortcutBindingKind.Key, null,
            new HashSet<ushort> { 999, VirtualKeys.RMenu });

        var normalized = stored.NormalizedForStorageMigration();

        Assert.NotNull(normalized.ExactModifierKeyCodes);
        Assert.Equal(new HashSet<ushort> { VirtualKeys.RMenu }, normalized.ExactModifierKeyCodes!);
        Assert.Equal(ShortcutModifiers.Alt, normalized.Modifiers);

        var first = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.Control | ShortcutModifiers.Alt,
            ShortcutBindingKind.Key, null,
            new HashSet<ushort> { VirtualKeys.LControl, VirtualKeys.LMenu });
        var second = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.Alt | ShortcutModifiers.Control,
            ShortcutBindingKind.Key, null,
            new HashSet<ushort> { VirtualKeys.LMenu, VirtualKeys.LControl });

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void ConflictDetection()
    {
        var first = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.Control, ShortcutBindingKind.Key, null);
        var same = new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.Control, ShortcutBindingKind.Key, null);
        var different = new ShortcutBinding(
            0x75, "F6", ShortcutModifiers.Control, ShortcutBindingKind.Key, null);

        Assert.True(first.ConflictsWith(same), "Equivalent bindings should conflict");
        Assert.True(same.ConflictsWith(first), "Conflict detection should be symmetric");
        Assert.False(first.ConflictsWith(different), "Different primary keys should not conflict");
        Assert.False(first.ConflictsWith(ShortcutBinding.Disabled), "Disabled bindings should not conflict");
    }

    [Fact]
    public void HoldSessionControllerLifecycle()
    {
        var controller = new DictationShortcutSessionController();

        Assert.Null(controller.Handle(ShortcutEvent.HoldActivated, isTranscribing: true));
        Assert.Equal(
            new DictationShortcutAction.Start(RecordingTriggerMode.Hold),
            controller.Handle(ShortcutEvent.HoldActivated, isTranscribing: false));
        Assert.Equal(
            new DictationShortcutAction.Stop(),
            controller.Handle(ShortcutEvent.HoldDeactivated, isTranscribing: false));
        Assert.Null(controller.ActiveMode);
    }

    [Fact]
    public void ToggleSessionControllerLifecycle()
    {
        var controller = new DictationShortcutSessionController();

        Assert.Equal(
            new DictationShortcutAction.Start(RecordingTriggerMode.Toggle),
            controller.Handle(ShortcutEvent.ToggleActivated, isTranscribing: false));
        // The press that started the session must not also stop it.
        Assert.Null(controller.Handle(ShortcutEvent.ToggleActivated, isTranscribing: false));
        Assert.Null(controller.Handle(ShortcutEvent.ToggleDeactivated, isTranscribing: false));
        Assert.True(controller.ToggleStopArmed);
        Assert.Equal(
            new DictationShortcutAction.Stop(),
            controller.Handle(ShortcutEvent.ToggleActivated, isTranscribing: false));
        Assert.Null(controller.ActiveMode);
    }

    [Fact]
    public void HoldToToggleSessionControllerLifecycle()
    {
        var controller = new DictationShortcutSessionController();

        Assert.Equal(
            new DictationShortcutAction.Start(RecordingTriggerMode.Hold),
            controller.Handle(ShortcutEvent.HoldActivated, isTranscribing: false));
        Assert.Equal(
            new DictationShortcutAction.SwitchedToToggle(),
            controller.Handle(ShortcutEvent.ToggleActivated, isTranscribing: false));
        // Releasing the hold key after latching must not stop the session.
        Assert.Null(controller.Handle(ShortcutEvent.HoldDeactivated, isTranscribing: false));
        Assert.Equal(RecordingTriggerMode.Toggle, controller.ActiveMode);
        Assert.Null(controller.Handle(ShortcutEvent.PasteAgainTriggered, isTranscribing: false));

        controller.BeginManual(RecordingTriggerMode.Hold);
        Assert.Equal(RecordingTriggerMode.Hold, controller.ActiveMode);
        controller.ForceToggleMode();
        Assert.Equal(RecordingTriggerMode.Toggle, controller.ActiveMode);
        controller.Reset();
        Assert.Null(controller.ActiveMode);
        Assert.False(controller.ToggleStopArmed);
    }
}
