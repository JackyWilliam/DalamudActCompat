namespace DalamudActCompat.Infrastructure.Cloud;

// 反馈: the header's feedback dialog sends these to DACT Cloud, which lists them in the admin
// console's 反馈 column; replies arrive as the player's 官方通知 conversation.
internal static class CloudFeedbackKind
{
    public const string Bug = "bug";
    public const string Idea = "idea";
    public const string Other = "other";
}

internal sealed record CloudFeedbackImage(string Name, string ContentType, string Data);

internal sealed record CloudFeedbackRequest(
    string Kind,
    string Text,
    string? Diagnostics,
    string? Conversation,
    IReadOnlyList<CloudFeedbackImage> Images);

internal sealed record CloudFeedbackReceipt(string Id, string Status, DateTimeOffset CreatedAt);

// status: open (已收到), replied (已回复) or closed (已关闭).
internal sealed record CloudFeedbackItem(string Id, string Kind, string Text, string Status, DateTimeOffset CreatedAt, DateTimeOffset? RepliedAt);

internal sealed record CloudFeedbackList(IReadOnlyList<CloudFeedbackItem> Items);
