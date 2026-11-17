using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HuaweiHROverlay.Core;

public enum AppBuildVariant
{
    WindowsFull,
    WindowsLite,
    LinuxDeb,
    LinuxRpm,
    LinuxTarball,
    MacArm64,
    MacX64
}

public sealed record ReleaseAssetInfo(
    string Name,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256Hex)
{
    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string HtmlUrl,
    AppBuildVariant DetectedVariant,
    ReleaseAssetInfo? SelectedAsset,
    DateTimeOffset? PublishedAt)
{
    public string VariantDisplay => DetectedVariant switch
    {
        AppBuildVariant.WindowsLite => "Windows Lite",
        AppBuildVariant.WindowsFull => "Windows Self-contained",
        AppBuildVariant.LinuxDeb => "Debian / Ubuntu (.deb)",
        AppBuildVariant.LinuxRpm => "Fedora / RPM (.rpm)",
        AppBuildVariant.LinuxTarball => "Linux Portable (.tar.gz)",
        AppBuildVariant.MacArm64 => "macOS Apple Silicon",
        AppBuildVariant.MacX64 => "macOS Intel",
        _ => "Portable"
    };
}

public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(Math.Round(BytesReceived * 100.0 / TotalBytes.Value, 1), 0, 100)
        : null;

    public string Summary
    {
        get
        {
            double recvMb = BytesReceived / (1024.0 * 1024.0);
            if (TotalBytes is > 0)
            {
                double totalMb = TotalBytes.Value / (1024.0 * 1024.0);
                return $"{Percentage:F0}%  ·  {recvMb:F1} / {totalMb:F1} MB";
            }
            return $"{recvMb:F1} MB downloaded";
        }
    }
}

