using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public class ChapterBackfillLogEntry : ModelBase
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Outcome { get; set; }
        public string Reason { get; set; }
        public DateTime ProcessedAt { get; set; }
    }
}
