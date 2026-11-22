using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.ClearScript.V8;

namespace DalamudActCompat.Fflogs.Upload
{
    // Runs FF Logs' own JS parser (downloaded from /desktop-client/parser) in an embedded
    // V8 engine to turn raw Network_*.log lines into the event/master-table upload format.
    // All V8 calls are serialized through _engineLock.
    public sealed class ParserEngine : IDisposable
    {
        private const int LiveLogPollIntervalMs = 500;
        private const int FightEndDelayMs = 5000;
        private const int FileCheckIntervalSeconds = 10;

        private const string WipeDirectorCode = "40000011";
        private const string ParserGameContentDetectionEnabled = "true";
        private const string ParserMetersEnabled = "false";
        private const string ParserLiveFightDataEnabled = "false";

        private static readonly HashSet<string> FightEndDirectorCodes = new HashSet<string>
        {
            "40000011", // Wipe cleanup
            "40000002", // Victory / duty complete
            "40000003", // Duty complete (alt)
            "40000005", // Duty complete (alt)
        };

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        private string _parserBundlePath;
        private V8ScriptEngine _engine;
        private readonly List<JsonElement> _ipcMessages = new List<JsonElement>();
        private readonly object _ipcLock = new object();
        private string _currentReportCode;

        private readonly SemaphoreSlim _engineLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public int ParserVersion { get; private set; }

        public ParserEngine(HttpClient http, string baseUrl)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public sealed class FightUpload
        {
            public string MasterTable;
            public FightData Fight;
            public long FightStartTime;
            public long FightEndTime;
            public FightUpload(string masterTable, FightData fight, long start, long end)
            {
                MasterTable = masterTable; Fight = fight; FightStartTime = start; FightEndTime = end;
            }
        }

        public sealed class IncrementalUploadResult
        {
            public string ReportCode { get; }
            public int FightCount { get; }

            public IncrementalUploadResult(string reportCode, int fightCount)
            {
                ReportCode = reportCode;
                FightCount = fightCount;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopParser();
            _engineLock.Dispose();
        }

        private void StopParser()
        {
            if (_engine != null)
            {
                try { _engine.Dispose(); } catch { }
                _engine = null;
            }
            _currentReportCode = null;
        }

        public void SetReportCode(string reportCode)
        {
            _currentReportCode = reportCode;
            SendMessage(new { message = "set-report-code", id = 0, reportCode });
        }

        private string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DalamudActCompat", "FflogsParser");

        private async Task EnsureParserAsync()
        {
            Directory.CreateDirectory(CacheDir);
            // Cache key includes host so the CN and Global parsers don't collide.
            var host = new Uri(_baseUrl).Host.Replace('.', '_');
            _parserBundlePath = Path.Combine(CacheDir, $"fflogs_parser_v8_v3_{host}.js");

            if (File.Exists(_parserBundlePath) &&
                File.GetLastWriteTimeUtc(_parserBundlePath) > DateTime.UtcNow.AddHours(-24))
            {
                var cachedSize = new FileInfo(_parserBundlePath).Length;
                if (cachedSize > 10000)
                {
                    UploadLog.Info($"Using cached parser: {_parserBundlePath}");
                    return;
                }
                UploadLog.Warn($"Cached parser invalid (size={cachedSize}), re-downloading...");
                File.Delete(_parserBundlePath);
            }

            UploadLog.Info("Downloading FF Logs parser...");
            var tempPath = _parserBundlePath + ".tmp";
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var parserPageUrl = $"{_baseUrl}/desktop-client/parser?id=1&ts={ts}" +
                    $"&gameContentDetectionEnabled={ParserGameContentDetectionEnabled}" +
                    $"&metersEnabled={ParserMetersEnabled}&liveFightDataEnabled={ParserLiveFightDataEnabled}";

                var html = await _http.GetStringAsync(parserPageUrl).ConfigureAwait(false);

                var match = Regex.Match(html, "src=\"([^\"]+parser-ff[^\"]+)\"");
                if (!match.Success)
                    throw new Exception("Could not find parser URL in response");

                var parserUrl = match.Groups[1].Value;
                if (parserUrl.StartsWith("/")) parserUrl = _baseUrl + parserUrl;
                UploadLog.Debug($"Found parser URL: {parserUrl}");

                var parserCode = await _http.GetStringAsync(parserUrl).ConfigureAwait(false);

                var scriptBlocks = Regex.Matches(html, "<script type=\"text/javascript\">([\\s\\S]*?)</script>");
                var glueCode = "";
                foreach (Match m in scriptBlocks)
                {
                    if (m.Groups[1].Value.Contains("ipcCollectFights"))
                    {
                        glueCode = m.Groups[1].Value;
                        break;
                    }
                }

                var fullBundle = parserCode + "\n\n" + glueCode;
                if (fullBundle.Length < 10000 || !fullBundle.Contains("ipcCollectFights"))
                    throw new Exception($"Downloaded parser bundle appears invalid (size={fullBundle.Length}). " +
                        "This may be a partial download or an error page.");
                if (fullBundle.Contains("<script") || fullBundle.Contains("</script>"))
                    throw new Exception("Parser bundle contains raw HTML — the parser page structure may have changed.");

                File.WriteAllText(tempPath, fullBundle);
                if (File.Exists(_parserBundlePath)) File.Delete(_parserBundlePath);
                File.Move(tempPath, _parserBundlePath);
                UploadLog.Info($"Parser downloaded and cached ({fullBundle.Length} bytes)");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                UploadLog.Error("Failed to download parser", ex);
                throw new Exception($"Failed to download parser: {ex.Message}");
            }
        }

