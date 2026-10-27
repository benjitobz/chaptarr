using System;
using System.Collections.Generic;
using NzbDrone.Core.ImportLists.Items;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.ImportLists
{
    public class ImportListItemResource
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Author { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? BookId { get; set; }
        public ImportListItemState State { get; set; }
        public string PendingError { get; set; }
        public DateTime? DateAdded { get; set; }
        public bool DateAddedFromList { get; set; }
        public List<MediaCover> Images { get; set; }
        public List<ImportListItemMediaState> Media { get; set; }
        public List<ImportListItemSource> Sources { get; set; }
    }

    public static class ImportListItemResourceMapper
    {
        public static ImportListItemResource ToResource(this ImportListItem model)
        {
            return new ImportListItemResource
            {
                Title = model.Title,
                Subtitle = model.Subtitle,
                Author = model.Author,
                ReleaseDate = model.ReleaseDate,
                BookId = model.BookId,
                State = model.State,
                PendingError = model.PendingError,
                DateAdded = model.DateAdded,
                DateAddedFromList = model.DateAddedFromList,
                Images = model.Images,
                Media = model.Media,
                Sources = model.Sources
            };
        }
    }
}
