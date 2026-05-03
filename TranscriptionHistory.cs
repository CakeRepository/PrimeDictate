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
    FailedToInject,
    CommandExecuted,
    CommandFailed
}

internal sealed record TranscriptCommittedEvent(
    Guid ThreadId,
    DateTime TimestampUtc,
    string Transcript,
    TranscriptDeliveryStatus DeliveryStatus,
    string? TargetDisplayName,
    string? TargetAppName,
    string? TargetWindowTitle,
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
    string? TargetAppName,
    string? TargetWindowTitle,
    string? Error,
    double AudioDurationSeconds,
    bool SendEnterAfterCommit,
    string? OriginalTranscript,
    string? OllamaSystemPrompt)
{
    public string TargetAppDisplayName => TranscriptHistoryTargets.GetAppName(this) ?? "Unknown app";

    public string TargetWindowDisplayName => TranscriptHistoryTargets.GetWindowTitle(this) ?? "Unknown window";

    public string TargetSummary => TranscriptHistoryTargets.GetTargetSummary(this);
}

internal enum TranscriptHistoryFilter
{
    All,
    Injected,
    NotInjected
}

internal enum TranscriptHistoryTargetOptionKind
{
    All,
    Known,
    Unknown
}

internal sealed record TranscriptHistoryTargetOption(
    TranscriptHistoryTargetOptionKind Kind,
    string? Value,
    string DisplayName)
{
    public override string ToString() => this.DisplayName;
}

internal static class TranscriptHistoryTargets
{
    public static string? GetAppName(TranscriptHistoryEntry entry) => Normalize(entry.TargetAppName);

    public static string? GetWindowTitle(TranscriptHistoryEntry entry) =>
        Normalize(entry.TargetWindowTitle) ?? ExtractWindowTitle(entry.TargetDisplayName);

    public static string GetTargetSummary(TranscriptHistoryEntry entry)
    {
        var appName = GetAppName(entry);
        var windowTitle = GetWindowTitle(entry);
        if (!string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(windowTitle))
        {
            return $"{appName} - {windowTitle}";
        }

        return appName ?? windowTitle ?? "Unknown target";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractWindowTitle(string? targetDisplayName)
    {
        var normalized = Normalize(targetDisplayName);
        if (normalized is null)
        {
            return null;
        }

        var handleIndex = normalized.LastIndexOf(" (0x", StringComparison.OrdinalIgnoreCase);
        if (handleIndex > 0 && normalized.EndsWith(')'))
        {
            return normalized[..handleIndex].Trim();
        }

        return normalized.StartsWith("window 0x", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }
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
    private static readonly TranscriptHistoryTargetOption AllAppsOption =
        new(TranscriptHistoryTargetOptionKind.All, null, "All apps");

    private static readonly TranscriptHistoryTargetOption AllWindowsOption =
        new(TranscriptHistoryTargetOptionKind.All, null, "All windows");

    private static readonly TranscriptHistoryTargetOption UnknownAppOption =
        new(TranscriptHistoryTargetOptionKind.Unknown, null, "Unknown app");

    private static readonly TranscriptHistoryTargetOption UnknownWindowOption =
        new(TranscriptHistoryTargetOptionKind.Unknown, null, "Unknown window");

    private readonly List<TranscriptHistoryEntry> allEntries = [];
    private TranscriptHistoryEntry? selectedEntry;
    private TranscriptHistoryFilter selectedFilter;
    private TranscriptHistoryTargetOption selectedTargetApp = AllAppsOption;
    private TranscriptHistoryTargetOption selectedTargetWindow = AllWindowsOption;
    private string searchText = string.Empty;

    public TranscriptionHistoryViewModel()
    {
        this.Entries = new ObservableCollection<TranscriptHistoryEntry>();
        this.TargetAppOptions = new ObservableCollection<TranscriptHistoryTargetOption> { AllAppsOption };
        this.TargetWindowOptions = new ObservableCollection<TranscriptHistoryTargetOption> { AllWindowsOption };
        this.Filters = Enum.GetValues<TranscriptHistoryFilter>();
        this.selectedFilter = TranscriptHistoryFilter.All;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TranscriptHistoryEntry> Entries { get; }

    public ObservableCollection<TranscriptHistoryTargetOption> TargetAppOptions { get; }

    public ObservableCollection<TranscriptHistoryTargetOption> TargetWindowOptions { get; }

    public IReadOnlyList<TranscriptHistoryFilter> Filters { get; }

    public string SearchText
    {
        get => this.searchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (normalized == this.searchText)
            {
                return;
            }

            this.searchText = normalized;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
            this.ApplyFilter();
        }
    }

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
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
            this.ApplyFilter();
        }
    }

