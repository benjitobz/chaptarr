using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryLibraryChangeForwarderFixture
    {
        private const long EbookLibraryId = 3;
        private const string RelativePath = "Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub";

        public class StubProxy : DispatchProxy
        {
            public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handlers.TryGetValue(targetMethod.Name, out var handler))
                {
                    return handler(args);
                }

                throw new NotImplementedException($"Stub does not handle {targetMethod.Name}");
            }
        }

        private static T Stub<T>(out StubProxy stub)
        {
            var proxy = DispatchProxy.Create<T, StubProxy>();
            stub = (StubProxy)(object)proxy;
            return proxy;
        }

        private class ScriptedGrimmoryProxy : IGrimmoryProxy
        {
            public Dictionary<long, List<GrimmoryBook>> BooksByLibrary { get; } = new Dictionary<long, List<GrimmoryBook>>();
            public List<GrimmoryAuditEntry> AuditEntries { get; } = new List<GrimmoryAuditEntry>();

            public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings) => new List<GrimmoryLibrary>();
            public void RefreshLibrary(GrimmorySettings settings, long libraryId) { }

            public List<GrimmoryBook> GetLibraryBooks(GrimmorySettings settings, long libraryId, bool bypassCache = false)
            {
                return BooksByLibrary.TryGetValue(libraryId, out var books) ? books : new List<GrimmoryBook>();
            }

            public GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false) => null;
            public void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata) { }
            public void UploadBookCover(GrimmorySettings settings, long bookId, byte[] image, string fileName) { }
            public byte[] GetBookCover(GrimmorySettings settings, long bookId) => new byte[] { 9 };
            public string BuildCoverUrl(GrimmorySettings settings, long bookId) => $"http://grimmory/cover/{bookId}";
            public List<GrimmoryAuditEntry> GetMetadataAuditEntries(GrimmorySettings settings, DateTime fromUtc) => AuditEntries.ToList();
            public ValidationFailure Test(GrimmorySettings settings) => null;
        }

        private class TestEditTarget : NotificationBase<GrimmorySettings>, IExternalLibraryEditTarget
        {
            public List<(Book Book, ExternalLibraryEditPayload Payload)> Pushes { get; } = new List<(Book, ExternalLibraryEditPayload)>();

            public override string Name => "TestTarget";
            public override string Link => string.Empty;
            public bool AcceptsExternalLibraryEdits => true;

            public void PushExternalLibraryEdit(Book book, List<BookFile> files, ExternalLibraryEditPayload payload)
            {
                Pushes.Add((book, payload));
            }

            public override ValidationResult Test()
            {
                return new ValidationResult();
            }
        }

        private class Context
        {
            public ScriptedGrimmoryProxy Proxy;
            public GrimmoryLibraryChangeForwarder Forwarder;
            public TestEditTarget Target;
            public GrimmorySettings Settings;
        }

        private static GrimmoryBook BuildGrimmoryBook(DateTime? coverUpdatedOn = null)
        {
            var slash = RelativePath.LastIndexOf('/');

            return new GrimmoryBook
            {
                Id = 100,
                LibraryId = EbookLibraryId,
                PrimaryFile = new GrimmoryBookFile
                {
                    FileSubPath = RelativePath.Substring(0, slash),
                    FileName = RelativePath.Substring(slash + 1)
                },
                Metadata = new GrimmoryBookMetadata
                {
                    Title = "Assassin's Apprentice",
                    Description = "Edited in Grimmory.",
                    Publisher = "Voyager",
                    SeriesName = "Farseer",
                    SeriesNumber = 1,
                    Language = "eng",
                    Isbn13 = "9780007562252",
                    Categories = new List<string> { "fantasy" },
                    CoverUpdatedOn = coverUpdatedOn
                }
            };
        }

        private static Context CreateContext(int targetDefinitionId = 2)
        {
            var context = new Context();
            var proxy = new ScriptedGrimmoryProxy();
            context.Proxy = proxy;

            var settings = new GrimmorySettings
            {
                Url = "http://grimmory:6060",
                Username = "chaptarr",
                Password = "secret",
                EbookLibraryId = EbookLibraryId,
                ForwardEdits = true
            };
            context.Settings = settings;

            var commandQueue = Stub<IManageCommandQueue>(out var commandStub);
            commandStub.Handlers["Push"] = _ => null;

            var source = new NzbDrone.Core.Notifications.Grimmory.Grimmory(proxy, commandQueue, new CacheManager(), LogManager.GetLogger("test"))
            {
                Definition = new NotificationDefinition { Id = 1, Name = "Grimmory", Settings = settings }
            };

            var target = new TestEditTarget
            {
                Definition = new NotificationDefinition { Id = targetDefinitionId, Name = "TestTarget", Settings = new GrimmorySettings() }
            };
            context.Target = target;

            var factory = Stub<INotificationFactory>(out var factoryStub);
            factoryStub.Handlers["GetAvailableProviders"] = _ => new List<INotification> { source, target };

            var rootFolderService = Stub<IRootFolderService>(out var rootStub);
            rootStub.Handlers["All"] = _ => new List<RootFolder> { new RootFolder { Id = 1, Path = @"C:\books".AsOsAgnostic() } };

            var expectedPath = Path.Combine(@"C:\books".AsOsAgnostic(), RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var bookFile = new BookFile { Id = 40, EditionId = 30, Path = expectedPath, MediaType = "ebook" };

            var mediaFileService = Stub<IMediaFileService>(out var mediaFileStub);
            mediaFileStub.Handlers["GetFileWithPath"] = args => string.Equals((string)args[0], expectedPath, StringComparison.OrdinalIgnoreCase) ? bookFile : null;
            mediaFileStub.Handlers["GetFilesByBook"] = _ => new List<BookFile> { bookFile };

            var editionService = Stub<IEditionService>(out var editionStub);
            editionStub.Handlers["GetEdition"] = args => (int)args[0] == 30 ? new Edition { Id = 30, BookId = 10 } : null;

            var bookService = Stub<IBookService>(out var bookStub);
            bookStub.Handlers["GetBook"] = args => (int)args[0] == 10 ? new Book { Id = 10, Title = "Assassin's Apprentice" } : null;

            context.Forwarder = new GrimmoryLibraryChangeForwarder(
                factory,
                proxy,
                rootFolderService,
                mediaFileService,
                editionService,
                bookService,
                LogManager.GetLogger("GrimmoryLibraryChangeForwarderFixture"));

            return context;
        }

        private static GrimmoryAuditEntry MetadataEditBy(string username)
        {
            return new GrimmoryAuditEntry
            {
                Id = 1,
                Username = username,
                EntityType = "Book",
                EntityId = 100,
                CreatedAt = DateTime.UtcNow
            };
        }

        [Test]
        public void should_not_forward_anything_on_first_poll()
        {
            var context = CreateContext();
            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook() };
            context.Proxy.AuditEntries.Add(MetadataEditBy("editor"));

            context.Forwarder.Handle(new ApplicationStartedEvent());

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_metadata_edit_by_another_user()
        {
            var context = CreateContext();
            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook() };

            context.Forwarder.Handle(new ApplicationStartedEvent());
            context.Proxy.AuditEntries.Add(MetadataEditBy("editor"));
            context.Forwarder.Handle(new ApplicationStartedEvent());

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            var (book, payload) = context.Target.Pushes[0];

            Assert.Multiple(() =>
            {
                Assert.That(book.Id, Is.EqualTo(10));
                Assert.That(payload.Title, Is.EqualTo("Assassin's Apprentice"));
                Assert.That(payload.Description, Is.EqualTo("Edited in Grimmory."));
                Assert.That(payload.SeriesName, Is.EqualTo("Farseer"));
                Assert.That(payload.Identifiers["isbn"], Is.EqualTo("9780007562252"));
                Assert.That(payload.CoverBytes, Is.Not.Null);
            });

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_ignore_edits_made_by_the_connections_own_user()
        {
            var context = CreateContext();
            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook() };

            context.Forwarder.Handle(new ApplicationStartedEvent());
            context.Proxy.AuditEntries.Add(MetadataEditBy("chaptarr"));
            context.Forwarder.Handle(new ApplicationStartedEvent());

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_cover_change_detected_from_timestamps()
        {
            var context = CreateContext();
            var initial = DateTime.UtcNow.AddHours(-1);
            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook(initial) };

            context.Forwarder.Handle(new ApplicationStartedEvent());

            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook(DateTime.UtcNow) };
            context.Forwarder.Handle(new ApplicationStartedEvent());

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_skip_target_sharing_the_sources_definition()
        {
            var context = CreateContext(targetDefinitionId: 1);
            context.Proxy.BooksByLibrary[EbookLibraryId] = new List<GrimmoryBook> { BuildGrimmoryBook() };

            context.Forwarder.Handle(new ApplicationStartedEvent());
            context.Proxy.AuditEntries.Add(MetadataEditBy("editor"));
            context.Forwarder.Handle(new ApplicationStartedEvent());

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }
    }
}
