using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Updates for the Linux packages. The release manifest
/// (colitu.com/downloads/linux/latest.json) is signed offline; the matching
/// .deb or .rpm is downloaded, checked against the signed SHA256 and installed
/// with pkexec, which asks the user through the desktop's own polkit prompt.
/// Installations that did not come from a package (a copied folder) open the
/// download page instead.
/// </summary>
public sealed class ColituUpdateService
{
    public static ColituUpdateService Instance { get; } = new();

    private static readonly int LocalVersionCode = ColituAuthService.VersionCode(ColituAuthService.ClientVersion);

    /// <summary>Folder under the data directory that holds a downloaded update (owner only).</summary>
    public const string UpdateFolderName = "guiUpdates";

    /// <summary>Directory the packages install to; anything else is a manual installation.</summary>
    public const string PackageInstallDir = "/opt/colitu-vpn";
    private const string PackageName = "colitu-vpn";

    public const string DownloadPageUrl = $"{ColituAuthService.WebBaseUrl}/download/linux";

    private static string ManifestUrl =>
#if DEBUG
        Environment.GetEnvironmentVariable("COLITU_UPDATE_MANIFEST_URL")?.Trim().NullIfEmpty() ??
#endif
        $"{ColituAuthService.WebBaseUrl}/downloads/linux/latest.json";

    private static readonly string LocalVersionName = ColituAuthService.ClientVersion;

    /// <summary>A package larger than this is not downloaded (the real ones are well under 200 MB).</summary>
    internal const long MaxPackageBytes = 512L << 20;
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(30);

