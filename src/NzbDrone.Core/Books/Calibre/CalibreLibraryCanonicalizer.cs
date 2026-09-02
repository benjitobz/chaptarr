using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreLibraryCanonicalizer : IHandle<AuthorScannedEvent>, IHandle<BookFileAddedEvent>, IExecute<CanonicalizeCalibreLibraryCommand>, IExecute<CanonicalizeCalibreBookCommand>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMediaFileService _mediaFileService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public CalibreLibraryCanonicalizer(IRootFolderService rootFolderService,
                                           IAuthorService authorService,
                                           IBookService bookService,
                                           IEditionService editionService,
                                           IManageCommandQueue commandQueueManager,
                                           IEventAggregator eventAggregator,
                                           IMediaFileService mediaFileService,
                                           ICalibreProxy calibreProxy,
                                           IConfigService configService,
                                           Logger logger)
        {
            _rootFolderService = rootFolderService;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _mediaFileService = mediaFileService;
            _calibreProxy = calibreProxy;
            _configService = configService;
            _logger = logger;
        }

        public void Handle(AuthorScannedEvent message)
        {
            CanonicalizeAuthor(message.Author);
        }

        public void Execute(CanonicalizeCalibreLibraryCommand message)
        {
            Author author;

            try
            {
                author = _authorService.GetAuthor(message.AuthorId);
            }
            catch (Exception)
            {
                return;
            }

            CanonicalizeAuthor(author);
        }

        public void Execute(CanonicalizeCalibreBookCommand message)
        {
            var book = _bookService.GetBook(message.BookId);

            if (book == null)
            {
                return;
            }

            var author = _authorService.GetAuthor(book.AuthorId);

            if (author == null || !TryGetCalibreRootFolder(author, out var settings))
            {
                return;
            }

            var files = _mediaFileService.GetFilesByBook(book.Id)
                .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                .ToList();

            var canonicalized = ProcessFiles(author, settings, files);

            if (canonicalized > 0)
            {
                _logger.Info("Canonicalized calibre metadata for '{0}'", book.Title);
            }
        }

        public void Handle(BookFileAddedEvent message)
        {
            // Ingest attaches can land without a trailing author scan, so queue a
            // canonicalize pass per book; per-book scoping keeps a late-attaching
            // sibling from being swallowed by the dedupe of an in-flight author pass.
            if (!_configService.CanonicalizeCalibreLibraryMetadata)
            {
                return;
            }

            var bookFile = message.BookFile;
            var book = bookFile?.Edition?.Book;

            if (book == null && bookFile != null && bookFile.EditionId > 0)
            {
                book = _editionService.GetEdition(bookFile.EditionId)?.Book;
            }

            if (book == null || book.Id <= 0)
            {
                return;
            }

            _commandQueueManager.Push(new CanonicalizeCalibreBookCommand { BookId = book.Id });
        }

        private void CanonicalizeAuthor(Author author)
        {
            if (author == null || !TryGetCalibreRootFolder(author, out var settings))
            {
                return;
            }

            var files = _mediaFileService.GetFilesByAuthor(author.Id)
                .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                .ToList();

            var canonicalized = ProcessFiles(author, settings, files);

            if (canonicalized > 0)
            {
                _logger.Info("Canonicalized calibre metadata for {0} book(s) under {1}", canonicalized, author.Name);
            }
        }

        private bool TryGetCalibreRootFolder(Author author, out CalibreSettings settings)
        {
            settings = null;

            if (author.Path.IsNullOrWhiteSpace() || !_configService.CanonicalizeCalibreLibraryMetadata)
            {
                return false;
            }

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(author.Path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", author.Path);
                return false;
            }

            if (rootFolder == null || !rootFolder.IsCalibreLibrary || rootFolder.CalibreSettings == null)
            {
                return false;
            }

            if (!rootFolder.CanonicalizeCalibreMetadata)
            {
                return false;
            }

            settings = rootFolder.CalibreSettings;
            return true;
        }

        private int ProcessFiles(Author author, CalibreSettings settings, List<BookFile> files)
        {
            if (!files.Any())
            {
                return 0;
            }

            var groups = new Dictionary<int, List<BookFile>>();

            foreach (var file in files)
            {
                var calibreId = file.CalibreId;

                if (calibreId == 0)
                {
                    try
                    {
                        calibreId = _calibreProxy.GetCalibreIdForPath(file.Path, settings);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to resolve calibre id for {0}", file.Path);
                        continue;
                    }

                    if (calibreId == 0)
                    {
                        continue;
                    }

                    file.CalibreId = calibreId;
                    _mediaFileService.Update(file);
                }

                if (!groups.TryGetValue(calibreId, out var group))
                {
                    groups[calibreId] = group = new List<BookFile>();
                }

                group.Add(file);
            }

            var canonicalized = 0;
            var renamedFiles = new List<RenamedBookFile>();

            foreach (var pair in groups)
            {
                try
                {
                    if (CanonicalizeBook(pair.Key, pair.Value, settings, renamedFiles))
                    {
                        canonicalized++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to canonicalize calibre book {0} for {1}", pair.Key, author.Name);
                }
            }

            if (renamedFiles.Any())
            {
                // Calibre renamed folders and files during the stamp; announce it like any
                // other rename so connections (e.g. AudioBookShelf) track the move instead
                // of seeing a vanished folder plus an unrelated new one.
                _eventAggregator.PublishEvent(new AuthorRenamedEvent(author, renamedFiles));
            }

            return canonicalized;
        }

        private bool CanonicalizeBook(int calibreId, List<BookFile> files, CalibreSettings settings, List<RenamedBookFile> renamedFiles)
        {
            var reference = files.FirstOrDefault(f => f.Edition?.Title.IsNotNullOrWhiteSpace() == true);

            if (reference == null)
            {
                return false;
            }

            var calibreBook = _calibreProxy.GetBook(calibreId, settings);

            if (calibreBook == null)
            {
                return false;
            }

            var canonicalTitle = reference.Edition.Title;
            var canonicalAuthor = reference.Author?.Name;
            var titleMatches = calibreBook.Title != null &&
                               calibreBook.Title.Equals(canonicalTitle, StringComparison.OrdinalIgnoreCase);
            var authorMatches = canonicalAuthor.IsNullOrWhiteSpace() ||
                                (calibreBook.Authors?.Any(a => a.Equals(canonicalAuthor, StringComparison.OrdinalIgnoreCase)) ?? false);

            var book = reference.Edition.Book;
            var chosenSeries = CalibreSeriesSelector.Select(book)?.Series.Value.Title;
            var calibreSeries = calibreBook.Series;

            // Re-stamp a series only when calibre still holds one of this book's own
            // provider series; an unrecognized name is a manual edit and stays untouched.
            var seriesNeedsStamp = chosenSeries.IsNotNullOrWhiteSpace() &&
                                   !chosenSeries.Equals(calibreSeries ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                                   (calibreSeries.IsNullOrWhiteSpace() || CalibreSeriesSelector.KnownSeriesTitles(book).Contains(calibreSeries));

            if (titleMatches && authorMatches && !seriesNeedsStamp)
            {
                return false;
            }

            _calibreProxy.SetFields(reference, settings, updateCover: true, embed: true);
            RefreshTrackedPaths(calibreId, files, settings, renamedFiles);
            RewriteOpfTitle(files, canonicalTitle);
            return true;
        }

        private void RewriteOpfTitle(List<BookFile> files, string canonicalTitle)
        {
            // The headless content server never refreshes calibre's per-book metadata.opf
            // backups, and Audiobookshelf trusts those files first, so keep the title in
            // step ourselves after a canonicalization pass.
            var folder = files
                .Select(f => Path.GetDirectoryName(f.Path))
                .FirstOrDefault(d => d.IsNotNullOrWhiteSpace());

            if (folder == null)
            {
                return;
            }

            var opfPath = Path.Combine(folder, "metadata.opf");

            try
            {
                if (!File.Exists(opfPath))
                {
                    return;
                }

                var escaped = canonicalTitle
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
                var content = File.ReadAllText(opfPath);
                var updated = Regex.Replace(content, "<dc:title>.*?</dc:title>", "<dc:title>" + escaped + "</dc:title>", RegexOptions.Singleline);

                if (!updated.Equals(content, StringComparison.Ordinal))
                {
                    File.WriteAllText(opfPath, updated);
                    _logger.Debug("Rewrote OPF title for {0}", opfPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to rewrite OPF title at {0}", opfPath);
            }
        }

        private void RefreshTrackedPaths(int calibreId, List<BookFile> files, CalibreSettings settings, List<RenamedBookFile> renamedFiles)
        {
            var refreshed = _calibreProxy.GetBook(calibreId, settings);
            var formats = refreshed?.Formats;

            if (formats == null || formats.Count == 0)
            {
                return;
            }

            var updatedCount = 0;

            foreach (var file in files)
            {
                var extension = (Path.GetExtension(file.Path) ?? string.Empty).TrimStart('.');

                if (extension.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var format = formats.FirstOrDefault(kvp => kvp.Key.Equals(extension, StringComparison.OrdinalIgnoreCase)).Value;

                if (format?.Path == null || format.Path.PathEquals(file.Path))
                {
                    continue;
                }

                if (_mediaFileService.GetFileWithPath(format.Path) != null)
                {
                    // The folder watcher already re-tracked the renamed file; the stale
                    // row this loop holds will be purged by the scan's cleanup instead.
                    continue;
                }

                var previousPath = file.Path;
                file.Path = format.Path;

                try
                {
                    _mediaFileService.Update(file);
                    updatedCount++;
                    renamedFiles.Add(new RenamedBookFile { BookFile = file, PreviousPath = previousPath });
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to update tracked path to {0}", format.Path);
                }
            }

            if (updatedCount > 0)
            {
                _logger.Debug("Updated {0} tracked file path(s) after calibre rename of book {1}", updatedCount, calibreId);
            }
        }
    }
}
