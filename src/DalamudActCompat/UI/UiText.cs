using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DalamudActCompat.Plugin;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

public sealed class UiText(PluginConfiguration configuration)
{
    public string Language => UiLanguages.Normalize(configuration.UiLanguage);
    public bool IsChinese => Language is "zh-CN" or "zh-TW";

    public string Get(string chinese, string english)
        => Language == "zh-CN" ? chinese : UiTranslations.Get(Language, english);

    // Zero-allocation dual-branch interpolated string overload: only the active language's
    // arguments are evaluated and formatted; the unused language branch is skipped completely.
    [OverloadResolutionPriority(1)]
    public string Format(
        [InterpolatedStringHandlerArgument("")] ref ChineseFormatHandler chinese,
        [InterpolatedStringHandlerArgument("")] ref EnglishFormatHandler english)
        => chinese.IsActive
            ? chinese.ToStringAndClear()
            : english.Format();

    // Fallback overload for non-interpolated string / FormattableString callers.
    public string Format(string chinese, FormattableString english)
        => Language == "zh-CN" ? chinese : string.Format(CultureInfo.CurrentCulture,
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

    [InterpolatedStringHandler]
    public ref struct ChineseFormatHandler
    {
        private DefaultInterpolatedStringHandler inner;
        private readonly bool active;

        public ChineseFormatHandler(int literalLength, int formattedCount, UiText text, out bool shouldAppend)
        {
            active = text.Language == "zh-CN";
            shouldAppend = active;
            inner = active ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) => inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => inner.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => inner.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string? format) => inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(ReadOnlySpan<char> value) => inner.AppendFormatted(value);
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(string? value) => inner.AppendFormatted(value);
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) => inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) => inner.AppendFormatted(value, alignment, format);

        internal bool IsActive => active;

        internal string ToStringAndClear() => active ? inner.ToStringAndClear() : string.Empty;
    }

    [InterpolatedStringHandler]
    public ref struct EnglishFormatHandler
    {
        private readonly string? language;
        private StringBuilder? template;
        private object?[]? args;
        private int argIndex;

        public EnglishFormatHandler(int literalLength, int formattedCount, UiText text, out bool shouldAppend)
        {
            var lang = text.Language;
            var active = lang != "zh-CN";
            shouldAppend = active;
            if (active)
            {
                language = lang;
                template = new StringBuilder(literalLength + formattedCount * 6);
                args = formattedCount > 0 ? new object?[formattedCount] : Array.Empty<object?>();
            }
            else
            {
                language = null;
                template = null;
                args = null;
            }
            argIndex = 0;
        }

        public void AppendLiteral(string value)
        {
            if (template is null) return;
            foreach (var ch in value)
            {
                if (ch is '{' or '}') template.Append(ch);
                template.Append(ch);
            }
        }

        public void AppendFormatted<T>(T value)
            => AppendHole(value, 0, null);

        public void AppendFormatted<T>(T value, string? format)
            => AppendHole(value, 0, format);

        public void AppendFormatted<T>(T value, int alignment)
            => AppendHole(value, alignment, null);

        public void AppendFormatted<T>(T value, int alignment, string? format)
            => AppendHole(value, alignment, format);

        public void AppendFormatted(string? value)
            => AppendHole(value, 0, null);

        public void AppendFormatted(string? value, int alignment = 0, string? format = null)
            => AppendHole(value, alignment, format);

        public void AppendFormatted(object? value, int alignment = 0, string? format = null)
            => AppendHole(value, alignment, format);

        public void AppendFormatted(ReadOnlySpan<char> value)
            => AppendHole(value.ToString(), 0, null);

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null)
            => AppendHole(value.ToString(), alignment, format);

        private void AppendHole(object? value, int alignment, string? format)
        {
            if (template is null || args is null) return;
            var index = argIndex++;
            args[index] = value;
            template.Append('{').Append(index);
            if (alignment != 0) template.Append(',').Append(alignment);
            if (!string.IsNullOrEmpty(format)) template.Append(':').Append(format);
            template.Append('}');
        }

        internal string Format()
        {
            if (template is null || language is null) return string.Empty;
            var translated = UiTranslations.Get(language, template.ToString());
            return args is { Length: > 0 }
                ? string.Format(CultureInfo.CurrentCulture, translated, args)
                : translated;
        }
    }
}
