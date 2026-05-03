using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record MoonshineModelOption(
    string Id,
    string DisplayName,
    string InstallDirectoryName,
    string ArchiveFileName,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    public string DownloadUri =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}";

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct MoonshineModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
{
    public double? Percentage => this.Stage == "download" && this.TotalBytes is > 0
        ? Math.Min(100d, this.BytesDownloaded * 100d / this.TotalBytes.Value)
        : null;

    public string ProgressLabel => this.Stage switch
    {
        "extract" => "Extracting model files",
        "ready" => "Installed",
        _ => this.TotalBytes is > 0
            ? $"{WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)} / {WhisperModelCatalog.FormatByteSize(this.TotalBytes.Value)}"
            : WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)
    };
}

internal static class MoonshineModelCatalog
{
    private static readonly string ManagedModelsDirectory = Path.Combine(
        ModelStorage.GetManagedModelsDirectory(),
        "moonshine");

    public static IReadOnlyList<MoonshineModelOption> Options { get; } =
    [
        new(
            Id: "moonshine-tiny-v2-en",
            DisplayName: "Moonshine Tiny v2 (English)",
            InstallDirectoryName: "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27",
            ArchiveFileName: "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27.tar.bz2",
            Description: "The latest Moonshine v2 Tiny model. Fast on CPU today; Qualcomm NPU use requires prepared QNN artifacts in the model folder.",
            ApproximateBytes: 83_886_080,
            Recommended: true),
        new(
            Id: "moonshine-base-en",
            DisplayName: "Moonshine Base (English) [v1]",
            InstallDirectoryName: "sherpa-onnx-moonshine-base-en-int8",
            ArchiveFileName: "sherpa-onnx-moonshine-base-en-int8.tar.bz2",
            Description: "The original Moonshine v1 model. Fast and reliable, using a 4-stage pipeline.",
            ApproximateBytes: 250_807_309)
    ];

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out MoonshineModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static MoonshineModelOption? TryGetByPath(string? modelPath)
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
        MoonshineModelOption option,
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

    internal static bool IsValidModelDirectory(string? directoryPath)
    {
        return IsValidV1ModelDirectory(directoryPath) || IsValidV2ModelDirectory(directoryPath);
    }

    internal static bool IsValidV1ModelDirectory(string? directoryPath)
    {
        return TryResolveV1Files(directoryPath, out _);
    }

    internal static bool IsValidV2ModelDirectory(string? directoryPath)
    {
        return TryResolveV2Files(directoryPath, out _);
    }

    internal static bool HasPreparedQnnArtifacts(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var qnnDirectory = Path.Combine(directoryPath, "qnn");
        if (!Directory.Exists(qnnDirectory))
        {
            return false;
        }

        if (TryResolveV2Files(directoryPath, out var v2Files))
        {
            var encoderName = Path.GetFileName(v2Files.Value.Encoder);
            var decoderName = Path.GetFileName(v2Files.Value.Decoder);
            return FindFile(
                    qnnDirectory,
                    "encoder.qdq.onnx",
                    "encoder.qnn.onnx",
                    encoderName.Replace(".onnx", ".qdq.onnx", StringComparison.Ordinal).Replace(".ort", ".qdq.onnx", StringComparison.Ordinal),
                    encoderName.Replace(".onnx", ".qnn.onnx", StringComparison.Ordinal).Replace(".ort", ".qnn.onnx", StringComparison.Ordinal)) is not null &&
                FindFile(
                    qnnDirectory,
                    "decoder.qdq.onnx",
                    "decoder.qnn.onnx",
                    decoderName.Replace(".onnx", ".qdq.onnx", StringComparison.Ordinal).Replace(".ort", ".qdq.onnx", StringComparison.Ordinal),
                    decoderName.Replace(".onnx", ".qnn.onnx", StringComparison.Ordinal).Replace(".ort", ".qnn.onnx", StringComparison.Ordinal)) is not null;
        }

        if (TryResolveV1Files(directoryPath, out _))
        {
            return FindFile(qnnDirectory, "preprocess.qdq.onnx", "preprocess.qnn.onnx") is not null &&
                FindFile(qnnDirectory, "encode.qdq.onnx", "encode.qnn.onnx") is not null &&
                FindFile(qnnDirectory, "uncached_decode.qdq.onnx", "uncached_decode.qnn.onnx") is not null &&
                FindFile(qnnDirectory, "cached_decode.qdq.onnx", "cached_decode.qnn.onnx") is not null;
        }

        return false;
    }

