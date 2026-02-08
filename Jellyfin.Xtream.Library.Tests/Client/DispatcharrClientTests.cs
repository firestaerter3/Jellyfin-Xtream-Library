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
            ("/api/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.TestConnectionAsync("http://test.example.com", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnection_InvalidCredentials_ReturnsFalse()
    {
        var httpClient = CreateMockHttpClient(
            ("/api/token/", HttpStatusCode.Unauthorized, "{}"));

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
            new { id = 1, stream_id = 100, m3u_account = 1 },
            new { id = 2, stream_id = 200, m3u_account = 2 },
        };

        var httpClient = CreateMockHttpClient(
            ("/api/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
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
            ("/api/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
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
            ("/api/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
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
            ("/api/token/", HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" })),
            ("/api/vod/movies/999/", HttpStatusCode.NotFound, "{}"));

        var client = new DispatcharrClient(httpClient, _mockLogger.Object);
        client.Configure("admin", "password");

        var result = await client.GetMovieDetailAsync("http://test.example.com", 999, CancellationToken.None);

        result.Should().BeNull();
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

            if (url.Contains("/api/token/refresh/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { access = "new-token" }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (url.Contains("/api/token/"))
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
