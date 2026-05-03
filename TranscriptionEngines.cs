using System.IO;
using System.Runtime.InteropServices;
using SherpaOnnx;
using Whisper.net.LibraryLoader;

namespace PrimeDictate;

internal readonly record struct TranscriptionEngineConfiguration(
    TranscriptionBackendKind Backend,
    TranscriptionComputeInterface ComputeInterface,
    string? SelectedModelId,
    string? ConfiguredModelPath);

internal interface ITranscriptionEngine : IAsyncDisposable
{
    TranscriptionBackendKind Backend { get; }

    string Name { get; }

    ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        TranscriptionEngineConfiguration configuration,
        CancellationToken cancellationToken);
}

internal sealed class TranscriptionEngineHost : IAsyncDisposable
{
    private readonly SemaphoreSlim engineGate = new(initialCount: 1, maxCount: 1);
    private readonly object configurationSync = new();
    private ITranscriptionEngine? activeEngine;
    private TranscriptionEngineConfiguration configuration = new(
        TranscriptionBackendKind.Whisper,
        TranscriptionComputeInterface.Cpu,
        SelectedModelId: null,
        ConfiguredModelPath: null);

    public void UpdateConfiguration(
        TranscriptionBackendKind transcriptionBackend,
        TranscriptionComputeInterface transcriptionComputeInterface,
        string? selectedModelId,
        string? configuredModelPath)
    {
        TranscriptionEngineConfiguration snapshot;
        lock (this.configurationSync)
        {
            this.configuration = new TranscriptionEngineConfiguration(
                transcriptionBackend,
                transcriptionComputeInterface,
                string.IsNullOrWhiteSpace(selectedModelId) ? null : selectedModelId.Trim(),
                string.IsNullOrWhiteSpace(configuredModelPath) ? null : configuredModelPath.Trim());
            snapshot = this.configuration;
        }

        AppLog.Info($"Transcription configuration updated: {FormatConfiguration(snapshot)}");
    }

    public string ConfiguredBackendName => GetBackendName(this.GetConfigurationSnapshot().Backend);

    public string ConfigurationSummary => FormatConfiguration(this.GetConfigurationSnapshot());

    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        CancellationToken cancellationToken = default)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        var configurationSnapshot = this.GetConfigurationSnapshot();
        await this.engineGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var engine = await this.EnsureEngineAsync(configurationSnapshot).ConfigureAwait(false);
            return await engine.TranscribeAsync(audio, configurationSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.engineGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.engineGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (this.activeEngine is not null)
            {
                await this.activeEngine.DisposeAsync().ConfigureAwait(false);
                this.activeEngine = null;
            }
        }
        finally
        {
            this.engineGate.Release();
            this.engineGate.Dispose();
        }
    }

    private async Task<ITranscriptionEngine> EnsureEngineAsync(TranscriptionEngineConfiguration configurationSnapshot)
    {
        if (this.activeEngine is not null &&
            this.activeEngine.Backend == configurationSnapshot.Backend)
        {
            return this.activeEngine;
        }

        if (this.activeEngine is not null)
        {
            await this.activeEngine.DisposeAsync().ConfigureAwait(false);
        }

        this.activeEngine = CreateEngine(configurationSnapshot.Backend);
        AppLog.Info($"Selected transcription engine: {this.activeEngine.Name}");
        return this.activeEngine;
    }

    private TranscriptionEngineConfiguration GetConfigurationSnapshot()
    {
        lock (this.configurationSync)
        {
            return this.configuration;
        }
    }

    private static ITranscriptionEngine CreateEngine(TranscriptionBackendKind backend) => backend switch
    {
        TranscriptionBackendKind.Moonshine => new MoonshineTranscriptionEngine(),
        TranscriptionBackendKind.Parakeet => new ParakeetOnnxTranscriptionEngine(),
        TranscriptionBackendKind.WhisperNet => new WhisperNetTranscriptionEngine(),
        TranscriptionBackendKind.QualcommQnn => new QualcommQnnTranscriptionEngine(),
        _ => new WhisperOnnxTranscriptionEngine()
    };

    private static string GetBackendName(TranscriptionBackendKind backend) => backend switch
    {
        TranscriptionBackendKind.Moonshine => "Moonshine Core (Pure ONNX)",
        TranscriptionBackendKind.Parakeet => "Parakeet ONNX",
        TranscriptionBackendKind.WhisperNet => "Whisper.net (GGML)",
        TranscriptionBackendKind.QualcommQnn => "Qualcomm AI Hub Whisper QNN",
        _ => "Whisper ONNX"
    };

    private static string FormatConfiguration(TranscriptionEngineConfiguration configuration)
    {
        var backend = GetBackendName(configuration.Backend);
        var modelId = string.IsNullOrWhiteSpace(configuration.SelectedModelId)
            ? "<auto>"
            : configuration.SelectedModelId;
        var modelPath = string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath)
            ? "<auto>"
            : configuration.ConfiguredModelPath;
        var compute = configuration.ComputeInterface switch
        {
            TranscriptionComputeInterface.Gpu => "GPU",
            TranscriptionComputeInterface.Npu => "NPU",
            _ => "CPU"
        };
        return $"backend={backend}, compute={compute}, modelId={modelId}, modelPath={modelPath}";
    }
}

