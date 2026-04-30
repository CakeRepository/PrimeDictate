using System.IO;

namespace PrimeDictate;

internal sealed record TranscriptionComputeChoice(
    TranscriptionComputeInterface Kind,
    string Label,
    string Description)
{
    public override string ToString() => this.Label;
}

internal readonly record struct TranscriptionConfigurationSelection(
    TranscriptionBackendKind Backend,
    TranscriptionComputeInterface ComputeInterface,
    string? SelectedModelId,
    string? ModelPath);

internal static class TranscriptionRuntimeSupport
{
    public static IReadOnlyList<TranscriptionComputeChoice> GetComputeChoices(
        TranscriptionBackendKind backend,
        string? selectedModelId,
        string? configuredModelPath)
    {
        var choices = new List<TranscriptionComputeChoice>
        {
            new(
                TranscriptionComputeInterface.Cpu,
                "CPU (most compatible)",
                "Runs transcription on the CPU. This is the fallback for every backend.")
        };

        if (backend == TranscriptionBackendKind.WhisperNet)
        {
            if (PlatformSupport.SupportsWhisperNetGpu)
            {
                choices.Add(new TranscriptionComputeChoice(
                    TranscriptionComputeInterface.Gpu,
                    $"GPU ({PlatformSupport.WhisperNetGpuRuntimeLabel})",
                    "Runs Whisper.net through the fastest detected GPU runtime before falling back to CPU."));
            }

            if (CanUseWhisperNetNpu(selectedModelId, configuredModelPath))
            {
                choices.Add(new TranscriptionComputeChoice(
                    TranscriptionComputeInterface.Npu,
                    "NPU (OpenVINO)",
                    "Runs the Whisper encoder through OpenVINO NPU sidecars for supported GGML models."));
            }
        }
        else if (backend == TranscriptionBackendKind.QualcommQnn &&
            PlatformSupport.SupportsQualcommQnnHtp)
        {
            choices.Add(new TranscriptionComputeChoice(
                TranscriptionComputeInterface.Npu,
                "NPU (Qualcomm QNN HTP)",
                "Runs the Moonshine stages through ONNX Runtime QNN HTP with CPU fallback disabled for QNN sessions."));
        }

        return choices;
    }

    public static bool IsBackendSupportedOnCurrentMachine(TranscriptionBackendKind backend) =>
        backend != TranscriptionBackendKind.QualcommQnn ||
        PlatformSupport.ShouldOfferQualcommQnnBackend;

    public static bool IsComputeSupported(
        TranscriptionBackendKind backend,
        TranscriptionComputeInterface computeInterface,
        string? selectedModelId,
        string? configuredModelPath) =>
        GetComputeChoices(backend, selectedModelId, configuredModelPath)
            .Any(choice => choice.Kind == computeInterface);

    public static TranscriptionComputeInterface GetBestComputeInterface(
        TranscriptionBackendKind backend,
        string? selectedModelId,
        string? configuredModelPath)
    {
        var choices = GetComputeChoices(backend, selectedModelId, configuredModelPath);
        return choices.FirstOrDefault(choice => choice.Kind == TranscriptionComputeInterface.Gpu)?.Kind
            ?? choices.FirstOrDefault(choice => choice.Kind == TranscriptionComputeInterface.Npu)?.Kind
            ?? TranscriptionComputeInterface.Cpu;
    }

