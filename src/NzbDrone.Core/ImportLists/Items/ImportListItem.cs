using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Core.ImportLists.Items
{
    public class ImportListItemMediaState
    {
        public BookMediaType MediaType { get; set; }
        public int BookId { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
        public ImportListItemState State { get; set; }
        public TrackedDownloadState? QueueState { get; set; }
    }

    public class ImportListItemSeries
    {
        public string Title { get; set; }
        public string Position { get; set; }
    }

    public class ImportListItem
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Author { get; set; }
        public string TitleSlug { get; set; }
        public string SeriesTitle { get; set; }
        public string SeriesPosition { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string AuthorProviderId { get; set; }
        public string BookProviderId { get; set; }
        public string EditionProviderId { get; set; }
        public string Isbn13 { get; set; }
        public DateTime? DateAdded { get; set; }
        public int? AuthorId { get; set; }
        public int? BookId { get; set; }
        public ImportListItemState State { get; set; }
        public string PendingError { get; set; }
        public List<MediaCover.MediaCover> Images { get; set; } = new();
        public List<ImportListItemMediaState> Media { get; set; } = new();
    }
}