/// <summary>
/// Cross-platform GitHub Releases updater (Windows, Linux Debian/Arch/Fedora, and macOS).
/// Automatically picks the matching OS/package asset, verifies SHA-256 checksums,
/// and applies in-place binary updates or launches the downloaded OS package.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string RepoOwner = "fishuvn";
    public const string RepoName = "huawei-hr-overlay";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private readonly HttpClient _http;

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HuaweiHROverlay", GetCurrentVersionString()));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static string GetCurrentVersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            string clean = plusIdx >= 0 ? infoVer[..plusIdx] : infoVer;
            if (TryParseVersion(clean, out var parsed))
                return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        }

        var v = asm.GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "2.0.0";
    }

    public static AppBuildVariant DetectCurrentBuildVariant()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? AppBuildVariant.MacArm64
                : AppBuildVariant.MacX64;
        }

        if (OperatingSystem.IsLinux())
        {
            string? procPath = Environment.ProcessPath;
            // System-managed /usr install prefers native distro package if on Debian or Fedora;
            // user-writable portable/Arch install prefers in-place .tar.gz auto-swap!
            if (!string.IsNullOrEmpty(procPath) && procPath.StartsWith("/usr/", StringComparison.Ordinal))
            {
                if (File.Exists("/etc/debian_version"))
                    return AppBuildVariant.LinuxDeb;
                if (File.Exists("/etc/fedora-release") || File.Exists("/etc/redhat-release"))
                    return AppBuildVariant.LinuxRpm;
            }
            return AppBuildVariant.LinuxTarball;
        }

        var asm = Assembly.GetExecutingAssembly();
        foreach (var meta in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(meta.Key, "BuildVariant", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(meta.Value, "lite", StringComparison.OrdinalIgnoreCase))
                    return AppBuildVariant.WindowsLite;
                if (string.Equals(meta.Value, "full", StringComparison.OrdinalIgnoreCase))
                    return AppBuildVariant.WindowsFull;
            }
        }

        var winPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(winPath))
        {
            string fn = Path.GetFileName(winPath);
            if (fn.Contains("lite", StringComparison.OrdinalIgnoreCase))
                return AppBuildVariant.WindowsLite;
            if (!fn.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(winPath))
            {
                try
                {
                    long len = new FileInfo(winPath).Length;
                    return len > 0 && len < 45L * 1024 * 1024
                        ? AppBuildVariant.WindowsLite
                        : AppBuildVariant.WindowsFull;
                }
                catch { }
            }
        }

        return AppBuildVariant.WindowsFull;
    }

    public static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        int dashIdx = s.IndexOfAny(['-', '+']);
        if (dashIdx >= 0)
            s = s[..dashIdx];

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 4) return false;
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return false;

        int build = parts.Length >= 3 && int.TryParse(parts[2], out int b) ? b : 0;
        int rev = parts.Length == 4 && int.TryParse(parts[3], out int r) ? r : -1;

        version = rev >= 0
            ? new Version(major, minor, build, rev)
            : new Version(major, minor, build);
        return true;
    }

    public static ReleaseAssetInfo? SelectBestAsset(IReadOnlyList<ReleaseAssetInfo> assets, AppBuildVariant variant)
    {
        var valid = assets.Where(a => !string.IsNullOrWhiteSpace(a.DownloadUrl)).ToList();
        if (valid.Count == 0) return null;

        return variant switch
        {
            AppBuildVariant.WindowsLite =>
                valid.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.WindowsFull =>
                valid.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.LinuxDeb =>
                valid.FirstOrDefault(a => a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.LinuxRpm =>
                valid.FirstOrDefault(a => a.Name.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.LinuxTarball =>
                valid.FirstOrDefault(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.MacArm64 =>
                valid.FirstOrDefault(a => a.Name.Contains("macos-arm64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.Contains("macos-arm64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),

            AppBuildVariant.MacX64 =>
                valid.FirstOrDefault(a => a.Name.Contains("macos-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                ?? valid.FirstOrDefault(a => a.Name.Contains("macos-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),

            _ => null
        };
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, checkCts.Token);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub API rate limit reached. Try again shortly or open Releases in browser.");

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("No published GitHub releases found.");

        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(checkCts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: checkCts.Token);
        return ParseReleaseDocument(doc.RootElement, GetCurrentVersionString(), DetectCurrentBuildVariant());
    }

    public static UpdateCheckResult ParseReleaseDocument(
        JsonElement root,
        string currentVersionStr,
        AppBuildVariant variant)
    {
        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
        string title = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
        string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        string htmlUrl = root.TryGetProperty("html_url", out var urlProp) && !string.IsNullOrWhiteSpace(urlProp.GetString())
            ? urlProp.GetString()!
            : ReleasesPageUrl;

        bool isDraft = root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
        bool isPrerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubProp)
            && pubProp.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pubProp.GetString(), out var dto))
        {
            publishedAt = dto;
        }

        var assets = new List<ReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsProp.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string dlUrl = item.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                long size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;

                string? sha256 = null;
                if (item.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    string rawDigest = d.GetString() ?? "";
                    if (rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha256 = rawDigest["sha256:".Length..].Trim();
                }

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dlUrl))
                    assets.Add(new ReleaseAssetInfo(name, dlUrl, size, sha256));
            }
        }

        TryParseVersion(currentVersionStr, out var currentVer);
        bool validRemote = TryParseVersion(tagName, out var latestVer);
        string cleanLatest = validRemote
            ? $"{latestVer.Major}.{latestVer.Minor}.{Math.Max(0, latestVer.Build)}"
            : tagName.TrimStart('v', 'V');

        bool updateAvailable = !isDraft && !isPrerelease && validRemote && latestVer > currentVer;
        var selectedAsset = SelectBestAsset(assets, variant);

        return new UpdateCheckResult(
            IsUpdateAvailable: updateAvailable,
            CurrentVersion: currentVersionStr,
            LatestVersion: cleanLatest,
            ReleaseTitle: string.IsNullOrWhiteSpace(title) ? $"v{cleanLatest}" : title,
            ReleaseNotes: FormatReleaseNotesSummary(body),
            HtmlUrl: htmlUrl,
            DetectedVariant: variant,
            SelectedAsset: selectedAsset,
            PublishedAt: publishedAt);
    }

    public async Task<string> DownloadUpdateAsync(
        ReleaseAssetInfo asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        string stageDir = Path.Combine(Path.GetTempPath(), "HuaweiHROverlay-Update");
        Directory.CreateDirectory(stageDir);

        string safeFileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(safeFileName))
            safeFileName = "HuaweiHROverlay-update.bin";

        string partPath = Path.Combine(stageDir, safeFileName + ".part");
        string finalPath = Path.Combine(stageDir, safeFileName);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? totalBytes = resp.Content.Headers.ContentLength ?? (asset.SizeBytes > 0 ? asset.SizeBytes : null);
            progress?.Report(new UpdateDownloadProgress(0, totalBytes));

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] header = new byte[4];
            int headerRead = 0;

            await using (var netStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                long lastReportTick = 0;

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(45));

                    int read = await netStream.ReadAsync(buffer, readCts.Token);
                    if (read == 0) break;

                    if (headerRead < 4)
                    {
                        int toCopy = Math.Min(4 - headerRead, read);
                        Array.Copy(buffer, 0, header, headerRead, toCopy);
                        headerRead += toCopy;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha256.AppendData(buffer, 0, read);
                    totalRead += read;

                    long now = Environment.TickCount64;
                    if (now - lastReportTick >= 80)
                    {
                        lastReportTick = now;
                        progress?.Report(new UpdateDownloadProgress(totalRead, totalBytes));
                    }
                }

                await fileStream.FlushAsync(ct);
                progress?.Report(new UpdateDownloadProgress(totalRead, totalBytes));

                if (totalRead < 16 * 1024 || !IsValidArtifactHeader(safeFileName, header))
                    throw new InvalidDataException("Downloaded update file failed binary format verification.");
            }

            if (!string.IsNullOrWhiteSpace(asset.Sha256Hex))
            {
                string actualHex = Convert.ToHexString(sha256.GetHashAndReset());
                if (!string.Equals(actualHex, asset.Sha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 checksum mismatch on downloaded update.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            throw;
        }
    }

    private static bool IsValidArtifactHeader(string fileName, ReadOnlySpan<byte> h)
    {
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return h[0] == (byte)'M' && h[1] == (byte)'Z'; // Windows PE
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return h[0] == 0x1F && h[1] == 0x8B; // GZip
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return h[0] == (byte)'P' && h[1] == (byte)'K'; // ZIP
        if (fileName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
            return h[0] == (byte)'!' && h[1] == (byte)'<'; // ar archive (!<arch>)
        if (fileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            return h[0] == 0xED && h[1] == 0xAB && h[2] == 0xEE && h[3] == 0xDB; // RPM lead
        // DMG (UDIF/zlib/lzfse) — reject HTML error pages
        return !(h[0] == (byte)'<' && (h[1] == (byte)'!' || h[1] == (byte)'h' || h[1] == (byte)'H'));
    }

    public static (bool ShouldExitApp, string Message) LaunchSelfUpdate(string stagedFilePath)
    {
        if (!File.Exists(stagedFilePath))
            return (false, "Downloaded update file was not found.");

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe)
            || Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            RevealInFileManager(stagedFilePath);
            return (false, $"Running inside dotnet host — downloaded update saved at {stagedFilePath}");
        }

        if (OperatingSystem.IsWindows() && stagedFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ApplyWindowsExeUpdate(stagedFilePath, currentExe);

        if (OperatingSystem.IsLinux())
            return ApplyLinuxUpdate(stagedFilePath, currentExe);

        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"\"{stagedFilePath}\"") { UseShellExecute = false });
            return (false, "Opened downloaded macOS disk image — drag Huawei HR Overlay.app to Applications.");
        }

        RevealInFileManager(stagedFilePath);
        return (false, $"Update downloaded to {stagedFilePath}");
    }

    private static (bool ShouldExitApp, string Message) ApplyWindowsExeUpdate(string stagedExePath, string currentExe)
    {
        string targetDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        bool requiresElevation = !HasDirectoryWriteAccess(targetDir);

        string stageDir = Path.GetDirectoryName(stagedExePath)!;
        string scriptPath = Path.Combine(stageDir, $"apply-update-{Environment.ProcessId}.ps1");

        string escapedSrc = stagedExePath.Replace("'", "''");
        string escapedDst = currentExe.Replace("'", "''");
        string escapedWorkDir = targetDir.Replace("'", "''");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $waitPid = {{Environment.ProcessId}}
            $src = '{{escapedSrc}}'
            $dst = '{{escapedDst}}'
            $workDir = '{{escapedWorkDir}}'

            try { Wait-Process -Id $waitPid -Timeout 30 -ErrorAction SilentlyContinue } catch {}

            for ($i = 0; $i -lt 30; $i++) {
                try {
                    Move-Item -LiteralPath $src -Destination $dst -Force
                    break
                } catch {
                    if ($i -eq 29) { throw }
                    Start-Sleep -Milliseconds 350
                }
            }

            Start-Process -FilePath $dst -WorkingDirectory $workDir
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = requiresElevation,
            CreateNoWindow = !requiresElevation,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (requiresElevation) psi.Verb = "runas";

        Process.Start(psi);
        return (true, "Applying Windows update and restarting…");
    }

    private static (bool ShouldExitApp, string Message) ApplyLinuxUpdate(string stagedFilePath, string currentExe)
    {
        if (stagedFilePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            string targetDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
            if (!HasDirectoryWriteAccess(targetDir))
            {
                RevealInFileManager(stagedFilePath);
                return (false, $"Install dir is read-only — saved update tarball to {stagedFilePath}");
            }

            string extractDir = Path.Combine(Path.GetDirectoryName(stagedFilePath)!, $"extract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            using (var fs = File.OpenRead(stagedFilePath))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gz, extractDir, overwriteFiles: true);
            }

            string? extractedBin = Directory
                .EnumerateFiles(extractDir, "HuaweiHROverlay", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (extractedBin == null)
                throw new InvalidDataException("Extracted Linux archive did not contain HuaweiHROverlay binary.");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(extractedBin,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // On Linux POSIX filesystems, renaming over a running executable unlinks the old inode cleanly!
            string tempDest = currentExe + ".new";
            File.Copy(extractedBin, tempDest, overwrite: true);
            File.Move(tempDest, currentExe, overwrite: true);
            try { Directory.Delete(extractDir, recursive: true); } catch { }

            Process.Start(new ProcessStartInfo(currentExe)
            {
                WorkingDirectory = targetDir,
                UseShellExecute = false
            });
            return (true, "Updated Linux binary — restarting…");
        }

        // System .deb / .rpm package: open with desktop software installer or reveal
        RevealInFileManager(stagedFilePath);
        string cmdHint = stagedFilePath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
            ? $"sudo apt install \"{stagedFilePath}\""
            : $"sudo dnf install \"{stagedFilePath}\"";
        return (false, $"Package verified! Run: {cmdHint}");
    }

    private static bool HasDirectoryWriteAccess(string directoryPath)
    {
        try
        {
            string probe = Path.Combine(directoryPath, $".hr-update-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    private static void RevealInFileManager(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(filePath)}\"") { UseShellExecute = false });
        }
        catch { }
    }

    private static string FormatReleaseNotesSummary(string rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
            return "Bug fixes and cross-platform improvements.";

        var lines = rawMarkdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l)
                     && !l.StartsWith("|")
                     && !l.StartsWith("*(full changelog", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("---"))
            .Select(l => l.TrimStart('#').Trim().Replace("**", "").Replace("`", ""))
            .Take(10)
            .ToList();

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "New version available on GitHub.";
    }

    public void Dispose() => _http.Dispose();
}
