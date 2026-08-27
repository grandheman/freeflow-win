using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeFlow.Core.Updates;

/// <summary>
/// A semantic version, used to decide whether a published release is newer than
/// the running build.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>Sources/UpdateManager.swift</c>. Follows semver 2.0 precedence:
/// build metadata is ignored entirely, a prerelease sorts before its stable release,
/// and prerelease identifiers compare numerically when both sides are numeric and
/// lexically otherwise.
/// </para>
/// <para>
/// Parsing is strict on purpose. A malformed tag must not compare as newer and
/// trigger an update prompt for a release that does not exist.
/// </para>
/// </remarks>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }

    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    /// <summary>Parses a version string, returning null when it is not valid semver.</summary>
    public static SemanticVersion? TryParse(string? value)
    {
        if (value is null) return null;

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        // Build metadata has no effect on precedence.
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0) normalized = normalized[..plusIndex];

        var dashIndex = normalized.IndexOf('-');
        var corePart = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var prereleasePart = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : null;

        var coreComponents = corePart.Split('.');
        if (coreComponents.Length != 3) return null;

        if (!TryParseNumber(coreComponents[0], out var major) ||
            !TryParseNumber(coreComponents[1], out var minor) ||
            !TryParseNumber(coreComponents[2], out var patch))
        {
            return null;
        }

        IReadOnlyList<string> prerelease = Array.Empty<string>();

        if (prereleasePart is not null)
        {
            // "1.2.3-" and "1.2.3-alpha..1" are both malformed.
            if (prereleasePart.Length == 0) return null;

            var identifiers = prereleasePart.Split('.');
            if (identifiers.Any(identifier => identifier.Length == 0)) return null;

            prerelease = identifiers;
        }

        return new SemanticVersion(major, minor, patch, prerelease);
    }

    private static bool TryParseNumber(string value, out int result)
    {
        result = 0;
        // Reject signs and other characters int.TryParse would otherwise accept.
        if (value.Length == 0 || !value.All(char.IsAsciiDigit)) return false;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // A version with a prerelease has lower precedence than one without.
        if (Prerelease.Count == 0 && other.Prerelease.Count == 0) return 0;
        if (Prerelease.Count == 0) return 1;
        if (other.Prerelease.Count == 0) return -1;

        var shared = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < shared; index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            if (left == right) continue;

            var leftIsNumeric = TryParseNumber(left, out var leftNumber);
            var rightIsNumeric = TryParseNumber(right, out var rightNumber);

            // Numeric identifiers always have lower precedence than alphanumeric ones.
            if (leftIsNumeric && rightIsNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftIsNumeric) return -1;
            if (rightIsNumeric) return 1;

            return string.CompareOrdinal(left, right);
        }

        // A larger set of identifiers wins when all shared ones are equal.
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Major, Minor, Patch, string.Join(".", Prerelease));

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return Prerelease.Count == 0 ? core : $"{core}-{string.Join(".", Prerelease)}";
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(SemanticVersion? left, SemanticVersion? right)
        => left is null ? right is null : left.Equals(right);
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);
}
