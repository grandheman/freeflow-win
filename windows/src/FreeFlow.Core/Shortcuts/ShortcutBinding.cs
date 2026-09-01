using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FreeFlow.Core.Shortcuts;

public enum ShortcutBindingKind
{
    Disabled,
    /// <summary>A normal key, optionally with modifiers (for example Ctrl+Shift+D).</summary>
    Key,
    /// <summary>A modifier key used on its own as the trigger (for example Right Ctrl).</summary>
    ModifierKey,
}

public enum RecordingTriggerMode
{
    Hold,
    Toggle,
}

public static class RecordingTriggerModeExtensions
{
    public static string BadgeTitle(this RecordingTriggerMode mode)
        => mode == RecordingTriggerMode.Hold ? "Hold" : "Tap";
}

public enum ShortcutRole
{
    Hold,
    Toggle,
    PasteAgain,
}

public static class ShortcutRoleExtensions
{
    public static string Title(this ShortcutRole role) => role switch
    {
        ShortcutRole.Hold => "Hold to Talk",
        ShortcutRole.Toggle => "Tap to Toggle",
        ShortcutRole.PasteAgain => "Paste Again",
        _ => string.Empty,
    };
}

/// <summary>
/// Built-in shortcut choices offered in the UI.
/// </summary>
/// <remarks>
/// The macOS build offered Fn, Right Option, and F5. Fn cannot be used on Windows:
/// on virtually all laptops it is handled inside the keyboard firmware and never
/// produces a scan code the OS can observe. Right Ctrl takes its place as the
/// default because it is a real key, is side-distinguishable, and is rarely bound
/// by other software.
/// </remarks>
public enum ShortcutPreset
{
    RightControl,
    RightAlt,
    CapsLock,
    F5,
}

public static class ShortcutPresetExtensions
{
    public static string Id(this ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.RightControl => "rightControl",
        ShortcutPreset.RightAlt => "rightAlt",
        ShortcutPreset.CapsLock => "capsLock",
        ShortcutPreset.F5 => "f5",
        _ => "custom",
    };

    public static string Title(this ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.RightControl => "Right Ctrl",
        ShortcutPreset.RightAlt => "Right Alt",
        ShortcutPreset.CapsLock => "Caps Lock",
        ShortcutPreset.F5 => "F5",
        _ => "Custom",
    };

    public static ShortcutBinding Binding(this ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.RightControl => new ShortcutBinding(
            VirtualKeys.RControl, "Right Ctrl", ShortcutModifiers.None,
            ShortcutBindingKind.ModifierKey, preset),
        ShortcutPreset.RightAlt => new ShortcutBinding(
            VirtualKeys.RMenu, "Right Alt", ShortcutModifiers.None,
            ShortcutBindingKind.ModifierKey, preset),
        ShortcutPreset.CapsLock => new ShortcutBinding(
            VirtualKeys.CapsLock, "Caps Lock", ShortcutModifiers.None,
            ShortcutBindingKind.Key, preset),
        ShortcutPreset.F5 => new ShortcutBinding(
            VirtualKeys.F5, "F5", ShortcutModifiers.None,
            ShortcutBindingKind.Key, preset),
        _ => ShortcutBinding.Disabled,
    };
}

/// <summary>
/// One shortcut definition: a primary input plus the modifiers that must accompany it.
/// </summary>
/// <remarks>
/// <para>
/// A binding matches in one of two ways. With <see cref="ExactModifierKeyCodes"/> null,
/// the required <see cref="Modifiers"/> must be a subset of what is currently held and
/// extra modifiers are tolerated. With it set, the held modifiers must match exactly,
/// which is what stops a hold binding from staying active once the user adds the extra
/// modifier that promotes it to the toggle binding.
/// </para>
/// <para>Ported from <c>Sources/ShortcutCore/ShortcutModels.swift</c>.</para>
/// </remarks>
public sealed record ShortcutBinding
{
    public ushort KeyCode { get; }
    public string KeyDisplay { get; }
    public ShortcutModifiers Modifiers { get; }
    public ShortcutBindingKind Kind { get; }
    public ShortcutPreset? Preset { get; }

    /// <summary>
    /// When set, the pressed modifier key codes must match this set exactly
    /// (subject to the caller's permitted-additional-modifiers allowance).
    /// </summary>
    public IReadOnlySet<ushort>? ExactModifierKeyCodes { get; }

    [JsonConstructor]
    public ShortcutBinding(
        ushort keyCode,
        string keyDisplay,
        ShortcutModifiers modifiers,
        ShortcutBindingKind kind,
        ShortcutPreset? preset,
        IReadOnlySet<ushort>? exactModifierKeyCodes = null)
    {
        KeyCode = keyCode;
        KeyDisplay = keyDisplay;
        Modifiers = modifiers;
        Kind = kind;
        Preset = preset;
        ExactModifierKeyCodes = exactModifierKeyCodes;
    }

    public static readonly ShortcutBinding Disabled = new(
        VirtualKeys.None, "Disabled", ShortcutModifiers.None, ShortcutBindingKind.Disabled, null);

