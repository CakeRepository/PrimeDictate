using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SherpaOnnx;

namespace PrimeDictate;

internal readonly record struct MoonshineV1Files(string Preprocess, string Encode, string Uncached, string Cached, string Tokens);
internal readonly record struct MoonshineV2Files(string Encoder, string Decoder, string Tokens);

internal sealed class MoonshineTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim syncRoot = new(1, 1);
    private IMoonshineTranscriber? transcriber;
    private string? loadedModelDirectory;
    private TranscriptionComputeInterface? loadedComputeInterface;
    private bool loadedStrictValidation;

    public TranscriptionBackendKind Backend => TranscriptionBackendKind.Moonshine;

    public string Name => "Moonshine Core (Pure ONNX)";

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

            var transcriber = this.EnsureTranscriber(configuration);
            try
            {
                return await Task.Run(() => transcriber.Transcribe(samples), cancellationToken).ConfigureAwait(false);
            }
            catch (OnnxRuntimeException ex) when (transcriber.ActiveRuntime == QualcommQnnActiveRuntime.QnnHtp && !transcriber.StrictValidation)
            {
                AppLog.Info(
                    $"Moonshine QNN failed during inference and will fall back to CPU ORT. {transcriber.DiagnosticsSummary}. Error: {ex.Message}");

                this.ResetTranscriber();
                var cpuTranscriber = MoonshineTranscriberFactory.Create(
                    transcriber.ModelDirectory,
                    QualcommQnnActiveRuntime.Cpu,
                    transcriber.RuntimeOptions,
                    preferQnnArtifacts: false);
                this.AssignLoadedTranscriber(cpuTranscriber, transcriber.ModelDirectory, configuration.ComputeInterface, transcriber.StrictValidation);
                return await Task.Run(() => cpuTranscriber.Transcribe(samples), cancellationToken).ConfigureAwait(false);
            }
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

    private IMoonshineTranscriber EnsureTranscriber(TranscriptionEngineConfiguration configuration)
    {
        var modelDirectory = ResolveModelDirectoryOrThrow(configuration);
        var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory);
        var requestedRuntime = configuration.ComputeInterface == TranscriptionComputeInterface.Npu
            ? QualcommQnnActiveRuntime.QnnHtp
            : QualcommQnnActiveRuntime.Cpu;

        if (this.transcriber is not null &&
            string.Equals(this.loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase) &&
            this.loadedComputeInterface == configuration.ComputeInterface &&
            this.loadedStrictValidation == runtimeOptions.StrictValidation)
        {
            return this.transcriber;
        }

        this.ResetTranscriber();

        AppLog.Info($"Loaded transcription backend: {this.Name} from {modelDirectory} using {requestedRuntime}");

        IMoonshineTranscriber created;
        if (requestedRuntime == QualcommQnnActiveRuntime.QnnHtp)
        {
            try
            {
                created = MoonshineTranscriberFactory.Create(
                    modelDirectory,
                    QualcommQnnActiveRuntime.QnnHtp,
                    runtimeOptions,
                    preferQnnArtifacts: true);
            }
            catch (Exception ex) when (!runtimeOptions.StrictValidation)
            {
                AppLog.Info(
                    $"Moonshine QNN session creation failed and will fall back to CPU ORT. Error: {ex.Message}");
                created = MoonshineTranscriberFactory.Create(
                    modelDirectory,
                    QualcommQnnActiveRuntime.Cpu,
                    runtimeOptions,
                    preferQnnArtifacts: false);
            }
        }
        else
        {
            created = MoonshineTranscriberFactory.Create(
                modelDirectory,
                QualcommQnnActiveRuntime.Cpu,
                runtimeOptions,
                preferQnnArtifacts: false);
        }

        this.AssignLoadedTranscriber(created, modelDirectory, configuration.ComputeInterface, runtimeOptions.StrictValidation);
        return created;
    }

    private void AssignLoadedTranscriber(
        IMoonshineTranscriber created,
        string modelDirectory,
        TranscriptionComputeInterface computeInterface,
        bool strictValidation)
    {
        this.transcriber = created;
        this.loadedModelDirectory = modelDirectory;
        this.loadedComputeInterface = computeInterface;
        this.loadedStrictValidation = strictValidation;
        AppLog.Info($"Moonshine Core diagnostics: {created.DiagnosticsSummary}");
    }

    private void ResetTranscriber()
    {
        this.transcriber?.Dispose();
        this.transcriber = null;
        this.loadedModelDirectory = null;
        this.loadedComputeInterface = null;
        this.loadedStrictValidation = false;
    }

    private static string ResolveModelDirectoryOrThrow(TranscriptionEngineConfiguration configuration)
    {
        if (MoonshineModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Moonshine model folder not found or incomplete. Core Moonshine requires a folder containing an encoder and a decoder ONNX model.");
        }

        if (MoonshineModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            MoonshineModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in MoonshineModelCatalog.Options)
        {
            if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Moonshine model not found. Download one in onboarding or browse to a local Moonshine model folder.");
    }
}

