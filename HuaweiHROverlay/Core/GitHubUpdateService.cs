using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HuaweiHROverlay.Core;

public enum AppBuildVariant
{
    Full,
    Lite
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
    DateTimeOffset? PublishedAt);

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
/// Checks GitHub Releases (fishuvn/huawei-hr-overlay) for newer versions,
/// downloads the matching single-file EXE variant (Full or Lite) with SHA-256 verification,
/// and applies the update safely via a post-exit helper script.
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
            // Infinite overall timeout so large self-contained EXE downloads (~185 MB)
            // don't abort at 100s on slower connections; individual operations enforce timeouts.
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
        return v != null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.0.0";
    }

    public static AppBuildVariant DetectCurrentBuildVariant()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var meta in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(meta.Key, "BuildVariant", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(meta.Value, "lite", StringComparison.OrdinalIgnoreCase))
                    return AppBuildVariant.Lite;
                if (string.Equals(meta.Value, "full", StringComparison.OrdinalIgnoreCase))
                    return AppBuildVariant.Full;
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string fileName = Path.GetFileName(processPath);
            if (fileName.Contains("lite", StringComparison.OrdinalIgnoreCase))
                return AppBuildVariant.Lite;

            if (!fileName.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(processPath))
            {
                try
                {
                    long len = new FileInfo(processPath).Length;
                    // Self-contained single-file WPF build is ~150-190 MB; lite is ~2-30 MB
                    return len > 0 && len < 60L * 1024 * 1024
                        ? AppBuildVariant.Lite
                        : AppBuildVariant.Full;
                }
                catch { /* fallback below */ }
            }
        }

        return AppBuildVariant.Full;
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
        var exes = assets
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(a.DownloadUrl))
            .ToList();

        if (exes.Count == 0) return null;

        if (variant == AppBuildVariant.Lite)
        {
            return exes.FirstOrDefault(a => a.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
                ?? exes[0];
        }

        return exes.FirstOrDefault(a => !a.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
            ?? exes[0];
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, checkCts.Token);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub API rate limit reached. Try again in a few minutes or open Releases in your browser.");

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
            safeFileName = "HuaweiHROverlay-update.exe";

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
            byte[] header = new byte[2];
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

                    if (headerRead < 2)
                    {
                        int toCopy = Math.Min(2 - headerRead, read);
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

                if (totalRead < 64 * 1024 || headerRead < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidDataException("Downloaded file is not a valid Windows executable.");
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

    public static (bool Launched, string Message) LaunchSelfUpdate(string stagedExePath)
    {
        if (!File.Exists(stagedExePath))
            return (false, "Downloaded update file was not found.");

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe)
            || Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            OpenFileInExplorer(stagedExePath);
            return (false, "Running inside dotnet host — opened downloaded EXE in Explorer.");
        }

        string targetDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        bool requiresElevation = !HasDirectoryWriteAccess(targetDir);

        string stageDir = Path.Combine(Path.GetTempPath(), "HuaweiHROverlay-Update");
        Directory.CreateDirectory(stageDir);
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

            try {
                Wait-Process -Id $waitPid -Timeout 30 -ErrorAction SilentlyContinue
            } catch {}

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
        if (requiresElevation)
            psi.Verb = "runas";

        Process.Start(psi);
        return (true, "Applying update and restarting…");
    }

    private static bool HasDirectoryWriteAccess(string directoryPath)
    {
        try
        {
            string probe = Path.Combine(directoryPath, $".hr-update-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void OpenFileInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    private static string FormatReleaseNotesSummary(string rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
            return "Bug fixes and improvements.";

        var lines = rawMarkdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l)
                     && !l.StartsWith("|") // skip markdown tables in compact summary
                     && !l.StartsWith("*(full changelog", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("---"))
            .Select(l =>
            {
                l = l.TrimStart('#').Trim();
                l = l.Replace("**", "").Replace("`", "");
                return l;
            })
            .Take(10)
            .ToList();

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "New version available on GitHub.";
    }

    public void Dispose() => _http.Dispose();
}
