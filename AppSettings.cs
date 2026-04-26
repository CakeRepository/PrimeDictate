using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }

    public HotkeyGesture DictationHotkey { get; set; } = HotkeyGesture.Default;

    public TrayClickBehavior TrayClickBehavior { get; set; } = TrayClickBehavior.DoubleClickOpensSettings;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TranscriptionBackendKind TranscriptionBackend { get; set; } = TranscriptionBackendKind.Whisper;

    public string? SelectedModelId { get; set; }

    public string? ModelPath { get; set; }

    public bool ExclusiveMicAccessWhileDictating { get; set; }

    public int AutoCommitSilenceSeconds { get; set; } = 3;

    public bool SendEnterAfterCommit { get; set; }

    public bool ReturnToStartTargetOnCommit { get; set; }

    public bool PlayAudioCues { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayMode OverlayMode { get; set; } = OverlayMode.CompactMicrophone;

    public bool IsOverlaySticky { get; set; }

    public bool EnableOllamaPostProcessing { get; set; }

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "gemma:2b";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OllamaMode OllamaMode { get; set; } = OllamaMode.Default;

    public static AppSettings CreateDefaultForFirstRun() => new()
    {
        FirstRunCompleted = false,
        DictationHotkey = HotkeyGesture.Default,
        TrayClickBehavior = TrayClickBehavior.DoubleClickOpensSettings,
        TranscriptionBackend = TranscriptionBackendKind.Whisper,
        SelectedModelId = null,
        ModelPath = null,
        ExclusiveMicAccessWhileDictating = false,
        AutoCommitSilenceSeconds = 3,
        SendEnterAfterCommit = false,
        ReturnToStartTargetOnCommit = false,
        PlayAudioCues = true,
        OverlayMode = OverlayMode.CompactMicrophone,
        IsOverlaySticky = false,
        EnableOllamaPostProcessing = false,
        OllamaEndpoint = "http://localhost:11434",
        OllamaModel = "gemma:2b",
        OllamaMode = OllamaMode.Default
    };
}

internal enum TrayClickBehavior
{
    SingleClickOpensSettings = 0,
    DoubleClickOpensSettings = 1
}

internal enum TranscriptionBackendKind
{
    Whisper = 0,
    Parakeet = 1,
    Moonshine = 2
}

internal enum OverlayMode
{
    CompactMicrophone = 0,
    FullPanel = 1
}

internal enum OllamaMode
{
    Default = 0,
    Prompt = 1,
    Bug = 2,
    Update = 3,
    Communication = 4,
    Blog = 5,
    VibeCoding = 6
}

internal sealed class HotkeyGesture
{
    public static HotkeyGesture Default => new()
    {
        KeyCode = KeyCode.VcSpace,
        Ctrl = true,
        Shift = true,
        Alt = false
    };

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyCode KeyCode { get; set; }

    public bool Ctrl { get; set; }

    public bool Shift { get; set; }

    public bool Alt { get; set; }

    public bool IsValid(out string error)
    {
        if (!this.Ctrl && !this.Shift && !this.Alt)
        {
            error = "Hotkey must include at least one modifier key (Ctrl, Shift, or Alt).";
            return false;
        }

        if (this.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl
            or KeyCode.VcLeftShift or KeyCode.VcRightShift
            or KeyCode.VcLeftAlt or KeyCode.VcRightAlt)
        {
            error = "Hotkey key cannot be a modifier key.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (this.Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (this.Shift)
        {
            parts.Add("Shift");
        }

        if (this.Alt)
        {
            parts.Add("Alt");
        }

        parts.Add(this.KeyCode switch
        {
            >= KeyCode.VcA and <= KeyCode.VcZ => this.KeyCode.ToString().Replace("Vc", string.Empty),
            >= KeyCode.Vc0 and <= KeyCode.Vc9 => this.KeyCode.ToString().Replace("Vc", string.Empty),
            _ => this.KeyCode switch
            {
                KeyCode.VcSpace => "Space",
                _ => this.KeyCode.ToString().Replace("Vc", string.Empty)
            }
        });

        return string.Join("+", parts);
    }
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string settingsPath;

    public SettingsStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrimeDictate");
        this.settingsPath = Path.Combine(baseDir, "settings.json");
    }

    public string SettingsPath => this.settingsPath;

    public AppSettings LoadOrDefault()
    {
        if (!File.Exists(this.settingsPath))
        {
            return AppSettings.CreateDefaultForFirstRun();
        }

        var json = File.ReadAllText(this.settingsPath);
        var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        return parsed ?? AppSettings.CreateDefaultForFirstRun();
    }

    public void Save(AppSettings settings)
    {
        var folder = Path.GetDirectoryName(this.settingsPath)
            ?? throw new InvalidOperationException("Settings directory is invalid.");
        Directory.CreateDirectory(folder);
        File.WriteAllText(this.settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
