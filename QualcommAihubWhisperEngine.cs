using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PrimeDictate;

internal sealed class QualcommAihubWhisperTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim syncRoot = new(1, 1);
    private QualcommAihubWhisperTranscriber? transcriber;
    private string? loadedModelDirectory;

    public TranscriptionBackendKind Backend => TranscriptionBackendKind.QualcommQnn;

    public string Name => "Qualcomm AI Hub Whisper QNN";

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

        if (configuration.ComputeInterface != TranscriptionComputeInterface.Npu)
        {
            throw new InvalidOperationException("Qualcomm AI Hub Whisper requires the Qualcomm QNN NPU compute path.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await this.syncRoot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var samples = new float[sampleCount];
            TranscriptionAudio.CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

            var transcriber = this.EnsureTranscriber(configuration);
            return await Task.Run(() => transcriber.Transcribe(samples, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
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
            this.ResetTranscriber();
        }
        finally
        {
            this.syncRoot.Release();
            this.syncRoot.Dispose();
        }
    }

    private QualcommAihubWhisperTranscriber EnsureTranscriber(TranscriptionEngineConfiguration configuration)
    {
        var modelDirectory = ResolveModelDirectoryOrThrow(configuration);
        if (this.transcriber is not null &&
            string.Equals(this.loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return this.transcriber;
        }

        this.ResetTranscriber();

        if (!PlatformSupport.SupportsQualcommQnnHtp)
        {
            throw new InvalidOperationException(QnnRuntimeSupport.GetAvailability().Summary);
        }

        var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory, strictValidationOverride: true) with
        {
            EnableContextCache = false
        };

        AppLog.Info($"Loaded transcription backend: {this.Name} from {modelDirectory} using QNN HTP");
        this.transcriber = QualcommAihubWhisperTranscriber.Create(modelDirectory, runtimeOptions);
        this.loadedModelDirectory = modelDirectory;
        AppLog.Info($"Qualcomm AI Hub Whisper diagnostics: {this.transcriber.DiagnosticsSummary}");
        return this.transcriber;
    }

    private void ResetTranscriber()
    {
        this.transcriber?.Dispose();
        this.transcriber = null;
        this.loadedModelDirectory = null;
    }

    private static string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (QualcommAihubWhisperModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "The selected Qualcomm AI Hub package contains only encoder.bin and decoder.bin raw QNN context binaries. PrimeDictate needs the matching precompiled_qnn_onnx package with encoder.onnx and decoder.onnx wrappers so ONNX Runtime can execute it.");
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Qualcomm AI Hub Whisper model folder not found or incomplete. Pick a folder containing encoder.onnx, decoder.onnx, encoder_qairt_context.bin, decoder_qairt_context.bin, metadata.json, and multilingual.tiktoken.");
        }

        if (QualcommAihubWhisperModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in QualcommAihubWhisperModelCatalog.Options)
        {
            if (QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Qualcomm AI Hub Whisper model not found. Download the model in onboarding or browse to an extracted precompiled_qnn_onnx model folder.");
    }
}

internal sealed class QualcommAihubWhisperTranscriber : IDisposable
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = SampleRate * 30;
    private const int MeanDecodeLength = 200;
    private const int DecoderLayers = 12;
    private const int DecoderHeads = 12;
    private const int HeadDim = 64;
    private const int AudioEmbeddingLength = 1500;
    private const int EndOfTranscriptToken = 50257;
    private const int StartOfTranscriptToken = 50258;
    private const int LanguageEnglishToken = 50259;
    private const int TranscribeToken = 50359;
    private const int NoTimestampsToken = 50363;
    private const double DecoderTokensPerSecondBudget = 12d;
    private const int DecoderTokenAllowance = 16;
    private static readonly int[] PromptTokens =
    [
        StartOfTranscriptToken,
        LanguageEnglishToken,
        TranscribeToken,
        NoTimestampsToken
    ];
    private static readonly Float16 MaskNegative = (Float16)(-100f);
    private static readonly Float16 HalfZero = (Float16)0f;

    private readonly InferenceSession encoderSession;
    private readonly InferenceSession decoderSession;
    private readonly QualcommAihubWhisperFeatureExtractor featureExtractor = new();
    private readonly WhisperTiktokenDecoder tokenizer;
    private readonly QualcommAihubWhisperArtifacts artifacts;
    private readonly QualcommQnnRuntimeOptions runtimeOptions;

    private QualcommAihubWhisperTranscriber(
        QualcommAihubWhisperArtifacts artifacts,
        QualcommQnnRuntimeOptions runtimeOptions,
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        WhisperTiktokenDecoder tokenizer)
    {
        this.artifacts = artifacts;
        this.runtimeOptions = runtimeOptions;
        this.encoderSession = encoderSession;
        this.decoderSession = decoderSession;
        this.tokenizer = tokenizer;
    }

    public string DiagnosticsSummary =>
        $"modelDirectory={this.artifacts.ModelDirectory}, runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(QualcommQnnActiveRuntime.QnnHtp, this.runtimeOptions, contextFilePath: "<precompiled-ep-context>")})";

    public static QualcommAihubWhisperTranscriber Create(
        string modelDirectory,
        QualcommQnnRuntimeOptions runtimeOptions)
    {
        if (!QualcommAihubWhisperModelCatalog.TryResolveArtifacts(modelDirectory, out var artifacts))
        {
            throw new FileNotFoundException(
                "Qualcomm AI Hub Whisper model folder is incomplete. Expected encoder.onnx, decoder.onnx, context binaries, metadata.json, and multilingual.tiktoken.");
        }

        InferenceSession CreateSession(string sessionName, string modelPath)
        {
            using var options = QnnRuntimeSupport.CreateSessionOptions(
                QualcommQnnActiveRuntime.QnnHtp,
                runtimeOptions,
                sessionTag: $"PrimeDictate.{sessionName}",
                contextFilePath: null);
            return new InferenceSession(modelPath, options);
        }

        return new QualcommAihubWhisperTranscriber(
            artifacts.Value,
            runtimeOptions,
            CreateSession("QualcommAihubWhisperEncoder", artifacts.Value.EncoderOnnxPath),
            CreateSession("QualcommAihubWhisperDecoder", artifacts.Value.DecoderOnnxPath),
            WhisperTiktokenDecoder.Load(artifacts.Value.TokenizerPath));
    }

    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length <= ChunkSamples)
        {
            return this.TranscribeChunk(samples, cancellationToken);
        }

        var transcript = new StringBuilder();
        for (var offset = 0; offset < samples.Length; offset += ChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(ChunkSamples, samples.Length - offset);
            var chunk = new float[chunkLength];
            Array.Copy(samples, offset, chunk, 0, chunkLength);
            var chunkText = this.TranscribeChunk(chunk, cancellationToken);
            if (string.IsNullOrWhiteSpace(chunkText))
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                transcript.Append(' ');
            }

            transcript.Append(chunkText.Trim());
        }

        return transcript.ToString().Trim();
    }

    public void Dispose()
    {
        this.decoderSession.Dispose();
        this.encoderSession.Dispose();
    }

    private string TranscribeChunk(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputFeatures = this.featureExtractor.Extract(samples, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var encoderOutputs = this.encoderSession.Run(
            [NamedOnnxValue.CreateFromTensor("input_features", inputFeatures)]);
        var crossCacheByName = CopyFloat16Outputs(encoderOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        var attentionMaskValues = CreateFilledArray<Float16>(MeanDecodeLength, MaskNegative);
        var attentionMask = new DenseTensor<Float16>(attentionMaskValues, [1, 1, 1, MeanDecodeLength]);
        var selfCacheByName = CreateInitialSelfCache();
        var outputTokens = new List<int>();
        var currentToken = PromptTokens[0];
        var position = 0;
        var maxDecodeSteps = GetMaxDecodeSteps(samples.Length);
        var reachedBudget = true;

        for (var n = 0; n < maxDecodeSteps; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attentionMaskValues[MeanDecodeLength - n - 1] = HalfZero;
            var inputs = new List<NamedOnnxValue>(2 + DecoderLayers * 4 + 1)
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<int>(new[] { currentToken }, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            for (var layer = 0; layer < DecoderLayers; layer++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"k_cache_self_{layer}_in", selfCacheByName[$"k_cache_self_{layer}_in"]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"v_cache_self_{layer}_in", selfCacheByName[$"v_cache_self_{layer}_in"]));
            }

            for (var layer = 0; layer < DecoderLayers; layer++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor($"k_cache_cross_{layer}", crossCacheByName[$"k_cache_cross_{layer}"]));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"v_cache_cross_{layer}", crossCacheByName[$"v_cache_cross_{layer}"]));
            }

            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<int>(new[] { position }, new[] { 1 })));

            using var decoderOutputs = this.decoderSession.Run(inputs);
            var logits = CopyFloat16Tensor(decoderOutputs.First(output => string.Equals(output.Name, "logits", StringComparison.Ordinal)));
            var nextToken = ArgMax(logits);

            selfCacheByName = CopySelfCacheOutputs(decoderOutputs);
            if (n < PromptTokens.Length - 1)
            {
                currentToken = PromptTokens[n + 1];
                position++;
                continue;
            }

            if (nextToken == EndOfTranscriptToken)
            {
                reachedBudget = false;
                break;
            }

            outputTokens.Add(nextToken);
            currentToken = nextToken;
            position++;
        }

        var decoded = this.tokenizer.Decode(outputTokens);
        if (string.IsNullOrWhiteSpace(decoded) && outputTokens.Count > 0)
        {
            AppLog.Info($"Qualcomm AI Hub Whisper decoded no text; generated tokens: {FormatTokenPreview(outputTokens)}.");
        }
        else if (reachedBudget)
        {
            AppLog.Info(
                $"Qualcomm AI Hub Whisper reached decode budget ({maxDecodeSteps} steps, {outputTokens.Count} generated tokens).");
        }

        return decoded;
    }

    private static int GetMaxDecodeSteps(int sampleCount)
    {
        var audioSeconds = Math.Max(0.5d, sampleCount / (double)SampleRate);
        var generatedTokenBudget = (int)Math.Ceiling(audioSeconds * DecoderTokensPerSecondBudget) + DecoderTokenAllowance;
        return Math.Clamp(
            PromptTokens.Length + generatedTokenBudget,
            PromptTokens.Length + 8,
            MeanDecodeLength - 1);
    }

    private static string FormatTokenPreview(IReadOnlyList<int> tokens)
    {
        const int maxPreviewTokens = 32;
        var previewCount = Math.Min(tokens.Count, maxPreviewTokens);
        var builder = new StringBuilder();
        for (var i = 0; i < previewCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(tokens[i]);
        }

        if (tokens.Count > previewCount)
        {
            builder.Append(",...");
        }

        return builder.ToString();
    }

    private static Dictionary<string, DenseTensor<Float16>> CreateInitialSelfCache()
    {
        var caches = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        for (var layer = 0; layer < DecoderLayers; layer++)
        {
            caches[$"k_cache_self_{layer}_in"] = new DenseTensor<Float16>(
                new Float16[DecoderHeads * HeadDim * (MeanDecodeLength - 1)],
                [DecoderHeads, 1, HeadDim, MeanDecodeLength - 1]);
            caches[$"v_cache_self_{layer}_in"] = new DenseTensor<Float16>(
                new Float16[DecoderHeads * (MeanDecodeLength - 1) * HeadDim],
                [DecoderHeads, 1, MeanDecodeLength - 1, HeadDim]);
        }

        return caches;
    }

    private static Dictionary<string, DenseTensor<Float16>> CopyFloat16Outputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensors = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            tensors[output.Name] = CopyFloat16Tensor(output);
        }

        return tensors;
    }

    private static Dictionary<string, DenseTensor<Float16>> CopySelfCacheOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensors = new Dictionary<string, DenseTensor<Float16>>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (!output.Name.Contains("_cache_self_", StringComparison.Ordinal))
            {
                continue;
            }

            var inputName = output.Name.Replace("_out", "_in", StringComparison.Ordinal);
            tensors[inputName] = CopyFloat16Tensor(output);
        }

        return tensors;
    }

    private static DenseTensor<Float16> CopyFloat16Tensor(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<Float16>();
        return new DenseTensor<Float16>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static int ArgMax(DenseTensor<Float16> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty)
        {
            return EndOfTranscriptToken;
        }

        var bestIndex = 0;
        var bestValue = span[0].ToFloat();
        for (var i = 1; i < span.Length; i++)
        {
            var candidate = span[i].ToFloat();
            if (candidate > bestValue)
            {
                bestValue = candidate;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static T[] CreateFilledArray<T>(int length, T value)
    {
        var values = new T[length];
        Array.Fill(values, value);
        return values;
    }
}

internal sealed class QualcommAihubWhisperFeatureExtractor
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = SampleRate * 30;
    private const int Nfft = 400;
    private const int HopLength = 160;
    private const int MelBins = 80;
    private const int Frames = 3000;
    private const int FrequencyBins = Nfft / 2 + 1;
    private const float LogFloor = 1e-10f;

    private static readonly Lazy<float[]> HannWindow = new(CreateHannWindow);
    private static readonly Lazy<float[,]> MelFilters = new(CreateMelFilters);
    private static readonly Lazy<float[,]> CosTable = new(() => CreateTrigTable(MathF.Cos));
    private static readonly Lazy<float[,]> SinTable = new(() => CreateTrigTable(MathF.Sin));

    public DenseTensor<Float16> Extract(float[] samples, CancellationToken cancellationToken)
    {
        var padded = new float[ChunkSamples];
        Array.Copy(samples, padded, Math.Min(samples.Length, padded.Length));

        var power = new float[FrequencyBins];
        var mel = new float[MelBins * Frames];
        var silenceLog = MathF.Log10(LogFloor);
        Array.Fill(mel, silenceLog);
        var maxLog = silenceLog;
        var framesToCompute = GetFrameCountToCompute(samples.Length);
        var window = HannWindow.Value;
        var filters = MelFilters.Value;
        var cos = CosTable.Value;
        var sin = SinTable.Value;

        for (var frame = 0; frame < framesToCompute; frame++)
        {
            if ((frame & 0x1f) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var frameStart = frame * HopLength - Nfft / 2;
            for (var frequency = 0; frequency < FrequencyBins; frequency++)
            {
                var real = 0f;
                var imaginary = 0f;
                for (var n = 0; n < Nfft; n++)
                {
                    var sample = GetReflectedSample(padded, frameStart + n) * window[n];
                    real += sample * cos[frequency, n];
                    imaginary -= sample * sin[frequency, n];
                }

                power[frequency] = real * real + imaginary * imaginary;
            }

            for (var melBin = 0; melBin < MelBins; melBin++)
            {
                var sum = 0f;
                for (var frequency = 0; frequency < FrequencyBins; frequency++)
                {
                    sum += filters[melBin, frequency] * power[frequency];
                }

                var logValue = MathF.Log10(MathF.Max(sum, LogFloor));
                mel[melBin * Frames + frame] = logValue;
                if (logValue > maxLog)
                {
                    maxLog = logValue;
                }
            }
        }

        var clampFloor = maxLog - 8f;
        var features = new Float16[MelBins * Frames];
        for (var i = 0; i < mel.Length; i++)
        {
            var normalized = (MathF.Max(mel[i], clampFloor) + 4f) / 4f;
            features[i] = (Float16)normalized;
        }

        return new DenseTensor<Float16>(features, [1, MelBins, Frames]);
    }

    private static int GetFrameCountToCompute(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return 1;
        }

        return Math.Clamp((sampleCount + Nfft) / HopLength + 1, 1, Frames);
    }

    private static float GetReflectedSample(float[] samples, int index)
    {
        if (index < 0)
        {
            index = -index;
        }
        else if (index >= samples.Length)
        {
            index = 2 * samples.Length - index - 2;
        }

        if (index < 0 || index >= samples.Length)
        {
            return 0f;
        }

        return samples[index];
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[Nfft];
        for (var i = 0; i < window.Length; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / Nfft));
        }

        return window;
    }

    private static float[,] CreateTrigTable(Func<float, float> trig)
    {
        var table = new float[FrequencyBins, Nfft];
        for (var frequency = 0; frequency < FrequencyBins; frequency++)
        {
            for (var n = 0; n < Nfft; n++)
            {
                table[frequency, n] = trig(2f * MathF.PI * frequency * n / Nfft);
            }
        }

        return table;
    }

    private static float[,] CreateMelFilters()
    {
        var filters = new float[MelBins, FrequencyBins];
        var fftFrequencies = new float[FrequencyBins];
        for (var i = 0; i < FrequencyBins; i++)
        {
            fftFrequencies[i] = i * SampleRate / (float)Nfft;
        }

        var minMel = HertzToMel(0f);
        var maxMel = HertzToMel(SampleRate / 2f);
        var melPoints = new float[MelBins + 2];
        for (var i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = MelToHertz(minMel + (maxMel - minMel) * i / (melPoints.Length - 1));
        }

        for (var melBin = 0; melBin < MelBins; melBin++)
        {
            var lower = melPoints[melBin];
            var center = melPoints[melBin + 1];
            var upper = melPoints[melBin + 2];
            var enorm = 2f / (upper - lower);

            for (var frequency = 0; frequency < FrequencyBins; frequency++)
            {
                var hz = fftFrequencies[frequency];
                var lowerSlope = (hz - lower) / (center - lower);
                var upperSlope = (upper - hz) / (upper - center);
                filters[melBin, frequency] = MathF.Max(0f, MathF.Min(lowerSlope, upperSlope)) * enorm;
            }
        }

        return filters;
    }

    private static float HertzToMel(float hertz)
    {
        const float minLogHertz = 1000f;
        const float minLogMel = 15f;
        var linearMel = 3f * hertz / 200f;
        if (hertz < minLogHertz)
        {
            return linearMel;
        }

        return minLogMel + MathF.Log(hertz / minLogHertz) / (MathF.Log(6.4f) / 27f);
    }

    private static float MelToHertz(float mel)
    {
        const float minLogHertz = 1000f;
        const float minLogMel = 15f;
        if (mel < minLogMel)
        {
            return 200f * mel / 3f;
        }

        return minLogHertz * MathF.Exp((mel - minLogMel) * (MathF.Log(6.4f) / 27f));
    }
}