    public static bool NormalizeSettingsForCurrentMachine(AppSettings settings)
    {
        var changed = false;
        var requestedComputeInterface = settings.TranscriptionComputeInterface;

        if (!IsBackendSupportedOnCurrentMachine(settings.TranscriptionBackend))
        {
            if (requestedComputeInterface != TranscriptionComputeInterface.Cpu &&
                TryFindBestInstalledHardwareConfiguration(out var backendHardwareSelection))
            {
                settings.TranscriptionBackend = backendHardwareSelection.Backend;
                settings.TranscriptionComputeInterface = backendHardwareSelection.ComputeInterface;
                settings.SelectedModelId = backendHardwareSelection.SelectedModelId;
                settings.ModelPath = backendHardwareSelection.ModelPath;
                return true;
            }

            if (settings.TranscriptionBackend == TranscriptionBackendKind.QualcommQnn)
            {
                settings.TranscriptionBackend = TranscriptionBackendKind.Moonshine;
                settings.TranscriptionComputeInterface = TranscriptionComputeInterface.Cpu;
                changed = true;
            }
            else
            {
                settings.TranscriptionBackend = TranscriptionBackendKind.Whisper;
                settings.TranscriptionComputeInterface = TranscriptionComputeInterface.Cpu;
                settings.SelectedModelId = null;
                settings.ModelPath = null;
                changed = true;
            }
        }

        if (IsComputeSupported(
                settings.TranscriptionBackend,
                settings.TranscriptionComputeInterface,
                settings.SelectedModelId,
                settings.ModelPath))
        {
            return changed;
        }

        if (settings.TranscriptionComputeInterface != TranscriptionComputeInterface.Cpu &&
            TryFindBestInstalledHardwareConfiguration(out var hardwareSelection))
        {
            settings.TranscriptionBackend = hardwareSelection.Backend;
            settings.TranscriptionComputeInterface = hardwareSelection.ComputeInterface;
            settings.SelectedModelId = hardwareSelection.SelectedModelId;
            settings.ModelPath = hardwareSelection.ModelPath;
            return true;
        }

        settings.TranscriptionComputeInterface = GetBestComputeInterface(
            settings.TranscriptionBackend,
            settings.SelectedModelId,
            settings.ModelPath);
        return true;
    }

    public static bool TryFindBestInstalledHardwareConfiguration(
        out TranscriptionConfigurationSelection selection)
    {
        if (PlatformSupport.SupportsWhisperNetGpu)
        {
            foreach (var option in WhisperNetModelCatalog.Options)
            {
                if (WhisperNetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
                {
                    selection = new TranscriptionConfigurationSelection(
                        TranscriptionBackendKind.WhisperNet,
                        TranscriptionComputeInterface.Gpu,
                        option.Id,
                        installedPath);
                    return true;
                }
            }
        }

        if (PlatformSupport.SupportsWhisperNetOpenVino)
        {
            foreach (var option in WhisperNetModelCatalog.Options)
            {
                if (WhisperNetModelCatalog.TryResolveInstalledArtifacts(option, out var artifacts) &&
                    artifacts.Value.HasOpenVinoSidecars)
                {
                    selection = new TranscriptionConfigurationSelection(
                        TranscriptionBackendKind.WhisperNet,
                        TranscriptionComputeInterface.Npu,
                        option.Id,
                        artifacts.Value.ModelPath);
                    return true;
                }
            }
        }

        if (PlatformSupport.SupportsQualcommQnnHtp)
        {
            foreach (var option in MoonshineModelCatalog.Options)
            {
                if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
                {
                    selection = new TranscriptionConfigurationSelection(
                        TranscriptionBackendKind.QualcommQnn,
                        TranscriptionComputeInterface.Npu,
                        option.Id,
                        installedPath);
                    return true;
                }
            }
        }

        selection = default;
        return false;
    }

    private static bool CanUseWhisperNetNpu(string? selectedModelId, string? configuredModelPath)
    {
        if (!PlatformSupport.SupportsWhisperNetOpenVino)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredModelPath) &&
            File.Exists(configuredModelPath))
        {
            var artifacts = CreateWhisperNetArtifacts(configuredModelPath);
            return artifacts.HasOpenVinoSidecars;
        }

        if (WhisperNetModelCatalog.TryGetById(selectedModelId, out var selectedOption))
        {
            return selectedOption.SupportsOpenVinoBundle;
        }

        return WhisperNetModelCatalog.Options.Any(option =>
            option.Recommended &&
            option.SupportsOpenVinoBundle);
    }

    private static WhisperNetModelArtifacts CreateWhisperNetArtifacts(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        return new WhisperNetModelArtifacts(
            Path.GetFullPath(modelPath),
            Path.GetFullPath(Path.Combine(directory, $"{stem}-encoder-openvino.xml")),
            Path.GetFullPath(Path.Combine(directory, $"{stem}-encoder-openvino.bin")));
    }
}
