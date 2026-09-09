using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.Grimmory;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryProxyFixture
    {
        private const string LibrariesJson = "[{\"id\":10,\"name\":\"Ebooks\",\"allowedFormats\":[\"EPUB\",\"PDF\"]},{\"id\":20,\"name\":\"Audiobooks\",\"allowedFormats\":[\"AUDIOBOOK\"]}]";

        [Test]
        public void should_login_and_fetch_libraries_with_bearer_token()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token1" } };
            var proxy = CreateProxy(httpClient);

            var libraries = proxy.GetLibraries(BuildSettings());

            Assert.That(libraries, Has.Count.EqualTo(2));
            Assert.That(libraries[0].Id, Is.EqualTo(10));
            Assert.That(libraries[0].Name, Is.EqualTo("Ebooks"));
            Assert.That(libraries[0].AllowedFormats, Is.EqualTo(new List<string> { "EPUB", "PDF" }));
            Assert.That(httpClient.LoginCount, Is.EqualTo(1));

            var libraryRequest = httpClient.Requests.Last();
            Assert.That(libraryRequest.Url.ToString(), Does.EndWith("/api/v1/libraries"));
            Assert.That(libraryRequest.Headers["Authorization"], Is.EqualTo("Bearer token1"));
        }

        [Test]
        public void should_reuse_cached_token_across_calls()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token1" } };
            var proxy = CreateProxy(httpClient);
            var settings = BuildSettings();

            proxy.GetLibraries(settings);
            proxy.RefreshLibrary(settings, 10);

            Assert.That(httpClient.LoginCount, Is.EqualTo(1));
        }

        [Test]
        public void should_relogin_once_when_token_is_rejected()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token2" } };
            var proxy = CreateProxy(httpClient);

            var libraries = proxy.GetLibraries(BuildSettings());

            Assert.That(libraries, Has.Count.EqualTo(2));
            Assert.That(httpClient.LoginCount, Is.EqualTo(2));
        }

        [Test]
        public void should_throw_authentication_exception_when_relogin_still_rejected()
        {
            var httpClient = new ScriptedHttpClient();
            var proxy = CreateProxy(httpClient);

            Assert.Throws<GrimmoryAuthenticationException>(() => proxy.GetLibraries(BuildSettings()));
            Assert.That(httpClient.LoginCount, Is.EqualTo(2));
        }

        [Test]
        public void should_throw_authentication_exception_when_login_is_rejected()
        {
            var httpClient = new ScriptedHttpClient { RejectLogin = true };
            var proxy = CreateProxy(httpClient);

            Assert.Throws<GrimmoryAuthenticationException>(() => proxy.GetLibraries(BuildSettings()));
        }

        [Test]
        public void should_send_put_to_refresh_endpoint()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token1" } };
            var proxy = CreateProxy(httpClient);

            proxy.RefreshLibrary(BuildSettings(), 20);

            var refreshRequest = httpClient.Requests.Last();
            Assert.That(refreshRequest.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(refreshRequest.Url.ToString(), Does.EndWith("/api/v1/libraries/20/refresh"));
        }

        [Test]
        public void test_should_fail_when_configured_library_is_missing()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token1" } };
            var proxy = CreateProxy(httpClient);

            var settings = BuildSettings();
            settings.EbookLibraryId = 99;

            var failure = proxy.Test(settings);

            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.PropertyName, Is.EqualTo(nameof(GrimmorySettings.EbookLibraryId)));
        }

        [Test]
        public void test_should_pass_when_configured_libraries_exist()
        {
            var httpClient = new ScriptedHttpClient { ValidTokens = { "token1" } };
            var proxy = CreateProxy(httpClient);

            Assert.That(proxy.Test(BuildSettings()), Is.Null);
        }

        private static GrimmoryProxy CreateProxy(ScriptedHttpClient httpClient)
        {
            return new GrimmoryProxy(httpClient, new CacheManager(), LogManager.GetLogger("GrimmoryProxyFixture"));
        }

        private static GrimmorySettings BuildSettings()
        {
            return new GrimmorySettings
            {
                Url = "http://grimmory:6060",
                Username = "chaptarr",
                Password = "secret",
                EbookLibraryId = 10,
                AudiobookLibraryId = 20
            };
        }

        private class ScriptedHttpClient : IHttpClient
        {
            public List<HttpRequest> Requests { get; } = new List<HttpRequest>();
            public HashSet<string> ValidTokens { get; } = new HashSet<string>();
            public bool RejectLogin { get; set; }
            public int LoginCount { get; private set; }

            public HttpResponse Execute(HttpRequest request)
            {
                Requests.Add(request);

                var url = request.Url.ToString();
                var headers = new HttpHeader { ContentType = "application/json" };

                if (url.EndsWith("/api/v1/auth/login"))
                {
                    LoginCount++;

                    if (RejectLogin)
                    {
                        return new HttpResponse(request, headers, string.Empty, HttpStatusCode.Unauthorized);
                    }

                    // The first login hands out token1, the second token2, and so on. Which of
                    // them the server still accepts is controlled per-test via ValidTokens.
                    return new HttpResponse(request, headers, $"{{\"accessToken\":\"token{LoginCount}\"}}");
                }

                var authorization = request.Headers["Authorization"];

                if (authorization == null || !ValidTokens.Contains(authorization.Replace("Bearer ", string.Empty)))
                {
                    return new HttpResponse(request, headers, string.Empty, HttpStatusCode.Unauthorized);
                }

                if (url.EndsWith("/api/v1/libraries"))
                {
                    return new HttpResponse(request, headers, LibrariesJson);
                }

                if (url.Contains("/api/v1/libraries/") && url.EndsWith("/refresh"))
                {
                    return new HttpResponse(request, headers, string.Empty, HttpStatusCode.NoContent);
                }

                return new HttpResponse(request, headers, string.Empty, HttpStatusCode.NotFound);
            }

            public HttpResponse Get(HttpRequest request) => Execute(request);

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }
    }
}