    public TranscriptHistoryTargetOption SelectedTargetApp
    {
        get => this.selectedTargetApp;
        set
        {
            var next = value ?? AllAppsOption;
            if (Equals(next, this.selectedTargetApp))
            {
                return;
            }

            this.selectedTargetApp = next;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
            this.RebuildTargetWindowOptions();
            this.ApplyFilter();
        }
    }

    public TranscriptHistoryTargetOption SelectedTargetWindow
    {
        get => this.selectedTargetWindow;
        set
        {
            var next = value ?? AllWindowsOption;
            if (Equals(next, this.selectedTargetWindow))
            {
                return;
            }

            this.selectedTargetWindow = next;
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
            this.ApplyFilter();
        }
    }

    public string ResultSummary => $"{this.Entries.Count:N0} of {this.allEntries.Count:N0} entries";

    public bool HasActiveRetrievalFilters =>
        !string.IsNullOrWhiteSpace(this.searchText) ||
        this.selectedFilter != TranscriptHistoryFilter.All ||
        this.selectedTargetApp.Kind != TranscriptHistoryTargetOptionKind.All ||
        this.selectedTargetWindow.Kind != TranscriptHistoryTargetOptionKind.All;

    public void Load(IEnumerable<TranscriptHistoryEntry> historyEntries)
    {
        this.allEntries.Clear();
        this.allEntries.AddRange(historyEntries
            .OrderByDescending(item => item.TimestampUtc)
            .Take(TranscriptionHistoryStore.MaxEntries));
        this.RebuildTargetAppOptions();
        this.RebuildTargetWindowOptions();
        this.ApplyFilter();
    }

    public void Add(TranscriptHistoryEntry entry)
    {
        this.allEntries.Insert(0, entry);
        if (this.allEntries.Count > TranscriptionHistoryStore.MaxEntries)
        {
            this.allEntries.RemoveAt(this.allEntries.Count - 1);
        }

        this.RebuildTargetAppOptions();
        this.RebuildTargetWindowOptions();
        this.ApplyFilter();
    }

    public void ClearRetrievalFilters()
    {
        var changed = !string.IsNullOrWhiteSpace(this.searchText) ||
            this.selectedFilter != TranscriptHistoryFilter.All ||
            this.selectedTargetApp.Kind != TranscriptHistoryTargetOptionKind.All ||
            this.selectedTargetWindow.Kind != TranscriptHistoryTargetOptionKind.All;
        if (!changed)
        {
            return;
        }

        this.searchText = string.Empty;
        this.selectedFilter = TranscriptHistoryFilter.All;
        this.selectedTargetApp = AllAppsOption;
        this.selectedTargetWindow = AllWindowsOption;
        this.OnPropertyChanged(nameof(this.SearchText));
        this.OnPropertyChanged(nameof(this.SelectedFilter));
        this.OnPropertyChanged(nameof(this.SelectedTargetApp));
        this.OnPropertyChanged(nameof(this.SelectedTargetWindow));
        this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
        this.RebuildTargetWindowOptions();
        this.ApplyFilter();
    }

    private void ApplyFilter()
    {
        var previousSelection = this.SelectedEntry;
        this.Entries.Clear();
        foreach (var item in this.allEntries)
        {
            if (this.MatchesRetrieval(item))
            {
                this.Entries.Add(item);
            }
        }

        this.SelectedEntry = previousSelection is not null && this.Entries.Contains(previousSelection)
            ? previousSelection
            : this.Entries.FirstOrDefault();
        this.OnPropertyChanged(nameof(this.ResultSummary));
    }

