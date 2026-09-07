using System.Globalization;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal static class FriendsMessagePreview
{
    internal static string LastMessage(CloudChatConversation chat, string? selfId)
    {
        // The server's monotonic ID orders both sides and pending/history together.
        // Unread flags and delivery ACKs must never change the conversation preview.
        var latest = chat.History.Concat(chat.Pending).MaxBy(message => message.Id);
        if (latest is null) return "";
        var text = string.Join(" ", latest.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (latest.Sender.UserId == selfId ? "我：" : "") + text;
    }

    internal static string Ellipsize(string text, float width, Func<string, float> measure)
    {
        if (width <= 0) return "";
        if (measure(text) <= width) return text;
        const string ellipsis = "…";
        if (measure(ellipsis) > width) return "";
        // Cut at text-element boundaries so emoji/surrogate pairs remain intact.
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var low = 0; var high = boundaries.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = middle < boundaries.Length ? boundaries[middle] : text.Length;
            if (measure(text[..end] + ellipsis) <= width) low = middle; else high = middle - 1;
        }
        return text[..(low < boundaries.Length ? boundaries[low] : text.Length)] + ellipsis;
    }
}
