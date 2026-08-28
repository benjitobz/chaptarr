using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public interface IGrimmoryProxy
    {
        List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings);
        void RefreshLibrary(GrimmorySettings settings, int libraryId);
        void SyncLibraryFiles(GrimmorySettings settings);
        ValidationFailure Test(GrimmorySettings settings);
    }

    public class GrimmoryProxy : IGrimmoryProxy
    {
        private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromMinutes(30);

        private readonly IHttpClient _httpClient;
        private readonly ICached<string> _tokenCache;
        private readonly Logger _logger;

        public GrimmoryProxy(IHttpClient httpClient, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _tokenCache = cacheManager.GetCache<string>(GetType(), "tokens");
            _logger = logger;
        }

        public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings)
        {
            var response = ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, "api/v1/libraries", token).Build();
                return _httpClient.Get(request);
            });

            return Json.Deserialize<List<GrimmoryLibrary>>(response.Content) ?? new List<GrimmoryLibrary>();
        }

        public void RefreshLibrary(GrimmorySettings settings, int libraryId)
        {
            ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, $"api/v1/libraries/{libraryId}/refresh", token).Build();
                request.Method = HttpMethod.Put;
                return _httpClient.Execute(request);
            });

            _logger.Debug("Triggered Grimmory refresh for library {0}", libraryId);
        }

        public void SyncLibraryFiles(GrimmorySettings settings)
        {
            try
            {
                ExecuteWithAuth(settings, token =>
                {
                    var request = BuildRequest(settings, "api/v1/tasks/start", token).Build();
                    request.Method = HttpMethod.Post;
                    request.Headers.ContentType = "application/json";
                    request.SuppressHttpError = true;
                    // options must be explicitly null: Grimmory's TaskCreateRequest resolves the
                    // options type from taskType (external property) and rejects the body when
                    // the property is absent for task types without a registered options class.
                    // Serialized literally because ToJson drops null-valued properties.
                    request.SetContent("{\"taskType\":\"SYNC_LIBRARY_FILES\",\"triggeredByCron\":false,\"options\":null}");
                    return _httpClient.Execute(request);
                });
            }
            catch (HttpException ex) when (ex.Response?.Content?.Contains("already running", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.Debug("Grimmory library file sync is already running");
                return;
            }

            _logger.Debug("Triggered Grimmory library file sync");
        }

        public ValidationFailure Test(GrimmorySettings settings)
        {
            try
            {
                var libraries = GetLibraries(settings);

                if (settings.LibraryId > 0 && !libraries.Exists(l => l.Id == settings.LibraryId))
                {
                    return new ValidationFailure(nameof(GrimmorySettings.LibraryId), "The selected library was not found in Grimmory");
                }
            }
            catch (GrimmoryAuthenticationException)
            {
                return new ValidationFailure(nameof(GrimmorySettings.Username), "Authentication failed, check the username and password");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to Grimmory");
                return new ValidationFailure(nameof(GrimmorySettings.Url), "Unable to connect: " + ex.Message);
            }

            return null;
        }

        private HttpResponse ExecuteWithAuth(GrimmorySettings settings, Func<string, HttpResponse> action)
        {
            var token = GetAccessToken(settings, false);
            var response = action(token);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                token = GetAccessToken(settings, true);
                response = action(token);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new GrimmoryAuthenticationException("Grimmory rejected the configured credentials");
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new HttpException(response);
            }

            return response;
        }

        private string GetAccessToken(GrimmorySettings settings, bool forceRefresh)
        {
            var cacheKey = $"{settings.Url}:{settings.Username}";

            if (forceRefresh)
            {
                _tokenCache.Remove(cacheKey);
            }

            return _tokenCache.Get(cacheKey, () => Login(settings), TokenCacheDuration);
        }

        private string Login(GrimmorySettings settings)
        {
            var request = new HttpRequestBuilder(HttpUri.CombinePath(settings.Url, "api/v1/auth/login"))
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = HttpMethod.Post;
            request.Headers.ContentType = "application/json";
            request.SuppressHttpError = true;
            request.SetContent(new { username = settings.Username, password = settings.Password }.ToJson());

            var response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new GrimmoryAuthenticationException("Grimmory rejected the configured credentials");
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new HttpException(response);
            }

            var tokenResponse = Json.Deserialize<GrimmoryTokenResponse>(response.Content);

            if (tokenResponse?.AccessToken.IsNullOrWhiteSpace() != false)
            {
                throw new GrimmoryAuthenticationException("Grimmory did not return an access token");
            }

            return tokenResponse.AccessToken;
        }

        private static HttpRequestBuilder BuildRequest(GrimmorySettings settings, string relativePath, string token)
        {
            return new HttpRequestBuilder(HttpUri.CombinePath(settings.Url, relativePath))
                .Accept(HttpAccept.Json)
                .SetHeader("Authorization", $"Bearer {token}");
        }

        private class GrimmoryTokenResponse
        {
            [JsonProperty("accessToken")]
            public string AccessToken { get; set; }
        }
    }

    public class GrimmoryLibrary
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class GrimmoryAuthenticationException : Exception
    {
        public GrimmoryAuthenticationException(string message)
            : base(message)
        {
        }
    }
}