    private bool MatchesRetrieval(TranscriptHistoryEntry entry) =>
        MatchesFilter(entry, this.selectedFilter) &&
        MatchesSearch(entry, this.searchText) &&
        MatchesTargetOption(TranscriptHistoryTargets.GetAppName(entry), this.selectedTargetApp) &&
        MatchesTargetOption(TranscriptHistoryTargets.GetWindowTitle(entry), this.selectedTargetWindow);

    private static bool MatchesFilter(TranscriptHistoryEntry entry, TranscriptHistoryFilter filter) =>
        filter switch
        {
            TranscriptHistoryFilter.Injected => entry.DeliveryStatus == TranscriptDeliveryStatus.Injected,
            TranscriptHistoryFilter.NotInjected => entry.DeliveryStatus != TranscriptDeliveryStatus.Injected,
            _ => true
        };

    private static bool MatchesSearch(TranscriptHistoryEntry entry, string searchText)
    {
        var terms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        var searchableText = string.Join('\n', entry.Transcript, entry.OriginalTranscript);
        return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesTargetOption(string? targetValue, TranscriptHistoryTargetOption option) =>
        option.Kind switch
        {
            TranscriptHistoryTargetOptionKind.Known => string.Equals(
                targetValue,
                option.Value,
                StringComparison.OrdinalIgnoreCase),
            TranscriptHistoryTargetOptionKind.Unknown => string.IsNullOrWhiteSpace(targetValue),
            _ => true
        };

    private void RebuildTargetAppOptions()
    {
        var previousSelection = this.selectedTargetApp;
        this.TargetAppOptions.Clear();
        this.TargetAppOptions.Add(AllAppsOption);

        var knownApps = this.allEntries
            .Select(TranscriptHistoryTargets.GetAppName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        foreach (var app in knownApps)
        {
            this.TargetAppOptions.Add(new TranscriptHistoryTargetOption(
                TranscriptHistoryTargetOptionKind.Known,
                app,
                app!));
        }

        if (this.allEntries.Any(entry => TranscriptHistoryTargets.GetAppName(entry) is null))
        {
            this.TargetAppOptions.Add(UnknownAppOption);
        }

        this.selectedTargetApp = this.TargetAppOptions.FirstOrDefault(option => Equals(option, previousSelection))
            ?? AllAppsOption;
        this.OnPropertyChanged(nameof(this.SelectedTargetApp));
        if (!Equals(this.selectedTargetApp, previousSelection))
        {
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
        }
    }

    private void RebuildTargetWindowOptions()
    {
        var previousSelection = this.selectedTargetWindow;
        this.TargetWindowOptions.Clear();
        this.TargetWindowOptions.Add(AllWindowsOption);

        var matchingAppEntries = this.allEntries
            .Where(entry => MatchesTargetOption(TranscriptHistoryTargets.GetAppName(entry), this.selectedTargetApp));
        var knownWindows = matchingAppEntries
            .Select(TranscriptHistoryTargets.GetWindowTitle)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        foreach (var window in knownWindows)
        {
            this.TargetWindowOptions.Add(new TranscriptHistoryTargetOption(
                TranscriptHistoryTargetOptionKind.Known,
                window,
                window!));
        }

        if (matchingAppEntries.Any(entry => TranscriptHistoryTargets.GetWindowTitle(entry) is null))
        {
            this.TargetWindowOptions.Add(UnknownWindowOption);
        }

        this.selectedTargetWindow = this.TargetWindowOptions.FirstOrDefault(option => Equals(option, previousSelection))
            ?? AllWindowsOption;
        this.OnPropertyChanged(nameof(this.SelectedTargetWindow));
        if (!Equals(this.selectedTargetWindow, previousSelection))
        {
            this.OnPropertyChanged(nameof(this.HasActiveRetrievalFilters));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

