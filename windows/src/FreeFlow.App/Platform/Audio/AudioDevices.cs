using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace FreeFlow.App.Platform.Audio;

/// <summary>A selectable microphone.</summary>
/// <param name="Id">Stable endpoint id, persisted in settings.</param>
/// <param name="Name">Friendly name shown in the UI.</param>
/// <param name="IsDefault">True for the current system default capture device.</param>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates capture endpoints.
/// </summary>
/// <remarks>
/// Replaces the <c>AVCaptureDevice.DiscoverySession</c> helpers in
/// <c>Sources/AudioRecorder.swift</c>.
/// </remarks>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> CaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = defaultDevice.ID;
        }
        catch (Exception)
        {
            // No default capture device is a normal state on a machine with no microphone.
        }

        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return devices;
    }

    /// <summary>
    /// Resolves a saved device id, falling back to the system default when that device
    /// is gone (unplugged headset, changed dock).
    /// </summary>
    public static MMDevice? Resolve(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception)
            {
                // Fall through to the default device below.
            }
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? DefaultDeviceId()
        => CaptureDevices().FirstOrDefault(device => device.IsDefault)?.Id;
}
