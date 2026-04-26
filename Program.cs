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
    private readonly Func<Task> onHotkeyPressedAsync;
    private readonly object configSync = new();
    private HotkeyGesture hotkey;

    public GlobalHotkeyListener(Func<Task> onHotkeyPressedAsync, HotkeyGesture hotkey)
    {
        this.onHotkeyPressedAsync = onHotkeyPressedAsync;
        this.hotkey = hotkey;
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
        if (!this.IsDictationHotkey(args))
        {
            return;
        }

        args.SuppressEvent = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await this.onHotkeyPressedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Hotkey action failed: {ex.Message}");
            }
        });
    }

    public void UpdateHotkey(HotkeyGesture hotkey)
    {
        lock (this.configSync)
        {
            this.hotkey = hotkey;
        }
    }

    private bool IsDictationHotkey(KeyboardHookEventArgs args)
    {
        var mask = args.RawEvent.Mask;
        HotkeyGesture currentHotkey;
        lock (this.configSync)
        {
            currentHotkey = this.hotkey;
        }

        return args.Data.KeyCode == currentHotkey.KeyCode &&
            (!currentHotkey.Ctrl || mask.HasCtrl()) &&
            (!currentHotkey.Shift || mask.HasShift()) &&
            (!currentHotkey.Alt || mask.HasAlt());
    }
}

internal sealed class DictationController : IAsyncDisposable
{
    private static readonly TimeSpan LiveTranscribeInterval = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan LiveMinAudio = TimeSpan.FromSeconds(0.55);
    private static readonly TimeSpan LivePreviewMaxAudio = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SilenceProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecentSpeechWindow = TimeSpan.FromMilliseconds(450);
    private const double SpeechRmsThreshold = 0.01;

    private readonly SemaphoreSlim toggleGate = new(initialCount: 1, maxCount: 1);
    private readonly DefaultMicrophoneRecorder recorder = new();
    private readonly WhisperTextInjectionPipeline textInjectionPipeline = new();
    private readonly object configSync = new();
    private bool exclusiveMicAccessWhileDictating;
    private TimeSpan autoCommitSilenceDelay;
    private bool sendEnterAfterCommit;

    private CancellationTokenSource? livePreviewCts;
    private Task? livePreviewTask;
    private Guid? activeThreadId;
    private ForegroundInputTarget? activeInputTarget;
    private int autoCommitRequested;

    public event Action<bool>? RecordingStateChanged;
    public event Action<bool>? ProcessingStateChanged;
    public event Action<Guid>? ThreadStarted;
    public event Action<Guid>? ThreadCompleted;
    public event Action<Guid, string>? ThreadTranscriptUpdated;
    public event Action<TranscriptCommittedEvent>? TranscriptCommitted;
    public event Action<double>? AudioLevelUpdated;

