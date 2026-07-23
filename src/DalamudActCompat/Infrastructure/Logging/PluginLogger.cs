using Dalamud.Plugin.Services;

namespace DalamudActCompat.Infrastructure.Logging;

public sealed class PluginLogger
{
    private readonly IPluginLog log;

    public PluginLogger(IPluginLog log)
    {
        this.log = log;
    }

    public void Information(string message) => log.Information(message);

    public void Warning(string message) => log.Warning(message);

    public void Error(Exception exception, string message) => log.Error(exception, message);

    public void Debug(string message, bool enabled)
    {
        if (enabled)
        {
            log.Debug(message);
        }
    }
}
