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
        public PagingResource<ImportListItemResource> GetItems([FromRoute] int id, [FromQuery] PagingRequestResource paging, [FromQuery] bool refresh = false)
        {
            return Page(_importListItemService.GetItems(id, refresh), paging);
        }

        [HttpGet("items")]
        public PagingResource<ImportListItemResource> GetAllItems([FromQuery] PagingRequestResource paging, [FromQuery] bool refresh = false)
        {
            return Page(_importListItemService.GetAllItems(refresh), paging);
        }

        private PagingResource<ImportListItemResource> Page(List<ImportListItem> items, PagingRequestResource paging)
        {
            var page = Math.Max(paging.Page ?? 1, 1);
            var pageSize = Math.Max(paging.PageSize ?? 10, 1);

            return new PagingResource<ImportListItemResource>
            {
                Page = page,
                PageSize = pageSize,
                SortKey = "dateAdded",
                SortDirection = SortDirection.Descending,
                TotalRecords = items.Count,
                Records = items.Skip((page - 1) * pageSize).Take(pageSize).Select(ToResourceWithLocalCovers).ToList()
            };
        }

        private ImportListItemResource ToResourceWithLocalCovers(ImportListItem item)
        {
            var resource = item.ToResource();

            if (item.BookId.HasValue)
            {
                _coverMapper.ConvertToLocalUrls(item.BookId.Value, MediaCoverEntity.Book, resource.Images);
            }

            return resource;
        }
    }
}
