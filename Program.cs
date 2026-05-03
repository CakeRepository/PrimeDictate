using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed class GlobalHotkeyListener : IDisposable
{
    private readonly IGlobalHook hook;
    private readonly Func<Task> onDictationHotkeyPressedAsync;
    private readonly Func<Task> onStopHotkeyPressedAsync;
    private readonly Func<Task> onHistoryHotkeyPressedAsync;
    private readonly object configSync = new();
    private HotkeyGesture dictationHotkey;
    private HotkeyGesture stopHotkey;
    private HotkeyGesture historyHotkey;

    public GlobalHotkeyListener(
        Func<Task> onDictationHotkeyPressedAsync,
        Func<Task> onStopHotkeyPressedAsync,
        Func<Task> onHistoryHotkeyPressedAsync,
        HotkeyGesture dictationHotkey,
        HotkeyGesture stopHotkey,
        HotkeyGesture historyHotkey)
    {
        this.onDictationHotkeyPressedAsync = onDictationHotkeyPressedAsync;
        this.onStopHotkeyPressedAsync = onStopHotkeyPressedAsync;
        this.onHistoryHotkeyPressedAsync = onHistoryHotkeyPressedAsync;
        this.dictationHotkey = dictationHotkey;
        this.stopHotkey = stopHotkey;
        this.historyHotkey = historyHotkey;
        this.hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
        this.hook.KeyPressed += this.OnKeyPressed;
    }

    public Task RunAsync() => this.hook.RunAsync();

    public void Dispose()
    {
        this.hook.KeyPressed -= this.OnKeyPressed;
        this.hook.Dispose();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs args)
    {
        var action = this.MatchHotkey(args);
        if (action is null)
        {
            return;
        }

        args.SuppressEvent = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Hotkey action failed: {ex.Message}");
            }
        });
    }

    public void UpdateHotkeys(HotkeyGesture dictationHotkey, HotkeyGesture stopHotkey, HotkeyGesture historyHotkey)
    {
        lock (this.configSync)
        {
            this.dictationHotkey = dictationHotkey;
            this.stopHotkey = stopHotkey;
            this.historyHotkey = historyHotkey;
        }
    }

    private Func<Task>? MatchHotkey(KeyboardHookEventArgs args)
    {
        var mask = args.RawEvent.Mask;
        HotkeyGesture currentDictationHotkey;
        HotkeyGesture currentStopHotkey;
        HotkeyGesture currentHistoryHotkey;
        lock (this.configSync)
        {
            currentDictationHotkey = this.dictationHotkey;
            currentStopHotkey = this.stopHotkey;
            currentHistoryHotkey = this.historyHotkey;
        }

        if (Matches(args, mask, currentStopHotkey))
        {
            return this.onStopHotkeyPressedAsync;
        }

        if (Matches(args, mask, currentHistoryHotkey))
        {
            return this.onHistoryHotkeyPressedAsync;
        }

        return Matches(args, mask, currentDictationHotkey)
            ? this.onDictationHotkeyPressedAsync
            : null;
    }

    private static bool Matches(KeyboardHookEventArgs args, EventMask mask, HotkeyGesture hotkey) =>
        args.Data.KeyCode == hotkey.KeyCode &&
        (!hotkey.Ctrl || mask.HasCtrl()) &&
        (!hotkey.Shift || mask.HasShift()) &&
        (!hotkey.Alt || mask.HasAlt());
}

internal sealed class DictationController : IAsyncDisposable
{
    private static readonly TimeSpan LiveTranscribeInterval = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan LiveMinAudio = TimeSpan.FromSeconds(0.55);
    private static readonly TimeSpan LivePreviewMaxAudio = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan VoiceShellTypeTargetWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VoiceShellTypePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SilenceProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecentSpeechWindow = TimeSpan.FromMilliseconds(450);
    private const double MinSpeechRmsThreshold = 0.0018;
    private const double MaxSpeechRmsThreshold = 0.02;
    private const double NoiseFloorSmoothing = 0.08;

    private readonly SemaphoreSlim toggleGate = new(initialCount: 1, maxCount: 1);
    private readonly DefaultMicrophoneRecorder recorder = new();
    private readonly WhisperTextInjectionPipeline textInjectionPipeline = new();
    private readonly object configSync = new();
    private bool exclusiveMicAccessWhileDictating;
    private string? selectedInputDeviceId;
    private double inputGainMultiplier;
    private TimeSpan autoCommitSilenceDelay;
    private bool sendEnterAfterCommit;
    private bool returnToStartTargetOnCommit;
    private bool enableVoiceCommands = true;
    private string voiceStopPhrase = "potato farmer";
    private string voiceHistoryPhrase = "show me the money";
    private List<VoiceShellCommand> voiceShellCommands = new();

    private CancellationTokenSource? livePreviewCts;
    private Task? livePreviewTask;
    private Guid? activeThreadId;
    private ForegroundInputTarget? activeInputTarget;
    private int autoCommitRequested;
    private int emergencyStopRequested;
    private int voiceStopRequested;
    private int voiceHistoryRequested;
    private bool enableOllamaPostProcessing;
    private string ollamaEndpoint = "http://localhost:11434";
    private string ollamaModel = "gemma:2b";
    private OllamaMode ollamaMode = OllamaMode.Default;
    private List<TranscriptReplacementRule> transcriptReplacements = new();
    private long lastSpeechTicksUtc;
    private double adaptiveNoiseFloorRms = MinSpeechRmsThreshold;
    private double maxObservedRmsThisSession;

    public event Action<bool>? RecordingStateChanged;
    public event Action<bool>? ProcessingStateChanged;
    public event Action<Guid>? ThreadStarted;
    public event Action<Guid>? ThreadCompleted;
    public event Action<Guid, string>? ThreadTranscriptUpdated;
    public event Action<TranscriptCommittedEvent>? TranscriptCommitted;
    public event Action<double>? AudioLevelUpdated;
    public event Action? HistoryRequested;

