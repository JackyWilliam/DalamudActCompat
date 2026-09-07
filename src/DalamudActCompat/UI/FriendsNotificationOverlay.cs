using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager
{
    private const string NotificationPrefix = "##DACTIncoming-";
    private static readonly ImGuiWindowFlags NotificationFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;

    private sealed record IncomingLayout(FriendIncomingNotification Entry, string Body, float Height);

    private unsafe void DrawIncomingNotifications(FriendsChatSnapshot state, long now)
    {
        var context = ImGui.GetCurrentContext();
        bool IsNotification(ImGuiWindowPtr window) => window.Handle != null &&
            (Marshal.PtrToStringUTF8((nint)window.Name)?.StartsWith(NotificationPrefix, StringComparison.Ordinal) ?? false);
        if (context.NavWindow.Handle != null && !IsNotification(context.NavWindow)) lastNonNotificationFocus = context.NavWindow.ID;
        if (incoming.Visible.Count == 0) return;
        var viewport = ImGui.GetMainViewport();
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var padding = Math.Max(3, 10 * scale); var gap = 8 * scale;
        var expandWidth = ImGui.CalcTextSize("全部展开").X + padding * 2;
        var totalWidth = Math.Min(320 * scale + gap + expandWidth, viewport.WorkSize.X - 16);
        var bodyWidth = Math.Max(1, totalWidth - gap - expandWidth);
        var textWidth = Math.Max(1, bodyWidth - padding * 2);
        var lineHeight = ImGui.GetTextLineHeight();
        var buttonHeight = ImGui.GetFrameHeight();
        var layouts = new List<IncomingLayout>(); var totalHeight = 0f;
        foreach (var entry in incoming.Visible)
        {
            var body = FriendsMessagePreview.Ellipsize(entry.Message.Text, lineHeight * 3, s => ImGui.CalcTextSize(s, false, textWidth).Y);
            var hasReplies = FriendsIncomingNotifications.Replies(entry.Message).Count > 0;
            var height = padding * 2 + lineHeight + gap + ImGui.CalcTextSize(body, false, textWidth).Y + (hasReplies ? buttonHeight + gap : 0);
            if (layouts.Count > 0 && totalHeight + gap + height > viewport.WorkSize.Y - 16) break;
            layouts.Add(new(entry, body, height)); totalHeight += height + (layouts.Count > 1 ? gap : 0);
        }
        var nextY = viewport.WorkPos.Y + Math.Max(8, (viewport.WorkSize.Y - totalHeight) / 2);
        var right = configuration.FriendNotificationsOnRight;
        foreach (var layout in layouts)
        {
            var entry = layout.Entry;
            entry.Y = entry.Y is { } oldY ? oldY + (nextY - oldY) * (1 - MathF.Exp(-ImGui.GetIO().DeltaTime * 18)) : nextY;
            entry.Y = Math.Clamp(entry.Y.Value, viewport.WorkPos.Y + 8, Math.Max(viewport.WorkPos.Y + 8, viewport.WorkPos.Y + viewport.WorkSize.Y - layout.Height - 8));
            nextY += layout.Height + gap;
        }
        // During downward movement the rectangles can briefly overlap. Paint the
        // newest last so its text/buttons stay above the older sliding bubbles.
        foreach (var layout in layouts.AsEnumerable().Reverse())
        {
            var entry = layout.Entry;
            var settledX = right ? viewport.WorkPos.X + viewport.WorkSize.X - 8 - totalWidth : viewport.WorkPos.X + 8;
            var outsideX = right ? viewport.WorkPos.X + viewport.WorkSize.X : viewport.WorkPos.X - totalWidth;
            var groupX = outsideX + (settledX - outsideX) * entry.SlideProgress(now);
            var bodyPosition = new Vector2(groupX + (right ? expandWidth + gap : 0), entry.Y!.Value);
            var expandPosition = new Vector2(groupX + (right ? 0 : bodyWidth + gap), entry.Y.Value + (layout.Height - buttonHeight) / 2);
            var id = state.Session.Generation + "-" + entry.Message.Id;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * entry.Opacity(now));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            ImGui.SetNextWindowPos(bodyPosition); ImGui.SetNextWindowSize(new(bodyWidth, layout.Height));
            if (ImGui.Begin(NotificationPrefix + id, NotificationFlags | ImGuiWindowFlags.NoInputs))
            {
                ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
                var list = ImGui.GetWindowDrawList();
                var background = Navy; background.W = 1 - Math.Clamp(configuration.FriendNotificationBackgroundTransparency, 0, 100) / 100f;
                list.AddRectFilled(bodyPosition, bodyPosition + new Vector2(bodyWidth, layout.Height), ImGui.GetColorU32(background), 9 * scale);
                var author = entry.Message.Sender.IsOfficial ? "DACT 官方通知" : entry.Message.Sender.Name;
                author = FriendsMessagePreview.Ellipsize(author, textWidth, s => ImGui.CalcTextSize(s).X);
                var origin = bodyPosition + new Vector2(padding);
                list.AddText(origin, ImGui.GetColorU32(entry.Message.Sender.IsOfficial ? Gold : Blue), author);
                origin.Y += lineHeight + gap;
                list.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin, ImGui.GetColorU32(Vector4.One), layout.Body, textWidth);
                if (FriendsIncomingNotifications.Replies(entry.Message).Count > 0)
                {
                    var status = NotificationReplyStatus(entry, state);
                    if (status is not null)
                        list.AddText(bodyPosition + new Vector2(padding, layout.Height - padding - buttonHeight), ImGui.GetColorU32(Blue),
                            FriendsMessagePreview.Ellipsize(status, textWidth, s => ImGui.CalcTextSize(s).X));
                }
            }
            ImGui.End();
            DrawNotificationReplies(entry, state, now, textWidth, bodyPosition + new Vector2(padding, layout.Height - padding - buttonHeight), id);
            // Keep the explicit action in its own tight window. The transparent
            // gap and the rest of the screen must not become an input-catching box.
            ImGui.SetNextWindowPos(expandPosition); ImGui.SetNextWindowSize(new(expandWidth, buttonHeight));
            if (ImGui.Begin(NotificationPrefix + id + "-expand", NotificationFlags))
            {
                ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
                if (ImGui.Button("全部展开", new(expandWidth, buttonHeight)) && controller.Snapshot.Session == state.Session)
                { OpenChat(entry.Message.ConversationId); entry.ExpiresAt = now; }
            }
            ImGui.End(); ImGui.PopStyleVar(3);
        }
        // Mouse buttons may focus an ImGui window even with NoNav. Only an
        // explicit OpenChat may focus chat; quick replies return to the prior owner.
        if (IsNotification(context.NavWindow))
        {
            ImGuiWindowPtr previous = default;
            for (var i = 0; i < context.Windows.Size; i++)
                if (context.Windows[i].ID == lastNonNotificationFocus && context.Windows[i].Active)
                { previous = context.Windows[i]; break; }
            // FocusWindow normally cancels a pressed button in another window.
            // Preserve its mouse press until release while returning keyboard focus.
            var preserveActive = context.ActiveIdNoClearOnFocusLoss;
            context.ActiveIdNoClearOnFocusLoss = true;
            ImGuiP.FocusWindow(previous);
            context.ActiveIdNoClearOnFocusLoss = preserveActive;
        }
        if (context.ActiveId != 0 && IsNotification(context.ActiveIdWindow)) ImGui.SetNextFrameWantCaptureKeyboard(false);
    }

    private static string? NotificationReplyStatus(FriendIncomingNotification entry, FriendsChatSnapshot state)
    {
        if (entry.ReplyOperation is not { } operation) return null;
        var view = state.Conversations.GetValueOrDefault(entry.Message.ConversationId);
        if (view is null) return "会话已关闭";
        if (view.Chat.History.Concat(view.Chat.Pending).Any(m => m.OperationId == operation && m.Sender.UserId == state.Friends?.User?.Id)) return "已回复";
        if (view.PendingSend?.OperationId == operation && !state.Busy) return null;
        return state.Busy ? "正在回复…" : "未能确认，请展开聊天查看";
    }

    private static bool NotificationButton(string id, string label, Vector2 position, Vector2 size, bool enabled)
    {
        // No enclosing input window: only each actual button's rectangle receives
        // mouse events. Message text, rounded backgrounds and button gaps pass through.
        ImGui.SetNextWindowPos(position); ImGui.SetNextWindowSize(size);
        var clicked = false;
        if (ImGui.Begin(NotificationPrefix + id, NotificationFlags | (enabled ? 0 : ImGuiWindowFlags.NoInputs)))
        {
            // New no-focus windows may be inserted below an existing body. Keep
            // the actual controls above its translucent fill without focusing them.
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            ImGui.BeginDisabled(!enabled); clicked = ImGui.Button(label, size); ImGui.EndDisabled();
        }
        ImGui.End(); return clicked;
    }

    private void DrawNotificationReplies(FriendIncomingNotification entry, FriendsChatSnapshot state, long now, float width, Vector2 position, string id)
    {
        var view = state.Conversations.GetValueOrDefault(entry.Message.ConversationId);
        if (view is null || NotificationReplyStatus(entry, state) is not null) return;
        var buttonHeight = ImGui.GetFrameHeight();
        if (entry.ReplyOperation is { } operation)
        {
            if (view.PendingSend?.OperationId == operation && !state.Busy)
            {
                var retrySize = new Vector2(ImGui.CalcTextSize("重试原回复").X + ImGui.GetStyle().FramePadding.X * 2, buttonHeight);
                if (NotificationButton(id + "-retry", "重试原回复", position, retrySize, true) && controller.Retry(entry.Message.ConversationId, state.Session, operation))
                    entry.ExpiresAt = Math.Max(entry.ExpiresAt, now + 3000);
            }
            return;
        }
        var replies = FriendsIncomingNotifications.Replies(entry.Message);
        for (var i = 0; i < replies.Count; i++)
        {
            var buttonWidth = (width - ImGui.GetStyle().ItemSpacing.X) / replies.Count;
            var label = FriendsMessagePreview.Ellipsize(replies[i], buttonWidth - ImGui.GetStyle().FramePadding.X * 2, s => ImGui.CalcTextSize(s).X);
            if (NotificationButton(id + "-reply-" + i, label, position + new Vector2(i * (buttonWidth + ImGui.GetStyle().ItemSpacing.X), 0),
                    new(buttonWidth, buttonHeight), !state.Busy && view.PendingSend is null) &&
                controller.Send(entry.Message.ConversationId, replies[i], expectedSession: state.Session) is { } sent)
            { entry.ReplyOperation = sent; entry.ExpiresAt = Math.Max(entry.ExpiresAt, now + 3000); }
        }
    }
}
