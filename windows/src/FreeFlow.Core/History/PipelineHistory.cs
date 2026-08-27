using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeFlow.Core.History;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineHistoryIntent
{
    Dictation,
    CommandAutomatic,
    CommandManual,
}

/// <summary>
/// One recorded run of the dictation pipeline, shown in the debug panel.
/// </summary>
/// <remarks>
/// <para>
/// This record holds the user's actual dictated text, the surrounding app context,
/// and optionally a screenshot. It is sensitive by nature. It is stored only on the
/// local machine, is never transmitted anywhere, and the whole history can be cleared
/// from Settings.
/// </para>
/// <para>Ported from <c>Sources/PipelineHistoryItem.swift</c>.</para>
/// </remarks>
public sealed record PipelineHistoryItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public PipelineHistoryIntent Intent { get; init; } = PipelineHistoryIntent.Dictation;

    public string? SelectedText { get; init; }
    public string? CapturedSelection { get; init; }

    public string RawTranscript { get; init; } = "";
    public string PostProcessedTranscript { get; init; } = "";
    public string? PostProcessingPrompt { get; init; }
    public string? SystemPrompt { get; init; }

    public string ContextSummary { get; init; } = "";
    public string? ContextSystemPrompt { get; init; }
    public string? ContextPrompt { get; init; }
    public string? ContextScreenshotDataUrl { get; init; }
    public string ContextScreenshotStatus { get; init; } = "";

    public string PostProcessingStatus { get; init; } = "";
    public string DebugStatus { get; init; } = "";
    public string CustomVocabulary { get; init; } = "";
    public string? AudioFileName { get; init; }

    public string? ContextAppName { get; init; }
    public string? ContextApplicationId { get; init; }
    public string? ContextWindowTitle { get; init; }
}

/// <summary>
/// Local, bounded store of recent pipeline runs.
/// </summary>
/// <remarks>
/// <para>
/// The macOS build used Core Data. A JSON file is used here instead: the data set is
/// small and bounded, and it avoids taking a database dependency for what is a debug
/// aid. Writes are atomic, and an unreadable file degrades to an empty history rather
/// than blocking startup.
/// </para>
/// <para>
/// The entry cap exists because screenshot data URLs make items large; without it the
/// file would grow without limit.
/// </para>
/// </remarks>
public sealed class PipelineHistoryStore
{
    /// <summary>Most recent runs retained. Older entries are dropped on write.</summary>
    public const int MaxEntries = 100;

    private readonly string _filePath;
    private readonly object _gate = new();
    private List<PipelineHistoryItem> _items;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PipelineHistoryStore(string filePath)
    {
        _filePath = filePath;
        _items = Load();
    }

    public IReadOnlyList<PipelineHistoryItem> LoadAll()
    {
        lock (_gate) return _items.OrderByDescending(item => item.Timestamp).ToList();
    }

    public PipelineHistoryItem? MostRecent()
    {
        lock (_gate) return _items.OrderByDescending(item => item.Timestamp).FirstOrDefault();
    }

    public void Append(PipelineHistoryItem item)
    {
        lock (_gate)
        {
            _items.Add(item);

            if (_items.Count > MaxEntries)
            {
                _items = _items
                    .OrderByDescending(existing => existing.Timestamp)
                    .Take(MaxEntries)
                    .ToList();
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
    }

    private List<PipelineHistoryItem> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<PipelineHistoryItem>();

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<PipelineHistoryItem>>(json, SerializerOptions)
                ?? new List<PipelineHistoryItem>();
        }
        catch (Exception)
        {
            // A corrupt debug history is not worth failing over.
            return new List<PipelineHistoryItem>();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_items, SerializerOptions);
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(_filePath)) File.Replace(temporaryPath, _filePath, null);
            else File.Move(temporaryPath, _filePath);
        }
        catch (Exception)
        {
            // Ignored for the same reason as above.
        }
    }
}
