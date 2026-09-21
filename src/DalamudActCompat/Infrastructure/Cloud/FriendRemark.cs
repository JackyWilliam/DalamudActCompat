using System.Text;

namespace DalamudActCompat.Infrastructure.Cloud;

internal static class FriendRemark
{
    internal const int MaximumLength = 40;

    internal static bool IsValid(string value)
        => value.EnumerateRunes().Count() <= MaximumLength && !value.Any(char.IsControl);

    internal static string Display(string? remark, string username)
        => string.IsNullOrEmpty(remark) ? username : $"{remark}（{username}）";
}
