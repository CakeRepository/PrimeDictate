using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrimeDictate;

internal sealed record AppUpdateInfo(
    string TagName,
    Version Version,
    Uri ReleasePageUrl,
    Uri InstallerDownloadUrl,
    string InstallerAssetName,
    Uri Sha256DownloadUrl,
    long? InstallerSizeBytes,
    DateTimeOffset? PublishedAt,
    string? ReleaseNotes)
{
    public string DisplayVersion => this.TagName.StartsWith('v') ? this.TagName[1..] : this.TagName;
}

internal sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public int? Percent =>
        this.TotalBytes is > 0
            ? (int)Math.Clamp(this.BytesReceived * 100 / this.TotalBytes.Value, 0, 100)
            : null;
}

internal sealed class GitHubUpdateService : IDisposable
{
    private const string Owner = "CakeRepository";
    private const string Repository = "PrimeDictate";
    private static readonly Uri LatestReleaseApiUrl = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
    private readonly HttpClient httpClient;

    public GitHubUpdateService()
    {
        this.httpClient = new HttpClient();
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
    }

    public static Version CurrentApplicationVersion
    {
        get
        {
            var informationalVersion = GetInformationalVersion();
            if (!string.IsNullOrWhiteSpace(informationalVersion) &&
                TryParseReleaseVersion(informationalVersion, out var parsedInformationalVersion))
            {
                return parsedInformationalVersion;
            }

            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return NormalizeVersion(version);
        }
    }

