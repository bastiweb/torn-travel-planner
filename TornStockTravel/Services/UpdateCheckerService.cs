using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class UpdateCheckerService : IDisposable
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/bastiweb/torn-travel-planner/releases/latest");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const string ExpectedAssetName = "TornStockTravel.exe";

    private readonly HttpClient _httpClient;

    public UpdateCheckerService()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TornStockTravel-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<GitHubUpdateInfo?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseUri);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        string? tagName = ReadString(root, "tag_name");
        if (!TryParseVersion(tagName, out Version? parsedReleaseVersion)
            || parsedReleaseVersion is null
            || parsedReleaseVersion <= currentVersion)
        {
            return null;
        }

        Version releaseVersion = parsedReleaseVersion;

        JsonElement? assetElement = FindReleaseAsset(root, ExpectedAssetName);
        if (assetElement is null)
        {
            return null;
        }

        string? browserDownloadUrl = ReadString(assetElement.Value, "browser_download_url");
        string? htmlUrl = ReadString(root, "html_url");
        if (!Uri.TryCreate(browserDownloadUrl, UriKind.Absolute, out Uri? assetUri)
            || !Uri.TryCreate(htmlUrl, UriKind.Absolute, out Uri? releaseUri))
        {
            return null;
        }

        return new GitHubUpdateInfo(
            tagName ?? releaseVersion.ToString(),
            releaseVersion,
            ReadString(root, "name") ?? string.Empty,
            ReadString(root, "body"),
            releaseUri,
            assetUri,
            ReadString(assetElement.Value, "name") ?? ExpectedAssetName,
            ReadLong(assetElement.Value, "size"));
    }

    public async Task<string> DownloadUpdateAsync(
        GitHubUpdateInfo updateInfo,
        CancellationToken cancellationToken = default)
    {
        string updateDirectory = GetUpdateDirectory(updateInfo.TagName);
        Directory.CreateDirectory(updateDirectory);
        string downloadPath = Path.Combine(updateDirectory, ExpectedAssetName);
        string temporaryPath = $"{downloadPath}.download";

        using HttpRequestMessage request = new(HttpMethod.Get, updateInfo.AssetDownloadUrl);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Update download returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream localStream = File.Create(temporaryPath);
        await remoteStream.CopyToAsync(localStream, cancellationToken);
        localStream.Close();

        if (File.Exists(downloadPath))
        {
            File.Delete(downloadPath);
        }

        File.Move(temporaryPath, downloadPath);
        return downloadPath;
    }

    public void StartUpdater(string downloadedExePath)
    {
        string? targetPath = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(targetPath)
            || !targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(targetPath))
        {
            throw new InvalidOperationException("The current executable path could not be detected.");
        }

        string updateDirectory = Path.GetDirectoryName(downloadedExePath)
            ?? throw new InvalidOperationException("The update directory could not be detected.");
        string scriptPath = Path.Combine(updateDirectory, "install-update.ps1");
        File.WriteAllText(scriptPath, BuildUpdaterScript());

        using Process currentProcess = Process.GetCurrentProcess();
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            Arguments = BuildUpdaterArguments(scriptPath, downloadedExePath, targetPath, currentProcess.Id, updateDirectory),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(startInfo);
    }

    public static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private static JsonElement? FindReleaseAsset(JsonElement root, string assetName)
    {
        if (!root.TryGetProperty("assets", out JsonElement assetsElement)
            || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement assetElement in assetsElement.EnumerateArray())
        {
            string? name = ReadString(assetElement, "name");
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return assetElement.Clone();
            }
        }

        return null;
    }

    private static bool TryParseVersion(string? tagName, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        string normalized = tagName.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version);
    }

    private static string GetUpdateDirectory(string tagName)
    {
        string safeTag = string.Join("_", tagName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Path.GetTempPath(), "TornStockTravelUpdate", safeTag);
    }

    private static string? GetCurrentExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static string BuildUpdaterArguments(
        string scriptPath,
        string sourcePath,
        string targetPath,
        int processId,
        string updateDirectory)
    {
        return string.Join(
            " ",
            "-NoProfile",
            "-ExecutionPolicy Bypass",
            "-File",
            Quote(scriptPath),
            "-SourcePath",
            Quote(sourcePath),
            "-TargetPath",
            Quote(targetPath),
            "-ProcessId",
            processId.ToString(CultureInfo.InvariantCulture),
            "-TempDirectory",
            Quote(updateDirectory));
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static string BuildUpdaterScript()
    {
        return @"
param(
    [Parameter(Mandatory=$true)][string]$SourcePath,
    [Parameter(Mandatory=$true)][string]$TargetPath,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$TempDirectory
)

$ErrorActionPreference = 'Stop'
$targetDirectory = Split-Path -Parent $TargetPath

try {
    $forceAfter = (Get-Date).AddSeconds(15)
    $deadline = (Get-Date).AddSeconds(120)
    $lastError = $null
    $installed = $false

    try {
        Wait-Process -Id $ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    } catch {
    }

    while (-not $installed -and (Get-Date) -lt $deadline) {
        try {
            $runningProcess = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($runningProcess -and (Get-Date) -gt $forceAfter) {
                Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 700
            }

            Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Force
            $installed = $true
        } catch {
            $lastError = $_
            Start-Sleep -Milliseconds 750
        }
    }

    if (-not $installed) {
        if ($lastError) {
            throw $lastError
        }

        throw ""The target file stayed locked for too long.""
    }

    Start-Process -FilePath $TargetPath -WorkingDirectory $targetDirectory
} catch {
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            ""The update could not be installed. The existing app will be restarted.`n`n$($_.Exception.Message)"",
            ""Update failed"",
            ""OK"",
            ""Error"") | Out-Null
    } catch {
    }

    if (Test-Path -LiteralPath $TargetPath) {
        Start-Process -FilePath $TargetPath -WorkingDirectory $targetDirectory
    }
} finally {
    Start-Sleep -Seconds 2
    try {
        Remove-Item -LiteralPath $TempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
    }
}
";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : 0;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
