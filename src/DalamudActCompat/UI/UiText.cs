using DalamudActCompat.Plugin;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed class UiText(PluginConfiguration configuration)
{
    public string Language => UiLanguages.Normalize(configuration.UiLanguage);
    public bool IsChinese => Language is "zh-CN" or "zh-TW";

    public string Get(string chinese, string english)
        => Language == "zh-CN" ? chinese : UiTranslations.Get(Language, english);

    // Keep arguments separate from the translation key. Names, paths and user
    // content must never enter the catalog or be changed by translation rules.
    public string Format(string chinese, FormattableString english)
        => Language == "zh-CN" ? chinese : string.Format(System.Globalization.CultureInfo.CurrentCulture,
            UiTranslations.Get(Language, english.Format), english.GetArguments());

    // Only application-owned status messages pass through this boundary. Account
    // names, notes, chat text, and announcement bodies must retain their original text.
    internal string SystemMessage(string message) => Language == "zh-CN" ? message : SystemStatusText.Localize(message, Language);

    internal string MessageBody(CloudChatMessage message) => message.QuickMessageId switch
    {
        CloudChatPolicy.InviteNext => Get("下把邀我", "Invite me next time"),
        CloudChatPolicy.WhenFinished => Get("你什么时候结束", "When will you finish?"),
        _ => message.Text,
    };
}