    internal static bool TryResolveV1Files(
        string? directoryPath,
        [NotNullWhen(true)] out MoonshineV1Files? files)
    {
        files = null;
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return false;

        var preprocess = FindFile(directoryPath, "preprocess.onnx", "preprocess.qdq.onnx");
        var encode = FindFile(directoryPath, "encode.int8.onnx", "encode.onnx", "encoder.int8.onnx");
        var uncached = FindFile(directoryPath, "uncached_decode.int8.onnx", "uncached_decode.onnx");
        var cached = FindFile(directoryPath, "cached_decode.int8.onnx", "cached_decode.onnx");
        var tokens = FindFile(directoryPath, "tokens.txt");

        if (preprocess != null && encode != null && uncached != null && cached != null && tokens != null)
        {
            files = new MoonshineV1Files(preprocess, encode, uncached, cached, tokens);
            return true;
        }

        return false;
    }

    internal static bool TryResolveV2Files(
        string? directoryPath,
        [NotNullWhen(true)] out MoonshineV2Files? files)
    {
        files = null;
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return false;

        // Prioritize the exact names seen in the optimized HF models (.ort and .onnx)
        var encoder = FindFile(directoryPath, 
            "encoder_model.ort", "encoder_model_int8.ort", "encoder_model.onnx", "encoder_model_int8.onnx",
            "encoder.int8.onnx", "encoder.onnx", "encode.int8.onnx", "encode.onnx");
        
        var decoder = FindFile(directoryPath, 
            "decoder_model_merged.ort", "decoder_model_merged_int8.ort", "decoder_model_merged.onnx", "decoder_model_merged_int8.onnx",
            "decoder.int8.onnx", "decoder.onnx", "merged_decoder.onnx", "decode.int8.onnx", "decode.onnx");
        
        var tokens = FindFile(directoryPath, "tokens.txt", "tokenizer.json", "vocab.txt");

        if (encoder != null && decoder != null && tokens != null)
        {
            files = new MoonshineV2Files(encoder, decoder, tokens);
            return true;
        }

        return false;
    }

    private static string? FindFile(string directoryPath, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directoryPath, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static IReadOnlyList<string> GetRequiredFilesV1() =>
    [
        "preprocess.onnx",
        "encode.int8.onnx",
        "uncached_decode.int8.onnx",
        "cached_decode.int8.onnx",
        "tokens.txt"
    ];

    internal static IReadOnlyList<string> GetRequiredFiles() =>
        ["preprocess.onnx or encoder_model.ort", "decode model(s)", "tokens.txt"];
}

internal static class MoonshineModelDownloader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<string> DownloadAsync(
        MoonshineModelOption option,
        IProgress<MoonshineModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelsDirectory = MoonshineModelCatalog.GetManagedModelsDirectory();
        var destinationPath = Path.Combine(modelsDirectory, option.InstallDirectoryName);
        Directory.CreateDirectory(modelsDirectory);

        if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            progress?.Report(new MoonshineModelDownloadProgress("ready", 1, 1));
            return installedPath;
        }

        var archivePath = Path.Combine(modelsDirectory, option.ArchiveFileName + ".download");
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
                        progress?.Report(new MoonshineModelDownloadProgress("download", bytesDownloaded, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new MoonshineModelDownloadProgress("extract", bytesDownloaded, totalBytes));
            await ExtractArchiveAsync(archivePath, extractPath, cancellationToken).ConfigureAwait(false);

            var validExtractedDirectory = FindValidModelDirectory(extractPath);
            if (validExtractedDirectory == null)
            {
                var foundFiles = Directory.Exists(extractPath)
                    ? string.Join(", ", Directory.EnumerateFiles(extractPath, "*", SearchOption.AllDirectories).Select(Path.GetFileName))
                    : "None (extraction failed?)";

                var expectedFiles = option.Id.Contains("v2", StringComparison.OrdinalIgnoreCase)
                    ? "encoder_model.ort, decoder_model_merged.ort, tokens.txt"
                    : "preprocess.onnx, encode.int8.onnx, uncached_decode.int8.onnx, cached_decode.int8.onnx, tokens.txt";

                throw new InvalidOperationException(
                    $"The extracted Moonshine model is incomplete.\n\nExpected: {expectedFiles}\n\nFound: {foundFiles}");
            }

            Directory.Move(validExtractedDirectory, destinationPath);
            progress?.Report(new MoonshineModelDownloadProgress("ready", bytesDownloaded, bytesDownloaded));
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

            if (!completed && Directory.Exists(destinationPath) && !MoonshineModelCatalog.IsValidModelDirectory(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
        }
    }

    private static string? FindValidModelDirectory(string rootPath)
    {
        if (MoonshineModelCatalog.IsValidModelDirectory(rootPath))
        {
            return rootPath;
        }

        foreach (var subDir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            if (MoonshineModelCatalog.IsValidModelDirectory(subDir))
            {
                return subDir;
            }
        }

        return null;
    }

    private static async Task ExtractArchiveAsync(string archivePath, string extractPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extractPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xjf \"{archivePath}\" -C \"{extractPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start tar.exe to extract the Moonshine model archive.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Moonshine archive extraction failed: {detail.Trim()}");
        }
    }
}
