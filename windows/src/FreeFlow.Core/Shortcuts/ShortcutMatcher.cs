using System.Collections.Generic;
using System.Linq;

namespace FreeFlow.Core.Shortcuts;

/// <summary>Raw input reported by a platform keyboard backend.</summary>
public abstract record ShortcutInputEvent
{
    /// <summary>A modifier key went down or up.</summary>
    public sealed record ModifierChanged(ushort KeyCode, bool IsDown) : ShortcutInputEvent;

    /// <summary>Authoritative snapshot of every modifier currently held.</summary>
    public sealed record ModifierSnapshot(IReadOnlySet<ushort> PressedModifierKeyCodes) : ShortcutInputEvent;

    /// <summary>A non-modifier key went down or up.</summary>
    public sealed record KeyChanged(ushort KeyCode, bool IsDown, bool IsRepeat) : ShortcutInputEvent;

    /// <summary>The backend restarted; all key state must be treated as released.</summary>
    public sealed record BackendReset : ShortcutInputEvent;
}

/// <summary>Whether the backend should swallow the event or let it reach the focused app.</summary>
public enum ShortcutConsumeDecision
{
    Consume,
    Passthrough,
}

/// <summary>High-level shortcut transitions emitted by the matcher.</summary>
public enum ShortcutEvent
{
    HoldActivated,
    HoldDeactivated,
    ToggleActivated,
    ToggleDeactivated,
    PasteAgainTriggered,
}

public sealed record ShortcutConfiguration
{
    public ShortcutBinding Hold { get; init; } = ShortcutBinding.Disabled;
    public ShortcutBinding Toggle { get; init; } = ShortcutBinding.Disabled;
    public ShortcutBinding PasteAgain { get; init; } = ShortcutBinding.Disabled;

    /// <summary>
    /// Modifiers that may additionally be held without breaking an exact match.
    /// Set while a recording is in flight so the user can add the toggle modifier
    /// mid-hold without the hold binding immediately deactivating.
    /// </summary>
    public ShortcutModifiers PermittedAdditionalExactMatchModifiers { get; init; } = ShortcutModifiers.None;

    public static readonly ShortcutConfiguration DisabledConfiguration = new();

    public static ShortcutConfiguration Default => new()
    {
        Hold = ShortcutBinding.DefaultHold,
        Toggle = ShortcutBinding.DefaultToggle,
    };
}

/// <summary>Everything the matcher needs to remember between events.</summary>
public sealed class ShortcutInputState
{
    public HashSet<ushort> PressedKeyCodes { get; set; } = new();
    public HashSet<ushort> PressedModifierKeyCodes { get; set; } = new();
    public bool HoldIsActive { get; set; }
    public bool ToggleIsActive { get; set; }
    public bool PasteAgainIsActive { get; set; }

    public ShortcutModifiers CurrentModifiers => VirtualKeys.Modifiers(PressedModifierKeyCodes);

    public ShortcutInputState Clone() => new()
    {
        PressedKeyCodes = new HashSet<ushort>(PressedKeyCodes),
        PressedModifierKeyCodes = new HashSet<ushort>(PressedModifierKeyCodes),
        HoldIsActive = HoldIsActive,
        ToggleIsActive = ToggleIsActive,
        PasteAgainIsActive = PasteAgainIsActive,
    };

