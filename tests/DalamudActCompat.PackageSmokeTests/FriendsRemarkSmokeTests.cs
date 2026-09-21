using DalamudActCompat.Infrastructure.Cloud;

internal static partial class FriendsUiSmokeTests
{
    private static async Task RemarksAsync()
    {
        var api = new Fake { VisualData = true };
        var disk = new MemoryDisk();
        using (var controller = new FriendsChatController(api, disk, TimeSpan.FromHours(1)))
        {
            await Until(() => controller.Snapshot.Friends is not null, "remark initial list");
            var expectedSession = api.FriendsSession;
            async Task Save(string value)
            {
                var operation = controller.SetRemark("native-friend", value, expectedSession, controller.Snapshot.Friends!.Friends[0].Remark!.Revision);
                Check(operation is not null, "Remark edit was not queued.");
                await Until(() => !controller.Snapshot.Busy && controller.Snapshot.LastSavedRemark == operation, "remark durable save");
            }
            await Save("  固定队奶妈 ☕  ");
            Check(controller.Snapshot.FriendDisplayName(api.Peer.Id, api.Peer.Username) == $"固定队奶妈 ☕（{api.Peer.Username}）" && api.Attempts.IsEmpty,
                "Remark changed the public name or entered outgoing messages.");
            Check(controller.Snapshot.FriendDisplayName(api.Peer.Id, "改名后的账号") == "固定队奶妈 ☕（改名后的账号）", "Remark was keyed by a mutable nickname.");
            Check(controller.SetRemark("native-friend", new string('字', 41), expectedSession, 1) is null &&
                controller.SetRemark("native-friend", "两行\n备注", expectedSession, 1) is null, "Oversized/multiline remark was accepted.");
            api.FailRemark = true;
            controller.SetRemark("native-friend", "不应生效", expectedSession, 1);
            await Until(() => !controller.Snapshot.Busy && controller.Snapshot.RemarkStatus.Contains("未确认"), "remark save failure");
            Check(controller.Snapshot.Remarks[api.Peer.Id] == "固定队奶妈 ☕" && controller.Snapshot.State == "ready", "Failed save changed the label or disconnected chat.");
            api.FailRemark = false;
            await Save(" ");
            Check(!controller.Snapshot.Remarks.ContainsKey(api.Peer.Id), "Blank remark was not cleared.");
            await Save("重启后保留");
        }
        // A clean disk models reinstall/a second computer, so persistence cannot
        // accidentally pass by reusing this client's local encrypted state.
        using (var restarted = new FriendsChatController(api, new MemoryDisk(), TimeSpan.FromHours(1)))
        {
            await Until(() => restarted.Snapshot.Remarks.ContainsKey(api.Peer.Id), "remark restart");
            var oldSession = api.FriendsSession;
            api.SwitchAccount();
            Check(restarted.Snapshot.Remarks.IsEmpty && restarted.SetRemark("native-friend", "旧窗口草稿", oldSession, 0) is null,
                "Remark or stale edit crossed accounts.");
            await Until(() => restarted.Snapshot.Friends?.User?.Id == api.Self.Id, "remark new account");
            Check(restarted.Snapshot.Remarks.IsEmpty, "New account inherited private remarks.");
            var operation = restarted.SetRemark("native-friend", "临时好友", api.FriendsSession, 0);
            await Until(() => !restarted.Snapshot.Busy && restarted.Snapshot.LastSavedRemark == operation, "remark before removal");
            api.Remarks[api.Self.Id] = new("另一台电脑修改", 2); restarted.Refresh();
            await Until(() => restarted.Snapshot.Remarks[api.Peer.Id] == "另一台电脑修改", "remote edit refresh");
            restarted.SetRemark("native-friend", "过期草稿", api.FriendsSession, 1);
            await Until(() => !restarted.Snapshot.Busy, "stale cloud revision");
            Check(api.CurrentRemark.Text == "另一台电脑修改" && restarted.Snapshot.RemarkStatus.Contains("未确认"), "Stale edit overwrote another device.");
            api.CommitRemarkThenFail = true;
            restarted.SetRemark("native-friend", "响应丢失仍能恢复", api.FriendsSession, 2);
            await Until(() => !restarted.Snapshot.Busy && restarted.Snapshot.Remarks[api.Peer.Id] == "响应丢失仍能恢复", "lost response reconciliation");
            api.CommitRemarkThenFail = false;
            operation = restarted.SetRemark("native-friend", "响应丢失仍能恢复", api.FriendsSession, 2);
            await Until(() => !restarted.Snapshot.Busy && restarted.Snapshot.LastSavedRemark == operation, "same request retry");
            api.VisualData = false; restarted.Refresh();
            await Until(() => restarted.Snapshot.Remarks.IsEmpty, "removed friend cleanup");
        }

        var oldApi = new Fake { VisualData = true, RemarkSupported = false };
        using var oldController = new FriendsChatController(oldApi, new MemoryDisk(), TimeSpan.FromHours(1));
        await Until(() => oldController.Snapshot.Friends is not null, "old server friends list");
        oldController.SetRemark("native-friend", "unsupported", oldApi.FriendsSession, 0);
        await Until(() => !oldController.Snapshot.Busy && oldController.Snapshot.RemarkStatus.Contains("尚未支持"), "old server graceful fallback");
        Check(oldController.Snapshot.State == "ready" && oldController.Snapshot.Remarks.IsEmpty && oldApi.Remarks.Count == 0,
            "Missing remark capability broke existing friends or wrote a local-only note.");
        Console.WriteLine("Friend remarks: private cloud create/edit/clear, reinstall/second device, failures/lost responses, revision conflicts, account isolation and old-server compatibility passed.");
    }
}