    public static string CurrentApplicationVersionText
    {
        get
        {
            var informationalVersion = GetInformationalVersion();
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion.Split('+', 2)[0];
            }

            return CurrentApplicationVersion.ToString(3);
        }
    }

    public static string InstallerArchitecture =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static string? GetInformationalVersion() =>
        typeof(App)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(LatestReleaseApiUrl);
        using var response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;

        if (TryGetBoolean(root, "draft") || TryGetBoolean(root, "prerelease"))
        {
            return null;
        }

        var tagName = GetRequiredString(root, "tag_name");
        if (!TryParseReleaseVersion(tagName, out var releaseVersion) ||
            releaseVersion <= CurrentApplicationVersion)
        {
            return null;
        }

        var architecture = InstallerArchitecture;
        var expectedInstallerAssetName = $"PrimeDictate-Setup-{tagName}-{architecture}.msi";
        if (!TryFindAsset(root, expectedInstallerAssetName, out var installerAsset))
        {
            throw new InvalidOperationException(
                $"The latest PrimeDictate release is {tagName}, but it does not include {expectedInstallerAssetName}.");
        }

        var expectedShaAssetName = $"{expectedInstallerAssetName}.sha256";
        if (!TryFindAsset(root, expectedShaAssetName, out var shaAsset))
        {
            throw new InvalidOperationException(
                $"The latest PrimeDictate release is {tagName}, but it does not include {expectedShaAssetName}.");
        }

        return new AppUpdateInfo(
            tagName,
            releaseVersion,
            new Uri(GetRequiredString(root, "html_url"), UriKind.Absolute),
            new Uri(GetRequiredString(installerAsset, "browser_download_url"), UriKind.Absolute),
            expectedInstallerAssetName,
            new Uri(GetRequiredString(shaAsset, "browser_download_url"), UriKind.Absolute),
            TryGetInt64(installerAsset, "size"),
            TryGetDateTimeOffset(root, "published_at"),
            TryGetString(root, "body"));
    }

    public async Task<string> DownloadAndVerifyInstallerAsync(
        AppUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expectedHash = await this.DownloadExpectedSha256Async(update, cancellationToken).ConfigureAwait(false);
        var updateDirectory = GetUpdateDownloadDirectory();
        Directory.CreateDirectory(updateDirectory);

        var installerPath = Path.Combine(updateDirectory, update.InstallerAssetName);
        var useUniqueInstallerPath = false;
        if (File.Exists(installerPath))
        {
            try
            {
                if (string.Equals(await ComputeSha256Async(installerPath, cancellationToken).ConfigureAwait(false), expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var existingLength = new FileInfo(installerPath).Length;
                    progress?.Report(new UpdateDownloadProgress(existingLength, existingLength));
                    return installerPath;
                }
            }
            catch (IOException ex)
            {
                useUniqueInstallerPath = true;
                AppLog.Info($"Cached update installer is currently unavailable; downloading a fresh copy: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                useUniqueInstallerPath = true;
                AppLog.Info($"Cached update installer could not be read; downloading a fresh copy: {ex.Message}");
            }
        }

        var tempPath = Path.Combine(
            updateDirectory,
            $"{Path.GetFileNameWithoutExtension(update.InstallerAssetName)}.{Environment.ProcessId}.{Guid.NewGuid():N}.download");

        using var request = CreateGitHubRequest(update.InstallerDownloadUrl);
        using var response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.InstallerSizeBytes;
        string actualHash;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1024 * 128];
            long bytesReceived = 0;

            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, bytesRead));
                bytesReceived += bytesRead;
                progress?.Report(new UpdateDownloadProgress(bytesReceived, totalBytes));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(tempPath);
            throw new InvalidDataException(
                $"Downloaded installer hash mismatch. Expected {expectedHash}, got {actualHash}.");
        }

        var targetPath = useUniqueInstallerPath ? GetUniqueInstallerPath(updateDirectory, update.InstallerAssetName) : installerPath;
        try
        {
            File.Move(tempPath, targetPath, overwrite: !useUniqueInstallerPath);
        }
        catch (IOException) when (!useUniqueInstallerPath)
        {
            targetPath = GetUniqueInstallerPath(updateDirectory, update.InstallerAssetName);
            File.Move(tempPath, targetPath, overwrite: false);
        }

        return targetPath;
    }

    public static Process StartInstaller(string installerPath, AppSettings? settings)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The update installer was not found.", installerPath);
        }

        var launchAtLoginProperty = ShouldInstallerCreateAllUsersStartupShortcut(settings)
            ? "LAUNCHATLOGIN=1"
            : "LAUNCHATLOGIN=0";
        var installerDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory;
        var scriptPath = Path.Combine(installerDirectory, $"PrimeDictate.StartUpdate.{Environment.ProcessId}.{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, GetInstallerLaunchScript(), Encoding.UTF8);
        var arguments = string.Join(
            " ",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            QuoteArgument(scriptPath),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            QuoteArgument(installerPath),
            launchAtLoginProperty);

        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installerDirectory
        }) ?? throw new InvalidOperationException("The update handoff process did not start.");
    }

    private static string GetInstallerLaunchScript() =>
        """
        param(
            [int] $PrimeDictateProcessId,
            [string] $InstallerPath,
            [string] $LaunchAtLoginProperty
        )

        $ErrorActionPreference = 'Stop'

        try {
            Wait-Process -Id $PrimeDictateProcessId -Timeout 120 -ErrorAction SilentlyContinue
        } catch {
        }

        $quotedInstallerPath = '"' + $InstallerPath.Replace('"', '\"') + '"'
        $installerArguments = "/i $quotedInstallerPath $LaunchAtLoginProperty"
        Start-Process -FilePath 'msiexec.exe' -ArgumentList $installerArguments -Verb RunAs

        try {
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        } catch {
        }
        """;

    private static string GetUniqueInstallerPath(string updateDirectory, string installerAssetName) =>
        Path.Combine(
            updateDirectory,
            $"{Path.GetFileNameWithoutExtension(installerAssetName)}.{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}.msi");

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<string> DownloadExpectedSha256Async(AppUpdateInfo update, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(update.Sha256DownloadUrl);
        using var response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var checksumText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var part in checksumText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 64 && part.All(IsHexDigit))
            {
                return part.ToLowerInvariant();
            }
        }

        throw new InvalidDataException($"{update.InstallerAssetName}.sha256 did not contain a valid SHA256 checksum.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 128];

        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, bytesRead));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string GetUpdateDownloadDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrimeDictate",
            "updates");

    private static HttpRequestMessage CreateGitHubRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"PrimeDictate/{CurrentApplicationVersion.ToString(3)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool ShouldInstallerCreateAllUsersStartupShortcut(AppSettings? settings) =>
        settings?.LaunchAtLoginScope is null or LaunchAtLoginScope.NotConfigured or LaunchAtLoginScope.AllUsers;

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool TryFindAsset(JsonElement root, string assetName, out JsonElement asset)
    {
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in assets.EnumerateArray())
            {
                if (string.Equals(TryGetString(candidate, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                {
                    asset = candidate;
                    return true;
                }
            }
        }

        asset = default;
        return false;
    }

    private static bool TryParseReleaseVersion(string tagName, out Version version)
    {
        var versionText = tagName.Trim();
        if (versionText.StartsWith('v') || versionText.StartsWith('V'))
        {
            versionText = versionText[1..];
        }

        versionText = versionText.Split(['-', '+'], 2, StringSplitOptions.TrimEntries)[0];
        if (Version.TryParse(versionText, out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0 && parsed.Build >= 0)
        {
            version = new Version(parsed.Major, parsed.Minor, parsed.Build);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static Version NormalizeVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    private static bool TryGetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName)
        ?? throw new InvalidDataException($"GitHub release response did not include '{propertyName}'.");

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result))
        {
            return result;
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return result;
        }

        return null;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
