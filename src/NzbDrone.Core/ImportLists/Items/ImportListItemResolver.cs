using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Items
{
    public static class ImportListItemResolver
    {
        public static List<Book> MatchBooks(List<Book> books, ImportListItemInfo item)
        {
            var bookProviderId = NormalizeProviderId(item.BookProviderId);
            var matches = books.Where(b => HasProviderId(b, bookProviderId)).ToList();

            if (matches.Any())
            {
                return matches;
            }

            var editionProviderId = NormalizeProviderId(item.EditionProviderId);

            if (editionProviderId != null && !editionProviderId.Equals(bookProviderId, StringComparison.OrdinalIgnoreCase))
            {
                matches = books.Where(b => HasProviderId(b, editionProviderId) || HasEdition(b, e => MatchesEditionId(e, editionProviderId))).ToList();

                if (matches.Any())
                {
                    return ExpandToSiblingWorkRows(books, matches);
                }
            }

            var isbn13 = NormalizeIsbn13(item.Isbn13);
            var asin = item.Asin?.Trim();

            if (isbn13 != null || asin.IsNotNullOrWhiteSpace())
            {
                matches = books.Where(b => HasIdentifier(b, isbn13, asin)).ToList();

                if (matches.Any())
                {
                    return ExpandToSiblingWorkRows(books, matches);
                }
            }

            matches = MatchByTitle(books, item.Book);

            return matches.Select(WorkKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? ExpandToSiblingWorkRows(books, matches)
                : new List<Book>();
        }

        public static ImportListItem Build(ImportListItemInfo item, Author author, List<Book> books, Func<int, bool> hasFiles, Func<int, bool> isQueued, PendingAuthorImport pendingAuthor, bool excluded)
        {
            books = books.OrderBy(b => b.MediaType).ToList();

            var listDate = item.ReleaseDate > DateTime.MinValue ? item.ReleaseDate : (DateTime?)null;
            var primary = books.FirstOrDefault(b => hasFiles(b.Id)) ?? books.FirstOrDefault();

            var result = new ImportListItem
            {
                Title = item.Book,
                Subtitle = primary?.Subtitle,
                Author = author?.Name ?? item.Author,
                ReleaseDate = books.Select(b => b.ReleaseDate).FirstOrDefault(d => d.HasValue) ?? item.PublishedDate,
                BookProviderId = NormalizeProviderId(item.BookProviderId),
                EditionProviderId = NormalizeProviderId(item.EditionProviderId),
                DateAdded = listDate ?? (books.Any() ? books.Min(b => b.Added) : (DateTime?)null),
                DateAddedFromList = listDate.HasValue,
                BookId = primary?.Id,
                Images = primary?.Images ?? new(),
                Media = books.Select(b => new ImportListItemMediaState
                {
                    MediaType = b.MediaType,
                    BookId = b.Id,
                    State = MediaState(b, author, hasFiles(b.Id), isQueued(b.Id))
                }).ToList()
            };

            if (excluded)
            {
                result.State = ImportListItemState.Excluded;
            }
            else if (author == null)
            {
                result.State = pendingAuthor == null ? ImportListItemState.NotInLibrary : ImportListItemState.PendingAuthor;
                result.PendingError = pendingAuthor?.LastError;
            }
            else
            {
                result.State = result.Media.Any() ? result.Media.Max(m => m.State) : ImportListItemState.NotInLibrary;
            }

            return result;
        }

        internal static string NormalizeProviderId(string value)
        {
            return ImportListProviderIdHelper.Normalize(value, "gr");
        }

        private static ImportListItemState MediaState(Book book, Author author, bool hasFiles, bool queued)
        {
            if (hasFiles)
            {
                return ImportListItemState.Downloaded;
            }

            if (queued)
            {
                return ImportListItemState.Queued;
            }

            return book.IsMonitoredWithAuthor(author) ? ImportListItemState.Missing : ImportListItemState.Unmonitored;
        }

        private static List<Book> MatchByTitle(List<Book> books, string title)
        {
            var wanted = NormalizeTitle(title);

            if (wanted == null)
            {
                return new List<Book>();
            }

            var exact = books.Where(b => wanted.Equals(NormalizeTitle(b.Title), StringComparison.OrdinalIgnoreCase)).ToList();

            return exact.Any()
                ? exact
                : books.Where(b => wanted.Equals(NormalizeTitle(TitleBeforeSubtitle(b.Title)), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static string TitleBeforeSubtitle(string title)
        {
            var colon = title?.IndexOf(':') ?? -1;

            return colon > 0 ? title.Substring(0, colon) : null;
        }

        private static string NormalizeTitle(string title)
        {
            var normalized = title?.CleanBookTitle().CleanAuthorName();

            return normalized.IsNullOrWhiteSpace() ? null : normalized;
        }

        private static List<Book> ExpandToSiblingWorkRows(List<Book> books, List<Book> matches)
        {
            var workIds = new HashSet<string>(matches.SelectMany(BookEditionIdentity.GetCanonicalWorkProviderIds), StringComparer.OrdinalIgnoreCase);

            return workIds.Count == 0
                ? matches
                : books.Where(b => matches.Contains(b) || BookEditionIdentity.GetCanonicalWorkProviderIds(b).Any(workIds.Contains)).ToList();
        }

        private static string WorkKey(Book book)
        {
            return BookEditionIdentity.GetCanonicalWorkProviderIds(book).FirstOrDefault() ?? $"book:{book.Id}";
        }

        private static bool HasProviderId(Book book, string providerId)
        {
            return providerId != null &&
                   (BookIdentity.GetProviderIdentityTokens(book).Contains(providerId) ||
                    BookEditionIdentity.HasCanonicalWorkProviderId(book, providerId) ||
                    BookEditionIdentity.HasCanonicalEditionProviderId(book, providerId) ||
                    MatchesProviderId(book.ForeignEditionId, providerId));
        }

        private static bool HasEdition(Book book, Func<Edition, bool> predicate)
        {
            return book.Editions?.Any(predicate) == true;
        }

        private static bool MatchesEditionId(Edition edition, string providerId)
        {
            return MatchesProviderId(edition.ForeignEditionId, providerId) ||
                   (edition.GoodreadsEditionId > 0 && providerId.Equals($"gr:{edition.GoodreadsEditionId}", StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesProviderId(string foreignEditionId, string providerId)
        {
            var candidate = StripMediaTypeSuffix(foreignEditionId);

            return candidate != null &&
                   (candidate.Equals(providerId, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals(providerId.Substring(providerId.IndexOf(':') + 1), StringComparison.OrdinalIgnoreCase));
        }

        private static string StripMediaTypeSuffix(string foreignEditionId)
        {
            if (foreignEditionId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var suffix = new[] { "-ebook", "-audiobook" }.FirstOrDefault(s => foreignEditionId.EndsWith(s, StringComparison.OrdinalIgnoreCase));

            return suffix == null ? foreignEditionId : foreignEditionId.Substring(0, foreignEditionId.Length - suffix.Length);
        }

        private static bool HasIdentifier(Book book, string isbn13, string asin)
        {
            if (isbn13 != null && (NormalizeIsbn13(book.ISBN13) == isbn13 || HasEdition(book, e => NormalizeIsbn13(e.Isbn13) == isbn13)))
            {
                return true;
            }

            return asin.IsNotNullOrWhiteSpace() &&
                   (SameAsin(book.ASIN, asin) || HasEdition(book, e => SameAsin(e.Asin, asin) || SameAsin(e.AudibleASIN, asin)));
        }

        private static bool SameAsin(string candidate, string asin)
        {
            return string.Equals(candidate?.Trim(), asin, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeIsbn13(string value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

            return digits.Length == 13 ? digits : null;
        }
    }
}
