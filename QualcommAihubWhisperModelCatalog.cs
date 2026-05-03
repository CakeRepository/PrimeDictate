using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record QualcommAihubWhisperModelOption(
    string Id,
    string DisplayName,
    string InstallDirectoryName,
    string RunnableArchiveFileName,
    string RawContextArchiveFileName,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    private const string ReleaseBaseUri =
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/whisper_small/releases/v0.52.0";

    public string DownloadUri => $"{ReleaseBaseUri}/{this.RunnableArchiveFileName}";

    public string RawContextDownloadUri => $"{ReleaseBaseUri}/{this.RawContextArchiveFileName}";

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct QualcommAihubWhisperArtifacts(
    string ModelDirectory,
    string EncoderOnnxPath,
    string DecoderOnnxPath,
    string EncoderContextPath,
    string DecoderContextPath,
    string TokenizerPath,
    string MetadataPath);

internal readonly record struct QualcommAihubWhisperModelDownloadProgress(
    string Stage,
    long BytesDownloaded,
    long? TotalBytes)
{
    public double? Percentage => this.Stage == "download" && this.TotalBytes is > 0
        ? Math.Min(100d, this.BytesDownloaded * 100d / this.TotalBytes.Value)
        : null;

    public string ProgressLabel => this.Stage switch
    {
        "extract" => "Extracting model files",
        "tokenizer" => "Installing tokenizer",
        "ready" => "Installed",
        _ => this.TotalBytes is > 0
            ? $"{WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)} / {WhisperModelCatalog.FormatByteSize(this.TotalBytes.Value)}"
            : WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)
    };
}

internal static class QualcommAihubWhisperModelCatalog
{
    internal const string TokenizerFileName = "multilingual.tiktoken";

    private static readonly string ManagedModelsDirectory = Path.Combine(
        ModelStorage.GetManagedModelsDirectory(),
        "qualcomm-aihub-whisper");