        // Browser shims the FF Logs parser JS expects.
        private const string BrowserShimsJs = @"
            var window = {
                location: { search: '?id=1&gameContentDetectionEnabled=true&metersEnabled=false&liveFightDataEnabled=false' },
                addEventListener: function(type, listener) { if (type === 'message') { this._messageListener = listener; } },
                removeEventListener: function() {},
                dispatchEvent: function() { return true; },
                _messageListener: null
            };
            var document = {
                createElement: function() { return { style: {}, appendChild: function(){}, addEventListener: function(){} }; },
                getElementsByTagName: function() { return [{ appendChild: function(){} }]; },
                getElementById: function() { return null; },
                querySelector: function() { return null; },
                querySelectorAll: function() { return []; },
                body: { appendChild: function(){} },
                head: { appendChild: function(){} },
                readyState: 'complete',
                addEventListener: function() {},
                removeEventListener: function() {}
            };
            var self = window;
            var navigator = { userAgent: 'ACTLogsUploader/1.0', platform: 'Win32' };
            var location = window.location;
            var localStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {} };
            var sessionStorage = localStorage;

            var _timerId = 0;
            var _pendingTimeouts = [];
            function setTimeout(fn, delay) {
                var id = ++_timerId;
                if (typeof fn === 'function') _pendingTimeouts.push(fn);
                return id;
            }
            function setInterval() { return ++_timerId; }
            function clearTimeout() {}
            function clearInterval() {}
            var performance = { now: function() { return Date.now(); } };
            function fetch() { throw new Error('fetch not supported'); }

            function __drainTimeouts() {
                var safety = 1000;
                while (_pendingTimeouts.length > 0 && safety-- > 0) {
                    var batch = _pendingTimeouts.splice(0, _pendingTimeouts.length);
                    for (var i = 0; i < batch.length; i++) {
                        try { batch[i](); } catch(e) {}
                    }
                }
            }

            var URLSearchParams = function(init) {
                this._params = {};
                if (typeof init === 'string') {
                    var str = init.charAt(0) === '?' ? init.substring(1) : init;
                    var pairs = str.split('&');
                    for (var i = 0; i < pairs.length; i++) {
                        var kv = pairs[i].split('=');
                        if (kv.length === 2) this._params[decodeURIComponent(kv[0])] = decodeURIComponent(kv[1]);
                    }
                }
            };
            URLSearchParams.prototype.get = function(k) { return this._params.hasOwnProperty(k) ? this._params[k] : null; };
            URLSearchParams.prototype.set = function(k, v) { this._params[k] = v; };
            URLSearchParams.prototype.has = function(k) { return this._params.hasOwnProperty(k); };

            function __captureMessage(r) {
                if (r && typeof r === 'object') {
                    if (Array.isArray(r)) {
                        __ipc.capture(JSON.stringify({
                            message: r.message,
                            id: r.id,
                            data: Array.from(r)
                        }));
                        return;
                    }
                }
                __ipc.capture(JSON.stringify(r));
            }

            window.sendToHost = function(channel, id, event, data) {
                __captureMessage({ message: channel, id: id, data: data });
            };

            function __dispatchMessage(msgJson) {
                if (!window._messageListener) throw new Error('Parser not initialized: no message listener');
                var msg = JSON.parse(msgJson);
                window._messageListener({
                    data: msg,
                    source: { postMessage: function(r) { __captureMessage(r); } },
                    origin: 'emulator'
                });
                __drainTimeouts();
            }
        ";

