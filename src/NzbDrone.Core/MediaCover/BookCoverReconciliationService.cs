using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    public class BookCoverReconciliationService : IHandle<AuthorScannedEvent>
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public BookCoverReconciliationService(IBookService bookService,
                                              IEditionService editionService,
                                              IMapCoversToLocal mediaCoverService,
                                              IEventAggregator eventAggregator,
                                              Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaCoverService = mediaCoverService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // The monitored edition can change outside a refresh or an explicit switch - file
        // matching pins the edition that fits the files it just linked - and none of those
        // paths revisit the cover, so the stored file keeps another edition's artwork. A scan
        // is where those changes settle; reconcile any book whose stored cover no longer
        // belongs to its monitored edition.
        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;

            if (author == null || author.Id <= 0)
            {
                return;
            }

            var books = _bookService.GetBooksByAuthor(author.Id);

            if (!books.Any())
            {
                return;
            }

            var editionsByBook = _editionService.GetEditionsByBook(books.Select(b => b.Id))
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                if (!editionsByBook.TryGetValue(book.Id, out var editions))
                {
                    continue;
                }

                var monitored = editions.FirstOrDefault(e => e.Monitored);
                var storedEditionId = StoredCoverEditionId(book.Id);

                if (monitored == null || storedEditionId == null || storedEditionId == monitored.Id)
                {
                    continue;
                }

                _logger.Debug("Cover for book {0} belongs to edition {1} but edition {2} is monitored; reconciling", book.Id, storedEditionId, monitored.Id);

                try
                {
                    book.Editions = editions;
                    _mediaCoverService.EnsureBookCovers(book);
                    _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(book));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to reconcile the cover for book {0}", book.Id);
                }
            }
        }

        private int? StoredCoverEditionId(int bookId)
        {
            try
            {
                var coverPath = _mediaCoverService.GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, "jpg");
                var metadataPath = Path.Combine(Path.GetDirectoryName(coverPath), "cover-metadata.json");

                if (!File.Exists(metadataPath))
                {
                    return null;
                }

                return JObject.Parse(File.ReadAllText(metadataPath))["selectedEdition"]?["localEditionId"]?.Value<int?>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read the cover sidecar for book {0}", bookId);
                return null;
            }
        }
    }
}
