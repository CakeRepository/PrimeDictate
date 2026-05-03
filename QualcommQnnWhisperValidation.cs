using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace PrimeDictate;

internal sealed record WhisperQnnArtifacts(
    string ModelDirectory,
    string EncoderPath,
    string DecoderPath,
    string TokensPath,
    bool UsingQnnArtifacts)
{
    public string Describe() =>
        $"modelDirectory={this.ModelDirectory}, encoder={Path.GetFileName(this.EncoderPath)}, decoder={Path.GetFileName(this.DecoderPath)}, tokens={Path.GetFileName(this.TokensPath)}, qnnArtifacts={this.UsingQnnArtifacts}";
}

internal static class QualcommQnnWhisperValidationHarness
{
    public static string RunBackendSmokeValidation(string modelDirectory, string computeInterface)
    {
        var requestedRuntime = string.Equals(computeInterface, TranscriptionComputeInterface.Npu.ToString(), StringComparison.OrdinalIgnoreCase)
            ? QualcommQnnActiveRuntime.QnnHtp
            : QualcommQnnActiveRuntime.Cpu;
        var strictValidation = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp;
        return RunValidation(modelDirectory, requestedRuntime, strictValidation);
    }

    public static string RunSmokeValidation(string modelDirectory, bool strictValidation)
    {
        return RunValidation(modelDirectory, QualcommQnnActiveRuntime.QnnHtp, strictValidation);
    }

    public static string RunAihubWavTranscription(string modelDirectory, string wavFilePath)
    {
        var normalizedModelDirectory = Path.GetFullPath(modelDirectory);
        var normalizedWavFilePath = Path.GetFullPath(wavFilePath);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "Qualcomm AI Hub Whisper QNN",
            ["requestedRuntime"] = QualcommQnnActiveRuntime.QnnHtp.ToString(),
            ["modelDirectory"] = normalizedModelDirectory,
            ["wavFile"] = normalizedWavFilePath,
            ["strictValidation"] = true,
            ["validationScope"] = "full-precompiled-qnn-onnx-transcription",
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory
        };