        private async Task StartParserAsync()
        {
            await EnsureParserAsync().ConfigureAwait(false);
            StopParser();

            UploadLog.Info("Starting V8 parser engine...");
            _engine = new V8ScriptEngine();
            _engine.AddHostObject("__ipc", new IpcHost(this));
            _engine.Execute(BrowserShimsJs);

            var bundleCode = File.ReadAllText(_parserBundlePath);
            _engine.Execute(bundleCode);
            _engine.Execute("__drainTimeouts()");

            var hasListener = _engine.Evaluate("!!window._messageListener");
            if (!(hasListener is true))
                throw new Exception("Parser did not register a message listener — bundle may be invalid");

            var versionResponses = SendMessageAndCollect(new { message = "get-parser-version" });
            var versionResult = FindResponseByChannel(versionResponses, "get-parser-version-completed");
            if (!versionResult.HasValue ||
                versionResult.Value.ValueKind != JsonValueKind.Number ||
                !versionResult.Value.TryGetInt32(out var parserVersion))
                throw new Exception("Parser did not return a valid version");

            ParserVersion = parserVersion;
            UploadLog.Info($"V8 parser engine ready (parser version {ParserVersion})");
        }

        private void SendMessageCore(object message)
        {
            if (_engine == null)
                throw new InvalidOperationException("Parser engine is not running");

            var json = JsonSerializer.Serialize(message);
            if (!json.StartsWith("{\"message\":\"parse-lines\""))
                UploadLog.Debug($"[Parser] Sending: {json.Substring(0, Math.Min(200, json.Length))}...");

            _engine.Execute($"__dispatchMessage({JsonSerializer.Serialize(json)})");
        }

        private void SendMessage(object message)
        {
            _engineLock.Wait();
            try { SendMessageCore(message); }
            finally { _engineLock.Release(); }
        }

        private List<JsonElement> SendMessageAndCollect(object message)
        {
            _engineLock.Wait();
            try
            {
                lock (_ipcLock) { _ipcMessages.Clear(); }
                SendMessageCore(message);
                lock (_ipcLock) { return new List<JsonElement>(_ipcMessages); }
            }
            finally { _engineLock.Release(); }
        }