    // The manifest is read into memory (a few hundred bytes): a broken or hostile answer cannot grow without bound.
    private readonly HttpClient _httpClient = new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxManifestBytes };
    // HttpClient.Timeout also cancels content streaming, so packages get their own client with a generous limit.
    private readonly HttpClient _downloadClient = new(CreateHandler()) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>The release manifest is a few hundred bytes.</summary>
    internal const int MaxManifestBytes = 256 * 1024;

    /// <summary>Most redirects followed for one manifest or package request.</summary>
    internal const int MaxRedirects = 3;

    /// <summary>
    /// Handler for the manifest and the package. Redirects are not followed automatically
    /// (<see cref="SendCheckedAsync"/> follows them by hand and checks every hop); the certificate
    /// pin sits on the handler, so it applies to the host of a followed redirect too.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() =>
        ColituCertPins.Pin(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<ColituUpdateInfo>? UpdateAvailable;

    private ColituUpdateService() { }

    /// <summary>The package format this installation came from, or null for a manual installation.</summary>
    public static string? InstalledPackageKind()
    {
        var exe = Utils.GetExePath();
        if (!exe.StartsWith(PackageInstallDir + "/", StringComparison.Ordinal))
        {
            return null;
        }
        if (File.Exists("/usr/bin/dpkg") && File.Exists("/var/lib/dpkg/info/colitu-vpn.list"))
        {
            return "deb";
        }
        // Only when rpm itself installed Colitu: a tarball copied to /opt on a Debian system
        // with the rpm tool present must not be "updated" with rpm -U --force.
        if (File.Exists("/usr/bin/rpm") && IsRpmInstalled())
        {
            return "rpm";
        }
        return null;
    }

    private static bool IsRpmInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/rpm")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "-q", PackageName })
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var process = Process.Start(startInfo);
            if (process == null || !process.WaitForExit(5000))
            {
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ColituUpdateInfo?> CheckForUpdateAsync(bool ignoreAttemptCache = false)
    {
        try
        {
            ClearCompletedAttempt();
            using var response = await SendCheckedAsync(_httpClient, new Uri(ManifestUrl), request =>
            {
                request.Headers.TryAddWithoutValidation("X-Client-Platform", ColituAuthService.ClientPlatform);
                request.Headers.TryAddWithoutValidation("X-App-Version", LocalVersionName);
                request.Headers.TryAddWithoutValidation("User-Agent", $"ColituVPN/{LocalVersionName} Linux");
            }, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<ColituLinuxVersionPayload>(_jsonOptions);
            if (payload == null) return null;
            if (!ColituUpdateSignature.VerifyV2(payload))
            {
                Logging.SaveLog("ColituUpdateService: release manifest signature_v2 is missing or invalid; update ignored");
                return null;
            }
            if (!ColituUpdateSignature.IsFresh(payload, DateTimeOffset.UtcNow, out var staleReason))
            {
                Logging.SaveLog($"ColituUpdateService: release manifest rejected ({staleReason}); no update offered");
                return null;
            }

            var remoteCode = payload.LatestVersionCode;
            var remoteName = payload.VersionName ?? remoteCode.ToString(CultureInfo.InvariantCulture);
            if (!IsRemoteVersionNewer(remoteName, remoteCode)) return null;

            // The signed manifest carries the x64 packages only: on any other architecture apt/dnf
            // would refuse them, so the update opens the download page instead.
            var kind = RuntimeInformation.OSArchitecture == Architecture.X64 ? InstalledPackageKind() : null;
            var package = kind == "deb" ? payload.Deb : kind == "rpm" ? payload.Rpm : null;
            if (!ignoreAttemptCache && WasRecentlyAttempted(remoteCode, package?.Url, package?.Sha256))
            {
                return null;
            }

            var info = new ColituUpdateInfo
            {
                VersionCode = remoteCode,
                CurrentVersion = LocalVersionName,
                NewVersion = remoteName,
                PackageKind = package == null ? null : kind,
                DownloadUrl = package?.Url ?? DownloadPageUrl,
                Sha256 = package?.Sha256,
                Force = payload.ForceUpdate,
                Notes = payload.ReleaseNotes ?? ""
            };
            if (info.CanInstall)
            {
                ValidateDownloadUrl(info.DownloadUrl);
            }
            UpdateAvailable?.Invoke(info);
            return info;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituUpdateService.CheckForUpdateAsync", ex);
            return null;
        }
    }

    public async Task DownloadUpdateAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadUpdateOnceAsync(info, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    private async Task DownloadUpdateOnceAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!info.CanInstall)
        {
            throw new InvalidOperationException("This installation cannot update itself.");
        }
        ValidateDownloadUrl(info.DownloadUrl);
        if (!IsSha256Hex(info.Sha256))
        {
            throw new InvalidOperationException("The release manifest does not carry a valid SHA256 for the update.");
        }
        var updateDir = ColituHardening.CreateProtectedDirectory(Path.Combine(Utils.StartupPath(), UpdateFolderName));
        var destPath = Path.Combine(updateDir, $"colitu-vpn-{info.VersionCode}.{info.PackageKind}");

        try
        {
            await DownloadToAsync(info, destPath, progress, cancellationToken);
            VerifySha256(destPath, info.Sha256);
        }
        catch
        {
            // A cancelled, broken or wrong download is never kept (up to 100 MB).
            try { File.Delete(destPath); } catch { }
            throw;
        }
        info.LocalPath = destPath;
    }

    private async Task DownloadToAsync(ColituUpdateInfo info, string destPath, IProgress<ColituDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await SendCheckedAsync(_downloadClient, new Uri(info.DownloadUrl), null, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        if (total > MaxPackageBytes)
        {
            throw new InvalidOperationException("The update package is too large.");
        }
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var dest = File.Create(destPath))
        {
            var buffer = new byte[65536];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                downloaded += read;
                if (downloaded > MaxPackageBytes)
                {
                    // The size header is not trusted: never fill the disk with a runaway download.
                    throw new InvalidOperationException("The update package is too large.");
                }
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total > 0)
                {
                    progress?.Report(new ColituDownloadProgress
                    {
                        TotalBytes = total,
                        DownloadedBytes = downloaded,
                        Percent = (int)(downloaded * 100 / total)
                    });
                }
            }
        }
    }

    /// <summary>
    /// Installs the downloaded package through pkexec (polkit asks the user) and
    /// starts the new version once the package manager is done. The package's
    /// own scripts stop nothing: this process exits after the install succeeds.
    /// </summary>
    public async Task InstallAsync(ColituUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.LocalPath) || !File.Exists(info.LocalPath))
        {
            throw new InvalidOperationException("Update file not downloaded.");
        }
        // Checked again right before root sees the file; the hash goes to root's script as an argument.
        if (!IsSha256Hex(info.Sha256))
        {
            throw new InvalidOperationException("The release manifest does not carry a valid SHA256 for the update.");
        }
        VerifySha256(info.LocalPath, info.Sha256!);
        SaveUpdateState(new ColituUpdateState
        {
            AttemptedVersionCode = info.VersionCode,
            AttemptedVersionName = info.NewVersion,
            DownloadUrl = info.DownloadUrl,
            Sha256 = info.Sha256,
            AttemptedAt = DateTimeOffset.Now
        });

        // Absolute paths: pkexec would otherwise look the program up in this user's PATH.
        var installer = info.PackageKind == "deb"
            ? new[] { ColituShell.SystemBinary("apt-get"), "install", "-y", "--allow-downgrades" }
            : File.Exists("/usr/bin/dnf")
                ? ["/usr/bin/dnf", "install", "-y"]
                : File.Exists("/usr/bin/zypper")
                    ? ["/usr/bin/zypper", "--non-interactive", "install", "--allow-unsigned-rpm"]
                    : [ColituShell.SystemBinary("rpm"), "-U", "--force"];

        // The package sits in this user's folder, and the polkit prompt can stay
        // open for minutes: verifying it here is not enough. Root copies it into
        // a root-only temp folder, checks the SHA256 of that copy and installs
        // exactly that copy, so swapping the file during the prompt fails.
        const string script = """
            set -eu
            src="$1"; sum="$2"; ext="$3"; shift 3
            dir=$(mktemp -d /var/tmp/colitu-update.XXXXXX)
            trap 'rm -rf "$dir"' EXIT
            cp -- "$src" "$dir/package.$ext"
            echo "$sum  $dir/package.$ext" | sha256sum -c --quiet -
            "$@" "$dir/package.$ext"
            """;
        var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("pkexec"))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { ColituShell.SystemBinary("sh"), "-c", script, "colitu-update", info.LocalPath, info.Sha256!.Trim().ToLowerInvariant(), info.PackageKind! }.Concat(installer))
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pkexec could not be started.");
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            // 126/127: the user dismissed the polkit prompt or pkexec is missing.
            throw new InvalidOperationException($"Package installation failed ({process.ExitCode}): {(await stderr).Trim()}");
        }
        try { File.Delete(info.LocalPath); } catch { }
    }

    /// <summary>Starts the freshly installed version after this process has exited.</summary>
    public static void RelaunchAfterExit()
    {
        var exe = Path.Combine(PackageInstallDir, "ColituVPN");
        // Absolute paths, like every other program Colitu starts: never one from a user-writable PATH entry.
        var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("setsid")) { UseShellExecute = false };
        foreach (var arg in new[] { ColituShell.SystemBinary("sh"), "-c", $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.5; done; exec \"{exe}\"" })
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var _ = Process.Start(startInfo);
    }

    /// <summary>Exactly 64 hex digits (the SHA-256 the signed manifest carries).</summary>
    internal static bool IsSha256Hex(string? value) =>
        value?.Trim() is { Length: 64 } hex && hex.All(char.IsAsciiHexDigit);

    private static void VerifySha256(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            File.Delete(filePath);
            throw new InvalidOperationException("SHA256 verification failed. The downloaded file may be corrupted.");
        }
    }

    private static void ValidateDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Update download URL is invalid.");
        }
