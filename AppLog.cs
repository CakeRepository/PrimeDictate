using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PrimeDictate;

internal enum AppLogLevel
{
    Info,
    Error
}

internal sealed record AppLogEntry(
    DateTime TimestampUtc,
    AppLogLevel Level,
    string Message,
    Guid? ThreadId = null,
    int RepeatCount = 1)
{
    public string DisplayMessage =>
        this.RepeatCount > 1 ? $"{this.Message} (x{this.RepeatCount})" : this.Message;
}

internal static class AppLog
{
    public static event Action<AppLogEntry>? EntryWritten;

    public static void Info(string message, Guid? threadId = null) =>
        Write(AppLogLevel.Info, message, threadId);

    public static void Error(string message, Guid? threadId = null) =>
        Write(AppLogLevel.Error, message, threadId);

    public static void Write(AppLogLevel level, string message, Guid? threadId = null)
    {
        EntryWritten?.Invoke(new AppLogEntry(DateTime.UtcNow, level, message, threadId));
    }
}

internal sealed class DictationThreadViewModel : INotifyPropertyChanged
{
    private string latestTranscript = string.Empty;

    public DictationThreadViewModel(Guid id)
    {
        this.Id = id;
        this.Title = $"Session {id.ToString()[..8]}";
        this.StartedAtUtc = DateTime.UtcNow;
        this.Entries = new ObservableCollection<AppLogEntry>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public string Title { get; }

    public DateTime StartedAtUtc { get; }

    public DateTime? EndedAtUtc { get; private set; }

    public ObservableCollection<AppLogEntry> Entries { get; }

    public string LatestTranscript
    {
        get => this.latestTranscript;
        set
        {
            if (value == this.latestTranscript)
            {
                return;
            }

            this.latestTranscript = value;
            this.OnPropertyChanged();
        }
    }

    public string Summary =>
        this.EndedAtUtc is null
            ? $"{this.StartedAtUtc.ToLocalTime():t} - active"
            : $"{this.StartedAtUtc.ToLocalTime():t} - {this.EndedAtUtc.Value.ToLocalTime():t}";

    public void MarkCompleted()
    {
        this.EndedAtUtc = DateTime.UtcNow;
        this.OnPropertyChanged(nameof(this.Summary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class DictationWorkspaceViewModel : INotifyPropertyChanged
{
    private const int MaxGlobalEntries = 600;
    private const int MaxThreadEntries = 300;
    private const int MaxThreads = 100;

    private DictationThreadViewModel? selectedThread;

    public DictationWorkspaceViewModel()
    {
        this.Threads = new ObservableCollection<DictationThreadViewModel>();
        this.GlobalEntries = new ObservableCollection<AppLogEntry>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DictationThreadViewModel> Threads { get; }

    public ObservableCollection<AppLogEntry> GlobalEntries { get; }

    public DictationThreadViewModel? SelectedThread
    {
        get => this.selectedThread;
        set
        {
            if (ReferenceEquals(value, this.selectedThread))
            {
                return;
            }

            this.selectedThread = value;
            this.OnPropertyChanged();
        }
    }

    public DictationThreadViewModel StartThread(Guid threadId)
    {
        var thread = new DictationThreadViewModel(threadId);
        this.Threads.Insert(0, thread);
        while (this.Threads.Count > MaxThreads)
        {
            this.Threads.RemoveAt(this.Threads.Count - 1);
        }

        this.SelectedThread = thread;
        return thread;
    }

    public DictationThreadViewModel? GetThread(Guid threadId) =>
        this.Threads.FirstOrDefault(t => t.Id == threadId);

    public void AppendEntry(AppLogEntry entry)
    {
        InsertWithAggregation(this.GlobalEntries, entry, MaxGlobalEntries);
        if (entry.ThreadId is Guid threadId && this.GetThread(threadId) is { } thread)
        {
            InsertWithAggregation(thread.Entries, entry, MaxThreadEntries);
        }
    }

    public void MarkThreadCompleted(Guid threadId)
    {
        var thread = this.GetThread(threadId);
        thread?.MarkCompleted();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void InsertWithAggregation(ObservableCollection<AppLogEntry> target, AppLogEntry entry, int maxEntries)
    {
        if (target.Count > 0 && CanAggregate(target[0], entry))
        {
            var currentTop = target[0];
            target[0] = currentTop with
            {
                TimestampUtc = entry.TimestampUtc,
                RepeatCount = currentTop.RepeatCount + 1
            };
        }
        else
        {
            target.Insert(0, entry);
            while (target.Count > maxEntries)
            {
                target.RemoveAt(target.Count - 1);
            }
        }
    }

    private static bool CanAggregate(AppLogEntry existing, AppLogEntry incoming) =>
        existing.Level == incoming.Level &&
        existing.ThreadId == incoming.ThreadId &&
        string.Equals(existing.Message, incoming.Message, StringComparison.Ordinal);
}
