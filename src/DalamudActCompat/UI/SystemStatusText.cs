using System.Text.RegularExpressions;

namespace DalamudActCompat.UI;

internal static class SystemStatusText
{
    // Services keep their existing snapshots and exception text. Translate only at
    // display time so changing UI language also updates an already-visible result.
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["正在处理其他操作，请稍后再试。"] = "Another operation is in progress. Please try again shortly.",
        ["共用账号与服务器不一致，请重新登录。"] = "The shared account does not match the server. Please sign in again.",
        ["请先登录云账号。"] = "Please sign in to your cloud account first.",
        ["好友账号会话已切换。"] = "The friend account session has changed.",
        ["登录账号已切换。"] = "The signed-in account has changed.",
        ["登录已失效，请重新登录。"] = "Your session is no longer valid. Please sign in again.",
        ["请登录或注册账号。"] = "Please sign in or create an account.",
        ["正在验证已保存的登录状态…"] = "Validating the saved session…",
        ["正在确认封禁状态…"] = "Checking account restrictions…",
        ["封禁已解除。请重启游戏或重载 DACT，然后重新登录。"] = "The restriction has been lifted. Restart the game or reload DACT, then sign in again.",
        ["正在验证登录状态…"] = "Validating your session…",
        ["登录已过期，请重新登录。"] = "Your session has expired. Please sign in again.",
        ["云账号已连接。"] = "Cloud account connected.",
        ["正在注册账号…"] = "Creating your account…",
        ["注册成功。请立即抄下恢复密钥；它只在本次注册后显示。"] = "Account created. Save your recovery key now; it is shown only once after registration.",
        ["正在登录…"] = "Signing in…",
        ["账号缺少云端加密密钥，无法安全读取备份。"] = "This account has no cloud encryption key; backups cannot be read safely.",
        ["恢复密钥与该账号不匹配，无法重新封装云备份密钥。"] = "The recovery key does not match this account; the backup encryption key cannot be rewrapped.",
        ["登录成功。请选择版本后预览或恢复。"] = "Signed in. Select a backup to preview or restore.",
        ["登录成功，正在读取云端版本…"] = "Signed in. Loading cloud backups…",
        ["正在重置密码…"] = "Resetting your password…",
        ["密码已重置，其他设备的旧登录已失效。"] = "Password reset. Previous sessions on other devices are now invalid.",
        ["密码已重置，正在读取云端版本…"] = "Password reset. Loading cloud backups…",
        ["正在退出登录…"] = "Signing out…",
        ["已退出登录。"] = "Signed out.",
        ["正在刷新云端版本…"] = "Refreshing cloud backups…",
        ["正在生成好友激活码…"] = "Generating an invitation key…",
        ["好友激活码已生成；完整激活码只显示这一次。"] = "Invitation key generated. The full key is shown only once.",
        ["正在确认管理员通知…"] = "Acknowledging the administrator notice…",
        ["管理员通知已确认。"] = "Administrator notice acknowledged.",
        ["正在确认赞助者通知…"] = "Acknowledging the sponsor notice…",
        ["赞助者通知已确认。"] = "Sponsor notice acknowledged.",
        ["正在加密并上传配置…"] = "Encrypting and uploading settings…",
        ["正在自动同步配置…"] = "Syncing settings automatically…",
        ["当前是默认配置，已跳过云同步；云端备份保持不变。"] = "Default settings detected. Sync was skipped and existing cloud backups were kept.",
        ["自动同步检查完成：配置没有变化。"] = "Sync check complete: settings have not changed.",
        ["正在下载并检查恢复内容…"] = "Downloading and checking the backup…",
        ["预览完成；确认后才会覆盖本机配置。"] = "Preview ready. Local settings are replaced only after confirmation.",
        ["正在恢复配置…"] = "Restoring settings…",
        ["恢复完成。请重载 DACT，使全部配置安全生效。"] = "Restore complete. Reload DACT to apply all settings safely.",
        ["正在回滚上次恢复…"] = "Rolling back the last restore…",
        ["没有可用的本地恢复快照。"] = "No local recovery snapshot is available.",
        ["已回滚。请重载 DACT，使全部配置安全生效。"] = "Rollback complete. Reload DACT to apply all settings safely.",
        ["操作已取消。"] = "Operation cancelled.",
        ["云服务返回了不匹配的账号加密密钥。"] = "The cloud service returned an encryption key for a different account.",
        ["请输入注册时保存的恢复密钥。旧备份无法由服务器解密。"] = "Enter the recovery key saved during registration. The server cannot decrypt old backups.",
        ["请选择仍存在的云端版本。"] = "Select an available cloud backup.",
        ["无法删除本机保存的云账号凭据。"] = "Could not remove the saved cloud credentials on this PC.",
        ["您的账号及关联机器已经被封禁"] = "Your account and linked devices have been banned",
        ["您的账号已经被封禁"] = "Your account has been banned",
        ["正在准备云端操作或恢复运行组件，请稍候…"] = "Preparing the cloud operation or restoring runtime components. Please wait…",
        ["本机共用登录状态已失效，请重新登录。"] = "The shared session on this PC is invalid. Please sign in again.",
        ["正在同步共用账号…"] = "Syncing the shared account…",
        ["正在验证共用账号…"] = "Validating the shared account…",
        ["已同步退出登录，请在 DACT 或 RPets 登录。"] = "Shared sign-out synced. Sign in through DACT or RPets.",
        ["已同步登录，但旧格式本机恢复副本暂时无法保存。"] = "Shared sign-in synced, but the legacy local recovery copy could not be saved.",
        ["已同步登录共用账号。"] = "Shared account sign-in synced.",
        ["正在连接好友服务…"] = "Connecting to the friend service…",
        ["登录后可使用好友功能。"] = "Sign in to use friends and chat.",
        ["服务器暂未开放好友功能，账号与云同步仍可正常使用。"] = "Friends are not available on this server yet. Account and cloud sync remain available.",
        ["好友连接暂时中断，将自动重连；原账号与云同步不受影响。"] = "Friend connection interrupted. Reconnecting automatically; account and cloud sync are unaffected.",
        ["好友服务版本较旧，暂时无法显示聊天身份。"] = "The friend service is outdated and cannot provide chat identities yet.",
        ["好友账号身份发生变化，请重新登录。"] = "Your friend account identity changed. Please sign in again.",
        ["好友服务已连接。"] = "Friend service connected.",
        ["发送已确认。"] = "Delivery confirmed.",
        ["原发送序号已过期，消息不再保留；未重新发送。"] = "The original send sequence expired and the message is no longer retained. It was not resent.",
        ["发送结果待确认，请重试原消息。"] = "Delivery is unconfirmed. Retry the original message.",
        ["好友服务尚未就绪。"] = "The friend service is not ready yet.",
        ["状态已保存。"] = "Status saved.",
        ["未找到可添加的账号。"] = "No account available to add was found.",
        ["已找到账号。"] = "Account found.",
        ["好友申请已处理。"] = "Friend request processed.",
        ["已放弃待确认发送；原消息可能已送达，请先核对记录。"] = "Unconfirmed send discarded. The original may have arrived; check the conversation first.",
        ["好友关系已变化，未保存备注。"] = "The friendship changed. The note was not saved.",
        ["当前服务器尚未支持备注云同步，请稍后重试。"] = "This server does not support synced friend notes yet. Please try again later.",
        ["备注已清除并同步。"] = "Note cleared and synced.",
        ["备注已保存并同步。"] = "Note saved and synced.",
        ["该会话暂时不能发送消息。"] = "Messages cannot be sent in this conversation right now.",
        ["请先处理上一条待确认消息。"] = "Resolve the previous unconfirmed message first.",
        ["该会话为只读通知。"] = "This announcement conversation is read-only.",
        ["消息需为1～2000个字符。"] = "Messages must contain 1–2,000 characters.",
        ["快捷消息无效。"] = "Invalid quick message.",
        ["正在保存并发送…"] = "Saving and sending…",
        ["已发送，待对方上线接收。"] = "Sent. Waiting for the recipient to come online.",
        ["已发送。"] = "Sent.",
        ["发送已确认，但本机状态保存失败；将重新核对记录。"] = "Delivery confirmed, but local state could not be saved. The conversation will be checked again.",
        ["原消息已超出保留范围，未重新发送。"] = "The original message is no longer retained. It was not resent.",
        ["本机保存失败，未开始本次发送，请重试。"] = "Local save failed. Sending did not start; please retry.",
        ["发送结果尚未确认，请重试原消息。"] = "Delivery is still unconfirmed. Retry the original message.",
        ["好友本机状态文件过大。"] = "The local friend state file is too large.",
        ["好友本机状态无法读取。"] = "Could not read local friend state.",
        ["请先读取当前账号的好友状态。"] = "Load friend state for the current account first.",
        ["另一游戏窗口已更新此会话的待发送消息；本次未覆盖，请先在原窗口处理。"] = "Another game window updated the pending message. Nothing was overwritten; resolve it in that window first.",
        ["好友本机状态超过容量限制。"] = "Local friend state exceeds the size limit.",
        ["好友账号标识无效。"] = "Invalid friend account identifier.",
        ["好友本机状态格式无效。"] = "Invalid local friend state format.",
        ["当前好友服务尚不支持备注云同步。"] = "This friend service does not support synced notes yet.",
        ["当前好友服务不支持编辑状态。"] = "This friend service does not support status editing.",
        ["账号状态已在另一插件变更，请重试。"] = "Another plugin changed the account state. Please retry.",
        ["本机登录状态已恢复，请重新登录。"] = "Local sign-in state was recovered. Please sign in again.",
        ["共用登录文件过大，无法自动恢复。"] = "The shared sign-in file is too large to recover automatically.",
        ["共用登录文件被目录占用。"] = "A directory occupies the shared sign-in file path.",
        ["共用登录文件损坏。"] = "The shared sign-in file is damaged.",
        ["共用登录状态繁忙，请稍后重试。"] = "The shared session is busy. Please try again shortly.",
        // Current cloud API validation replies use the same display boundary as
        // local errors; keep protocol codes and server behavior untouched.
        ["操作过于频繁，请稍后重试。"] = "Too many requests. Please try again shortly.",
        ["请输入完整账号名。"] = "Enter the full account name.",
        ["好友申请不存在。"] = "Friend request not found.",
        ["好友备注最多40字，不能包含换行或控制字符。"] = "Friend notes allow up to 40 characters, without line breaks or control characters.",
        ["当前不是好友。"] = "You are not currently friends.",
        ["其他设备已修改此备注，请刷新后重新编辑。"] = "Another device changed this note. Refresh before editing again.",
        ["好友数量已达上限。"] = "You have reached the friend limit.",
        ["暂时无法添加该好友。"] = "This friend cannot be added right now.",
        ["不能添加自己为好友。"] = "You cannot add yourself as a friend.",
        ["待处理申请过多。"] = "Too many pending friend requests.",
        ["只能处理收到的申请。"] = "You can only respond to requests you received.",
        ["申请已经处理。"] = "This request has already been handled.",
        ["状态文字最多80个字符，不能包含换行或控制字符。"] = "Status text allows up to 80 characters, without line breaks or control characters.",
        ["其他设备已更新状态，请刷新后再保存。"] = "Another device updated your status. Refresh before saving again.",
        ["客户端标识无效。"] = "Invalid client identifier.",
        ["在线状态参数无效。"] = "Invalid presence settings.",
        ["副本状态参数无效。"] = "Invalid duty status.",
        ["在线客户端过多。"] = "Too many clients are online.",
        ["会话不存在。"] = "Conversation not found.",
        ["已读位置必须是非负消息标识。"] = "The read position must be a non-negative message identifier.",
        ["消息字段无效。"] = "Invalid message fields.",
        ["快捷消息标识无效。"] = "Invalid quick message identifier.",
        ["消息内容或发送序号无效。"] = "Invalid message content or send sequence.",
        ["官方会话仅允许管理员发送。"] = "Only administrators can send announcements.",
        ["好友暂时无法接收消息。"] = "This friend cannot receive messages right now.",
        ["发送序号已被其他消息使用，请重新同步。"] = "Another message used this send sequence. Please sync again.",
        ["发送序号已过期或不连续，请重新同步；不要自动重发未知结果的旧消息。"] = "The send sequence expired or has a gap. Sync again; do not automatically resend unconfirmed messages.",
        ["确认列表最多包含3个消息标识。"] = "An acknowledgement can contain up to three message identifiers.",
        ["只能确认发送给自己的消息。"] = "You can only acknowledge messages addressed to you.",
        ["分页标识无效。"] = "Invalid page cursor.",
        ["账号标识无效。"] = "Invalid account identifier.",
        ["账号不存在。"] = "Account not found.",
        ["请求必须是JSON对象。"] = "The request must be a JSON object.",
        ["接口不存在。"] = "API endpoint not found.",
        ["请填写 DACT 账号和密码。"] = "Enter your DACT account and password.",
        ["用户名或密码错误。"] = "Incorrect username or password.",
        ["密码长度必须为 10 到 128 个字符。"] = "Passwords must contain 10–128 characters.",
        ["新密码长度必须为 10 到 128 个字符。"] = "The new password must contain 10–128 characters.",
        ["设备标识无效。"] = "Invalid device identifier.",
        ["用户名或重置码无效。"] = "Invalid username or reset code.",
        ["用户名或恢复密钥无效。"] = "Invalid username or recovery key.",
        ["注册密钥无效。"] = "Invalid activation key.",
        ["注册密钥无效、已使用或已过期。"] = "The activation key is invalid, used, or expired.",
        ["登录状态不存在或已过期。"] = "Your session is missing or expired.",
        ["备份不存在。"] = "Backup not found.",
        ["请先登录。"] = "Please sign in first.",
    };

    private sealed record Rule(string Pattern, string Format, bool TranslateFirst = false)
    {
        internal Regex Matcher { get; } = new("\\A" + Pattern + "\\z",
            RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    // Anchored templates preserve timestamps and diagnostics verbatim. Never perform
    // broad substring replacement: server details may contain user names or paths.
    private static readonly Rule[] Rules =
    [
        new(@"(.*) 封禁原因：(.*)", "{0} Reason: {1}", true),
        new(@"(.*)（无法保存本机恢复密钥：(.*)）", "{0} (Could not save the local recovery key: {1})", true),
        new(@"(.*)（共用登录同步失败：(.*)）", "{0} (Shared sign-in sync failed: {1})", true),
        new(@"(.*)（本地封禁标记写入失败：(.*)）", "{0} (Could not save the local restriction marker: {1})", true),
        new(@"(.*)（封禁时间：(.*)）", "{0} (Banned at: {1})", true),
        new(@"(.*) 重试会保留原消息；需要改写时先放弃此发送。", "{0} Retrying keeps the original message. Discard this send before editing it.", true),
        new(@"云账号已连接，但(.*)。", "Cloud account connected, but {0}.", true),
        new(@"注册成功，但(.*)。请立即抄下恢复密钥；它只在本次注册后显示。", "Account created, but {0}. Save your recovery key now; it is shown only once.", true),
        new(@"登录成功，但(.*)。", "Signed in, but {0}.", true),
        new(@"密码已重置，但(.*)。", "Password reset, but {0}.", true),
        new(@"已刷新，共 (\d+) 个云端版本。", "Refreshed: {0} cloud backups."),
        new(@"上传完成：(.*)。", "Upload complete: {0}."),
        new(@"云服务请求失败（HTTP (\d+)）。", "Cloud request failed (HTTP {0})."),
        new(@"恢复密钥自助改密暂未启用：(.*)", "Recovery-key password reset is not enabled: {0}", true),
        new(@"自动登录状态未能保存：(.*)", "Automatic sign-in could not be saved: {0}", true),
        new(@"旧的自动登录状态未能清除：(.*)", "Previous automatic sign-in could not be cleared: {0}", true),
        new(@"无法恢复本地封禁标记：(.*)", "Could not restore the local restriction marker: {0}", true),
        new(@"共用登录同步失败：(.*)", "Shared sign-in sync failed: {0}", true),
        new(@"共用账号连接失败，将自动重试：(.*)", "Shared account connection failed; retrying automatically: {0}", true),
        new(@"备注同步未确认：(.*)", "Note sync is unconfirmed: {0}", true),
    ];

    internal static string English(string message) => Translate(message, 0);

    private static string Translate(string message, int depth)
    {
        if (Messages.TryGetValue(message, out var translated)) return translated;
        if (depth >= 4 || !message.Any(c => c is >= '\u4e00' and <= '\u9fff')) return message;
        foreach (var rule in Rules)
        {
            var match = rule.Matcher.Match(message);
            if (!match.Success) continue;
            var values = match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray();
            if (rule.TranslateFirst) values[0] = Translate(values[0], depth + 1);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, rule.Format, values);
        }
        return message;
    }
}
