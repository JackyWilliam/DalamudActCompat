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
        var read = service.ListFriendsAsync(default);
        await service.LogoutAsync(default);
        pending.SetResult(Json(new { friends = Array.Empty<object>(), requests = Array.Empty<object>(), onlineCount = 0, policyNotice = CloudChatPolicy.Notice }));
        try { await read; throw new Exception("Previous session response escaped after logout."); }
        catch (OperationCanceledException) { }
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!service.Snapshot.IsSignedIn, "Friends cleanup revived a signed-out account.");
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
    private sealed record Fixture(string BaseUrl, Account A, Account B, Account C, string OfficialId);
    private sealed record LegacyFixture(string BaseUrl, string ActivationKey);

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
