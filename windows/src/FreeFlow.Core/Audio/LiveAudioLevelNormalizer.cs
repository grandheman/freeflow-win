using System;

namespace FreeFlow.Core.Audio;

/// <summary>
/// Turns raw RMS into a 0-1 level for the recording overlay meter.
/// </summary>
/// <remarks>
/// <para>
/// A fixed dB range looks dead on a quiet mic and pinned on a loud one, so the
/// normalizer tracks a rolling noise floor and peak ceiling and scales between them.
/// The floor falls quickly and rises slowly, so a burst of background noise does not
/// permanently desensitize the meter.
/// </para>
/// <para>
/// Ported unchanged from <c>Sources/LiveAudioLevelNormalizer.swift</c>. The constants
/// are tuned; changing them changes how the meter feels.
/// </para>
/// </remarks>
public struct LiveAudioLevelNormalizer
{
    private const float MinimumRms = 0.00001f;
    private const float MinSpanDb = 18;
    private const float PeakHeadroomDb = 8;
    private const float SpeechGateMarginDb = 3;
    private const float MinimumVisibleActiveLevel = 0.12f;
    private const float NoiseGateNormalizedThreshold = 0.06f;
    private const float FloorRiseWindowDb = 4;
    private const float FloorFallBlend = 0.12f;
    private const float FloorRiseBlend = 0.02f;
    private const float PeakAttackBlend = 0.55f;
    private const float PeakReleaseBlend = 0.04f;
    private const float DisplayAttackBlend = 0.45f;
    private const float DisplayReleaseBlend = 0.12f;

    private float _noiseFloorDb = -55;
    private float _peakCeilingDb = -37;
    private float _displayLevel;

    public LiveAudioLevelNormalizer() { }

    public void Reset()
    {
        _noiseFloorDb = -55;
        _peakCeilingDb = -37;
        _displayLevel = 0;
    }

    public float NormalizedLevel(float rms)
    {
        var levelDb = 20 * MathF.Log10(MathF.Max(rms, MinimumRms));

        UpdateNoiseFloor(levelDb);
        UpdatePeakCeiling(levelDb);

        var displayCeilingDb = _peakCeilingDb + PeakHeadroomDb;
        var dynamicSpan = MathF.Max(displayCeilingDb - _noiseFloorDb, MinSpanDb + PeakHeadroomDb);
        var normalized = Clamp((levelDb - _noiseFloorDb) / dynamicSpan);
        var isActiveSpeech = levelDb >= _noiseFloorDb + SpeechGateMarginDb;

        if (normalized < NoiseGateNormalizedThreshold && levelDb <= _noiseFloorDb + SpeechGateMarginDb)
        {
            normalized = 0;
        }
        else if (isActiveSpeech)
        {
            // Keep real speech visible even when it barely clears the floor.
            normalized = MathF.Max(normalized, MinimumVisibleActiveLevel);
        }

        var blend = normalized > _displayLevel ? DisplayAttackBlend : DisplayReleaseBlend;
        _displayLevel = Mix(_displayLevel, normalized, blend);
        return _displayLevel;
    }

    private void UpdateNoiseFloor(float levelDb)
    {
        var ceilingLimitedLevel = MathF.Min(levelDb, _peakCeilingDb - MinSpanDb);

        if (ceilingLimitedLevel <= _noiseFloorDb)
        {
            _noiseFloorDb = Mix(_noiseFloorDb, ceilingLimitedLevel, FloorFallBlend);
        }
        else if (ceilingLimitedLevel <= _noiseFloorDb + FloorRiseWindowDb)
        {
            _noiseFloorDb = Mix(_noiseFloorDb, ceilingLimitedLevel, FloorRiseBlend);
        }
    }

    private void UpdatePeakCeiling(float levelDb)
    {
        var minimumCeiling = _noiseFloorDb + MinSpanDb;

        if (levelDb >= _peakCeilingDb)
        {
            _peakCeilingDb = Mix(_peakCeilingDb, levelDb, PeakAttackBlend);
        }
        else
        {
            _peakCeilingDb = Mix(_peakCeilingDb, MathF.Max(levelDb, minimumCeiling), PeakReleaseBlend);
        }

        _peakCeilingDb = MathF.Max(_peakCeilingDb, minimumCeiling);
    }

    private static float Mix(float current, float target, float blend)
        => current + (target - current) * blend;

    private static float Clamp(float value) => MathF.Min(MathF.Max(value, 0), 1);
}