    /// <summary>Default hold-to-talk trigger. See <see cref="ShortcutPreset"/> for why it is not Fn.</summary>
    public static ShortcutBinding DefaultHold => ShortcutPreset.RightControl.Binding();

    /// <summary>Default tap-to-toggle trigger: the hold key plus Shift, so it extends the hold binding.</summary>
    public static ShortcutBinding DefaultToggle
        => ShortcutPreset.RightControl.Binding().WithAddedModifiers(ShortcutModifiers.Shift);

    public string Id
    {
        get
        {
            var exact = string.Join(",", OrderedExactModifierKeyCodes(
                ExactModifierKeyCodes ?? new HashSet<ushort>()));
            return $"{Kind}:{KeyCode}:{(int)Modifiers}:{Preset?.Id() ?? "custom"}:{exact}";
        }
    }

    public bool IsDisabled => Kind == ShortcutBindingKind.Disabled;

    public bool IsCustom => Preset is null && !IsDisabled;

    public string DisplayName
    {
        get
        {
            if (IsDisabled) return "Disabled";
            return string.Join(" + ", ModifierDisplayNames.Append(PrimaryDisplayName));
        }
    }

    public string SelectionTitle => Preset?.Title() ?? DisplayName;

    /// <summary>How many modifiers the binding requires. Used to order overlapping activations.</summary>
    public int SpecificityScore => ModifierDisplayNames.Count;

    public bool RequiresExactModifierMatch
        => Kind == ShortcutBindingKind.ModifierKey || ExactModifierKeyCodes is not null;

    private string PrimaryDisplayName
    {
        get
        {
            if (Kind != ShortcutBindingKind.ModifierKey) return KeyDisplay;
            return VirtualKeys.ExactModifierDisplayLabel(KeyCode) ?? KeyDisplay;
        }
    }

    public IReadOnlyList<string> ModifierDisplayNames
        => BuildModifierDisplayNames(Modifiers, DisplayedExactModifierKeyCodes);

    private IReadOnlySet<ushort>? DisplayedExactModifierKeyCodes
    {
        get
        {
            if (ExactModifierKeyCodes is null) return null;
            // A modifier-key binding lists its own key as the primary input, not as a modifier.
            var filtered = Kind == ShortcutBindingKind.ModifierKey
                ? ExactModifierKeyCodes.Where(c => c != KeyCode).ToHashSet()
                : ExactModifierKeyCodes.ToHashSet();
            return NormalizedExactModifierKeyCodes(filtered);
        }
    }

    /// <summary>
    /// Widens this binding with extra modifiers. Used to derive the toggle binding
    /// from the hold binding so the two share a primary key.
    /// </summary>
    public ShortcutBinding WithAddedModifiers(ShortcutModifiers extraModifiers)
    {
        if (IsDisabled) return this;

        var updatedModifiers = Modifiers | extraModifiers;
        IReadOnlySet<ushort>? updatedExact = ExactModifierKeyCodes;
        if (ExactModifierKeyCodes is { Count: > 0 })
        {
            var union = ExactModifierKeyCodes.ToHashSet();
            union.UnionWith(VirtualKeys.ExactModifierKeyCodesPreservingSides(extraModifiers));
            updatedExact = NormalizedExactModifierKeyCodes(union);
        }

        return new ShortcutBinding(KeyCode, KeyDisplay, updatedModifiers, Kind, Preset, updatedExact);
    }

    /// <summary>
    /// Repairs bindings loaded from older settings files whose modifier mask and
    /// exact key-code set disagree.
    /// </summary>
    public ShortcutBinding NormalizedForStorageMigration()
    {
        var normalizedExact = NormalizedExactModifierKeyCodes(ExactModifierKeyCodes);

        var extras = normalizedExact is null
            ? new HashSet<ushort>()
            : Kind == ShortcutBindingKind.ModifierKey
                ? normalizedExact.Where(c => c != KeyCode).ToHashSet()
                : normalizedExact.ToHashSet();

        var normalizedModifiers = Modifiers | VirtualKeys.Modifiers(extras);

        var exactUnchanged = normalizedExact is null
            ? ExactModifierKeyCodes is null
            : ExactModifierKeyCodes is not null && normalizedExact.SetEquals(ExactModifierKeyCodes);

        if (exactUnchanged && normalizedModifiers == Modifiers) return this;

        return new ShortcutBinding(KeyCode, KeyDisplay, normalizedModifiers, Kind, Preset, normalizedExact);
    }

    /// <summary>
    /// True when both bindings could fire for the same physical key state at the
    /// same specificity, which would make the pair ambiguous.
    /// </summary>
    public bool ConflictsWith(ShortcutBinding other)
    {
        if (IsDisabled || other.IsDisabled) return false;
        if (!PrimaryInputOverlaps(other)) return false;
        if (SpecificityScore != other.SpecificityScore) return false;

        // Brute-force every combination of held modifier keys. The set is small
        // (8 codes) so this stays cheap and avoids subtle set-algebra mistakes.
        var ordered = VirtualKeys.ModifierKeyCodes.OrderBy(c => c).ToArray();
        var combinations = 1 << ordered.Length;

        for (var mask = 0; mask < combinations; mask++)
        {
            var pressed = new HashSet<ushort>();
            for (var index = 0; index < ordered.Length; index++)
            {
                if ((mask & (1 << index)) != 0) pressed.Add(ordered[index]);
            }

            if (IsActive(pressed) && other.IsActive(pressed)) return true;
        }

        return false;
    }