        private static JsonElement? FindResponseWithKey(List<JsonElement> responses, string key)
        {
            foreach (var msg in responses)
            {
                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (msg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out _))
                    return d;
                if (msg.TryGetProperty(key, out _))
                    return msg;
            }
            return null;
        }

        private static JsonElement? FindResponseByChannel(List<JsonElement> responses, string channel)
        {
            foreach (var msg in responses)
            {
                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (msg.TryGetProperty("message", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == channel)
                    return msg.TryGetProperty("data", out var d) ? d : msg;
            }
            return null;
        }

        // Two-pass parse: scan raids, then replay each in isolation to build a per-fight
        // master table scoped to that raid.
        public async Task<List<FightUpload>> ProcessLogAsync(string logPath, string reportCode, string regionCode)
        {
            await StartParserAsync().ConfigureAwait(false);
            var uploads = new List<FightUpload>();
            try
            {
                UploadLog.Info($"[Parser] Processing log: {Path.GetFileName(logPath)}");
                long? startDateMs = null;
                var firstBatch = await LogFileHelper.ReadBatchSharedAsync(logPath, 0).ConfigureAwait(false);
                foreach (var line in firstBatch.Lines)
                {
                    var t = TryParseLineTime(line);
                    if (t.HasValue) { startDateMs = t.Value.ToUnixTimeMilliseconds(); break; }
                }

                // Pass 1: scan to discover raids
                SendMessage(new { message = "set-report-code", id = 0, reportCode });
                if (startDateMs.HasValue) SendSetStartDate(startDateMs.Value);

                await ParseFileInBatchesAsync(logPath, regionCode, true, Array.Empty<object>()).ConfigureAwait(false);

                var scannedResponses = SendMessageAndCollect(new { message = "collect-scanned-raids", id = 1 });
                var scannedRaidsElement = FindResponseByChannel(scannedResponses, "collect-scanned-raids-completed");
                if (!scannedRaidsElement.HasValue || scannedRaidsElement.Value.ValueKind != JsonValueKind.Array)
                {
                    UploadLog.Warn("[Parser] No scanned raids — log may contain no fights");
                    return uploads;
                }

                var rawScanned = scannedRaidsElement.Value;
                int raidCount = rawScanned.GetArrayLength();
                UploadLog.Info($"[Parser] Scan found {raidCount} raid(s)");
                if (raidCount == 0) return uploads;

                var scannedRaidJson = new List<string>(raidCount);
                foreach (var raid in rawScanned.EnumerateArray())
                    scannedRaidJson.Add(raid.GetRawText());

                // Pass 2: per-raid replay with raidsToUpload filter
                for (int raidIdx = 0; raidIdx < scannedRaidJson.Count; raidIdx++)
                {
                    UploadLog.Debug($"[Parser] Replaying raid {raidIdx + 1}/{scannedRaidJson.Count}");

                    SendMessageAndCollect(new { message = "clear-state", id = 100 + raidIdx });
                    SendMessage(new { message = "set-report-code", id = 0, reportCode });
                    if (startDateMs.HasValue) SendSetStartDate(startDateMs.Value);

                    var raidsToUploadJson = $"[{scannedRaidJson[raidIdx]}]";
                    using (var raidsDoc = JsonDocument.Parse(raidsToUploadJson))
                    {
                        var raidsElement = raidsDoc.RootElement.Clone();
                        await ParseFileInBatchesAsync(logPath, regionCode, false, raidsElement).ConfigureAwait(false);
                    }

                    var fightsResponses = SendMessageAndCollect(new
                    {
                        message = "collect-fights",
                        id = 200 + raidIdx,
                        pushFightIfNeeded = false,
                        scanningOnly = false
                    });

                    var fightsResult = FindResponseWithKey(fightsResponses, "fights");
                    if (!fightsResult.HasValue ||
                        !fightsResult.Value.TryGetProperty("fights", out var fightsArray) ||
                        fightsArray.GetArrayLength() == 0)
                    {
                        UploadLog.Warn($"[Parser] Raid {raidIdx + 1}: no fights produced");
                        continue;
                    }

                    var masterResponses = SendMessageAndCollect(new
                    {
                        message = "collect-master-info",
                        id = 300 + raidIdx,
                        reportCode
                    });

                    var masterResult = FindResponseWithKey(masterResponses, "actorsString");
                    if (!masterResult.HasValue)
                    {
                        UploadLog.Warn($"[Parser] Raid {raidIdx + 1}: no master info");
                        continue;
                    }

                    var fr = fightsResult.Value;
                    long globalStartTime = fr.TryGetProperty("startTime", out var st) ? st.GetInt64() : 0;
                    var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
                    var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;

                    var perFightMaster = BuildMasterTableString(fightsResult, masterResult.Value);

                    foreach (var fight in fightsArray.EnumerateArray())
                    {
                        var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
                        if (!TryGetLastEventRelativeTime(eventsStr, out long lastRel)) continue;

                        long fightEndTime = globalStartTime + lastRel;
                        var fd = new FightData
                        {
                            Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                            StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                            EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                            EventsString = eventsStr,
                            EventCount = fight.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0,
                            LogVersion = logVer,
                            GameVersion = gameVer
                        };
                        uploads.Add(new FightUpload(perFightMaster, fd, globalStartTime, fightEndTime));
                    }
                }

                UploadLog.Info($"[Parser] Prepared {uploads.Count} fight upload(s)");
            }
            finally
            {
                StopParser();
            }
            return uploads;
        }

        // Normal manual uploads follow Archon App Lite's one-pass flow: read a bounded part,
        // parse it, upload committed fights, then clear those fights before reading more.
        public async Task<IncrementalUploadResult> ProcessAndUploadLogAsync(
            string logPath,
            string regionCode,
            Func<int, Task<string>> createReport,
            Func<string, FightData, int, long, long, Task> onFightComplete)
        {
            await StartParserAsync().ConfigureAwait(false);
            try
            {
                var reportCode = await createReport(ParserVersion).ConfigureAwait(false);
                SetReportCode(reportCode);

                var firstBatch = await LogFileHelper.ReadBatchSharedAsync(logPath, 0).ConfigureAwait(false);
                foreach (var line in firstBatch.Lines)
                {
                    var t = TryParseLineTime(line);
                    if (!t.HasValue) continue;
                    SendSetStartDate(t.Value.ToUnixTimeMilliseconds());
                    break;
                }

                long position = 0;
                int nextSegmentId = 1;
                while (true)
                {
                    var batch = await LogFileHelper.ReadBatchSharedAsync(logPath, position).ConfigureAwait(false);
                    if (batch.Lines.Count > 0)
                    {
                        SendParseLines(logPath, batch, regionCode);
                        position = batch.CurrentPosition;
                    }

                    nextSegmentId = await DrainCompletedFightsAsync(
                        nextSegmentId,
                        onFightComplete,
                        batch.EndOfFile,
                        "Manual").ConfigureAwait(false);

                    if (batch.EndOfFile) break;
                    if (batch.CurrentPosition <= position && batch.Lines.Count == 0)
                        throw new InvalidDataException("Log reader made no progress before the end of the file.");
                    position = batch.CurrentPosition;
                }

                return new IncrementalUploadResult(reportCode, nextSegmentId - 1);
            }
            finally
            {
                StopParser();
            }
        }

        private string BuildMasterTableString(JsonElement? fightsData, JsonElement masterData)
        {
            var sb = new StringBuilder();
            var logVersion = 72;
            var gameVersion = 1;
            var logFileDetails = "";

            if (fightsData.HasValue)
            {
                var fd = fightsData.Value;
                logVersion = fd.TryGetProperty("logVersion", out var lv) ? lv.GetInt32() : 72;
                gameVersion = fd.TryGetProperty("gameVersion", out var gv) ? gv.GetInt32() : 1;
                logFileDetails = fd.TryGetProperty("logFileDetails", out var lfd) ? lfd.GetString() ?? "" : "";
            }

            sb.Append(logVersion).Append('|').Append(gameVersion).Append('|').Append(logFileDetails).Append('\n');

            var sections = new (string IdKey, string StringKey)[]
            {
                ("lastAssignedActorID", "actorsString"),
                ("lastAssignedAbilityID", "abilitiesString"),
                ("lastAssignedTupleID", "tuplesString"),
                ("lastAssignedPetID", "petsString"),
            };

            foreach (var (idKey, stringKey) in sections)
            {
                int lastId = masterData.TryGetProperty(idKey, out var idProp) && idProp.ValueKind == JsonValueKind.Number
                    ? idProp.GetInt32() : 0;
                var value = masterData.TryGetProperty(stringKey, out var prop) ? prop.GetString() ?? "" : "";
                sb.Append(lastId).Append('\n').Append(value);
                if (value.Length > 0 && value[value.Length - 1] != '\n')
                    sb.Append('\n');
            }

            var result = sb.ToString();
            UploadLog.Debug($"[Parser] Master table: {result.Length} chars");
            return result;
        }

        private static bool TryGetLastEventRelativeTime(string eventsString, out long lastRelativeMs)
        {
            lastRelativeMs = 0;
            if (string.IsNullOrEmpty(eventsString)) return false;

            int end = eventsString.Length;
            while (end > 0 && (eventsString[end - 1] == '\n' || eventsString[end - 1] == '\r')) end--;
            if (end == 0) return false;

            int lineStart = eventsString.LastIndexOf('\n', end - 1) + 1;
            int pipe = eventsString.IndexOf('|', lineStart, end - lineStart);
            if (pipe < 0) return false;

            int firstNewline = eventsString.IndexOf('\n');
            int secondNewline = firstNewline >= 0 ? eventsString.IndexOf('\n', firstNewline + 1) : -1;
            if (secondNewline < 0 || lineStart <= secondNewline) return false;

            return long.TryParse(eventsString.Substring(lineStart, pipe - lineStart), out lastRelativeMs);
        }

        private async Task ParseFileInBatchesAsync(string logPath, string regionCode, bool scanning, object raidsToUpload)
        {
            long position = 0;
            long lineCount = 0;
            while (true)
            {
                var batch = await LogFileHelper.ReadBatchSharedAsync(logPath, position).ConfigureAwait(false);
                if (batch.Lines.Count > 0)
                {
                    SendParseLines(logPath, batch, regionCode, scanning, raidsToUpload);
                    lineCount += batch.Lines.Count;
                }

                if (batch.EndOfFile) break;
                if (batch.CurrentPosition <= position)
                    throw new InvalidDataException("Log reader made no progress before the end of the file.");
                position = batch.CurrentPosition;
            }
            UploadLog.Debug($"[Parser] Parsed {lineCount} line(s) in bounded batches");
        }

        private void SendParseLines(
            string logPath,
            LogFileHelper.LogFileBatch batch,
            string regionCode,
            bool scanning = false,
            object raidsToUpload = null)
        {
            SendMessage(new
            {
                message = "parse-lines",
                id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                lines = batch.Lines,
                scanning,
                selectedRegion = regionCode,
                raidsToUpload = raidsToUpload ?? Array.Empty<object>(),
                logFilePosition = new
                {
                    filePath = logPath,
                    startingPosition = batch.StartingPosition,
                    currentPosition = batch.CurrentPosition
                }
            });
        }

        private async Task<long> ParseAvailableLogBatchesAsync(
            string logPath,
            long position,
            string regionCode,
            bool scanning,
            object raidsToUpload,
            Func<LogFileHelper.LogFileBatch, Task> afterParse,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousPosition = position;
                var batch = await LogFileHelper.ReadBatchSharedAsync(logPath, position).ConfigureAwait(false);
                if (batch.Lines.Count > 0)
                {
                    SendParseLines(logPath, batch, regionCode, scanning, raidsToUpload);
                    position = batch.CurrentPosition;
                    if (afterParse != null)
                        await afterParse(batch).ConfigureAwait(false);
                }

                if (batch.EndOfFile) return position;
                if (batch.CurrentPosition <= previousPosition)
                    throw new InvalidDataException("Log reader made no progress before the end of the file.");
                position = batch.CurrentPosition;
            }
        }

        private async Task<long> FindLastFfxivHeaderResetPositionAsync(
            string logPath,
            CancellationToken cancellationToken)
        {
            long position = 0;
            long lastResetPosition = 0;
            bool foundReset = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await LogFileHelper.ReadBatchSharedAsync(logPath, position).ConfigureAwait(false);
                for (var i = 0; i < batch.Lines.Count; i++)
                {
                    if (!batch.Lines[i].StartsWith("253|", StringComparison.Ordinal)) continue;

                    if (i == 0)
                    {
                        lastResetPosition = batch.StartingPosition;
                    }
                    else
                    {
                        var prefix = await LogFileHelper.ReadBatchSharedAsync(
                            logPath,
                            batch.StartingPosition,
                            i,
                            LogFileHelper.DefaultBatchByteCount).ConfigureAwait(false);
                        lastResetPosition = prefix.CurrentPosition;
                    }
                    foundReset = true;
                }

                if (batch.EndOfFile) break;
                if (batch.CurrentPosition <= position)
                    throw new InvalidDataException("Log reader made no progress while finding the last header reset.");
                position = batch.CurrentPosition;
            }

            return foundReset ? lastResetPosition : 0;
        }

        private void SendCallWipe() => SendMessage(new { message = "call-wipe", id = 0 });
        private void SendSetLiveLoggingStartTime(long startTimeMs) => SendMessage(new { message = "set-live-logging-start-time", id = 0, startTime = startTimeMs });
        private void SendSetStartDate(long startDateMs) => SendMessage(new { message = "set-start-date", id = 0, startDate = startDateMs });

        private static DateTimeOffset? TryParseLineTime(string line)
        {
            var first = line.IndexOf('|');
            if (first < 0) return null;
            var second = line.IndexOf('|', first + 1);
            if (second < 0) return null;
            var ts = line.Substring(first + 1, second - first - 1);
            if (DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
                return dto;
            return null;
        }

        private static long? TryReadLatestTimestampFromFile(string logPath)
        {
            try
            {
                using (var fs = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len == 0) return null;
                    int peekSize = (int)Math.Min(8192, len);
                    fs.Seek(-peekSize, SeekOrigin.End);
                    var buf = new byte[peekSize];
                    int read = fs.Read(buf, 0, peekSize);
                    var tail = Encoding.UTF8.GetString(buf, 0, read);
                    var lines = tail.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        var t = TryParseLineTime(lines[i]);
                        if (t.HasValue) return t.Value.ToUnixTimeMilliseconds();
                    }
                }
            }
            catch { }
            return null;
        }

        // Tail the newest log file, uploading fights as they complete.
        public async Task StartLiveLogAsync(
            string logDirectory, string regionCode, bool uploadPreviousFights,
            Func<string, FightData, int, long, long, Task> onFightComplete,
            CancellationToken cancellationToken = default)
        {
            await StartParserAsync().ConfigureAwait(false);
            try
            {
                var actualDirectory = logDirectory;
                if (File.Exists(logDirectory)) actualDirectory = Path.GetDirectoryName(logDirectory) ?? logDirectory;
                else if (!Directory.Exists(logDirectory))
                {
                    var parentDir = Path.GetDirectoryName(logDirectory);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir)) actualDirectory = parentDir;
                    else throw new Exception($"Log directory not found: {logDirectory}");
                }

                var files = Directory.GetFiles(actualDirectory, "*.log");
                if (files.Length == 0) throw new Exception("No log files found in directory");

                string logPath = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                UploadLog.Info($"[LiveLog] Monitoring: {Path.GetFileName(logPath)}");

                int nextSegmentId = 1;
                DateTime lastFileCheckTime = DateTime.UtcNow;
                bool checkPending = false;
                long lastPosition = 0;
                bool fightEndDetected = false;
                DateTime fightEndDetectedTime = DateTime.MinValue;

                var firstBatch = await LogFileHelper.ReadBatchSharedAsync(logPath, 0).ConfigureAwait(false);
                long? firstLineMs = null;
                foreach (var line in firstBatch.Lines)
                {
                    var t = TryParseLineTime(line);
                    if (t.HasValue) { firstLineMs = t.Value.ToUnixTimeMilliseconds(); break; }
                }
                if (firstLineMs.HasValue) SendSetStartDate(firstLineMs.Value);

                if (uploadPreviousFights)
                {
                    if (firstLineMs.HasValue) SendSetLiveLoggingStartTime(firstLineMs.Value);
                    UploadLog.Info("[LiveLog] Reading existing lines in bounded batches");
                    lastPosition = await ParseAvailableLogBatchesAsync(
                        logPath,
                        0,
                        regionCode,
                        false,
                        Array.Empty<object>(),
                        async batch =>
                        {
                            nextSegmentId = await DrainCompletedFightsAsync(
                                nextSegmentId, onFightComplete, false, "LiveLog").ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var anchorMs = TryReadLatestTimestampFromFile(logPath) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    SendSetLiveLoggingStartTime(anchorMs);
                    var contextPosition = await FindLastFfxivHeaderResetPositionAsync(
                        logPath, cancellationToken).ConfigureAwait(false);
                    UploadLog.Info($"[LiveLog] Reading context from the last header reset at byte {contextPosition}");
                    lastPosition = await ParseAvailableLogBatchesAsync(
                        logPath,
                        contextPosition,
                        regionCode,
                        false,
                        Array.Empty<object>(),
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                bool livePhaseReady = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var positionBeforeRead = lastPosition;
                        lastPosition = await ParseAvailableLogBatchesAsync(
                            logPath,
                            lastPosition,
                            regionCode,
                            false,
                            Array.Empty<object>(),
                            batch =>
                            {
                                foreach (var line in batch.Lines)
                                    HandleDirectorLine(line, ref fightEndDetected, ref fightEndDetectedTime, livePhaseReady);
                                return Task.CompletedTask;
                            },
                            cancellationToken).ConfigureAwait(false);

                        if (lastPosition > positionBeforeRead)
                        {
                            checkPending = false;
                        }
                        else if (!livePhaseReady)
                        {
                            livePhaseReady = true;
                            UploadLog.Info("[LiveLog] Caught up with file — live phase started");
                        }

                        if ((DateTime.UtcNow - lastFileCheckTime).TotalSeconds > FileCheckIntervalSeconds)
                        {
                            lastFileCheckTime = DateTime.UtcNow;
                            var currentFiles = Directory.GetFiles(actualDirectory, "*.log");
                            if (currentFiles.Length > 0)
                            {
                                var newestFile = currentFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                                if (newestFile != logPath)
                                {
                                    lastPosition = await ParseAvailableLogBatchesAsync(
                                        logPath,
                                        lastPosition,
                                        regionCode,
                                        false,
                                        Array.Empty<object>(),
                                        null,
                                        cancellationToken).ConfigureAwait(false);
                                    UploadLog.Info($"[LiveLog] Switching to newer log file: {Path.GetFileName(newestFile)}");
                                    logPath = newestFile;
                                    lastPosition = 0;
                                    livePhaseReady = false;
                                }
                            }
                        }

                        bool fightEndCheck = fightEndDetected && (DateTime.UtcNow - fightEndDetectedTime).TotalMilliseconds >= FightEndDelayMs;

                        if (fightEndCheck && !checkPending)
                        {
                            fightEndDetected = false;
                            checkPending = true;
                            nextSegmentId = await DrainCompletedFightsAsync(
                                nextSegmentId, onFightComplete, false, "LiveLog").ConfigureAwait(false);
                        }

                        await Task.Delay(LiveLogPollIntervalMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (FileNotFoundException)
                    {
                        var replacement = Directory.Exists(actualDirectory)
                            ? Directory.GetFiles(actualDirectory, "*.log")
                                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                .FirstOrDefault()
                            : null;
                        if (!string.IsNullOrEmpty(replacement))
                        {
                            logPath = replacement;
                            lastPosition = 0;
                            livePhaseReady = false;
                            UploadLog.Info($"[LiveLog] Active log was archived; switched to {Path.GetFileName(logPath)}");
                        }
                        await Task.Delay(FileCheckIntervalSeconds * 1000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        UploadLog.Error("[LiveLog] Error in loop", ex);
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    }
                }

                UploadLog.Info("[LiveLog] Stopped");

                if (!cancellationToken.IsCancellationRequested && _engine != null)
                {
                    try
                    {
                        await DrainCompletedFightsAsync(
                            nextSegmentId, onFightComplete, true, "LiveLog").ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    { UploadLog.Error("[LiveLog] Error in final check", ex); }
                }
            }
            finally
            {
                StopParser();
            }
        }

        private void HandleDirectorLine(string line, ref bool fightEndDetected, ref DateTime fightEndDetectedTime, bool sendCallWipe)
        {
            if (!line.StartsWith("33|")) return;

            if (sendCallWipe && line.Contains($"|{WipeDirectorCode}|"))
            {
                UploadLog.Info("[LiveLog] Wipe director seen — sending call-wipe");
                SendCallWipe();
            }

            if (!fightEndDetected)
            {
                foreach (var code in FightEndDirectorCodes)
                {
                    if (line.Contains($"|{code}|"))
                    {
                        UploadLog.Info($"[LiveLog] Fight end detected (Director {code})");
                        fightEndDetected = true;
                        fightEndDetectedTime = DateTime.UtcNow;
                        break;
                    }
                }
            }
        }

        private async Task<int> DrainCompletedFightsAsync(
            int nextSegmentId,
            Func<string, FightData, int, long, long, Task> onFightComplete,
            bool pushFightIfNeeded,
            string context)
        {
            var fightsResponses = SendMessageAndCollect(new
            {
                message = "collect-fights",
                id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                pushFightIfNeeded,
                scanningOnly = false
            });

            var fightsResult = FindResponseWithKey(fightsResponses, "fights");
            if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
            {
                var currentCount = fightsArray.GetArrayLength();
                if (currentCount > 0)
                {
                    UploadLog.Info($"[{context}] {currentCount} new fight(s) detected");

                    object masterMsg = _currentReportCode != null
                        ? new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reportCode = _currentReportCode }
                        : (object)new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    var masterResponses = SendMessageAndCollect(masterMsg);
                    var masterResult = FindResponseWithKey(masterResponses, "actorsString");

                    if (masterResult.HasValue)
                    {
                        var fr = fightsResult.Value;
                        long globalStartTime = fr.TryGetProperty("startTime", out var gst) ? gst.GetInt64() : 0;
                        var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
                        var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;
                        var masterStr = BuildMasterTableString(fightsResult, masterResult.Value);

                        foreach (var fight in fightsArray.EnumerateArray())
                        {
                            var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
                            if (!TryGetLastEventRelativeTime(eventsStr, out long lastRel))
                            {
                                UploadLog.Warn($"[{context}] Skipping a fight with no valid final event time");
                                continue;
                            }

                            long fightEndTime = globalStartTime + lastRel;
                            var fightData = new FightData
                            {
                                Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                                StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                                EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                                EventsString = eventsStr,
                                EventCount = fight.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0,
                                LogVersion = logVer,
                                GameVersion = gameVer
                            };
                            UploadLog.Info($"[{context}] Uploading: {fightData.Name} (segment {nextSegmentId})");
                            await onFightComplete(
                                masterStr, fightData, nextSegmentId, globalStartTime, fightEndTime).ConfigureAwait(false);
                            nextSegmentId++;
                        }

                        SendMessageAndCollect(new
                        {
                            message = "clear-fights",
                            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    }
                }
            }
            return nextSegmentId;
        }

        // Exposed to JS for IPC capture (__ipc.capture).
        public sealed class IpcHost
        {
            private readonly ParserEngine _svc;
            public IpcHost(ParserEngine svc) => _svc = svc;

            public void capture(string json)
            {
                try
                {
                    var doc = JsonDocument.Parse(json);
                    lock (_svc._ipcLock) { _svc._ipcMessages.Add(doc.RootElement.Clone()); }
                }
                catch (Exception ex) { UploadLog.Debug($"IPC parse error: {ex.Message}"); }
            }
        }
    }
}


