using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

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
        DactUi.BeginFrame();
        var scale = DactUi.S;
        var padding = Math.Max(3, 10 * scale); var gap = 8 * scale;
        // 全部展开 sits inside the bubble, in the sender's row on the right (user request), and
        // opens the conversation in its own floating window.
        var expandLabel = uiText.Get("全部展开", "Open chat");
        var expandWidth = DactUi.TextWidth(expandLabel, DactFonts.Small) + 34 * scale;
        var bodyWidth = Math.Max(1, Math.Min(340 * scale, viewport.WorkSize.X - 16));
        var totalWidth = bodyWidth;
        var textWidth = Math.Max(1, bodyWidth - padding * 2);
        var lineHeight = ImGui.GetTextLineHeight();
        var headerHeight = Math.Max(lineHeight, 24 * scale);
        // 1.0 buttons are 30 px tall at the default font.
        var buttonHeight = 30 * scale;
        var layouts = new List<IncomingLayout>(); var totalHeight = 0f;
        foreach (var entry in incoming.Visible)
        {
            var body = FriendsMessagePreview.Ellipsize(uiText.MessageBody(entry.Message), lineHeight * 3, s => ImGui.CalcTextSize(s, false, textWidth).Y);
            var hasReplies = FriendsIncomingNotifications.Replies(entry.Message, uiText).Count > 0;
            var height = padding * 2 + headerHeight + gap + ImGui.CalcTextSize(body, false, textWidth).Y + (hasReplies ? buttonHeight + gap : 0);
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
            var bodyPosition = new Vector2(groupX, entry.Y!.Value);
            var expandPosition = new Vector2(bodyPosition.X + bodyWidth - padding + 4 * scale - expandWidth, bodyPosition.Y + padding);
            var id = state.Session.Generation + "-" + entry.Message.Id;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * entry.Opacity(now));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            ImGui.SetNextWindowPos(bodyPosition); ImGui.SetNextWindowSize(new(bodyWidth, layout.Height));
            if (ImGui.Begin(NotificationPrefix + id, NotificationFlags | ImGuiWindowFlags.NoInputs))
            {
                ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
                var list = ImGui.GetWindowDrawList();
                // 1.0 card: raised fill (its transparency is the player's 气泡背景透明度) with the card border.
                var opacity = 1 - Math.Clamp(configuration.FriendNotificationBackgroundTransparency, 0, 100) / 100f;
                list.AddRectFilled(bodyPosition, bodyPosition + new Vector2(bodyWidth, layout.Height), ImGui.GetColorU32(DactTheme.Palette.Raised with { W = opacity }), 10 * scale);
                list.AddRect(bodyPosition, bodyPosition + new Vector2(bodyWidth, layout.Height), ImGui.GetColorU32(DactUi.CardBorder with { W = DactUi.CardBorder.W * Math.Max(opacity, .35f) }),
                    10 * scale, ImDrawFlags.None, Math.Max(1, scale));
                var author = entry.Message.Sender.IsOfficial ? uiText.Get("DACT 官方通知", "DACT announcements") : state.FriendDisplayName(entry.Message.Sender.UserId, entry.Message.Sender.Name);
                var authorWidth = Math.Max(1, textWidth - expandWidth - 6 * scale);
                author = FriendsMessagePreview.Ellipsize(author, authorWidth, s => ImGui.CalcTextSize(s).X);
                var origin = bodyPosition + new Vector2(padding, padding + (headerHeight - lineHeight) / 2);
                AccountIdentityBadge.DrawName(administratorIcon, author,
                    !entry.Message.Sender.IsOfficial && state.Conversations.GetValueOrDefault(entry.Message.ConversationId)?.Chat.Peer.IsAdmin == true,
                    origin, authorWidth, entry.Message.Sender.IsOfficial ? Gold : Blue, sponsorIcon,
                    entry.Message.Sender.IsOfficial ? 0 : state.Conversations.GetValueOrDefault(entry.Message.ConversationId)?.Chat.Peer.SponsorTier ?? entry.Message.Sender.SponsorTier, text: uiText);
                origin = bodyPosition + new Vector2(padding, padding + headerHeight + gap);
                list.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin, ImGui.GetColorU32(DactTheme.Palette.Text), layout.Body, textWidth);
                if (FriendsIncomingNotifications.Replies(entry.Message, uiText).Count > 0)
                {
                    var status = NotificationReplyStatus(entry, state);
                    if (status is not null)
                        list.AddText(bodyPosition + new Vector2(padding, layout.Height - padding - buttonHeight), ImGui.GetColorU32(Blue),
                            FriendsMessagePreview.Ellipsize(status, textWidth, s => ImGui.CalcTextSize(s).X));
                }
            }
            ImGui.End();
            DrawNotificationReplies(entry, state, now, textWidth, bodyPosition + new Vector2(padding, layout.Height - padding - buttonHeight), id);
            // Keep the explicit action in its own tight window. The rest of the bubble and
            // the screen must not become an input-catching box.
            if (NotificationLink(id + "-expand", expandLabel, expandPosition, new(expandWidth, headerHeight)) && entry.DismissedAt is null &&
                controller.Snapshot.Session == state.Session)
            {
                OpenChatWindow(entry.Message.ConversationId, bodyPosition, bodyPosition + new Vector2(bodyWidth, layout.Height), right);
                entry.Dismiss(now);
            }
            ImGui.PopStyleVar(3);
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
            // Returning keyboard focus must not raise the chat over the pressed
            // notification: the mouse release would then hit the chat underneath.
            var root = previous.Handle != null ? previous.RootWindow : default;
            var previousFlags = root.Handle != null ? root.Flags : default;
            if (root.Handle != null) root.Flags |= ImGuiWindowFlags.NoBringToFrontOnFocus;
            ImGuiP.FocusWindow(previous);
            if (root.Handle != null) root.Flags = previousFlags;
            context.ActiveIdNoClearOnFocusLoss = preserveActive;
        }
        if (context.ActiveId != 0 && IsNotification(context.ActiveIdWindow)) ImGui.SetNextFrameWantCaptureKeyboard(false);
    }

    private string? NotificationReplyStatus(FriendIncomingNotification entry, FriendsChatSnapshot state)
    {
        if (entry.ReplyOperation is not { } operation) return null;
        var view = state.Conversations.GetValueOrDefault(entry.Message.ConversationId);
        if (view is null) return uiText.Get("会话已关闭", "Conversation closed");
        if (view.Chat.History.Concat(view.Chat.Pending).Any(m => m.OperationId == operation && m.Sender.UserId == state.Friends?.User?.Id)) return uiText.Get("已回复", "Reply sent");
        if (view.PendingSend?.OperationId == operation && !state.Busy) return null;
        return state.Busy ? uiText.Get("正在回复…", "Sending reply…") : uiText.Get("未能确认，请展开聊天查看", "Unconfirmed. Open the chat to check.");
    }

    // 全部展开 in the bubble's sender row: a compact accent pill with the pop-out icon, since it
    // opens a window of its own.
    private static bool NotificationLink(string id, string label, Vector2 position, Vector2 size)
    {
        ImGui.SetNextWindowPos(position); ImGui.SetNextWindowSize(size);
        var clicked = false;
        if (ImGui.Begin(NotificationPrefix + id, NotificationFlags))
        {
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            var s = DactUi.S;
            var palette = DactTheme.Palette;
            clicked = ImGui.InvisibleButton("##notification-link", size);
            var min = ImGui.GetItemRectMin();
            var hover = DactMotion.Float(ImGuiP.GetItemID(), ImGui.IsItemHovered() ? 1 : 0, DactMotion.Fast);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, DactUi.U32(palette.Accent with { W = .12f + .14f * hover }), size.Y * .5f);
            DactUi.DrawText(draw, min + new Vector2(10 * s, 0), size.Y, label, palette.Accent, DactFonts.Small);
            DactIcons.Draw(draw, "external", new Vector2(min.X + size.X - 22 * s, min.Y + (size.Y - 12 * s) * .5f), 12 * s, palette.Accent);
        }
        ImGui.End();
        return clicked;
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
            clicked = DactUi.Button(label + "##notification-button", width: size.X, disabled: !enabled);
        }
        ImGui.End(); return clicked;
    }

    private void DrawNotificationReplies(FriendIncomingNotification entry, FriendsChatSnapshot state, long now, float width, Vector2 position, string id)
    {
        var view = state.Conversations.GetValueOrDefault(entry.Message.ConversationId);
        if (view is null || NotificationReplyStatus(entry, state) is not null) return;
        var buttonHeight = 30 * DactUi.S;
        if (entry.ReplyOperation is { } operation)
        {
            if (view.PendingSend?.OperationId == operation && !state.Busy)
            {
                var retrySize = new Vector2(ImGui.CalcTextSize(uiText.Get("重试原回复", "Retry original reply")).X + ImGui.GetStyle().FramePadding.X * 2, buttonHeight);
                if (NotificationButton(id + "-retry", uiText.Get("重试原回复", "Retry original reply"), position, retrySize, true) && controller.Retry(entry.Message.ConversationId, state.Session, operation))
                    entry.ExpiresAt = Math.Max(entry.ExpiresAt, now + 3000);
            }
            return;
        }
        var replies = FriendsIncomingNotifications.Replies(entry.Message, uiText);
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
