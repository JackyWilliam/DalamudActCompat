using DalamudActCompat.Plugin;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed class UiText(PluginConfiguration configuration)
{
    public bool IsChinese => !string.Equals(configuration.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public string Get(string chinese, string english) => IsChinese ? chinese : english;

    // Only application-owned status messages pass through this boundary. Account
    // names, notes, chat text, and announcement bodies must retain their original text.
    internal string SystemMessage(string message) => IsChinese ? message : SystemStatusText.English(message);

    internal string MessageBody(CloudChatMessage message) => message.QuickMessageId switch
    {
        CloudChatPolicy.InviteNext => Get("下把邀我", "Invite me next time"),
        CloudChatPolicy.WhenFinished => Get("你什么时候结束", "When will you finish?"),
        _ => message.Text,
    };
}
