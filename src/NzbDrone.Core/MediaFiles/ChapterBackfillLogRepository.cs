using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IChapterBackfillLogRepository : IBasicRepository<ChapterBackfillLogEntry>
    {
    }

    public class ChapterBackfillLogRepository : BasicRepository<ChapterBackfillLogEntry>, IChapterBackfillLogRepository
    {
        public ChapterBackfillLogRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }
    }
}