        try
        {
            if (!QualcommAihubWhisperModelCatalog.TryResolveArtifacts(normalizedModelDirectory, out var artifacts))
            {
                throw new FileNotFoundException(
                    "Qualcomm AI Hub Whisper model folder is incomplete. Expected encoder.onnx, decoder.onnx, context binaries, metadata.json, and multilingual.tiktoken.");
            }

            var samples = LoadPcm16KhzMonoWav(normalizedWavFilePath, out var audioDuration, out var sourceFormat);
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(normalizedModelDirectory, strictValidationOverride: true) with
            {
                EnableContextCache = false
            };

            using var transcriber = QualcommAihubWhisperTranscriber.Create(normalizedModelDirectory, runtimeOptions);
            var stopwatch = Stopwatch.StartNew();
            var transcript = transcriber.Transcribe(samples, CancellationToken.None);
            stopwatch.Stop();

            result["artifacts"] = new
            {
                artifacts.Value.ModelDirectory,
                Encoder = Path.GetFileName(artifacts.Value.EncoderOnnxPath),
                Decoder = Path.GetFileName(artifacts.Value.DecoderOnnxPath),
                EncoderContext = Path.GetFileName(artifacts.Value.EncoderContextPath),
                DecoderContext = Path.GetFileName(artifacts.Value.DecoderContextPath)
            };
            result["sourceFormat"] = sourceFormat;
            result["audioSeconds"] = audioDuration.TotalSeconds;
            result["elapsedSeconds"] = stopwatch.Elapsed.TotalSeconds;
            result["transcript"] = transcript;
            result["runSucceeded"] = !string.IsNullOrWhiteSpace(transcript);
            result["proofStatus"] = string.IsNullOrWhiteSpace(transcript)
                ? "QNN HTP transcription ran but returned no text."
                : "QNN HTP transcription produced text.";
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP transcription validation failed.";
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RunValidation(
        string modelDirectory,
        QualcommQnnActiveRuntime requestedRuntime,
        bool strictValidation)
    {
        var normalizedModelDirectory = Path.GetFullPath(modelDirectory);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "Whisper ONNX QNN Probe",
            ["requestedRuntime"] = requestedRuntime.ToString(),
            ["modelDirectory"] = normalizedModelDirectory,
            ["strictValidation"] = strictValidation,
            ["validationScope"] = "session-creation-only",
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["limitations"] = "This harness validates direct encoder/decoder session creation only. It does not run Whisper feature extraction or autoregressive decoding in managed code."
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(normalizedModelDirectory, strictValidationOverride: strictValidation) with
            {
                EnableContextCache = false
            };

            if (QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(normalizedModelDirectory))
            {
                throw new InvalidOperationException(
                    "This folder contains a raw qnn_context_binary package only. ONNX Runtime needs the matching precompiled_qnn_onnx package with encoder.onnx and decoder.onnx EPContext wrappers.");
            }

            if (QualcommAihubWhisperModelCatalog.TryResolveArtifacts(normalizedModelDirectory, out var qaihubArtifacts))
            {
                if (requestedRuntime != QualcommQnnActiveRuntime.QnnHtp)
                {
                    throw new InvalidOperationException(
                        "Qualcomm AI Hub precompiled_qnn_onnx Whisper packages require the QNN HTP provider; CPU execution is not supported for EPContext wrappers.");
                }

                using var qaihubEncoderSession = CreatePrecompiledQnnSession(
                    "QaihubWhisperEncoder",
                    qaihubArtifacts.Value.EncoderOnnxPath,
                    runtimeOptions);
                using var qaihubDecoderSession = CreatePrecompiledQnnSession(
                    "QaihubWhisperDecoder",
                    qaihubArtifacts.Value.DecoderOnnxPath,
                    runtimeOptions);

                result["requestedBackend"] = "Qualcomm AI Hub Whisper QNN";
                result["validationScope"] = "precompiled-qnn-onnx-session-creation-only";
                result["artifacts"] = new
                {
                    qaihubArtifacts.Value.ModelDirectory,
                    Encoder = Path.GetFileName(qaihubArtifacts.Value.EncoderOnnxPath),
                    Decoder = Path.GetFileName(qaihubArtifacts.Value.DecoderOnnxPath),
                    EncoderContext = Path.GetFileName(qaihubArtifacts.Value.EncoderContextPath),
                    DecoderContext = Path.GetFileName(qaihubArtifacts.Value.DecoderContextPath)
                };
                result["encoderSessionCreated"] = true;
                result["decoderSessionCreated"] = true;
                result["encoderInputs"] = qaihubEncoderSession.InputNames;
                result["encoderOutputs"] = qaihubEncoderSession.OutputNames;
                result["decoderInputs"] = qaihubDecoderSession.InputNames;
                result["decoderOutputs"] = qaihubDecoderSession.OutputNames;
                result["runSucceeded"] = true;
                result["proofStatus"] = "QNN HTP strict validation passed for Qualcomm AI Hub Whisper EPContext session creation.";
                result["loadedModules"] = CaptureRuntimeModuleSnapshot();
                return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            }

            var artifacts = ResolveArtifacts(normalizedModelDirectory, preferQnnArtifacts: requestedRuntime == QualcommQnnActiveRuntime.QnnHtp);

            using var encoderSession = CreateSession("QnnWhisperEncoder", artifacts.EncoderPath, requestedRuntime, runtimeOptions, normalizedModelDirectory);
            using var decoderSession = CreateSession("QnnWhisperDecoder", artifacts.DecoderPath, requestedRuntime, runtimeOptions, normalizedModelDirectory);

            result["artifacts"] = artifacts.Describe();
            result["encoderSessionCreated"] = true;
            result["decoderSessionCreated"] = true;
            result["encoderInputs"] = encoderSession.InputNames;
            result["encoderOutputs"] = encoderSession.OutputNames;
            result["decoderInputs"] = decoderSession.InputNames;
            result["decoderOutputs"] = decoderSession.OutputNames;
            result["runSucceeded"] = true;
            result["proofStatus"] = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp && strictValidation
                ? "QNN HTP strict validation passed for Whisper encoder/decoder session creation."
                : "Whisper encoder/decoder session creation was not strictly validated on QNN HTP.";
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP strict validation failed for Whisper session creation.";
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static WhisperQnnArtifacts ResolveArtifacts(string modelDirectory, bool preferQnnArtifacts)
    {
        if (!WhisperModelCatalog.TryResolveModelFiles(modelDirectory, out var modelFiles))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder is incomplete. Expected encoder, decoder, and tokens files.");
        }

        var files = modelFiles.Value;
        var qnnDirectory = Path.Combine(modelDirectory, "qnn");
        var qnnEncoder = preferQnnArtifacts ? ResolveOptionalQnnArtifact(qnnDirectory, files.Encoder) : null;
        var qnnDecoder = preferQnnArtifacts ? ResolveOptionalQnnArtifact(qnnDirectory, files.Decoder) : null;

        return new WhisperQnnArtifacts(
            modelDirectory,
            qnnEncoder ?? files.Encoder,
            qnnDecoder ?? files.Decoder,
            files.Tokens,
            qnnEncoder is not null || qnnDecoder is not null);
    }

    private static string? ResolveOptionalQnnArtifact(string qnnDirectory, string baseModelPath)
    {
        if (!Directory.Exists(qnnDirectory))
        {
            return null;
        }

        var baseFileName = Path.GetFileName(baseModelPath);
        var candidates = new[]
        {
            baseFileName,
            baseFileName.Replace(".int8.onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qnn.onnx", StringComparison.OrdinalIgnoreCase)
        };

        foreach (var candidateFileName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidatePath = Path.Combine(qnnDirectory, candidateFileName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static InferenceSession CreateSession(
        string sessionName,
        string modelPath,
        QualcommQnnActiveRuntime runtime,
        QualcommQnnRuntimeOptions runtimeOptions,
        string modelDirectory)
    {
        var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
        Directory.CreateDirectory(contextDirectory);
        var contextPath = runtime == QualcommQnnActiveRuntime.QnnHtp
            ? Path.Combine(contextDirectory, $"{sessionName}_{Guid.NewGuid():N}_ctx.onnx")
            : null;

        using var options = QnnRuntimeSupport.CreateSessionOptions(
            runtime,
            runtimeOptions,
            sessionTag: $"PrimeDictate.{sessionName}",
            contextFilePath: contextPath);
        return new InferenceSession(modelPath, options);
    }

    private static InferenceSession CreatePrecompiledQnnSession(
        string sessionName,
        string modelPath,
        QualcommQnnRuntimeOptions runtimeOptions)
    {
        using var options = QnnRuntimeSupport.CreateSessionOptions(
            QualcommQnnActiveRuntime.QnnHtp,
            runtimeOptions,
            sessionTag: $"PrimeDictate.{sessionName}",
            contextFilePath: null);
        return new InferenceSession(modelPath, options);
    }

    private static float[] LoadPcm16KhzMonoWav(
        string wavFilePath,
        out TimeSpan duration,
        out string sourceFormat)
    {
        using var reader = new WaveFileReader(wavFilePath);
        sourceFormat = reader.WaveFormat.ToString();
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.SampleRate != 16_000 ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException(
                $"Validation WAV must be 16 kHz, 16-bit, mono PCM. Actual format: {sourceFormat}.");
        }

        var bytes = new byte[reader.Length];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = reader.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var samples = new float[totalRead / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(bytes, i * 2);
            samples[i] = sample / 32768f;
        }

        duration = TimeSpan.FromSeconds(samples.Length / 16_000d);
        return samples;
    }

    private static IReadOnlyList<string> CaptureRuntimeModuleSnapshot()
    {
        try
        {
            var snapshot = new List<string>();
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (!module.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                    && !module.ModuleName.Contains("qnn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshot.Add($"{module.ModuleName} => {module.FileName}");
            }

            snapshot.Sort(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new[] { $"<module snapshot unavailable>: {ex.Message}" };
        }
    }
}
