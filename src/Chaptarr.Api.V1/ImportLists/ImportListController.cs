using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Items;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Api.V1.ImportLists
{
    [V1ApiController]
    public class ImportListController : ProviderControllerBase<ImportListResource, ImportListBulkResource, IImportList, ImportListDefinition>
    {
        public static readonly ImportListResourceMapper ResourceMapper = new();
        public static readonly ImportListBulkResourceMapper BulkResourceMapper = new();

        private const int MaxItemsPageSize = 500;

        private readonly IImportListItemService _importListItemService;
        private readonly IMapCoversToLocal _coverMapper;

        public ImportListController(IImportListFactory importListFactory,
                                    IImportListItemService importListItemService,
                                    IMapCoversToLocal coverMapper,
                                    QualityProfileExistsValidator qualityProfileExistsValidator,
                                    MetadataProfileExistsValidator metadataProfileExistsValidator)
            : base(importListFactory, "importlist", ResourceMapper, BulkResourceMapper)
        {
            _importListItemService = importListItemService;
            _coverMapper = coverMapper;

            var requiresGenericAddedAuthorSettings = (ImportListResource r) =>
                r?.Implementation != "HardcoverLibraryImportList" &&
                r?.Implementation != "GoodreadsBookshelf" &&
                r?.Implementation != "GoodreadsListImportList" &&
                r?.Implementation != "GoodreadsSeriesImportList";

            SharedValidator.When(requiresGenericAddedAuthorSettings, () =>
            {
                Http.Validation.RuleBuilderExtensions.ValidId(SharedValidator.RuleFor(s => s.QualityProfileId));
                Http.Validation.RuleBuilderExtensions.ValidId(SharedValidator.RuleFor(s => s.MetadataProfileId));

                SharedValidator.RuleFor(c => c.RootFolderPath).IsValidPath();
                SharedValidator.RuleFor(c => c.QualityProfileId).SetValidator(qualityProfileExistsValidator);
                SharedValidator.RuleFor(c => c.MetadataProfileId).SetValidator(metadataProfileExistsValidator);
            });
        }

        [HttpGet("{id:int}/items")]
        [Produces("application/json")]
        public PagingResource<ImportListItemResource> GetItems([FromRoute] int id, [FromQuery] PagingRequestResource paging, [FromQuery] bool refresh = false)
        {
            return Page(_importListItemService.GetItems(id, refresh), paging);
        }

        [HttpGet("items")]
        [Produces("application/json")]
        public PagingResource<ImportListItemResource> GetAllItems([FromQuery] PagingRequestResource paging, [FromQuery] bool refresh = false)
        {
            return Page(_importListItemService.GetAllItems(refresh), paging);
        }

        private PagingResource<ImportListItemResource> Page(List<ImportListItem> items, PagingRequestResource paging)
        {
            var pagingResource = new PagingResource<ImportListItemResource>(paging);
            var page = Math.Max(pagingResource.Page, 1);
            var pageSize = Math.Clamp(pagingResource.PageSize, 1, MaxItemsPageSize);
            var sortKey = string.IsNullOrWhiteSpace(pagingResource.SortKey) ? "dateAdded" : pagingResource.SortKey;
            var sortDirection = pagingResource.SortDirection == SortDirection.Ascending ? SortDirection.Ascending : SortDirection.Descending;

            var ordered = Sort(items, sortKey, sortDirection);

            var records = ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ToResource)
                .ToList();

            return new PagingResource<ImportListItemResource>
            {
                Page = page,
                PageSize = pageSize,
                SortKey = sortKey,
                SortDirection = sortDirection,
                TotalRecords = items.Count,
                Records = records
            };
        }

        private ImportListItemResource ToResource(ImportListItem item)
        {
            var resource = item.ToResource();

            if (item.BookId.HasValue && resource.Images.Any())
            {
                _coverMapper.ConvertToLocalUrls(item.BookId.Value, MediaCoverEntity.Book, resource.Images);
            }

            return resource;
        }

        private static IEnumerable<ImportListItem> Sort(List<ImportListItem> items, string sortKey, SortDirection direction)
        {
            var descending = direction == SortDirection.Descending;

            switch (sortKey.ToLowerInvariant())
            {
                case "title":
                    return descending
                        ? items.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase)
                        : items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
                case "state":
                    return descending
                        ? items.OrderByDescending(i => i.State).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                        : items.OrderBy(i => i.State).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
                case "releasedate":
                    return descending
                        ? items.OrderByDescending(i => i.ReleaseDate ?? DateTime.MinValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                        : items.OrderBy(i => i.ReleaseDate ?? DateTime.MinValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
                default:
                    return descending
                        ? items.OrderByDescending(i => i.DateAdded ?? DateTime.MinValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                        : items.OrderBy(i => i.DateAdded ?? DateTime.MinValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
