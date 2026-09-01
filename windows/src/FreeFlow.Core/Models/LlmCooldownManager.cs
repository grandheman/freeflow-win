using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using FreeFlow.Core.Storage;

namespace FreeFlow.Core.Models;

/// <summary>
/// Tracks per-model rate-limit cooldowns so subsequent requests skip a rate-limited
/// model instead of sending a doomed request and paying an extra round trip.
/// </summary>
/// <remarks>
/// <para>Two storage tiers:</para>
/// <list type="bullet">
/// <item>Minute-level limits (retry-after under an hour) stay in memory and are cleared on restart.</item>
/// <item>Daily limits are persisted so the cooldown survives a restart and is visible in Settings.</item>
/// </list>
/// <para>
/// The macOS build used a Swift actor. Here a lock provides the same serialization,
/// which keeps callers synchronous.
/// </para>
/// <para>Ported from <c>Sources/LLMCooldownManager.swift</c>.</para>
/// </remarks>
public sealed class LlmCooldownManager
{
    /// <summary>Cooldowns at or above this threshold are treated as daily limits and persisted.</summary>
    private static readonly TimeSpan DailyLimitThreshold = TimeSpan.FromHours(1);

    /// <summary>
    /// Fallback cooldown used when a 429 carries no parseable timing header. Kept well
    /// below <see cref="DailyLimitThreshold"/> so it stays in memory and lets the next
    /// call re-probe soon.
    /// </summary>
    private const double DefaultReprobeCooldownSeconds = 60;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
    private readonly IKeyValueStore _store;
    private readonly Func<DateTimeOffset> _now;

    public LlmCooldownManager(IKeyValueStore store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when the model is currently blocked by a rate-limit cooldown.</summary>
    public bool IsInCooldown(string model)
    {
        var now = _now();

        lock (_gate)
        {
            if (_cooldowns.TryGetValue(model, out var until))
            {
                if (now < until) return true;
                _cooldowns.Remove(model);
            }

            var persisted = PersistedExpiry(model);
            if (persisted is not null)
            {
                if (now < persisted.Value) return true;
                _store.Remove(StoreKey(model));
            }

            return false;
        }
    }

    /// <summary>
    /// Registers a cooldown from a 429 response.
    /// </summary>
    /// <param name="persist">
    /// Pass true for a daily-limit signal (the RPD reset header) so a daily quota that
    /// happens to reset in under an hour is still persisted and shown in Settings.
    /// </param>
    public void SetCooldown(string model, double retryAfterSeconds, bool persist = false)
    {
        var expiry = _now().AddSeconds(retryAfterSeconds);

        lock (_gate)
        {
            if (persist || retryAfterSeconds >= DailyLimitThreshold.TotalSeconds)
            {
                _store.SetDouble(StoreKey(model), expiry.ToUnixTimeMilliseconds() / 1000.0);
            }
            else
            {
                _cooldowns[model] = expiry;
            }
        }
    }

    /// <summary>
    /// Picks the model to actually send to: the primary when it is not cooling down,
    /// otherwise the fallback when it exists and is itself not cooling down, otherwise
    /// null so the caller can skip a doomed request.
    /// </summary>
    public string? EffectivePrimary(string primary, string? fallback)
    {
        if (!IsInCooldown(primary)) return primary;
        if (string.IsNullOrEmpty(fallback) || IsInCooldown(fallback!)) return null;
        return fallback;
    }

    /// <summary>Stable persistence key, also read by the Settings UI.</summary>
    public static string StoreKey(string model) => $"llm_cooldown_expiry_{model}";

    private DateTimeOffset? PersistedExpiry(string model)
    {
        var timestamp = _store.GetDouble(StoreKey(model));
        if (timestamp <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(timestamp * 1000));
    }

    /// <summary>
    /// Reads how long a model must cool down from a 429 response, and whether the
    /// limit is a daily one.
    /// </summary>
    /// <remarks>
    /// Priority: an exhausted daily request quota, then <c>retry-after</c>, then the
    /// per-minute token reset, then a short re-probe fallback. The daily check runs
    /// first because Groq usually also sends <c>retry-after</c>; honoring that first
    /// would hide a short, near-reset daily window and stop it from persisting.
    /// </remarks>
    public static (double Seconds, bool IsDaily) RateLimitCooldown(HttpResponseHeaders headers)
    {
        var remainingRequests = ParseDouble(FirstHeader(headers, "x-ratelimit-remaining-requests"));
        if (remainingRequests is <= 0)
        {
            var dailyReset = ParseGroqDuration(FirstHeader(headers, "x-ratelimit-reset-requests"));
            if (dailyReset is not null) return (dailyReset.Value, true);
        }

        // retry-after is the authoritative wait Groq sets specifically on a 429.
        var retryAfter = ParseGroqDuration(FirstHeader(headers, "retry-after"));
        if (retryAfter is not null) return (retryAfter.Value, false);

        // x-ratelimit-reset-tokens carries the per-minute (TPM) reset, e.g. "7.66s".
        var tokenReset = ParseGroqDuration(FirstHeader(headers, "x-ratelimit-reset-tokens"));
        if (tokenReset is not null) return (tokenReset.Value, false);

        // No timing header present (rare): cool down briefly so the next call can re-probe.
        return (DefaultReprobeCooldownSeconds, false);
    }

    private static string? FirstHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        foreach (var value in values) return value;
        return null;
    }

    private static double? ParseDouble(string? value)
    {
        if (value is null) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Parses a Groq duration string.
    /// </summary>
    /// <remarks>
    /// Accepts bare seconds ("2", "7.66"), a single suffixed unit ("7.66s", "120ms"),
    /// and compound forms ("2m59.56s", "1h0m0s", "1h2m3.5s"). Returns null for empty,
    /// unrecognized-unit, negative, or non-finite input, so a malformed header can
    /// never yield a negative or NaN cooldown.
    /// </remarks>
    internal static double? ParseGroqDuration(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;

        // A bare number is plain seconds, which covers the retry-after integer form.
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
        }

        // Otherwise accumulate <number><unit> segments left to right.
        double total = 0;
        var numberBuffer = new StringBuilder();
        var matchedAnyUnit = false;
        var index = 0;

        while (index < trimmed.Length)
        {
            var character = trimmed[index];
            if (char.IsDigit(character) || character == '.')
            {
                numberBuffer.Append(character);
                index++;
                continue;
            }

            // Hit a unit: the preceding digits must form a valid number.
            if (!double.TryParse(numberBuffer.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }
            numberBuffer.Clear();

            // "ms" must be checked before the single-letter "m" and "s".
            if (string.CompareOrdinal(trimmed, index, "ms", 0, 2) == 0)
            {
                total += number / 1000.0;
                index += 2;
            }
            else if (character == 'h')
            {
                total += number * 3600.0;
                index++;
            }
            else if (character == 'm')
            {
                total += number * 60.0;
                index++;
            }
            else if (character == 's')
            {
                total += number;
                index++;
            }
            else
            {
                return null; // Unrecognized unit.
            }

            matchedAnyUnit = true;
        }

        // Reject a trailing number with no unit (for example "1h30") and unit-less input.
        if (numberBuffer.Length > 0 || !matchedAnyUnit) return null;

        return double.IsFinite(total) && total >= 0 ? total : null;
    }
}
