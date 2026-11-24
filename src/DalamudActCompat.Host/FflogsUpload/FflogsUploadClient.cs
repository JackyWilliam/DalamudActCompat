// Ported from ACTLogsUploader (MIT, see THIRD_PARTY_NOTICES.md), whose sources are not
// nullable-annotated. The request flow is kept the way upstream tested it against the live
// service, which also keeps later upstream fixes easy to carry over.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace DalamudActCompat.Host.FflogsUpload
{
    public sealed class FightData
    {
        public string Name = string.Empty;
        public long StartTime;
        public long EndTime;
        public string EventsString = string.Empty;
        public int EventCount;
        public int LogVersion = 72;
        public int GameVersion = 1;
    }

    public sealed class GuildInfo
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
    }

    // FF Logs desktop-client transport: login, session cookie + XSRF, create-report,
    // ZIP master-table + segment uploads, terminate.
    public sealed class FflogsUploadClient : IDisposable
    {
        // The server gates report creation on client version, rejecting old ones with a 400 that
        // points users to the Archon App. Match the upload-only Archon App Lite release.
        private const string CLIENT_VERSION = "9.5.0";
        private const int MaxRetries = 3;

        // Ending a report is cleanup: it must still go out after the caller cancelled, but it
        // may not hold the helper forever when the site stopped answering.
        private static readonly TimeSpan TerminateTimeout = TimeSpan.FromSeconds(30);

        private readonly string _baseUrl;
        private readonly string _cacheDirectory;
        private readonly CookieContainer _cookies;
        private readonly ParserEngine _parser;
        private ParserEngine _liveParser;

        public HttpClient HttpClient { get; }
        public bool IsLoggedIn { get; private set; }
        public string CurrentReportCode { get; private set; }
        public string Username { get; private set; }
        public List<GuildInfo> Guilds { get; } = new List<GuildInfo>();

        // What the site answered to a refused sign-in. Shown to the player, never logged.
        public string LastLoginError { get; private set; }

        // Fights in the last whole-log upload; the interface shows it next to the report.
        public int LastUploadFightCount { get; private set; }

        // The status request reads these from another thread while the live task writes them.
        private volatile bool _isLiveLogging;
        private int _liveFightCount;
        private volatile string _liveReportCode;
        private volatile string _liveError;

        public bool IsLiveLogging => _isLiveLogging;
        public int LiveFightCount => Volatile.Read(ref _liveFightCount);
        public string LiveReportCode => _liveReportCode;
        public string LiveError => _liveError;
        private CancellationTokenSource _liveLogCts;
        private Task _liveLogTask;

        public FflogsUploadClient(string baseUrl, string cacheDirectory)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _cacheDirectory = cacheDirectory;
            _cookies = new CookieContainer();
            var inner = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            var xsrf = new XsrfDelegatingHandler(_cookies, new Uri(_baseUrl), inner);
            HttpClient = new HttpClient(xsrf) { BaseAddress = new Uri(_baseUrl) };

            HttpClient.DefaultRequestHeaders.Add("User-Agent",
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) FFLogsUploader/{CLIENT_VERSION} Chrome/138.0.7204.251 Electron/37.7.0 Safari/537.36");
            HttpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
            HttpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\"");
            HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
            HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
            HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
            HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");

            _parser = new ParserEngine(HttpClient, _baseUrl, _cacheDirectory);
        }

        public async Task<bool> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            LastLoginError = null;
            try
            {
                UploadLog.Info("[Login] Starting login...");
                var payload = new Dictionary<string, string>
                {
                    ["email"] = email,
                    ["password"] = password,
                    ["version"] = CLIENT_VERSION,
                    ["clientTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await HttpClient.PostAsync("/desktop-client/log-in", content, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    // The body can repeat the address that was typed, so only the status is logged.
                    UploadLog.Error($"[Login] Failed: HTTP {(int)response.StatusCode}");
                    LastLoginError = $"HTTP {(int)response.StatusCode}: {Trunc(responseBody, 300)}";
                    return false;
                }

                try
                {
                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var user = doc.RootElement.GetProperty("user");
                        Username = user.GetProperty("userName").GetString();
                        Guilds.Clear();
                        if (user.TryGetProperty("guilds", out var guildsArray))
                        {
                            foreach (var guild in guildsArray.EnumerateArray())
                            {
                                Guilds.Add(new GuildInfo
                                {
                                    Id = guild.GetProperty("id").GetInt32().ToString(),
                                    Name = guild.TryGetProperty("name", out var gn) ? gn.GetString() ?? "Unknown" : "Unknown",
                                });
                            }
                        }
                        UploadLog.Info($"[Login] Logged in, {Guilds.Count} guild(s)");
                    }
                }
                catch (Exception ex)
                {
                    UploadLog.Warn($"[Login] Could not parse user data: {ex.Message}");
                }

                var tokenResponse = await HttpClient.PostAsync("/desktop-client/token", null, cancellationToken).ConfigureAwait(false);
                if (tokenResponse.StatusCode != HttpStatusCode.OK)
                    UploadLog.Warn("[Login] Token refresh failed");

                IsLoggedIn = true;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                UploadLog.Error("[Login] Login failed", ex);
                LastLoginError = ex.GetBaseException().Message;
                return false;
            }
        }

        public async Task<string> CreateReportAsync(string filename, string description, int visibility,
            int serverOrRegion, string guildId, int parserVersion, int logMode,
            CancellationToken cancellationToken = default)
        {
            if (parserVersion <= 0)
                throw new Exception("Parser version is unavailable.");

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var payload = new Dictionary<string, object>
            {
                ["clientVersion"] = CLIENT_VERSION,
                ["parserVersion"] = parserVersion,
                ["startTime"] = ts,
                ["endTime"] = ts,
                ["guildId"] = string.IsNullOrEmpty(guildId) ? null : (object)int.Parse(guildId),
                ["fileName"] = Path.GetFileName(filename),
                ["serverOrRegion"] = serverOrRegion,
                ["visibility"] = visibility,
                ["reportTagId"] = null,
                ["description"] = description,
                ["logMode"] = logMode,
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync("/desktop-client/create-report", content, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Create report failed: {(int)response.StatusCode} - {Trunc(responseBody, 300)}");

            try
            {
                using (var doc = JsonDocument.Parse(responseBody))
                    CurrentReportCode = doc.RootElement.GetProperty("code").GetString();
            }
            catch
            {
                CurrentReportCode = responseBody.Trim().Trim('"');
            }

            UploadLog.Info($"[CreateReport] {CurrentReportCode}");
            return CurrentReportCode ?? throw new Exception("No report code in response");
        }

        // Parse a log into its fight list without creating a report (for the fight picker).
        public Task<List<ParserEngine.FightUpload>> PrepareUploadsAsync(string logPath, string regionCode,
            CancellationToken cancellationToken = default)
            => _parser.ProcessLogAsync(logPath, "0000000000", regionCode, cancellationToken);

        // Create a report, upload the given fights, terminate. Segment ids are renumbered 1..N.
        public async Task<string> UploadPreparedAsync(string fileName, IList<ParserEngine.FightUpload> uploads,
            int serverOrRegion, int visibility, string guildId, string description,
            CancellationToken cancellationToken = default)
        {
            if (uploads == null || uploads.Count == 0)
                throw new Exception("No fights to upload.");

            var reportCode = await CreateReportAsync(fileName, description, visibility, serverOrRegion,
                guildId, _parser.ParserVersion, 2, cancellationToken).ConfigureAwait(false);
            try
            {
                for (int i = 0; i < uploads.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int segmentId = i + 1;
                    var u = uploads[i];
                    await WithRetryAsync(() => UploadMasterTableAsync(reportCode, u.MasterTable, segmentId, cancellationToken), cancellationToken).ConfigureAwait(false);
                    await WithRetryAsync(() => UploadSegmentAsync(reportCode, u.Fight, segmentId, u.FightStartTime, u.FightEndTime, false, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // A report that was created but never ended stays "in progress" on the
                // player's account, so it is closed on failure and cancellation as well.
                await TerminateReportAsync(reportCode).ConfigureAwait(false);
            }
            UploadLog.Info($"Upload complete: {reportCode}");
            return reportCode;
        }

        public async Task<string> UploadLogAsync(string logPath, int serverOrRegion, string regionCode, int visibility, string guildId, string description,
            CancellationToken cancellationToken = default)
        {
            var fileName = Path.GetFileName(logPath);
            ParserEngine.IncrementalUploadResult result = null;
            string reportCode = null;
            try
            {
                result = await _parser.ProcessAndUploadLogAsync(
                    logPath,
                    regionCode,
                    async parserVersion =>
                    {
                        reportCode = await CreateReportAsync(
                            fileName,
                            description,
                            visibility,
                            serverOrRegion,
                            guildId,
                            parserVersion,
                            2,
                            cancellationToken).ConfigureAwait(false);
                        return reportCode;
                    },
                    async (masterTable, fight, segmentId, startTime, endTime) =>
                    {
                        await WithRetryAsync(() => UploadMasterTableAsync(
                            reportCode, masterTable, segmentId, cancellationToken), cancellationToken).ConfigureAwait(false);
                        await WithRetryAsync(() => UploadSegmentAsync(
                            reportCode, fight, segmentId, startTime, endTime, false, cancellationToken), cancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!string.IsNullOrEmpty(reportCode))
                    await TerminateReportAsync(reportCode).ConfigureAwait(false);
            }

            if (result == null)
                throw new Exception("Upload did not produce a result.");
            if (result.FightCount == 0)
                throw new Exception("Parser produced no fights - nothing to upload.");
            LastUploadFightCount = result.FightCount;

            UploadLog.Info($"Upload complete: {result.ReportCode}");
            return result.ReportCode;
        }

        public void StartLiveLog(string logDirectory, int serverOrRegion, string regionCode, int visibility, string guildId, string description, bool uploadPreviousFights)
        {
            if (IsLiveLogging) { UploadLog.Warn("Live logging already in progress"); return; }
            var liveLogCts = new CancellationTokenSource();
            _liveLogCts = liveLogCts;
            _isLiveLogging = true;
            Volatile.Write(ref _liveFightCount, 0);
            _liveReportCode = null;
            _liveError = null;
            var liveParser = new ParserEngine(HttpClient, _baseUrl, _cacheDirectory);
            _liveParser = liveParser;

            _liveLogTask = Task.Run(async () =>
            {
                string reportCode = null;
                try
                {
                    await liveParser.StartLiveLogAsync(logDirectory, regionCode, uploadPreviousFights,
                        async (masterData, fight, segmentId, startTime, endTime) =>
                        {
                            if (reportCode == null)
                            {
                                reportCode = await CreateReportAsync("live.log", description, visibility,
                                    serverOrRegion, guildId, liveParser.ParserVersion, 1, liveLogCts.Token).ConfigureAwait(false);
                                _liveReportCode = reportCode;
                                liveParser.SetReportCode(reportCode);
                            }
                            await WithRetryAsync(() => UploadMasterTableAsync(reportCode, masterData, segmentId, liveLogCts.Token), liveLogCts.Token).ConfigureAwait(false);
                            await WithRetryAsync(() => UploadSegmentAsync(reportCode, fight, segmentId, startTime, endTime, true, liveLogCts.Token), liveLogCts.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref _liveFightCount);
                        }, liveLogCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { UploadLog.Info("[LiveLog] Cancelled"); }
                catch (Exception ex)
                {
                    UploadLog.Error("[LiveLog] Error", ex);
                    // Live upload runs unattended; without this the interface would only show
                    // that it stopped.
                    _liveError = ex.GetBaseException().Message;
                }
                finally
                {
                    liveParser.Dispose();
                    if (ReferenceEquals(_liveParser, liveParser))
                        _liveParser = null;
                    if (reportCode != null)
                    {
                        await TerminateReportAsync(reportCode).ConfigureAwait(false);
                        UploadLog.Info($"[LiveLog] Ended. Report: {reportCode}");
                    }
                    _isLiveLogging = false;
                }
            });
        }

        public void StopLiveLog()
        {
            if (!IsLiveLogging) return;
            _liveLogCts?.Cancel();
        }

        public async Task WaitForLiveLogAsync(int timeoutMs = 5000)
        {
            if (_liveLogTask == null) return;
            _liveLogCts?.Cancel();
            try
            {
                var completed = await Task.WhenAny(_liveLogTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != _liveLogTask)
                    UploadLog.Warn("[LiveLog] Task did not finish within timeout");
            }
            catch { }
        }

        private async Task WithRetryAsync(Func<Task> action, CancellationToken cancellationToken, int maxRetries = MaxRetries)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try { await action().ConfigureAwait(false); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    UploadLog.Warn($"[Retry] {attempt + 1}/{maxRetries} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static string GenerateWebKitBoundary()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            var suffix = new char[16];
            for (int i = 0; i < suffix.Length; i++) suffix[i] = chars[random.Next(chars.Length)];
            return $"----WebKitFormBoundary{new string(suffix)}";
        }

        private static HttpContent CreateStringPart(string name, string value)
        {
            var content = new StringContent(value);
            content.Headers.ContentType = null;
            content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
            return content;
        }

        private static HttpContent CreateFilePart(string name, string filename, byte[] data)
        {
            var content = new ByteArrayContent(data);
            content.Headers.Clear();
            content.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"; filename=\"{filename}\"");
            content.Headers.TryAddWithoutValidation("Content-Type", "application/zip");
            return content;
        }

        private static byte[] ZipLogTxt(string contents)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("log.txt");
                    using (var es = entry.Open())
                    using (var writer = new StreamWriter(es))
                        writer.Write(contents);
                }
                return ms.ToArray();
            }
        }

        private async Task UploadMasterTableAsync(string reportCode, string masterTableContent, int segmentId,
            CancellationToken cancellationToken)
        {
            var zipBytes = ZipLogTxt(masterTableContent);
            var boundary = GenerateWebKitBoundary();
            using (var content = new MultipartFormDataContent(boundary))
            {
                // The server expects an unquoted boundary (matching the Electron client).
                content.Headers.Remove("Content-Type");
                content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
                content.Add(CreateStringPart("segmentId", segmentId.ToString()));
                content.Add(CreateStringPart("isRealTime", "false"));
                content.Add(CreateFilePart("logfile", "blob", zipBytes));

                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/set-report-master-table/{reportCode}") { Content = content })
                {
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        private async Task UploadSegmentAsync(string reportCode, FightData fight, int segmentId, long startTime, long endTime, bool isLive,
            CancellationToken cancellationToken)
        {
            var eventsContent = $"{fight.LogVersion}|{fight.GameVersion}\n{fight.EventCount}\n{fight.EventsString}";
            var zipBytes = ZipLogTxt(eventsContent);

            var parameters = JsonSerializer.Serialize(new
            {
                startTime,
                endTime,
                mythic = 0,
                isLiveLog = isLive,
                isRealTime = false,
                inProgressEventCount = 0,
                segmentId,
            });

            var boundary = GenerateWebKitBoundary();
            using (var formContent = new MultipartFormDataContent(boundary))
            {
                formContent.Headers.Remove("Content-Type");
                formContent.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
                formContent.Add(CreateFilePart("logfile", "blob", zipBytes));
                formContent.Add(CreateStringPart("parameters", parameters));

                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/add-report-segment/{reportCode}") { Content = formContent })
                {
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        private async Task TerminateReportAsync(string reportCode)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TerminateTimeout))
                {
                    await WithRetryAsync(async () =>
                    {
                        var response = await HttpClient.PostAsync($"/desktop-client/terminate-report/{reportCode}", null, timeout.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"Terminate failed with status {(int)response.StatusCode}");
                    }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                UploadLog.Warn($"[TerminateReport] Failed for {reportCode}: {ex.Message}");
            }
        }

        private static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n));

        public void Dispose()
        {
            try
            {
                _liveLogCts?.Cancel();
                _liveLogTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
            _liveParser = null;
            try { _parser?.Dispose(); } catch { }
            try { HttpClient?.Dispose(); } catch { }
        }
    }
}


