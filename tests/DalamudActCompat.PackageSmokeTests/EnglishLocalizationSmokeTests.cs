using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class EnglishLocalizationSmokeTests
{
    internal static void Run()
    {
        var configuration = new PluginConfiguration { UiLanguage = "en" };
        var text = new UiText(configuration);
        var saved = CloudClientSnapshot.SignedOut();
        Check(text.SystemMessage(saved.StatusMessage) == "Please sign in or create an account.", "Signed-out cloud status was not translated.");
        configuration.UiLanguage = "zh-CN";
        Check(text.SystemMessage(saved.StatusMessage) == saved.StatusMessage, "Switching back changed the stored Chinese status.");
        configuration.UiLanguage = "EN";
        Check(text.SystemMessage("备注已保存并同步。") == "Note saved and synced.", "Live language switching missed friend note status.");
        Check(text.SystemMessage("已刷新，共 2 个云端版本。") == "Refreshed: 2 cloud backups.", "Backup count was lost in translation.");
        Check(text.SystemMessage("上传完成：2026-09-27 14:03:26。") == "Upload complete: 2026-09-27 14:03:26.", "Upload timestamp changed.");
        Check(text.SystemMessage("云服务请求失败（HTTP 503）。") == "Cloud request failed (HTTP 503).", "HTTP diagnostics changed.");
        const string diagnostic = "C:\\朋友\\状态已保存。\\file.json: access denied";
        Check(text.SystemMessage("自动登录状态未能保存：" + diagnostic) == "Automatic sign-in could not be saved: " + diagnostic,
            "Translation rewrote a diagnostic path containing Chinese text.");
        Check(text.SystemMessage("登录成功，但自动登录状态未能保存：disk full。") == "Signed in, but Automatic sign-in could not be saved: disk full.",
            "Nested sign-in warning remained Chinese.");
        Check(text.SystemMessage("备注同步未确认：好友关系已变化，未保存备注。") == "Note sync is unconfirmed: The friendship changed. The note was not saved.",
            "Nested friend status remained Chinese.");
        Check(text.SystemMessage("备注同步未确认：其他设备已修改此备注，请刷新后重新编辑。") == "Note sync is unconfirmed: Another device changed this note. Refresh before editing again.",
            "The current cloud API's note conflict remained Chinese.");
        Check(text.SystemMessage("自定义错误正文 你好") == "自定义错误正文 你好", "Unknown diagnostic text was discarded.");

        var message = new CloudChatMessage(1, "local-test", new("user", "friend", "中文账号"), "me", 1,
            Guid.Empty, "状态已保存。", null, "history", DateTimeOffset.UnixEpoch, null);
        Check(text.MessageBody(message) == message.Text, "A user's message was translated as application status.");
        var quick = message with { QuickMessageId = CloudChatPolicy.InviteNext };
        Check(text.MessageBody(quick) == "Invite me next time" && quick.Text == message.Text, "Quick message display mutated stored content.");
        Check(FriendsIncomingNotifications.Replies(quick, text).SequenceEqual(new[] { "Sure, next time", "Not this time" }),
            "English quick replies were not localized.");
        configuration.UiLanguage = "zh-CN";
        Check(FriendsIncomingNotifications.Replies(quick, text)[0] == "好，下把叫你", "Quick reply language was cached across a language switch.");
        Console.WriteLine("English UI: live language switching, cloud/friend statuses, counts/timestamps, nested errors, preserved diagnostics/user content and quick replies passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