    public DictationController(
        bool exclusiveMicAccessWhileDictating = false,
        string? selectedInputDeviceId = null,
        double inputGainMultiplier = 1.0,
        TimeSpan? autoCommitSilenceDelay = null,
        bool sendEnterAfterCommit = false,
        bool returnToStartTargetOnCommit = false,
        TranscriptionBackendKind transcriptionBackend = TranscriptionBackendKind.Whisper,
        TranscriptionComputeInterface transcriptionComputeInterface = TranscriptionComputeInterface.Cpu,
        string? selectedModelId = null,
        string? modelPath = null,
        bool enableOllamaPostProcessing = false,
        string ollamaEndpoint = "http://localhost:11434",
        string ollamaModel = "gemma:2b",
        OllamaMode ollamaMode = OllamaMode.Default,
        bool enableVoiceCommands = true,
        string voiceStopPhrase = "potato farmer",
        string voiceHistoryPhrase = "show me the money",
        IReadOnlyList<VoiceShellCommand>? voiceShellCommands = null,
        IReadOnlyList<TranscriptReplacementRule>? transcriptReplacements = null)
    {
        this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
        this.selectedInputDeviceId = string.IsNullOrWhiteSpace(selectedInputDeviceId) ? null : selectedInputDeviceId;
        this.inputGainMultiplier = NormalizeInputGain(inputGainMultiplier);
        this.autoCommitSilenceDelay = NormalizeSilenceDelay(autoCommitSilenceDelay ?? TimeSpan.FromSeconds(3));
        this.sendEnterAfterCommit = sendEnterAfterCommit;
        this.returnToStartTargetOnCommit = returnToStartTargetOnCommit;
        this.enableOllamaPostProcessing = enableOllamaPostProcessing;
        this.ollamaEndpoint = ollamaEndpoint;
        this.ollamaModel = ollamaModel;
        this.ollamaMode = ollamaMode;
        this.enableVoiceCommands = enableVoiceCommands;
        this.voiceStopPhrase = NormalizeVoiceCommandPhrase(voiceStopPhrase);
        this.voiceHistoryPhrase = NormalizeVoiceCommandPhrase(voiceHistoryPhrase);
        this.ReplaceVoiceShellCommands(voiceShellCommands);
        this.ReplaceTranscriptReplacementRules(transcriptReplacements);
        this.textInjectionPipeline.UpdateConfiguration(
            transcriptionBackend,
            transcriptionComputeInterface,
            selectedModelId,
            modelPath);
        this.recorder.UpdateInputDevice(this.selectedInputDeviceId);
        this.recorder.UpdateInputGain(this.inputGainMultiplier);
        this.recorder.AudioLevelUpdated += this.OnRecorderAudioLevelUpdated;
    }

    public bool IsRecording => this.recorder.IsRecording;

    public string ActiveMicAccessModeLabel => this.recorder.ActiveShareMode switch
    {
        AudioClientShareMode.Exclusive => "Exclusive",
        AudioClientShareMode.Shared => "Shared",
        _ => "N/A"
    };

    public void UpdateCaptureOptions(
        bool exclusiveMicAccessWhileDictating,
        string? selectedInputDeviceId,
        double inputGainMultiplier,
        TimeSpan autoCommitSilenceDelay,
        bool sendEnterAfterCommit,
        bool returnToStartTargetOnCommit,
        TranscriptionBackendKind transcriptionBackend,
        TranscriptionComputeInterface transcriptionComputeInterface,
        string? selectedModelId,
        string? modelPath,
        bool enableOllamaPostProcessing,
        string ollamaEndpoint,
        string ollamaModel,
        OllamaMode ollamaMode,
        bool enableVoiceCommands,
        string voiceStopPhrase,
        string voiceHistoryPhrase,
        IReadOnlyList<VoiceShellCommand>? voiceShellCommands = null,
        IReadOnlyList<TranscriptReplacementRule>? transcriptReplacements = null)
    {
        lock (this.configSync)
        {
            this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
            this.selectedInputDeviceId = string.IsNullOrWhiteSpace(selectedInputDeviceId) ? null : selectedInputDeviceId;
            this.inputGainMultiplier = NormalizeInputGain(inputGainMultiplier);
            this.autoCommitSilenceDelay = NormalizeSilenceDelay(autoCommitSilenceDelay);
            this.sendEnterAfterCommit = sendEnterAfterCommit;
            this.returnToStartTargetOnCommit = returnToStartTargetOnCommit;
            this.enableOllamaPostProcessing = enableOllamaPostProcessing;
            this.ollamaEndpoint = ollamaEndpoint;
            this.ollamaModel = ollamaModel;
            this.ollamaMode = ollamaMode;
            this.enableVoiceCommands = enableVoiceCommands;
            this.voiceStopPhrase = NormalizeVoiceCommandPhrase(voiceStopPhrase);
            this.voiceHistoryPhrase = NormalizeVoiceCommandPhrase(voiceHistoryPhrase);
            this.ReplaceVoiceShellCommands(voiceShellCommands);
            this.ReplaceTranscriptReplacementRules(transcriptReplacements);
        }

        this.textInjectionPipeline.UpdateConfiguration(
            transcriptionBackend,
            transcriptionComputeInterface,
            selectedModelId,
            modelPath);
        this.recorder.UpdateInputDevice(this.selectedInputDeviceId);
        this.recorder.UpdateInputGain(this.inputGainMultiplier);
    }