internal static class TranscriptionAudio
{
    public static int GetPcm16MonoSampleCount(PcmAudioBuffer audio, string backendName)
    {
        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException($"{backendName} input must be 16 kHz, 16-bit mono PCM.");
        }

        return audio.Pcm16KhzMono.Length / 2;
    }

    public static void CopyPcm16ToFloatSamples(byte[] pcm16, Span<float> destination)
    {
        var pcmBytes = pcm16.AsSpan(0, destination.Length * 2);
        var samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = samples[i] / 32768f;
        }
    }
}

internal abstract class SherpaOnnxTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim syncRoot = new SemaphoreSlim(1, 1);
    private OfflineRecognizer? recognizer;
    private string? loadedModelDirectory;
    private string? loadedProvider;

    public abstract TranscriptionBackendKind Backend { get; }

    public abstract string Name { get; }

    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        TranscriptionEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sampleCount = TranscriptionAudio.GetPcm16MonoSampleCount(audio, this.Name);
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await this.syncRoot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = new float[sampleCount];
            TranscriptionAudio.CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

            return await Task.Run(() =>
            {
                var recognizerInstance = this.EnsureRecognizer(configuration);
                using var stream = recognizerInstance.CreateStream();
                stream.AcceptWaveform(audio.SampleRate, samples);
                recognizerInstance.Decode([stream]);

                try
                {
                    return stream.Result.Text.Trim();
                }
                catch (NullReferenceException)
                {
                    // The C# wrapper for SherpaOnnx throws a NullReferenceException if the native library
                    // returns a null pointer when the audio contains no speech (especially in live preview).
                    // We should just return an empty string instead of crashing and reloading the model.
                    return string.Empty;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.syncRoot.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.syncRoot.WaitAsync().ConfigureAwait(false);
        try
        {
            this.ResetRecognizer();
        }
        finally
        {
            this.syncRoot.Release();
            this.syncRoot.Dispose();
        }
    }

    protected abstract string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration);

    protected abstract void ConfigureRecognizer(OfflineRecognizerConfig config, string modelDirectory);

    private OfflineRecognizer EnsureRecognizer(TranscriptionEngineConfiguration configuration)
    {
        var modelDirectory = this.ResolveModelDirectoryOrThrow(configuration);
        var provider = ResolveProvider(configuration.ComputeInterface);
        if (this.recognizer is not null &&
            string.Equals(this.loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(this.loadedProvider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return this.recognizer;
        }

        DisposeRecognizer(this.recognizer);
        this.recognizer = null;
        this.loadedModelDirectory = null;
        this.loadedProvider = null;

        AppLog.Info($"Loaded transcription backend: {this.Name} from {modelDirectory} using provider '{provider}'");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = provider;
        this.ConfigureRecognizer(config, modelDirectory);

        try
        {
            this.recognizer = new OfflineRecognizer(config);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to initialize {this.Name} recognizer with provider '{config.ModelConfig.Provider}' for model directory '{modelDirectory}'.",
                ex);
        }
        this.loadedModelDirectory = modelDirectory;
        this.loadedProvider = provider;
        return this.recognizer;
    }

    private void ResetRecognizer()
    {
        DisposeRecognizer(this.recognizer);
        this.recognizer = null;
        this.loadedModelDirectory = null;
        this.loadedProvider = null;
    }

    private static string ResolveProvider(TranscriptionComputeInterface computeInterface) => "cpu";

    private static void DisposeRecognizer(object? recognizer)
    {
        if (recognizer is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal sealed class WhisperOnnxTranscriptionEngine : SherpaOnnxTranscriptionEngine
{
    public override TranscriptionBackendKind Backend => TranscriptionBackendKind.Whisper;

    public override string Name => "Whisper ONNX";

    protected override string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (WhisperModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder not found or incomplete. Pick a folder containing *-encoder.int8.onnx, *-decoder.int8.onnx, and *-tokens.txt.");
        }

        if (WhisperModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            WhisperModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in WhisperModelCatalog.Options)
        {
            if (WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Whisper ONNX model not found. Download one in onboarding or browse to a local Whisper ONNX model folder.");
    }

    protected override void ConfigureRecognizer(OfflineRecognizerConfig config, string modelDirectory)
    {
        if (!WhisperModelCatalog.TryResolveModelFiles(modelDirectory, out var modelFiles))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder is incomplete. Expected encoder, decoder, and tokens files.");
        }

        config.ModelConfig.Tokens = modelFiles.Value.Tokens;
        config.ModelConfig.Whisper.Encoder = modelFiles.Value.Encoder;
        config.ModelConfig.Whisper.Decoder = modelFiles.Value.Decoder;
        config.ModelConfig.Whisper.Language = "en";
        config.ModelConfig.Whisper.Task = "transcribe";
    }

}

internal sealed class ParakeetOnnxTranscriptionEngine : SherpaOnnxTranscriptionEngine
{
    public override TranscriptionBackendKind Backend => TranscriptionBackendKind.Parakeet;

    public override string Name => "Parakeet ONNX";

    protected override string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (ParakeetModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Parakeet model folder not found or incomplete. Pick a folder containing encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, and tokens.txt.");
        }

        if (ParakeetModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            ParakeetModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in ParakeetModelCatalog.Options)
        {
            if (ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Parakeet model not found. Download one in onboarding or browse to a local Parakeet model folder.");
    }

    protected override void ConfigureRecognizer(OfflineRecognizerConfig config, string modelDirectory)
    {
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
    }
}

internal sealed class WhisperNetTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim syncRoot = new SemaphoreSlim(1, 1);
    private Whisper.net.WhisperFactory? factory;
    private Whisper.net.WhisperProcessor? processor;
    private string? loadedModelPath;
    private string? loadedOpenVinoEncoderPath;
    private TranscriptionComputeInterface? loadedComputeInterface;

    public TranscriptionBackendKind Backend => TranscriptionBackendKind.WhisperNet;

    public string Name => "Whisper.net (GGML)";

    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        TranscriptionEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sampleCount = TranscriptionAudio.GetPcm16MonoSampleCount(audio, this.Name);
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await this.syncRoot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = new float[sampleCount];
            TranscriptionAudio.CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

            return await Task.Run(async () =>
            {
                var processorInstance = this.EnsureProcessor(configuration);
                var sb = new System.Text.StringBuilder();
                await foreach (var segment in processorInstance.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        sb.Append(segment.Text);
                    }
                }
                return sb.ToString().Trim();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.syncRoot.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.syncRoot.WaitAsync().ConfigureAwait(false);
        try
        {
            this.ResetProcessor();
        }
        finally
        {
            this.syncRoot.Release();
            this.syncRoot.Dispose();
        }
    }

    private Whisper.net.WhisperProcessor EnsureProcessor(TranscriptionEngineConfiguration configuration)
    {
        var modelPath = this.ResolveModelPathOrThrow(configuration);
        var openVinoEncoderPath = ShouldUseOpenVino(configuration.ComputeInterface)
            ? TryGetOpenVinoEncoderPath(modelPath)
            : null;
        if (this.processor is not null &&
            string.Equals(this.loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            this.loadedComputeInterface == configuration.ComputeInterface &&
            string.Equals(this.loadedOpenVinoEncoderPath, openVinoEncoderPath, StringComparison.OrdinalIgnoreCase))
        {
            return this.processor;
        }

        this.ResetProcessor();

        AppLog.Info($"Loaded transcription backend: {this.Name} from {modelPath}");

        try
        {
            if (ShouldUseOpenVino(configuration.ComputeInterface) &&
                OperatingSystem.IsWindows() &&
                RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                AppLog.Info(
                    $"Whisper.net OpenVINO runtime supports Windows x64 only; current process architecture is {RuntimeInformation.ProcessArchitecture}. CPU fallback is expected.");
            }

            ConfigureWhisperRuntime(configuration.ComputeInterface, openVinoEncoderPath is not null);

            var openVinoDevice = GetOpenVinoDevice(configuration.ComputeInterface);
            if (ShouldUseOpenVino(configuration.ComputeInterface) && openVinoEncoderPath is null)
            {
                AppLog.Info(
                    $"OpenVINO encoder sidecars were not found next to {Path.GetFileName(modelPath)}. Falling back to CPU runtime for final transcription.");
            }

            this.factory = Whisper.net.WhisperFactory.FromPath(modelPath);

            var builder = this.factory.CreateBuilder()
                .WithLanguage("en");

            if (openVinoEncoderPath is not null)
            {
                var openVinoCachePath = GetOpenVinoCachePath(modelPath, openVinoDevice);
                Directory.CreateDirectory(openVinoCachePath);
                builder = builder.WithOpenVinoEncoder(openVinoEncoderPath, openVinoDevice, openVinoCachePath);
                AppLog.Info(
                    $"Configured Whisper.net OpenVINO encoder: device={openVinoDevice}, xml={openVinoEncoderPath}, cache={openVinoCachePath}");
            }

            this.processor = builder.Build();
            this.loadedModelPath = modelPath;
            this.loadedOpenVinoEncoderPath = openVinoEncoderPath;
            this.loadedComputeInterface = configuration.ComputeInterface;

            AppLog.Info($"Whisper.net native runtime selected: {RuntimeOptions.LoadedLibrary}");
            if (configuration.ComputeInterface == TranscriptionComputeInterface.Gpu &&
                RuntimeOptions.LoadedLibrary is not RuntimeLibrary.Cuda and not RuntimeLibrary.Vulkan)
            {
                AppLog.Info(
                    $"GPU was requested, but Whisper.net loaded {RuntimeOptions.LoadedLibrary} instead. Check CUDA/Vulkan runtime availability if GPU transcription is expected.");
            }
            else if (openVinoEncoderPath is not null && RuntimeOptions.LoadedLibrary != RuntimeLibrary.OpenVino)
            {
                AppLog.Info(
                    $"OpenVINO was requested for device {openVinoDevice}, but Whisper.net loaded {RuntimeOptions.LoadedLibrary} instead. Final transcription will not use the NPU until the OpenVINO runtime can be loaded.");
            }

            return this.processor;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to initialize {this.Name} processor for model path '{modelPath}'.",
                ex);
        }
    }

    private string ResolveModelPathOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath) && File.Exists(configuration.ConfiguredModelPath))
        {
            return configuration.ConfiguredModelPath;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Whisper.net GGML model file not found. Pick a valid .bin file.");
        }

        if (WhisperNetModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            WhisperNetModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in WhisperNetModelCatalog.Options)
        {
            if (WhisperNetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Whisper.net model not found. Download one in onboarding or browse to a local .bin model file.");
    }

    private void ResetProcessor()
    {
        this.processor?.Dispose();
        this.processor = null;
        this.factory?.Dispose();
        this.factory = null;
        this.loadedModelPath = null;
        this.loadedOpenVinoEncoderPath = null;
        this.loadedComputeInterface = null;
    }

    private static void ConfigureWhisperRuntime(
        TranscriptionComputeInterface computeInterface,
        bool preferOpenVino)
    {
        RuntimeOptions.LoadedLibrary = null;
        RuntimeOptions.RuntimeLibraryOrder = computeInterface switch
        {
            TranscriptionComputeInterface.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            TranscriptionComputeInterface.Gpu => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ when preferOpenVino => [RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
        };
    }

    private static bool ShouldUseOpenVino(TranscriptionComputeInterface computeInterface) =>
        computeInterface == TranscriptionComputeInterface.Npu;

    private static string GetOpenVinoDevice(TranscriptionComputeInterface computeInterface) =>
        computeInterface switch
        {
            TranscriptionComputeInterface.Npu => "NPU",
            TranscriptionComputeInterface.Gpu => "GPU",
            _ => "CPU"
        };

    private static string? TryGetOpenVinoEncoderPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(modelPath);
        var openVinoEncoderPath = Path.Combine(directory, $"{stem}-encoder-openvino.xml");
        var openVinoWeightsPath = Path.Combine(directory, $"{stem}-encoder-openvino.bin");
        return File.Exists(openVinoEncoderPath) && File.Exists(openVinoWeightsPath)
            ? openVinoEncoderPath
            : null;
    }

    private static string GetOpenVinoCachePath(string modelPath, string openVinoDevice)
    {
        var modelDirectory = Path.GetDirectoryName(modelPath);
        return Path.Combine(
            string.IsNullOrWhiteSpace(modelDirectory)
                ? ModelStorage.GetManagedModelsDirectory()
                : modelDirectory,
            ".openvino-cache",
            openVinoDevice.ToLowerInvariant());
    }
}
