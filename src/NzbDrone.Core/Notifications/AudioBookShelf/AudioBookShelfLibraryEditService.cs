using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelfLibraryEditService : IHandle<MediaCoversUpdatedEvent>
    {
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        public AudioBookShelfLibraryEditService(IBookService bookService,
                                                IMediaFileService mediaFileService,
                                                INotificationFactory notificationFactory,
                                                Logger logger)
        {
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            // AudioBookShelf keeps its own copy of an item's metadata and only re-reads
            // it on a rename, so a change Chaptarr makes to a book would otherwise never
            // show up there.
            var shelves = _notificationFactory.GetAvailableProviders()
                .OfType<AudioBookShelf>()
                .Where(s => ((AudioBookShelfSettings)s.Definition.Settings).PushLibraryEdits)
                .ToList();

            if (!shelves.Any())
            {
                return;
            }

            foreach (var book in GetBooks(message))
            {
                var files = _mediaFileService.GetFilesByBook(book.Id)
                    .Where(f => f.Path.IsNotNullOrWhiteSpace())
                    .ToList();

                if (!files.Any())
                {
                    continue;
                }

                foreach (var shelf in shelves)
                {
                    try
                    {
                        shelf.PushBookMetadata(book, files);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to push metadata for {0} to AudioBookShelf", book.Title);
                    }
                }
            }
        }

        private IEnumerable<Book> GetBooks(MediaCoversUpdatedEvent message)
        {
            if (message.Book != null && message.Book.Id > 0)
            {
                return new[] { message.Book };
            }

            var author = message.Author;

            if (author == null || author.Id <= 0)
            {
                return Array.Empty<Book>();
            }

            return _bookService.GetBooksByAuthor(author.Id).Where(b => b != null && b.Id > 0);
        }
    }
}
