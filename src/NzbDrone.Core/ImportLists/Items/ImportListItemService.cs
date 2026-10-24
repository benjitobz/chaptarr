using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.ImportLists.Items
{
    public interface IImportListItemService
    {
        List<ImportListItem> GetItems(int importListId, bool refresh);
    }

    public class ImportListItemService : IImportListItemService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private readonly IImportListFactory _importListFactory;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IQueueService _queueService;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly ICached<List<ImportListItemInfo>> _cache;
        private readonly Logger _logger;

        public ImportListItemService(IImportListFactory importListFactory,
                                     IAuthorService authorService,
                                     IBookService bookService,
                                     IMediaFileService mediaFileService,
                                     IQueueService queueService,
                                     IPendingAuthorImportService pendingAuthorImportService,
                                     IImportListExclusionService importListExclusionService,
                                     ICacheManager cacheManager,
                                     Logger logger)
        {
            _importListFactory = importListFactory;
            _authorService = authorService;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _queueService = queueService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _importListExclusionService = importListExclusionService;
            _cache = cacheManager.GetCache<List<ImportListItemInfo>>(GetType());
            _logger = logger;
        }

        public List<ImportListItem> GetItems(int importListId, bool refresh)
        {
            var definition = _importListFactory.Get(importListId);
            var cacheKey = importListId.ToString(CultureInfo.InvariantCulture);

            if (refresh)
            {
                _cache.Remove(cacheKey);
            }

            var items = _cache.Get(cacheKey, () => Fetch(definition), CacheDuration);

            return Resolve(items);
        }

        private List<ImportListItemInfo> Fetch(ImportListDefinition definition)
        {
            var importList = _importListFactory.GetInstance(definition);

            if (importList == null)
            {
                return new List<ImportListItemInfo>();
            }

            try
            {
                return importList.Fetch()?.Where(i => i != null).ToList() ?? new List<ImportListItemInfo>();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to fetch items for import list '{0}'", definition.Name);
                return new List<ImportListItemInfo>();
            }
        }

        private List<ImportListItem> Resolve(List<ImportListItemInfo> items)
        {
            var authorsByProviderId = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            var booksByAuthorId = new Dictionary<int, List<Book>>();
            var filesByBookId = new Dictionary<int, bool>();

            var pendingByProviderId = _pendingAuthorImportService.GetAll()
                .Where(p => p?.ProviderId != null)
                .GroupBy(p => NormalizeProviderId(p.ProviderId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First(), StringComparer.OrdinalIgnoreCase);

            var queueByBookId = _queueService.GetQueue()
                .Where(q => q?.Book != null)
                .GroupBy(q => q.Book.Id)
                .ToDictionary(g => g.Key, g => g.First().TrackedDownloadState);

            var exclusions = _importListExclusionService.All();

            Author FindAuthor(string providerId)
            {
                var normalized = NormalizeProviderId(providerId);

                if (normalized.IsNullOrWhiteSpace())
                {
                    return null;
                }

                if (authorsByProviderId.TryGetValue(normalized, out var cached))
                {
                    return cached;
                }

                var separator = normalized.IndexOf(':');
                var prefix = separator > 0 ? normalized.Substring(0, separator) : "gr";
                var rawId = separator > 0 ? normalized.Substring(separator + 1) : normalized;
                var author = _authorService.FindByProviderId(prefix, normalized) ?? _authorService.FindByProviderId(prefix, rawId);

                authorsByProviderId[normalized] = author;

                return author;
            }

            List<Book> BooksFor(Author author)
            {
                if (!booksByAuthorId.TryGetValue(author.Id, out var books))
                {
                    books = _bookService.GetBooksByAuthor(author.Id) ?? new List<Book>();
                    booksByAuthorId[author.Id] = books;
                }

                return books;
            }

            bool HasFiles(int bookId)
            {
                if (!filesByBookId.TryGetValue(bookId, out var hasFiles))
                {
                    hasFiles = _mediaFileService.GetFilesByBook(bookId)?.Any() == true;
                    filesByBookId[bookId] = hasFiles;
                }

                return hasFiles;
            }

            bool IsQueued(int bookId)
            {
                return queueByBookId.ContainsKey(bookId);
            }

            TrackedDownloadState? QueueState(int bookId)
            {
                return queueByBookId.TryGetValue(bookId, out var state) ? state : null;
            }

            var result = new List<ImportListItem>();

            foreach (var item in items)
            {
                var author = FindAuthor(item.AuthorProviderId);
                var matched = author == null ? new List<Book>() : ImportListItemResolver.MatchBooks(BooksFor(author), item);
                var authorProviderId = NormalizeProviderId(item.AuthorProviderId);
                var bookProviderId = NormalizeProviderId(item.BookProviderId);
                var excluded = exclusions.Any(exclusion => IsExcluded(exclusion, authorProviderId, bookProviderId, matched));
                var pending = author == null && authorProviderId != null && pendingByProviderId.TryGetValue(authorProviderId, out var pendingImport)
                    ? pendingImport
                    : null;

                result.Add(ImportListItemResolver.Build(item, author, matched, HasFiles, IsQueued, QueueState, pending, excluded));
            }

            return result
                .OrderByDescending(i => i.DateAdded ?? DateTime.MinValue)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsExcluded(ImportListExclusion exclusion, string authorProviderId, string bookProviderId, List<Book> matchedBooks)
        {
            if (exclusion == null)
            {
                return false;
            }

            if (bookProviderId.IsNotNullOrWhiteSpace() && ImportListExclusionBookMatcher.AppliesToProviderId(exclusion, bookProviderId, null))
            {
                return true;
            }

            if (matchedBooks != null && matchedBooks.Any(book => ImportListExclusionBookMatcher.AppliesToBook(exclusion, book)))
            {
                return true;
            }

            return authorProviderId.IsNotNullOrWhiteSpace() && MatchesExclusionId(authorProviderId, exclusion.ForeignId);
        }

        private static bool MatchesExclusionId(string providerId, string exclusionId)
        {
            if (providerId.IsNullOrWhiteSpace() || exclusionId.IsNullOrWhiteSpace())
            {
                return false;
            }

            exclusionId = exclusionId.Trim();

            if (providerId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var separator = providerId.IndexOf(':');
            var rawId = separator > 0 ? providerId.Substring(separator + 1) : providerId;

            return !exclusionId.Contains(':') && rawId.Equals(exclusionId, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeProviderId(string value)
        {
            return value.IsNullOrWhiteSpace() ? null : ImportListProviderIdHelper.Normalize(value, "gr");
        }
    }
}