    private bool PrimaryInputOverlaps(ShortcutBinding other)
    {
        if (Kind != other.Kind) return false;
        return Kind != ShortcutBindingKind.Disabled && KeyCode == other.KeyCode;
    }

    private bool IsActive(IReadOnlySet<ushort> pressedModifierKeyCodes)
    {
        var currentModifiers = VirtualKeys.Modifiers(pressedModifierKeyCodes);
        if (!currentModifiers.IsSupersetOf(Modifiers)) return false;

        if (ExactModifierKeyCodes is not null &&
            !ExactModifierKeyCodesMatch(pressedModifierKeyCodes, ExactModifierKeyCodes))
        {
            return false;
        }

        return Kind switch
        {
            ShortcutBindingKind.Disabled => false,
            ShortcutBindingKind.Key => true,
            ShortcutBindingKind.ModifierKey => pressedModifierKeyCodes.Contains(KeyCode),
            _ => false,
        };
    }

    public static IReadOnlySet<ushort>? NormalizedExactModifierKeyCodes(IReadOnlySet<ushort>? exactModifierKeyCodes)
    {
        if (exactModifierKeyCodes is null) return null;
        var normalized = exactModifierKeyCodes.Where(VirtualKeys.ModifierKeyCodes.Contains).ToHashSet();
        return normalized.Count == 0 ? null : normalized;
    }

    public static IReadOnlyList<ushort> OrderedExactModifierKeyCodes(IReadOnlySet<ushort> exactModifierKeyCodes)
        => VirtualKeys.ModifierDisplayOrder.Where(exactModifierKeyCodes.Contains).ToList();

    /// <summary>
    /// Exact-match test for held modifiers, evaluated per logical modifier group.
    /// </summary>
    /// <remarks>
    /// For each group (Ctrl, Alt, Shift, Win): no required code means nothing from
    /// that group may be held, unless the caller explicitly permits it; both sides
    /// required means either side satisfies it; one side required means exactly
    /// that side must be held.
    /// </remarks>
    public static bool ExactModifierKeyCodesMatch(
        IReadOnlySet<ushort> pressedModifierKeyCodes,
        IReadOnlySet<ushort> exactModifierKeyCodes,
        ShortcutModifiers permittedAdditionalExactMatchModifiers = ShortcutModifiers.None)
    {
        var required = NormalizedExactModifierKeyCodes(exactModifierKeyCodes) ?? new HashSet<ushort>();

        foreach (var (logical, groupCodes) in VirtualKeys.MatchSpecs)
        {
            var requiredInGroup = required.Where(groupCodes.Contains).ToHashSet();
            var pressedInGroup = pressedModifierKeyCodes.Where(groupCodes.Contains).ToHashSet();

            if (requiredInGroup.Count == 0)
            {
                if (pressedInGroup.Count > 0 &&
                    !permittedAdditionalExactMatchModifiers.Contains(logical))
                {
                    return false;
                }
                continue;
            }

            if (requiredInGroup.Count == groupCodes.Count)
            {
                // Either side is acceptable, but at least one must be held.
                if (pressedInGroup.Count == 0 || !pressedInGroup.IsSubsetOf(requiredInGroup)) return false;
            }
            else if (!pressedInGroup.SetEquals(requiredInGroup))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Renders modifier labels, collapsing to a side-agnostic name when both
    /// sides of a modifier are accepted.
    /// </summary>
    public static IReadOnlyList<string> BuildModifierDisplayNames(
        ShortcutModifiers modifiers,
        IReadOnlySet<ushort>? exactModifierKeyCodes)
    {
        var required = NormalizedExactModifierKeyCodes(exactModifierKeyCodes) ?? new HashSet<ushort>();
        var names = new List<string>();

        foreach (var (logical, groupCodes) in VirtualKeys.MatchSpecs)
        {
            var matching = required.Where(groupCodes.Contains).ToHashSet();

            if (groupCodes.Count > 0 && matching.Count == groupCodes.Count)
            {
                names.Add(VirtualKeys.LogicalModifierDisplayLabel(logical));
                continue;
            }

            if (matching.Count > 0)
            {
                foreach (var keyCode in VirtualKeys.ModifierDisplayOrder.Where(matching.Contains))
                {
                    var label = VirtualKeys.ExactModifierDisplayLabel(keyCode);
                    if (label is not null) names.Add(label);
                }
            }
            else if (modifiers.Contains(logical))
            {
                names.Add(VirtualKeys.LogicalModifierDisplayLabel(logical));
            }
        }

        return names;
    }
}
