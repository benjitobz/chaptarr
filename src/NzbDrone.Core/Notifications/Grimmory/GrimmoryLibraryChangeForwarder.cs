using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Notifications.Grimmory
{
    // Watches Grimmory for metadata and cover edits and forwards them to connections that
    // implement IExternalLibraryEditTarget. Grimmory is remote, so unlike the calibre
    // forwarder there is no filesystem signal to hook - metadata edits are detected through
    // Grimmory's audit log (filtered by the connection's own username, so Chaptarr's pushes
    // do not echo back) and cover edits through each book's coverUpdatedOn stamps.
    public class GrimmoryLibraryChangeForwarder : IHandle<ApplicationStartedEvent>, IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan AuditOverlap = TimeSpan.FromMinutes(5);

        private readonly INotificationFactory _notificationFactory;
        private readonly IGrimmoryProxy _proxy;
        private readonly IRootFolderService _rootFolderService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        private readonly System.Timers.Timer _timer;
        private readonly object _pollLock = new object();
        private readonly ConcurrentDictionary<int, SourceState> _states = new ConcurrentDictionary<int, SourceState>();

        private class SourceState
        {
            public DateTime LastAuditPoll { get; set; }
            public Dictionary<long, (DateTime? Cover, DateTime? AudiobookCover)> CoverStamps { get; } = new Dictionary<long, (DateTime?, DateTime?)>();
            public bool Primed { get; set; }
        }

        public GrimmoryLibraryChangeForwarder(INotificationFactory notificationFactory,
                                              IGrimmoryProxy proxy,
                                              IRootFolderService rootFolderService,
                                              IMediaFileService mediaFileService,
                                              IEditionService editionService,
                                              IBookService bookService,
                                              Logger logger)
        {
            _notificationFactory = notificationFactory;
            _proxy = proxy;
            _rootFolderService = rootFolderService;
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _bookService = bookService;
            _logger = logger;

            _timer = new System.Timers.Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
            _timer.Elapsed += (s, e) => Poll();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            Poll();
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        private void Poll()
        {
            if (!System.Threading.Monitor.TryEnter(_pollLock))
            {
                return;
            }

            try
            {
                foreach (var source in ForwardingSources())
                {
                    try
                    {
                        PollSource(source);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to poll Grimmory '{0}' for library edits", source.Definition.Name);
                    }
                }
            }
            finally
            {
                System.Threading.Monitor.Exit(_pollLock);
            }
        }

        private List<Grimmory> ForwardingSources()
        {
            return _notificationFactory.GetAvailableProviders()
                .OfType<Grimmory>()
                .Where(g => (g.Definition?.Settings as GrimmorySettings)?.ForwardEdits == true)
                .ToList();
        }

        private void PollSource(Grimmory source)
        {
            var settings = (GrimmorySettings)source.Definition.Settings;
            var state = _states.GetOrAdd(source.Definition.Id, _ => new SourceState { LastAuditPoll = DateTime.UtcNow });
            var pollStarted = DateTime.UtcNow;

            var libraryIds = new[] { settings.EbookLibraryId, settings.AudiobookLibraryId }.Where(id => id > 0).Distinct().ToList();
            var books = libraryIds
                .SelectMany(id => FetchLibraryBooks(settings, id))
                .GroupBy(b => b.Id)
                .Select(g => g.First())
                .ToDictionary(b => b.Id);

            var changedIds = new HashSet<long>(DetectCoverChanges(state, books));

            if (state.Primed)
            {
                foreach (var entry in _proxy.GetMetadataAuditEntries(settings, state.LastAuditPoll - AuditOverlap))
                {
                    if (entry.EntityId == null || entry.EntityType != "Book")
                    {
                        continue;
                    }

                    if (entry.CreatedAt == null || entry.CreatedAt <= state.LastAuditPoll - AuditOverlap)
                    {
                        continue;
                    }

                    if (entry.Username.IsNotNullOrWhiteSpace() && entry.Username.Equals(settings.Username, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    changedIds.Add(entry.EntityId.Value);
                }
            }

            state.LastAuditPoll = pollStarted;

            if (!state.Primed)
            {
                state.Primed = true;
                return;
            }

            if (changedIds.Empty())
            {
                return;
            }

            var targets = _notificationFactory.GetAvailableProviders()
                .OfType<IExternalLibraryEditTarget>()
                .Where(t => t.AcceptsExternalLibraryEdits && t.Definition?.Id != source.Definition.Id)
                .ToList();

            if (targets.Empty())
            {
                _logger.Debug("Grimmory '{0}' has {1} changed book(s) but no connections accept library edits", source.Definition.Name, changedIds.Count);
                return;
            }

            foreach (var grimmoryId in changedIds)
            {
                if (!books.TryGetValue(grimmoryId, out var grimmoryBook))
                {
                    continue;
                }

                try
                {
                    ForwardBook(settings, grimmoryBook, targets);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to forward Grimmory edit for book {0}", grimmoryId);
                }
            }
        }

        private List<GrimmoryBook> FetchLibraryBooks(GrimmorySettings settings, long libraryId)
        {
            try
            {
                return _proxy.GetLibraryBooks(settings, libraryId, bypassCache: true);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to list Grimmory library {0}", libraryId);
                return new List<GrimmoryBook>();
            }
        }

        private List<long> DetectCoverChanges(SourceState state, Dictionary<long, GrimmoryBook> books)
        {
            var changed = new List<long>();

            foreach (var book in books.Values)
            {
                var stamps = (book.Metadata?.CoverUpdatedOn, book.Metadata?.AudiobookCoverUpdatedOn);

                if (state.CoverStamps.TryGetValue(book.Id, out var previous) && state.Primed)
                {
                    if ((stamps.Item1 != null && stamps.Item1 > (previous.Cover ?? DateTime.MinValue)) ||
                        (stamps.Item2 != null && stamps.Item2 > (previous.AudiobookCover ?? DateTime.MinValue)))
                    {
                        changed.Add(book.Id);
                    }
                }

                state.CoverStamps[book.Id] = stamps;
            }

            return changed;
        }

        private void ForwardBook(GrimmorySettings settings, GrimmoryBook grimmoryBook, List<IExternalLibraryEditTarget> targets)
        {
            var bookFile = ResolveBookFile(grimmoryBook);

            if (bookFile == null)
            {
                _logger.Debug("No Chaptarr file matches Grimmory book {0}; skipping forward", grimmoryBook.Id);
                return;
            }

            var edition = _editionService.GetEdition(bookFile.EditionId);
            var book = edition == null ? null : _bookService.GetBook(edition.BookId);

            if (book == null)
            {
                return;
            }

            var files = _mediaFileService.GetFilesByBook(book.Id);
            var payload = BuildPayload(settings, grimmoryBook);

            foreach (var target in targets)
            {
                try
                {
                    target.PushExternalLibraryEdit(book, files, payload);
                    _logger.Debug("Forwarded Grimmory edit of '{0}' to {1}", book.Title, target.Definition?.Name);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to forward Grimmory edit of '{0}' to {1}", book.Title, target.Definition?.Name);
                }
            }
        }

        private BookFile ResolveBookFile(GrimmoryBook grimmoryBook)
        {
            foreach (var relativePath in grimmoryBook.AllFiles().Select(f => f?.RelativePath()).Where(p => p.IsNotNullOrWhiteSpace()))
            {
                var osRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);

                foreach (var rootFolder in _rootFolderService.All())
                {
                    var candidate = Path.Combine(rootFolder.Path, osRelative);
                    var file = _mediaFileService.GetFileWithPath(candidate);

                    if (file != null)
                    {
                        return file;
                    }
                }
            }

            return null;
        }

        private ExternalLibraryEditPayload BuildPayload(GrimmorySettings settings, GrimmoryBook grimmoryBook)
        {
            var metadata = grimmoryBook.Metadata;
            var payload = new ExternalLibraryEditPayload();

            if (metadata != null)
            {
                payload.Title = metadata.Title;
                payload.Subtitle = metadata.Subtitle;
                payload.Description = metadata.Description;
                payload.Publisher = metadata.Publisher;
                payload.SeriesName = metadata.SeriesName;
                payload.SeriesPosition = metadata.SeriesNumber;
                payload.Languages = metadata.Language.IsNotNullOrWhiteSpace() ? new List<string> { metadata.Language } : null;
                payload.Genres = metadata.Categories?.Any() == true ? metadata.Categories : null;

                if (DateTime.TryParse(metadata.PublishedDate, out var published))
                {
                    payload.PublishedDate = published;
                }

                var identifiers = new Dictionary<string, string>();

                if (metadata.Isbn13.IsNotNullOrWhiteSpace())
                {
                    identifiers["isbn"] = metadata.Isbn13;
                }

                if (metadata.Asin.IsNotNullOrWhiteSpace())
                {
                    identifiers["asin"] = metadata.Asin;
                }

                if (metadata.GoodreadsId.IsNotNullOrWhiteSpace())
                {
                    identifiers["goodreads"] = metadata.GoodreadsId;
                }

                if (identifiers.Any())
                {
                    payload.Identifiers = identifiers;
                }
            }

            try
            {
                payload.CoverBytes = _proxy.GetBookCover(settings, grimmoryBook.Id);
                payload.CoverUrl = _proxy.BuildCoverUrl(settings, grimmoryBook.Id);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not fetch Grimmory cover for book {0}", grimmoryBook.Id);
            }

            return payload;
        }
    }
}
