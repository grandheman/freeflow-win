using System;
using System.Collections.Generic;

namespace FreeFlow.Core.Shortcuts;

/// <summary>
/// Logical modifier keys, as a bit flag set.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>ShortcutModifiers</c> option set. The macOS build had
/// Command / Control / Option / Shift / Function. Windows has no Fn key that reaches
/// user space (it is handled in keyboard firmware on nearly all laptops), so the
/// Function flag is replaced by Windows, which is the closest available "extra"
/// modifier and the one the default bindings use.
/// </remarks>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

public static class ShortcutModifiersExtensions
{
    public static bool Contains(this ShortcutModifiers value, ShortcutModifiers other)
        => (value & other) == other;

    public static bool IsSupersetOf(this ShortcutModifiers value, ShortcutModifiers other)
        => (value & other) == other;

    public static bool IntersectsWith(this ShortcutModifiers value, ShortcutModifiers other)
        => (value & other) != ShortcutModifiers.None;

    /// <summary>Display names in a stable, human-familiar order.</summary>
    public static IReadOnlyList<string> OrderedDisplayNames(this ShortcutModifiers value)
    {
        var names = new List<string>();
        if (value.Contains(ShortcutModifiers.Control)) names.Add("Ctrl");
        if (value.Contains(ShortcutModifiers.Alt)) names.Add("Alt");
        if (value.Contains(ShortcutModifiers.Shift)) names.Add("Shift");
        if (value.Contains(ShortcutModifiers.Windows)) names.Add("Win");
        return names;
    }
}
