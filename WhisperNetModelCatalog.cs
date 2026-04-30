using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record WhisperNetModelOption(
    string Id,
    string DisplayName,
    string FileName,
    string Description,
    long ApproximateBytes,
    string? OpenVinoBundleFileName = null,
    bool Recommended = false)
{
    public string DownloadUri =>
        $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{this.FileName}";

    public string? OpenVinoBundleUri =>
        string.IsNullOrWhiteSpace(this.OpenVinoBundleFileName)
            ? null
            : $"https://huggingface.co/Intel/whisper.cpp-openvino-models/resolve/main/{this.OpenVinoBundleFileName}";

    public bool SupportsOpenVinoBundle => !string.IsNullOrWhiteSpace(this.OpenVinoBundleFileName);

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct WhisperNetModelArtifacts(string ModelPath, string OpenVinoXmlPath, string OpenVinoBinPath)
{
    public bool HasOpenVinoSidecars =>
        File.Exists(this.OpenVinoXmlPath) &&
        File.Exists(this.OpenVinoBinPath);
}

internal readonly record struct WhisperNetModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
{
    public double? Percentage => this.Stage is "download" && this.TotalBytes is > 0
        ? Math.Min(100d, this.BytesDownloaded * 100d / this.TotalBytes.Value)
        : null;

    public string ProgressLabel => this.Stage switch
    {
        "download" => this.TotalBytes is > 0
            ? $"{WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)} / {WhisperModelCatalog.FormatByteSize(this.TotalBytes.Value)}"
            : WhisperModelCatalog.FormatByteSize(this.BytesDownloaded),
        "repair-openvino" => "Repairing OpenVINO sidecars",
        "extract-openvino-bundle" => "Extracting OpenVINO bundle",
        "ready" => "Installed",
        _ => this.Stage
    };
}

internal static class WhisperNetModelCatalog
{
    private static readonly string ManagedModelsDirectory = Path.Combine(
        ModelStorage.GetManagedModelsDirectory(),
        "whisper.net");

    public static IReadOnlyList<WhisperNetModelOption> Options { get; } =
    [
        new(
            Id: "large-v3-turbo",
            DisplayName: "Large V3 Turbo (GGML)",
            FileName: "ggml-large-v3-turbo.bin",
            Description: "The fastest Whisper V3 GGML option. It can use Whisper.net GPU runtimes when available; Intel does not currently publish an OpenVINO NPU bundle for Turbo.",
            ApproximateBytes: 1_618_426_976),
        new(
            Id: "large-v3",
            DisplayName: "Large V3 (GGML)",
            FileName: "ggml-large-v3.bin",
            Description: "The highest-accuracy Whisper V3 model. It can use Whisper.net GPU runtimes, and Intel publishes a matching OpenVINO bundle for NPU acceleration.",
            ApproximateBytes: 4_277_163_902,
            OpenVinoBundleFileName: "ggml-large-v3-models.zip",
            Recommended: true),
        new(
            Id: "base.en",
            DisplayName: "Base English (GGML)",
            FileName: "ggml-base.en.bin",
            Description: "Standard English GGML model. It can use Whisper.net GPU runtimes when available; Intel does not currently publish a matching OpenVINO NPU bundle for the English-only base model.",
            ApproximateBytes: 147_964_352),
        new(
            Id: "tiny.en",
            DisplayName: "Tiny English (GGML)",
            FileName: "ggml-tiny.en.bin",
            Description: "Very small English GGML model. It can use Whisper.net GPU runtimes when available; Intel does not currently publish a matching OpenVINO NPU bundle for Tiny English.",
            ApproximateBytes: 77_720_256)
    ];

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out WhisperNetModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static WhisperNetModelOption? TryGetByPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(modelPath);
        return Options.FirstOrDefault(candidate =>
            string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryResolveInstalledArtifacts(
        WhisperNetModelOption option,
        [NotNullWhen(true)] out WhisperNetModelArtifacts? artifacts)
    {
        foreach (var root in EnumerateInstallRoots())
        {
            var candidate = CreateArtifacts(root, option);
            if (File.Exists(candidate.ModelPath))
            {
                artifacts = candidate;
                return true;
            }
        }

        artifacts = null;
        return false;
    }

    internal static bool TryResolveInstalledPath(
        WhisperNetModelOption option,
        [NotNullWhen(true)] out string? installedPath)
    {
        if (TryResolveInstalledArtifacts(option, out var artifacts))
        {
            installedPath = artifacts.Value.ModelPath;
            return true;
        }

        installedPath = null;
        return false;
    }

    internal static WhisperNetModelArtifacts GetManagedArtifacts(WhisperNetModelOption option) =>
        CreateArtifacts(ManagedModelsDirectory, option);

    private static WhisperNetModelArtifacts CreateArtifacts(string root, WhisperNetModelOption option)
    {
        var modelPath = Path.Combine(root, option.FileName);
        var stem = Path.GetFileNameWithoutExtension(option.FileName);
        return new WhisperNetModelArtifacts(
            Path.GetFullPath(modelPath),
            Path.GetFullPath(Path.Combine(root, $"{stem}-encoder-openvino.xml")),
            Path.GetFullPath(Path.Combine(root, $"{stem}-encoder-openvino.bin")));
    }

    private static IEnumerable<string> EnumerateInstallRoots()
    {
        yield return ManagedModelsDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "models", "whisper.net");

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            yield return Path.Combine(dir.FullName, "models", "whisper.net");
            dir = dir.Parent;
        }
    }
}

