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
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly ISeriesService _seriesService;
        private readonly ICached<List<ImportListItemInfo>> _cache;
        private readonly Logger _logger;

        public ImportListItemService(IImportListFactory importListFactory,
                                     IAuthorService authorService,
                                     IBookService bookService,
                                     IMediaFileService mediaFileService,
                                     IQueueService queueService,
                                     IPendingAuthorImportService pendingAuthorImportService,
                                     IImportListExclusionService importListExclusionService,
                                     ISeriesBookLinkService seriesBookLinkService,
                                     ISeriesService seriesService,
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
            _seriesBookLinkService = seriesBookLinkService;
            _seriesService = seriesService;
            _cache = cacheManager.GetCache<List<ImportListItemInfo>>(GetType());
            _logger = logger;
        }

        public List<ImportListItem> GetItems(int importListId, bool refresh)
        {
            var definition = _importListFactory.Get(importListId);
            var source = CreateSource(definition);

            return Resolve(FetchCached(definition, refresh).Select(item => (item, source)).ToList());
        }

        public List<ImportListItem> GetAllItems(bool refresh)
        {
            var tagged = new List<(ImportListItemInfo Item, ImportListItemSource Source)>();

            foreach (var definition in _importListFactory.All())
            {
                var source = CreateSource(definition);
                tagged.AddRange(FetchCached(definition, refresh).Select(item => (item, source)));
            }

            return Resolve(tagged)
                .GroupBy(MergeKey, StringComparer.OrdinalIgnoreCase)
                .Select(Merge)
                .OrderByDescending(i => i.DateAdded ?? DateTime.MinValue)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<ImportListItemInfo> FetchCached(ImportListDefinition definition, bool refresh)
        {
            var cacheKey = definition.Id.ToString(CultureInfo.InvariantCulture);

            if (refresh)
            {
                _cache.Remove(cacheKey);
            }

            return _cache.Get(cacheKey, () => Fetch(definition), CacheDuration);
        }

        private ImportListItemSource CreateSource(ImportListDefinition definition)
        {
            string implementationName = null;

            try
            {
                implementationName = _importListFactory.GetInstance(definition)?.Name;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve the implementation name for import list '{0}'", definition.Name);
            }

            return new ImportListItemSource
            {
                ImportListId = definition.Id,
                Name = definition.Name,
                Implementation = definition.Implementation,
                ImplementationName = implementationName.IsNotNullOrWhiteSpace() ? implementationName : definition.Implementation
            };
        }

        private static string MergeKey(ImportListItem item)
        {
            if (item.Media.Any())
            {
                return "books:" + string.Join(",", item.Media.Select(m => m.BookId).OrderBy(id => id));
            }

            if (item.BookProviderId.IsNotNullOrWhiteSpace())
            {
                return "work:" + item.BookProviderId;
            }

            if (item.EditionProviderId.IsNotNullOrWhiteSpace())
            {
                return "edition:" + item.EditionProviderId;
            }

            return "title:" + (item.Title ?? string.Empty).Trim() + "|" + (item.Author ?? string.Empty).Trim();
        }

        private static ImportListItem Merge(IGrouping<string, ImportListItem> group)
        {
            var items = group.ToList();
            var merged = items.OrderByDescending(i => i.DateAdded ?? DateTime.MinValue).First();

            merged.Sources = items
                .SelectMany(i => i.Sources)
                .GroupBy(s => s.ImportListId)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (items.Any(i => i.State == ImportListItemState.Excluded))
            {
                merged.State = ImportListItemState.Excluded;
            }

            return merged;
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
                return importList.FetchForPreview()?.Where(i => i != null).ToList() ?? new List<ImportListItemInfo>();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to fetch items for import list '{0}'", definition.Name);
                return new List<ImportListItemInfo>();
            }
        }

        private List<ImportListItem> Resolve(List<(ImportListItemInfo Item, ImportListItemSource Source)> items)
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

            var authorsByName = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);

            Author FindAuthorByName(ImportListItemInfo item)
            {
                if (item.AuthorProviderId.IsNotNullOrWhiteSpace() || item.Author.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var name = item.Author.Trim();

                if (!authorsByName.TryGetValue(name, out var author))
                {
                    author = _authorService.FindByName(name);
                    authorsByName[name] = author;
                }

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

            var matchedItems = items
                .Select(tagged =>
                {
                    var author = FindAuthor(tagged.Item.AuthorProviderId) ?? FindAuthorByName(tagged.Item);
                    var matched = author == null ? new List<Book>() : ImportListItemResolver.MatchBooks(BooksFor(author), tagged.Item);

                    return (tagged.Item, tagged.Source, Author: author, Matched: matched);
                })
                .ToList();

            var seriesLookup = BuildSeriesLookup(matchedItems.SelectMany(m => m.Matched).ToList(), matchedItems.Select(m => m.Author).Where(a => a != null).ToList());
            var result = new List<ImportListItem>();

            foreach (var (item, source, author, matched) in matchedItems)
            {
                var authorProviderId = NormalizeProviderId(item.AuthorProviderId);
                var bookProviderId = NormalizeProviderId(item.BookProviderId);
                var excluded = exclusions.Any(exclusion => IsExcluded(exclusion, authorProviderId, bookProviderId, matched));
                var pending = author == null && authorProviderId != null && pendingByProviderId.TryGetValue(authorProviderId, out var pendingImport)
                    ? pendingImport
                    : null;

                var built = ImportListItemResolver.Build(item, author, matched, HasFiles, IsQueued, QueueState, seriesLookup, pending, excluded);

                if (source != null)
                {
                    built.Sources.Add(source);
                }

                result.Add(built);
            }

            return result
                .OrderByDescending(i => i.DateAdded ?? DateTime.MinValue)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private Func<int, List<ImportListItemSeries>> BuildSeriesLookup(List<Book> books, List<Author> authors)
        {
            var bookIds = books.Select(b => b.Id).Distinct().ToList();

            if (!bookIds.Any())
            {
                return _ => new List<ImportListItemSeries>();
            }

            var linksByBook = _seriesBookLinkService.GetLinksByBook(bookIds)
                .Where(link => link != null)
                .GroupBy(link => link.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var seriesById = authors
                .Select(a => a.Id)
                .Distinct()
                .SelectMany(authorId => _seriesService.GetByAuthorId(authorId) ?? new List<Series>())
                .Where(s => s != null)
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());

            Series SeriesFor(int seriesId)
            {
                if (seriesById.TryGetValue(seriesId, out var cached))
                {
                    return cached;
                }

                Series found = null;

                try
                {
                    found = _seriesService.GetSeries(seriesId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Series {0} referenced by a book link could not be loaded", seriesId);
                }

                seriesById[seriesId] = found;

                return found;
            }

            return bookId => linksByBook.TryGetValue(bookId, out var links)
                ? ImportListItemResolver.BuildSeriesList(links, SeriesFor)
                : new List<ImportListItemSeries>();
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
