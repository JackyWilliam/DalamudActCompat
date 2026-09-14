using System.Net.Http;
using DalamudActCompat.Compatibility.PluginHost;

namespace DalamudActCompat.Plugin;

public sealed partial class Plugin
{
    private int simulantDownloadActive;

    private void DownloadSimulant()
    {
        if (Interlocked.CompareExchange(ref simulantDownloadActive, 1, 0) != 0)
        {
            return;
        }
        SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
            ThirdPartyPluginInstallState.Preflighting,
            "仿生石 / Simulant", "simulant", SimulantDownload.Version,
            Detail: "正在从 MnFeN/Simulant 下载并校验原版 DLL……"));
        StartBackgroundOperation(async () =>
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(bundledUpdateCancellation.Token);
                cancellation.CancelAfter(TimeSpan.FromMinutes(5));
                using var client = new HttpClient();
                var path = await SimulantDownload.DownloadAsync(
                    paths.BundledPluginUpdateCacheDirectory, client, cancellation.Token).ConfigureAwait(false);
                InstallActPlugin(path);
            }
            catch (Exception exception)
            {
                SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Failed,
                    "仿生石 / Simulant", "simulant", SimulantDownload.Version,
                    Detail: exception.GetBaseException().Message,
                    Diagnostic: exception.ToString()));
                logger.Error(exception, "Simulant download failed.");
            }
            finally
            {
                Volatile.Write(ref simulantDownloadActive, 0);
            }
        });
    }
}
