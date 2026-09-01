namespace FreeFlow.Core.Storage;

/// <summary>
/// Small persistence surface standing in for the macOS build's <c>UserDefaults</c>.
/// </summary>
/// <remarks>
/// Kept deliberately narrow so core logic stays testable with an in-memory
/// implementation and never has to know where settings actually live.
/// </remarks>
public interface IKeyValueStore
{
    double GetDouble(string key);
    void SetDouble(string key, double value);
    void Remove(string key);
}

/// <summary>Volatile store used by tests and as a safe default.</summary>
public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly System.Collections.Generic.Dictionary<string, double> _values = new();

    public double GetDouble(string key) => _values.TryGetValue(key, out var value) ? value : 0;

    public void SetDouble(string key, double value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}