    /// <summary>
    /// True when any currently held key is referenced by a configured binding.
    /// Used to decide whether releasing keys should be waited on before acting.
    /// </summary>
    public bool HasPressedShortcutInputs(ShortcutConfiguration configuration)
    {
        var currentModifiers = CurrentModifiers;

        var keyReferenceHeld = PressedKeyCodes.Any(keyCode =>
            (configuration.Hold.Kind == ShortcutBindingKind.Key && configuration.Hold.KeyCode == keyCode) ||
            (configuration.Toggle.Kind == ShortcutBindingKind.Key && configuration.Toggle.KeyCode == keyCode) ||
            (configuration.PasteAgain.Kind == ShortcutBindingKind.Key && configuration.PasteAgain.KeyCode == keyCode));

        if (keyReferenceHeld) return true;

        foreach (var binding in new[] { configuration.Hold, configuration.Toggle, configuration.PasteAgain })
        {
            if (ShortcutMatcher.ReferencesPressedModifiers(
                    binding,
                    PressedModifierKeyCodes,
                    currentModifiers,
                    configuration.PermittedAdditionalExactMatchModifiers))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record ShortcutMatchResult(
    ShortcutInputState State,
    IReadOnlyList<ShortcutEvent> EmittedEvents,
    ShortcutConsumeDecision ConsumeDecision);

/// <summary>
/// Pure reducer turning raw key events into shortcut activations.
/// </summary>
/// <remarks>
/// Ported from <c>Sources/ShortcutCore/ShortcutMatcher.swift</c>. Deliberately free
/// of any platform dependency so the hold/toggle semantics stay testable without a
/// keyboard hook.
/// </remarks>
public static class ShortcutMatcher
{
    public static ShortcutMatchResult Reduce(
        ShortcutInputState state,
        ShortcutInputEvent inputEvent,
        ShortcutConfiguration configuration)
    {
        switch (inputEvent)
        {
            case ShortcutInputEvent.BackendReset:
                return ReduceBackendReset(state, configuration);

            case ShortcutInputEvent.ModifierSnapshot snapshot:
            {
                var nextState = state.Clone();
                nextState.PressedModifierKeyCodes = new HashSet<ushort>(snapshot.PressedModifierKeyCodes);

                var emitted = UpdateActiveBindings(nextState, configuration);
                return new ShortcutMatchResult(
                    nextState,
                    emitted,
                    emitted.Count == 0 ? ShortcutConsumeDecision.Passthrough : ShortcutConsumeDecision.Consume);
            }

            case ShortcutInputEvent.ModifierChanged modifier:
            {
                // Evaluate consumption before and after the state change so both the
                // key that activates a binding and the key that releases it are swallowed.
                var consumeBefore = ShouldConsumeModifierEvent(modifier.KeyCode, state, configuration);

                var nextState = state.Clone();
                if (modifier.IsDown) nextState.PressedModifierKeyCodes.Add(modifier.KeyCode);
                else nextState.PressedModifierKeyCodes.Remove(modifier.KeyCode);

                var consumeAfter = ShouldConsumeModifierEvent(modifier.KeyCode, nextState, configuration);
                var emitted = UpdateActiveBindings(nextState, configuration);

                return new ShortcutMatchResult(
                    nextState,
                    emitted,
                    consumeBefore || consumeAfter
                        ? ShortcutConsumeDecision.Consume
                        : ShortcutConsumeDecision.Passthrough);
            }

            case ShortcutInputEvent.KeyChanged key:
            {
                var consumeBefore = ShouldConsumeKeyEvent(key.KeyCode, state, configuration);

                var nextState = state.Clone();

                if (key.IsRepeat)
                {
                    // Auto-repeat must not re-fire an activation.
                    return new ShortcutMatchResult(
                        nextState,
                        new List<ShortcutEvent>(),
                        consumeBefore ? ShortcutConsumeDecision.Consume : ShortcutConsumeDecision.Passthrough);
                }

                if (key.IsDown) nextState.PressedKeyCodes.Add(key.KeyCode);
                else nextState.PressedKeyCodes.Remove(key.KeyCode);

                var consumeAfter = ShouldConsumeKeyEvent(key.KeyCode, nextState, configuration);
                var emitted = UpdateActiveBindings(nextState, configuration);

                return new ShortcutMatchResult(
                    nextState,
                    emitted,
                    consumeBefore || consumeAfter
                        ? ShortcutConsumeDecision.Consume
                        : ShortcutConsumeDecision.Passthrough);
            }

            default:
                return new ShortcutMatchResult(state, new List<ShortcutEvent>(), ShortcutConsumeDecision.Passthrough);
        }
    }

    private static ShortcutMatchResult ReduceBackendReset(
        ShortcutInputState state,
        ShortcutConfiguration configuration)
    {
        var nextState = state.Clone();
        nextState.PressedKeyCodes.Clear();
        nextState.PressedModifierKeyCodes.Clear();
        var emitted = UpdateActiveBindings(nextState, configuration);
        return new ShortcutMatchResult(nextState, emitted, ShortcutConsumeDecision.Passthrough);
    }

    private static IReadOnlyList<ShortcutEvent> UpdateActiveBindings(
        ShortcutInputState state,
        ShortcutConfiguration configuration)
    {
        var previousHold = state.HoldIsActive;
        var previousToggle = state.ToggleIsActive;
        var previousPasteAgain = state.PasteAgainIsActive;

        state.HoldIsActive = BindingIsActive(configuration.Hold, state, configuration);
        state.ToggleIsActive = BindingIsActive(configuration.Toggle, state, configuration);
        state.PasteAgainIsActive = BindingIsActive(configuration.PasteAgain, state, configuration);

        return EmitChanges(
            previousHold, previousToggle, previousPasteAgain,
            state.HoldIsActive, state.ToggleIsActive, state.PasteAgainIsActive,
            configuration);
    }

    /// <summary>
    /// Orders overlapping transitions so the more specific binding activates first
    /// and the less specific one deactivates first. That ordering is what lets a
    /// hold session latch into toggle mode without a stop event slipping in between.
    /// </summary>
    private static IReadOnlyList<ShortcutEvent> EmitChanges(
        bool previousHold, bool previousToggle, bool previousPasteAgain,
        bool currentHold, bool currentToggle, bool currentPasteAgain,
        ShortcutConfiguration configuration)
    {
        var activations = new List<(ShortcutEvent Event, int Score)>();
        var deactivations = new List<(ShortcutEvent Event, int Score)>();

        if (!previousHold && currentHold)
            activations.Add((ShortcutEvent.HoldActivated, configuration.Hold.SpecificityScore));
        if (!previousToggle && currentToggle)
            activations.Add((ShortcutEvent.ToggleActivated, configuration.Toggle.SpecificityScore));
        // Paste Again is a one-shot: fire on the leading edge only.
        if (!previousPasteAgain && currentPasteAgain)
            activations.Add((ShortcutEvent.PasteAgainTriggered, configuration.PasteAgain.SpecificityScore));

        if (previousHold && !currentHold)
            deactivations.Add((ShortcutEvent.HoldDeactivated, configuration.Hold.SpecificityScore));
        if (previousToggle && !currentToggle)
            deactivations.Add((ShortcutEvent.ToggleDeactivated, configuration.Toggle.SpecificityScore));

        return activations.OrderByDescending(x => x.Score).Select(x => x.Event)
            .Concat(deactivations.OrderBy(x => x.Score).Select(x => x.Event))
            .ToList();
    }

    private static bool BindingIsActive(
        ShortcutBinding binding,
        ShortcutInputState state,
        ShortcutConfiguration configuration)
    {
        if (binding.IsDisabled) return false;

        if (!ModifiersAreActive(
                binding,
                state.PressedModifierKeyCodes,
                state.CurrentModifiers,
                configuration.PermittedAdditionalExactMatchModifiers))
        {
            return false;
        }

        return binding.Kind switch
        {
            ShortcutBindingKind.Disabled => false,
            ShortcutBindingKind.Key => state.PressedKeyCodes.Contains(binding.KeyCode),
            ShortcutBindingKind.ModifierKey => state.PressedModifierKeyCodes.Contains(binding.KeyCode),
            _ => false,
        };
    }

    private static bool ShouldConsumeKeyEvent(
        ushort keyCode,
        ShortcutInputState state,
        ShortcutConfiguration configuration)
        => RelevantKeyBindings(keyCode, configuration)
            .Any(binding => BindingIsActive(binding, state, configuration));

    private static bool ShouldConsumeModifierEvent(
        ushort keyCode,
        ShortcutInputState state,
        ShortcutConfiguration configuration)
        => RelevantModifierBindings(keyCode, configuration)
            .Any(binding => BindingIsActive(binding, state, configuration));

    private static IEnumerable<ShortcutBinding> RelevantKeyBindings(
        ushort keyCode,
        ShortcutConfiguration configuration)
        => new[] { configuration.Hold, configuration.Toggle, configuration.PasteAgain }
            .Where(b => b.Kind == ShortcutBindingKind.Key && b.KeyCode == keyCode);

    private static IEnumerable<ShortcutBinding> RelevantModifierBindings(
        ushort keyCode,
        ShortcutConfiguration configuration)
        => new[] { configuration.Hold, configuration.Toggle, configuration.PasteAgain }
            .Where(b => b.Kind != ShortcutBindingKind.Disabled && ModifierEventAffects(keyCode, b));

    private static bool ModifierEventAffects(ushort keyCode, ShortcutBinding binding)
    {
        if (binding.KeyCode == keyCode) return true;
        if (!VirtualKeys.ModifierKeyCodes.Contains(keyCode)) return false;

        // Exact-match bindings care about every modifier, because an unexpected one
        // is precisely what breaks the match.
        if (binding.ExactModifierKeyCodes is not null) return true;

        var logical = VirtualKeys.LogicalModifier(keyCode);
        return logical.HasValue && binding.Modifiers.Contains(logical.Value);
    }

    internal static bool ModifiersAreActive(
        ShortcutBinding binding,
        IReadOnlySet<ushort> pressedModifierKeyCodes,
        ShortcutModifiers currentModifiers,
        ShortcutModifiers permittedAdditionalExactMatchModifiers)
    {
        if (!currentModifiers.IsSupersetOf(binding.Modifiers)) return false;
        if (binding.ExactModifierKeyCodes is null) return true;

        return ShortcutBinding.ExactModifierKeyCodesMatch(
            pressedModifierKeyCodes,
            binding.ExactModifierKeyCodes,
            permittedAdditionalExactMatchModifiers);
    }

    internal static bool ReferencesPressedModifiers(
        ShortcutBinding binding,
        IReadOnlySet<ushort> pressedModifierKeyCodes,
        ShortcutModifiers currentModifiers,
        ShortcutModifiers permittedAdditionalExactMatchModifiers)
    {
        if (binding.ExactModifierKeyCodes is not null)
        {
            if (binding.ExactModifierKeyCodes.Overlaps(pressedModifierKeyCodes)) return true;

            if (permittedAdditionalExactMatchModifiers != ShortcutModifiers.None)
            {
                var additional = VirtualKeys.MatchingModifierKeyCodes(permittedAdditionalExactMatchModifiers);
                if (additional.Overlaps(pressedModifierKeyCodes)) return true;
            }
        }
        else if (binding.Modifiers.IntersectsWith(currentModifiers))
        {
            return true;
        }

        return binding.Kind == ShortcutBindingKind.ModifierKey
            && pressedModifierKeyCodes.Contains(binding.KeyCode);
    }
}
