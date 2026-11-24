// Ported from ACTLogsUploader (MIT, see THIRD_PARTY_NOTICES.md); kept close to upstream.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace DalamudActCompat.Host.FflogsUpload
{
    public static class LogFileHelper
    {
        // Archon App Lite reads log files in bounded parts instead of serializing an entire
        // file into one parser message. Keep the same default limits here.
        public const int DefaultBatchLineCount = 5000;
        public const int DefaultBatchByteCount = 8 * 1024 * 1024;
        private const int MaximumLineByteCount = 256 * 1024;
        private const int ReadBufferByteCount = 64 * 1024;

        public sealed class LogFileBatch
        {
            public List<string> Lines { get; }
            public long StartingPosition { get; }
            public long CurrentPosition { get; }
            public bool EndOfFile { get; }

            public LogFileBatch(List<string> lines, long startingPosition, long currentPosition, bool endOfFile)
            {
                Lines = lines;
                StartingPosition = startingPosition;
                CurrentPosition = currentPosition;
                EndOfFile = endOfFile;
            }
        }

        // Upstream's AutoDetectLogDirectory is left out on purpose: DACT always passes its own
        // Network log directory and never guesses another ACT installation's.

        public static string GetLatestLogFileFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (File.Exists(path)) return path;
            if (Directory.Exists(path))
            {
                try
                {
                    var files = Directory.GetFiles(path, "*.log");
                    if (files.Length > 0)
                        return files.Select(f => new FileInfo(f))
                                    .OrderByDescending(fi => fi.LastWriteTime)
                                    .First().FullName;
                }
                catch (Exception ex) { UploadLog.Warn($"[LogFileHelper] {ex.Message}"); }
            }
            return path;
        }

        // Reads only complete UTF-8 lines and returns the byte position immediately after the
        // last returned newline. A partial trailing line is retried from its start next time.
        public static async Task<LogFileBatch> ReadBatchSharedAsync(
            string path,
            long position,
            int maximumLineCount = DefaultBatchLineCount,
            int maximumByteCount = DefaultBatchByteCount)
        {
            if (maximumLineCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLineCount));
            if (maximumByteCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumByteCount));

            var lines = new List<string>();
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferByteCount,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (position < 0 || stream.Length < position)
                    position = 0;

                var startingPosition = position;
                var committedPosition = position;
                var readPosition = position;
                var snapshotLength = stream.Length;
                var maximumReadPosition = Math.Min(snapshotLength, startingPosition + maximumByteCount);
                var readBuffer = new byte[ReadBufferByteCount];

                using (var lineBuffer = new MemoryStream())
                {
                    stream.Seek(startingPosition, SeekOrigin.Begin);

                    while (readPosition < maximumReadPosition && lines.Count < maximumLineCount)
                    {
                        var requested = (int)Math.Min(readBuffer.Length, maximumReadPosition - readPosition);
                        var read = await stream.ReadAsync(readBuffer, 0, requested).ConfigureAwait(false);
                        if (read == 0) break;

                        for (var i = 0; i < read; i++)
                        {
                            var value = readBuffer[i];
                            readPosition++;

                            if (value == (byte)'\n')
                            {
                                lines.Add(DecodeLine(lineBuffer));
                                lineBuffer.SetLength(0);
                                committedPosition = readPosition;
                                if (lines.Count >= maximumLineCount) break;
                                continue;
                            }

                            lineBuffer.WriteByte(value);
                            if (lineBuffer.Length > MaximumLineByteCount)
                                throw new InvalidDataException("Log contains more than 256 KiB without a newline.");
                        }
                    }

                    // Match Archon: do not commit an unterminated trailing line. A live file can
                    // complete it on the next poll without feeding a split line to the parser.
                    var endOfFile = readPosition >= snapshotLength;
                    return new LogFileBatch(lines, startingPosition, committedPosition, endOfFile);
                }
            }
        }

        private static string DecodeLine(MemoryStream lineBuffer)
        {
            var buffer = lineBuffer.GetBuffer();
            var length = (int)lineBuffer.Length;
            if (length > 0 && buffer[length - 1] == (byte)'\r') length--;
            return Encoding.UTF8.GetString(buffer, 0, length).Replace("\0", "").Trim();
        }
    }
}