internal interface IMoonshineTranscriber : IDisposable
{
    string ModelDirectory { get; }
    QualcommQnnActiveRuntime ActiveRuntime { get; }
    QualcommQnnRuntimeOptions RuntimeOptions { get; }
    bool StrictValidation { get; }
    string DiagnosticsSummary { get; }
    string Transcribe(float[] samples);
}

internal static class MoonshineTranscriberFactory
{
    public static IMoonshineTranscriber Create(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        bool preferQnnArtifacts)
    {
        if (activeRuntime == QualcommQnnActiveRuntime.Cpu)
        {
            return SherpaMoonshineTranscriber.Create(modelDirectory, runtimeOptions);
        }

        if (MoonshineModelCatalog.TryResolveV2Files(modelDirectory, out var v2Files))
        {
            return MoonshineV2Transcriber.Create(modelDirectory, activeRuntime, runtimeOptions, v2Files.Value, preferQnnArtifacts);
        }

        if (MoonshineModelCatalog.TryResolveV1Files(modelDirectory, out var v1Files))
        {
            return MoonshineV1Transcriber.Create(modelDirectory, activeRuntime, runtimeOptions, v1Files.Value, preferQnnArtifacts);
        }

        throw new FileNotFoundException($"No valid Moonshine model (v1 or v2) found in {modelDirectory}");
    }
}

internal sealed class SherpaMoonshineTranscriber : IMoonshineTranscriber
{
    private const int SampleRate = 16_000;

    private readonly OfflineRecognizer recognizer;
    private readonly string version;

    private SherpaMoonshineTranscriber(
        string modelDirectory,
        QualcommQnnRuntimeOptions runtimeOptions,
        OfflineRecognizer recognizer,
        string version)
    {
        this.ModelDirectory = modelDirectory;
        this.RuntimeOptions = runtimeOptions;
        this.recognizer = recognizer;
        this.version = version;
    }

    public string ModelDirectory { get; }
    public QualcommQnnActiveRuntime ActiveRuntime => QualcommQnnActiveRuntime.Cpu;
    public QualcommQnnRuntimeOptions RuntimeOptions { get; }
    public bool StrictValidation => false;

    public string DiagnosticsSummary =>
        $"activeRuntime={this.ActiveRuntime}, version={this.version}, runtime=sherpa-onnx, provider=cpu";

    public static SherpaMoonshineTranscriber Create(
        string modelDirectory,
        QualcommQnnRuntimeOptions runtimeOptions)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.ModelConfig.NumThreads = 2;

        string version;
        if (MoonshineModelCatalog.TryResolveV2Files(modelDirectory, out var v2Files))
        {
            config.ModelConfig.Tokens = v2Files.Value.Tokens;
            config.ModelConfig.Moonshine.Encoder = v2Files.Value.Encoder;
            config.ModelConfig.Moonshine.MergedDecoder = v2Files.Value.Decoder;
            version = "v2";
        }
        else if (MoonshineModelCatalog.TryResolveV1Files(modelDirectory, out var v1Files))
        {
            config.ModelConfig.Tokens = v1Files.Value.Tokens;
            config.ModelConfig.Moonshine.Preprocessor = v1Files.Value.Preprocess;
            config.ModelConfig.Moonshine.Encoder = v1Files.Value.Encode;
            config.ModelConfig.Moonshine.UncachedDecoder = v1Files.Value.Uncached;
            config.ModelConfig.Moonshine.CachedDecoder = v1Files.Value.Cached;
            version = "v1";
        }
        else
        {
            throw new FileNotFoundException($"No valid Moonshine model (v1 or v2) found in {modelDirectory}");
        }

        return new SherpaMoonshineTranscriber(
            modelDirectory,
            runtimeOptions,
            new OfflineRecognizer(config),
            version);
    }

    public string Transcribe(float[] samples)
    {
        using var stream = this.recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        this.recognizer.Decode([stream]);

        try
        {
            return stream.Result.Text.Trim();
        }
        catch (NullReferenceException)
        {
            return string.Empty;
        }
    }

    public void Dispose() => this.recognizer.Dispose();
}

