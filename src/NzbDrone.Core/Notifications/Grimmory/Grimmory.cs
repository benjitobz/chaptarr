using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class Grimmory : NotificationBase<GrimmorySettings>
    {
        private readonly IGrimmoryProxy _proxy;
        private readonly Logger _logger;
        private readonly ICached<GrimmoryUpdateQueue> _pendingLibrariesCache;

        public Grimmory(IGrimmoryProxy proxy, ICacheManager cacheManager, Logger logger)
        {
            _proxy = proxy;
            _logger = logger;
            _pendingLibrariesCache = cacheManager.GetRollingCache<GrimmoryUpdateQueue>(GetType(), "pendingLibraries", TimeSpan.FromDays(1));
        }

        public override string Name => "Grimmory";
        public override string Link => "https://github.com/grimmory-tools/grimmory";

        private class GrimmoryUpdateQueue
        {
            public HashSet<long> PendingLibraries { get; } = new HashSet<long>();
            public bool Refreshing { get; set; }
        }

        public override bool HasPendingQueue
        {
            get
            {
                var queue = _pendingLibrariesCache.Find(QueueKey);

                if (queue == null)
                {
                    return false;
                }

                lock (queue)
                {
                    return !queue.Refreshing && queue.PendingLibraries.Any();
                }
            }
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            if (message.BookFiles == null || message.BookFiles.Empty())
            {
                return;
            }

            QueueRefresh(GetLibraryId(message.Book, message.BookFiles.FirstOrDefault()), "import");
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            foreach (var renamedFile in renamedFiles ?? new List<RenamedBookFile>())
            {
                if (renamedFile?.BookFile != null)
                {
                    QueueRefresh(GetLibraryId(null, renamedFile.BookFile), "rename");
                }
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage message)
        {
            if (message.DeletedFiles)
            {
                QueueRefresh(Settings.EbookLibraryId, "author delete");
                QueueRefresh(Settings.AudiobookLibraryId, "author delete");
            }
        }

        public override void OnBookDelete(BookDeleteMessage message)
        {
            if (message.DeletedFiles)
            {
                QueueRefresh(GetLibraryId(message.Book, null), "book delete");
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage message)
        {
            QueueRefresh(GetLibraryId(message.Book, message.BookFile), "file delete");
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            QueueRefresh(GetLibraryId(message.Book, message.BookFile), "retag");
        }

        public override void ProcessQueue()
        {
            var queue = _pendingLibrariesCache.Find(QueueKey);

            if (queue == null)
            {
                return;
            }

            lock (queue)
            {
                if (queue.Refreshing)
                {
                    return;
                }

                queue.Refreshing = true;
            }

            try
            {
                while (true)
                {
                    List<long> libraryIds;

                    lock (queue)
                    {
                        if (queue.PendingLibraries.Empty())
                        {
                            queue.Refreshing = false;
                            return;
                        }

                        libraryIds = queue.PendingLibraries.ToList();
                        queue.PendingLibraries.Clear();
                    }

                    var failed = new List<long>();

                    foreach (var libraryId in libraryIds)
                    {
                        try
                        {
                            _proxy.RefreshLibrary(Settings, libraryId);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "Failed to trigger Grimmory refresh for library {0}", libraryId);
                            failed.Add(libraryId);
                        }
                    }

                    if (failed.Any())
                    {
                        throw new InvalidOperationException($"Failed to trigger Grimmory refresh for libraries: {string.Join(", ", failed)}");
                    }
                }
            }
            catch
            {
                lock (queue)
                {
                    queue.Refreshing = false;
                }

                throw;
            }
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getLibraries")
            {
                if (Settings.Url.IsNullOrWhiteSpace() || Settings.Username.IsNullOrWhiteSpace() || Settings.Password.IsNullOrWhiteSpace())
                {
                    return new { options = new List<object>() };
                }

                try
                {
                    var libraries = _proxy.GetLibraries(Settings);

                    return new
                    {
                        options = libraries
                            .OrderBy(l => l.Name, StringComparer.InvariantCultureIgnoreCase)
                            .Select(l => new
                            {
                                Value = l.Id,
                                Name = l.Name,
                                Hint = l.AllowedFormats?.Any() == true ? string.Join(", ", l.AllowedFormats) : "All formats"
                            })
                    };
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retrieve libraries from Grimmory");
                    return new { options = new List<object>() };
                }
            }

            return new { };
        }

        private string QueueKey => $"{Settings.Url}:{Settings.Username}";

        private void QueueRefresh(long libraryId, string reason)
        {
            if (libraryId <= 0)
            {
                return;
            }

            _logger.Debug("Grimmory: queueing refresh of library {0} after {1}", libraryId, reason);

            var queue = _pendingLibrariesCache.Get(QueueKey, () => new GrimmoryUpdateQueue());

            lock (queue)
            {
                queue.PendingLibraries.Add(libraryId);
            }
        }

        private long GetLibraryId(Book book, BookFile bookFile)
        {
            if (book != null)
            {
                return book.MediaType == BookMediaType.Ebook ? Settings.EbookLibraryId : Settings.AudiobookLibraryId;
            }

            var mediaType = bookFile?.MediaType;

            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            return mediaType switch
            {
                "ebook" => Settings.EbookLibraryId,
                "audiobook" => Settings.AudiobookLibraryId,
                _ => 0
            };
        }
    }
}
