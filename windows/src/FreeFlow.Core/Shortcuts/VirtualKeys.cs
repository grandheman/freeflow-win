using System.Collections.Generic;

namespace FreeFlow.Core.Shortcuts;

/// <summary>
/// Windows virtual-key codes used by shortcut bindings, plus the side-aware
/// modifier tables that replace the macOS keyCode tables (54/55/56/58...).
/// </summary>
public static class VirtualKeys
{
    public const ushort None = 0;

    // Side-specific modifiers. Windows reports these distinctly from a low-level
    // keyboard hook, which is what lets "Right Alt" work as a standalone binding.
    public const ushort LControl = 0xA2;
    public const ushort RControl = 0xA3;
    public const ushort LMenu = 0xA4;   // Left Alt
    public const ushort RMenu = 0xA5;   // Right Alt
    public const ushort LShift = 0xA0;
    public const ushort RShift = 0xA1;
    public const ushort LWin = 0x5B;
    public const ushort RWin = 0x5C;

    public const ushort CapsLock = 0x14;
    public const ushort Escape = 0x1B;
    public const ushort F5 = 0x74;

    /// <summary>Every key code treated as a modifier by the matcher.</summary>
    public static readonly IReadOnlySet<ushort> ModifierKeyCodes = new HashSet<ushort>
    {
        LControl, RControl, LMenu, RMenu, LShift, RShift, LWin, RWin,
    };

    /// <summary>
    /// Groups side-specific key codes under the logical modifier they produce.
    /// Mirrors <c>modifierKeyCodeMatchSpecs</c> in the macOS source.
    /// </summary>
    public static readonly IReadOnlyList<(ShortcutModifiers Logical, IReadOnlySet<ushort> KeyCodes)> MatchSpecs =
        new List<(ShortcutModifiers, IReadOnlySet<ushort>)>
        {
            (ShortcutModifiers.Control, new HashSet<ushort> { LControl, RControl }),
            (ShortcutModifiers.Alt,     new HashSet<ushort> { LMenu, RMenu }),
            (ShortcutModifiers.Shift,   new HashSet<ushort> { LShift, RShift }),
            (ShortcutModifiers.Windows, new HashSet<ushort> { LWin, RWin }),
        };

    /// <summary>Display order used when rendering a binding's modifier list.</summary>
    public static readonly IReadOnlyList<ushort> ModifierDisplayOrder =
        new ushort[] { LControl, RControl, LMenu, RMenu, LShift, RShift, LWin, RWin };

    /// <summary>The logical modifier a modifier key code produces, or null for a normal key.</summary>
    public static ShortcutModifiers? LogicalModifier(ushort keyCode) => keyCode switch
    {
        LControl or RControl => ShortcutModifiers.Control,
        LMenu or RMenu => ShortcutModifiers.Alt,
        LShift or RShift => ShortcutModifiers.Shift,
        LWin or RWin => ShortcutModifiers.Windows,
        _ => null,
    };

    /// <summary>Collapses a set of pressed modifier key codes into a logical modifier mask.</summary>
    public static ShortcutModifiers Modifiers(IReadOnlyCollection<ushort> pressedModifierKeyCodes)
    {
        var modifiers = ShortcutModifiers.None;
        foreach (var keyCode in pressedModifierKeyCodes)
        {
            var logical = LogicalModifier(keyCode);
            if (logical.HasValue) modifiers |= logical.Value;
        }
        return modifiers;
    }

    /// <summary>Canonical (left-hand) key code for a modifier, used when sides are interchangeable.</summary>
    public static ushort CanonicalModifierKeyCode(ushort keyCode) => keyCode switch
    {
        RControl => LControl,
        RMenu => LMenu,
        RShift => LShift,
        RWin => LWin,
        _ => keyCode,
    };

    /// <summary>Canonical key codes for a logical modifier mask.</summary>
    public static HashSet<ushort> ExactModifierKeyCodes(ShortcutModifiers modifiers)
    {
        var keyCodes = new HashSet<ushort>();
        if (modifiers.Contains(ShortcutModifiers.Control)) keyCodes.Add(LControl);
        if (modifiers.Contains(ShortcutModifiers.Alt)) keyCodes.Add(LMenu);
        if (modifiers.Contains(ShortcutModifiers.Shift)) keyCodes.Add(LShift);
        if (modifiers.Contains(ShortcutModifiers.Windows)) keyCodes.Add(LWin);
        return keyCodes;
    }

    /// <summary>
    /// Both sides of every modifier in the mask. Used when widening a binding
    /// (hold + extra modifiers = toggle) so either side satisfies the match.
    /// </summary>
    public static HashSet<ushort> ExactModifierKeyCodesPreservingSides(ShortcutModifiers modifiers)
    {
        var keyCodes = new HashSet<ushort>();
        foreach (var (logical, codes) in MatchSpecs)
        {
            if (modifiers.Contains(logical)) keyCodes.UnionWith(codes);
        }
        return keyCodes;
    }

    /// <summary>Every modifier key code whose logical modifier is in the mask.</summary>
    public static HashSet<ushort> MatchingModifierKeyCodes(ShortcutModifiers modifiers)
    {
        var result = new HashSet<ushort>();
        foreach (var keyCode in ModifierKeyCodes)
        {
            var logical = LogicalModifier(keyCode);
            if (logical.HasValue && modifiers.Contains(logical.Value)) result.Add(keyCode);
        }
        return result;
    }

    /// <summary>Side-aware display label for a modifier key code, or null if not a modifier.</summary>
    public static string? ExactModifierDisplayLabel(ushort keyCode) => keyCode switch
    {
        LControl => "Ctrl",
        RControl => "Right Ctrl",
        LMenu => "Alt",
        RMenu => "Right Alt",
        LShift => "Shift",
        RShift => "Right Shift",
        LWin => "Win",
        RWin => "Right Win",
        _ => null,
    };

    /// <summary>Side-agnostic label for a logical modifier.</summary>
    public static string LogicalModifierDisplayLabel(ShortcutModifiers modifier) => modifier switch
    {
        ShortcutModifiers.Control => "Ctrl",
        ShortcutModifiers.Alt => "Alt",
        ShortcutModifiers.Shift => "Shift",
        ShortcutModifiers.Windows => "Win",
        _ => "Modifier",
    };
}
