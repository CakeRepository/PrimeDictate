using System.IO;
using System.Runtime.InteropServices;

namespace PrimeDictate;

internal static class PlatformSupport
{
    private static readonly Lazy<bool> WhisperNetOpenVinoSupport = new(() =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        HasWhisperNetRuntimeAsset("openvino"));

    private static readonly Lazy<bool> WhisperNetCudaSupport = new(() =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")) &&
        HasWhisperNetRuntimeAsset("cuda"));

    private static readonly Lazy<bool> WhisperNetVulkanSupport = new(() =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")) &&
        HasWhisperNetRuntimeAsset("vulkan"));

    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    public static bool IsWindowsArm64 =>
        OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static bool SupportsWhisperNetOpenVino =>
        WhisperNetOpenVinoSupport.Value;

    public static bool SupportsWhisperNetCuda =>
        WhisperNetCudaSupport.Value;

    public static bool SupportsWhisperNetVulkan =>
        WhisperNetVulkanSupport.Value;

    public static bool SupportsWhisperNetGpu =>
        SupportsWhisperNetCuda ||
        SupportsWhisperNetVulkan;

    public static bool SupportsQualcommQnnHtp => QnnRuntimeSupport.GetAvailability().SupportsQnnHtp;

    public static bool ShouldOfferQualcommQnnBackend => SupportsQualcommQnnHtp;

    public static string WhisperNetRuntimeSummary => SupportsWhisperNetOpenVino
        ? SupportsWhisperNetGpu
            ? $"Whisper.net GGML can use CPU, {WhisperNetGpuLabel}, and OpenVINO NPU on this machine."
            : "Whisper.net GGML can use CPU and OpenVINO NPU on this machine."
        : SupportsWhisperNetGpu
            ? $"Whisper.net GGML can use CPU and {WhisperNetGpuLabel} on this machine."
            : IsWindowsArm64
                ? "Whisper.net GGML runs natively on ARM64 with CPU. GPU/NPU acceleration currently requires an x64 build."
                : $"Whisper.net GGML can use CPU, but no supported GPU/NPU runtime was detected for {RuntimeInformation.ProcessArchitecture}.";

    public static string QualcommQnnRuntimeSummary => QnnRuntimeSupport.GetAvailability().Summary;

    public static string WhisperNetGpuLabel
    {
        get
        {
            var runtimeLabel = WhisperNetGpuRuntimeLabel;
            return string.Equals(runtimeLabel, "GPU", StringComparison.Ordinal)
                ? "GPU"
                : $"{runtimeLabel} GPU";
        }
    }

    public static string WhisperNetGpuRuntimeLabel
    {
        get
        {
            if (SupportsWhisperNetCuda && SupportsWhisperNetVulkan)
            {
                return "CUDA/Vulkan";
            }

            if (SupportsWhisperNetCuda)
            {
                return "CUDA";
            }

            if (SupportsWhisperNetVulkan)
            {
                return "Vulkan";
            }

            return "GPU";
        }
    }

    private static bool HasWhisperNetRuntimeAsset(string runtimeName)
    {
        var nativeFileName = OperatingSystem.IsWindows() ? "whisper.dll" : "libwhisper.so";
        foreach (var candidateDirectory in EnumerateWhisperNetRuntimeDirectories(runtimeName))
        {
            var candidatePath = Path.Combine(candidateDirectory, nativeFileName);
            if (File.Exists(candidatePath) &&
                TryLoadNativeLibrary(candidatePath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryLoadNativeLibrary(string path)
    {
        try
        {
            var handle = NativeLibrary.Load(path);
            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateWhisperNetRuntimeDirectories(string runtimeName)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeName, "win-x64");
            var assemblyDirectory = Path.GetDirectoryName(typeof(PlatformSupport).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                yield return Path.Combine(assemblyDirectory, "runtimes", runtimeName, "win-x64");
            }

            yield return Path.Combine(Directory.GetCurrentDirectory(), "runtimes", runtimeName, "win-x64");
        }
    }
}