internal sealed record MoonshineOrtArtifacts(
    string ModelDirectory,
    IReadOnlyList<string> StagePaths,
    string TokensPath,
    bool UsingQnnArtifacts)
{
    public string Describe() =>
        $"modelDirectory={this.ModelDirectory}, stages=[{string.Join(", ", this.StagePaths.Select(Path.GetFileName))}], qnnArtifacts={this.UsingQnnArtifacts}";
}

internal sealed class MoonshineV1Transcriber : IMoonshineTranscriber
{
    private readonly InferenceSession preprocessSession;
    private readonly InferenceSession encodeSession;
    private readonly InferenceSession uncachedDecodeSession;
    private readonly InferenceSession cachedDecodeSession;
    private readonly MoonshineTokenizer tokenizer;
    private readonly MoonshineOrtArtifacts artifacts;

    private MoonshineV1Transcriber(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        MoonshineOrtArtifacts artifacts,
        InferenceSession preprocessSession,
        InferenceSession encodeSession,
        InferenceSession uncachedDecodeSession,
        InferenceSession cachedDecodeSession,
        MoonshineTokenizer tokenizer)
    {
        this.ModelDirectory = modelDirectory;
        this.ActiveRuntime = activeRuntime;
        this.RuntimeOptions = runtimeOptions;
        this.artifacts = artifacts;
        this.preprocessSession = preprocessSession;
        this.encodeSession = encodeSession;
        this.uncachedDecodeSession = uncachedDecodeSession;
        this.cachedDecodeSession = cachedDecodeSession;
        this.tokenizer = tokenizer;
    }

    public string ModelDirectory { get; }
    public QualcommQnnActiveRuntime ActiveRuntime { get; }
    public QualcommQnnRuntimeOptions RuntimeOptions { get; }
    public bool StrictValidation => this.RuntimeOptions.StrictValidation;

    public string DiagnosticsSummary =>
        $"activeRuntime={this.ActiveRuntime}, version=v1, artifacts=({this.artifacts.Describe()}), runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(this.ActiveRuntime, this.RuntimeOptions, contextFilePath: "<per-session>")})";

    public static MoonshineV1Transcriber Create(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        MoonshineV1Files v1Files,
        bool preferQnnArtifacts)
    {
        var artifacts = ResolveArtifacts(modelDirectory, v1Files, preferQnnArtifacts);
        var tokenizer = MoonshineTokenizer.Load(artifacts.TokensPath);
        if (activeRuntime == QualcommQnnActiveRuntime.QnnHtp && !artifacts.UsingQnnArtifacts)
        {
            throw new InvalidOperationException(
                "Moonshine v1 QNN requires prepared artifacts under the model folder's qnn subfolder.");
        }

        InferenceSession CreateSession(string sessionName, string modelPath)
        {
            var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
            Directory.CreateDirectory(contextDirectory);
            var contextPath = activeRuntime == QualcommQnnActiveRuntime.QnnHtp
                ? Path.Combine(contextDirectory, $"{sessionName}_ctx.onnx")
                : null;

            using var options = QnnRuntimeSupport.CreateSessionOptions(
                activeRuntime,
                runtimeOptions,
                sessionTag: $"PrimeDictate.{sessionName}",
                contextFilePath: contextPath);
            return new InferenceSession(modelPath, options);
        }

        return new MoonshineV1Transcriber(
            modelDirectory,
            activeRuntime,
            runtimeOptions,
            artifacts,
            CreateSession("MoonshineV1Preprocess", artifacts.StagePaths[0]),
            CreateSession("MoonshineV1Encode", artifacts.StagePaths[1]),
            CreateSession("MoonshineV1UncachedDecode", artifacts.StagePaths[2]),
            CreateSession("MoonshineV1CachedDecode", artifacts.StagePaths[3]),
            tokenizer);
    }

    public string Transcribe(float[] samples)
    {
        var audioTensor = CreateTensor(samples, 1, samples.Length);
        var features = RunSingleOutput(
            this.preprocessSession,
            NamedOnnxValue.CreateFromTensor(this.preprocessSession.InputNames[0], audioTensor));
        var featuresLength = features.Dimensions[1];
        var featuresLengthTensor = CreateTensor(new[] { featuresLength }, 1);
        var encoderOut = RunSingleOutput(
            this.encodeSession,
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[0], features),
            NamedOnnxValue.CreateFromTensor(this.encodeSession.InputNames[1], featuresLengthTensor));