#if DEBUG
        if (uri.IsLoopback)
        {
            return;
        }
#endif
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Update download URL must use HTTPS.");
        }
        if (!TrustedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443)
        {
            throw new InvalidOperationException("Update download host is not trusted.");
        }
    }

    /// <summary>
    /// GET <paramref name="start"/> and follow up to <see cref="MaxRedirects"/> redirects by hand.
    /// Every hop must pass <see cref="IsAllowedRedirectTarget"/>; otherwise the request is abandoned
    /// with an <see cref="InvalidOperationException"/> (not retried by the download loop) and the
    /// rejected host is logged masked. The client's handler must have AllowAutoRedirect off.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendCheckedAsync(
        HttpClient client,
        Uri start,
        Action<HttpRequestMessage>? prepare,
        HttpCompletionOption completion,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? mirrorHosts = null)
    {
        var uri = start;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            prepare?.Invoke(request);
            var response = await client.SendAsync(request, completion, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
            {
                throw new InvalidOperationException("Update server sent a redirect without a target.");
            }
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (redirects + 1 > MaxRedirects)
            {
                LogRedirect($"ColituUpdateService: more than {MaxRedirects} redirects; download abandoned");
                throw new InvalidOperationException("Update server redirected too many times.");
            }
            if (!IsAllowedRedirectTarget(next, mirrorHosts))
            {
                LogRedirect($"ColituUpdateService: redirect to an untrusted address refused ({MaskForLog(next)}); download abandoned");
                throw new InvalidOperationException("Update server redirected to an untrusted address.");
            }
            uri = next;
        }
    }

    private static void LogRedirect(string message)
    {
        try
        {
            Logging.SaveLog(message);
        }
        catch
        {
            // Logging must not replace the refusal.
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MultipleChoices or System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found or
        System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// A redirect may lead only to https on port 443, without user info, to colitu.com, a subdomain
    /// of it, or a mirror host embedded by the build (<see cref="ColituNotices.SafeUrl"/>).
    /// </summary>
    /// <param name="mirrorHosts">Mirror hosts; null takes the ones embedded by the build.</param>
    internal static bool IsAllowedRedirectTarget(Uri uri, IReadOnlySet<string>? mirrorHosts = null)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }
#if DEBUG
        if (uri.IsLoopback)
        {
            return true;
        }
#endif
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Port == 443
            && string.IsNullOrEmpty(uri.UserInfo)
            && ColituNotices.SafeUrl(uri.AbsoluteUri, mirrorHosts) != null;
    }

    /// <summary>Scheme, port and only the last label of the host: no node or mirror name in logs.</summary>
    internal static string MaskForLog(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return "?";
        var host = uri.IdnHost.TrimEnd('.');
        var dot = host.LastIndexOf('.');
        var masked = dot > 0 && dot < host.Length - 1 && !char.IsDigit(host[^1]) ? "***" + host[dot..] : "***";
        return $"{uri.Scheme}://{(string.IsNullOrEmpty(uri.UserInfo) ? "" : "***@")}{masked}:{uri.Port}";
    }

    // Fixed list: the API address can be configured, so it must not widen where packages come from.
    private static readonly string[] TrustedDownloadHosts = ["colitu.com", "www.colitu.com", "api.colitu.com"];

    private static bool WasRecentlyAttempted(int versionCode, string? downloadUrl, string? sha256)
    {
        var state = LoadUpdateState();
        if (state?.AttemptedVersionCode != versionCode) return false;
        if (!string.Equals(state.DownloadUrl ?? "", downloadUrl ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(state.Sha256 ?? "", sha256 ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        return DateTimeOffset.Now - state.AttemptedAt < AttemptCooldown;
    }

    private static void ClearCompletedAttempt()
    {
        DeleteStalePackages(Path.Combine(Utils.StartupPath(), UpdateFolderName), LocalVersionCode);
        var state = LoadUpdateState();
        if (state == null) return;
        if (LocalVersionCode >= state.AttemptedVersionCode || DateTimeOffset.Now - state.AttemptedAt >= AttemptCooldown)
        {
            try { File.Delete(StatePath()); } catch { }
        }
    }

    /// <summary>
    /// Packages of a version this app already is (or is newer than) stay in <paramref name="folder"/>
    /// when an install failed or the app was updated another way, and an interrupted download of an
    /// older run may be there too: up to 100 MB each, never needed again.
    /// </summary>
    internal static void DeleteStalePackages(string folder, int localVersionCode)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(folder, "colitu-vpn-*.*"))
            {
                var name = Path.GetFileName(file);
                var dot = name.LastIndexOf('.');
                var extension = dot > 0 ? name[(dot + 1)..] : "";
                var code = dot > 0 ? name["colitu-vpn-".Length..dot] : "";
                if (extension is "deb" or "rpm"
                    && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version <= localVersionCode)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituUpdateService.DeleteStalePackages: {ex.GetType().Name}");
        }
    }

    private static ColituUpdateState? LoadUpdateState()
    {
        try
        {
            return File.Exists(StatePath())
                ? JsonSerializer.Deserialize<ColituUpdateState>(File.ReadAllText(StatePath()))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveUpdateState(ColituUpdateState state)
    {
        try
        {
            File.WriteAllText(StatePath(), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool IsRemoteVersionNewer(string? remoteVersionName, int remoteVersionCode)
    {
        var comparison = CompareSemanticVersions(remoteVersionName, LocalVersionName);
        return comparison.HasValue ? comparison.Value > 0 : remoteVersionCode > LocalVersionCode;
    }

    private static int? CompareSemanticVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        if (leftParts == null || rightParts == null) return null;

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    private static int[]? ParseVersionParts(string? version)
    {
        var clean = (version ?? "").Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)) clean = clean[1..];
        clean = clean.Split(['+', '-'], 2)[0];
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _))) return null;
        return parts.Select(int.Parse).ToArray();
    }

    private static string StatePath() => Utils.GetConfigPath("colitu-update-state.json");
}

