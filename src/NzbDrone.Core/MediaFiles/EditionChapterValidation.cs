using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MediaFiles
{
    public static class EditionChapterValidation
    {
        public static bool TryValidate(Edition edition, TimeSpan sourceDuration, out List<EditionChapter> chapters, out string skipReason)
        {
            chapters = null;
            skipReason = null;

            if (edition?.Chapters == null || edition.Chapters.Count < 2)
            {
                return false;
            }

            var candidates = edition.Chapters
                .Where(chapter => chapter != null &&
                                  chapter.StartOffsetMs >= 0 &&
                                  chapter.Title.IsNotNullOrWhiteSpace())
                .GroupBy(chapter => chapter.StartOffsetMs)
                .Select(group => group.First())
                .OrderBy(chapter => chapter.StartOffsetMs)
                .ToList();

            if (candidates.Count < 2 || sourceDuration <= TimeSpan.Zero)
            {
                return false;
            }

            var referenceDuration = GetReferenceDuration(edition, candidates);
            if (referenceDuration <= TimeSpan.Zero)
            {
                return false;
            }

            var allowedDifference = AudiobookDurationTolerance.ForMatchingSeconds((int)Math.Round(referenceDuration.TotalSeconds, MidpointRounding.AwayFromZero));
            var actualDifference = (sourceDuration - referenceDuration).Duration();
            if (actualDifference > TimeSpan.FromSeconds(allowedDifference))
            {
                skipReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "source duration {0} does not match provider chapter duration {1}; allowed difference is {2}",
                    FormatTime(sourceDuration),
                    FormatTime(referenceDuration),
                    FormatTime(TimeSpan.FromSeconds(allowedDifference)));
                return false;
            }

            var lastStart = TimeSpan.FromMilliseconds(candidates.Last().StartOffsetMs);
            if (lastStart >= sourceDuration)
            {
                skipReason = "the last chapter starts after the source audio ends";
                return false;
            }

            chapters = candidates;
            return true;
        }

        private static TimeSpan GetReferenceDuration(Edition edition, IReadOnlyList<EditionChapter> chapters)
        {
            var chapterEndMs = chapters
                .Where(chapter => chapter.LengthMs > 0)
                .Select(chapter => chapter.StartOffsetMs + chapter.LengthMs)
                .DefaultIfEmpty(0)
                .Max();

            if (chapterEndMs > 0)
            {
                return TimeSpan.FromMilliseconds(chapterEndMs);
            }

            if (MediaDuration.HasDuration(edition?.DurationSeconds))
            {
                return TimeSpan.FromSeconds(edition.DurationSeconds.Value);
            }

            return TimeSpan.Zero;
        }

        public static string FormatTime(TimeSpan value)
        {
            var hours = (int)Math.Floor(value.TotalHours);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
                hours,
                value.Minutes,
                value.Seconds,
                value.Milliseconds);
        }
    }
}