        var tokens = new List<int>();
        var seqLen = 1;
        var currentToken = this.tokenizer.SosTokenId;

        var decodeResult = RunDecoder(this.uncachedDecodeSession, currentToken, seqLen, encoderOut, states: null);
        var maxLen = Math.Max(1, (int)Math.Ceiling(encoderOut.Dimensions[1] * 384d / 16_000d * 6d));

        for (var i = 0; i < maxLen; i++)
        {
            var nextToken = ArgMax(decodeResult.Logits);
            if (nextToken == this.tokenizer.EosTokenId)
            {
                break;
            }

            tokens.Add(nextToken);
            seqLen += 1;
            decodeResult = RunDecoder(this.cachedDecodeSession, nextToken, seqLen, encoderOut, decodeResult.States);
        }

        return this.tokenizer.Decode(tokens);
    }

    public void Dispose()
    {
        this.cachedDecodeSession.Dispose();
        this.uncachedDecodeSession.Dispose();
        this.encodeSession.Dispose();
        this.preprocessSession.Dispose();
    }

    private static MoonshineOrtArtifacts ResolveArtifacts(string modelDirectory, MoonshineV1Files v1Files, bool preferQnnArtifacts)
    {
        var qnnDirectory = Path.Combine(modelDirectory, "qnn");
        var useQnnDirectory = preferQnnArtifacts && Directory.Exists(qnnDirectory);

        var stages = new[] { "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx" };
        var stagePaths = new List<string>(stages.Length);
        var usingQnnArtifacts = true;

        var v1FilePaths = new[] { v1Files.Preprocess, v1Files.Encode, v1Files.Uncached, v1Files.Cached };

        for (int i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];
            var qnnName = stage.Replace(".onnx", ".qdq.onnx", StringComparison.Ordinal);
            var qnnAltName = stage.Replace(".onnx", ".qnn.onnx", StringComparison.Ordinal);
            var qnnPath = useQnnDirectory ? ResolveOptionalQnnArtifact(qnnDirectory, qnnName, qnnAltName) : null;

            if (qnnPath != null)
            {
                stagePaths.Add(qnnPath);
            }
            else
            {
                stagePaths.Add(v1FilePaths[i]);
                usingQnnArtifacts = false;
            }
        }

        return new MoonshineOrtArtifacts(modelDirectory, stagePaths, v1Files.Tokens, usingQnnArtifacts);
    }

    private static string? ResolveOptionalQnnArtifact(string qnnDirectory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var candidate = Path.Combine(qnnDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private DecoderStepResult RunDecoder(
        InferenceSession session,
        int token,
        int tokenLength,
        DenseTensor<float> encoderOut,
        IReadOnlyList<DenseTensor<float>>? states)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], CreateTensor(new[] { token }, 1, 1)),
            NamedOnnxValue.CreateFromTensor(session.InputNames[1], encoderOut),
            NamedOnnxValue.CreateFromTensor(session.InputNames[2], CreateTensor(new[] { tokenLength }, 1))
        };

        if (states is not null)
        {
            for (var i = 3; i < session.InputNames.Count && (i - 3) < states.Count; i++)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(session.InputNames[i], states[i - 3]));
            }
        }

        using var outputs = session.Run(inputs, session.OutputNames);
        var logits = CopyFloatTensor(outputs[0]);
        var nextStates = new List<DenseTensor<float>>(Math.Max(0, outputs.Count - 1));
        for (var i = 1; i < outputs.Count; i++)
        {
            nextStates.Add(CopyFloatTensor(outputs[i]));
        }

        return new DecoderStepResult(logits, nextStates);
    }

    private static DenseTensor<float> RunSingleOutput(InferenceSession session, params NamedOnnxValue[] inputs)
    {
        using var outputs = session.Run(inputs, [session.OutputNames[0]]);
        return CopyFloatTensor(outputs[0]);
    }

    private static DenseTensor<float> CopyFloatTensor(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<float>();
        return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static DenseTensor<float> CreateTensor(float[] values, params int[] dimensions) =>
        new DenseTensor<float>(values, dimensions);

    private static DenseTensor<int> CreateTensor(int[] values, params int[] dimensions) =>
        new DenseTensor<int>(values, dimensions);

    private static int ArgMax(DenseTensor<float> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty) return 0;
        var bestIndex = 0;
        var bestValue = span[0];
        for (var i = 1; i < span.Length; i++)
        {
            if (span[i] > bestValue)
            {
                bestValue = span[i];
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private sealed record DecoderStepResult(DenseTensor<float> Logits, IReadOnlyList<DenseTensor<float>> States);
}

internal sealed class MoonshineTokenizer
{
    private readonly Dictionary<int, string> idToToken;

    private MoonshineTokenizer(Dictionary<int, string> idToToken, int sosTokenId, int eosTokenId)
    {
        this.idToToken = idToToken;
        this.SosTokenId = sosTokenId;
        this.EosTokenId = eosTokenId;
    }

    public int SosTokenId { get; }

    public int EosTokenId { get; }

    public static MoonshineTokenizer Load(string tokensPath)
    {
        var idToToken = new Dictionary<int, string>();
        int? sos = null;
        int? eos = null;
        var decodeBase64Tokens = ShouldDecodeBase64Tokens(tokensPath);

        foreach (var line in File.ReadLines(tokensPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedLine = line.TrimEnd();
            var separatorIndex = trimmedLine.LastIndexOfAny(['\t', ' ']);
            if (separatorIndex <= 0 ||
                !int.TryParse(trimmedLine[(separatorIndex + 1)..].Trim(), out var tokenId))
            {
                continue;
            }

            var tokenText = trimmedLine[..separatorIndex];
            var token = decodeBase64Tokens ? DecodeBase64TokenText(tokenText) : tokenText;
            idToToken[tokenId] = token;
            if (string.Equals(token, "<s>", StringComparison.Ordinal))
            {
                sos = tokenId;
            }
            else if (string.Equals(token, "</s>", StringComparison.Ordinal))
            {
                eos = tokenId;
            }
        }

        if (sos is null || eos is null)
        {
            throw new InvalidOperationException("Moonshine tokens.txt is missing the <s> or </s> token required for greedy decoding.");
        }

        return new MoonshineTokenizer(idToToken, sos.Value, eos.Value);
    }

    private static bool ShouldDecodeBase64Tokens(string tokensPath)
    {
        foreach (var line in File.ReadLines(tokensPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedLine = line.TrimEnd();
            var separatorIndex = trimmedLine.LastIndexOfAny(['\t', ' ']);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var token = DecodeBase64TokenText(trimmedLine[..separatorIndex]);
            return string.Equals(token, "<unk>", StringComparison.Ordinal) ||
                string.Equals(token, "<s>", StringComparison.Ordinal) ||
                string.Equals(token, "</s>", StringComparison.Ordinal);
        }

        return false;
    }

    private static string DecodeBase64TokenText(string token)
    {
        try
        {
            var bytes = Convert.FromBase64String(token);
            var decoded = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrEmpty(decoded) ? token : decoded;
        }
        catch (FormatException)
        {
            return token;
        }
        catch (ArgumentException)
        {
            return token;
        }
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var pieces = new List<string>();
        foreach (var tokenId in tokenIds)
        {
            if (this.idToToken.TryGetValue(tokenId, out var token))
            {
                if (string.Equals(token, "<s>", StringComparison.Ordinal) ||
                    string.Equals(token, "</s>", StringComparison.Ordinal))
                {
                    continue;
                }

                pieces.Add(token);
            }
        }

        return string.Concat(pieces).Replace("\u2581", " ", StringComparison.Ordinal).Trim();
    }
}

internal sealed class MoonshineV2Transcriber : IMoonshineTranscriber
{
    private const int SampleRate = 16_000;
    private const int MaxSamplesPerWindow = SampleRate * 8;
    private const double MaxDecoderTokensPerAudioSecond = 24d;
    private const int DecoderTokenAllowance = 32;
    private const int MaxDecoderTokens = 768;

    private readonly InferenceSession encoderSession;
    private readonly InferenceSession decoderSession;
    private readonly MoonshineTokenizer tokenizer;
    private readonly MoonshineOrtArtifacts artifacts;

    private MoonshineV2Transcriber(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        MoonshineOrtArtifacts artifacts,
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        MoonshineTokenizer tokenizer)
    {
        this.ModelDirectory = modelDirectory;
        this.ActiveRuntime = activeRuntime;
        this.RuntimeOptions = runtimeOptions;
        this.artifacts = artifacts;
        this.encoderSession = encoderSession;
        this.decoderSession = decoderSession;
        this.tokenizer = tokenizer;
    }

    public string ModelDirectory { get; }
    public QualcommQnnActiveRuntime ActiveRuntime { get; }
    public QualcommQnnRuntimeOptions RuntimeOptions { get; }
    public bool StrictValidation => this.RuntimeOptions.StrictValidation;

    public string DiagnosticsSummary =>
        $"activeRuntime={this.ActiveRuntime}, version=v2, artifacts=({this.artifacts.Describe()}), runtimePlan=({QnnRuntimeSupport.DescribeRuntimePlan(this.ActiveRuntime, this.RuntimeOptions, contextFilePath: "<per-session>")})";

    public static MoonshineV2Transcriber Create(
        string modelDirectory,
        QualcommQnnActiveRuntime activeRuntime,
        QualcommQnnRuntimeOptions runtimeOptions,
        MoonshineV2Files v2Files,
        bool preferQnnArtifacts)
    {
        var artifacts = ResolveArtifacts(modelDirectory, v2Files, preferQnnArtifacts);
        var tokenizer = MoonshineTokenizer.Load(artifacts.TokensPath);
        if (activeRuntime == QualcommQnnActiveRuntime.QnnHtp && !artifacts.UsingQnnArtifacts)
        {
            throw new InvalidOperationException(
                "Moonshine v2 QNN requires qnn/encoder.qdq.onnx and qnn/decoder.qdq.onnx. The stock encoder_model.ort and decoder_model_merged.ort files are not QNN-ready.");
        }

        InferenceSession CreateSession(string sessionName, string modelPath)
        {
            var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
            Directory.CreateDirectory(contextDirectory);
            var contextPath = activeRuntime == QualcommQnnActiveRuntime.QnnHtp
                ? Path.Combine(contextDirectory, $"{sessionName}_ctx.onnx")
                : null;

            using var options = QnnRuntimeSupport.CreateSessionOptions(
                activeRuntime,
                runtimeOptions,
                sessionTag: $"PrimeDictate.{sessionName}",
                contextFilePath: contextPath);
            return new InferenceSession(modelPath, options);
        }

        return new MoonshineV2Transcriber(
            modelDirectory,
            activeRuntime,
            runtimeOptions,
            artifacts,
            CreateSession("MoonshineV2Encoder", artifacts.StagePaths[0]),
            CreateSession("MoonshineV2Decoder", artifacts.StagePaths[1]),
            tokenizer);
    }

    public string Transcribe(float[] samples)
    {
        if (samples.Length <= MaxSamplesPerWindow)
        {
            return this.TranscribeWindow(samples);
        }

        var transcript = new StringBuilder();
        for (var offset = 0; offset < samples.Length; offset += MaxSamplesPerWindow)
        {
            var chunkLength = Math.Min(MaxSamplesPerWindow, samples.Length - offset);
            var chunk = new float[chunkLength];
            Array.Copy(samples, offset, chunk, 0, chunkLength);

            var chunkTranscript = this.TranscribeWindow(chunk);
            AppendWindowTranscript(transcript, chunkTranscript);
        }

        return transcript.ToString().Trim();
    }

    private string TranscribeWindow(float[] samples)
    {
        var audioInputName = ResolveInputName(
            this.encoderSession,
            ["input_values", "audio", "input"]);
        var audioMaskName = ResolveInputName(
            this.encoderSession,
            ["attention_mask", "mask"],
            required: false);

        var encoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(audioInputName, CreateTensor(samples, 1, samples.Length))
        };

        if (audioMaskName is not null)
        {
            encoderInputs.Add(NamedOnnxValue.CreateFromTensor(
                audioMaskName,
                CreateLongTensor(CreateFilledLongArray(samples.Length), 1, samples.Length)));
        }

        using var encoderOutputs = this.encoderSession.Run(encoderInputs);
        var encoderOut = CopyFloatTensor(encoderOutputs[0]);

        var encoderMaskName = ResolveInputName(this.decoderSession, ["encoder_attention_mask", "encoder_mask"]);
        var inputIdsName = ResolveInputName(this.decoderSession, ["input_ids", "token"]);
        var encoderHiddenName = ResolveInputName(this.decoderSession, ["encoder_hidden_states", "encoder_out", "hidden"]);
        var useCacheBranchName = ResolveInputName(
            this.decoderSession,
            ["use_cache_branch"],
            required: false);
        var pastInputNames = this.decoderSession.InputNames
            .Where(name => name.StartsWith("past_key_values.", StringComparison.Ordinal))
            .ToArray();

        var encoderFrames = encoderOut.Dimensions[1];
        var encoderMask = CreateLongTensor(CreateFilledLongArray(encoderFrames), 1, encoderFrames);
        var stateByInputName = this.CreateInitialPastStates(pastInputNames);
        Dictionary<string, DenseTensor<float>>? encoderCacheByInputName = null;

        var tokens = new List<int>();
        var currentToken = this.tokenizer.SosTokenId;
        var maxLen = Math.Clamp(
            (int)Math.Ceiling(samples.Length / (double)SampleRate * MaxDecoderTokensPerAudioSecond) + DecoderTokenAllowance,
            DecoderTokenAllowance,
            MaxDecoderTokens);
        var reachedDecoderLimit = true;

        for (var i = 0; i < maxLen; i++)
        {
            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(encoderMaskName, encoderMask),
                NamedOnnxValue.CreateFromTensor(inputIdsName, CreateInt64Tensor([(long)currentToken], 1, 1)),
                NamedOnnxValue.CreateFromTensor(encoderHiddenName, encoderOut)
            };

            foreach (var pastInputName in pastInputNames)
            {
                decoderInputs.Add(NamedOnnxValue.CreateFromTensor(pastInputName, stateByInputName[pastInputName]));
            }

            if (useCacheBranchName is not null)
            {
                decoderInputs.Add(NamedOnnxValue.CreateFromTensor(
                    useCacheBranchName,
                    CreateBoolTensor([i > 0], 1)));
            }

            using var outputs = this.decoderSession.Run(decoderInputs);
            var logits = CopyFloatTensor(outputs[0]);
            var nextToken = ArgMaxLastDimension(logits);

            if (nextToken == this.tokenizer.EosTokenId)
            {
                reachedDecoderLimit = false;
                break;
            }

            tokens.Add(nextToken);
            currentToken = nextToken;

            var nextStateByInputName = new Dictionary<string, DenseTensor<float>>(StringComparer.Ordinal);
            for (var outputIndex = 1; outputIndex < outputs.Count; outputIndex++)
            {
                var stateInputName = ToPastInputName(outputs[outputIndex].Name);
                if (stateInputName is not null && pastInputNames.Contains(stateInputName))
                {
                    nextStateByInputName[stateInputName] = CopyFloatTensor(outputs[outputIndex]);
                }
            }

            if (encoderCacheByInputName is null)
            {
                encoderCacheByInputName = nextStateByInputName
                    .Where(item => item.Key.Contains(".encoder.", StringComparison.Ordinal))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            }
            else
            {
                foreach (var item in encoderCacheByInputName)
                {
                    nextStateByInputName[item.Key] = item.Value;
                }
            }

            foreach (var pastInputName in pastInputNames)
            {
                if (!nextStateByInputName.ContainsKey(pastInputName) &&
                    stateByInputName.TryGetValue(pastInputName, out var previousState))
                {
                    nextStateByInputName[pastInputName] = previousState;
                }
            }

            stateByInputName = nextStateByInputName;
        }

        if (reachedDecoderLimit)
        {
            AppLog.Info(
                $"Moonshine v2 decoder reached its token budget ({maxLen:N0} tokens for {samples.Length / (double)SampleRate:N2}s audio).");
        }

        return this.tokenizer.Decode(tokens);
    }

    private static void AppendWindowTranscript(StringBuilder transcript, string chunkTranscript)
    {
        if (string.IsNullOrWhiteSpace(chunkTranscript))
        {
            return;
        }

        if (transcript.Length > 0)
        {
            transcript.Append(' ');
        }

        transcript.Append(chunkTranscript.Trim());
    }

    public void Dispose()
    {
        this.decoderSession.Dispose();
        this.encoderSession.Dispose();
    }

    private static MoonshineOrtArtifacts ResolveArtifacts(string modelDirectory, MoonshineV2Files v2Files, bool preferQnnArtifacts)
    {
        var qnnDirectory = Path.Combine(modelDirectory, "qnn");
        var useQnnDirectory = preferQnnArtifacts && Directory.Exists(qnnDirectory);

        var encoderName = Path.GetFileName(v2Files.Encoder);
        var decoderName = Path.GetFileName(v2Files.Decoder);

        var qnnEncoder = useQnnDirectory
            ? ResolveOptionalQnnArtifact(
                qnnDirectory,
                "encoder.qdq.onnx",
                "encoder.qnn.onnx",
                encoderName.Replace(".onnx", ".qdq.onnx", StringComparison.Ordinal).Replace(".ort", ".qdq.onnx", StringComparison.Ordinal),
                encoderName.Replace(".onnx", ".qnn.onnx", StringComparison.Ordinal).Replace(".ort", ".qnn.onnx", StringComparison.Ordinal))
            : null;
        var qnnDecoder = useQnnDirectory
            ? ResolveOptionalQnnArtifact(
                qnnDirectory,
                "decoder.qdq.onnx",
                "decoder.qnn.onnx",
                decoderName.Replace(".onnx", ".qdq.onnx", StringComparison.Ordinal).Replace(".ort", ".qdq.onnx", StringComparison.Ordinal),
                decoderName.Replace(".onnx", ".qnn.onnx", StringComparison.Ordinal).Replace(".ort", ".qnn.onnx", StringComparison.Ordinal))
            : null;

        var usingQnnArtifacts = useQnnDirectory && qnnEncoder != null && qnnDecoder != null;
        var stagePaths = new[]
        {
            qnnEncoder ?? v2Files.Encoder,
            qnnDecoder ?? v2Files.Decoder
        };

        return new MoonshineOrtArtifacts(modelDirectory, stagePaths, v2Files.Tokens, usingQnnArtifacts);
    }

    private static string? ResolveOptionalQnnArtifact(string qnnDirectory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var candidate = Path.Combine(qnnDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static DenseTensor<float> CopyFloatTensor(DisposableNamedOnnxValue value)
    {
        var tensor = value.AsTensor<float>();
        return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static DenseTensor<float> CreateTensor(float[] values, params int[] dimensions) =>
        new DenseTensor<float>(values, dimensions);

    private static DenseTensor<long> CreateLongTensor(long[] values, params int[] dimensions) =>
        new DenseTensor<long>(values, dimensions);

    private static DenseTensor<long> CreateInt64Tensor(long[] values, params int[] dimensions) =>
        new DenseTensor<long>(values, dimensions);

    private static DenseTensor<bool> CreateBoolTensor(bool[] values, params int[] dimensions) =>
        new DenseTensor<bool>(values, dimensions);

    private static long[] CreateFilledLongArray(int length)
    {
        var values = new long[length];
        Array.Fill(values, 1L);
        return values;
    }

    private Dictionary<string, DenseTensor<float>> CreateInitialPastStates(IEnumerable<string> inputNames)
    {
        var states = new Dictionary<string, DenseTensor<float>>(StringComparer.Ordinal);
        foreach (var inputName in inputNames)
        {
            var numHeads = this.GetInputDimensionOrDefault(inputName, 1, 8);
            var headDim = this.GetInputDimensionOrDefault(inputName, 3, 36);
            states[inputName] = new DenseTensor<float>(new float[0], [1, numHeads, 0, headDim]);
        }

        return states;
    }

    private int GetInputDimensionOrDefault(string inputName, int dimensionIndex, int fallback)
    {
        if (this.decoderSession.InputMetadata.TryGetValue(inputName, out var metadata) &&
            metadata.Dimensions.Length > dimensionIndex &&
            metadata.Dimensions[dimensionIndex] > 0)
        {
            return metadata.Dimensions[dimensionIndex];
        }

        return fallback;
    }

    private static string ResolveInputName(
        InferenceSession session,
        IReadOnlyList<string> candidates,
        bool required = true)
    {
        foreach (var candidate in candidates)
        {
            var exactMatch = session.InputNames.FirstOrDefault(name =>
                string.Equals(name, candidate, StringComparison.Ordinal));
            if (exactMatch is not null)
            {
                return exactMatch;
            }
        }

        foreach (var candidate in candidates)
        {
            var containsMatch = session.InputNames.FirstOrDefault(name =>
                name.Contains(candidate, StringComparison.Ordinal));
            if (containsMatch is not null)
            {
                return containsMatch;
            }
        }

        if (!required)
        {
            return null!;
        }

        throw new InvalidOperationException(
            $"Moonshine v2 model input mismatch. Expected one of [{string.Join(", ", candidates)}], but model inputs are [{string.Join(", ", session.InputNames)}].");
    }

    private static string? ToPastInputName(string outputName)
    {
        const string prefix = "present.";
        return outputName.StartsWith(prefix, StringComparison.Ordinal)
            ? "past_key_values." + outputName[prefix.Length..]
            : null;
    }

    private static int ArgMaxLastDimension(DenseTensor<float> logits)
    {
        var span = logits.Buffer.Span;
        if (span.IsEmpty) return 0;

        var vocabularySize = logits.Dimensions[^1];
        var start = Math.Max(0, span.Length - vocabularySize);
        var bestIndex = 0;
        var bestValue = span[start];
        for (var i = 1; i < vocabularySize; i++)
        {
            var candidate = span[start + i];
            if (candidate > bestValue)
            {
                bestValue = candidate;
                bestIndex = i;
            }
        }
        return bestIndex;
    }
}
