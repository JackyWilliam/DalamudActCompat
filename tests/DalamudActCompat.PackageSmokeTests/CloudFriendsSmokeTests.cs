using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;

internal static class CloudFriendsSmokeTests
{
    public static async Task RunAsync(string root)
    {
        await ErrorsAsync();
        await ConnectionAsync();
        await PresenceSessionAsync(root);
        foreach (var mode in new[] { "online", "old-server", "network-failure" }) await SessionAsync(root, mode);
        await RevocationDuringInitializationAsync(root);
        if (Environment.GetEnvironmentVariable("DACT_FRIENDS_FIXTURE") is { Length: > 0 } fixture)
            await LiveAsync(JsonSerializer.Deserialize<Fixture>(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        if (Environment.GetEnvironmentVariable("DACT_FRIENDS_LEGACY_FIXTURE") is { Length: > 0 } legacy)
            await LegacyAsync(root, JsonSerializer.Deserialize<LegacyFixture>(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Console.WriteLine("Cloud friends: errors, connection lifetime and session isolation passed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = JsonContent.Create(value) };
    private static async Task<CloudApiException> ApiErrorAsync(Func<Task> call, string code)
    {
        try { await call(); throw new Exception($"Expected {code}."); }
        catch (CloudApiException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}."); return error; }
    }

    private static async Task ErrorsAsync()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Check(request.Headers.Authorization?.Parameter == "isolated-token", "Friends request lost auth.");
            return Task.FromResult(Json(new { error = "sequence_retired", message = "retired", nextSendSequence = 39, retryAfterSeconds = 60 }, HttpStatusCode.Conflict));
        })) { BaseAddress = new Uri("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        var error = await ApiErrorAsync(() => api.ListFriendsAsync("isolated-token", default), "sequence_retired");
        Check(error.StatusCode == HttpStatusCode.Conflict && error.NextSendSequence == 39 && error.RetryAfterSeconds == 60,
            "Client discarded retry/sequence contract.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await api.SyncChatAsync("isolated-token", cancelled.Token); throw new Exception("Cancellation was swallowed."); }
        catch (OperationCanceledException) { }
        using var malformedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            { Content = new StringContent("<html>proxy error</html>") }))) { BaseAddress = new Uri("https://isolated.test/") };
        using var malformedApi = new CloudApiClient(malformedHttp);
        await ApiErrorAsync(() => malformedApi.SyncChatAsync("isolated-token", default), "http_error");
    }

    private static async Task ConnectionAsync()
    {
        var puts = new ConcurrentQueue<Guid>(); var deletes = new ConcurrentQueue<Guid>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var id = body.RootElement.GetProperty("clientId").GetGuid();
            Check(request.RequestUri!.AbsolutePath == "/api/v1/friends/presence", "Unexpected connection route.");
            (request.Method == HttpMethod.Put ? puts : deletes).Enqueue(id);
            return Json(new { online = request.Method == HttpMethod.Put, onlineConnectionCount = 1, heartbeatIntervalSeconds = 25, expiresAt = DateTimeOffset.UtcNow.AddSeconds(90) });
        })) { BaseAddress = new Uri("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        using var first = new CancellationTokenSource(); using var second = new CancellationTokenSource();
        Task Delay(TimeSpan interval, CancellationToken ct)
        {
            Check(interval == TimeSpan.FromSeconds(25), "Wrong heartbeat cadence.");
            return Task.Delay(Timeout.Infinite, ct);
        }
        var one = CloudFriendConnection.RunAsync(api, "same-token", first.Token, Delay);
        var two = CloudFriendConnection.RunAsync(api, "same-token", second.Token, Delay);
        Check(puts.Count == 2 && puts.Distinct().Count() == 2, "Reused session monitors share a lease.");
        first.Cancel(); try { await one; } catch (OperationCanceledException) { }
        Check(deletes.Count == 1 && deletes.First() == puts.First() && !two.IsCompleted, "Old cleanup disconnected the successor.");
        second.Cancel(); try { await two; } catch (OperationCanceledException) { }
        Check(deletes.Count == 2, "Connection cleanup did not finish.");
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized })
        {
            using var stop = new CancellationTokenSource();
            using var failureHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
                new { error = status == HttpStatusCode.NotFound ? "not_found" : "unauthorized", message = "isolated" }, status))))
                { BaseAddress = new Uri("https://isolated.test/") };
            using var failureApi = new CloudApiClient(failureHttp);
            var run = CloudFriendConnection.RunAsync(failureApi, "same-token", stop.Token, (interval, ct) =>
            {
                Check(interval == TimeSpan.FromMinutes(5), "Old deployment capability was retried too frequently.");
                stop.Cancel(); return Task.CompletedTask;
            });
            if (status == HttpStatusCode.Unauthorized) await ApiErrorAsync(() => run, "unauthorized");
            else await run;
        }
    }

    private static async Task SessionAsync(string root, string mode)
    {
        var paths = new PluginPaths(Path.Combine(root, "friends-session-" + mode)); paths.EnsureCreated();
        var credentials = new CloudCredentialStore(paths.CloudCredentialFile);
        credentials.Save(new CloudStoredCredentials("isolated", "candidate-token", DateTimeOffset.UtcNow.AddDays(1),
            new PortableConfigurationBackupService().GenerateRecoveryKey()));
        var presence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var friendReads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/friends/presence"))
            {
                if (request.Method == HttpMethod.Put) presence.TrySetResult(); else cleanup.TrySetResult();
                if (mode == "network-failure") throw new HttpRequestException("isolated network failure");
                if (mode == "old-server") return Task.FromResult(Json(new { error = "not_found", message = "old deployment" }, HttpStatusCode.NotFound));
                return Task.FromResult(Json(new { online = true, onlineConnectionCount = 1, heartbeatIntervalSeconds = 25 }));
            }
            if (path.EndsWith("/friends")) { Interlocked.Increment(ref friendReads); return pending.Task; }
            if (path.EndsWith("/auth/me") || path.EndsWith("/auth/logout")) return Task.FromResult(Json(new { }));
            if (path.EndsWith("/backups")) return Task.FromResult(Json(new { backups = Array.Empty<object>() }));
            if (path.EndsWith("/invitations")) return Task.FromResult(Json(new { quota = 3, used = 0, remaining = 3, invitations = Array.Empty<object>() }));
            return Task.FromResult(Json(new { error = "not_found", message = "isolated" }, HttpStatusCode.NotFound));
        })) { BaseAddress = new Uri("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        using var service = new CloudClientService(paths, api, credentials, new CloudBanStore(paths.CloudBanFile),
            new CloudMachineIdentity(paths.CloudDeviceFile), new CloudKeyEnvelopeService(), new PortableConfigurationBackupService());
        try { await service.ListFriendsAsync(default); throw new Exception("Unvalidated session accessed friends."); }
        catch (InvalidOperationException) { }
        Check(friendReads == 0 && !presence.Task.IsCompleted, "Candidate token announced online before validation.");
        await service.InitializeAsync(default);
        await presence.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(service.Snapshot.IsSignedIn, $"{mode} heartbeat failure invalidated login.");
        try { await service.ListFriendsAsync(default, service.FriendsSession with { Generation = -1 }); throw new Exception("Stale UI command captured current credentials."); }
        catch (OperationCanceledException) { }
        Check(friendReads == 0, "Rejected generation reached HTTP.");
        var read = service.ListFriendsAsync(default);
        await service.LogoutAsync(default);
        pending.SetResult(Json(new { friends = Array.Empty<object>(), requests = Array.Empty<object>(), onlineCount = 0, policyNotice = CloudChatPolicy.Notice }));
        try { await read; throw new Exception("Previous session response escaped after logout."); }
        catch (OperationCanceledException) { }
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!service.Snapshot.IsSignedIn, "Friends cleanup revived a signed-out account.");
    }

    private static async Task PresenceSessionAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "friends-presence-session")); paths.EnsureCreated();
        var credentials = new CloudCredentialStore(paths.CloudCredentialFile);
        credentials.Save(new CloudStoredCredentials("isolated", "presence-token", DateTimeOffset.UtcNow.AddDays(1),
            new PortableConfigurationBackupService().GenerateRecoveryKey()));
        var profile = new CloudPresenceSettings("online", "", true, 1); var failSave = false;
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/friends/presence/settings"))
            {
                if (failSave) throw new HttpRequestException("isolated uncertain privacy response");
                profile = (await request.Content!.ReadFromJsonAsync<CloudPresenceSettings>(cancellationToken: ct))! with { Revision = profile.Revision + 1 };
                return Json(profile);
            }
            if (path.EndsWith("/friends/presence"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (request.Method == HttpMethod.Put)
                {
                    var hasDuty = body.RootElement.TryGetProperty("duty", out var duty) && duty.ValueKind == JsonValueKind.Object;
                    if (hasDuty) uploaded.TrySetResult();
                    else if (uploaded.Task.IsCompleted) cleared.TrySetResult();
                }
                // No settings on the initial legacy-shaped heartbeat: exercise a
                // privacy click before the first profile response for this login.
                return Json(new { online = true, onlineConnectionCount = 1, heartbeatIntervalSeconds = 25 });
            }
            if (path.EndsWith("/friends")) return Json(new CloudFriendList([], 0, [], CloudChatPolicy.Notice, new("self", "isolated"), profile));
            if (path.EndsWith("/auth/me") || path.EndsWith("/auth/logout")) return Json(new { });
            if (path.EndsWith("/backups")) return Json(new { backups = Array.Empty<object>() });
            if (path.EndsWith("/invitations")) return Json(new { quota = 3, used = 0, remaining = 3, invitations = Array.Empty<object>() });
            return Json(new { error = "not_found", message = "isolated" }, HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        using var service = new CloudClientService(paths, api, credentials, new CloudBanStore(paths.CloudBanFile),
            new CloudMachineIdentity(paths.CloudDeviceFile), new CloudKeyEnvelopeService(), new PortableConfigurationBackupService());
        await service.InitializeAsync(default); var session = service.FriendsSession;
        Check(service.FriendDutySharingSession is null, "Duty collection started before account opt-in was read.");
        service.SuppressFriendDuty(session); await service.ListFriendsAsync(default, session);
        Check(service.FriendDutySharingSession is null, "First delayed settings response undid local privacy suppression.");
        await service.UpdateFriendPresenceSettingsAsync(profile, default, session);
        Check(service.FriendDutySharingSession == session, "Confirmed opt-in did not enable duty sampling.");
        service.SetFriendDutyActivity(session, new(123, "隔离副本"));
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(4));
        failSave = true;
        try { await service.UpdateFriendPresenceSettingsAsync(profile with { ShareDuty = false }, default, session); throw new Exception("Expected failed privacy save."); }
        catch (HttpRequestException) { }
        service.SetFriendDutyActivity(session, new(124, "must not upload"));
        await service.ListFriendsAsync(default, session);
        Check(service.FriendDutySharingSession is null, "Failed save or stale list resumed activity sharing.");
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(4));
        failSave = false;
        profile = await service.UpdateFriendPresenceSettingsAsync(profile with { ShareDuty = false }, default, session);
        Check(service.FriendDutySharingSession is null, "Confirmed off enabled collection.");
        await service.LogoutAsync(default);
        service.SetFriendDutyActivity(session, new(123, "old login"));
        Check(service.FriendDutySharingSession is null, "Previous login revived duty activity.");
        try { await service.UpdateFriendPresenceSettingsAsync(profile with { ShareDuty = true }, default, session); throw new Exception("Old login edited preferences."); }
        catch (InvalidOperationException) { }
    }

    private static async Task LiveAsync(Fixture f)
    {
        // The private service harness provides isolated ephemeral accounts only.
        Check(new Uri(f.BaseUrl).IsLoopback, "Live friends tests require a loopback fixture.");
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl + "/") };
        using var api = new CloudApiClient(http);
        var a = f.A.Token; var b = f.B.Token; var c = f.C.Token;
        Check((await api.ListFriendsAsync(a, default)).Friends.Count == 0, "Fixture was not isolated.");
        var lookup = await api.LookupFriendAsync(a, f.B.Username.ToUpperInvariant(), default);
        Check(lookup.User?.Id == f.B.Id && lookup.Relationship == "none", "Exact lookup contract changed.");
        var request = await api.RequestFriendAsync(a, f.B.Username, default);
        Check((await api.ListFriendsAsync(b, default)).Requests.Single().Direction == "incoming", "Incoming request missing.");
        await ApiErrorAsync(() => api.AcceptFriendAsync(a, request.Id, default), "friend_request_forbidden");
        var accepted = await api.AcceptFriendAsync(b, request.Id, default);
        var id = accepted.ConversationId!;
        await ApiErrorAsync(() => api.GetChatAsync(c, id, default), "conversation_not_found");
        CloudChatSendRequest? last = null;
        for (var i = 1; i <= 8; i++)
        {
            last = new CloudChatSendRequest(i, Guid.NewGuid(), $"offline {i}");
            await api.SendChatAsync(a, id, last, default);
        }
        var duplicate = await api.SendChatAsync(a, id, last!, default);
        Check(duplicate.Duplicate && duplicate.NextSendSequence == 9, "Retry inserted another message.");
        var retired = await ApiErrorAsync(() => api.SendChatAsync(a, id, new(1, Guid.NewGuid(), "retired"), default), "sequence_retired");
        Check(retired.NextSendSequence == 9, "Retired watermark was lost.");
        var sync = await api.SyncChatAsync(b, default);
        Check(sync.Deliveries.Count == 3 && sync.Deliveries.First().Text == "offline 6", "Offline cap failed across C# HTTP.");
        Check(sync.PolicyNotice == CloudChatPolicy.Notice && sync.QuickMessages.Any(q => q.Id == CloudChatPolicy.InviteNext), "Notice/quick message contract differs.");
        var ack = await api.AcknowledgeChatAsync(b, id, sync.Deliveries.Select(m => m.Id).ToArray(), default);
        Check(ack.History.Count == 3 && ack.Pending.Count == 0 && ack.RetiredIds!.Count == 0, "ACK deserialization failed.");
        var leaseA = Guid.NewGuid(); var leaseB = Guid.NewGuid();
        await api.SetFriendPresenceAsync(a, leaseA, true, default);
        await api.SetFriendPresenceAsync(b, leaseB, true, default);
        Check((await api.ListFriendsAsync(a, default)).OnlineCount == 1, "Heartbeat did not affect friend count.");
        var profile = (await api.ListFriendsAsync(b, default)).PresenceSettings!;
        Check(profile == CloudPresenceSettings.Default, "New account duty sharing was not off by default.");
        profile = await api.UpdateFriendPresenceSettingsAsync(b, profile with { Status = "busy", Text = "今晚刷坐骑", ShareDuty = true }, default);
        var duty = new CloudDutyActivity(123, "隔离测试副本");
        await api.SetFriendPresenceAsync(b, leaseB, true, default, new(profile.Revision, duty));
        var shown = (await api.ListFriendsAsync(a, default)).Friends.Single();
        Check(shown.Status == "busy" && shown.StatusText == "今晚刷坐骑" && shown.Duty == duty, "New presence payload failed across real C# HTTP.");
        var oldRevision = profile.Revision;
        profile = await api.UpdateFriendPresenceSettingsAsync(b, profile with { ShareDuty = false }, default);
        await api.SetFriendPresenceAsync(b, leaseB, true, default, new(oldRevision, duty));
        Check((await api.ListFriendsAsync(a, default)).Friends.Single().Duty is null, "Late activity restored revoked sharing.");
        profile = await api.UpdateFriendPresenceSettingsAsync(b, profile with { Status = "invisible" }, default);
        Check((await api.ListFriendsAsync(a, default)).OnlineCount == 0, "Invisible friend counted online.");
        await api.UpdateFriendPresenceSettingsAsync(b, profile with { Status = "online", Text = "" }, default);
        for (var i = 0; i < 24; i++)
        {
            var token = i % 2 == 0 ? a : b;
            var view = await api.GetChatAsync(token, id, default);
            await api.SendChatAsync(token, id, new(view.NextSendSequence!.Value, Guid.NewGuid(), QuickMessageId: CloudChatPolicy.WhenFinished), default);
        }
        var history = await api.GetChatAsync(a, id, default);
        Check(history.History.Count == 20 && history.History.All(m => m.Text == "你什么时候结束" && !m.Sender.IsOfficial), "Combined retention/quick message identity failed.");
        Check(history.History.Select(m => m.Id).SequenceEqual(history.History.Select(m => m.Id).Order()), "History order unstable.");
        var official = await api.GetChatAsync(a, f.OfficialId, default);
        Check(official.Kind == "official" && official.NextSendSequence is null && official.Pending.Single().Sender.IsOfficial, "Official sender was not distinguishable.");
        await api.AcknowledgeChatAsync(a, f.OfficialId, official.Pending.Select(m => m.Id).ToArray(), default);
        await ApiErrorAsync(() => api.SendChatAsync(a, f.OfficialId, new(1, Guid.NewGuid(), "forbidden"), default), "official_sender_required");
        await api.SetFriendPresenceAsync(b, leaseB, false, default);
        Check((await api.ListFriendsAsync(a, default)).OnlineCount == 0, "Disconnect did not clear online state.");
        await LiveControllersAsync(api, f, id);
        await api.RemoveFriendAsync(a, request.Id, default);
        await ApiErrorAsync(() => api.GetChatAsync(b, id, default), "conversation_not_found");
        var declined = await api.RequestFriendAsync(c, f.B.Username, default);
        Check((await api.DeclineFriendAsync(b, declined.Id, default)).State == "declined", "Decline failed.");
        await api.SetFriendPresenceAsync(a, leaseA, false, default);
        Console.WriteLine("Cloud friends: real C# -> HTTP -> SQLite relations, 20/3, retry, ACK, quick/official messages passed.");
    }

    private static async Task RevocationDuringInitializationAsync(string root)
    {
        var paths = new PluginPaths(Path.Combine(root, "friends-revoked-initialization")); paths.EnsureCreated();
        var credentials = new CloudCredentialStore(paths.CloudCredentialFile);
        credentials.Save(new CloudStoredCredentials("isolated", "revoked-during-refresh", DateTimeOffset.UtcNow.AddDays(1),
            new PortableConfigurationBackupService().GenerateRecoveryKey()));
        var backups = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/friends/presence"))
            {
                if (request.Method == HttpMethod.Delete) rejected.TrySetResult();
                return Task.FromResult(Json(new { error = "unauthorized", message = "revoked" }, HttpStatusCode.Unauthorized));
            }
            if (path.EndsWith("/backups")) return backups.Task;
            if (path.EndsWith("/invitations")) return Task.FromResult(Json(new { invitations = Array.Empty<object>() }));
            return Task.FromResult(Json(new { }));
        })) { BaseAddress = new Uri("https://isolated.test/") };
        using var api = new CloudApiClient(http);
        using var service = new CloudClientService(paths, api, credentials, new CloudBanStore(paths.CloudBanFile),
            new CloudMachineIdentity(paths.CloudDeviceFile), new CloudKeyEnvelopeService(), new PortableConfigurationBackupService());
        var initialization = service.InitializeAsync(default);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(SpinWait.SpinUntil(() => service.Snapshot.StatusIsError, TimeSpan.FromSeconds(3)), "Revoked heartbeat was not applied.");
        // A success response already in flight is older than the revocation.
        backups.SetResult(Json(new { backups = Array.Empty<object>() }));
        await initialization;
        Check(!service.Snapshot.IsSignedIn, "Stale backup refresh resurrected a revoked session.");
    }

    private sealed record Account(string Id, string Username, string Token);
    private sealed record ProductionKeys(string BaseUrl, string UsernameA, string UsernameB, string ActivationA, string ActivationB);
    public static async Task ProductionAsync(string root)
    {
        // This path is opt-in and needs two newly generated, expiring QA activation
        // keys. No existing account credentials or recipients are accepted.
        var path = Environment.GetEnvironmentVariable("DACT_FRIENDS_QA_KEYS") ?? throw new Exception("Explicit QA key manifest required.");
        var keys = JsonSerializer.Deserialize<ProductionKeys>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(keys.BaseUrl == "https://admin.localhost2019.com/" && keys.UsernameA.StartsWith("dactqa_") && keys.UsernameB.StartsWith("dactqa_") && keys.UsernameA != keys.UsernameB,
            "Production smoke restricted to the configured host and isolated QA accounts.");
        using var http = new HttpClient { BaseAddress = new Uri(keys.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        using var api = new CloudApiClient(http);
        var backup = new PortableConfigurationBackupService(); var envelope = new CloudKeyEnvelopeService();
        var recovery = backup.GenerateRecoveryKey(); var password = "isolated-" + Guid.NewGuid().ToString("N");
        var device = "dact-device-v1_" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var a = await api.RegisterAsync(keys.UsernameA, password, keys.ActivationA, device, envelope.Create(recovery, password), envelope.CreateRecoveryVerifier(recovery), default);
        var b = await api.RegisterAsync(keys.UsernameB, password, keys.ActivationB, device, envelope.Create(recovery, password), envelope.CreateRecoveryVerifier(recovery), default);
        try
        {
            var login = await api.LoginAsync(keys.UsernameA, password, device, default);
            Check(envelope.Open(login.KeyEnvelope!, password) == recovery, "Production login lost encrypted recovery key.");
            var configRoot = Path.Combine(root, "production-qa", "pluginConfigs");
            var config = Path.Combine(configRoot, "DalamudActCompat"); Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(configRoot, "DalamudActCompat.json"), "{\"Version\":16,\"CloudMarker\":\"isolated friends deployment QA\"}");
            var file = Path.Combine(root, "qa.dactcloud");
            var exported = await backup.ExportEncryptedAsync(config, file, recovery, default);
            var uploaded = await api.UploadBackupAsync(login.Token, file, exported.ContentId, default);
            var downloaded = Path.Combine(root, "qa-downloaded.dactcloud");
            await api.DownloadBackupAsync(login.Token, uploaded, downloaded, default);
            var uploadedBytes = await File.ReadAllBytesAsync(file);
            var downloadedBytes = await File.ReadAllBytesAsync(downloaded);
            Check(uploadedBytes.SequenceEqual(downloadedBytes), "Production encrypted backup bytes changed.");
            Check((await backup.PreviewRestoreAsync(downloaded, config, recovery, default)).FileCount == 1, "Production encrypted backup could not decrypt.");
            var relation = await api.RequestFriendAsync(a.Token, keys.UsernameB, default);
            var accepted = await api.AcceptFriendAsync(b.Token, relation.Id, default); var id = accepted.ConversationId!;
            for (var i = 1; i <= 4; i++) await api.SendChatAsync(a.Token, id, new(i, Guid.NewGuid(), "isolated QA " + i), default);
            var waiting = await api.GetChatAsync(b.Token, id, default);
            Check(waiting.Pending.Count == 3 && waiting.Pending.First().Text.EndsWith("2"), "Production offline pruning failed.");
            var acknowledged = await api.AcknowledgeChatAsync(b.Token, id, waiting.Pending.Select(m => m.Id).ToArray(), default);
            Check(acknowledged.Pending.Count == 0 && acknowledged.History.Count == 3, "Production ACK failed.");
            var lease = Guid.NewGuid(); await api.SetFriendPresenceAsync(b.Token, lease, true, default);
            Check((await api.ListFriendsAsync(a.Token, default)).OnlineCount == 1, "Production online count failed.");
            var request = new CloudChatSendRequest(5, Guid.NewGuid(), QuickMessageId: CloudChatPolicy.WhenFinished);
            var sent = await api.SendChatAsync(a.Token, id, request, default);
            var retry = await api.SendChatAsync(a.Token, id, request, default);
            Check(sent.Message.Text == "你什么时候结束" && retry.Duplicate && retry.Message.Id == sent.Message.Id, "Production quick/retry contract failed.");
            await api.SetFriendPresenceAsync(b.Token, lease, false, default);
            await api.RemoveFriendAsync(a.Token, relation.Id, default);
            await api.LogoutAsync(login.Token, default);
            Console.WriteLine("Production QA passed: isolated registration/login/key envelope, encrypted backup upload/download/decrypt, friends, offline3 pruning, ACK, online count, quick send and immutable retry.");
        }
        finally { await api.LogoutAsync(a.Token, default); await api.LogoutAsync(b.Token, default); }
    }
    private sealed record Fixture(string BaseUrl, Account A, Account B, Account C, string OfficialId);
    private sealed record LegacyFixture(string BaseUrl, string ActivationKey);

    private static async Task LiveControllersAsync(CloudApiClient api, Fixture f, string id)
    {
        var root = Path.Combine(Path.GetTempPath(), "dact-friends-controller-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = new FriendsChatController(new RemoteSession(api, f.A), new FriendsLocalStateStore(Path.Combine(root, "a")), TimeSpan.FromMilliseconds(50));
            using var b = new FriendsChatController(new RemoteSession(api, f.B), new FriendsLocalStateStore(Path.Combine(root, "b")), TimeSpan.FromMilliseconds(50));
            a.AttachConsumer();
            async Task Until(Func<bool> check)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!check()) await Task.Delay(10, limit.Token);
            }
            await Until(() => a.Snapshot.Conversations.ContainsKey(id) && b.Snapshot.Friends is not null);
            Check(a.Snapshot.Friends!.User?.Id == f.A.Id && b.Snapshot.Friends!.User?.Id == f.B.Id, "Controller account identities differ from authenticated users.");
            var op = a.Send(id, "", CloudChatPolicy.InviteNext);
            await Until(() => !a.Snapshot.Busy && a.Snapshot.Conversations[id].PendingSend is null && a.Snapshot.Conversations[id].Chat.Pending.Any(m => m.OperationId == op));
            var waiting = await api.GetChatAsync(f.B.Token, id, default);
            Check(waiting.Pending.Single().Text == "下把邀我" && b.Snapshot.Conversations.Count == 0, "No-consumer delivery was swallowed.");
            b.AttachConsumer();
            await Until(() => b.Snapshot.Conversations.TryGetValue(id, out var view) && view.Chat.Pending.Count == 0 && view.Chat.History.Any(m => m.OperationId == op));
            Check(b.Snapshot.Conversations[id].Unread && b.Snapshot.Conversations[id].Chat.History.Count == 20, "Controller lost unread state or 20-history cap after ACK.");
            b.MarkRead(id, long.MaxValue); await Until(() => !b.Snapshot.Conversations[id].Unread);
            await Until(() => b.Snapshot.Conversations[id].Chat.ReadThrough > 0);
            using (var reinstalled = new FriendsChatController(new RemoteSession(api, f.B),
                new FriendsLocalStateStore(Path.Combine(root, "b-clean-reinstall")), TimeSpan.FromMilliseconds(50)))
            {
                reinstalled.AttachConsumer(); await Until(() => reinstalled.Snapshot.InitialSyncComplete);
                Check(!reinstalled.Snapshot.Conversations[id].Unread, "Real server failed to preserve read state with an empty local store.");
                a.Send(id, "new after reinstall");
                await Until(() => reinstalled.Snapshot.Conversations[id].UnreadCount == 1);
                await Until(() => b.Snapshot.Conversations[id].UnreadCount == 1);
                b.MarkRead(id, long.MaxValue);
                await Until(() => !reinstalled.Snapshot.Conversations[id].Unread);
            }
            var official = a.Snapshot.Conversations[f.OfficialId];
            Check(official.Chat.Kind == "official" && official.Chat.NextSendSequence is null && official.Chat.History.Single().Sender.IsOfficial, "Controller official identity not readonly.");
            a.Send(f.OfficialId, "cannot reply"); await Until(() => !a.Snapshot.Busy);
            Check((await api.GetChatAsync(f.A.Token, f.OfficialId, default)).History.Count == 1, "UI controller allowed reply to official.");
            Console.WriteLine("Cloud friends: two real account UI controllers, consumer timing, quick messages, unread and official readonly passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class RemoteSession(CloudApiClient api, Account account) : ICloudFriendsSession
    {
        public CloudFriendsSession FriendsSession => new(1, true, account.Username);
        private Task<T> Run<T>(CloudFriendsSession? expected, Func<Task<T>> call)
        { Check(expected == FriendsSession, "Remote UI omitted its account generation."); return call(); }
        public Task<CloudFriendList> ListFriendsAsync(CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.ListFriendsAsync(account.Token, ct));
        public Task<CloudFriendLookup> LookupFriendAsync(string name, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.LookupFriendAsync(account.Token, name, ct));
        public Task<CloudFriendRelation> RequestFriendAsync(string name, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.RequestFriendAsync(account.Token, name, ct));
        public Task<CloudFriendRelation> AcceptFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.AcceptFriendAsync(account.Token, id, ct));
        public Task<CloudFriendRelation> DeclineFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.DeclineFriendAsync(account.Token, id, ct));
        public Task<CloudFriendRemoval> RemoveFriendAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.RemoveFriendAsync(account.Token, id, ct));
        public Task<CloudChatSync> SyncChatAsync(CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.SyncChatAsync(account.Token, ct));
        public Task<CloudChatConversation> GetChatAsync(string id, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.GetChatAsync(account.Token, id, ct));
        public Task<CloudChatSendResult> SendChatAsync(string id, CloudChatSendRequest message, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.SendChatAsync(account.Token, id, message, ct));
        public Task<CloudChatConversation> AcknowledgeChatAsync(string id, IReadOnlyList<long> ids, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.AcknowledgeChatAsync(account.Token, id, ids, ct));
        public Task<CloudChatConversation> MarkChatReadAsync(string id, long through, CancellationToken ct, CloudFriendsSession? expectedSession = null) => Run(expectedSession, () => api.MarkChatReadAsync(account.Token, id, through, ct));
    }

    private static async Task LegacyAsync(string root, LegacyFixture f)
    {
        Check(new Uri(f.BaseUrl).IsLoopback, "Legacy upgrade tests require a loopback fixture.");
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl + "/") };
        using var api = new CloudApiClient(http);
        var backup = new PortableConfigurationBackupService();
        var envelope = new CloudKeyEnvelopeService();
        var recovery = backup.GenerateRecoveryKey();
        const string password = "isolated-upgrade-password";
        var device = "dact-device-v1_" + new string('L', 43);
        var registered = await api.RegisterAsync("legacy_upgrade", password, f.ActivationKey, device,
            envelope.Create(recovery, password), envelope.CreateRecoveryVerifier(recovery), default);
        var login = await api.LoginAsync("legacy_upgrade", password, device, default);
        Check(envelope.Open(login.KeyEnvelope!, password) == recovery, "Legacy login lost encryption key.");
        var configRoot = Path.Combine(root, "legacy-upgrade", "pluginConfigs");
        var paths = new PluginPaths(Path.Combine(configRoot, "DalamudActCompat")); paths.EnsureCreated();
        await File.WriteAllTextAsync(Path.Combine(configRoot, "DalamudActCompat.json"), "{\"Version\":16,\"CloudMarker\":\"upgrade preserved\"}");
        var credentials = new CloudCredentialStore(paths.CloudCredentialFile);
        credentials.Save(new CloudStoredCredentials(login.User.Username, login.Token, login.ExpiresAt, recovery));
        using var service = new CloudClientService(paths, api, credentials, new CloudBanStore(paths.CloudBanFile),
            new CloudMachineIdentity(paths.CloudDeviceFile), envelope, backup);
        await service.InitializeAsync(default);
        Check(service.Snapshot.IsSignedIn, "New client could not initialize against old service.");
        await ApiErrorAsync(() => service.ListFriendsAsync(default), "not_found");
        Check(service.Snapshot.IsSignedIn, "Missing friends endpoint broke existing login.");
        Check(await service.UploadAsync(paths.ConfigDirectory, default), "Missing friends endpoint broke encrypted upload.");
        var versions = await api.ListBackupsAsync(login.Token, default);
        Check(versions.Count == 1 && service.Snapshot.IsSignedIn, "Upgrade compatibility lost backup state.");
        var downloaded = Path.Combine(root, "legacy-upgrade", "download.dactcloud");
        await api.DownloadBackupAsync(login.Token, versions[0], downloaded, default);
        var preview = await backup.PreviewRestoreAsync(downloaded, paths.ConfigDirectory, recovery, default);
        Check(preview is not null, "Legacy encrypted backup was not decryptable by the new client.");
        await service.LogoutAsync(default);
        Console.WriteLine("Cloud friends: new client against pre-feature service preserves login and encrypted backup upload/download/decryption.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
