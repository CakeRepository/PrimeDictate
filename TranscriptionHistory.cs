using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrimeDictate;

internal enum TranscriptDeliveryStatus
{
    Injected,
    SkippedFocusChanged,
    FailedToInject
}

internal sealed record TranscriptCommittedEvent(
    Guid ThreadId,
    DateTime TimestampUtc,
    string Transcript,
    TranscriptDeliveryStatus DeliveryStatus,
    string? TargetDisplayName,
    string? Error,
    TimeSpan AudioDuration,
    bool SendEnterAfterCommit,
    string? OriginalTranscript,
    string? OllamaSystemPrompt);

internal sealed record TranscriptHistoryEntry(
    Guid Id,
    Guid ThreadId,
    DateTime TimestampUtc,
    string Transcript,
    TranscriptDeliveryStatus DeliveryStatus,
    string? TargetDisplayName,
    string? Error,
    double AudioDurationSeconds,
    bool SendEnterAfterCommit,
    string? OriginalTranscript,
    string? OllamaSystemPrompt);

internal enum TranscriptHistoryFilter
{
    All,
    Injected,
    NotInjected
}

internal sealed class TranscriptionHistoryStore
{
    internal const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string historyPath;
    private readonly object sync = new();

    public TranscriptionHistoryStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrimeDictate");
        this.historyPath = Path.Combine(baseDir, "history.json");
    }

    public string HistoryPath => this.historyPath;

    public IReadOnlyList<TranscriptHistoryEntry> Load()
    {
        lock (this.sync)
        {
            if (!File.Exists(this.historyPath))
            {
                return [];
            }

            var json = File.ReadAllText(this.historyPath);
            var parsed = JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(json, JsonOptions);
            return parsed ?? [];
        }
    }

    public void Append(TranscriptHistoryEntry entry)
    {
        lock (this.sync)
        {
            List<TranscriptHistoryEntry> entries;
            if (File.Exists(this.historyPath))
            {
                var json = File.ReadAllText(this.historyPath);
                entries = JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(json, JsonOptions) ?? [];
            }
            else
            {
                entries = [];
            }

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            var folder = Path.GetDirectoryName(this.historyPath)
                ?? throw new InvalidOperationException("History directory is invalid.");
            Directory.CreateDirectory(folder);
            File.WriteAllText(this.historyPath, JsonSerializer.Serialize(entries, JsonOptions));
        }
    }
}

internal sealed class TranscriptionHistoryViewModel : INotifyPropertyChanged
{
    private readonly List<TranscriptHistoryEntry> allEntries = [];
    private TranscriptHistoryEntry? selectedEntry;
    private TranscriptHistoryFilter selectedFilter;

    public TranscriptionHistoryViewModel()
    {
        this.Entries = new ObservableCollection<TranscriptHistoryEntry>();
        this.Filters = Enum.GetValues<TranscriptHistoryFilter>();
        this.selectedFilter = TranscriptHistoryFilter.All;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TranscriptHistoryEntry> Entries { get; }

    public IReadOnlyList<TranscriptHistoryFilter> Filters { get; }

    public TranscriptHistoryEntry? SelectedEntry
    {
        get => this.selectedEntry;
        set
        {
            if (ReferenceEquals(value, this.selectedEntry))
            {
                return;
            }

            this.selectedEntry = value;
            this.OnPropertyChanged();
        }
    }

    public TranscriptHistoryFilter SelectedFilter
    {
        get => this.selectedFilter;
        set
        {
            if (value == this.selectedFilter)
            {
                return;
            }

            this.selectedFilter = value;
            this.OnPropertyChanged();
            this.ApplyFilter();
        }
    }

    public void Load(IEnumerable<TranscriptHistoryEntry> historyEntries)
    {
        this.allEntries.Clear();
        this.allEntries.AddRange(historyEntries
            .OrderByDescending(item => item.TimestampUtc)
            .Take(TranscriptionHistoryStore.MaxEntries));
        this.ApplyFilter();
    }

    public void Add(TranscriptHistoryEntry entry)
    {
        this.allEntries.Insert(0, entry);
        TranscriptHistoryEntry? removedEntry = null;
        if (this.allEntries.Count > TranscriptionHistoryStore.MaxEntries)
        {
            removedEntry = this.allEntries[^1];
            this.allEntries.RemoveAt(this.allEntries.Count - 1);
        }

        if (MatchesFilter(entry, this.selectedFilter))
        {
            this.Entries.Insert(0, entry);
            this.SelectedEntry ??= entry;
        }

        if (removedEntry is not null)
        {
            this.Entries.Remove(removedEntry);
            if (Equals(this.SelectedEntry, removedEntry))
            {
                this.SelectedEntry = this.Entries.FirstOrDefault();
            }
        }
    }

    private void ApplyFilter()
    {
        this.Entries.Clear();
        foreach (var item in this.allEntries)
        {
            if (MatchesFilter(item, this.selectedFilter))
            {
                this.Entries.Add(item);
            }
        }

        this.SelectedEntry = this.Entries.FirstOrDefault();
    }

    private static bool MatchesFilter(TranscriptHistoryEntry entry, TranscriptHistoryFilter filter) =>
        filter switch
        {
            TranscriptHistoryFilter.Injected => entry.DeliveryStatus == TranscriptDeliveryStatus.Injected,
            TranscriptHistoryFilter.NotInjected => entry.DeliveryStatus != TranscriptDeliveryStatus.Injected,
            _ => true
        };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

