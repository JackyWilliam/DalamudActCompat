using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class UiLanguageSmokeTests
{
    internal static void Run()
    {
        var config = new PluginConfiguration(); var text = new UiText(config);
        var keys = UiTranslations.Catalog("zh-TW").Keys.ToHashSet(StringComparer.Ordinal);
        Check(keys.Count > 1300, "The language bundle is missing most UI strings.");
        foreach (var (id, _) in UiLanguages.All)
        {
            config.UiLanguage = id;
            Check(text.Language == id, "A supported language fell back to another locale.");
            var restored = new PluginConfiguration(); restored.RestoreFrom(config.CreateSnapshot());
            Check(new UiText(restored).Language == id, "Configuration restore lost the selected language.");
            Check(text.Get("好友", "Friends") != "Friends" || id == "en", "The friend UI was not localized.");
            Check(text.Get("好友##unchanged-42", "Friends##unchanged-42").EndsWith("##unchanged-42", StringComparison.Ordinal),
                "Translation changed a hidden control ID.");
            const string user = "玩家 Alice {0} / 現在は休憩中";
            var label = text.Format($"账号：{user}", $"Account: {user}");
            Check(label.Contains(user, StringComparison.Ordinal), "Formatting translated or reinterpreted user content.");
            var message = new CloudChatMessage(1, "local", new("user", "friend", "名字"), "me", 1,
                Guid.Empty, "状态已保存。", null, "history", DateTimeOffset.UnixEpoch, null);
            Check(text.MessageBody(message) == message.Text, "Language switching translated a user message.");
            var status = text.SystemMessage("已刷新，共 42 个云端版本。");
            Check(status.Contains("42", StringComparison.Ordinal) && (id == "zh-CN" || status != "已刷新，共 42 个云端版本。"),
                "A formatted system status lost its count or translation.");
            NotificationMessages(text);
            if (id is "zh-CN" or "en") continue;
            var catalog = UiTranslations.Catalog(id);
            Check(keys.SetEquals(catalog.Keys), $"Incomplete catalog: {id}.");
            foreach (var (source, translated) in catalog)
            {
                Check(translated.Length > 0 && !Regex.IsMatch(translated, @"(?:ZXQ|ZZR)\d+|(?:QXZ|RZZ)\b", RegexOptions.IgnoreCase),
                    $"Invalid translation token in {id}: {source}");
                var expected = Placeholders(source); var actual = Placeholders(translated);
                Check(expected.SequenceEqual(actual), $"Placeholder mismatch in {id}: {source}");
                // Commands are copyable help text, so translating even one
                // argument makes otherwise readable instructions unusable.
                foreach (Match command in Regex.Matches(source, @"/actcompat(?: (?:status|meter|history|on|off|simple)(?: off)?)?"))
                    Check(translated.Contains(command.Value, StringComparison.Ordinal), $"Translated command in {id}: {source}");
                if (expected.Length > 0)
                {
                    var format = CompositeFormat.Parse(translated);
                    _ = string.Format(CultureInfo.InvariantCulture, format,
                        Enumerable.Repeat<object>(new AnyFormat(), format.MinimumArgumentCount).ToArray());
                }
            }
        }
        foreach (var (input, expected) in new[] { ("EN", "en"), ("ja-JP", "ja"), ("de_DE", "de"),
                     ("fr-FR", "fr"), ("zh-Hant", "zh-TW"), ("zh-HK", "zh-TW"), ("unknown", "zh-CN") })
        {
            config.UiLanguage = input; Check(text.Language == expected, "Legacy/locale language normalization failed.");
        }
        config.UiLanguage = "ja";
        Check(PlayerIdentityFormatter.FormatJob("PLD", text) == "ナイト", "Japanese job terminology is incorrect.");
        config.UiLanguage = "zh-TW";
        Check(text.Get("设置", "Settings") == "設定", "Traditional Chinese settings wording is incorrect.");
        Console.WriteLine($"UI languages: six live locales, {keys.Count} matching catalog entries, placeholders/IDs, user content, locale aliases, job terms and configuration restore passed.");
    }

    private static void NotificationMessages(UiText text)
    {
        // Exercise the production builders: a valid catalog alone cannot catch
        // a notification that still concatenates Chinese outside UiText.
        var plugin = typeof(DalamudActCompat.Plugin.Plugin);
        const string diagnostic = "原始诊断 {0} / C:/user-data";
        var update = plugin.GetMethod("BuildBundledPluginUpdateMessage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)update.Invoke(null, [new BundledActPluginUpdateCheckResult([], [diagnostic]), 2, text])!;
        Check(result.Contains(diagnostic, StringComparison.Ordinal) && result.Contains('2') && result.Contains('1'),
            "Update notification changed diagnostics or counts.");
        Check(text.Language == "zh-CN" || !result.Contains("项来源检查失败", StringComparison.Ordinal),
            "Update notification retained untranslated application text.");
        var ban = plugin.GetMethod("BuildCloudBanSummary", BindingFlags.NonPublic | BindingFlags.Static)!;
        result = (string)ban.Invoke(null, [new CloudBanNotice("test", "account", DateTimeOffset.UnixEpoch, null, diagnostic), text])!;
        Check(result.Contains(diagnostic, StringComparison.Ordinal) &&
            (text.Language == "zh-CN" || !result.Contains("封禁时间", StringComparison.Ordinal)),
            "Restriction notice lost its reason or retained untranslated application text.");
    }

    private static string[] Placeholders(string value) => Regex.Matches(value, @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}")
        .Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
    private sealed class AnyFormat : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) => "value";
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
