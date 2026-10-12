using System.Globalization;

namespace ServiceLib.Common;

/// <summary>
/// Which core binary may be started as root (TUN mode, through sudo). A packaged install keeps a
/// copy of the cores in the user's data folder (~/.local/share/ColituVPN/bin) for the unprivileged
/// core; anything running as the user can replace that copy, so root never runs it. Root runs the
/// same file from the install folder's bin (/opt/colitu-vpn/bin), and only when that file and
/// every folder above it belong to root and nobody else can write them. A portable copy in a
/// user-owned folder fails the check: TUN is refused there, proxy mode keeps working.
/// </summary>
public static class RootCoreGuard
{
    /// <summary>Owner uid and permission bits (st_mode &amp; 07777) of a path; null when it can't be read.</summary>
    public delegate (long Uid, int Mode)? StatFunc(string path);

    private const int GroupOrOtherWrite = 0b000_010_010; // 0022

    /// <summary>
    /// The file root may run for <paramref name="fileName"/> (a core under <paramref name="dataBin"/>):
    /// the same relative path under <paramref name="installBin"/>. A file outside the data bin keeps its path.
    /// </summary>
    public static string InstallCopyOf(string fileName, string dataBin, string installBin)
    {
        // Unix paths as strings: the same on every host the tests run on.
        var data = dataBin.TrimEnd('/') + "/";
        if (!fileName.StartsWith(data, StringComparison.Ordinal))
        {
            return fileName;
        }
        var relative = fileName[data.Length..];
        if (relative.Length == 0 || relative.Split('/').Any(part => part is "" or "." or ".."))
        {
            return fileName;
        }
        return installBin.TrimEnd('/') + "/" + relative;
    }

    /// <summary>
    /// True when <paramref name="realPath"/> (no symlinks left) and each folder above it up to "/"
    /// are owned by root (uid 0) and not writable by group or others.
    /// </summary>
    public static bool IsRootOwnedChain(string realPath, StatFunc stat, out string reason)
    {
        if (!realPath.StartsWith('/') || realPath.Split('/').Any(part => part is "." or ".."))
        {
            reason = $"{realPath}: not an absolute path";
            return false;
        }
        for (var path = realPath; path != null; path = ParentOf(path))
        {
            var info = stat(path);
            if (info is not { } value)
            {
                reason = $"{path}: cannot be checked";
                return false;
            }
            if (value.Uid != 0)
            {
                reason = $"{path}: owned by uid {value.Uid}, not root";
                return false;
            }
            if ((value.Mode & GroupOrOtherWrite) != 0)
            {
                reason = $"{path}: writable by group or others ({Convert.ToString(value.Mode, 8)})";
                return false;
            }
        }
        reason = "";
        return true;
    }

    /// <summary>The folder above a Unix path ("/" for a top-level entry); null for "/" itself.</summary>
    internal static string? ParentOf(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (trimmed == "/")
        {
            return null;
        }
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>
    /// The core root may run for <paramref name="fileName"/>: the install folder's copy with every
    /// symlink resolved, after the ownership check. Null (with the reason) when there is none or it
    /// is not safe; the caller then must not start anything as root.
    /// </summary>
    public static string? ResolveForRoot(string fileName, out string reason) =>
        ResolveForRoot(fileName, Utils.GetBinPath(""), Utils.GetBaseDirectory("bin"), RealPath, StatPath, out reason);

    public static string? ResolveForRoot(string fileName, string dataBin, string installBin, Func<string, string?> realPath, StatFunc stat, out string reason)
    {
        var candidate = InstallCopyOf(fileName, dataBin, installBin);
        // Never the user's copy: a core missing from the install folder is not run as root at all.
        var real = realPath(candidate);
        if (real == null)
        {
            reason = $"{candidate}: not found in the install folder";
            return null;
        }
        return IsRootOwnedChain(real, stat, out reason) ? real : null;
    }

    /// <summary>
    /// The launcher sudo runs (scripts/colitu-core-root, installed root:root 0755 in the install bin):
    /// it copies and checks the config (RootConfigPolicy) before starting the core as root.
    /// </summary>
    public const string LauncherName = "colitu-core-root";

    /// <summary>
    /// The launcher root may run: the install folder's colitu-core-root with every symlink resolved,
    /// after the same ownership check as the cores. Null (with the reason) when missing or not safe.
    /// </summary>
    public static string? ResolveLauncher(out string reason) =>
        ResolveLauncher(Utils.GetBaseDirectory("bin"), RealPath, StatPath, out reason);

    /// <summary>
    /// This app's exe with every symlink resolved (as sing-box sees it in /proc/&lt;pid&gt;/exe) when
    /// it and every folder above it belong to root and nobody else can write them; otherwise null.
    /// The TUN ping rule names the exe only then: a path the user can write could hold anything.
    /// </summary>
    public static string? TrustedAppPath() => TrustedAppPath(Utils.GetExePath(), RealPath, StatPath);

    public static string? TrustedAppPath(string? exePath, Func<string, string?> realPath, StatFunc stat)
    {
        if (string.IsNullOrEmpty(exePath) || realPath(exePath) is not { } real)
        {
            return null;
        }
        return IsRootOwnedChain(real, stat, out _) ? real : null;
    }

    public static string? ResolveLauncher(string installBin, Func<string, string?> realPath, StatFunc stat, out string reason)
    {
        var candidate = installBin.TrimEnd('/') + "/" + LauncherName;
        var real = realPath(candidate);
        if (real == null)
        {
            reason = $"{candidate}: not found in the install folder";
            return null;
        }
        return IsRootOwnedChain(real, stat, out reason) ? real : null;
    }

    /// <summary>
    /// The install bin as the launcher sees it (symlinks resolved): root-run configs name the rule
    /// sets shipped there. Windows and unreadable paths keep the plain install path.
    /// </summary>
    public static string InstallBin()
    {
        var bin = Utils.GetBaseDirectory("bin").TrimEnd('/', '\\');
        return Utils.IsWindows() ? bin : RealPath(bin) ?? bin;
    }

    /// <summary>The path with every symlink resolved (each component, like realpath(3)); null on failure.</summary>
    public static string? RealPath(string path)
    {
        try
        {
            var current = "/";
            var parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                var next = Path.Combine(current, parts[index]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists)
                {
                    return null;
                }
                if (info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null || !target.Exists)
                    {
                        return null;
                    }
                    // The target may itself sit below symlinked folders: resolve it from the start.
                    var rest = string.Join('/', parts.Skip(index + 1));
                    return RealPath(rest.Length == 0 ? target.FullName : Path.Combine(target.FullName, rest));
                }
                current = next;
            }
            return current;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Owner and mode through coreutils stat (absolute path, no PATH lookup); null when unreadable.</summary>
    private static (long Uid, int Mode)? StatPath(string path)
    {
        try
        {
            var stat = File.Exists("/usr/bin/stat") ? "/usr/bin/stat" : "/bin/stat";
            var startInfo = new ProcessStartInfo(stat)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "-c", "%u %a", "--", path })
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
            {
                return null;
            }
            return ParseStat(output);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"&lt;uid&gt; &lt;octal mode&gt;" as printed by <c>stat -c '%u %a'</c>.</summary>
    public static (long Uid, int Mode)? ParseStat(string? output)
    {
        var parts = (output ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || parts[1].Length is 0 or > 4 || parts[1].Any(ch => ch is < '0' or > '7'))
        {
            return null;
        }
        return (uid, Convert.ToInt32(parts[1], 8));
    }
}
