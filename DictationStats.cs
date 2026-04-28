using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrimeDictate;

internal sealed class DictationStatsState
{
    public long TotalWords { get; set; }

    public long TotalCharacters { get; set; }

    public double TotalAudioSeconds { get; set; }

    public int TotalSessions { get; set; }

    public int InjectedSessions { get; set; }

    public DateTime? FirstCommitUtc { get; set; }

    public DateTime? LastCommitUtc { get; set; }

    public List<DailyDictationStats> DailyStats { get; set; } = [];

    public HashSet<string> UnlockedAchievementIds { get; set; } = [];
}

internal sealed class DailyDictationStats
{
    public string Date { get; set; } = "";

    public long Words { get; set; }

    public long Characters { get; set; }

    public double AudioSeconds { get; set; }

    public int Sessions { get; set; }
}

internal sealed record DictationAchievement(
    string Id,
    string Title,
    string Message,
    long WordThreshold);

internal sealed record DictationStatsUpdate(
    DictationStatsState State,
    IReadOnlyList<DictationAchievement> NewAchievements);

internal sealed class DictationStatsStore
{
    private const int MaxDailyBuckets = 370;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static readonly IReadOnlyList<DictationAchievement> Achievements =
    [
        new("words-1000", "First 1,000 words", "PrimeDictate has typed your first 1,000 words.", 1_000),
        new("words-10000", "10,000-word flow", "You have dictated 10,000 words locally.", 10_000),
        new("words-100000", "100,000-word milestone", "PrimeDictate has helped you type 100,000 words.", 100_000),
        new("words-1000000", "Million-word engine", "PrimeDictate has helped you type 1,000,000 words.", 1_000_000)
    ];

    private readonly string statsPath;
    private readonly object sync = new();

    public DictationStatsStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrimeDictate");
        this.statsPath = Path.Combine(baseDir, "stats.json");
    }

    public string StatsPath => this.statsPath;

    public DictationStatsState LoadOrCreate(IReadOnlyList<TranscriptHistoryEntry> historyEntries)
    {
        lock (this.sync)
        {
            if (File.Exists(this.statsPath))
            {
                var json = File.ReadAllText(this.statsPath);
                return JsonSerializer.Deserialize<DictationStatsState>(json, JsonOptions) ?? new DictationStatsState();
            }

            var state = AggregateFromHistory(historyEntries);
            SaveCore(state);
            return state;
        }
    }

    public DictationStatsUpdate RecordCommit(TranscriptHistoryEntry entry)
    {
        lock (this.sync)
        {
            var state = File.Exists(this.statsPath)
                ? JsonSerializer.Deserialize<DictationStatsState>(File.ReadAllText(this.statsPath), JsonOptions) ?? new DictationStatsState()
                : new DictationStatsState();

            var previousWords = state.TotalWords;
            ApplyEntry(state, entry);
            var newlyUnlocked = UnlockAchievements(state, previousWords);
            SaveCore(state);
            return new DictationStatsUpdate(state, newlyUnlocked);
        }
    }

    internal static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var words = 0;
        var inWord = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (character == '\'' && inWord)
            {
                continue;
            }
            else
            {
                inWord = false;
            }
        }

        return words;
    }

    internal static int CountNonWhiteSpaceCharacters(string text)
    {
        var characters = 0;
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                characters++;
            }
        }

        return characters;
    }

    private static DictationStatsState AggregateFromHistory(IEnumerable<TranscriptHistoryEntry> historyEntries)
    {
        var state = new DictationStatsState();
        foreach (var entry in historyEntries.OrderBy(item => item.TimestampUtc))
        {
            ApplyEntry(state, entry);
        }

        UnlockAchievements(state, previousTotalWords: 0);
        return state;
    }

    private static void ApplyEntry(DictationStatsState state, TranscriptHistoryEntry entry)
    {
        state.TotalSessions++;
        state.FirstCommitUtc ??= entry.TimestampUtc;
        state.LastCommitUtc = entry.TimestampUtc;

        if (entry.DeliveryStatus != TranscriptDeliveryStatus.Injected)
        {
            return;
        }

        var words = CountWords(entry.Transcript);
        if (words == 0)
        {
            return;
        }

        var characters = CountNonWhiteSpaceCharacters(entry.Transcript);
        state.TotalWords += words;
        state.TotalCharacters += characters;
        state.TotalAudioSeconds += Math.Max(0, entry.AudioDurationSeconds);
        state.InjectedSessions++;

        var daily = GetOrCreateDailyStats(state, entry.TimestampUtc.ToLocalTime());
        daily.Words += words;
        daily.Characters += characters;
        daily.AudioSeconds += Math.Max(0, entry.AudioDurationSeconds);
        daily.Sessions++;

        TrimDailyBuckets(state);
    }

    private static DailyDictationStats GetOrCreateDailyStats(DictationStatsState state, DateTime timestampLocal)
    {
        var date = timestampLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var existing = state.DailyStats.FirstOrDefault(day => string.Equals(day.Date, date, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var created = new DailyDictationStats { Date = date };
        state.DailyStats.Add(created);
        state.DailyStats.Sort((left, right) => string.Compare(left.Date, right.Date, StringComparison.Ordinal));
        return created;
    }

    private static IReadOnlyList<DictationAchievement> UnlockAchievements(DictationStatsState state, long previousTotalWords)
    {
        List<DictationAchievement>? unlocked = null;
        foreach (var achievement in Achievements)
        {
            if (previousTotalWords < achievement.WordThreshold &&
                state.TotalWords >= achievement.WordThreshold &&
                state.UnlockedAchievementIds.Add(achievement.Id))
            {
                unlocked ??= [];
                unlocked.Add(achievement);
            }
        }

        return unlocked ?? [];
    }

    private static void TrimDailyBuckets(DictationStatsState state)
    {
        if (state.DailyStats.Count <= MaxDailyBuckets)
        {
            return;
        }

        state.DailyStats.Sort((left, right) => string.Compare(left.Date, right.Date, StringComparison.Ordinal));
        state.DailyStats.RemoveRange(0, state.DailyStats.Count - MaxDailyBuckets);
    }

    private void SaveCore(DictationStatsState state)
    {
        var folder = Path.GetDirectoryName(this.statsPath)
            ?? throw new InvalidOperationException("Stats directory is invalid.");
        Directory.CreateDirectory(folder);
        File.WriteAllText(this.statsPath, JsonSerializer.Serialize(state, JsonOptions));
    }
}
