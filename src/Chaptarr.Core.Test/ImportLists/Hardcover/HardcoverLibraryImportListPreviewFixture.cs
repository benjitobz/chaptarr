using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.ImportLists.Hardcover
{
    [TestFixture]
    public class HardcoverLibraryImportListPreviewFixture
    {
        private class StateRepositoryProxy : DispatchProxy
        {
            public HardcoverLibraryImportListState State { get; set; }
            public List<string> Writes { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IHardcoverLibraryImportListStateRepository.GetByImportListId))
                {
                    return State;
                }

                Writes.Add(targetMethod.Name);

                return targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType
                    ? args?.FirstOrDefault()
                    : Activator.CreateInstance(targetMethod.ReturnType);
            }
        }

        private class RecordingStatusService : IImportListStatusService
        {
            public List<string> Calls { get; } = new();

            public List<ImportListStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) => Calls.Add("success");
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) => Calls.Add("failure");
            public void RecordConnectionFailure(int providerId) => Calls.Add("connectionFailure");
            public DateTime? GetLastSyncListInfo(int importListId) => null;
            public void UpdateListSyncStatus(int importListId) { }
        }

        private class GraphQlStubHttpClient : IHttpClient
        {
            public List<string> Bodies { get; } = new();

            public HttpResponse Execute(HttpRequest request)
            {
                var body = request.ContentData == null ? string.Empty : Encoding.UTF8.GetString(request.ContentData);
                Bodies.Add(body);

                string json;

                if (body.Contains("listId", StringComparison.OrdinalIgnoreCase))
                {
                    json = "{\"data\":{\"list_books\":[]}}";
                }
                else if (body.Contains("userId", StringComparison.OrdinalIgnoreCase))
                {
                    json = "{\"data\":{\"lists\":[{\"id\":5}]}}";
                }
                else
                {
                    json = "{\"data\":{\"me\":[{\"id\":1,\"username\":\"tester\"}]}}";
                }

                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, json);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private GraphQlStubHttpClient _httpClient;
        private RecordingStatusService _statusService;
        private StateRepositoryProxy _stateRepository;
        private HardcoverLibraryImportListState _state;
        private HardcoverLibraryImportList _importList;

        [SetUp]
        public void Setup()
        {
            _httpClient = new GraphQlStubHttpClient();
            _statusService = new RecordingStatusService();

            _state = new HardcoverLibraryImportListState
            {
                Id = 3,
                ImportListId = 1,
                HardcoverUserId = 1,
                SettingsSignature = "owned:True;wtr:False;cr:False;read:False;ma:False;me:False",
                OwnedCursorListBookId = 100,
                LastFullSyncAt = DateTime.UtcNow.AddHours(-1),
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            };

            var repository = DispatchProxy.Create<IHardcoverLibraryImportListStateRepository, StateRepositoryProxy>();
            _stateRepository = (StateRepositoryProxy)(object)repository;
            _stateRepository.State = _state;

            _importList = new HardcoverLibraryImportList(
                _httpClient,
                new Lazy<IQualityProfileService>(() => DispatchProxy.Create<IQualityProfileService, ThrowingProxy<IQualityProfileService>>()),
                new Lazy<IMetadataProfileService>(() => DispatchProxy.Create<IMetadataProfileService, ThrowingProxy<IMetadataProfileService>>()),
                new Lazy<ITagService>(() => DispatchProxy.Create<ITagService, ThrowingProxy<ITagService>>()),
                DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                repository,
                _statusService,
                null,
                null,
                LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Hardcover",
                    Settings = new HardcoverLibraryImportListSettings
                    {
                        ApiToken = "token",
                        ImportOwned = true,
                        ImportWantToRead = false,
                        ImportCurrentlyReading = false,
                        ImportRead = false,
                        MonitorAudiobooks = false,
                        MonitorEbooks = false
                    }
                }
            };
        }

        [Test]
        public void preview_should_read_the_whole_owned_list_without_saving_state_or_status()
        {
            _importList.FetchForPreview();

            var listBookRequests = _httpClient.Bodies.Where(b => b.Contains("listId", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(listBookRequests, Is.Not.Empty);
                Assert.That(listBookRequests.All(b => b.Contains("beforeId", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(listBookRequests.Any(b => b.Contains("afterId", StringComparison.OrdinalIgnoreCase)), Is.False);
                Assert.That(_stateRepository.Writes, Is.Empty);
                Assert.That(_statusService.Calls, Is.Empty);
                Assert.That(_state.OwnedCursorListBookId, Is.EqualTo(100));
            });
        }

        [Test]
        public void sync_fetch_should_still_use_the_delta_cursor_and_record_status()
        {
            _importList.Fetch();

            var listBookRequests = _httpClient.Bodies.Where(b => b.Contains("listId", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(listBookRequests.Any(b => b.Contains("afterId", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(_statusService.Calls, Is.EqualTo(new[] { "success" }));
            });
        }
    }
}
