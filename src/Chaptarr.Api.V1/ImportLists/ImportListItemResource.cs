using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.ImportLists.Items;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.ImportLists
{
    public class ImportListItemMediaResource
    {
        public BookMediaType MediaType { get; set; }
        public int BookId { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
        public ImportListItemState State { get; set; }
        public TrackedDownloadState? QueueState { get; set; }
    }

    public class ImportListItemSeriesResource
    {
        public string Title { get; set; }
        public string Position { get; set; }
    }

    public class ImportListItemSourceResource
    {
        public int ImportListId { get; set; }
        public string Name { get; set; }
        public string Implementation { get; set; }
        public string ImplementationName { get; set; }
    }

    public class ImportListItemResource
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Author { get; set; }
        public string TitleSlug { get; set; }
        public List<ImportListItemSeriesResource> Series { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? AuthorId { get; set; }
        public int? BookId { get; set; }
        public ImportListItemState State { get; set; }
        public string PendingError { get; set; }
        public DateTime? DateAdded { get; set; }
        public string AuthorProviderId { get; set; }
        public string BookProviderId { get; set; }
        public string EditionProviderId { get; set; }
        public string Isbn13 { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<ImportListItemMediaResource> Media { get; set; }
        public List<ImportListItemSourceResource> Sources { get; set; }
    }

    public static class ImportListItemResourceMapper
    {
        public static ImportListItemResource ToResource(this ImportListItem model)
        {
            if (model == null)
            {
                return null;
            }

            return new ImportListItemResource
            {
                Title = model.Title,
                Subtitle = model.Subtitle,
                Author = model.Author,
                TitleSlug = model.TitleSlug,
                Series = model.Series?.Select(s => new ImportListItemSeriesResource { Title = s.Title, Position = s.Position }).ToList() ?? new List<ImportListItemSeriesResource>(),
                ReleaseDate = model.ReleaseDate,
                AuthorId = model.AuthorId,
                BookId = model.BookId,
                State = model.State,
                PendingError = model.PendingError,
                DateAdded = model.DateAdded,
                AuthorProviderId = model.AuthorProviderId,
                BookProviderId = model.BookProviderId,
                EditionProviderId = model.EditionProviderId,
                Isbn13 = model.Isbn13,
                Images = model.Images?.Select(image => new MediaCover(image.CoverType, image.Url) { RemoteUrl = image.RemoteUrl }).ToList() ?? new List<MediaCover>(),
                Media = model.Media?.Select(media => new ImportListItemMediaResource
                {
                    MediaType = media.MediaType,
                    BookId = media.BookId,
                    Monitored = media.Monitored,
                    HasFiles = media.HasFiles,
                    State = media.State,
                    QueueState = media.QueueState
                }).ToList() ?? new List<ImportListItemMediaResource>(),
                Sources = model.Sources?.Select(source => new ImportListItemSourceResource
                {
                    ImportListId = source.ImportListId,
                    Name = source.Name,
                    Implementation = source.Implementation,
                    ImplementationName = source.ImplementationName
                }).ToList() ?? new List<ImportListItemSourceResource>()
            };
        }
    }
}