    public static IReadOnlyList<QualcommAihubWhisperModelOption> Options { get; } =
    [
        new(
            Id: "qaihub-whisper-small-snapdragon-x-elite",
            DisplayName: "Qualcomm AI Hub Whisper Small (Snapdragon X Elite)",
            InstallDirectoryName: "whisper_small-precompiled_qnn_onnx-float-qualcomm_snapdragon_x_elite",
            RunnableArchiveFileName: "whisper_small-precompiled_qnn_onnx-float-qualcomm_snapdragon_x_elite.zip",
            RawContextArchiveFileName: "whisper_small-qnn_context_binary-float-qualcomm_snapdragon_x_elite.zip",
            Description: "Qualcomm AI Hub Whisper Small compiled for Snapdragon X Elite / X Plus NPU through ONNX Runtime QNN. PrimeDictate installs the ONNX wrapper package because the raw context-binary ZIP is not directly runnable by ONNX Runtime.",
            ApproximateBytes: 520_029_222,
            Recommended: true)
    ];

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out QualcommAihubWhisperModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static QualcommAihubWhisperModelOption? TryGetByPath(string? modelPath)
    {
        if (!TryResolveDirectory(modelPath, out var resolvedPath))
        {
            return null;
        }

        var directoryName = Path.GetFileName(resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Options.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallDirectoryName, directoryName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryResolveDirectory(string? path, [NotNullWhen(true)] out string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            var candidate = Path.GetFullPath(path);
            if (IsValidModelDirectory(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        resolvedPath = null;
        return false;
    }

    internal static bool TryResolveInstalledPath(
        QualcommAihubWhisperModelOption option,
        [NotNullWhen(true)] out string? installedPath)
    {
        var candidate = Path.Combine(ManagedModelsDirectory, option.InstallDirectoryName);
        if (IsValidModelDirectory(candidate))
        {
            installedPath = Path.GetFullPath(candidate);
            return true;
        }

        installedPath = null;
        return false;
    }

    internal static bool TryResolveArtifacts(
        string? directoryPath,
        [NotNullWhen(true)] out QualcommAihubWhisperArtifacts? artifacts)
    {
        artifacts = null;
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var directory = Path.GetFullPath(directoryPath);
        var encoderOnnx = Path.Combine(directory, "encoder.onnx");
        var decoderOnnx = Path.Combine(directory, "decoder.onnx");
        var encoderContext = Path.Combine(directory, "encoder_qairt_context.bin");
        var decoderContext = Path.Combine(directory, "decoder_qairt_context.bin");
        var tokenizer = Path.Combine(directory, TokenizerFileName);
        var metadata = Path.Combine(directory, "metadata.json");

        if (File.Exists(encoderOnnx) &&
            File.Exists(decoderOnnx) &&
            File.Exists(encoderContext) &&
            File.Exists(decoderContext) &&
            File.Exists(tokenizer) &&
            File.Exists(metadata))
        {
            artifacts = new QualcommAihubWhisperArtifacts(
                directory,
                encoderOnnx,
                decoderOnnx,
                encoderContext,
                decoderContext,
                tokenizer,
                metadata);
            return true;
        }

        return false;
    }

    internal static bool IsValidModelDirectory(string? directoryPath) =>
        TryResolveArtifacts(directoryPath, out _);

    internal static bool IsRawContextOnlyDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(directoryPath, "encoder.bin")) &&
            File.Exists(Path.Combine(directoryPath, "decoder.bin")) &&
            File.Exists(Path.Combine(directoryPath, "metadata.json")) &&
            !File.Exists(Path.Combine(directoryPath, "encoder.onnx")) &&
            !File.Exists(Path.Combine(directoryPath, "decoder.onnx"));
    }

    internal static IReadOnlyList<string> GetRequiredFiles() =>
    [
        "encoder.onnx",
        "decoder.onnx",
        "encoder_qairt_context.bin",
        "decoder_qairt_context.bin",
        TokenizerFileName,
        "metadata.json"
    ];
}

internal static class QualcommAihubWhisperModelDownloader
{
    private const string TokenizerUri =
        "https://raw.githubusercontent.com/openai/whisper/839639a223b92ad61851baae9ad8a695ccb41ce5/whisper/assets/multilingual.tiktoken";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<string> DownloadAsync(
        QualcommAihubWhisperModelOption option,
        IProgress<QualcommAihubWhisperModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelsDirectory = QualcommAihubWhisperModelCatalog.GetManagedModelsDirectory();
        var destinationPath = Path.Combine(modelsDirectory, option.InstallDirectoryName);
        Directory.CreateDirectory(modelsDirectory);

        if (QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            progress?.Report(new QualcommAihubWhisperModelDownloadProgress("ready", 1, 1));
            return installedPath;
        }

        var archivePath = Path.Combine(modelsDirectory, option.RunnableArchiveFileName + ".download");
        var extractPath = Path.Combine(modelsDirectory, option.InstallDirectoryName + ".extract");
        var completed = false;

        try
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            if (Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }

            long bytesDownloaded = 0;
            long? totalBytes = null;

            using (var response = await HttpClient.GetAsync(
                       option.DownloadUri,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                totalBytes = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    archivePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);

                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        bytesDownloaded += read;
                        progress?.Report(new QualcommAihubWhisperModelDownloadProgress("download", bytesDownloaded, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new QualcommAihubWhisperModelDownloadProgress("extract", bytesDownloaded, totalBytes));
            ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

            var validExtractedDirectory = FindValidModelDirectory(extractPath);
            if (validExtractedDirectory is null)
            {
                throw new InvalidOperationException(
                    $"The extracted Qualcomm AI Hub Whisper package is incomplete. Expected {string.Join(", ", QualcommAihubWhisperModelCatalog.GetRequiredFiles())}.");
            }

            Directory.Move(validExtractedDirectory, destinationPath);

            progress?.Report(new QualcommAihubWhisperModelDownloadProgress("tokenizer", bytesDownloaded, totalBytes));
            await DownloadTokenizerAsync(destinationPath, cancellationToken).ConfigureAwait(false);

            if (!QualcommAihubWhisperModelCatalog.IsValidModelDirectory(destinationPath))
            {
                throw new InvalidOperationException(
                    $"The installed Qualcomm AI Hub Whisper package is incomplete. Expected {string.Join(", ", QualcommAihubWhisperModelCatalog.GetRequiredFiles())}.");
            }

            progress?.Report(new QualcommAihubWhisperModelDownloadProgress("ready", bytesDownloaded, bytesDownloaded));
            completed = true;
            return destinationPath;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            if (!completed &&
                Directory.Exists(destinationPath) &&
                !QualcommAihubWhisperModelCatalog.IsValidModelDirectory(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
        }
    }

    private static string? FindValidModelDirectory(string rootPath)
    {
        if (HasRunnablePackageFiles(rootPath))
        {
            return rootPath;
        }

        foreach (var subDir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            if (HasRunnablePackageFiles(subDir))
            {
                return subDir;
            }
        }

        return null;
    }

    private static bool HasRunnablePackageFiles(string directoryPath) =>
        File.Exists(Path.Combine(directoryPath, "encoder.onnx")) &&
        File.Exists(Path.Combine(directoryPath, "decoder.onnx")) &&
        File.Exists(Path.Combine(directoryPath, "encoder_qairt_context.bin")) &&
        File.Exists(Path.Combine(directoryPath, "decoder_qairt_context.bin")) &&
        File.Exists(Path.Combine(directoryPath, "metadata.json"));

    private static async Task DownloadTokenizerAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var tokenizerPath = Path.Combine(destinationPath, QualcommAihubWhisperModelCatalog.TokenizerFileName);
        if (File.Exists(tokenizerPath))
        {
            return;
        }

        using var response = await HttpClient.GetAsync(
            TokenizerUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            tokenizerPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