    public async Task ToggleRecordingAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!this.recorder.IsRecording)
            {
                bool useExclusiveMicAccess;
                TimeSpan silenceDelay;
                lock (this.configSync)
                {
                    useExclusiveMicAccess = this.exclusiveMicAccessWhileDictating;
                    silenceDelay = this.autoCommitSilenceDelay;
                }

                var threadId = Guid.NewGuid();
                this.activeThreadId = threadId;
                this.activeInputTarget = ForegroundInputTarget.Capture();
                Interlocked.Exchange(ref this.autoCommitRequested, 0);
                Interlocked.Exchange(ref this.emergencyStopRequested, 0);
                Interlocked.Exchange(ref this.voiceStopRequested, 0);
                Interlocked.Exchange(ref this.voiceHistoryRequested, 0);
                Interlocked.Exchange(ref this.lastSpeechTicksUtc, 0);
                this.adaptiveNoiseFloorRms = MinSpeechRmsThreshold;
                this.maxObservedRmsThisSession = 0;
                this.ThreadStarted?.Invoke(threadId);
                this.recorder.Start(useExclusiveMicAccess);
                this.livePreviewCts = new CancellationTokenSource();
                var liveToken = this.livePreviewCts.Token;
                this.livePreviewTask = Task.Run(() => this.LivePreviewLoopAsync(liveToken), CancellationToken.None);
                AppLog.Info(
                    $"Recording started (live preview, auto-commit after {silenceDelay.TotalSeconds:N0}s silence, mic mode: {this.ActiveMicAccessModeLabel}).",
                    threadId);
                this.RecordingStateChanged?.Invoke(true);
                return;
            }

            await this.StopAndCommitRecordingCoreAsync("manual stop").ConfigureAwait(false);
        }
        finally
        {
            this.toggleGate.Release();
        }
    }

    public async Task StopRecordingAsync()
    {
        Interlocked.Exchange(ref this.emergencyStopRequested, 1);
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!this.recorder.IsRecording)
            {
                AppLog.Info("Emergency stop requested; no active recording to discard.", this.activeThreadId);
                return;
            }

            await this.StopAndDiscardRecordingCoreAsync("emergency stop hotkey").ConfigureAwait(false);
        }
        finally
        {
            this.toggleGate.Release();
        }
    }

    private async Task LivePreviewLoopAsync(CancellationToken cancellationToken)
    {
        long lastTranscribedCapturedBytes = 0;
        var recordingStartedUtc = DateTime.UtcNow;
        var nextTranscribeAfterUtc = DateTime.MinValue;

        while (true)
        {
            try
            {
                await Task.Delay(SilenceProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var nowUtc = DateTime.UtcNow;
            var lastSpeechUtc = this.GetLastSpeechUtc();
            var heardSpeech = lastSpeechUtc is DateTime detectedSpeechUtc &&
                detectedSpeechUtc >= recordingStartedUtc;

            if (heardSpeech &&
                nowUtc >= nextTranscribeAfterUtc)
            {
                nextTranscribeAfterUtc = nowUtc + LiveTranscribeInterval;
                if (this.recorder.TryGetPcm16KhzMonoSnapshot(
                        out var snap,
                        out var capturedBytes,
                        LivePreviewMaxAudio) &&
                    !snap.IsEmpty &&
                    snap.Duration >= LiveMinAudio &&
                    capturedBytes != lastTranscribedCapturedBytes)
                {
                    try
                    {
                        var transcript = await this.textInjectionPipeline
                            .TranscribeAsync(snap, cancellationToken, logTranscript: false)
                            .ConfigureAwait(false);
                        lastTranscribedCapturedBytes = capturedBytes;
                        var commandMatch = VoiceCommandMatcher.Apply(transcript, this.GetVoiceCommandOptionsSnapshot());
                        if (this.activeThreadId is Guid threadId &&
                            (!string.IsNullOrWhiteSpace(commandMatch.CleanedText) ||
                                commandMatch.StopRequested ||
                                commandMatch.HistoryRequested))
                        {
                            this.ThreadTranscriptUpdated?.Invoke(threadId, commandMatch.CleanedText);
                        }

                        if (commandMatch.HistoryRequested)
                        {
                            this.RequestHistoryFromVoiceCommand();
                            this.RequestStopFromVoiceCommand();
                        }

                        if (commandMatch.StopRequested)
                        {
                            this.RequestStopFromVoiceCommand();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Live preview transcription failed: {ex.Message}", this.activeThreadId);
                    }
                }
            }

            if (!heardSpeech)
            {
                continue;
            }

            TimeSpan silenceDelay;
            lock (this.configSync)
            {
                silenceDelay = this.autoCommitSilenceDelay;
            }

            if (lastSpeechUtc is DateTime speechUtc && nowUtc - speechUtc >= silenceDelay)
            {
                this.RequestCommitAfterSilence();
                continue;
            }
        }
    }

    private void RequestCommitAfterSilence()
    {
        if (Interlocked.Exchange(ref this.autoCommitRequested, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await this.CommitAfterSilenceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Silence auto-commit failed: {ex.Message}", this.activeThreadId);
            }
        });
    }

    private void RequestStopFromVoiceCommand()
    {
        if (Interlocked.Exchange(ref this.voiceStopRequested, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref this.emergencyStopRequested, 1);
        _ = Task.Run(async () =>
        {
            await this.toggleGate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (!this.recorder.IsRecording)
                {
                    return;
                }

                AppLog.Info("Voice stop command detected.", this.activeThreadId);
                await this.StopAndDiscardRecordingCoreAsync("voice stop command").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Voice stop command failed: {ex.Message}", this.activeThreadId);
            }
            finally
            {
                this.toggleGate.Release();
            }
        });
    }

    private void RequestHistoryFromVoiceCommand()
    {
        if (Interlocked.Exchange(ref this.voiceHistoryRequested, 1) == 1)
        {
            return;
        }

        AppLog.Info("Voice history command detected.", this.activeThreadId);
    }

    private async Task CommitAfterSilenceAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!this.recorder.IsRecording)
            {
                return;
            }

            var speechResumeWindow = RecentSpeechWindow + TimeSpan.FromMilliseconds(150);
            if (this.GetLastSpeechUtc() is DateTime lastSpeechUtc &&
                DateTime.UtcNow - lastSpeechUtc < speechResumeWindow)
            {
                // If speech resumed while auto-commit was queued, keep recording.
                Interlocked.Exchange(ref this.autoCommitRequested, 0);
                AppLog.Info("Auto-commit canceled because speech resumed.", this.activeThreadId);
                return;
            }

            AppLog.Info("Auto-commit triggered by silence.", this.activeThreadId);
            await this.StopAndCommitRecordingCoreAsync("silence auto-commit").ConfigureAwait(false);
        }
        finally
        {
            this.toggleGate.Release();
        }
    }

    private async Task StopAndCommitRecordingCoreAsync(string reason)
    {
        this.livePreviewCts?.Cancel();

        // Stop recording and update UI immediately so it feels responsive even if the NPU is lagging
        var audio = await this.recorder.StopAsync().ConfigureAwait(false);
        AppLog.Info(
            $"Recording stopped ({reason}): {audio.Duration.TotalSeconds:N2}s, {audio.Pcm16KhzMono.Length:N0} bytes PCM.",
            this.activeThreadId);
        this.RecordingStateChanged?.Invoke(false);
        this.ProcessingStateChanged?.Invoke(true);

        if (this.livePreviewTask is { } liveTask)
        {
            try
            {
                await liveTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"Live preview loop failed: {ex.Message}", this.activeThreadId);
            }
        }

        this.livePreviewCts?.Dispose();
        this.livePreviewCts = null;
        this.livePreviewTask = null;

        if (this.IsEmergencyStopRequested())
        {
            AppLog.Info("Final transcription skipped because emergency stop was requested.", this.activeThreadId);
            if (this.activeThreadId is Guid canceledId)
            {
                this.ThreadCompleted?.Invoke(canceledId);
            }

            this.ProcessingStateChanged?.Invoke(false);
            this.activeThreadId = null;
            this.activeInputTarget = null;
            return;
        }

        try
        {
            await this.HandleRecordedAudioAsync(audio, reason).ConfigureAwait(false);
            if (this.activeThreadId is Guid completedId)
            {
                this.ThreadCompleted?.Invoke(completedId);
            }
        }
        finally
        {
            this.ProcessingStateChanged?.Invoke(false);
            this.activeThreadId = null;
            this.activeInputTarget = null;
            if (Interlocked.Exchange(ref this.voiceHistoryRequested, 0) == 1)
            {
                this.HistoryRequested?.Invoke();
            }
        }
    }

    private async Task StopAndDiscardRecordingCoreAsync(string reason)
    {
        this.livePreviewCts?.Cancel();

        var audio = await this.recorder.StopAsync().ConfigureAwait(false);
        AppLog.Info(
            $"Recording discarded ({reason}): {audio.Duration.TotalSeconds:N2}s, {audio.Pcm16KhzMono.Length:N0} bytes PCM.",
            this.activeThreadId);
        this.RecordingStateChanged?.Invoke(false);

        if (this.livePreviewTask is { } liveTask)
        {
            try
            {
                await liveTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"Live preview loop failed during discard: {ex.Message}", this.activeThreadId);
            }
        }

        this.livePreviewCts?.Dispose();
        this.livePreviewCts = null;
        this.livePreviewTask = null;

        if (this.activeThreadId is Guid completedId)
        {
            this.ThreadCompleted?.Invoke(completedId);
        }

        this.ProcessingStateChanged?.Invoke(false);
        this.activeThreadId = null;
        this.activeInputTarget = null;
        if (Interlocked.Exchange(ref this.voiceHistoryRequested, 0) == 1)
        {
            this.HistoryRequested?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            try
            {
                if (this.recorder.IsRecording)
                {
                    this.livePreviewCts?.Cancel();
                    if (this.livePreviewTask is { } liveTask)
                    {
                        try
                        {
                            await liveTask.ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    this.livePreviewCts?.Dispose();
                    this.livePreviewCts = null;
                    this.livePreviewTask = null;
                    _ = await this.recorder.StopAsync().ConfigureAwait(false);
                    this.RecordingStateChanged?.Invoke(false);
                    this.ProcessingStateChanged?.Invoke(false);
                }

                this.recorder.AudioLevelUpdated -= this.OnRecorderAudioLevelUpdated;
                this.recorder.Dispose();
            }
            finally
            {
                await this.textInjectionPipeline.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            this.toggleGate.Release();
            this.toggleGate.Dispose();
        }
    }

    private async Task HandleRecordedAudioAsync(PcmAudioBuffer audio, string stopReason)
    {
        if (audio.IsEmpty)
        {
            AppLog.Info("No audio captured.", this.activeThreadId);
            return;
        }

        if (this.IsEmergencyStopRequested())
        {
            AppLog.Info("Recorded audio discarded before final transcription because emergency stop was requested.", this.activeThreadId);
            return;
        }

        var target = this.activeInputTarget;
        if (target is null)
        {
            AppLog.Info("No foreground target snapshot captured at dictation start; text will go to current foreground app.", this.activeThreadId);
        }
        else
        {
            AppLog.Info($"Captured target: {target.DisplayName} (pid {target.ProcessId}).", this.activeThreadId);
        }

        AppLog.Info($"Runtime transcription config: {this.textInjectionPipeline.ConfigurationSummary}", this.activeThreadId);
        string? finalTranscript = null;
        string? originalTranscript = null;
        string? ollamaSystemPrompt = null;

        try
        {
            finalTranscript = await this.textInjectionPipeline.TranscribeAsync(audio, CancellationToken.None)
                .ConfigureAwait(false);
            finalTranscript = RemoveTrailingSilenceArtifact(finalTranscript, stopReason);
            var finalCommandMatch = VoiceCommandMatcher.Apply(
                finalTranscript,
                this.GetVoiceCommandOptionsSnapshot(),
                includeShellCommands: true);
            if (finalCommandMatch.ShellCommandInvocation is { } shellCommandInvocation)
            {
                await this.HandleVoiceShellCommandAsync(shellCommandInvocation, audio.Duration).ConfigureAwait(false);
                if (shellCommandInvocation.Command.CompletionBehavior == VoiceShellCommandCompletionBehavior.Stop ||
                    string.IsNullOrWhiteSpace(finalCommandMatch.CleanedText))
                {
                    return;
                }
            }

            finalTranscript = finalCommandMatch.CleanedText;
            if (finalCommandMatch.StopRequested)
            {
                Interlocked.Exchange(ref this.emergencyStopRequested, 1);
                AppLog.Info("Voice stop command detected in final transcript; skipped injection.", this.activeThreadId);
                return;
            }

            if (finalCommandMatch.HistoryRequested)
            {
                this.RequestHistoryFromVoiceCommand();
            }

            if (this.IsEmergencyStopRequested())
            {
                AppLog.Info("Final transcript discarded because emergency stop was requested.", this.activeThreadId);
                return;
            }

            TranscriptReplacementRule[] replacementSnapshot;
            lock (this.configSync)
            {
                replacementSnapshot = this.transcriptReplacements.Count == 0
                    ? Array.Empty<TranscriptReplacementRule>()
                    : this.transcriptReplacements.ToArray();
            }

            finalTranscript = TranscriptReplacement.Apply(finalTranscript, replacementSnapshot);
            if (this.IsEmergencyStopRequested())
            {
                AppLog.Info("Final transcript discarded before preview update because emergency stop was requested.", this.activeThreadId);
                return;
            }

            if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(finalTranscript))
            {
                this.ThreadTranscriptUpdated?.Invoke(threadId, finalTranscript);
            }

            if (string.IsNullOrWhiteSpace(finalTranscript))
            {
                AppLog.Info(
                    $"No transcript text produced. Peak mic RMS this session: {this.maxObservedRmsThisSession:0.0000}. " +
                    "Try increasing Input Gain or selecting a different model/backend.",
                    this.activeThreadId);
                return;
            }

            bool enableOllama;
            string ollamaUrl;
            string ollamaMod;
            OllamaMode ollamaModeSetting;
            lock (this.configSync)
            {
                enableOllama = this.enableOllamaPostProcessing;
                ollamaUrl = this.ollamaEndpoint;
                ollamaMod = this.ollamaModel;
                ollamaModeSetting = this.ollamaMode;
            }

            if (enableOllama)
            {
                originalTranscript = finalTranscript;
                if (this.activeThreadId is Guid id)
                {
                    this.ThreadTranscriptUpdated?.Invoke(id, "[AI is processing transcript...]");
                }
                
                var ollamaResult = await OllamaPostProcessor.ProcessTranscriptAsync(
                    finalTranscript,
                    ollamaUrl,
                    ollamaMod,
                    ollamaModeSetting,
                    target,
                    CancellationToken.None).ConfigureAwait(false);

                finalTranscript = ollamaResult.ProcessedText;
                ollamaSystemPrompt = ollamaResult.SystemPrompt;

                if (this.IsEmergencyStopRequested())
                {
                    AppLog.Info("Processed transcript discarded because emergency stop was requested.", this.activeThreadId);
                    return;
                }

                if (this.activeThreadId is Guid updatedId && !string.IsNullOrWhiteSpace(finalTranscript))
                {
                    this.ThreadTranscriptUpdated?.Invoke(updatedId, finalTranscript);
                }
            }

            bool shouldSendEnter;
            bool shouldReturnToStartTarget;
            lock (this.configSync)
            {
                shouldSendEnter = this.sendEnterAfterCommit;
                shouldReturnToStartTarget = this.returnToStartTargetOnCommit;
            }

            if (this.IsEmergencyStopRequested())
            {
                AppLog.Info("Transcript injection skipped because emergency stop was requested.", this.activeThreadId);
                return;
            }

            if (target is not null && !target.IsStillForeground())
            {
                if (!shouldReturnToStartTarget)
                {
                    AppLog.Error(
                        $"Focused window changed before transcript typing; skipped injection for {target.DisplayName}.",
                        this.activeThreadId);
                    this.PublishTranscriptCommit(
                        finalTranscript,
                        audio.Duration,
                        TranscriptDeliveryStatus.SkippedFocusChanged,
                        target.DisplayName,
                        "Focused window changed before transcript typing.",
                        sendEnterAfterCommit: false,
                        originalTranscript: originalTranscript,
                        ollamaSystemPrompt: ollamaSystemPrompt,
                        targetAppName: target.ProcessName,
                        targetWindowTitle: target.Title);
                    return;
                }

                if (!shouldSendEnter && target.TryInjectTextDirectly(finalTranscript))
                {
                    AppLog.Info(
                        $"Transcript inserted into the original target without reactivating {target.DisplayName}.",
                        this.activeThreadId);
                    this.PublishTranscriptCommit(
                        finalTranscript,
                        audio.Duration,
                        TranscriptDeliveryStatus.Injected,
                        target.DisplayName,
                        error: null,
                        sendEnterAfterCommit: false,
                        originalTranscript: originalTranscript,
                        ollamaSystemPrompt: ollamaSystemPrompt,
                        targetAppName: target.ProcessName,
                        targetWindowTitle: target.Title);
                    return;
                }

                if (!target.TryRestoreForInput())
                {
                    AppLog.Error(
                        $"Focused window changed before transcript typing; could not restore original target {target.DisplayName}.",
                        this.activeThreadId);
                    this.PublishTranscriptCommit(
                        finalTranscript,
                        audio.Duration,
                        TranscriptDeliveryStatus.SkippedFocusChanged,
                        target.DisplayName,
                        "Focused window changed and PrimeDictate could not restore the original target.",
                        sendEnterAfterCommit: false,
                        originalTranscript: originalTranscript,
                        ollamaSystemPrompt: ollamaSystemPrompt,
                        targetAppName: target.ProcessName,
                        targetWindowTitle: target.Title);
                    return;
                }

                AppLog.Info(
                    $"Focused window changed; restored original target {target.DisplayName} for transcript typing.",
                    this.activeThreadId);
            }

            this.textInjectionPipeline.InjectTextToTarget(finalTranscript);
            AppLog.Info($"Transcript typed into target ({finalTranscript.Length:N0} chars).", this.activeThreadId);

            if (shouldSendEnter)
            {
                this.textInjectionPipeline.SendEnterToTarget();
                AppLog.Info("Enter key sent after transcript commit.", this.activeThreadId);
            }

            this.PublishTranscriptCommit(
                finalTranscript,
                audio.Duration,
                TranscriptDeliveryStatus.Injected,
                target?.DisplayName,
                error: null,
                sendEnterAfterCommit: shouldSendEnter,
                originalTranscript: originalTranscript,
                ollamaSystemPrompt: ollamaSystemPrompt,
                targetAppName: target?.ProcessName,
                targetWindowTitle: target?.Title);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Transcription or text injection failed: {ex.GetType().Name}: {ex.Message}", this.activeThreadId);
            AppLog.Error(ex.ToString(), this.activeThreadId);
            if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(finalTranscript))
            {
                this.TranscriptCommitted?.Invoke(new TranscriptCommittedEvent(
                    ThreadId: threadId,
                    TimestampUtc: DateTime.UtcNow,
                    Transcript: finalTranscript ?? string.Empty,
                    DeliveryStatus: TranscriptDeliveryStatus.FailedToInject,
                    TargetDisplayName: target?.DisplayName,
                    TargetAppName: target?.ProcessName,
                    TargetWindowTitle: target?.Title,
                    Error: ex.Message,
                    AudioDuration: audio.Duration,
                    SendEnterAfterCommit: false,
                    OriginalTranscript: originalTranscript,
                    OllamaSystemPrompt: ollamaSystemPrompt));
            }
        }
    }

    private async Task HandleVoiceShellCommandAsync(
        VoiceShellCommandInvocation invocation,
        TimeSpan audioDuration)
    {
        var shellCommand = invocation.Command;
        var phrase = shellCommand.Phrase.Trim();
        var transcript = string.IsNullOrWhiteSpace(invocation.TextToType)
            ? $"Voice command: {phrase}"
            : $"Voice command: {phrase}; typed: {invocation.TextToType}";
        if (this.activeThreadId is Guid threadId)
        {
            this.ThreadTranscriptUpdated?.Invoke(threadId, transcript);
        }

        try
        {
            var result = VoiceShellCommandRunner.Run(shellCommand);
            AppLog.Info(
                $"Voice command ran: \"{phrase}\" -> {shellCommand.Command.Trim()} (pid {result.ProcessId?.ToString() ?? "unknown"}).",
                this.activeThreadId);
            if (!string.IsNullOrWhiteSpace(invocation.TextToType))
            {
                if (!await this.WaitForVoiceShellTypeTargetAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Chained typing skipped because the command did not move focus away from the starting window.");
                }

                this.textInjectionPipeline.InjectTextToTarget(invocation.TextToType);
                AppLog.Info($"Voice command typed chained text ({invocation.TextToType.Length:N0} chars).", this.activeThreadId);
            }

            this.PublishTranscriptCommit(
                transcript,
                audioDuration,
                TranscriptDeliveryStatus.CommandExecuted,
                "Command Prompt",
                error: null,
                sendEnterAfterCommit: false,
                targetAppName: "Command Prompt",
                targetWindowTitle: "Command Prompt");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Voice command failed: \"{phrase}\" -> {ex.Message}", this.activeThreadId);
            this.PublishTranscriptCommit(
                transcript,
                audioDuration,
                TranscriptDeliveryStatus.CommandFailed,
                "Command Prompt",
                ex.Message,
                sendEnterAfterCommit: false,
                targetAppName: "Command Prompt",
                targetWindowTitle: "Command Prompt");
        }
    }

    private async Task<bool> WaitForVoiceShellTypeTargetAsync()
    {
        var startTarget = this.activeInputTarget;
        if (startTarget is null)
        {
            await Task.Delay(VoiceShellTypePollInterval).ConfigureAwait(false);
            return true;
        }

        var deadlineUtc = DateTime.UtcNow + VoiceShellTypeTargetWait;
        while (DateTime.UtcNow < deadlineUtc)
        {
            await Task.Delay(VoiceShellTypePollInterval).ConfigureAwait(false);
            if (!startTarget.IsStillForeground())
            {
                return true;
            }
        }

        return false;
    }

    private void PublishTranscriptCommit(
        string transcript,
        TimeSpan audioDuration,
        TranscriptDeliveryStatus status,
        string? targetDisplayName,
        string? error,
        bool sendEnterAfterCommit,
        string? originalTranscript = null,
        string? ollamaSystemPrompt = null,
        string? targetAppName = null,
        string? targetWindowTitle = null)
    {
        if (this.activeThreadId is not Guid threadId)
        {
            return;
        }

        this.TranscriptCommitted?.Invoke(new TranscriptCommittedEvent(
            ThreadId: threadId,
            TimestampUtc: DateTime.UtcNow,
            Transcript: transcript,
            DeliveryStatus: status,
            TargetDisplayName: targetDisplayName,
            TargetAppName: targetAppName,
            TargetWindowTitle: targetWindowTitle,
            Error: error,
            AudioDuration: audioDuration,
            SendEnterAfterCommit: sendEnterAfterCommit,
            OriginalTranscript: originalTranscript,
            OllamaSystemPrompt: ollamaSystemPrompt));
    }

    private void ReplaceTranscriptReplacementRules(IReadOnlyList<TranscriptReplacementRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            this.transcriptReplacements = new List<TranscriptReplacementRule>();
            return;
        }

        this.transcriptReplacements = rules
            .Select(r => new TranscriptReplacementRule { Find = r.Find, Replace = r.Replace })
            .ToList();
    }

    private void ReplaceVoiceShellCommands(IReadOnlyList<VoiceShellCommand>? commands)
    {
        if (commands is null || commands.Count == 0)
        {
            this.voiceShellCommands = new List<VoiceShellCommand>();
            return;
        }

        this.voiceShellCommands = commands
            .Where(command =>
                !string.IsNullOrWhiteSpace(command.Phrase) &&
                !string.IsNullOrWhiteSpace(command.Command))
            .Select(command => new VoiceShellCommand
            {
                Enabled = command.Enabled,
                Phrase = command.Phrase.Trim(),
                CompletionBehavior = command.CompletionBehavior,
                Command = command.Command.Trim()
            })
            .ToList();
    }

    private VoiceCommandOptions GetVoiceCommandOptionsSnapshot()
    {
        lock (this.configSync)
        {
            return new VoiceCommandOptions(
                this.enableVoiceCommands,
                this.voiceStopPhrase,
                this.voiceHistoryPhrase,
                this.voiceShellCommands
                    .Select(command => new VoiceShellCommand
                    {
                        Enabled = command.Enabled,
                        Phrase = command.Phrase,
                        CompletionBehavior = command.CompletionBehavior,
                        Command = command.Command
                    })
                    .ToList());
        }
    }

    private bool IsEmergencyStopRequested() =>
        Interlocked.CompareExchange(ref this.emergencyStopRequested, 0, 0) == 1;

    private static string NormalizeVoiceCommandPhrase(string? phrase) =>
        string.IsNullOrWhiteSpace(phrase)
            ? string.Empty
            : phrase.Trim();

    private void OnRecorderAudioLevelUpdated(double rms)
    {
        if (rms > this.maxObservedRmsThisSession)
        {
            this.maxObservedRmsThisSession = rms;
        }

        // Track a soft ambient noise floor and derive a dynamic speech threshold.
        this.adaptiveNoiseFloorRms =
            (this.adaptiveNoiseFloorRms * (1 - NoiseFloorSmoothing)) +
            (Math.Max(0, rms) * NoiseFloorSmoothing);
        var dynamicThreshold = Math.Clamp(this.adaptiveNoiseFloorRms * 3.5, MinSpeechRmsThreshold, MaxSpeechRmsThreshold);

        if (rms >= dynamicThreshold)
        {
            Interlocked.Exchange(ref this.lastSpeechTicksUtc, DateTime.UtcNow.Ticks);
        }

        this.AudioLevelUpdated?.Invoke(rms);
    }

    private DateTime? GetLastSpeechUtc()
    {
        var ticks = Interlocked.Read(ref this.lastSpeechTicksUtc);
        return ticks > 0
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
    }

    private static TimeSpan NormalizeSilenceDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        if (delay > TimeSpan.FromSeconds(30))
        {
            return TimeSpan.FromSeconds(30);
        }

        return delay;
    }

    private static double NormalizeInputGain(double gain)
    {
        if (double.IsNaN(gain) || double.IsInfinity(gain))
        {
            return 1.0;
        }

        return Math.Clamp(gain, 0.5, 4.0);
    }

    private static string RemoveTrailingSilenceArtifact(string transcript, string stopReason)
    {
        if (!stopReason.Contains("silence", StringComparison.OrdinalIgnoreCase))
        {
            return transcript;
        }

        var text = transcript.TrimEnd();
        if (text.Length == 0)
        {
            return text;
        }

        if (!EndsWithTrailingToken(text, "ok") && !EndsWithTrailingToken(text, "okay"))
        {
            return text;
        }

        var splitIndex = text.LastIndexOf(' ');
        if (splitIndex <= 0)
        {
            return text;
        }

        var withoutToken = text[..splitIndex].TrimEnd(' ', '\t', ',', '.', ';', ':', '!', '?', '-', '—');
        if (withoutToken.Length == 0)
        {
            return text;
        }

        AppLog.Info("Removed trailing silence artifact token from transcript.");
        return withoutToken;
    }

    private static bool EndsWithTrailingToken(string text, string token)
    {
        if (text.Length <= token.Length)
        {
            return string.Equals(text, token, StringComparison.OrdinalIgnoreCase);
        }

        var tokenStart = text.Length - token.Length;
        if (!text.AsSpan(tokenStart).Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = text[tokenStart - 1];
        return char.IsWhiteSpace(boundary) || char.IsPunctuation(boundary);
    }

}

