// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

public class DispatcharrClientTests : IDisposable
{
    private readonly Mock<ILogger<DispatcharrClient>> _mockLogger;

    public DispatcharrClientTests()
    {
        _mockLogger = new Mock<ILogger<DispatcharrClient>>();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateMockHttpClient(params (string UrlContains, HttpStatusCode Status, string ResponseJson)[] responses)
    {
        var handler = new MockHttpMessageHandler(responses);
        return new HttpClient(handler);
    }

    #region LoginAsync Tests

    [Fact]
    public async Task TestConnection_ValidCredentials_ReturnsTrue()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.TestConnectionAsync("http://test.example.com", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnection_InvalidCredentials_ReturnsFalse()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.Unauthorized, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "wrongpassword");

        var result = await client.TestConnectionAsync("http://test.example.com", CancellationToken.None);

        result.Should().BeFalse();
    }

    #endregion

    #region GetMovieProvidersAsync Tests

    [Fact]
    public async Task GetMovieProviders_ReturnsProviders()
    {
        var providers = new[]
        {
            new { id = 1, stream_id = 100, m3u_account = new { id = 1, name = "Account1" } },
            new { id = 2, stream_id = 200, m3u_account = new { id = 2, name = "Account2" } },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/42/providers/", HttpStatusCode.OK, JsonConvert.SerializeObject(providers)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieProvidersAsync("http://test.example.com", 42, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].StreamId.Should().Be(100);
        result[1].StreamId.Should().Be(200);
    }

    [Fact]
    public async Task GetMovieProviders_NotFound_ReturnsEmpty()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/999/providers/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieProvidersAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion

    #region GetMovieDetailAsync Tests

    [Fact]
    public async Task GetMovieDetail_ReturnsDetail()
    {
        var detail = new { id = 42, uuid = "abc-123-def", name = "Test Movie" };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/42/", HttpStatusCode.OK, JsonConvert.SerializeObject(detail)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieDetailAsync("http://test.example.com", 42, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Uuid.Should().Be("abc-123-def");
        result.Name.Should().Be("Test Movie");
    }

    [Fact]
    public async Task GetMovieDetail_NotFound_ReturnsNull()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/999/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieDetailAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region GetMovieProviderInfoAsync Tests

    [Fact]
    public async Task GetMovieProviderInfo_ReturnsMappedVodInfoResponse()
    {
        // Shape verified live against a running Dispatcharr instance's
        // GET /api/vod/movies/{id}/provider-info/ response.
        var providerInfo = new
        {
            stream_id = "1479430",
            name = "Wolfs",
            o_name = "Wolfs",
            description = "A fixer's night spirals out of control.",
            plot = "A fixer's night spirals out of control.",
            genre = "Action,Comedy,Thriller",
            director = "",
            actors = "",
            country = "",
            release_date = "2024-09-20",
            rating = "6.653",
            tmdb_id = "",
            youtube_trailer = "",
            backdrop_path = Array.Empty<string>(),
            duration_secs = 0,
            bitrate = 0,
            video = new { },
            audio = new { },
            container_extension = "mp4",
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/42/provider-info/", HttpStatusCode.OK, JsonConvert.SerializeObject(providerInfo)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieProviderInfoAsync("http://test.example.com", 42, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Info.Should().NotBeNull();
        result.Info!.Name.Should().Be("Wolfs");
        result.Info.Plot.Should().Be("A fixer's night spirals out of control.");
        result.Info.Genre.Should().Be("Action,Comedy,Thriller");
        result.Info.ReleaseDate.Should().Be("2024-09-20");
        result.Info.Rating.Should().Be("6.653");
        result.MovieData.Should().NotBeNull();
        result.MovieData!.StreamId.Should().Be(1479430);
        result.MovieData.ContainerExtension.Should().Be("mp4");
    }

    [Fact]
    public async Task GetMovieProviderInfo_NotFound_ReturnsNull()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/999/provider-info/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieProviderInfoAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region GetSeriesProviderInfoAsync Tests

    [Fact]
    public async Task GetSeriesProviderInfo_ReturnsMappedSeriesInfo()
    {
        // Shape verified live against a running Dispatcharr instance's
        // GET /api/vod/series/{id}/provider-info/ response.
        var providerInfo = new
        {
            name = "Alone: Frozen",
            description = "Alone veterans survive 50 brutal days in Labrador.",
            genre = "Reality",
            rating = "8.0",
            tmdb_id = "207055",
            imdb_id = (string?)null,
            category_id = 349,
            backdrop_path = new[] { "http://dispatcharr.example.com/api/vod/series/7916/image/?kind=backdrop" },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/series/7916/provider-info/", HttpStatusCode.OK, JsonConvert.SerializeObject(providerInfo)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetSeriesProviderInfoAsync("http://test.example.com", 7916, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Alone: Frozen");
        result.Plot.Should().Be("Alone veterans survive 50 brutal days in Labrador.");
        result.Genre.Should().Be("Reality");
        result.Rating.Should().Be(8.0m);
        result.Tmdb.Should().Be("207055");
        result.CategoryId.Should().Be(349);
        result.BackdropPaths.Should().ContainSingle();
    }

    [Fact]
    public async Task GetSeriesProviderInfo_NotFound_ReturnsNull()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/series/999/provider-info/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetSeriesProviderInfoAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region GetSeriesEpisodesAsync Tests

    [Fact]
    public async Task GetSeriesEpisodes_GroupsBySeasonAndPicksHighestPriorityProvider()
    {
        // Shape verified live against a running Dispatcharr instance's
        // GET /api/vod/series/{id}/episodes/ response (trimmed to the fields this plugin reads;
        // the live response nests a lot more per-provider account/profile data than this).
        var episodes = new[]
        {
            new
            {
                name = "50 Day Freeze",
                season_number = 1,
                episode_number = 1,
                providers = new[]
                {
                    new
                    {
                        stream_id = "111111",
                        container_extension = "mkv",
                        m3u_account = new { priority = 1 },
                    },
                    new
                    {
                        stream_id = "266574",
                        container_extension = "mkv",
                        m3u_account = new { priority = 3 },
                    },
                },
            },
            new
            {
                name = "Frost Bound",
                season_number = 1,
                episode_number = 2,
                providers = new[]
                {
                    new
                    {
                        stream_id = "266575",
                        container_extension = "mkv",
                        m3u_account = new { priority = 3 },
                    },
                },
            },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/series/7916/episodes/", HttpStatusCode.OK, JsonConvert.SerializeObject(episodes)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetSeriesEpisodesAsync("http://test.example.com", 7916, CancellationToken.None);

        result.Should().ContainKey(1);
        result[1].Should().HaveCount(2);
        var first = result[1].Single(e => e.EpisodeNum == 1);
        first.EpisodeId.Should().Be(266574, "the priority-3 provider should win over priority-1");
        first.Title.Should().Be("50 Day Freeze");
        first.ContainerExtension.Should().Be("mkv");
    }

    [Fact]
    public async Task GetSeriesEpisodes_SkipsEpisodesWithNoProviders()
    {
        var episodes = new[]
        {
            new
            {
                name = "Orphaned Episode",
                season_number = 1,
                episode_number = 1,
                providers = Array.Empty<object>(),
            },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/series/7916/episodes/", HttpStatusCode.OK, JsonConvert.SerializeObject(episodes)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetSeriesEpisodesAsync("http://test.example.com", 7916, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSeriesEpisodes_NotFound_ReturnsEmpty()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/series/999/episodes/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetSeriesEpisodesAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion

    #region Cancellation Tests

    // A canceled request must propagate as a cancellation, not get swallowed by the generic
    // catch and turned into a null/empty result. StrmSyncService reads a null/empty Dispatcharr
    // response as "REST came back empty, fall back to the classic Xtream call" - if cancellation
    // were swallowed the same way, cancelling a sync would silently keep going on the classic
    // path instead of actually stopping.

    [Fact]
    public async Task GetMovieProviderInfo_Canceled_PropagatesCancellation()
    {
        var handler = new FuncHttpMessageHandler((request, ct) =>
        {
            if (request.RequestUri!.ToString().Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }),
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            throw new OperationCanceledException();
        });

        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "password");

        var act = () => client.GetMovieProviderInfoAsync("http://test.example.com", 42, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSeriesProviderInfo_Canceled_PropagatesCancellation()
    {
        var handler = new FuncHttpMessageHandler((request, ct) =>
        {
            if (request.RequestUri!.ToString().Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }),
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            throw new OperationCanceledException();
        });

        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "password");

        var act = () => client.GetSeriesProviderInfoAsync("http://test.example.com", 7916, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSeriesEpisodes_Canceled_PropagatesCancellation()
    {
        var handler = new FuncHttpMessageHandler((request, ct) =>
        {
            if (request.RequestUri!.ToString().Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }),
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            throw new OperationCanceledException();
        });

        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "password");

        var act = () => client.GetSeriesEpisodesAsync("http://test.example.com", 7916, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region GetChannelsAsync (GitHub #113)

    [Fact]
    public async Task GetChannelsAsync_ReturnsChannelsWithTheirStreams()
    {
        // Shape as returned by GET /api/channels/channels/?include_streams=true: a bare array, and
        // each channel carrying the upstream streams behind it.
        var payload = new[]
        {
            new
            {
                id = 42,
                uuid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
                name = "NPO 1 HD",
                streams = new[] { new { id = 7, stream_id = 69307 } },
            },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/channels/channels/", HttpStatusCode.OK, JsonConvert.SerializeObject(payload)));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetChannelsAsync("http://test.example.com", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(42);
        result[0].Uuid.Should().Be("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        result[0].Streams.Should().ContainSingle();
        result[0].Streams![0].StreamId.Should().Be(69307);
    }

    [Fact]
    public async Task GetChannelsAsync_NotFound_ReturnsEmptyRatherThanThrowing()
    {
        // An older Dispatcharr without this endpoint must degrade to credentialed URLs, not break
        // the whole playlist.
        var httpClient = CreateMockHttpClient(
            ("/api/accounts/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/channels/channels/", HttpStatusCode.NotFound, string.Empty));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        (await client.GetChannelsAsync("http://test.example.com", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetChannelsAsync_Canceled_PropagatesCancellation()
    {
        // An empty result means "no Dispatcharr match, fall back to the credentialed URL". If
        // cancellation collapsed into that, cancelling would quietly produce a playlist carrying
        // the provider password instead of stopping.
        var handler = new FuncHttpMessageHandler((request, ct) =>
        {
            if (request.RequestUri!.ToString().Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }),
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            throw new OperationCanceledException();
        });

        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "password");

        var act = () => client.GetChannelsAsync("http://test.example.com", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Token Refresh Tests

    [Fact]
    public async Task TokenRefresh_ExpiredToken_RefreshesAutomatically()
    {
        // First call gets token, second call gets 401 (token expired), then refresh, then retry succeeds
        var detail = new { id = 42, uuid = "abc-123", name = "Test" };
        var callCount = 0;

        var handler = new FuncHttpMessageHandler(async (request, ct) =>
        {
            callCount++;
            var url = request.RequestUri!.ToString();

            if (url.Contains("/api/accounts/token/refresh/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "new-token" }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (url.Contains("/api/accounts/token/") && !url.Contains("refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (url.Contains("/api/vod/movies/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(detail),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieDetailAsync("http://test.example.com", 42, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Uuid.Should().Be("abc-123");
    }

    #endregion

    /// <summary>
    /// Simple mock HTTP handler that matches URLs to responses.
    /// </summary>
    // GitHub #122: ten sync workers found no token at once, all logged in, and Dispatcharr's login
    // throttle answered 429. One login has to serve them all.
    [Fact]
    public async Task ConcurrentCalls_WithoutAToken_LogInOnce()
    {
        var logins = 0;
        var handler = new FuncHttpMessageHandler(async (request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            string json;
            if (url.Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref logins);
                await Task.Delay(100, ct).ConfigureAwait(false);
                json = JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" });
            }
            else
            {
                json = JsonConvert.SerializeObject(new { id = 42, uuid = "abc-123", name = "Test" });
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "secret");
        var baseUrl = $"http://login-once-{Guid.NewGuid():N}.example.com";

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetMovieDetailAsync(baseUrl, 42, CancellationToken.None)));

        results.Should().OnlyContain(r => r != null);
        logins.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentCalls_RejectedTogether_RenewOnce()
    {
        var renewals = 0;
        var currentToken = "old-token";
        var handler = new FuncHttpMessageHandler(async (request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/accounts/token/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref renewals);
                await Task.Delay(100, ct).ConfigureAwait(false);
                var issued = renewals == 1 ? "old-token" : "new-token";
                currentToken = issued;
                var body = JsonConvert.SerializeObject(new { access = issued, refresh = "refresh-token" });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            if (request.Headers.Authorization?.Parameter != "new-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var json = JsonConvert.SerializeObject(new { id = 42, uuid = "abc-123", name = "Test" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "secret");
        var baseUrl = $"http://renew-once-{Guid.NewGuid():N}.example.com";

        // First call logs in and gets "old-token", which every data call then rejects.
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetMovieDetailAsync(baseUrl, 42, CancellationToken.None)));

        results.Should().OnlyContain(r => r != null);
        renewals.Should().Be(2, "one login, then one renewal shared by all ten rejected calls");
        currentToken.Should().Be("new-token");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly (string UrlContains, HttpStatusCode Status, string ResponseJson)[] _responses;

        public MockHttpMessageHandler(params (string UrlContains, HttpStatusCode Status, string ResponseJson)[] responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            foreach (var (urlContains, status, responseJson) in _responses)
            {
                if (url.Contains(urlContains, StringComparison.OrdinalIgnoreCase))
                {
                    var response = new HttpResponseMessage(status)
                    {
                        Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                    };
                    return Task.FromResult(response);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>
    /// HTTP handler that delegates to a func for full control.
    /// </summary>
    // GitHub #83. The token base used to be re-derived from the request URL as scheme://authority,
    // which silently dropped a path. Dispatcharr behind a reverse proxy on a subpath then had its
    // data calls routed correctly and its login sent one level too high, where there is no route.
    [Fact]
    public async Task TokenRequest_KeepsThePathOfTheBaseUrlItWasGiven()
    {
        var requested = new List<string>();
        var handler = new FuncHttpMessageHandler((request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);

            var json = url.Contains("/api/accounts/token/", StringComparison.Ordinal)
                ? JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })
                : JsonConvert.SerializeObject(new { id = 42, uuid = "abc-123", name = "Test" });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        });

        var client = new DispatcharrClient(new HttpClient(handler), _mockLogger.Object);
        client.Configure("admin", "secret");

        await client.GetMovieDetailAsync("https://proxy.example.com/dispatcharr", 42, CancellationToken.None);

        requested.Should().Contain("https://proxy.example.com/dispatcharr/api/accounts/token/");
        requested.Should().NotContain(u => u.Equals("https://proxy.example.com/api/accounts/token/", StringComparison.Ordinal));
    }

    private class FuncHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public FuncHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
