using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.ImportLists.Items;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListItemResolverFixture
    {
        private static readonly Func<int, bool> NoFiles = _ => false;
        private static readonly Func<int, bool> NotQueued = _ => false;
        private static readonly Func<int, TrackedDownloadState?> NoQueueState = _ => null;

        private Author _author;

        [SetUp]
        public void Setup()
        {
            _author = new Author
            {
                Id = 7,
                Name = "Pierce Brown",
                Monitored = true,
                AudiobookMonitored = true,
                EbookMonitored = true
            };
        }

        [Test]
        public void match_books_should_match_on_work_id()
        {
            var book = CreateBook(1, "Red Rising", BookMediaType.Ebook, workId: "gr:15839976");
            var item = CreateItem("Red Rising", bookProviderId: "15839976");

            var result = ImportListItemResolver.MatchBooks(new[] { book }, item);

            Assert.That(result.Select(b => b.Id), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void match_books_should_fall_back_to_edition_id_and_include_sibling_media_rows()
        {
            var ebook = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111", goodreadsEditionId: 555);
            var audiobook = CreateBook(2, "Dark Age", BookMediaType.Audiobook, workId: "gr:111");
            var item = CreateItem("Dark Age", bookProviderId: "999", editionProviderId: "555");

            var result = ImportListItemResolver.MatchBooks(new[] { ebook, audiobook }, item);

            Assert.That(result.Select(b => b.Id), Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void match_books_should_fall_back_to_isbn()
        {
            var book = CreateBook(1, "Light Bringer", BookMediaType.Ebook, workId: "gr:222", isbn13: "978-0-425-28599-6");
            var item = CreateItem("Light Bringer", bookProviderId: "333", isbn13: "9780425285996");

            var result = ImportListItemResolver.MatchBooks(new[] { book }, item);

            Assert.That(result.Select(b => b.Id), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void match_books_should_fall_back_to_title_only_when_a_single_work_matches()
        {
            var book = CreateBook(1, "Morning Star", BookMediaType.Ebook, workId: "gr:444");
            var otherWorkSameTitle = CreateBook(2, "Morning Star", BookMediaType.Ebook, workId: "gr:445");
            var item = CreateItem("Morning Star", bookProviderId: "888");

            Assert.Multiple(() =>
            {
                Assert.That(ImportListItemResolver.MatchBooks(new[] { book }, item).Select(b => b.Id), Is.EqualTo(new[] { 1 }));
                Assert.That(ImportListItemResolver.MatchBooks(new[] { book, otherWorkSameTitle }, item), Is.Empty);
            });
        }

        [Test]
        public void match_books_should_return_empty_when_nothing_matches()
        {
            var book = CreateBook(1, "Golden Son", BookMediaType.Ebook, workId: "gr:1");
            var item = CreateItem("Iron Gold", bookProviderId: "2", editionProviderId: "3", isbn13: "9780000000002");

            Assert.That(ImportListItemResolver.MatchBooks(new[] { book }, item), Is.Empty);
        }

        [Test]
        public void build_should_report_per_media_states_and_the_strongest_overall_state()
        {
            var ebook = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111");
            var audiobook = CreateBook(2, "Dark Age", BookMediaType.Audiobook, workId: "gr:111");
            var item = CreateItem("Dark Age", bookProviderId: "111");

            var result = ImportListItemResolver.Build(item, _author, new[] { ebook, audiobook }, id => id == 1, id => id == 2, id => id == 2 ? TrackedDownloadState.Downloading : null, null, false);

            var ebookState = result.Media.Single(m => m.MediaType == BookMediaType.Ebook);
            var audiobookState = result.Media.Single(m => m.MediaType == BookMediaType.Audiobook);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.Downloaded));
                Assert.That(result.BookId, Is.EqualTo(1));
                Assert.That(result.AuthorId, Is.EqualTo(7));
                Assert.That(result.Author, Is.EqualTo("Pierce Brown"));
                Assert.That(result.Media, Has.Count.EqualTo(2));
                Assert.That(ebookState.State, Is.EqualTo(ImportListItemState.Downloaded));
                Assert.That(audiobookState.State, Is.EqualTo(ImportListItemState.Queued));
                Assert.That(audiobookState.QueueState, Is.EqualTo(TrackedDownloadState.Downloading));
            });
        }

        [Test]
        public void build_should_report_queued_for_pending_releases_without_a_tracked_state()
        {
            var book = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111");

            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), _author, new[] { book }, NoFiles, _ => true, NoQueueState, null, false);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.Queued));
                Assert.That(result.Media.Single().QueueState, Is.Null);
            });
        }

        [Test]
        public void build_should_report_missing_for_monitored_books_and_unmonitored_otherwise()
        {
            var monitored = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111");
            var unmonitored = CreateBook(2, "Dark Age", BookMediaType.Audiobook, workId: "gr:111");
            unmonitored.Monitored = false;
            unmonitored.AudiobookMonitored = false;
            unmonitored.EbookMonitored = false;

            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), _author, new[] { monitored, unmonitored }, NoFiles, NotQueued, NoQueueState, null, false);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.Missing));
                Assert.That(result.Media.Single(m => m.BookId == 1).State, Is.EqualTo(ImportListItemState.Missing));
                Assert.That(result.Media.Single(m => m.BookId == 2).State, Is.EqualTo(ImportListItemState.Unmonitored));
            });
        }

        [Test]
        public void build_should_report_not_in_library_when_author_is_missing()
        {
            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), null, Array.Empty<Book>(), NoFiles, NotQueued, NoQueueState, null, false);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.NotInLibrary));
                Assert.That(result.Author, Is.EqualTo("Pierce Brown"));
                Assert.That(result.Media, Is.Empty);
                Assert.That(result.BookId, Is.Null);
            });
        }

        [Test]
        public void build_should_report_pending_author_with_its_last_error()
        {
            var pending = new PendingAuthorImport { ProviderId = "gr:7", LastError = "Author not yet available" };

            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), null, Array.Empty<Book>(), NoFiles, NotQueued, NoQueueState, pending, false);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.PendingAuthor));
                Assert.That(result.PendingError, Is.EqualTo("Author not yet available"));
            });
        }

        [Test]
        public void build_should_report_not_in_library_when_author_exists_but_book_does_not()
        {
            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), _author, Array.Empty<Book>(), NoFiles, NotQueued, NoQueueState, null, false);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.NotInLibrary));
                Assert.That(result.AuthorId, Is.EqualTo(7));
            });
        }

        [Test]
        public void build_should_prefer_excluded_over_every_other_state()
        {
            var book = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111");

            var result = ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), _author, new[] { book }, _ => true, NotQueued, NoQueueState, null, true);

            Assert.Multiple(() =>
            {
                Assert.That(result.State, Is.EqualTo(ImportListItemState.Excluded));
                Assert.That(result.Media.Single().State, Is.EqualTo(ImportListItemState.Downloaded));
            });
        }

        [Test]
        public void build_should_use_release_date_as_date_added_and_fall_back_to_book_added()
        {
            var added = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var book = CreateBook(1, "Dark Age", BookMediaType.Ebook, workId: "gr:111");
            book.Added = added;

            var withDate = CreateItem("Dark Age", bookProviderId: "111");
            withDate.ReleaseDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

            Assert.Multiple(() =>
            {
                Assert.That(ImportListItemResolver.Build(withDate, _author, new[] { book }, NoFiles, NotQueued, NoQueueState, null, false).DateAdded, Is.EqualTo(withDate.ReleaseDate));
                Assert.That(ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), _author, new[] { book }, NoFiles, NotQueued, NoQueueState, null, false).DateAdded, Is.EqualTo(added));
                Assert.That(ImportListItemResolver.Build(CreateItem("Dark Age", bookProviderId: "111"), null, Array.Empty<Book>(), NoFiles, NotQueued, NoQueueState, null, false).DateAdded, Is.Null);
            });
        }

        private Book CreateBook(int id, string title, BookMediaType mediaType, string workId, long? goodreadsEditionId = null, string isbn13 = null)
        {
            var edition = new Edition
            {
                Id = id * 10,
                BookId = id,
                Title = title,
                ForeignEditionId = $"gr:{id * 10}-{mediaType.ToString().ToLowerInvariant()}",
                GoodreadsEditionId = goodreadsEditionId,
                Isbn13 = isbn13,
                Monitored = true
            };

            return new Book
            {
                Id = id,
                AuthorId = _author.Id,
                Author = _author,
                MediaType = mediaType,
                GoodreadsWorkId = workId,
                Title = title,
                TitleSlug = $"{id}-{title.ToLowerInvariant().Replace(' ', '-')}",
                Monitored = true,
                AudiobookMonitored = true,
                EbookMonitored = true,
                Added = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                Editions = new List<Edition> { edition }
            };
        }

        private static ImportListItemInfo CreateItem(string title, string bookProviderId, string editionProviderId = null, string isbn13 = null)
        {
            return new ImportListItemInfo
            {
                Author = "Pierce Brown",
                AuthorProviderId = "7",
                Book = title,
                BookProviderId = bookProviderId,
                EditionProviderId = editionProviderId,
                Isbn13 = isbn13
            };
        }
    }
}