internal sealed class DefaultMicrophoneRecorder : IDisposable
{
    private const int TargetSampleRate = 16_000;
    private const int TargetBitsPerSample = 16;
    private const int TargetChannels = 1;

    private readonly object syncRoot = new();

    private WasapiCapture? capture;
    private MemoryStream? captureBuffer;
    private WaveFormat? captureFormat;
    private TaskCompletionSource<Exception?>? stoppedSignal;
    private AudioClientShareMode? activeShareMode;
    private string? selectedInputDeviceId;
    private double inputGainMultiplier = 1.0;

    public bool IsRecording
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.capture is not null;
            }
        }
    }

    public AudioClientShareMode? ActiveShareMode
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.activeShareMode;
            }
        }
    }

    public event Action<double>? AudioLevelUpdated;

    public void Start(bool exclusiveMode)
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                throw new InvalidOperationException("Recording is already in progress.");
            }

            try
            {
                this.InitializeAndStartCapture(exclusiveMode);
            }
            catch (Exception ex) when (exclusiveMode)
            {
                AppLog.Info($"Exclusive microphone mode failed ({ex.Message}). Falling back to shared mode.");
                this.InitializeAndStartCapture(exclusiveMode: false);
            }
        }
    }

    public void UpdateInputDevice(string? deviceId)
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                throw new InvalidOperationException("Cannot change microphone while recording is in progress.");
            }

            this.selectedInputDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }
    }

    public void UpdateInputGain(double gainMultiplier)
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                throw new InvalidOperationException("Cannot change input gain while recording is in progress.");
            }

            if (double.IsNaN(gainMultiplier) || double.IsInfinity(gainMultiplier))
            {
                this.inputGainMultiplier = 1.0;
                return;
            }

            this.inputGainMultiplier = Math.Clamp(gainMultiplier, 0.5, 4.0);
        }
    }

    /// <summary>
    /// Copies captured audio so far, resampled to 16 kHz mono. Returns false if not recording.
    /// </summary>
    public bool TryGetPcm16KhzMonoSnapshot(
        [NotNullWhen(true)] out PcmAudioBuffer? snapshot,
        out long capturedBytes,
        TimeSpan? maxDuration = null)
    {
        byte[] rawAudio;
        WaveFormat rawFormat;

        lock (this.syncRoot)
        {
            if (this.capture is null || this.captureBuffer is null || this.captureFormat is null)
            {
                snapshot = null;
                capturedBytes = 0;
                return false;
            }

            capturedBytes = this.captureBuffer.Length;
            rawAudio = CopyCapturedAudio(this.captureBuffer, this.captureFormat, maxDuration);
            rawFormat = this.captureFormat;
        }

        var pcm16KhzMono = ConvertToPcm16KhzMono(rawAudio, rawFormat);
        snapshot = new PcmAudioBuffer(pcm16KhzMono, TargetSampleRate, TargetBitsPerSample, TargetChannels);
        return true;
    }

    public async Task<PcmAudioBuffer> StopAsync()
    {
        WasapiCapture activeCapture;
        TaskCompletionSource<Exception?> activeStoppedSignal;

        lock (this.syncRoot)
        {
            activeCapture = this.capture ?? throw new InvalidOperationException("Recording is not in progress.");
            activeStoppedSignal = this.stoppedSignal ?? throw new InvalidOperationException("Recorder state is invalid.");
        }

        activeCapture.StopRecording();

        var stopException = await activeStoppedSignal.Task.ConfigureAwait(false);
        if (stopException is not null)
        {
            throw new InvalidOperationException("Microphone capture failed.", stopException);
        }

        MemoryStream rawAudioStream;
        long rawAudioLength;
        WaveFormat rawFormat;

        lock (this.syncRoot)
        {
            rawAudioStream = this.captureBuffer ?? new MemoryStream();
            rawAudioLength = rawAudioStream.Length;
            rawFormat = this.captureFormat ?? throw new InvalidOperationException("Capture format is unavailable.");

            this.captureBuffer = null;
            this.ResetCaptureState(activeCapture);
        }

        using (rawAudioStream)
        {
            rawAudioStream.Position = 0;
            var pcm16KhzMono = ConvertToPcm16KhzMono(rawAudioStream, rawAudioLength, rawFormat);
            return new PcmAudioBuffer(pcm16KhzMono, TargetSampleRate, TargetBitsPerSample, TargetChannels);
        }
    }

    public void Dispose()
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                this.ResetCaptureState(this.capture);
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        double? rmsToPublish = null;
        byte[]? gainBuffer = null;
        byte[] sourceBuffer = args.Buffer;
        int bytesRecorded = args.BytesRecorded;

        lock (this.syncRoot)
        {
            if (this.captureFormat is not null && this.inputGainMultiplier != 1.0)
            {
                gainBuffer = ArrayPool<byte>.Shared.Rent(bytesRecorded);
                Buffer.BlockCopy(args.Buffer, 0, gainBuffer, 0, bytesRecorded);
                ApplyGainInPlace(gainBuffer.AsSpan(0, bytesRecorded), this.captureFormat, this.inputGainMultiplier);
                sourceBuffer = gainBuffer;
            }

            this.captureBuffer?.Write(sourceBuffer, 0, bytesRecorded);

            if (this.captureFormat is not null && this.AudioLevelUpdated is not null)
            {
                double rms = 0;
                var bytes = sourceBuffer;
                var recorded = bytesRecorded;

                if (this.captureFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    var floatCount = recorded / 4;
                    double sumSquares = 0;
                    for (var i = 0; i < floatCount; i++)
                    {
                        var sample = BitConverter.ToSingle(bytes, i * 4);
                        sumSquares += sample * sample;
                    }
                    rms = floatCount > 0 ? Math.Sqrt(sumSquares / floatCount) : 0;
                }
                else if (this.captureFormat.Encoding == WaveFormatEncoding.Pcm && this.captureFormat.BitsPerSample == 16)
                {
                    var sampleCount = recorded / 2;
                    double sumSquares = 0;
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var sample = BitConverter.ToInt16(bytes, i * 2);
                        var normalized = sample / 32768.0;
                        sumSquares += normalized * normalized;
                    }
                    rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
                }

                if (rms > 0)
                {
                    rmsToPublish = rms;
                }
            }
        }

        if (rmsToPublish is double level)
        {
            this.AudioLevelUpdated?.Invoke(level);
        }

        if (gainBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(gainBuffer);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        TaskCompletionSource<Exception?>? signal;

        lock (this.syncRoot)
        {
            signal = this.stoppedSignal;
        }

        signal?.TrySetResult(args.Exception);
    }

    private void ResetCaptureState(WasapiCapture captureToDispose)
    {
        captureToDispose.DataAvailable -= this.OnDataAvailable;
        captureToDispose.RecordingStopped -= this.OnRecordingStopped;
        captureToDispose.Dispose();

        this.captureBuffer?.Dispose();
        this.captureBuffer = null;
        this.captureFormat = null;
        this.capture = null;
        this.stoppedSignal = null;
        this.activeShareMode = null;
    }

    private void InitializeAndStartCapture(bool exclusiveMode)
    {
        WasapiCapture newCapture;
        var shareMode = exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        if (string.IsNullOrWhiteSpace(this.selectedInputDeviceId))
        {
            newCapture = new WasapiCapture
            {
                ShareMode = shareMode
            };
        }
        else
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(this.selectedInputDeviceId);
                newCapture = new WasapiCapture(device)
                {
                    ShareMode = shareMode
                };
                AppLog.Info($"Using selected microphone: {device.FriendlyName}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Selected microphone unavailable ({ex.Message}). Falling back to system default.");
                this.selectedInputDeviceId = null;
                newCapture = new WasapiCapture
                {
                    ShareMode = shareMode
                };
            }
        }

        this.capture = newCapture;
        this.captureFormat = newCapture.WaveFormat;
        this.captureBuffer = new MemoryStream();
        this.stoppedSignal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.activeShareMode = newCapture.ShareMode;

        newCapture.DataAvailable += this.OnDataAvailable;
        newCapture.RecordingStopped += this.OnRecordingStopped;

        try
        {
            newCapture.StartRecording();
        }
        catch
        {
            this.ResetCaptureState(newCapture);
            throw;
        }
    }

    private static byte[] CopyCapturedAudio(MemoryStream captureBuffer, WaveFormat rawFormat, TimeSpan? maxDuration)
    {
        var length = captureBuffer.Length;
        if (length <= 0)
        {
            return [];
        }

        var bytesToCopy = length;
        if (maxDuration is { } duration && duration > TimeSpan.Zero && rawFormat.AverageBytesPerSecond > 0)
        {
            bytesToCopy = Math.Min(
                length,
                Math.Max(rawFormat.BlockAlign, (long)(rawFormat.AverageBytesPerSecond * duration.TotalSeconds)));

            var blockAlign = Math.Max(1, rawFormat.BlockAlign);
            bytesToCopy -= bytesToCopy % blockAlign;
            if (bytesToCopy <= 0)
            {
                bytesToCopy = Math.Min(length, blockAlign);
            }
        }

        var copyLength = checked((int)bytesToCopy);
        var start = checked((int)(length - bytesToCopy));
        var copy = new byte[copyLength];
        Buffer.BlockCopy(captureBuffer.GetBuffer(), start, copy, 0, copyLength);
        return copy;
    }

    private static byte[] ConvertToPcm16KhzMono(byte[] rawAudio, WaveFormat rawFormat)
    {
        using var rawStream = new MemoryStream(rawAudio, writable: false);
        return ConvertToPcm16KhzMono(rawStream, rawAudio.Length, rawFormat, rawAudio);
    }

    private static byte[] ConvertToPcm16KhzMono(Stream rawStream, long rawLength, WaveFormat rawFormat) =>
        ConvertToPcm16KhzMono(rawStream, rawLength, rawFormat, existingTargetPcm: null);

    private static byte[] ConvertToPcm16KhzMono(
        Stream rawStream,
        long rawLength,
        WaveFormat rawFormat,
        byte[]? existingTargetPcm)
    {
        if (rawLength == 0)
        {
            return [];
        }

        if (rawFormat.Encoding == WaveFormatEncoding.Pcm &&
            rawFormat.SampleRate == TargetSampleRate &&
            rawFormat.BitsPerSample == TargetBitsPerSample &&
            rawFormat.Channels == TargetChannels)
        {
            if (existingTargetPcm is not null)
            {
                return existingTargetPcm;
            }

            using var targetCopy = new MemoryStream(EstimateTargetPcmLength(rawLength, rawFormat));
            rawStream.CopyTo(targetCopy);
            return targetCopy.ToArray();
        }

        using var waveStream = new RawSourceWaveStream(rawStream, rawFormat);
        using var resampler = new MediaFoundationResampler(
            waveStream,
            new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels))
        {
            ResamplerQuality = 60
        };
        using var convertedStream = new MemoryStream(EstimateTargetPcmLength(rawLength, rawFormat));

        var buffer = ArrayPool<byte>.Shared.Rent(TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8));
        try
        {
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                convertedStream.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return convertedStream.ToArray();
    }

    private static int EstimateTargetPcmLength(long rawLength, WaveFormat rawFormat)
    {
        if (rawLength <= 0 || rawFormat.AverageBytesPerSecond <= 0)
        {
            return 0;
        }

        var seconds = (double)rawLength / rawFormat.AverageBytesPerSecond;
        var targetBytes = seconds * TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8);
        return targetBytes >= int.MaxValue ? 0 : Math.Max(0, (int)Math.Ceiling(targetBytes));
    }

    private static void ApplyGainInPlace(Span<byte> buffer, WaveFormat format, double gainMultiplier)
    {
        if (gainMultiplier == 1.0 || buffer.IsEmpty)
        {
            return;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sampleBytes = buffer[..(buffer.Length & ~1)];
            var samples = MemoryMarshal.Cast<byte, short>(sampleBytes);
            for (var i = 0; i < samples.Length; i++)
            {
                var amplified = samples[i] * gainMultiplier;
                samples[i] = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
            }
        }
        else if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var sampleBytes = buffer[..(buffer.Length & ~3)];
            var samples = MemoryMarshal.Cast<byte, float>(sampleBytes);
            for (var i = 0; i < samples.Length; i++)
            {
                var amplified = samples[i] * gainMultiplier;
                samples[i] = (float)Math.Clamp(amplified, -1.0, 1.0);
            }
        }
    }
}

internal sealed record PcmAudioBuffer(
    byte[] Pcm16KhzMono,
    int SampleRate,
    int BitsPerSample,
    int Channels)
{
    public bool IsEmpty => this.Pcm16KhzMono.Length == 0;

    public TimeSpan Duration
    {
        get
        {
            var bytesPerSecond = this.SampleRate * this.Channels * (this.BitsPerSample / 8);
            return bytesPerSecond == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)this.Pcm16KhzMono.Length / bytesPerSecond);
        }
    }
}