    public DictationController(
        bool exclusiveMicAccessWhileDictating = false,
        TimeSpan? autoCommitSilenceDelay = null,
        bool sendEnterAfterCommit = false)
    {
        this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
        this.autoCommitSilenceDelay = NormalizeSilenceDelay(autoCommitSilenceDelay ?? TimeSpan.FromSeconds(3));
        this.sendEnterAfterCommit = sendEnterAfterCommit;
        this.recorder.AudioLevelUpdated += rms => this.AudioLevelUpdated?.Invoke(rms);
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
        TimeSpan autoCommitSilenceDelay,
        bool sendEnterAfterCommit)
    {
        lock (this.configSync)
        {
            this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
            this.autoCommitSilenceDelay = NormalizeSilenceDelay(autoCommitSilenceDelay);
            this.sendEnterAfterCommit = sendEnterAfterCommit;
        }
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

    private async Task LivePreviewLoopAsync(CancellationToken cancellationToken)
    {
        long lastTranscribedCapturedBytes = 0;
        var lastSpeechUtc = DateTime.UtcNow;
        var heardSpeech = false;
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

            if (!this.recorder.TryGetPcm16KhzMonoSnapshot(
                    out var snap,
                    out var capturedBytes,
                    LivePreviewMaxAudio) ||
                snap.IsEmpty)
            {
                continue;
            }

            var nowUtc = DateTime.UtcNow;
            if (snap.Duration >= LiveMinAudio && HasRecentSpeech(snap))
            {
                heardSpeech = true;
                lastSpeechUtc = nowUtc;
            }

            if (heardSpeech &&
                snap.Duration >= LiveMinAudio &&
                capturedBytes != lastTranscribedCapturedBytes &&
                nowUtc >= nextTranscribeAfterUtc)
            {
                nextTranscribeAfterUtc = nowUtc + LiveTranscribeInterval;
                try
                {
                    var transcript = await this.textInjectionPipeline
                        .TranscribeAsync(snap, cancellationToken, logTranscript: false)
                        .ConfigureAwait(false);
                    lastTranscribedCapturedBytes = capturedBytes;
                    if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(transcript))
                    {
                        this.ThreadTranscriptUpdated?.Invoke(threadId, transcript);
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

            if (!heardSpeech)
            {
                continue;
            }

            TimeSpan silenceDelay;
            lock (this.configSync)
            {
                silenceDelay = this.autoCommitSilenceDelay;
            }

            if (DateTime.UtcNow - lastSpeechUtc >= silenceDelay)
            {
                this.RequestCommitAfterSilence();
                break;
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

    private async Task CommitAfterSilenceAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!this.recorder.IsRecording)
            {
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

        var audio = await this.recorder.StopAsync().ConfigureAwait(false);
        AppLog.Info(
            $"Recording stopped ({reason}): {audio.Duration.TotalSeconds:N2}s, {audio.Pcm16KhzMono.Length:N0} bytes PCM.",
            this.activeThreadId);
        this.RecordingStateChanged?.Invoke(false);
        this.ProcessingStateChanged?.Invoke(true);

        try
        {
            await this.HandleRecordedAudioAsync(audio).ConfigureAwait(false);
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

    private async Task HandleRecordedAudioAsync(PcmAudioBuffer audio)
    {
        if (audio.IsEmpty)
        {
            AppLog.Info("No audio captured.", this.activeThreadId);
            return;
        }

        var target = this.activeInputTarget;
        string? finalTranscript = null;

        try
        {
            finalTranscript = await this.textInjectionPipeline.TranscribeAsync(audio, CancellationToken.None)
                .ConfigureAwait(false);
            if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(finalTranscript))
            {
                this.ThreadTranscriptUpdated?.Invoke(threadId, finalTranscript);
            }

            if (string.IsNullOrWhiteSpace(finalTranscript))
            {
                return;
            }

            if (target is not null && !target.IsStillForeground())
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
                    sendEnterAfterCommit: false);
                return;
            }

            this.textInjectionPipeline.InjectTextToTarget(finalTranscript);
            AppLog.Info("Transcript typed into target.", this.activeThreadId);

            bool shouldSendEnter;
            lock (this.configSync)
            {
                shouldSendEnter = this.sendEnterAfterCommit;
            }

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
                sendEnterAfterCommit: shouldSendEnter);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Transcription or text injection failed: {ex.Message}", this.activeThreadId);
            if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(finalTranscript))
            {
                this.TranscriptCommitted?.Invoke(new TranscriptCommittedEvent(
                    ThreadId: threadId,
                    TimestampUtc: DateTime.UtcNow,
                    Transcript: finalTranscript ?? string.Empty,
                    DeliveryStatus: TranscriptDeliveryStatus.FailedToInject,
                    TargetDisplayName: target?.DisplayName,
                    Error: ex.Message,
                    AudioDuration: audio.Duration,
                    SendEnterAfterCommit: false));
            }
        }
    }

    private void PublishTranscriptCommit(
        string transcript,
        TimeSpan audioDuration,
        TranscriptDeliveryStatus status,
        string? targetDisplayName,
        string? error,
        bool sendEnterAfterCommit)
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
            Error: error,
            AudioDuration: audioDuration,
            SendEnterAfterCommit: sendEnterAfterCommit));
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

    private static bool HasRecentSpeech(PcmAudioBuffer audio)
    {
        if (audio.BitsPerSample != 16 || audio.Channels != 1 || audio.Pcm16KhzMono.Length < 2)
        {
            return true;
        }

        var sampleBytes = audio.Pcm16KhzMono.AsSpan(0, audio.Pcm16KhzMono.Length & ~1);
        var samples = MemoryMarshal.Cast<byte, short>(sampleBytes);
        var totalSamples = samples.Length;
        var samplesToInspect = Math.Min(
            totalSamples,
            Math.Max(1, (int)(audio.SampleRate * RecentSpeechWindow.TotalSeconds)));
        var startSample = totalSamples - samplesToInspect;
        long sumSquares = 0;

        for (var sample = startSample; sample < totalSamples; sample++)
        {
            var value = samples[sample];
            sumSquares += (long)value * value;
        }

        var rms = Math.Sqrt((double)sumSquares / samplesToInspect) / short.MaxValue;
        return rms >= SpeechRmsThreshold;
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

        lock (this.syncRoot)
        {
            this.captureBuffer?.Write(args.Buffer, 0, args.BytesRecorded);

            if (this.captureFormat is not null && this.AudioLevelUpdated is not null)
            {
                double rms = 0;
                var bytes = args.Buffer;
                var recorded = args.BytesRecorded;

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
        var newCapture = new WasapiCapture
        {
            ShareMode = exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared
        };

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
