using System.Windows.Forms;

namespace DalamudActCompat.Host.ActApi;

public interface IActPluginV1
{
    void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText);

    void DeInitPlugin();
}
