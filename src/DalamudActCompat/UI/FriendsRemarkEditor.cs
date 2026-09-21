using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager
{
    private void DrawRemarkEditor(FriendsChatSnapshot state, float scale)
    {
        const string popup = "好友备注###DACTFriendRemark";
        var focus = openRemarkEditor;
        if (openRemarkEditor) { ImGui.OpenPopup(popup); openRemarkEditor = false; }
        var maximum = ImGui.GetMainViewport().WorkSize - new Vector2(16);
        var width = Math.Min(340 * scale, maximum.X);
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, maximum.Y));
        DactTheme.PreparePopupPosition(popup);
        if (!ImGui.BeginPopup(popup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        DactTheme.DrawGamePopupFrame();
        var friend = state.Friends?.Friends.FirstOrDefault(friend => friend.Id == remarkRelationId);
        if (friend is null) { remarkRelationId = null; ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (submittedRemark is { } operation)
        {
            if (state.LastSavedRemark == operation)
            {
                submittedRemark = null; remarkRelationId = null;
                ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return;
            }
            if (!state.Busy) { remarkError = state.RemarkStatus; submittedRemark = null; }
        }
        DactTheme.TextColored(Blue, "好友备注");
        ImGui.TextWrapped("账号：" + friend.User.Username);
        ImGui.Spacing();
        if (friend.Remark is null) ImGui.TextWrapped("服务器尚未支持备注云同步。");
        ImGui.BeginDisabled(state.Busy || friend.Remark is null);
        if (focus) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##friend-remark", "留空可清除备注", ref remarkDraft, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        var normalized = remarkDraft.Trim();
        var valid = FriendRemark.IsValid(normalized);
        ImGui.TextDisabled($"{normalized.EnumerateRunes().Count()}/{FriendRemark.MaximumLength} 字");
        if (!valid) ImGui.TextWrapped("备注最多 40 字，不能包含换行或控制字符。");
        if (remarkError.Length > 0) ImGui.TextWrapped(remarkError);
        ImGui.BeginDisabled(!valid);
        if ((DactTheme.Button("保存备注") || enter) && valid) Submit(normalized);
        ImGui.EndDisabled(); ImGui.SameLine();
        if (DactTheme.Button("清除备注")) Submit("");
        ImGui.EndDisabled(); ImGui.SameLine();
        if (DactTheme.Button("取消") || ImGui.IsKeyPressed(ImGuiKey.Escape))
        { remarkRelationId = null; submittedRemark = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();

        void Submit(string value)
        {
            // Bind the edit to the snapshot account. A logout between drawing and
            // clicking must never write a different account's private remark.
            submittedRemark = controller.SetRemark(friend.Id, value, state.Session, remarkRevision);
            remarkError = submittedRemark is null ? "正在处理其他操作，请稍后再试。" : "";
        }
    }
}
