using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Items
{
    public static class ImportListItemResolver
    {
        public static List<Book> MatchBooks(IReadOnlyCollection<Book> authorBooks, ImportListItemInfo item)
        {
            var books = authorBooks?.Where(b => b != null).ToList() ?? new List<Book>();

            if (books.Count == 0 || item == null)
            {
                return new List<Book>();
            }

            var bookProviderId = NormalizeProviderId(item.BookProviderId);
            var matches = books.Where(b => HasProviderId(b, bookProviderId)).ToList();

            if (matches.Any())
            {
                return matches;
            }

            var editionProviderId = NormalizeProviderId(item.EditionProviderId);

            if (editionProviderId.IsNotNullOrWhiteSpace() &&
                !editionProviderId.Equals(bookProviderId, StringComparison.OrdinalIgnoreCase))
            {
                matches = books
                    .Where(b => HasProviderId(b, editionProviderId) ||
                                HasEdition(b, edition => MatchesProviderId(StripMediaTypeSuffix(edition.ForeignEditionId), editionProviderId) ||
                                                         MatchesGoodreadsEditionId(edition, editionProviderId)))
                    .ToList();

                if (matches.Any())
                {
                    return ExpandToSiblingWorkRows(books, matches);
                }
            }

            var isbn13 = NormalizeIsbn13(item.Isbn13);
            var asin = item.Asin?.Trim();

            if (isbn13.IsNotNullOrWhiteSpace() || asin.IsNotNullOrWhiteSpace())
            {
                matches = books.Where(b => HasIdentifier(b, isbn13, asin)).ToList();

                if (matches.Any())
                {
                    return ExpandToSiblingWorkRows(books, matches);
                }
            }

            matches = MatchByTitle(books, item.Book);

            if (matches.Any() && matches.Select(WorkKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                return ExpandToSiblingWorkRows(books, matches);
            }

            return new List<Book>();
        }

        public static ImportListItem Build(ImportListItemInfo item,
                                           Author author,
                                           IReadOnlyCollection<Book> matchedBooks,
                                           Func<int, bool> hasFiles,
                                           Func<int, bool> isQueued,
                                           Func<int, TrackedDownloadState?> queueState,
                                           Func<int, ImportListItemSeries> series,
                                           PendingAuthorImport pendingAuthor,
                                           bool excluded)
        {
            var result = new ImportListItem
            {
                Title = item?.Book,
                Author = author?.Name ?? item?.Author,
                SeriesTitle = item?.Series,
                SeriesPosition = item?.SeriesPosition,
                ReleaseDate = item?.PublishedDate,
                AuthorProviderId = NormalizeProviderId(item?.AuthorProviderId),
                BookProviderId = NormalizeProviderId(item?.BookProviderId),
                EditionProviderId = NormalizeProviderId(item?.EditionProviderId),
                Isbn13 = NormalizeIsbn13(item?.Isbn13),
                AuthorId = author?.Id,
                DateAdded = item != null && item.ReleaseDate > DateTime.MinValue ? item.ReleaseDate : null
            };

            var books = matchedBooks?.Where(b => b != null).OrderBy(b => b.MediaType).ToList() ?? new List<Book>();

            foreach (var book in books)
            {
                var files = hasFiles?.Invoke(book.Id) == true;
                var queued = isQueued?.Invoke(book.Id) == true;
                var monitored = author != null && book.IsMonitoredWithAuthor(author);

                result.Media.Add(new ImportListItemMediaState
                {
                    MediaType = book.MediaType,
                    BookId = book.Id,
                    Monitored = monitored,
                    HasFiles = files,
                    QueueState = queued ? queueState?.Invoke(book.Id) : null,
                    State = files ? ImportListItemState.Downloaded
                        : queued ? ImportListItemState.Queued
                        : monitored ? ImportListItemState.Missing
                        : ImportListItemState.Unmonitored
                });
            }

            var primary = books.FirstOrDefault(b => hasFiles?.Invoke(b.Id) == true) ?? books.FirstOrDefault();

            if (primary != null)
            {
                result.BookId = primary.Id;
                result.TitleSlug = primary.TitleSlug;
                result.Subtitle = primary.Subtitle;
                result.Images = primary.Images?.Where(i => i != null).ToList() ?? new List<MediaCover.MediaCover>();
                result.DateAdded ??= books.Min(b => b.Added);
                result.ReleaseDate = books.Select(b => b.ReleaseDate).FirstOrDefault(d => d.HasValue) ?? result.ReleaseDate;

                var librarySeries = books.Select(b => series?.Invoke(b.Id)).FirstOrDefault(s => s != null && s.Title.IsNotNullOrWhiteSpace());

                if (librarySeries != null)
                {
                    result.SeriesTitle = librarySeries.Title;
                    result.SeriesPosition = librarySeries.Position;
                }
            }

            if (excluded)
            {
                result.State = ImportListItemState.Excluded;
            }
            else if (author == null)
            {
                result.State = pendingAuthor != null ? ImportListItemState.PendingAuthor : ImportListItemState.NotInLibrary;
                result.PendingError = pendingAuthor?.LastError;
            }
            else if (!result.Media.Any())
            {
                result.State = ImportListItemState.NotInLibrary;
            }
            else
            {
                result.State = result.Media.Max(m => m.State);
            }

            return result;
        }

        private static List<Book> MatchByTitle(List<Book> books, string title)
        {
            var wanted = NormalizeTitle(title);

            if (wanted.IsNullOrWhiteSpace())
            {
                return new List<Book>();
            }

            var exact = books
                .Where(b => string.Equals(NormalizeTitle(b.Title), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Any())
            {
                return exact;
            }

            return books
                .Where(b => string.Equals(NormalizeTitle(TitleBeforeSubtitle(b.Title)), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static string TitleBeforeSubtitle(string title)
        {
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var colon = title.IndexOf(':');

            return colon > 0 ? title.Substring(0, colon) : null;
        }

        private static string NormalizeTitle(string title)
        {
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var normalized = title.CleanBookTitle().CleanAuthorName();

            return normalized.IsNullOrWhiteSpace() ? null : normalized;
        }

        private static List<Book> ExpandToSiblingWorkRows(List<Book> books, List<Book> matches)
        {
            var workIds = new HashSet<string>(
                matches.SelectMany(BookEditionIdentity.GetCanonicalWorkProviderIds),
                StringComparer.OrdinalIgnoreCase);

            if (workIds.Count == 0)
            {
                return matches;
            }

            return books
                .Where(b => matches.Contains(b) || BookEditionIdentity.GetCanonicalWorkProviderIds(b).Any(workIds.Contains))
                .ToList();
        }

        private static string WorkKey(Book book)
        {
            return BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault() ?? $"book:{book.Id}";
        }

        private static bool HasProviderId(Book book, string providerId)
        {
            if (book == null || providerId.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(providerId))
            {
                return true;
            }

            return BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                   BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId) ||
                   MatchesProviderId(StripMediaTypeSuffix(book.ForeignEditionId), providerId);
        }

        private static bool HasEdition(Book book, Func<Edition, bool> predicate)
        {
            return book?.Editions?.Any(edition => edition != null && predicate(edition)) == true;
        }

        private static bool MatchesGoodreadsEditionId(Edition edition, string providerId)
        {
            if (!edition.GoodreadsEditionId.HasValue || edition.GoodreadsEditionId.Value <= 0)
            {
                return false;
            }

            return string.Equals($"gr:{edition.GoodreadsEditionId.Value}", providerId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasIdentifier(Book book, string isbn13, string asin)
        {
            if (isbn13.IsNotNullOrWhiteSpace())
            {
                if (string.Equals(NormalizeIsbn13(book.ISBN13), isbn13, StringComparison.Ordinal) ||
                    HasEdition(book, edition => string.Equals(NormalizeIsbn13(edition.Isbn13), isbn13, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            if (asin.IsNotNullOrWhiteSpace())
            {
                if (string.Equals(book.ASIN?.Trim(), asin, StringComparison.OrdinalIgnoreCase) ||
                    HasEdition(book, edition => string.Equals(edition.Asin?.Trim(), asin, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(edition.AudibleASIN?.Trim(), asin, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesProviderId(string candidate, string providerId)
        {
            if (candidate.IsNullOrWhiteSpace() || providerId.IsNullOrWhiteSpace())
            {
                return false;
            }

            candidate = candidate.Trim();

            if (candidate.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var separator = providerId.IndexOf(':');
            var rawId = separator > 0 && separator < providerId.Length - 1 ? providerId.Substring(separator + 1) : null;

            return rawId.IsNotNullOrWhiteSpace() && candidate.Equals(rawId, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripMediaTypeSuffix(string foreignEditionId)
        {
            if (foreignEditionId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var value = foreignEditionId.Trim();

            foreach (var suffix in new[] { "-ebook", "-audiobook" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring(0, value.Length - suffix.Length);
                }
            }

            return value;
        }

        private static string NormalizeIsbn13(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());

            return digits.Length == 13 ? digits : null;
        }

        private static string NormalizeProviderId(string value)
        {
            return value.IsNullOrWhiteSpace() ? null : ImportListProviderIdHelper.Normalize(value, "gr");
        }
    }
}
