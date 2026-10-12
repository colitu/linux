using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private int _linuxSudoPid = -1;
    private const string _tag = "CoreAdminHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        if (_config != null)
        {
            return;
        }
        _config = config;
        _updateFunc = updateFunc;

        await Task.CompletedTask;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    public async Task<ProcessService?> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath)
    {
        // Root never runs the core copy in the user's data folder: only the install folder's copy,
        // and only when it and every folder above it belong to root (see RootCoreGuard).
        var rootCore = RootCoreGuard.ResolveForRoot(fileName, out var reason);
        if (rootCore == null)
        {
            Logging.SaveLog($"{_tag}: core not run as root ({reason})");
            throw new UnauthorizedAccessException(RootCoreRefusedMessage);
        }

        // sudo is started directly with the command as arguments (no shell script in the user's
        // data folder that something running as the user could swap to read the password).
        var args = new StringBuilder("-S -- ");
        // Passing environment variables to the sudo command, here it only xray or sing-box.
        var envArgs = coreInfo.Environment.Where(kv => kv.Value.IsNotEmpty()).Select(kv => $"{kv.Key}={kv.Value.AppendQuotes()}").ToList();
        if (envArgs.Count > 0)
        {
            args.Append("env ").Append(string.Join(" ", envArgs)).Append(' ');
        }
        args.Append(rootCore.AppendQuotes()).Append(' ')
            .Append(string.Format(coreInfo.Arguments, Utils.GetBinConfigPath(configPath).AppendQuotes()));

        var procService = new ProcessService(
            fileName: SudoPath(),
            arguments: args.ToString(),
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: true,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        await procService.StartAsync(AppManager.Instance.LinuxSudoPwd);

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }
        _linuxSudoPid = procService.Id;

        return procService;
    }

    /// <summary>Shown when TUN mode is refused because the core is not in a root-owned install folder.</summary>
    public const string RootCoreRefusedMessage = "TUN mode needs the VPN core in a root-owned install folder (install the .deb/.rpm package); proxy mode still works.";

    private static string SudoPath() => File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : File.Exists("/bin/sudo") ? "/bin/sudo" : "sudo";

    public async Task KillProcessAsLinuxSudo()
    {
        if (_linuxSudoPid < 0)
        {
            return;
        }

        try
        {
            var shellFileName = Utils.IsMacOS() ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName;
            // The script text goes to root's bash straight from the app (bash -c), not through
            // a file in the user's data folder that anything running as the user could rewrite.
            var script = EmbedUtils.GetEmbedText(shellFileName).Replace("\r\n", "\n");
            var arg = new List<string>() { "-S", "-p", "", "--", Global.LinuxBash, "-c", script, "kill_as_sudo", _linuxSudoPid.ToString() };
            var result = await Cli.Wrap(SudoPath())
                .WithArguments(arg)
                .WithStandardInputPipe(PipeSource.FromString(AppManager.Instance.LinuxSudoPwd))
                .ExecuteBufferedAsync();

            await UpdateFunc(false, result.StandardOutput.ToString());

            await Task.Delay(1000); // Wait for a second to ensure the process is killed
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        _linuxSudoPid = -1;
    }
}