public sealed class ColituUpdateInfo
{
    public int VersionCode { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    /// <summary>"deb" or "rpm" when this installation can update itself; null opens the download page.</summary>
    public string? PackageKind { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool Force { get; set; }
    public string Notes { get; set; } = "";
    public string? LocalPath { get; set; }
    public bool CanInstall => PackageKind != null && ColituUpdateService.IsSha256Hex(Sha256);
}

public sealed class ColituDownloadProgress
{
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public int Percent { get; init; }
}

internal sealed class ColituLinuxVersionPayload
{
    public int LatestVersionCode { get; set; }
    public string? VersionName { get; set; }
    public bool ForceUpdate { get; set; }
    public string? ReleaseNotes { get; set; }
    public ColituLinuxPackage? Deb { get; set; }
    public ColituLinuxPackage? Rpm { get; set; }
    /// <summary>ISO-8601 UTC time the manifest was signed (part of the signature_v2 text).</summary>
    [JsonPropertyName("issued_at")]
    public string? IssuedAt { get; set; }
    /// <summary>ISO-8601 UTC time after which the manifest is void (part of the signature_v2 text).</summary>
    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }
    /// <summary>Base64 ECDSA P-256 signature over <see cref="ColituUpdateSignature.Message"/>.</summary>
    public string? Signature { get; set; }
    /// <summary>Base64 ECDSA P-256 signature over <see cref="ColituUpdateSignature.MessageV2"/> (v1 text + issued_at + expires_at).</summary>
    [JsonPropertyName("signature_v2")]
    public string? SignatureV2 { get; set; }
}

internal sealed class ColituLinuxPackage
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>
/// The release manifest is signed offline with the same release key as the
/// Windows manifest; the "colitu-linux-update-v1" prefix keeps a Windows
/// signature from being replayed here. Packages are installed as root, so
/// HTTPS alone is not enough: a compromised website must not be able to hand
/// every client a package.
/// </summary>
internal static class ColituUpdateSignature
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEb7h8TtW4ekewQccnpdJo2i0fsJ28
        9gl8IkgEiNIAJvkcXryv7AZUf9O4qZboDzW7Jg2rYpnGJrVkxa1HDRmk8Q==
        -----END PUBLIC KEY-----
        """;

    /// <summary>The exact text scripts/sign-linux-manifest.py signs; changing any field breaks the signature.</summary>
    internal static string Message(ColituLinuxVersionPayload payload) => string.Join("\n",
        "colitu-linux-update-v1",
        payload.LatestVersionCode.ToString(CultureInfo.InvariantCulture),
        payload.VersionName ?? "",
        payload.Deb?.Url ?? "",
        (payload.Deb?.Sha256 ?? "").Trim().ToLowerInvariant(),
        payload.Rpm?.Url ?? "",
        (payload.Rpm?.Sha256 ?? "").Trim().ToLowerInvariant(),
        payload.ForceUpdate ? "true" : "false");

    /// <summary>
    /// The text <c>signature_v2</c> signs: the unchanged v1 text plus issued_at and expires_at. The v1
    /// text and <c>signature</c> stay exactly as they were, so clients up to 1.4.3 keep verifying the manifest.
    /// </summary>
    internal static string MessageV2(ColituLinuxVersionPayload payload) => string.Join("\n",
        Message(payload),
        payload.IssuedAt ?? "",
        payload.ExpiresAt ?? "");

    /// <summary>A manifest older than this is not offered, so a replayed old one can't hold back a newer release.</summary>
    internal static readonly TimeSpan MaxManifestAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Freshness of an already verified manifest: it must carry issued_at and expires_at, must not
    /// be issued more than <see cref="MaxManifestAge"/> ago and must not be expired.
    /// </summary>
    internal static bool IsFresh(ColituLinuxVersionPayload payload, DateTimeOffset now, out string reason)
    {
        if (!TryParseUtc(payload.IssuedAt, out var issuedAt) || !TryParseUtc(payload.ExpiresAt, out var expiresAt))
        {
            reason = "issued_at/expires_at missing or invalid";
            return false;
        }
        if (now - issuedAt > MaxManifestAge)
        {
            reason = "issued_at is more than 30 days old";
            return false;
        }
        if (now >= expiresAt)
        {
            reason = "expires_at has passed";
            return false;
        }
        reason = "";
        return true;
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset result)
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    }

    /// <summary>The v1 signature (what clients up to 1.4.3 check). The current client uses <see cref="VerifyV2"/> only.</summary>
    internal static bool Verify(ColituLinuxVersionPayload payload, string publicKeyPem = PublicKeyPem) =>
        VerifyText(Message(payload), payload.Signature, publicKeyPem);

    /// <summary>The v2 signature over the v1 text plus issued_at/expires_at; a manifest without it is rejected.</summary>
    internal static bool VerifyV2(ColituLinuxVersionPayload payload, string publicKeyPem = PublicKeyPem) =>
        VerifyText(MessageV2(payload), payload.SignatureV2, publicKeyPem);

    private static bool VerifyText(string message, string? signature, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ColituUpdateState
{
    public int AttemptedVersionCode { get; set; }
    public string AttemptedVersionName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}
