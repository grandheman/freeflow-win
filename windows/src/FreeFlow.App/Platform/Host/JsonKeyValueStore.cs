using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FreeFlow.Core.Storage;

namespace FreeFlow.App.Platform.Host;

/// <summary>
/// Standard on-disk locations for FreeFlow data.
/// </summary>
/// <remarks>
/// Everything lives under the roaming profile so settings follow a domain user, with
/// recordings in the local temp area because they are transient and can be large.
/// </remarks>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeFlow");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "pipeline-history.json");

    public static string CredentialsFile => Path.Combine(DataDirectory, "credentials.dat");

    public static string RecordingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "FreeFlow");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
    }
}

/// <summary>
/// JSON-file implementation of the core key-value store.
/// </summary>
/// <remarks>
/// <para>
/// Stands in for the macOS build's <c>UserDefaults</c>. Writes go through a temporary
/// file and an atomic replace, so a crash or power loss mid-write cannot leave a
/// truncated settings file behind.
/// </para>
/// <para>
/// A corrupt file is treated as empty rather than fatal: losing preferences is
/// recoverable, refusing to start is not.
/// </para>
/// </remarks>
public sealed class JsonKeyValueStore : IKeyValueStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, JsonElement> _values;

    public JsonKeyValueStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.SettingsFile;
        _values = Load(_filePath);
    }

    private static Dictionary<string, JsonElement> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            var json = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return parsed ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings must not prevent startup.
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureCreated();

            var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(_filePath)) File.Replace(temporaryPath, _filePath, null);
            else File.Move(temporaryPath, _filePath);
        }
        catch (Exception)
        {
            // A failed settings write is not worth crashing over.
        }
    }

    public double GetDouble(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;
        }
    }

    public void SetDouble(string key, double value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public string? GetString(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }

    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var value)) return defaultValue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue,
            };
        }
    }

    public void SetBool(string key, bool value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public T? GetObject<T>(string key) where T : class
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var value)) return null;
            try
            {
                return value.Deserialize<T>();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void SetObject<T>(string key, T value) where T : class
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_values.Remove(key)) Save();
        }
    }
}
