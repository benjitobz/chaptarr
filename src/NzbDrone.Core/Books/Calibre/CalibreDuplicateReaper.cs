using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authors;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreDuplicateReaper : IHandle<AuthorScannedEvent>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly Logger _logger;

        public CalibreDuplicateReaper(IRootFolderService rootFolderService,
                                      IBookService bookService,
                                      IMediaFileService mediaFileService,
                                      ICalibreProxy calibreProxy,
                                      Logger logger)
        {
            _rootFolderService = rootFolderService;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _calibreProxy = calibreProxy;
            _logger = logger;
        }

        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;

            if (author == null || author.Path.IsNullOrWhiteSpace())
            {
                return;
            }

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(author.Path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", author.Path);
                return;
            }

            if (rootFolder == null ||
                !rootFolder.IsCalibreLibrary ||
                rootFolder.CalibreSettings == null ||
                !rootFolder.ReapCalibreDuplicates)
            {
                return;
            }

            var canonicalIds = _bookService.GetBooksByAuthor(author.Id)
                .Where(b => b != null && b.Id > 0)
                .SelectMany(b => _mediaFileService.GetFilesByBook(b.Id))
                .Where(f => f != null && f.CalibreId > 0)
                .Select(f => f.CalibreId)
                .Distinct()
                .ToList();

            if (!canonicalIds.Any())
            {
                return;
            }

            List<CalibreBook> libraryBooks;

            try
            {
                libraryBooks = _calibreProxy.GetAllBooks(rootFolder.CalibreSettings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to enumerate the calibre library for duplicate reaping");
                return;
            }

            foreach (var canonicalId in canonicalIds)
            {
                ReapDuplicateRecords(canonicalId, libraryBooks, rootFolder.CalibreSettings);
            }
        }

        private void ReapDuplicateRecords(int canonicalId, List<CalibreBook> libraryBooks, CalibreSettings settings)
        {
            if (libraryBooks == null || libraryBooks.Count == 0)
            {
                return;
            }

            var canonical = libraryBooks.FirstOrDefault(b => b.Id == canonicalId);

            if (canonical == null)
            {
                return;
            }

            var canonicalTitle = NormalizeTitle(canonical.Title);
            var canonicalAuthors = AuthorTokens(canonical);
            var canonicalFormats = FormatKeys(canonical);

            if (canonicalTitle.Length <= 4 || canonicalAuthors.Count == 0)
            {
                return;
            }

            var duplicates = new List<int>();

            foreach (var candidate in libraryBooks)
            {
                if (candidate == null || candidate.Id == canonicalId)
                {
                    continue;
                }

                if (!AuthorTokens(candidate).Overlaps(canonicalAuthors))
                {
                    continue;
                }

                if (!IsBrandedVariantOf(candidate, canonicalTitle))
                {
                    continue;
                }

                if (!FormatKeys(candidate).IsSubsetOf(canonicalFormats))
                {
                    _logger.Debug("Keeping duplicate calibre record {0}; it carries formats the canonical {1} lacks", candidate.Id, canonicalId);
                    continue;
                }

                duplicates.Add(candidate.Id);
            }

            if (!duplicates.Any())
            {
                return;
            }

            try
            {
                _calibreProxy.DeleteBookIds(duplicates, settings);
                _logger.Info("Removed {0} duplicate calibre record(s) for canonical book {1}: {2}", duplicates.Count, canonicalId, string.Join(",", duplicates));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to remove duplicate calibre records for canonical book {0}", canonicalId);
            }
        }

        private static bool IsBrandedVariantOf(CalibreBook candidate, string canonicalTitle)
        {
            var candidateTitle = NormalizeTitle(candidate?.Title);

            if (candidateTitle.Length == 0)
            {
                return false;
            }

            return candidateTitle.Equals(canonicalTitle, StringComparison.Ordinal) ||
                   candidateTitle.EndsWith(" " + canonicalTitle, StringComparison.Ordinal);
        }

        private static HashSet<string> AuthorTokens(CalibreBook book)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var authors = book?.Authors ?? new List<string>();

            if (book?.AuthorSort.IsNotNullOrWhiteSpace() == true)
            {
                authors = new List<string>(authors) { book.AuthorSort };
            }

            foreach (var author in authors)
            {
                if (author.IsNullOrWhiteSpace())
                {
                    continue;
                }

                foreach (var token in NormalizeTitle(author).Split(' '))
                {
                    if (token.Length > 1)
                    {
                        tokens.Add(token);
                    }
                }
            }

            return tokens;
        }

        private static HashSet<string> FormatKeys(CalibreBook book)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (book?.Formats != null)
            {
                foreach (var key in book.Formats.Keys)
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static string NormalizeTitle(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var cleaned = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9 ]", " ");
            return Regex.Replace(cleaned, "\\s+", " ").Trim();
        }
    }
}
