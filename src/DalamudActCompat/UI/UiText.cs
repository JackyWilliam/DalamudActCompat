using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

public sealed class UiText(PluginConfiguration configuration)
{
    public bool IsChinese => !string.Equals(configuration.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public string Get(string chinese, string english) => IsChinese ? chinese : english;
}