internal static class WhisperNetModelDownloader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<string> DownloadAsync(
        WhisperNetModelOption option,
        IProgress<WhisperNetModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelsDirectory = WhisperNetModelCatalog.GetManagedModelsDirectory();
        var managedArtifacts = WhisperNetModelCatalog.GetManagedArtifacts(option);
        var shouldUseOpenVinoBundle = option.SupportsOpenVinoBundle && PlatformSupport.SupportsWhisperNetOpenVino;

        Directory.CreateDirectory(modelsDirectory);

        if (WhisperNetModelCatalog.TryResolveInstalledArtifacts(option, out var installedArtifacts) &&
            (installedArtifacts.Value.HasOpenVinoSidecars || !shouldUseOpenVinoBundle))
        {
            progress?.Report(new WhisperNetModelDownloadProgress("ready", 1, 1));
            return installedArtifacts.Value.ModelPath;
        }

        var tempPath = managedArtifacts.ModelPath + ".download";
        var tempXmlPath = managedArtifacts.OpenVinoXmlPath + ".download";
        var tempBinPath = managedArtifacts.OpenVinoBinPath + ".download";
        var tempBundlePath = managedArtifacts.ModelPath + ".openvino.zip.download";

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
            if (File.Exists(tempBinPath)) File.Delete(tempBinPath);
            if (File.Exists(tempBundlePath)) File.Delete(tempBundlePath);

            var bytesDownloaded = 0L;
            long? totalBytes = null;

            if (shouldUseOpenVinoBundle && option.OpenVinoBundleUri is { } openVinoBundleUri)
            {
                using (var response = await HttpClient.GetAsync(openVinoBundleUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    totalBytes = response.Content.Headers.ContentLength;

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var destination = new FileStream(tempBundlePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true);

                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            bytesDownloaded += read;
                            progress?.Report(new WhisperNetModelDownloadProgress("download", bytesDownloaded, totalBytes));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                progress?.Report(new WhisperNetModelDownloadProgress("extract-openvino-bundle", bytesDownloaded, totalBytes));
                await ExtractOpenVinoBundleAsync(option, tempBundlePath, tempPath, tempXmlPath, tempBinPath, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(managedArtifacts.ModelPath))
            {
                bytesDownloaded = new FileInfo(managedArtifacts.ModelPath).Length;
                totalBytes = bytesDownloaded;
                progress?.Report(new WhisperNetModelDownloadProgress("repair-openvino", bytesDownloaded, totalBytes));
            }
            else
            {
                using (var response = await HttpClient.GetAsync(option.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    totalBytes = response.Content.Headers.ContentLength;

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true);

                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            bytesDownloaded += read;
                            progress?.Report(new WhisperNetModelDownloadProgress("download", bytesDownloaded, totalBytes));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (File.Exists(tempPath)) File.Move(tempPath, managedArtifacts.ModelPath, overwrite: true);
            if (File.Exists(tempXmlPath)) File.Move(tempXmlPath, managedArtifacts.OpenVinoXmlPath, overwrite: true);
            if (File.Exists(tempBinPath)) File.Move(tempBinPath, managedArtifacts.OpenVinoBinPath, overwrite: true);

            progress?.Report(new WhisperNetModelDownloadProgress("ready", bytesDownloaded, bytesDownloaded));
            return managedArtifacts.ModelPath;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
            if (File.Exists(tempBinPath)) File.Delete(tempBinPath);
            if (File.Exists(tempBundlePath)) File.Delete(tempBundlePath);
        }
    }

    private static async Task ExtractOpenVinoBundleAsync(
        WhisperNetModelOption option,
        string archivePath,
        string modelDestinationPath,
        string xmlDestinationPath,
        string binDestinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        await ExtractBundleEntryAsync(archive, option.FileName, modelDestinationPath, cancellationToken).ConfigureAwait(false);
        await ExtractBundleEntryAsync(archive, Path.GetFileName(xmlDestinationPath[..^".download".Length]), xmlDestinationPath, cancellationToken).ConfigureAwait(false);
        await ExtractBundleEntryAsync(archive, Path.GetFileName(binDestinationPath[..^".download".Length]), binDestinationPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractBundleEntryAsync(
        ZipArchive archive,
        string fileName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(candidate.FullName), fileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new FileNotFoundException($"OpenVINO bundle is missing the expected file '{fileName}'.");
        }

        await using var source = entry.Open();
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
