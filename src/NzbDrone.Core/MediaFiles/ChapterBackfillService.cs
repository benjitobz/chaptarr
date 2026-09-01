using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles
{
    public class ChapterBackfillService : IExecute<ChapterBackfillCommand>
    {
        private const int MaxStampsPerRun = 5;

        private static readonly string[] EligibleExtensions = { ".m4b", ".m4a", ".mp4", ".mp3" };

        private readonly IMediaFileRepository _mediaFileRepository;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly IChapterBackfillLogRepository _logRepository;
        private readonly IExternalToolsService _externalTools;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public ChapterBackfillService(IMediaFileRepository mediaFileRepository,
                                      IMediaFileService mediaFileService,
                                      IEditionService editionService,
                                      IChapterBackfillLogRepository logRepository,
                                      IExternalToolsService externalTools,
                                      IDiskProvider diskProvider,
                                      Logger logger)
        {
            _mediaFileRepository = mediaFileRepository;
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _logRepository = logRepository;
            _externalTools = externalTools;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void Execute(ChapterBackfillCommand message)
        {
            if (!_externalTools.IsFFmpegAvailable() || !_externalTools.IsFFprobeAvailable())
            {
                _logger.Warn("Chapter backfill requires ffmpeg and ffprobe; skipping run");
                return;
            }

            var logByPath = _logRepository.All()
                .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.Id).First(), StringComparer.Ordinal);

            var candidates = _mediaFileRepository.All()
                .Where(file => file.EditionId > 0 &&
                               string.Equals(file.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                               file.Path.IsNotNullOrWhiteSpace() &&
                               EligibleExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file.Id)
                .ToList();

            var stamped = 0;
            var examined = 0;

            foreach (var file in candidates)
            {
                if (stamped >= MaxStampsPerRun)
                {
                    _logger.Debug("Chapter backfill reached the per-run cap of {0} stamped files", MaxStampsPerRun);
                    break;
                }

                if (!message.RetrySkipped &&
                    logByPath.TryGetValue(file.Path, out var seen) &&
                    seen.Size == file.Size)
                {
                    continue;
                }

                if (!_diskProvider.FileExists(file.Path))
                {
                    continue;
                }

                examined++;

                if (ProcessFile(file, logByPath))
                {
                    stamped++;
                }
            }

            _logger.ProgressInfo("Chapter backfill finished: {0} file(s) stamped, {1} examined", stamped, examined);
        }

        private bool ProcessFile(BookFile file, Dictionary<string, ChapterBackfillLogEntry> logByPath)
        {
            var embeddedChapters = CountEmbeddedChapters(file.Path);
            if (embeddedChapters < 0)
            {
                return false;
            }

            if (embeddedChapters >= 2)
            {
                RecordOutcome(logByPath, file.Path, file.Size, "HasChapters", null);
                return false;
            }

            Edition edition = null;
            try
            {
                edition = _editionService.GetEdition(file.EditionId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to load edition {0} for chapter backfill of {1}", file.EditionId, file.Path);
            }

            if (!TryGetDuration(file.Path, out var duration))
            {
                _logger.Debug("Unable to read audio duration for {0}; will retry on a later pass", file.Path);
                return false;
            }

            if (!EditionChapterValidation.TryValidate(edition, duration, out var chapters, out var skipReason))
            {
                var reason = skipReason ?? "the metadata provider did not supply usable chapters";
                _logger.Debug("Skipping chapter backfill for {0} because {1}", file.Path, reason);
                RecordOutcome(logByPath, file.Path, file.Size, "Skipped", reason);
                return false;
            }

            if (!TryStampChapters(file.Path, chapters, duration))
            {
                return false;
            }

            file.Size = _diskProvider.GetFileSize(file.Path);
            file.Modified = _diskProvider.FileGetLastWrite(file.Path);
            _mediaFileService.Update(file);
            RecordOutcome(logByPath, file.Path, file.Size, "Stamped", null);
            _logger.Info("Stamped {0} provider chapters into {1}", chapters.Count, file.Path);
            return true;
        }

        private bool TryStampChapters(string path, IReadOnlyList<EditionChapter> chapters, TimeSpan duration)
        {
            var directory = Path.GetDirectoryName(path);
            var extension = Path.GetExtension(path);
            var token = Guid.NewGuid().ToString("N");
            var metadataPath = Path.Combine(directory, ".chaptarr-backfill-" + token + ".ffmeta");
            var outputPath = Path.Combine(directory, ".chaptarr-backfill-" + token + extension);

            try
            {
                _diskProvider.WriteAllText(metadataPath, BuildFfMetadata(chapters, duration));

                _externalTools.ExecuteFFmpeg(
                    new[]
                    {
                        "-hide_banner",
                        "-loglevel", "error",
                        "-y",
                        "-i", path,
                        "-f", "ffmetadata",
                        "-i", metadataPath,
                        "-map", "0",
                        "-map_metadata", "0",
                        "-map_chapters", "1",
                        "-c", "copy",
                        outputPath
                    },
                    timeoutMs: 600000,
                    preferStderrOnEmpty: true);

                if (!_diskProvider.FileExists(outputPath) || _diskProvider.GetFileSize(outputPath) == 0)
                {
                    _logger.Warn("Chapter backfill remux produced no output for {0}", path);
                    return false;
                }

                var outputChapters = CountEmbeddedChapters(outputPath);
                if (outputChapters != chapters.Count)
                {
                    _logger.Warn("Chapter backfill remux of {0} produced {1} chapters instead of {2}; keeping the original file", path, outputChapters, chapters.Count);
                    return false;
                }

                if (!TryGetDuration(outputPath, out var outputDuration) ||
                    (outputDuration - duration).Duration() > TimeSpan.FromSeconds(5))
                {
                    _logger.Warn("Chapter backfill remux of {0} changed the audio duration; keeping the original file", path);
                    return false;
                }

                _diskProvider.MoveFile(outputPath, path, true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Chapter backfill failed for {0}", path);
                return false;
            }
            finally
            {
                DeleteQuietly(metadataPath);
                DeleteQuietly(outputPath);
            }
        }

        private static string BuildFfMetadata(IReadOnlyList<EditionChapter> chapters, TimeSpan duration)
        {
            var totalMs = (long)Math.Round(duration.TotalMilliseconds);
            var builder = new StringBuilder(";FFMETADATA1\n");

            for (var i = 0; i < chapters.Count; i++)
            {
                var start = (long)chapters[i].StartOffsetMs;
                var end = i + 1 < chapters.Count ? Math.Min(chapters[i + 1].StartOffsetMs, totalMs) : totalMs;
                if (end <= start)
                {
                    continue;
                }

                builder.Append("[CHAPTER]\n");
                builder.Append("TIMEBASE=1/1000\n");
                builder.Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("title=").Append(EscapeFfMetadataValue(chapters[i].Title)).Append('\n');
            }

            return builder.ToString();
        }

        private static string EscapeFfMetadataValue(string value)
        {
            var collapsed = string.Join(" ", (value ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var builder = new StringBuilder(collapsed.Length);
            foreach (var c in collapsed)
            {
                if (c == '=' || c == ';' || c == '#' || c == '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private int CountEmbeddedChapters(string path)
        {
            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-show_chapters",
                        "-of", "csv=p=0",
                        path
                    },
                    timeoutMs: 30000);

                return output?
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Length ?? 0;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to inspect embedded chapters for {0}", path);
                return -1;
            }
        }

        private bool TryGetDuration(string path, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        path
                    },
                    timeoutMs: 30000);

                var line = output?
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();

                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    duration = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read audio duration for {0}", path);
            }

            return false;
        }

        private void RecordOutcome(Dictionary<string, ChapterBackfillLogEntry> logByPath, string path, long size, string outcome, string reason)
        {
            if (logByPath.TryGetValue(path, out var existing))
            {
                existing.Size = size;
                existing.Outcome = outcome;
                existing.Reason = reason;
                existing.ProcessedAt = DateTime.UtcNow;
                _logRepository.Update(existing);
                return;
            }

            var entry = new ChapterBackfillLogEntry
            {
                Path = path,
                Size = size,
                Outcome = outcome,
                Reason = reason,
                ProcessedAt = DateTime.UtcNow
            };

            _logRepository.Insert(entry);
            logByPath[path] = entry;
        }

        private void DeleteQuietly(string path)
        {
            try
            {
                if (_diskProvider.FileExists(path))
                {
                    _diskProvider.DeleteFile(path);
                }
            }
            catch
            {
            }
        }
    }
}
