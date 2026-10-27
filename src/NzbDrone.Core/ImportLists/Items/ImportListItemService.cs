using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.ImportLists.Items
{
    public interface IImportListItemService
    {
        List<ImportListItem> GetItems(int importListId, bool refresh);
        List<ImportListItem> GetAllItems(bool refresh);
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
            return Newest(Resolve(new[] { _importListFactory.Get(importListId) }, refresh));
        }

        public List<ImportListItem> GetAllItems(bool refresh)
        {
            return Newest(Resolve(_importListFactory.All(), refresh)
                .GroupBy(MergeKey, StringComparer.OrdinalIgnoreCase)
                .Select(Merge));
        }

        private List<ImportListItem> Resolve(IEnumerable<ImportListDefinition> lists, bool refresh)
        {
            var authors = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            var booksByAuthorId = new Dictionary<int, List<Book>>();
            var filesByBookId = new Dictionary<int, bool>();
            var exclusions = _importListExclusionService.All();

            var pendingByProviderId = _pendingAuthorImportService.GetAll()
                .Where(p => p.ProviderId.IsNotNullOrWhiteSpace())
                .GroupBy(p => ImportListItemResolver.NormalizeProviderId(p.ProviderId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First(), StringComparer.OrdinalIgnoreCase);

            var queuedBookIds = new HashSet<int>(_queueService.GetQueue().Where(q => q.Book != null).Select(q => q.Book.Id));

            Author FindAuthor(ImportListItemInfo item, string providerId)
            {
                var key = providerId ?? item.Author?.Trim();

                if (key.IsNullOrWhiteSpace())
                {
                    return null;
                }

                if (!authors.TryGetValue(key, out var author))
                {
                    author = providerId != null ? FindAuthorByProviderId(providerId) : _authorService.FindByName(key);
                    authors[key] = author;
                }

                return author;
            }

            List<Book> BooksFor(Author author)
            {
                if (!booksByAuthorId.TryGetValue(author.Id, out var books))
                {
                    books = _bookService.GetBooksByAuthor(author.Id);
                    booksByAuthorId[author.Id] = books;
                }

                return books;
            }

            bool HasFiles(int bookId)
            {
                if (!filesByBookId.TryGetValue(bookId, out var hasFiles))
                {
                    hasFiles = _mediaFileService.GetFilesByBook(bookId).Any();
                    filesByBookId[bookId] = hasFiles;
                }

                return hasFiles;
            }

            var result = new List<ImportListItem>();

            foreach (var list in lists)
            {
                var importList = _importListFactory.GetInstance(list);
                var source = new ImportListItemSource { ImportListId = list.Id, Name = list.Name, ImplementationName = importList.Name };

                foreach (var item in Fetch(importList, refresh))
                {
                    var authorProviderId = ImportListItemResolver.NormalizeProviderId(item.AuthorProviderId);
                    var bookProviderId = ImportListItemResolver.NormalizeProviderId(item.BookProviderId);
                    var author = FindAuthor(item, authorProviderId);
                    var books = author == null ? new List<Book>() : ImportListItemResolver.MatchBooks(BooksFor(author), item);
                    var pending = author == null && authorProviderId != null ? pendingByProviderId.GetValueOrDefault(authorProviderId) : null;
                    var excluded = exclusions.Any(e => ImportListExclusionBookMatcher.AppliesToProviderId(e, bookProviderId, null) ||
                                                       ImportListExclusionBookMatcher.AppliesToProviderId(e, authorProviderId, null) ||
                                                       books.Any(b => ImportListExclusionBookMatcher.AppliesToBook(e, b)));

                    var built = ImportListItemResolver.Build(item, author, books, HasFiles, queuedBookIds.Contains, pending, excluded);
                    built.Sources.Add(source);
                    result.Add(built);
                }
            }

            return result;
        }

        private List<ImportListItemInfo> Fetch(IImportList importList, bool refresh)
        {
            var key = importList.Definition.Id.ToString(CultureInfo.InvariantCulture);

            if (refresh)
            {
                _cache.Remove(key);
            }

            return _cache.Get(key, () =>
            {
                try
                {
                    return importList.FetchForPreview().ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to fetch items for import list '{0}'", importList.Definition.Name);
                    return new List<ImportListItemInfo>();
                }
            }, CacheDuration);
        }

        private Author FindAuthorByProviderId(string providerId)
        {
            var separator = providerId.IndexOf(':');
            var prefix = providerId.Substring(0, Math.Max(separator, 0));

            return _authorService.FindByProviderId(prefix, providerId) ?? _authorService.FindByProviderId(prefix, providerId.Substring(separator + 1));
        }

        private static string MergeKey(ImportListItem item)
        {
            if (item.Media.Any())
            {
                return "books:" + string.Join(",", item.Media.Select(m => m.BookId).OrderBy(id => id));
            }

            if (item.BookProviderId != null)
            {
                return "work:" + item.BookProviderId;
            }

            if (item.EditionProviderId != null)
            {
                return "edition:" + item.EditionProviderId;
            }

            return "title:" + item.Title?.Trim() + "|" + item.Author?.Trim();
        }

        private static ImportListItem Merge(IGrouping<string, ImportListItem> group)
        {
            var merged = group.MaxBy(i => i.DateAdded ?? DateTime.MinValue);

            merged.Sources = group
                .SelectMany(i => i.Sources)
                .DistinctBy(s => s.ImportListId)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (group.Any(i => i.State == ImportListItemState.Excluded))
            {
                merged.State = ImportListItemState.Excluded;
            }

            return merged;
        }

        private static List<ImportListItem> Newest(IEnumerable<ImportListItem> items)
        {
            return items
                .OrderByDescending(i => i.DateAdded ?? DateTime.MinValue)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
