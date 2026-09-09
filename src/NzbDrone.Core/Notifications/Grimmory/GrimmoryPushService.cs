using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class GrimmoryPushService : IExecute<PushGrimmoryMetadataCommand>, IHandle<MediaCoversUpdatedEvent>
    {
        public static readonly string[] AllFields =
        {
            "cover", "title", "subtitle", "authors", "series", "description",
            "publisher", "publisheddate", "language", "tags", "identifiers"
        };

        private static readonly TimeSpan WaitForBookTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan WaitForBookInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan AutoPushCooldown = TimeSpan.FromMinutes(5);

        private readonly INotificationFactory _notificationFactory;
        private readonly IGrimmoryProxy _proxy;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ICached<DateTime> _recentAutoPushes;
        private readonly Logger _logger;

        public GrimmoryPushService(INotificationFactory notificationFactory,
                                   IGrimmoryProxy proxy,
                                   IBookService bookService,
                                   IAuthorService authorService,
                                   IEditionService editionService,
                                   IMediaFileService mediaFileService,
                                   IRootFolderService rootFolderService,
                                   IMapCoversToLocal coverMapper,
                                   IManageCommandQueue commandQueueManager,
                                   ICacheManager cacheManager,
                                   Logger logger)
        {
            _notificationFactory = notificationFactory;
            _proxy = proxy;
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _coverMapper = coverMapper;
            _commandQueueManager = commandQueueManager;
            _recentAutoPushes = cacheManager.GetCache<DateTime>(GetType(), "recentAutoPushes");
            _logger = logger;
        }

        public static List<string> ToggleFields(GrimmorySettings settings)
        {
            var fields = new List<string>();

            if (settings.PushCovers)
            {
                fields.Add("cover");
            }

            if (settings.PushMetadata)
            {
                fields.AddRange(AllFields.Where(f => f != "cover"));
            }

            return fields;
        }

        // Fires when a book's cover/metadata is updated in Chaptarr (e.g. through the UI).
        // Author-scoped cover events are deliberately ignored - they fire during routine
        // author refreshes and would fan out into pushes for every book of the author.
        public void Handle(MediaCoversUpdatedEvent message)
        {
            var book = message.Book;

            if (book == null)
            {
                return;
            }

            var fields = _notificationFactory.GetAvailableProviders()
                .OfType<Grimmory>()
                .Select(g => g.Definition?.Settings as GrimmorySettings)
                .Where(s => s != null)
                .SelectMany(ToggleFields)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (fields.Empty())
            {
                return;
            }

            var cacheKey = book.Id.ToString();

            if (_recentAutoPushes.Find(cacheKey) != default)
            {
                return;
            }

            _recentAutoPushes.Set(cacheKey, DateTime.UtcNow, AutoPushCooldown);

            _commandQueueManager.Push(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { book.Id },
                Fields = fields
            });
        }

        public void Execute(PushGrimmoryMetadataCommand message)
        {
            var bookIds = message.BookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            var fields = message.Fields?.Any() == true ? message.Fields : AllFields.ToList();

            var connections = _notificationFactory.GetAvailableProviders()
                .OfType<Grimmory>()
                .Where(g => g.Definition?.Settings is GrimmorySettings)
                .ToList();

            if (!connections.Any())
            {
                _logger.Debug("No enabled Grimmory connections; nothing to push");
                return;
            }

            var pushed = 0;
            var failed = 0;

            foreach (var bookId in bookIds)
            {
                try
                {
                    if (PushBook(bookId, fields, connections, message.WaitForBook))
                    {
                        pushed++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Warn(ex, "Failed to push book {0} to Grimmory", bookId);
                }
            }

            _logger.Info("Pushed {0} of {1} book(s) to Grimmory", pushed, bookIds.Count);

            if (failed > 0 && pushed == 0)
            {
                throw new InvalidOperationException($"Failed to push {failed} book(s) to Grimmory");
            }
        }

        private bool PushBook(int bookId, List<string> fields, List<Grimmory> connections, bool waitForBook)
        {
            var book = _bookService.GetBook(bookId);

            if (book == null)
            {
                return false;
            }

            var files = _mediaFileService.GetFilesByBook(bookId)
                .Where(f => f?.Path.IsNotNullOrWhiteSpace() == true)
                .ToList();

            if (!files.Any())
            {
                _logger.Debug("No files on disk for '{0}'; nothing to push to Grimmory", book.Title);
                return false;
            }

            var author = _authorService.GetAuthor(book.AuthorId);
            var edition = ResolveEdition(book);
            var anyPushed = false;

            foreach (var connection in connections)
            {
                var settings = (GrimmorySettings)connection.Definition.Settings;
                var libraryId = book.MediaType == BookMediaType.Ebook ? settings.EbookLibraryId : settings.AudiobookLibraryId;

                if (libraryId <= 0)
                {
                    continue;
                }

                var grimmoryBook = FindGrimmoryBook(settings, libraryId, files, waitForBook);

                if (grimmoryBook == null)
                {
                    _logger.Debug("'{0}' not found in Grimmory library {1} on {2}; skipping", book.Title, libraryId, settings.Url);
                    continue;
                }

                var metadata = BuildMetadata(book, author, edition, fields);

                if (metadata.Any())
                {
                    _proxy.UpdateBookMetadata(settings, grimmoryBook.Id, metadata);
                }

                if (fields.Contains("cover", StringComparer.OrdinalIgnoreCase))
                {
                    PushCover(settings, grimmoryBook.Id, book, edition);
                }

                _logger.Debug("Pushed '{0}' to Grimmory book {1} on {2}", book.Title, grimmoryBook.Id, settings.Url);
                anyPushed = true;
            }

            return anyPushed;
        }

        private GrimmoryBook FindGrimmoryBook(GrimmorySettings settings, long libraryId, List<BookFile> files, bool waitForBook)
        {
            var deadline = waitForBook ? DateTime.UtcNow + WaitForBookTimeout : DateTime.UtcNow;
            var bypassCache = false;

            while (true)
            {
                foreach (var file in files)
                {
                    var relativePath = GetRootRelativePath(file.Path);

                    if (relativePath.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var grimmoryBook = _proxy.FindBookByPath(settings, libraryId, relativePath, bypassCache);

                    if (grimmoryBook != null)
                    {
                        return grimmoryBook;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                // Freshly imported books only exist in Grimmory once its (async) refresh has
                // scanned them, so re-fetch the library list until the book shows up.
                Thread.Sleep(WaitForBookInterval);
                bypassCache = true;
            }
        }

        private string GetRootRelativePath(string path)
        {
            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            if (rootFolder?.Path == null || rootFolder.Path.PathEquals(path))
            {
                return null;
            }

            return rootFolder.Path.GetRelativePath(path);
        }

        private Edition ResolveEdition(Book book)
        {
            var editions = _editionService.GetEditionsByBook(book.Id);

            return editions.FirstOrDefault(e => e.Monitored) ?? editions.FirstOrDefault();
        }

        private Dictionary<string, object> BuildMetadata(Book book, Author author, Edition edition, List<string> fields)
        {
            var metadata = new Dictionary<string, object>();
            var wanted = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

            void Add(string field, string grimmoryField, object value)
            {
                if (!wanted.Contains(field) || value == null || (value is string s && s.IsNullOrWhiteSpace()))
                {
                    return;
                }

                metadata[grimmoryField] = value;
                metadata[$"{grimmoryField}Locked"] = true;
            }

            Add("title", "title", edition?.Title ?? book.Title);
            Add("description", "description", edition?.Overview ?? book.Overview);
            Add("publisher", "publisher", edition?.Publisher);
            Add("language", "language", edition?.Language);

            if (wanted.Contains("publisheddate") && book.ReleaseDate.HasValue && book.ReleaseDate.Value > DateTime.MinValue)
            {
                metadata["publishedDate"] = book.ReleaseDate.Value.ToString("yyyy-MM-dd");
                metadata["publishedDateLocked"] = true;
            }

            if (wanted.Contains("authors") && author?.Name.IsNotNullOrWhiteSpace() == true)
            {
                metadata["authors"] = new List<string> { author.Name };
            }

            if (wanted.Contains("series"))
            {
                var seriesLink = book.SeriesLinks?.FirstOrDefault(l => l?.Series?.Value?.Title.IsNotNullOrWhiteSpace() == true);

                if (seriesLink != null)
                {
                    metadata["seriesName"] = seriesLink.Series.Value.Title;
                    metadata["seriesNameLocked"] = true;

                    if (double.TryParse(seriesLink.Position, out var position))
                    {
                        metadata["seriesNumber"] = position;
                        metadata["seriesNumberLocked"] = true;
                    }
                }
            }

            if (wanted.Contains("tags") && book.Genres?.Any() == true)
            {
                metadata["categories"] = book.Genres;
            }

            if (wanted.Contains("identifiers"))
            {
                if (edition?.Isbn13.IsNotNullOrWhiteSpace() == true)
                {
                    metadata["isbn13"] = edition.Isbn13;
                    metadata["isbn13Locked"] = true;
                }

                if (edition?.Asin.IsNotNullOrWhiteSpace() == true)
                {
                    metadata["asin"] = edition.Asin;
                    metadata["asinLocked"] = true;
                }

                if (edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
                {
                    metadata["goodreadsId"] = edition.ForeignEditionId;
                    metadata["goodreadsIdLocked"] = true;
                }
            }

            return metadata;
        }

        private void PushCover(GrimmorySettings settings, long grimmoryBookId, Book book, Edition edition)
        {
            var cover = (edition?.Images ?? book.Images)?.FirstOrDefault(i => i.CoverType == MediaCoverTypes.Cover);

            if (cover == null)
            {
                _logger.Debug("No cover known for '{0}'; skipping cover push", book.Title);
                return;
            }

            var coverPath = _coverMapper.GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension);

            if (coverPath.IsNullOrWhiteSpace() || !File.Exists(coverPath))
            {
                _logger.Debug("Cover file for '{0}' not present at {1}; skipping cover push", book.Title, coverPath);
                return;
            }

            _proxy.UploadBookCover(settings, grimmoryBookId, File.ReadAllBytes(coverPath), Path.GetFileName(coverPath));
        }
    }
}
