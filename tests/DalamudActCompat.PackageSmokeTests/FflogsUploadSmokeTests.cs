using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudActCompat.Fflogs.Upload;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Protocol;

// FFLogs log upload through the --fflogs-upload Host process, against a loopback stand-in for
// fflogs.com that serves a small stand-in parser. No real account, log or FF Logs server is
// involved. What it covers: the pinned V8 packages match what the build restored and install
// through the normal resource-pack path; the real V8 engine runs the parser message flow in the
// helper, not in this process; sign-in with the XSRF cookie; upload settings reach the site;
// whole-log, selected-fight and single-pull uploads send the right fights and always end their
// report; cancelling stops the upstream call without ending the session; live upload starts and
// stops; and the helper follows its owner's exit.
internal static class FflogsUploadSmokeTests
{
    private static readonly FflogsUploadOptions Options = new(Visibility: 2, Region: 3, GuildId: "42", Description: "night one");

    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "fflogs-upload");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(PackagedHostDirectory(), "DalamudActCompat.Host.exe");
        Assert(File.Exists(executable), $"Packaged Host executable is missing: {executable}");
        var engine = await ResolvePinnedEngineAsync(root);
        await using var site = FakeSite.Start();
        var log = SyntheticLog.Write(Path.Combine(root, "logs"));

        var service = new FflogsUploadService(
            Logger(),
            () => executable,
            Path.Combine(root, "fflogs-upload.dat"),
            Path.Combine(root, "parser-cache"),
            (_, _) => Task.FromResult(engine),
            site.Base);
        var helper = 0;
        try
        {
            await service.LogoutAsync();
            await service.StopLiveAsync();
            Assert(service.HelperProcessId == 0, "Signing out or stopping with no session started a helper process.");

            var refused = await FailureAsync(() => service.LoginAsync("player@example.com", "wrong", chineseSite: false));
            Assert(refused.Code == FflogsUploadProtocol.LoginFailed && refused.Message.Contains("credentials", StringComparison.Ordinal) &&
                   !service.IsLoggedIn,
                $"A refused sign-in was reported as {refused.Code}: {refused.Message}");
            helper = service.HelperProcessId;
            Assert(helper > 0 && helper != Environment.ProcessId, "FFLogs requests did not start a separate helper process.");
            var early = await FailureAsync(() => service.UploadLatestAsync(log.Directory, Options));
            Assert(early.Code == FflogsUploadProtocol.NotLoggedIn, $"Uploading while signed out failed as {early.Code}.");

            await service.LoginAsync("player@example.com", "correct", chineseSite: false);
            var status = service.Status;
            Assert(status.LoggedIn && status.UserName == "Tester" && !status.ChineseSite &&
                   status.Guilds.Single() is { Id: "42", Name: "Static" },
                "The signed-in account or its guild list did not reach the game side.");
            service.SavePassword("correct");
            Assert(service.HasSavedPassword && service.LoadSavedPassword() == "correct" &&
                   !File.ReadAllText(Path.Combine(root, "fflogs-upload.dat")).Contains("correct", StringComparison.Ordinal),
                "The remembered password did not round-trip or was written in clear text.");
            service.ClearSavedPassword();
            Assert(!service.HasSavedPassword, "Clearing the remembered password left the file behind.");

            var wholeResult = await service.UploadLatestAsync(log.Directory, Options);
            var whole = site.Report(wholeResult.ReportCode);
            Assert(wholeResult is { FightCount: 2, ChineseSite: false, Visibility: 2 } && wholeResult.Url == "https://www.fflogs.com/reports/" + wholeResult.ReportCode,
                "The finished upload did not report its fight count, visibility or report address.");
            Assert(whole.Xsrf == "xsrf=token" && whole.Create.GetProperty("visibility").GetInt32() == 2 &&
                   whole.Create.GetProperty("serverOrRegion").GetInt32() == 3 &&
                   whole.Create.GetProperty("guildId").GetInt32() == 42 &&
                   whole.Create.GetProperty("description").GetString() == "night one" &&
                   whole.Create.GetProperty("parserVersion").GetInt32() == 77,
                "Uploading the latest log ignored the upload settings, the XSRF cookie or the parser version.");
            Assert(whole.MasterTables == 2 && whole.Segments.Count == 2 && whole.Terminated &&
                   whole.Segments[0].Contains("hit-1a", StringComparison.Ordinal) && whole.Segments[1].Contains("hit-2a", StringComparison.Ordinal),
                "The whole-log upload did not send both fights in order and end the report.");
            Assert(Directory.GetFiles(Path.Combine(root, "parser-cache"), "*.js").Length == 1,
                "The parser was not cached in the directory the game side chose.");

            // Found with a real account: a log without a fight (a fresh login) created a report
            // first and only then noticed there was nothing to put in it.
            var quiet = SyntheticLog.WriteWithoutFights(Path.Combine(root, "quiet-logs"));
            var created = site.Requests.Count(request => request.Path == "/desktop-client/create-report");
            var nothing = await FailureAsync(() => service.UploadLatestAsync(quiet, Options));
            Assert(nothing.Code == FflogsUploadProtocol.NoFights &&
                   site.Requests.Count(request => request.Path == "/desktop-client/create-report") == created,
                $"Uploading a log without fights failed as {nothing.Code} or created a report anyway.");

            var fights = await service.PrepareAsync(log.Directory);
            Assert(fights.Count == 2 && fights[0] is { Index: 0, Name: "Fight One" } && fights[1] is { Index: 1, Name: "Fight Two" } &&
                   fights[0].EndUnixMs == log.FirstEnd.ToUnixTimeMilliseconds() && fights[1].EndUnixMs == log.SecondEnd.ToUnixTimeMilliseconds(),
                "Listing the fights of the latest log returned the wrong names or end times.");
            var pickedResult = await service.UploadPreparedAsync([1], Options);
            var picked = site.Report(pickedResult.ReportCode);
            Assert(picked.Segments.Count == 1 && picked.Segments[0].Contains("hit-2a", StringComparison.Ordinal) &&
                   !picked.Segments[0].Contains("hit-1a", StringComparison.Ordinal) && picked.Terminated && pickedResult.FightCount == 1,
                "Uploading one selected fight sent a different fight or left the report open.");

            // DACT closes its own record a few seconds after the last event of the pull.
            var single = site.Report((await service.UploadEncounterAsync(
                log.Directory, log.SecondStart, log.SecondEnd.AddSeconds(8), Options)).ReportCode);
            Assert(single.Segments.Count == 1 && single.Segments[0].Contains("hit-2a", StringComparison.Ordinal) && single.Terminated,
                "The history upload did not pick the pull whose end matches the record.");
            var first = site.Report((await service.UploadEncounterAsync(
                log.Directory, log.FirstStart, log.FirstEnd.AddSeconds(3), Options)).ReportCode);
            Assert(first.Segments.Count == 1 && first.Segments[0].Contains("hit-1a", StringComparison.Ordinal),
                "The history upload sent the wrong pull for the first record.");
            var between = await FailureAsync(() => service.UploadEncounterAsync(
                log.Directory, log.FirstEnd.AddMinutes(3), log.FirstEnd.AddMinutes(4), Options));
            var later = await FailureAsync(() => service.UploadEncounterAsync(
                log.Directory, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2), Options));
            Assert(between.Code == FflogsUploadProtocol.NoMatch && later.Code == FflogsUploadProtocol.NoMatch,
                "A record with no pull in the saved logs was uploaded as some other pull.");

            await CancellationAsync(service, site, log);

            await service.StartLiveAsync(log.Directory, Options);
            Assert(service.IsLiveLogging, "Live upload did not report itself as running.");
            await service.StopLiveAsync();
            Assert(await UntilAsync(service, current => !current.LiveActive), "Live upload kept running after it was stopped.");
            Assert(service.Status is { LoggedIn: true, LiveError: null }, "Stopping live upload ended the session or was reported as an error.");

            await service.LogoutAsync();
            Assert(!service.IsLoggedIn && service.HelperProcessId == helper, "Signing out did not end the session inside the same helper.");
        }
        finally
        {
            service.Dispose();
        }

        Assert(await ExitedAsync(helper), "The FFLogs upload helper outlived its owner's Dispose.");
        Assert(site.Requests.All(request => request.UserAgent.Contains("FFLogsUploader/", StringComparison.Ordinal)),
            "A request reached the site without the desktop-client identification it requires.");
        await ParentExitAsync(executable, Path.Combine(root, "parent-cache"));
        Assert(await ExitCodeAsync(executable, Path.Combine(root, "refused-cache"), "https://example.com") == 2,
            "The helper accepted a non-loopback stand-in site.");
        Console.WriteLine("FFLogs upload: pinned V8 packages, helper process, sign-in, upload settings, whole-log/selected/single-pull uploads, cancellation, live start/stop, sign-out, owner and parent exit passed.");
    }

    // Installs the two pinned NuGet packages through ResourcePackManager exactly as a player's
    // first parse would, with the archives the build restored standing in for the download.
    private static async Task<FflogsUploadEngine> ResolvePinnedEngineAsync(string root)
    {
        var packages = Path.Combine(AppContext.BaseDirectory, "FflogsEngine");
        using var http = new HttpClient(new LocalPackages(packages));
        using var packs = new ResourcePackManager(Path.Combine(root, "plugin"), Path.Combine(root, "packs"), Logger(), http);
        var last = -1;
        var engine = await FflogsUploadEngineFiles.ResolveAsync(packs, CancellationToken.None, percent => last = percent);
        Assert(File.Exists(Path.Combine(engine.NativeDirectory, "ClearScriptV8.win-x64.dll")) && File.Exists(engine.IcuDataAssembly),
            "The pinned packages did not contain the V8 library and ICU data where the helper looks for them.");
        Assert(last == 100, $"Engine download progress ended at {last}%.");
        var again = await FflogsUploadEngineFiles.ResolveAsync(
            packs, CancellationToken.None, _ => Assert(false, "A verified engine cache was downloaded again."));
        Assert(again == engine, "The second resolve returned different engine paths.");
        return engine;
    }

    private static async Task CancellationAsync(FflogsUploadService service, FakeSite site, SyntheticLog log)
    {
        using var cancel = new CancellationTokenSource();
        var upload = service.UploadLatestAsync(log.Directory, Options with { Description = FakeSite.SlowDescription }, cancel.Token);
        await site.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();
        cancel.Cancel();
        try
        {
            await upload;
            Assert(false, "An upload to a site that never answers completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert(clock.Elapsed < TimeSpan.FromSeconds(5), $"Cancelling took {clock.Elapsed.TotalSeconds:0.0} s.");
        // The helper must drop the upstream call too, or an abandoned upload would keep running.
        await site.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fights = await service.PrepareAsync(log.Directory);
        Assert(fights.Count == 2 && service.IsLoggedIn, "Cancelling an upload ended the session or left the helper unusable.");
        Assert(await LatestLogAsync(service, log.Directory) is { Name: "Network_fixture.log" },
            "The newest log of the directory was not found for display.");
    }

    // The first look starts a background read of the directory and answers null until it is done.
    private static async Task<FflogsLatestLog?> LatestLogAsync(FflogsUploadService service, string directory)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (service.LatestLog(directory) is { } latest) return latest;
            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<bool> UntilAsync(FflogsUploadService service, Func<FflogsUploadStatus, bool> reached)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (reached(service.Status)) return true;
            service.Tick();
            await Task.Delay(200);
        }

        return reached(service.Status);
    }

    private static async Task ParentExitAsync(string executable, string cacheRoot)
    {
        // A stand-in for the game: when it exits, the helper must not linger.
        using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        using var child = StartHelper(executable, cacheRoot, parent.Id, null);
        await Task.Delay(500);
        Assert(!child.HasExited, "The FFLogs upload helper exited while its parent was alive.");
        parent.Kill();
        Assert(await ExitedAsync(child.Id), "The FFLogs upload helper kept running after its parent exited.");
    }

    private static async Task<int> ExitCodeAsync(string executable, string cacheRoot, string site)
    {
        using var child = StartHelper(executable, cacheRoot, Environment.ProcessId, site);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await child.WaitForExitAsync(timeout.Token);
        return child.ExitCode;
    }

    private static Process StartHelper(string executable, string cacheRoot, int parent, string? site)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var arguments = new List<string>
        {
            FflogsUploadProtocol.ModeArgument,
            FflogsUploadProtocol.PipeArgument, "DACT-fflogs-test-" + Guid.NewGuid().ToString("N"),
            FflogsUploadProtocol.ParentArgument, parent.ToString(),
            FflogsUploadProtocol.CacheArgument, cacheRoot,
        };
        if (site is not null) arguments.AddRange([FflogsUploadProtocol.LoopbackSiteArgument, site]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var child = Process.Start(start)!;
        _ = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        return child;
    }

    private static async Task<FflogsUploadException> FailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (FflogsUploadException ex)
        {
            return ex;
        }

        Assert(false, "An FFLogs upload operation that must fail succeeded.");
        throw new InvalidOperationException();
    }

    private static async Task<bool> ExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static string PackagedHostDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DalamudActCompat.slnx"))) repository = repository.Parent;
        Assert(repository is not null, "Repository was not found.");
        return Path.Combine(repository!.FullName, "src", "DalamudActCompat", "bin", "Release", "host");
    }

    private static PluginLogger Logger() => new(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Answers the pinned package URLs (and their mirrors) from the restored archives.
    private sealed class LocalPackages(string directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Path.Combine(directory, Path.GetFileName(request.RequestUri!.AbsolutePath));
            Assert(File.Exists(path), $"The build did not provide {Path.GetFileName(path)} for the pinned-package check.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) });
        }
    }

    // Two pulls in the line format the stand-in parser understands. The times are recent because
    // the single-pull upload only opens logs written after the pull began.
    private sealed record SyntheticLog(
        string Directory,
        DateTimeOffset FirstStart,
        DateTimeOffset FirstEnd,
        DateTimeOffset SecondStart,
        DateTimeOffset SecondEnd)
    {
        public static SyntheticLog Write(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            var origin = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(-30);
            var log = new SyntheticLog(directory, origin.AddSeconds(60), origin.AddSeconds(180), origin.AddSeconds(600), origin.AddSeconds(750));
            var lines = new List<string> { Line("253", origin, "FFXIV_ACT_Plugin Version: fixture") };
            void Fight(string name, string tag, DateTimeOffset start, DateTimeOffset end)
            {
                lines.Add(Line("FS", start, name));
                for (var hit = 1; hit <= 3; hit++) lines.Add(Line("EV", start.AddSeconds(hit), $"hit-{tag}{(char)('a' + hit - 1)}"));
                lines.Add(Line("FE", end, string.Empty));
            }

            Fight("Fight One", "1", log.FirstStart, log.FirstEnd);
            Fight("Fight Two", "2", log.SecondStart, log.SecondEnd);
            File.WriteAllText(Path.Combine(directory, "Network_fixture.log"), string.Join("\n", lines) + "\n");
            return log;
        }

        // Lines the parser reads but that never start a fight; returns the directory.
        public static string WriteWithoutFights(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            var origin = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(-5);
            File.WriteAllText(Path.Combine(directory, "Network_quiet.log"), string.Join("\n",
                Line("253", origin, "FFXIV_ACT_Plugin Version: fixture"),
                Line("EV", origin.AddSeconds(5), "zone-in"),
                Line("EV", origin.AddSeconds(9), "chat")) + "\n");
            return directory;
        }

        private static string Line(string type, DateTimeOffset time, string text)
            => $"{type}|{time.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}+00:00|{text}|";
    }

    internal sealed record SiteRequest(string Method, string Path, string UserAgent);

    internal sealed class SiteReport
    {
        public JsonElement Create;
        public string Xsrf = string.Empty;
        public int MasterTables;
        public List<string> Segments { get; } = [];
        public bool Terminated;
    }

    // A tiny HTTP/1.1 server on 127.0.0.1 that answers like the FF Logs desktop-client endpoints
    // and serves a parser that implements the same message flow for the synthetic log.
    internal sealed class FakeSite : IAsyncDisposable
    {
        public const string SlowDescription = "never answered";

        // FS starts a fight, EV is an event and FE ends it. Event times are relative to the
        // log's start date, as in the real upload format.
        private const string Parser = """
            (function () {
              var startDate = 0, finished = [], scanned = [], current = null, next = 0;
              function relative(line) { return Date.parse(line.split('|')[1]) - startDate; }
              window.addEventListener('message', function (e) {
                var m = e.data;
                function answer(channel, data) { e.source.postMessage({ message: channel, id: m.id, data: data }); }
                switch (m.message) {
                  case 'get-parser-version': window.sendToHost('get-parser-version-completed', 0, null, 77); break;
                  case 'set-start-date': startDate = m.startDate; break;
                  case 'clear-state': finished = []; scanned = []; current = null; next = 0; break;
                  case 'clear-fights': finished = []; break;
                  case 'parse-lines':
                    var wanted = m.raidsToUpload || [];
                    for (var i = 0; i < m.lines.length; i++) {
                      var fields = m.lines[i].split('|');
                      if (fields[0] === 'FS') current = { index: next++, name: fields[2], events: [] };
                      else if (fields[0] === 'EV' && current) current.events.push(relative(m.lines[i]) + '|' + fields[2]);
                      else if (fields[0] === 'FE' && current) {
                        current.events.push(relative(m.lines[i]) + '|end');
                        scanned.push({ index: current.index, name: current.name });
                        var index = current.index;
                        var selected = wanted.length === 0 || wanted.some(function (raid) { return raid.index === index; });
                        if (!m.scanning && selected) finished.push(current);
                        current = null;
                      }
                    }
                    break;
                  case 'collect-scanned-raids': answer('collect-scanned-raids-completed', scanned); break;
                  case 'collect-fights':
                    answer('collect-fights-completed', {
                      startTime: startDate, logVersion: 72, gameVersion: 1, logFileDetails: 'fixture',
                      fights: finished.map(function (fight) {
                        return { name: fight.name, startTime: 0, endTime: 0, eventCount: fight.events.length, eventsString: fight.events.join('\n') + '\n' };
                      })
                    });
                    break;
                  case 'collect-master-info':
                    answer('collect-master-info-completed', {
                      lastAssignedActorID: 1, actorsString: 'actor\n', lastAssignedAbilityID: 0, abilitiesString: '',
                      lastAssignedTupleID: 0, tuplesString: '', lastAssignedPetID: 0, petsString: ''
                    });
                    break;
                }
              });
            })();
            """;

        private const string ParserPage =
            "<html><head><script src=\"/assets/parser-ff-fixture.js\"></script></head><body>" +
            "<script type=\"text/javascript\">/* ipcCollectFights */ var fixtureGlue = true;</script></body></html>";

        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<string, SiteReport> reports = new();
        private int reportNumber;
        private Task? loop;

        public string Base => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        public ConcurrentQueue<SiteRequest> Requests { get; } = new();

        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SlowAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SiteReport Report(string code)
            => reports.TryGetValue(code, out var report) ? report : throw new InvalidOperationException($"The site never created report {code}.");

        public static FakeSite Start()
        {
            var site = new FakeSite();
            site.listener.Start();
            site.loop = site.AcceptAsync();
            return site;
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var path = requestLine[1].Split('?')[0];
            var headers = lines.Skip(1).Where(line => line.Contains(':'))
                .ToDictionary(line => line[..line.IndexOf(':')].Trim(), line => line[(line.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            var body = headers.TryGetValue("Content-Length", out var length) ? new byte[int.Parse(length)] : [];
            await stream.ReadExactlyAsync(body);
            Requests.Enqueue(new SiteRequest(requestLine[0], path, headers.GetValueOrDefault("User-Agent") ?? string.Empty));

            var status = 200;
            var type = "application/json";
            var extra = string.Empty;
            var payload = "{}";
            var code = path[(path.LastIndexOf('/') + 1)..];
            switch (path)
            {
                case "/desktop-client/log-in":
                    if (JsonDocument.Parse(body).RootElement.GetProperty("password").GetString() == "correct")
                    {
                        // The real cookie value is URL-encoded; the client must decode it for the header.
                        extra = "Set-Cookie: XSRF-TOKEN=xsrf%3Dtoken; Path=/\r\nSet-Cookie: fflogs_session=fixture; Path=/\r\n";
                        payload = """{"user":{"userName":"Tester","guilds":[{"id":42,"name":"Static"}]}}""";
                    }
                    else
                    {
                        status = 422;
                        payload = """{"message":"These credentials do not match our records."}""";
                    }

                    break;
                case "/desktop-client/parser":
                    type = "text/html";
                    payload = ParserPage;
                    break;
                case "/assets/parser-ff-fixture.js":
                    type = "application/javascript";
                    // The client rejects bundles under 10000 characters as error pages.
                    payload = Parser + "\n/*" + new string('x', 11000) + "*/";
                    break;
                case "/desktop-client/create-report":
                    var create = JsonDocument.Parse(body).RootElement.Clone();
                    if (create.GetProperty("description").GetString() == SlowDescription)
                    {
                        // Never answer; report when the caller (the helper) gives up on the connection.
                        SlowStarted.TrySetResult();
                        try { while (await stream.ReadAsync(new byte[1]) > 0) { } }
                        catch (IOException) { }
                        SlowAborted.TrySetResult();
                        return;
                    }

                    code = "fixture" + Interlocked.Increment(ref reportNumber);
                    reports[code] = new SiteReport { Create = create, Xsrf = headers.GetValueOrDefault("X-XSRF-TOKEN") ?? string.Empty };
                    payload = JsonSerializer.Serialize(new { code });
                    break;
                case var _ when path.StartsWith("/desktop-client/set-report-master-table/", StringComparison.Ordinal):
                    lock (Report(code)) Report(code).MasterTables++;
                    break;
                case var _ when path.StartsWith("/desktop-client/add-report-segment/", StringComparison.Ordinal):
                    lock (Report(code)) Report(code).Segments.Add(ZippedLog(body));
                    break;
                case var _ when path.StartsWith("/desktop-client/terminate-report/", StringComparison.Ordinal):
                    Report(code).Terminated = true;
                    break;
                case "/desktop-client/token":
                    break;
                default:
                    status = 404;
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            var reason = status switch { 200 => "OK", 422 => "Unprocessable Entity", _ => "Not Found" };
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n{extra}Connection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }

        // The multipart body carries the segment as a ZIP with a single log.txt.
        private static string ZippedLog(byte[] body)
        {
            var start = body.AsSpan().IndexOf("PK\u0003\u0004"u8);
            Assert(start >= 0, "A segment upload carried no ZIP.");
            using var archive = new ZipArchive(new MemoryStream(body, start, body.Length - start), ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("log.txt")!.Open());
            return reader.ReadToEnd();
        }

        private static async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (buffer.Count < 64 * 1024)
            {
                if (await stream.ReadAsync(one) == 0) return null;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                    return Encoding.ASCII.GetString(buffer.ToArray(), 0, buffer.Count - 4);
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            if (loop is not null) await loop;
            stop.Dispose();
        }
    }
}
