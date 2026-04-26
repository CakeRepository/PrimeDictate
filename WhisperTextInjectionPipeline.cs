using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SharpHook;
using SharpHook.Data;
using Whisper.net;

namespace PrimeDictate;

internal static class ModelFileLocator
{
    private static readonly string DefaultRelativePath = Path.Combine("models", "ggml-large-v3-turbo.bin");

    internal static string ResolveOrThrow()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PRIME_DICTATE_MODEL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        if (File.Exists(DefaultRelativePath))
        {
            return Path.GetFullPath(DefaultRelativePath);
        }

        var fromBase = Path.Combine(AppContext.BaseDirectory, "models", "ggml-large-v3-turbo.bin");
        if (File.Exists(fromBase))
        {
            return fromBase;
        }

        if (TryFindPathAboveWorkingDirectory(out var fromWalk))
        {
            return fromWalk;
        }

        throw new FileNotFoundException(
            $"Whisper model not found. Place ggml-large-v3-turbo.bin in ./{DefaultRelativePath}, set PRIME_DICTATE_MODEL, or run from the repository root.");
    }

    private static bool TryFindPathAboveWorkingDirectory([NotNullWhen(true)] out string? foundPath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir.FullName, "models", "ggml-large-v3-turbo.bin");
            if (File.Exists(candidate))
            {
                foundPath = candidate;
                return true;
            }

            dir = dir.Parent;
        }

        foundPath = null;
        return false;
    }
}

/// <summary>
/// Lifecycle holder for the Whisper native model and the configured processor. Both must be disposed
/// in native wrapper order: processor, then factory.
/// </summary>
internal sealed class WhisperModelSession : IAsyncDisposable
{
    public WhisperModelSession(WhisperFactory factory, WhisperProcessor processor)
    {
        this.Factory = factory;
        this.Processor = processor;
    }

    public WhisperFactory Factory { get; }

    public WhisperProcessor Processor { get; }

    public async ValueTask DisposeAsync()
    {
        await this.Processor.DisposeAsync().ConfigureAwait(false);
        this.Factory.Dispose();
    }
}

/// <summary>
/// Transcribes with Whisper, then updates the focused control via final-only Unicode input (no clipboard).
/// Target injection is intentionally final-only: partial hypotheses are not typed into editors because repeated
/// correction loops fight autocomplete, caret movement, and slow input targets.
/// </summary>
internal sealed class WhisperTextInjectionPipeline
{
    private readonly SemaphoreSlim initGate = new(initialCount: 1, maxCount: 1);
    private readonly EventSimulator eventSimulator = new();
    private WhisperModelSession? session;

    /// <summary>
    /// Full-buffer transcription with no target mutation.
    /// </summary>
    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        CancellationToken cancellationToken = default,
        bool logTranscript = true)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        var text = await this.TranscribeToStringAsync(audio, cancellationToken).ConfigureAwait(false);
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (logTranscript)
            {
                AppLog.Info("Whisper returned no text.");
            }

            return string.Empty;
        }

        if (logTranscript)
        {
            AppLog.Info($"Transcribed: {text}");
        }

        return text;
    }

    public void InjectTextToTarget(string text)
    {
        var target = text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsUnicodeInput.SendText(target);
            return;
        }

        var textResult = this.eventSimulator.SimulateTextEntry(target);
        if (textResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {textResult}.");
        }
    }

    public void SendEnterToTarget()
    {
        var keyResult = this.eventSimulator.SimulateKeyStroke(new[] { KeyCode.VcEnter });
        if (keyResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {keyResult}.");
        }
    }

    private async Task<string> TranscribeToStringAsync(PcmAudioBuffer audio, CancellationToken cancellationToken)
    {
        var modelSession = await this.EnsureSessionAsync().ConfigureAwait(false);

        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException("Whisper input must be 16 kHz, 16-bit mono PCM.");
        }

        var sampleCount = audio.Pcm16KhzMono.Length / 2;
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        var samples = ArrayPool<float>.Shared.Rent(sampleCount);
        var builder = new StringBuilder();

        try
        {
            CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples.AsSpan(0, sampleCount));

            await foreach (var result in modelSession.Processor
                               .ProcessAsync(new ReadOnlyMemory<float>(samples, 0, sampleCount), cancellationToken)
                               .ConfigureAwait(false))
            {
                builder.Append(result.Text);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }

        return builder.ToString().Trim();
    }

    private static void CopyPcm16ToFloatSamples(byte[] pcm16, Span<float> destination)
    {
        var pcmBytes = pcm16.AsSpan(0, destination.Length * 2);
        var samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = samples[i] / 32768f;
        }
    }

    private async Task<WhisperModelSession> EnsureSessionAsync()
    {
        if (this.session is not null)
        {
            return this.session;
        }

        await this.initGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (this.session is not null)
            {
                return this.session;
            }

            var modelPath = ModelFileLocator.ResolveOrThrow();
            AppLog.Info($"Loading Whisper model from: {modelPath}");
            var factory = WhisperFactory.FromPath(modelPath);
            var processor = factory.CreateBuilder()
                .WithLanguageDetection()
                .Build();
            this.session = new WhisperModelSession(factory, processor);
            return this.session;
        }
        finally
        {
            this.initGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.initGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (this.session is not null)
            {
                await this.session.DisposeAsync().ConfigureAwait(false);
                this.session = null;
            }
        }
        finally
        {
            this.initGate.Release();
            this.initGate.Dispose();
        }
    }
}
