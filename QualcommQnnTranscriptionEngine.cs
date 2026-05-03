using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;

namespace PrimeDictate;

internal sealed class QualcommQnnTranscriptionEngine : ITranscriptionEngine
{
    private readonly MoonshineTranscriptionEngine moonshine = new();
    private readonly QualcommAihubWhisperTranscriptionEngine whisper = new();

    public TranscriptionBackendKind Backend => TranscriptionBackendKind.QualcommQnn;

    public string Name => "Qualcomm QNN (Experimental)";

    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        TranscriptionEngineConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (ShouldUseQualcommAihubWhisper(configuration))
        {
            var forcedConfiguration = configuration with { ComputeInterface = TranscriptionComputeInterface.Npu };
            return await this.whisper.TranscribeAsync(audio, forcedConfiguration, cancellationToken).ConfigureAwait(false);
        }

        var moonshineConfiguration = configuration with { ComputeInterface = TranscriptionComputeInterface.Npu };
        return await this.moonshine.TranscribeAsync(audio, moonshineConfiguration, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.whisper.DisposeAsync().ConfigureAwait(false);
        await this.moonshine.DisposeAsync().ConfigureAwait(false);
    }

    // Validation helpers for the smoke test harness
    public static string RunBackendSmokeValidation(string modelDirectory, string computeInterface)
    {
        return QualcommQnnValidationHarness.RunBackendSmokeValidation(modelDirectory, computeInterface);
    }

    private static bool ShouldUseQualcommAihubWhisper(TranscriptionEngineConfiguration configuration)
    {
        if (QualcommAihubWhisperModelCatalog.TryGetById(configuration.SelectedModelId, out _))
        {
            return true;
        }

        if (QualcommAihubWhisperModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out _) ||
            QualcommAihubWhisperModelCatalog.IsRawContextOnlyDirectory(configuration.ConfiguredModelPath))
        {
            return true;
        }

        return false;
    }
}

internal static class QualcommQnnValidationHarness
{
    public static string RunBackendSmokeValidation(string modelDirectory, string computeInterface)
    {
        var normalizedModelDirectory = Path.GetFullPath(modelDirectory);
        var requestedRuntime = string.Equals(computeInterface, TranscriptionComputeInterface.Npu.ToString(), StringComparison.OrdinalIgnoreCase)
            ? QualcommQnnActiveRuntime.QnnHtp
            : QualcommQnnActiveRuntime.Cpu;
        var strictValidation = requestedRuntime == QualcommQnnActiveRuntime.QnnHtp;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "QualcommQnn",
            ["requestedRuntime"] = requestedRuntime.ToString(),
            ["modelDirectory"] = normalizedModelDirectory,
            ["strictValidation"] = strictValidation,
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(normalizedModelDirectory, strictValidationOverride: strictValidation);
            using var transcriber = MoonshineTranscriberFactory.Create(
                normalizedModelDirectory,
                requestedRuntime,
                runtimeOptions,
                preferQnnArtifacts: requestedRuntime == QualcommQnnActiveRuntime.QnnHtp);

            var silence = new float[16_000];
            var transcript = transcriber.Transcribe(silence);
            result["runtime"] = transcriber.ActiveRuntime.ToString();
            result["diagnostics"] = transcriber.DiagnosticsSummary;
            result["runSucceeded"] = true;
            result["transcriptLength"] = transcript.Length;
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string RunSmokeValidation(string modelDirectory, bool strictValidation)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestedBackend"] = "QualcommQnn",
            ["modelDirectory"] = Path.GetFullPath(modelDirectory),
            ["strictValidation"] = strictValidation,
            ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
            ["baseDirectory"] = AppContext.BaseDirectory
        };

        try
        {
            var runtimeOptions = QnnRuntimeSupport.GetRuntimeOptions(modelDirectory, strictValidationOverride: strictValidation);
            using var transcriber = MoonshineTranscriberFactory.Create(
                modelDirectory,
                QualcommQnnActiveRuntime.QnnHtp,
                runtimeOptions,
                preferQnnArtifacts: true);

            result["runtime"] = transcriber.ActiveRuntime.ToString();
            result["diagnostics"] = transcriber.DiagnosticsSummary;

            var silence = new float[16_000];
            var transcript = transcriber.Transcribe(silence);
            result["runSucceeded"] = true;
            result["transcriptLength"] = transcript.Length;
            result["proofStatus"] = transcriber.ActiveRuntime == QualcommQnnActiveRuntime.QnnHtp && strictValidation
                ? "QNN HTP strict validation passed for session creation and inference run."
                : "QNN HTP was not strictly validated.";
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP strict validation failed.";
            result["error"] = ex.ToString();
            result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
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