internal sealed class WhisperTiktokenDecoder
{
    private const int SpecialTokenStart = 50257;
    private readonly Dictionary<int, byte[]> tokenBytesById;

    private WhisperTiktokenDecoder(Dictionary<int, byte[]> tokenBytesById)
    {
        this.tokenBytesById = tokenBytesById;
    }

    public static WhisperTiktokenDecoder Load(string tokenizerPath)
    {
        var tokenBytesById = new Dictionary<int, byte[]>();
        foreach (var line in File.ReadLines(tokenizerPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var tokenId))
            {
                continue;
            }

            tokenBytesById[tokenId] = DecodeTokenBytes(parts[0]);
        }

        if (tokenBytesById.Count == 0)
        {
            throw new InvalidOperationException("Whisper tokenizer file is empty or invalid.");
        }

        return new WhisperTiktokenDecoder(tokenBytesById);
    }

    private static byte[] DecodeTokenBytes(string encodedToken)
    {
        try
        {
            return Convert.FromBase64String(encodedToken);
        }
        catch (FormatException) when (string.Equals(encodedToken, "=", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetBytes(encodedToken);
        }
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        using var bytes = new MemoryStream();
        foreach (var tokenId in tokenIds)
        {
            if (tokenId >= SpecialTokenStart)
            {
                continue;
            }

            if (this.tokenBytesById.TryGetValue(tokenId, out var tokenBytes))
            {
                bytes.Write(tokenBytes, 0, tokenBytes.Length);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }
}
