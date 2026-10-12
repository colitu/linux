namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenLog()
    {
        try
        {
            switch (_config.CoreBasicItem.Loglevel)
            {
                case "debug":
                case "info":
                case "error":
                    _coreConfig.log.level = _config.CoreBasicItem.Loglevel;
                    break;

                case "warning":
                    _coreConfig.log.level = "warn";
                    break;

                default:
                    break;
            }
            if (_config.CoreBasicItem.Loglevel == Global.None)
            {
                _coreConfig.log.disabled = true;
            }
            // A core running as root (TUN) logs to its output only: colitu-core-root refuses log files.
            if (_config.CoreBasicItem.LogEnabled && !context.RunsCoreAsRoot)
            {
                var dtNow = DateTime.Now;
                _coreConfig.log.output = Utils.GetLogPath($"sbox_{dtNow:yyyy-MM-dd}.txt");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }
}
