using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Notifications.CalibreContentServer;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreLibraryChangeForwarder :
        IHandle<ApplicationStartedEvent>,
        IHandle<ModelEvent<RootFolder>>,
        IHandle<MediaFiles.Events.AuthorScannedEvent>,
        IDisposable
    {
        // Edits made in the calibre library itself never pass through Chaptarr, so the
        // connections would never hear about them. Every such edit writes the library's
        // metadata.db - a file the root folder watcher deliberately filters out - so a
        // watcher on that file alone is the change signal. The library's current values
        // are forwarded outward without writing any of it into Chaptarr's own records,
        // which stay the source for identity and its own pushes.
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<string, (DateTime LastModified, int BookId)> _lastSeen = new ConcurrentDictionary<string, (DateTime LastModified, int BookId)>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new ConcurrentDictionary<int, FileSystemWatcher>();
        private readonly ConcurrentDictionary<int, byte> _pendingRootFolders = new ConcurrentDictionary<int, byte>();
        private readonly System.Timers.Timer _debounce;
        private readonly object _forwardLock = new object();

        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        public CalibreLibraryChangeForwarder(IRootFolderService rootFolderService,
                                             ICalibreProxy calibreProxy,
                                             IMediaFileService mediaFileService,
                                             IEditionService editionService,
                                             IBookService bookService,
                                             INotificationFactory notificationFactory,
                                             Logger logger)
        {
            _rootFolderService = rootFolderService;
            _calibreProxy = calibreProxy;
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _bookService = bookService;
            _notificationFactory = notificationFactory;
            _logger = logger;

            _debounce = new System.Timers.Timer(DebounceDelay.TotalMilliseconds) { AutoReset = false };
            _debounce.Elapsed += (s, e) => ForwardPending();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            SyncWatchers();

            // Prime the baseline so the first observed write after startup is compared
            // against the library as it is now, not forwarded wholesale.
            foreach (var rootFolder in CalibreRootFolders())
            {
                ForwardChangedRecords(rootFolder, new List<CalibreContentServer>(), new List<AudioBookShelf>(), new List<CalibreContentServer>());
            }
        }

        public void Handle(ModelEvent<RootFolder> message)
        {
            SyncWatchers();
        }

        public void Handle(MediaFiles.Events.AuthorScannedEvent message)
        {
            // A lost inotify event would otherwise go unnoticed until the next edit;
            // scans already run on file changes, so let them double as a recheck.
            foreach (var rootFolder in CalibreRootFolders())
            {
                OnLibraryChanged(rootFolder.Id);
            }
        }

        private void SyncWatchers()
        {
            var wanted = CalibreRootFolders().ToDictionary(r => r.Id);

            foreach (var stale in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                if (_watchers.TryRemove(stale, out var watcher))
                {
                    watcher.Dispose();
                }
            }

            foreach (var rootFolder in wanted.Values)
            {
                if (_watchers.ContainsKey(rootFolder.Id))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(rootFolder.Path, "metadata.db*")
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        IncludeSubdirectories = false,
                        InternalBufferSize = 65536
                    };

                    var id = rootFolder.Id;
                    watcher.Changed += (s, e) => OnLibraryChanged(id);
                    watcher.Created += (s, e) => OnLibraryChanged(id);
                    watcher.Renamed += (s, e) => OnLibraryChanged(id);
                    watcher.Error += (s, e) =>
                    {
                        _logger.Warn(e.GetException(), "The calibre library watcher failed; recreating it");

                        if (_watchers.TryRemove(id, out var dead))
                        {
                            dead.Dispose();
                        }

                        SyncWatchers();
                        OnLibraryChanged(id);
                    };
                    watcher.EnableRaisingEvents = true;

                    _watchers[rootFolder.Id] = watcher;
                    _logger.Debug("Watching the calibre library under {0} for edits", rootFolder.Path);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to watch the calibre library under {0}", rootFolder.Path);
                }
            }
        }

        private void OnLibraryChanged(int rootFolderId)
        {
            _pendingRootFolders[rootFolderId] = 1;
            _debounce.Stop();
            _debounce.Start();
        }

        private void ForwardPending()
        {
            lock (_forwardLock)
            {
                var mirrors = _notificationFactory.GetAvailableProviders()
                    .OfType<CalibreContentServer>()
                    .Where(c => ((CalibreContentServerSettings)c.Definition.Settings).PushLibraryEdits)
                    .ToList();

                var deleters = _notificationFactory.GetAvailableProviders()
                    .OfType<CalibreContentServer>()
                    .Where(c => ((CalibreContentServerSettings)c.Definition.Settings).SyncChanges)
                    .ToList();

                var shelves = _notificationFactory.GetAvailableProviders()
                    .OfType<AudioBookShelf>()
                    .Where(s => ((AudioBookShelfSettings)s.Definition.Settings).PushLibraryEdits)
                    .ToList();

                foreach (var rootFolderId in _pendingRootFolders.Keys.ToList())
                {
                    _pendingRootFolders.TryRemove(rootFolderId, out _);

                    var rootFolder = CalibreRootFolders().FirstOrDefault(r => r.Id == rootFolderId);

                    if (rootFolder == null)
                    {
                        continue;
                    }

                    try
                    {
                        ForwardChangedRecords(rootFolder, mirrors, shelves, deleters);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to forward calibre library edits under {0}", rootFolder.Path);
                    }
                }
            }
        }

        private IEnumerable<RootFolder> CalibreRootFolders()
        {
            return _rootFolderService.All().Where(r => r.IsCalibreLibrary && r.CalibreSettings != null);
        }

        private void ForwardChangedRecords(RootFolder rootFolder, List<CalibreContentServer> mirrors, List<AudioBookShelf> shelves, List<CalibreContentServer> deleters)
        {
            var filesByCalibreId = _mediaFileService.GetFilesWithBasePath(rootFolder.Path)
                .Where(f => f.CalibreId > 0)
                .GroupBy(f => f.CalibreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (!filesByCalibreId.Any())
            {
                return;
            }

            List<CalibreBook> records;

            try
            {
                records = _calibreProxy.GetBooks(filesByCalibreId.Keys.ToList(), rootFolder.CalibreSettings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read the calibre library under {0}", rootFolder.Path);
                return;
            }

            // A remembered record the library no longer returns was deleted there. The
            // scan that notices the vanished files can clean the tracked rows before this
            // pass runs, so the book is remembered here rather than looked up through them.
            var returned = new HashSet<int>(records.Where(r => r != null).Select(r => r.Id));
            var prefix = rootFolder.Id + ":";

            foreach (var pair in _lastSeen.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                var calibreId = int.Parse(pair.Key.Substring(prefix.Length));

                if (returned.Contains(calibreId))
                {
                    continue;
                }

                if (filesByCalibreId.TryGetValue(calibreId, out var missingFiles) && missingFiles.Any(f => File.Exists(f.Path)))
                {
                    continue;
                }

                _lastSeen.TryRemove(pair.Key, out _);

                if (!deleters.Any() || pair.Value.BookId <= 0)
                {
                    continue;
                }

                var deletedBook = _bookService.GetBook(pair.Value.BookId);

                if (deletedBook == null)
                {
                    continue;
                }

                foreach (var deleter in deleters)
                {
                    try
                    {
                        deleter.RemoveDeletedBook(deletedBook);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to carry the library deletion of '{0}' to a content server", deletedBook.Title);
                    }
                }

                _logger.Info("The library deleted '{0}'; removed it from {1} content server(s)", deletedBook.Title, deleters.Count);
            }

            foreach (var record in records.Where(r => r != null && r.LastModified.HasValue))
            {
                var key = rootFolder.Id + ":" + record.Id;

                if (!filesByCalibreId.TryGetValue(record.Id, out var files))
                {
                    continue;
                }

                if (!_lastSeen.TryGetValue(key, out var previous))
                {
                    _lastSeen[key] = (record.LastModified.Value, ResolveBookId(files));
                    continue;
                }

                var bookId = previous.BookId > 0 ? previous.BookId : ResolveBookId(files);

                if (record.LastModified.Value <= previous.LastModified)
                {
                    if (bookId != previous.BookId)
                    {
                        _lastSeen[key] = (previous.LastModified, bookId);
                    }

                    continue;
                }

                _lastSeen[key] = (record.LastModified.Value, bookId);

                if (!mirrors.Any() && !shelves.Any())
                {
                    continue;
                }

                var book = bookId > 0 ? _bookService.GetBook(bookId) : null;

                if (book == null)
                {
                    continue;
                }

                var cover = _calibreProxy.GetCoverBytes(record.Id, rootFolder.CalibreSettings);

                foreach (var mirror in mirrors)
                {
                    try
                    {
                        mirror.PushExternalMetadata(book, record, cover);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to forward the library edit of '{0}' to a content server", record.Title);
                    }
                }

                foreach (var shelf in shelves)
                {
                    try
                    {
                        shelf.PushExternalBookMetadata(book, files, BuildShelfPayload(book, record), CoverUrl(rootFolder.CalibreSettings, record.Id));
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to forward the library edit of '{0}' to AudioBookShelf", record.Title);
                    }
                }

                _logger.Info("Forwarded a library edit of '{0}' to {1} connection(s)", record.Title, mirrors.Count + shelves.Count);
            }
        }

        private int ResolveBookId(List<MediaFiles.BookFile> files)
        {
            var edition = _editionService.GetEdition(files.First().EditionId);

            return edition?.BookId ?? 0;
        }

        private static AudioBookShelfItemMetadata BuildShelfPayload(Book book, CalibreBook record)
        {
            return new AudioBookShelfItemMetadata
            {
                Title = book.Title,
                Description = record.Comments,
                Publisher = record.Publisher,
                SeriesName = record.Series,
                SeriesPosition = record.Position?.ToString(),
                Genres = record.Tags ?? new List<string>()
            };
        }

        private static string CoverUrl(CalibreSettings settings, int calibreId)
        {
            var scheme = settings.UseSsl ? "https" : "http";
            var urlBase = settings.UrlBase.IsNullOrWhiteSpace() ? string.Empty : "/" + settings.UrlBase.Trim('/');

            return scheme + "://" + settings.Host + ":" + settings.Port + urlBase + "/get/cover/" + calibreId + "/" + settings.Library;
        }

        public void Dispose()
        {
            _debounce.Dispose();

            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }
}
